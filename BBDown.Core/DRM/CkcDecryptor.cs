using System.Diagnostics;
using System.Text.Json;

namespace BBDown.Core.DRM;

public static class DrmDecryptor
{
    public static async Task<(string kid, string keyHex)?> GetKeyCkcAsync(string kidHex)
    {
        var extractor = FindExtractor("ckc_puppeteer.js");
        return await RunNodeExtractor(extractor, kidHex);
    }

    public static async Task<(string kid, string keyHex)?> GetKeyWidevineAsync(string psshB64, string wvdPath)
    {
        var extractor = FindExtractor("widevine_decrypt.py");
        if (!File.Exists(extractor))
        {
            Logger.LogError($"Widevine extractor not found: {extractor}");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"\"{extractor}\" \"{psshB64}\" \"{wvdPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                Logger.LogError($"Widevine extractor error: {error}");
                return null;
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                Logger.LogError($"Widevine error: {err}");
                return null;
            }

            var keys = root.GetProperty("keys");
            if (keys.GetArrayLength() > 0)
            {
                var firstKey = keys[0];
                return (firstKey.GetProperty("kid").GetString()!,
                        firstKey.GetProperty("key").GetString()!);
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Widevine exception: {ex.Message}");
            return null;
        }
    }

    private static async Task<(string kid, string keyHex)?> RunNodeExtractor(string extractor, string kidHex)
    {
        if (!File.Exists(extractor))
        {
            Logger.LogError($"Extractor not found: {extractor}");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{extractor}\" {kidHex}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var extractorDir = Path.GetDirectoryName(extractor);
        if (!string.IsNullOrEmpty(extractorDir))
        {
            psi.Environment["NODE_PATH"] = Path.Combine(extractorDir, "node_modules");
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                Logger.LogError($"Extractor failed: {error}");
                return null;
            }

            using var result = JsonDocument.Parse(output);
            var root = result.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                Logger.LogError($"Error: {err}");
                return null;
            }

            var keyHex = root.GetProperty("key_hex").GetString()!;
            return (kidHex, keyHex);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Extractor exception: {ex.Message}");
            return null;
        }
    }

    private static string FindExtractor(string name)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(Directory.GetCurrentDirectory(), name),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return candidates[0];
    }
}
