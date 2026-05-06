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
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);

        var version = reader.ReadByte();
        if (version != 1)
            throw new InvalidDataException($"Unsupported WVD version: {version}");

        _ = reader.ReadByte(); // type
        _ = reader.ReadByte(); // security_level
        _ = reader.ReadByte(); // flags

        var privateKeyLen = (reader.ReadByte() << 8) | reader.ReadByte();
        var privateKeyBytes = reader.ReadBytes(privateKeyLen);

        var clientIdLen = (reader.ReadByte() << 8) | reader.ReadByte();
        var clientIdBytes = reader.ReadBytes(clientIdLen);

        var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.ASCII.GetString(privateKeyBytes));

        var clientId = ClientIdentification.Parser.ParseFrom(clientIdBytes);

        return new WvdDevice(clientIdBytes, rsa, clientId);
    }
}
