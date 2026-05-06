namespace BBDown.Core.DRM;

public static class DrmDecryptor
{
    public static async Task<(string kid, string keyHex)?> GetKeyWidevineAsync(string psshB64, string wvdPath)
    {
        if (!File.Exists(wvdPath))
        {
            Logger.LogWarn($"device.wvd 未找到: {wvdPath}");
            return null;
        }

        var keys = await WidevineCdm.GetKeysAsync(psshB64, wvdPath);
        if (keys == null || keys.Length == 0)
            return null;

        var (kid, key) = keys[0];
        Logger.LogDebug("Widevine key: kid={0}, key={1}", kid, key);
        return (kid, key);
    }
}
