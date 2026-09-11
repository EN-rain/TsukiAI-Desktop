using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class SettingsWindow
{
    private static List<AudioDeviceItem> GetOutputDevices()
    {
        var devices = new List<AudioDeviceItem> { new() { Id = -1, Name = "Default Device" } };
        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add(new AudioDeviceItem { Id = i, Name = caps.ProductName });
        }

        return devices;
    }

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

    private void RadioTtsMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTtsPanelVisibility(TtsMode.OpenVoice);
    }

    private void UpdateTtsPanelVisibility(TtsMode mode)
    {
        if (OpenVoiceTtsPanel == null)
        {
            return;
        }

        OpenVoiceTtsPanel.Visibility = Visibility.Visible;
    }

    private async void TestOpenVoice_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        var url = NormalizeOpenVoiceUrl(vm.OpenVoiceUrl);
        vm.OpenVoiceUrl = url;
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Please enter an OpenVoice V2 URL first.", "No URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TestOpenVoiceButton.IsEnabled = false;
        TestOpenVoiceButton.Content = "Testing...";

        try
        {
            var runtimeSettings = EnvConfiguration.ApplyToSettings(Result);
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post,
                $"{url.TrimEnd('/')}/tts")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    text = "connection test",
                    language = "EN"
                })
            };
            if (!string.IsNullOrWhiteSpace(runtimeSettings.OpenVoiceApiKey))
                request.Headers.TryAddWithoutValidation("X-Api-Key", runtimeSettings.OpenVoiceApiKey);
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", Guid.NewGuid().ToString("N"));
            using var synthResp = await OpenVoiceTestClient.SendAsync(
                request,
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var wavBytes = await synthResp.Content.ReadAsByteArrayAsync();

            if (!synthResp.IsSuccessStatusCode || wavBytes.Length == 0 ||
                wavBytes.Length < 12 ||
                System.Text.Encoding.ASCII.GetString(wavBytes, 0, 4) != "RIFF")
            {
                var synthBody = string.Empty;
                try { synthBody = System.Text.Encoding.UTF8.GetString(wavBytes); } catch { }
                var details = BuildRemoteTtsErrorDetails(url, synthResp.StatusCode, synthResp.ReasonPhrase, synthBody, "/tts");
                MessageBox.Show(this, details, "OpenVoice Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            MessageBox.Show(
                this,
                $"Connection successful.\nOpenVoice V2 synthesis: OK ({wavBytes.Length} bytes)",
                "OpenVoice Connection",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Remote TTS connection failed:\n{ex.Message}", "Connection Test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestOpenVoiceButton.IsEnabled = true;
            TestOpenVoiceButton.Content = "Test Connection";
        }
    }
}
