using System.Net.Http.Headers;
using System.Security.Cryptography;
using Google.Protobuf;
using BBDown.Core.DRM.Proto;
using BBDown.Core.Util;

namespace BBDown.Core.DRM;

public sealed class WidevineCdm : IDisposable
{
    private readonly WvdDevice _device;
    private RSA? _serviceKey;
    private bool _disposed;

    private const string LicenseUrl = "https://bvc-drm.bilivideo.com/bili_widevine";
    private const string CertUrl = "https://bvc-drm.bilivideo.com/cer/bilibili_certificate.bin";
    private static readonly byte[] WidevineSystemId = {
        0xed, 0xef, 0x8b, 0xa9, 0x79, 0xd6, 0x4a, 0xce,
        0xa3, 0xc8, 0x27, 0xdc, 0xd5, 0x1d, 0x21, 0xed
    };

    private WidevineCdm(WvdDevice device)
    {
        _device = device;
    }

    public static async Task<(string kid, string key)[]?> GetKeysAsync(string psshB64, string wvdPath)
    {
        WvdDevice device;
        try
        {
            device = WvdDevice.Load(wvdPath);
        }
        catch (Exception ex)
        {
            Logger.LogWarn($"加载 device.wvd 失败: {ex.Message}");
            return null;
        }

        using var cdm = new WidevineCdm(device);
        try
        {
            return await cdm.GetKeysInternalAsync(psshB64);
        }
        catch (Exception ex)
        {
            Logger.LogWarn($"Widevine 解密失败: {ex.Message}");
            return null;
        }
    }

    private async Task<(string kid, string key)[]?> GetKeysInternalAsync(string psshB64)
    {
        if (!await FetchServiceCertificateAsync())
            return null;

        var psshBytes = Convert.FromBase64String(psshB64);
        var keyIds = ParsePsshBox(psshBytes);
        if (keyIds.Count == 0)
        {
            Logger.LogWarn("PSSH 中未找到 key ID");
            return null;
        }

        var challenge = BuildChallenge(keyIds, psshBytes);

        byte[] responseBytes;
        try
        {
            responseBytes = await SendRequestAsync(challenge);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarn($"许可证请求失败: {ex.Message}");
            return null;
        }

        return ParseResponse(responseBytes);
    }

    // ---- service certificate ----

    private async Task<bool> FetchServiceCertificateAsync()
    {
        try
        {
            var data = await HTTPUtil.AppHttpClient.GetByteArrayAsync(CertUrl);
            var signedCert = SignedDrmCertificate.Parser.ParseFrom(data);
            var drmCert = DrmCertificate.Parser.ParseFrom(signedCert.Certificate);

            var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(drmCert.PublicKey.ToByteArray(), out _);
            _serviceKey = rsa;
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug("获取服务证书失败: {0}", ex.Message);
            Logger.LogWarn("无法获取 Widevine 服务证书");
            return false;
        }
    }

    // ---- PSSH parser ----

    private static List<byte[]> ParsePsshBox(byte[] raw)
    {
        var kids = new List<byte[]>();
        try
        {
            if (raw.Length < 28) return kids; // minimum PSSH box: 8 header + 4 fullbox + 16 system_id

            var pos = 8; // skip box size + type
            var version = raw[pos];
            pos += 4; // version + flags
            // validate Widevine system_id
            if (!raw.AsSpan(pos, 16).SequenceEqual(WidevineSystemId))
                return kids;
            pos += 16;

            if (version >= 1)
            {
                if (pos + 4 > raw.Length) return kids;
                var count = ReadU32Be(raw, pos); pos += 4;
                for (var i = 0; i < count && pos + 16 <= raw.Length; i++)
                {
                    var kid = new byte[16];
                    Buffer.BlockCopy(raw, pos, kid, 0, 16);
                    kids.Add(kid);
                    pos += 16;
                }
            }

            if (pos + 4 > raw.Length) return kids;
            var dataSize = (int)ReadU32Be(raw, pos); pos += 4;
            if (dataSize > 0 && dataSize <= 4096 && pos + dataSize <= raw.Length)
            {
                var psshData = new byte[dataSize];
                Buffer.BlockCopy(raw, pos, psshData, 0, dataSize);
                var header = WidevineCencHeader.Parser.ParseFrom(psshData);
                if (kids.Count == 0)
                {
                    foreach (var k in header.KeyIds)
                        kids.Add(k.ToByteArray());
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("PSSH parse error: {0}", ex.Message);
        }
        return kids;
    }

    private static uint ReadU32Be(byte[] buf, int offset)
    {
        return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16)
             | ((uint)buf[offset + 2] << 8) | buf[offset + 3];
    }

    // ---- license challenge ----

    private byte[] BuildChallenge(List<byte[]> keyIds, byte[] psshRaw)
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var req = new LicenseRequest
        {
            ClientId = _device.ClientIdentification,
            Type = LicenseRequest.Types.LicenseType.Streaming,
            RequestTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            GracePeriodEnd = (uint)DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            KeyControlNonce = ByteString.CopyFrom(nonce),
        };
        var wid = new LicenseRequest.Types.ContentIdentification.Types.WidevinePsshData();
        foreach (var kid in keyIds)
            wid.KeyIds.Add(ByteString.CopyFrom(kid));
        wid.PsshData = ByteString.CopyFrom(psshRaw);
        req.ContentId = new LicenseRequest.Types.ContentIdentification { WidevinePsshData = wid };

        var plaintext = req.ToByteArray();

        var sessionKey = new byte[16];
        var iv = new byte[16];
        RandomNumberGenerator.Fill(sessionKey);
        RandomNumberGenerator.Fill(iv);

        var padded = Pkcs7Pad(plaintext, 16);
        var ciphertext = AesCbcEncrypt(padded, sessionKey, iv);

        var msg = new byte[iv.Length + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, msg, 0, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, msg, iv.Length, ciphertext.Length);

        var sig = _device.Rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var encKey = _serviceKey!.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA1);

