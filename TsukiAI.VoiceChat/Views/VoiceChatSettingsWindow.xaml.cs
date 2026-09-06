using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            VoicePlatformIndex          = _initialSettings.VoicePlatform == VoiceIntegrationPlatform.VrChat ? 0 : 1,
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
        // Include explicit "Default" entry so device IDs in the list match
        // NAudio WaveOut device numbers (0 = first real device, -1 = OS default).
        var devices = new List<AudioDeviceItem>
        {
            new AudioDeviceItem { Id = -1, Name = "Default Output" }
        };
        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add(new AudioDeviceItem { Id = i, Name = caps.ProductName });
        }

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
            VoicePlatform               = _viewModel.VoicePlatformIndex == 0 ? VoiceIntegrationPlatform.VrChat : VoiceIntegrationPlatform.Other,
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
            OnPropertyChanged(nameof(IsVrChatPlatform));
            OnPropertyChanged(nameof(IsOtherPlatform));
            ApplyPlatformDefaults();
        }
    }

    public bool IsVrChatPlatform => VoicePlatformIndex == 0;
    public bool IsOtherPlatform => VoicePlatformIndex == 1;

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
