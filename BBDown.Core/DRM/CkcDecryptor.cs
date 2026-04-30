using System.Diagnostics;
using System.Text.Json;

namespace BBDown.Core.DRM;

public static class DrmDecryptor
{
    public static async Task<(string kid, string keyHex)?> GetKeyWidevineAsync(string psshB64, string wvdPath)
    {
        var extractor = FindFile("widevine_decrypt.py");
        if (!File.Exists(extractor))
        {
            Logger.LogWarn("widevine_decrypt.py 未找到，跳过自动解密");
            return null;
        }

        var (pythonOk, pythonMsg) = await CheckPythonAsync();
        if (!pythonOk)
        {
            Logger.LogWarn(pythonMsg);
            return null;
        }

        var pyExe = await FindPythonExeAsync() ?? "python3";
        var psi = new ProcessStartInfo
        {
            FileName = pyExe,
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

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var output = await stdoutTask;
            var error = await stderrTask;

            if (proc.ExitCode != 0)
            {
                if (error.Contains("ModuleNotFoundError") || error.Contains("No module named"))
                    Logger.LogWarn("pywidevine 未安装，请运行: pip install pywidevine 'construct==2.8.8'");
                else if (error.Contains("Device"))
                    Logger.LogWarn($"device.wvd 无效: {error.Trim()[..Math.Min(100, error.Length)]}");
                else
                    Logger.LogWarn($"Widevine 解密失败: {error.Trim()[..Math.Min(100, error.Length)]}");
                return null;
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err)) return null;

            var keys = root.GetProperty("keys");
            if (keys.GetArrayLength() > 0)
            {
                var k = keys[0];
                return (k.GetProperty("kid").GetString()!, k.GetProperty("key").GetString()!);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(bool ok, string msg)> CheckPythonAsync()
    {
        var pyExe = await FindPythonExeAsync();
        if (pyExe == null)
            return (false, "Python 未安装，Widevine 需要 Python: https://www.python.org/downloads/");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pyExe,
                Arguments = "-c \"from pywidevine import Device\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Python 未找到");
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return (false, "pywidevine 未安装，请运行: pip install pywidevine 'construct==2.8.8'");
            return (true, "");
        }
        catch
        {
            return (false, "Python 未安装，Widevine 需要 Python: https://www.python.org/downloads/");
        }
    }

    private static async Task<string?> FindPythonExeAsync()
    {
        foreach (var exe in new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0) return exe;
            }
            catch { }
        }
        return null;
    }

    private static string FindFile(string name)
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return Path.Combine(AppContext.BaseDirectory, name);
    }
}
