using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Net.Sockets;
using NAudio.Wave;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class VoiceChatSettingsWindow : Window
{
    private readonly SettingsVm _viewModel;
    private readonly AppSettings _initialSettings;

    public AppSettings? Result { get; private set; }

    public VoiceChatSettingsWindow()
    {
        InitializeComponent();
        _initialSettings = SettingsService.Load();

        _viewModel = new SettingsVm
        {
            VoiceChatInputDeviceNumber  = _initialSettings.VoiceChatInputDeviceNumber,
            VoiceChatOutputDeviceNumber = _initialSettings.VoiceChatOutputDeviceNumber,
            InputDevices                = GetInputDevices(),
            OutputDevices               = GetOutputDevices(),
            VoicePlatformIndex          = (int)_initialSettings.VoicePlatform,
            SttModeIndex                = (int)_initialSettings.SttMode,
            SttLanguageCode             = NormalizeSttLanguageCode(_initialSettings.SttLanguageCode),
            DiscordTranslationStrategyIndex = (int)_initialSettings.DiscordTranslationStrategy,
            UseMicrophoneInput          = _initialSettings.UseMicrophoneInput,
            MicrophonePushToTalk        = _initialSettings.MicrophonePushToTalk,
            VoiceReceptionToggleKeyText = NormalizeHotkey(_initialSettings.VoiceReceptionToggleKey),
            VrChatOscHost               = _initialSettings.VrChatOscHost,
            VrChatOscInputPortText      = _initialSettings.VrChatOscInputPort.ToString(),
            VrChatOscOutputPortText     = _initialSettings.VrChatOscOutputPort.ToString(),
            VrChatUseChatboxFallback    = _initialSettings.VrChatUseChatboxFallback
        };

        DataContext = _viewModel;
        _viewModel.ApplyPlatformDefaults();
        PopulateMicrophoneDevices(_initialSettings.MicrophoneDeviceId);
    }


    // =====================================================================
    //  Audio device helpers
    // =====================================================================

    private static List<AudioDeviceItem> GetInputDevices()
    {
        var devices = new List<AudioDeviceItem>();
        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            devices.Add(new AudioDeviceItem { Id = i, Name = caps.ProductName });
        }

        if (devices.Count == 0)
            devices.Add(new AudioDeviceItem { Id = -1, Name = "Default Input" });

        return devices;
    }

    private static List<AudioDeviceItem> GetOutputDevices()
    {
        var devices = new List<AudioDeviceItem>();
        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add(new AudioDeviceItem { Id = i, Name = caps.ProductName });
        }

        if (devices.Count == 0)
            devices.Add(new AudioDeviceItem { Id = -1, Name = "Default Output" });

        return devices;
    }

    private int GetSelectedMicrophoneDeviceId()
    {
        if (CmbMicrophoneDevice.SelectedItem is ComboBoxItem item && item.Tag is int deviceId)
            return deviceId;
        return -1;
    }

    // =====================================================================
    //  Event handlers
    // =====================================================================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _initialSettings with
        {
            SttMode                     = (SttMode)_viewModel.SttModeIndex,
            SttLanguageCode             = NormalizeSttLanguageCode(_viewModel.SttLanguageCode),
            VoicePlatform               = (VoiceIntegrationPlatform)_viewModel.VoicePlatformIndex,
            DiscordTranslationStrategy  = (TranslationStrategy)_viewModel.DiscordTranslationStrategyIndex,
            VoiceChatInputDeviceNumber  = _viewModel.VoiceChatInputDeviceNumber,
            VoiceChatOutputDeviceNumber = _viewModel.VoiceChatOutputDeviceNumber,
            VoiceOutputDeviceNumber     = _viewModel.VoiceChatOutputDeviceNumber,
            UseMicrophoneInput          = _viewModel.UseMicrophoneInput,
            MicrophonePushToTalk        = _viewModel.MicrophonePushToTalk,
            MicrophoneDeviceId          = GetSelectedMicrophoneDeviceId(),
            VoiceReceptionToggleKey     = NormalizeHotkey(_viewModel.VoiceReceptionToggleKeyText),
            VrChatOscHost               = NormalizeVrChatHost(_viewModel.VrChatOscHost),
            VrChatOscInputPort          = ParsePort(_viewModel.VrChatOscInputPortText, 9000),
            VrChatOscOutputPort         = ParsePort(_viewModel.VrChatOscOutputPortText, 9001),
            VrChatUseChatboxFallback    = _viewModel.VrChatUseChatboxFallback
        };

        SettingsService.Save(Result);
        UpdateDiscordBridgeEnv(Result.SttMode, Result.GroqApiKey, Result.SttLanguageCode);
        DialogResult = true;
        Close();
    }

    private void ClearVoiceChatHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Are you sure you want to clear voice chat history?",
            "Clear Voice Chat History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        ConversationHistoryService.ClearVoiceChatHistory();
        MessageBox.Show("Voice chat history cleared.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PopulateMicrophoneDevices(int selectedDeviceId)
    {
        CmbMicrophoneDevice.Items.Clear();
        CmbMicrophoneDevice.Items.Add(new ComboBoxItem { Content = "Default Microphone", Tag = -1 });

        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            CmbMicrophoneDevice.Items.Add(new ComboBoxItem { Content = caps.ProductName, Tag = i });
        }

        for (var i = 0; i < CmbMicrophoneDevice.Items.Count; i++)
        {
            if (CmbMicrophoneDevice.Items[i] is ComboBoxItem item && item.Tag is int id && id == selectedDeviceId)
            {
                CmbMicrophoneDevice.SelectedIndex = i;
                return;
            }
        }

        CmbMicrophoneDevice.SelectedIndex = 0;
    }

    private void TestMicrophone_Click(object sender, RoutedEventArgs e)
    {
        var selected = CmbMicrophoneDevice.SelectedItem as ComboBoxItem;
        MessageBox.Show($"Selected microphone: {selected?.Content}", "Microphone Test", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OpenDiscordEnv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var envPath = FindDiscordBridgeEnvPath();
            if (envPath is null)
            {
                MessageBox.Show(this, "Could not find discord-voice-bridge folder.", "Open .env",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var envDir = Path.GetDirectoryName(envPath)!;
            if (!File.Exists(envPath))
            {
                var examplePath = Path.Combine(envDir, ".env.example");
                if (File.Exists(examplePath))
                    File.Copy(examplePath, envPath);
                else
                    File.WriteAllText(envPath, string.Empty);
            }

            Process.Start(new ProcessStartInfo { FileName = envPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open .env:\n{ex.Message}", "Open .env",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? FindDiscordBridgeEnvPath()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "discord-voice-bridge", ".env"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "discord-voice-bridge", ".env")),
            Path.Combine(AppContext.BaseDirectory, "discord-voice-bridge", ".env")
        };

        foreach (var candidate in candidates)
        {
            var bridgeDir = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrWhiteSpace(bridgeDir) && Directory.Exists(bridgeDir))
                return candidate;
        }

        return null;
    }

    private static void UpdateDiscordBridgeEnv(SttMode sttMode, string groqApiKey, string sttLanguageCode)
    {
        var bridgeWasRunning = IsBridgeServerRunning();

        try
        {
            var envPath = FindDiscordBridgeEnvPath();
            if (envPath is null || !File.Exists(envPath))
            {
                DevLog.WriteLine("VoiceChatSettingsWindow: .env file not found");
                return;
            }

            var lines = File.ReadAllLines(envPath).ToList();
            var sttModeValue = sttMode == SttMode.CloudGroqWhisper ? "groq" : "assemblyai";

            var sttModeIndex = lines.FindIndex(l => l.StartsWith("STT_MODE="));
            if (sttModeIndex >= 0)
                lines[sttModeIndex] = $"STT_MODE={sttModeValue}";
            else
                lines.Insert(0, $"STT_MODE={sttModeValue}");

            if (sttMode == SttMode.CloudGroqWhisper && !string.IsNullOrWhiteSpace(groqApiKey))
            {
                var groqKeyIndex = lines.FindIndex(l => l.StartsWith("GROQ_API_KEY="));
                if (groqKeyIndex >= 0)
                    lines[groqKeyIndex] = $"GROQ_API_KEY={groqApiKey}";
                else
                    lines.Add($"GROQ_API_KEY={groqApiKey}");
            }

            var languageCode = NormalizeSttLanguageCode(sttLanguageCode);
            var sttLangIndex = lines.FindIndex(l => l.StartsWith("STT_LANGUAGE="));
            if (sttLangIndex >= 0)
                lines[sttLangIndex] = $"STT_LANGUAGE={languageCode}";
            else
                lines.Add($"STT_LANGUAGE={languageCode}");

            File.WriteAllLines(envPath, lines);
            DevLog.WriteLine($"VoiceChatSettingsWindow: Updated STT_MODE to {sttModeValue}, STT_LANGUAGE to {languageCode}");
            RestartBridgeIfNeeded(bridgeWasRunning, envPath);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"VoiceChatSettingsWindow: Failed to update .env: {ex.Message}");
        }
    }

    private static bool IsBridgeServerRunning()
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", 3001);
            return connectTask.Wait(300) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void RestartBridgeIfNeeded(bool bridgeWasRunning, string envPath)
    {
        if (!bridgeWasRunning)
        {
            return;
        }

        try
        {
            MainWindow.StopBridgeProcessesOnShutdown();
            var bridgeDir = Path.GetDirectoryName(envPath);
            if (string.IsNullOrWhiteSpace(bridgeDir) || !Directory.Exists(bridgeDir))
            {
                return;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c npm start",
                WorkingDirectory = bridgeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                DevLog.WriteLine("VoiceChatSettingsWindow: Failed to restart bridge process");
                return;
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
            DevLog.WriteLine("VoiceChatSettingsWindow: Restarted bridge to apply new STT mode");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"VoiceChatSettingsWindow: Bridge restart failed: {ex.Message}");
        }
    }

    private static string NormalizeSttLanguageCode(string? languageCode)
    {
        var code = (languageCode ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(code) ? "auto" : code;
    }

    private static string NormalizeVrChatHost(string? host)
    {
        var normalized = (host ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "127.0.0.1" : normalized;
    }

    private static int ParsePort(string? raw, int fallback)
    {
        return int.TryParse((raw ?? string.Empty).Trim(), out var parsed) && parsed > 0 && parsed <= 65535
            ? parsed
            : fallback;
    }

    private static string NormalizeHotkey(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? "F8" : value;
    }

}

// =========================================================================
//  View-models
// =========================================================================

public class SettingsVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private int _voicePlatformIndex;
    private bool _useMicrophoneInput;
    private bool _microphonePushToTalk;
    private string _vrChatOscHost = "127.0.0.1";
    private string _vrChatOscInputPortText = "9000";
    private string _vrChatOscOutputPortText = "9001";
    private bool _vrChatUseChatboxFallback;

    public int VoiceChatInputDeviceNumber  { get; set; } = -1;
    public int VoiceChatOutputDeviceNumber { get; set; } = -1;
    public List<AudioDeviceItem> InputDevices  { get; set; } = new();
    public List<AudioDeviceItem> OutputDevices { get; set; } = new();
    public int SttModeIndex { get; set; }
    public string SttLanguageCode { get; set; } = "auto";
    public int  DiscordTranslationStrategyIndex { get; set; }
    public string VoiceReceptionToggleKeyText { get; set; } = "F8";

    public int VoicePlatformIndex
    {
        get => _voicePlatformIndex;
        set
        {
            if (_voicePlatformIndex == value) return;
            _voicePlatformIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDiscordPlatform));
            OnPropertyChanged(nameof(IsVrChatPlatform));
            ApplyPlatformDefaults();
        }
    }

    public bool IsDiscordPlatform => VoicePlatformIndex == (int)VoiceIntegrationPlatform.Discord;
    public bool IsVrChatPlatform => VoicePlatformIndex == (int)VoiceIntegrationPlatform.VrChat;

    public bool UseMicrophoneInput
    {
        get => _useMicrophoneInput;
        set
        {
            var forcedValue = IsVrChatPlatform ? true : value;
            if (_useMicrophoneInput == forcedValue) return;
            _useMicrophoneInput = forcedValue;
            OnPropertyChanged();
        }
    }

    public bool MicrophonePushToTalk
    {
        get => _microphonePushToTalk;
        set
        {
            if (_microphonePushToTalk == value) return;
            _microphonePushToTalk = value;
            OnPropertyChanged();
        }
    }

    public string VrChatOscHost
    {
        get => _vrChatOscHost;
        set
        {
            if (_vrChatOscHost == value) return;
            _vrChatOscHost = value;
            OnPropertyChanged();
        }
    }

    public string VrChatOscInputPortText
    {
        get => _vrChatOscInputPortText;
        set
        {
            if (_vrChatOscInputPortText == value) return;
            _vrChatOscInputPortText = value;
            OnPropertyChanged();
        }
    }

    public string VrChatOscOutputPortText
    {
        get => _vrChatOscOutputPortText;
        set
        {
            if (_vrChatOscOutputPortText == value) return;
            _vrChatOscOutputPortText = value;
            OnPropertyChanged();
        }
    }

    public bool VrChatUseChatboxFallback
    {
        get => _vrChatUseChatboxFallback;
        set
        {
            if (_vrChatUseChatboxFallback == value) return;
            _vrChatUseChatboxFallback = value;
            OnPropertyChanged();
        }
    }

    public void ApplyPlatformDefaults()
    {
        if (IsVrChatPlatform)
        {
            UseMicrophoneInput = true;
        }
    }
}

public sealed class AudioDeviceItem
{
    public int    Id   { get; set; }
    public string Name { get; set; } = string.Empty;
}
