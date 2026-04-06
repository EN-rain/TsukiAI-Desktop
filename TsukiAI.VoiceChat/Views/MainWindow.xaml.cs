using System.Windows;
using System.Windows.Input;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using TsukiAI.Core.Services;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class MainWindow : Window
{
    private const string TtsPlaceholder = "Type text to test TTS...";
    private static readonly string BridgePath = ResolveBridgePath();
    private static readonly string BridgePidFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TsukiAI",
        "discord-bridge.pid");
    private Process? _serverProcess;
    private TsukiAI.Core.Models.AppSettings _settings = TsukiAI.Core.Models.AppSettings.Default;
    private Key _voiceReceptionToggleKey = Key.F8;
    private bool _isUpdatingServerStatusToggle;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        LoadVoiceReceptionSettings();
        UpdateServerStatusUi(false);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        StopBridgeServerProcess();
    }

    private void OpenMainSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsWindow = new SettingsWindow(owner: this);
            settingsWindow.ShowDialog();
            LoadVoiceReceptionSettings();
        }
        catch (Exception ex)
        {
            var details = ex.InnerException is null ? ex.Message : $"{ex.Message}\n{ex.InnerException.Message}";
            System.Windows.MessageBox.Show(
                this,
                $"Settings could not be opened.\n\n{details}",
                "TsukiAI Voice Chat",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void DevLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new DevLogWindow { Owner = this };
            win.Show();
        }
        catch (Exception ex)
        {
            var details = ex.InnerException is null ? ex.Message : $"{ex.Message}\n{ex.InnerException.Message}";
            MessageBox.Show(
                this,
                $"Dev Logs could not be opened.\n\n{details}",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AllUsersFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusState == null || FocusStatusDescription == null)
        {
            return;
        }
        FocusStatusTitle.Text = "All Users Mode";
        FocusStatusState.Text = "Active";
        FocusStatusDescription.Text = "Captures everyone in the selected voice channel.";
        if (SpecificUserPanel != null)
        {
            SpecificUserPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusState == null || FocusStatusDescription == null)
        {
            return;
        }
        FocusStatusTitle.Text = "Auto Focus Mode";
        FocusStatusState.Text = "Active";
        FocusStatusDescription.Text = "Automatically detects and focuses on the speaking user.";
        if (SpecificUserPanel != null)
        {
            SpecificUserPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SpecificUserFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusState == null || FocusStatusDescription == null)
        {
            return;
        }
        FocusStatusTitle.Text = "Specific User Mode";
        FocusStatusState.Text = "Waiting";
        FocusStatusDescription.Text = "Enter a Discord user ID and click Apply.";
        if (SpecificUserPanel != null)
        {
            SpecificUserPanel.Visibility = Visibility.Visible;
        }
    }

    private void ApplyFocusedUser_Click(object sender, RoutedEventArgs e)
    {
        var userId = FocusedUserIdInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
        {
            MessageBox.Show(this, "Enter a Discord user ID first.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FocusStatusTitle.Text = "Specific User Mode";
        FocusStatusState.Text = "Applied";
        FocusStatusDescription.Text = $"Focused user ID: {userId}";
    }

    private void TtsTestInput_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TtsTestInput.Text == TtsPlaceholder)
        {
            TtsTestInput.Text = string.Empty;
        }
    }

    private void TtsTestInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TtsTestInput.Text))
        {
            TtsTestInput.Text = TtsPlaceholder;
        }
    }

    private void TtsTestInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TtsCharCounter == null)
        {
            return;
        }
        var length = TtsTestInput.Text == TtsPlaceholder ? 0 : TtsTestInput.Text.Length;
        TtsCharCounter.Text = $"{length} / 1000";
    }

    private void ActivityFeedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox feedBox)
        {
            feedBox.ScrollToEnd();
        }
    }

    private void TtsTestPlayHere_Click(object sender, RoutedEventArgs e)
    {
        var text = TtsTestInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text == TtsPlaceholder)
        {
            MessageBox.Show(this, "Enter text to test TTS.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this, "Play Here UI copied from Desktop. TTS playback wiring is in TsukiAI.Desktop.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void TtsTestPlayInDiscord_Click(object sender, RoutedEventArgs e)
    {
        var text = TtsTestInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text == TtsPlaceholder)
        {
            MessageBox.Show(this, "Enter text to test TTS.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var playInDiscordButton = sender as System.Windows.Controls.Button;
        if (playInDiscordButton is not null)
        {
            playInDiscordButton.IsEnabled = false;
        }

        try
        {
            // Ensure bridge is running before posting playback request.
            if (_serverProcess is not { HasExited: false })
            {
                var started = TryStartBridgeServer(showSuccessMessage: false);
                if (!started)
                {
                    MessageBox.Show(this, "Bridge server is not running.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var payload = JsonSerializer.Serialize(new { text });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("http://127.0.0.1:3001/play-tts", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                DevLog.WriteLine("[Bridge HTTP] Play-in-Discord succeeded");
                return;
            }

            var error = ExtractErrorMessage(responseBody);
            MessageBox.Show(
                this,
                $"Play in Discord failed ({(int)response.StatusCode}): {error}",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Play in Discord failed: {ex.Message}",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (playInDiscordButton is not null)
            {
                playInDiscordButton.IsEnabled = true;
            }
        }
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "No response body";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
            {
                return errorProp.GetString() ?? body;
            }
        }
        catch
        {
            // ignore parsing failure, return raw body below
        }

        return body.Length > 220 ? body[..220] + "..." : body;
    }

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
        // Try the most direct option first, then shell-based fallbacks for PATH resolution.
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

                // If it exits immediately, treat as failed startup and try next candidate.
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

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Visibility = Visibility.Visible;
            RestoreButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Visibility = Visibility.Collapsed;
            RestoreButton.Visibility = Visibility.Visible;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        StopBridgeServerProcess();

        System.Windows.Application.Current.Shutdown();
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
            // best effort
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
        // If window-level process tracking is lost, use PID file and port lookup as a fallback.
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
                    // Process may already be gone
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
                // best effort
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
                    // best effort
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

    private void LoadVoiceReceptionSettings()
    {
        _settings = SettingsService.Load();
        _voiceReceptionToggleKey = ParseToggleHotkey(_settings.VoiceReceptionToggleKey, Key.F8);

        if (VoiceReceptionToggle != null)
        {
            VoiceReceptionToggle.Checked -= VoiceReceptionToggle_Checked;
            VoiceReceptionToggle.Unchecked -= VoiceReceptionToggle_Unchecked;
            VoiceReceptionToggle.IsChecked = _settings.VoiceTextReceptionEnabled;
            VoiceReceptionToggle.ToolTip = $"Toggle hotkey: {_voiceReceptionToggleKey}";
            VoiceReceptionToggle.Checked += VoiceReceptionToggle_Checked;
            VoiceReceptionToggle.Unchecked += VoiceReceptionToggle_Unchecked;
        }
    }

    private static Key ParseToggleHotkey(string? hotkeyText, Key fallback)
    {
        var raw = (hotkeyText ?? string.Empty).Trim();
        if (Enum.TryParse<Key>(raw, true, out var key) && key != Key.None)
        {
            return key;
        }

        return fallback;
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key != _voiceReceptionToggleKey)
        {
            return;
        }

        if (VoiceReceptionToggle == null)
        {
            return;
        }

        VoiceReceptionToggle.IsChecked = !(VoiceReceptionToggle.IsChecked ?? false);
        e.Handled = true;
    }

    private void VoiceReceptionToggle_Checked(object sender, RoutedEventArgs e) => PersistVoiceReceptionState(true);

    private void VoiceReceptionToggle_Unchecked(object sender, RoutedEventArgs e) => PersistVoiceReceptionState(false);

    private void PersistVoiceReceptionState(bool enabled)
    {
        _settings = _settings with { VoiceTextReceptionEnabled = enabled };
        SettingsService.Save(_settings);
    }
}
