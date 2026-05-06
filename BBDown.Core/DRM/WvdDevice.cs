using System.Security.Cryptography;
using BBDown.Core.DRM.Proto;

namespace BBDown.Core.DRM;

public class WvdDevice : IDisposable
{
    public byte[] ClientIdBytes { get; }
    public RSA Rsa { get; }
    public ClientIdentification ClientIdentification { get; }
    private bool _disposed;

    private WvdDevice(byte[] clientIdBytes, RSA rsa, ClientIdentification clientId)
    {
        ClientIdBytes = clientIdBytes;
        Rsa = rsa;
        ClientIdentification = clientId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Rsa.Dispose();
    }

    public static WvdDevice Load(string path)
    {
        var allBytes = File.ReadAllBytes(path);

        // 格式1: 带 "WVD" magic header (前3字节 = 0x57 0x56 0x44 = "WVD")
        if (allBytes.Length >= 4 && allBytes[0] == 0x57 && allBytes[1] == 0x56 && allBytes[2] == 0x44)
            return ParseWvd(allBytes.AsSpan(3));

        // 格式2: pywidevine v1 标准格式 (首字节 = version = 1)
        if (allBytes.Length >= 1 && allBytes[0] == 1)
            return ParseWvd(allBytes.AsSpan());

        // 格式3: 纯 PEM 私钥 + 伴生 client_id blob
        if (allBytes.Length > 0 && allBytes[0] == '-')
            return ParsePemPlusClientId(path, allBytes);

        throw new InvalidDataException($"无法识别的 WVD 文件格式 (首字节: {allBytes[0]})");
    }

    private static WvdDevice ParseWvd(Span<byte> data)
    {
        var version = data[0];
        if (version != 1)
            throw new InvalidDataException($"Unsupported WVD version: {version}");

        var type = data[1];
        var securityLevel = data[2];
        var flags = data[3];

        var privateKeyLen = (data[4] << 8) | data[5];
        var privateKeyBytes = data.Slice(6, privateKeyLen).ToArray();
        var offset = 6 + privateKeyLen;

        var clientIdLen = (data[offset] << 8) | data[offset + 1];
        var clientIdBytes = data.Slice(offset + 2, clientIdLen).ToArray();

        return Create(privateKeyBytes, clientIdBytes);
    }

    private static WvdDevice ParsePemPlusClientId(string wvdPath, byte[] allBytes)
    {
        // 尝试在同目录下查找 .client_id 或 _client_id.bin 文件
        var dir = Path.GetDirectoryName(wvdPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(wvdPath);

        byte[]? clientIdBytes = null;
        foreach (var candidate in new[] {
            Path.Combine(dir, baseName + "_client_id.bin"),
            Path.Combine(dir, baseName + ".client_id"),
            Path.Combine(dir, "client_id.bin"),
        })
        {
            if (File.Exists(candidate))
            {
                clientIdBytes = File.ReadAllBytes(candidate);
                break;
            }
        }

        if (clientIdBytes == null)
            throw new InvalidDataException("PEM 格式需要配套的 client_id 文件 (_client_id.bin)");

        return Create(allBytes, clientIdBytes);
    }

    private static WvdDevice Create(byte[] privateKeyBytes, byte[] clientIdBytes)
    {
        var rsa = RSA.Create();
        var pemString = System.Text.Encoding.ASCII.GetString(privateKeyBytes);

        // 如果 PEM 头尾不完整，尝试补全
        if (!pemString.Contains("-----BEGIN"))
        {
            pemString = "-----BEGIN RSA PRIVATE KEY-----\n" + pemString + "\n-----END RSA PRIVATE KEY-----";
        }

        rsa.ImportFromPem(pemString);

        // 尝试解析 protobuf，兼容非 protobuf 的原始 client_id
        ClientIdentification clientId;
        try
        {
            clientId = ClientIdentification.Parser.ParseFrom(clientIdBytes);
        }
        catch
        {
            // 如果 client_id 不是 protobuf 格式，构建最小 ClientIdentification
            clientId = new ClientIdentification
            {
                Type = ClientIdentification.Types.TokenType.DrmDeviceCertificate,
                Token = Google.Protobuf.ByteString.CopyFrom(clientIdBytes),
            };
        }

        return new WvdDevice(clientIdBytes, rsa, clientId);
    }
}