        var sm = new SignedMessage
        {
            Type = SignedMessage.Types.MessageType.LicenseRequest,
            Msg = ByteString.CopyFrom(msg),
            Signature = ByteString.CopyFrom(sig),
            SessionKey = ByteString.CopyFrom(encKey),
        };
        return sm.ToByteArray();
    }

    // ---- HTTP ----

    private static async Task<byte[]> SendRequestAsync(byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-protobuf");

        using var req = new HttpRequestMessage(HttpMethod.Post, LicenseUrl) { Content = content };
        req.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");

        var resp = await HTTPUtil.AppHttpClient.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync();
    }

    // ---- license response ----

    private (string kid, string key)[]? ParseResponse(byte[] data)
    {
        var sm = SignedMessage.Parser.ParseFrom(data);
        if (sm.Type != SignedMessage.Types.MessageType.License)
            return null;

        var msg = sm.Msg.ToByteArray();
        if (msg.Length < 16)
        {
            Logger.LogWarn("许可证响应数据异常");
            return null;
        }

        var sig = sm.Signature.ToByteArray();

        var ok = _serviceKey!.VerifyData(msg, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (!ok)
        {
            Logger.LogWarn("许可证签名校验失败");
            return null;
        }

        var encKey = sm.SessionKey.ToByteArray();
        byte[] sessionKey;
        try { sessionKey = _device.Rsa.Decrypt(encKey, RSAEncryptionPadding.OaepSHA1); }
        catch { sessionKey = _device.Rsa.Decrypt(encKey, RSAEncryptionPadding.OaepSHA256); }

        if (sessionKey.Length != 16)
        {
            Logger.LogWarn($"会话密钥长度异常: {sessionKey.Length}");
            return null;
        }

        var msgIv = new byte[16];
        Buffer.BlockCopy(msg, 0, msgIv, 0, 16);
        var ciphertext = new byte[msg.Length - 16];
        Buffer.BlockCopy(msg, 16, ciphertext, 0, ciphertext.Length);

        var padded = AesCbcDecrypt(ciphertext, sessionKey, msgIv);
        var licenseBytes = Pkcs7Unpad(padded);
        var license = License.Parser.ParseFrom(licenseBytes);

        var keys = new List<(string kid, string key)>();
        foreach (var kc in license.Key)
        {
            if (kc.Type != License.Types.KeyContainer.Types.KeyType.Content)
                continue;

            var keyIv = kc.Iv?.ToByteArray() ?? new byte[16];
            if (keyIv.Length < 16)
            {
                var ivPadded = new byte[16];
                Buffer.BlockCopy(keyIv, 0, ivPadded, 0, Math.Min(keyIv.Length, 16));
                keyIv = ivPadded;
            }
            var encContentKey = kc.Key.ToByteArray();
            byte[] contentKey;

            // Widevine spec: if IV is unset or all zeros → ECB, otherwise CBC
            var isZeroIv = keyIv.All(b => b == 0);
            if (isZeroIv)
            {
                contentKey = AesEcbDecrypt(encContentKey, sessionKey);
            }
            else
            {
                var dec = AesCbcDecrypt(encContentKey, sessionKey, keyIv);
                contentKey = Pkcs7Unpad(dec);
            }

            var kidHex = Convert.ToHexString(kc.Id.ToByteArray()).ToLowerInvariant();
            var keyHex = Convert.ToHexString(contentKey).ToLowerInvariant();
            keys.Add((kidHex, keyHex));
        }

        return keys.Count > 0 ? keys.ToArray() : null;
    }

    // ---- crypto helpers ----

    private static byte[] AesCbcEncrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesCbcDecrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEcbDecrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] Pkcs7Pad(byte[] data, int blockSize)
    {
        var pad = (byte)(blockSize - (data.Length % blockSize));
        var result = new byte[data.Length + pad];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        Array.Fill(result, pad, data.Length, pad);
        return result;
    }

    private static byte[] Pkcs7Unpad(byte[] data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("empty data for PKCS7 unpad");
        var pad = data[^1];
        if (pad == 0 || pad > 16 || pad > data.Length)
            throw new InvalidDataException("bad PKCS7 padding");
        for (var i = data.Length - pad; i < data.Length; i++)
        {
            if (data[i] != pad)
                throw new InvalidDataException("inconsistent PKCS7 padding");
        }
        var result = new byte[data.Length - pad];
        Buffer.BlockCopy(data, 0, result, 0, result.Length);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serviceKey?.Dispose();
        _device.Dispose();
    }
}
