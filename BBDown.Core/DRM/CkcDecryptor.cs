using System.Diagnostics;
using System.Text.Json;

namespace BBDown.Core.DRM;

public static class CkcDecryptor
{
    public static async Task<(string kid, string keyHex)?> GetKeyAsync(string kidHex, string extractorPath = "")
    {
        if (string.IsNullOrEmpty(extractorPath))
        {
            extractorPath = FindExtractor();
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{extractorPath}\" {kidHex}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var extractorDir = Path.GetDirectoryName(extractorPath);
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
                Logger.LogError($"CKC extractor failed: {error}");
                return null;
            }

            var result = JsonDocument.Parse(output).RootElement;
            if (result.TryGetProperty("error", out var err))
            {
                Logger.LogError($"CKC error: {err}");
                return null;
            }

            var keyHex = result.GetProperty("key_hex").GetString()!;
            return (kidHex, keyHex);
        }
        catch (Exception ex)
        {
            Logger.LogError($"CKC extractor exception: {ex.Message}");
            return null;
        }
    }

    private static string FindExtractor()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ckc_puppeteer.js"),
            Path.Combine(Directory.GetCurrentDirectory(), "ckc_puppeteer.js"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return candidates[0];
    }
}
