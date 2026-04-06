using System.Diagnostics;
using System.IO;

namespace TsukiAI.VoiceChat.Services;

public sealed class VoicevoxEngineService : IDisposable
{
    private Process? _process;
    private readonly string _executablePath;
    private bool _isDisposed;

    public VoicevoxEngineService(string executablePath)
    {
        _executablePath = executablePath;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        var finalPath = ResolvePath(_executablePath);
        if (finalPath is null)
        {
            TsukiAI.Core.Services.DevLog.WriteLine($"VoiceVox engine not found (configured: {_executablePath})");
            return;
        }

        var processName = Path.GetFileNameWithoutExtension(finalPath);
        var existing = Process.GetProcessesByName(processName);
        if (existing.Length > 0)
        {
            TsukiAI.Core.Services.DevLog.WriteLine($"VoiceVox engine already running (PID: {existing[0].Id})");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = finalPath,
                WorkingDirectory = Path.GetDirectoryName(finalPath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _process = new Process { StartInfo = psi };
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    TsukiAI.Core.Services.DevLog.WriteLine($"[VoiceVox] {e.Data}");
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    TsukiAI.Core.Services.DevLog.WriteLine($"[VoiceVox Error] {e.Data}");
            };

            if (_process.Start())
            {
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                TsukiAI.Core.Services.DevLog.WriteLine($"VoiceVox engine started (PID: {_process.Id})");
                try { await Task.Delay(2000, ct); } catch { }
            }
            else
            {
                TsukiAI.Core.Services.DevLog.WriteLine("Failed to start VoiceVox engine process.");
            }
        }
        catch (Exception ex)
        {
            TsukiAI.Core.Services.DevLog.WriteLine($"Error starting VoiceVox engine: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(1000);
            }
        }
        catch (Exception ex)
        {
            TsukiAI.Core.Services.DevLog.WriteLine($"Error stopping VoiceVox engine: {ex.Message}");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        Stop();
        _isDisposed = true;
    }

    private static string? ResolvePath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return null;
        if (Path.IsPathRooted(inputPath) && File.Exists(inputPath)) return inputPath;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var relative = Path.Combine(baseDir, inputPath);
        if (File.Exists(relative)) return relative;

        var current = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6; i++)
        {
            if (current.Parent is null) break;
            current = current.Parent;
            var check = Path.Combine(current.FullName, inputPath);
            if (File.Exists(check)) return check;
        }

        return null;
    }
}
