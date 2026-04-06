using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            DiscordTranslationStrategyIndex = (int)_initialSettings.DiscordTranslationStrategy,
            UseMicrophoneInput          = _initialSettings.UseMicrophoneInput,
            MicrophonePushToTalk        = _initialSettings.MicrophonePushToTalk
        };

        DataContext = _viewModel;
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
            SttMode                     = SttMode.CloudAssemblyAI,
            DiscordTranslationStrategy  = (TranslationStrategy)_viewModel.DiscordTranslationStrategyIndex,
            VoiceChatInputDeviceNumber  = _viewModel.VoiceChatInputDeviceNumber,
            VoiceChatOutputDeviceNumber = _viewModel.VoiceChatOutputDeviceNumber,
            VoiceOutputDeviceNumber     = _viewModel.VoiceChatOutputDeviceNumber,
            UseMicrophoneInput          = _viewModel.UseMicrophoneInput,
            MicrophonePushToTalk        = _viewModel.MicrophonePushToTalk,
            MicrophoneDeviceId          = GetSelectedMicrophoneDeviceId()
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

}

// =========================================================================
//  View-models
// =========================================================================

public class SettingsVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public int VoiceChatInputDeviceNumber  { get; set; } = -1;
    public int VoiceChatOutputDeviceNumber { get; set; } = -1;
    public List<AudioDeviceItem> InputDevices  { get; set; } = new();
    public List<AudioDeviceItem> OutputDevices { get; set; } = new();
    public int  DiscordTranslationStrategyIndex { get; set; }
    public bool UseMicrophoneInput   { get; set; }
    public bool MicrophonePushToTalk { get; set; }
}

public sealed class AudioDeviceItem
{
    public int    Id   { get; set; }
    public string Name { get; set; } = string.Empty;
}
