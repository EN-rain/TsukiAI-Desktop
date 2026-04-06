using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using TsukiAI.Core.Services;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class MainWindow
{
    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        _ = TryStartBridgeServer(showSuccessMessage: true);
    }

    private void ServerStatusToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingServerStatusToggle)
        {
            return;
        }

        var started = TryStartBridgeServer(showSuccessMessage: false);
        if (!started)
        {
            UpdateServerStatusUi(false);
        }
    }

    private void ServerStatusToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingServerStatusToggle)
        {
            return;
        }

        StopBridgeServerProcess();
        UpdateServerStatusUi(false);
    }

    private bool TryStartBridgeServer(bool showSuccessMessage)
    {
        try
        {
            if (!Directory.Exists(BridgePath))
            {
                MessageBox.Show(this, $"Bridge folder not found:\n{BridgePath}", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (_serverProcess is { HasExited: false })
            {
                UpdateServerStatusUi(true);
                return true;
            }

            var packageJsonPath = Path.Combine(BridgePath, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                MessageBox.Show(this, $"package.json not found in bridge folder:\n{BridgePath}", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var launchError = string.Empty;
            _serverProcess = StartBridgeServerProcess(BridgePath, out launchError);
            if (_serverProcess is null)
            {
                var nodeModulesPath = Path.Combine(BridgePath, "node_modules");
                var installHint = Directory.Exists(nodeModulesPath)
                    ? string.Empty
                    : "\n\nHint: dependencies are missing. Run `npm install` in discord-voice-bridge first.";

                MessageBox.Show(
                    this,
                    $"Failed to start bridge server.\n{launchError}{installHint}",
                    "TsukiAI Voice Chat",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            DevLog.WriteLine("Voice bridge server started from: {0}", BridgePath);
            SaveBridgePid(_serverProcess.Id);
            UpdateServerStatusUi(true);
            if (showSuccessMessage)
            {
                MessageBox.Show(this, "Voice bridge server started.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Failed to start voice bridge: {0}", ex.Message);
            MessageBox.Show(this, $"Failed to start server:\n{ex.Message}", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateServerStatusUi(false);
            return false;
        }
    }

    private static Process? StartBridgeServerProcess(string bridgePath, out string error)
    {
        var candidates = new[]
        {
            new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c npm start",
                WorkingDirectory = bridgePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            new ProcessStartInfo
            {
                FileName = "npm.cmd",
                Arguments = "start",
                WorkingDirectory = bridgePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c node index.js",
                WorkingDirectory = bridgePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        var errors = new List<string>();

        foreach (var psi in candidates)
        {
            try
            {
                var process = Process.Start(psi);
                if (process is null)
                {
                    errors.Add($"{psi.FileName} {psi.Arguments}: process did not start");
                    continue;
                }

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        DevLog.WriteLine("[Bridge] " + e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        DevLog.WriteLine("[Bridge:ERR] " + e.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (process.WaitForExit(1200))
                {
                    var code = process.ExitCode;
                    process.Dispose();
                    errors.Add($"{psi.FileName} {psi.Arguments}: exited early with code {code}");
                    continue;
                }

                error = string.Empty;
                DevLog.WriteLine("Voice bridge launch command succeeded: {0} {1}", psi.FileName, psi.Arguments);
                return process;
            }
            catch (Exception ex)
            {
                errors.Add($"{psi.FileName} {psi.Arguments}: {ex.Message}");
            }
        }

        error = string.Join("\n", errors);
        DevLog.WriteLine("Voice bridge launch failed. Attempts:\n{0}", error);
        return null;
    }

    private void StopBridgeServerProcess()
    {
        try
        {
            if (_serverProcess is { HasExited: false })
            {
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(2000);
            }
        }
        catch
        {
        }
        finally
        {
            _serverProcess?.Dispose();
            _serverProcess = null;
            StopBridgeProcessesOnShutdown();
            UpdateServerStatusUi(false);
        }
    }

    public static void StopBridgeProcessesOnShutdown()
    {
        TryKillBridgeProcessFromPidFile();
        TryKillBridgeProcessOnPort(3001);
    }

    private static void SaveBridgePid(int pid)
    {
        try
        {
            var dir = Path.GetDirectoryName(BridgePidFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(BridgePidFilePath, pid.ToString());
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Failed to write bridge PID file: {0}", ex.Message);
        }
    }

    private static void TryKillBridgeProcessFromPidFile()
    {
        try
        {
            if (!File.Exists(BridgePidFilePath))
                return;

            var txt = File.ReadAllText(BridgePidFilePath).Trim();
            if (int.TryParse(txt, out var pid) && pid > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                        DevLog.WriteLine("Bridge process killed from PID file: {0}", pid);
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Failed to kill bridge process from PID file: {0}", ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(BridgePidFilePath))
                    File.Delete(BridgePidFilePath);
            }
            catch
            {
            }
        }
    }

    private static void TryKillBridgeProcessOnPort(int port)
    {
        try
        {
            using var netstat = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c netstat -ano -p tcp | findstr :{port}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (netstat is null)
                return;

            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(1500);
            if (string.IsNullOrWhiteSpace(output))
                return;

            var pidMatches = Regex.Matches(output, @"\s+(\d+)\s*$", RegexOptions.Multiline);
            var killedAny = false;
            foreach (Match m in pidMatches)
            {
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out var pid) || pid <= 0)
                    continue;

                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                        killedAny = true;
                        DevLog.WriteLine("Bridge process killed on TCP port {0}: pid={1}", port, pid);
                    }
                }
                catch
                {
                }
            }

            if (!killedAny)
            {
                DevLog.WriteLine("No running bridge process found on TCP port {0}", port);
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Failed to kill bridge process on port {0}: {1}", port, ex.Message);
        }
    }

    private void UpdateServerStatusUi(bool isRunning)
    {
        if (ServerStatusText != null)
        {
            ServerStatusText.Text = isRunning ? "Running" : "Stopped";
            ServerStatusText.Foreground = isRunning
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x27, 0xAE, 0x60))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA0, 0xAC));
        }

        if (ServerStatusToggle != null)
        {
            _isUpdatingServerStatusToggle = true;
            try
            {
                ServerStatusToggle.IsChecked = isRunning;
            }
            finally
            {
                _isUpdatingServerStatusToggle = false;
            }
        }
    }

    private static string ResolveBridgePath()
    {
        var envPath = Environment.GetEnvironmentVariable("TSUKI_DISCORD_BRIDGE_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
            return envPath;

        var local = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "discord-voice-bridge"));
        if (Directory.Exists(local))
            return local;

        var cwd = Path.Combine(Environment.CurrentDirectory, "discord-voice-bridge");
        if (Directory.Exists(cwd))
            return cwd;

        return local;
    }
}

