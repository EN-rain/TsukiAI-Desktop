using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class SettingsWindow
{
    private void CaptureVoiceReceptionKey_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingVoiceReceptionKey = !_isCapturingVoiceReceptionKey;
        UpdateVoiceReceptionCaptureUi();
        if (_isCapturingVoiceReceptionKey)
        {
            Focus();
        }
    }

    private void UpdateVoiceReceptionCaptureUi()
    {
        if (BtnCaptureVoiceReceptionKey != null)
        {
            BtnCaptureVoiceReceptionKey.Content = _isCapturingVoiceReceptionKey ? "Cancel" : "Edit Key";
        }

        if (TxtVoiceReceptionKeyHint != null)
        {
            TxtVoiceReceptionKeyHint.Text = _isCapturingVoiceReceptionKey
                ? "Press any key now to set toggle key. Press Esc to cancel."
                : "Press this key in main window to toggle voice reception (e.g. F8, F9, Pause).";
        }

        if (TxtVoiceReceptionToggleKey != null)
        {
            TxtVoiceReceptionToggleKey.BorderBrush = _isCapturingVoiceReceptionKey
                ? (FindResource("AccentBrushRes") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.MediumPurple)
                : (FindResource("BorderBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray);
        }
    }

    private void PopulateMicrophoneDevices(int selectedDeviceId)
    {
        if (CmbMicrophoneDevice == null)
        {
            return;
        }

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
        var selected = CmbMicrophoneDevice?.SelectedItem as ComboBoxItem;
        MessageBox.Show($"Selected microphone: {selected?.Content}", "Microphone Test", MessageBoxButton.OK, MessageBoxImage.Information);
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

            var envDir = System.IO.Path.GetDirectoryName(envPath)!;
            if (!System.IO.File.Exists(envPath))
            {
                var examplePath = System.IO.Path.Combine(envDir, ".env.example");
                if (System.IO.File.Exists(examplePath))
                    System.IO.File.Copy(examplePath, envPath);
                else
                    System.IO.File.WriteAllText(envPath, string.Empty);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = envPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open .env:\n{ex.Message}", "Open .env",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

