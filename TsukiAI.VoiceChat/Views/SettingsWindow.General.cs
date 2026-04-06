using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using TsukiAI.Core.Models;
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
        UpdateTtsPanelVisibility(RadioLocalTts.IsChecked == true ? TtsMode.LocalVoiceVox : TtsMode.CloudRemote);
    }

    private void UpdateTtsPanelVisibility(TtsMode mode)
    {
        if (LocalTtsPanel == null || CloudTtsPanel == null)
        {
            return;
        }

        LocalTtsPanel.Visibility = mode == TtsMode.LocalVoiceVox ? Visibility.Visible : Visibility.Collapsed;
        CloudTtsPanel.Visibility = mode == TtsMode.CloudRemote ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TestCloudTts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        var url = NormalizeCloudTtsUrl(vm.CloudTtsUrl);
        vm.CloudTtsUrl = url;
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Please enter a Remote TTS URL first.", "No URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TestCloudTtsButton.IsEnabled = false;
        TestCloudTtsButton.Content = "Testing...";

        try
        {
            using var versionResp = await CloudTtsTestClient.GetAsync($"{url}/version");
            var versionBody = await versionResp.Content.ReadAsStringAsync();
            if (!versionResp.IsSuccessStatusCode)
            {
                var details = BuildRemoteTtsErrorDetails(url, versionResp.StatusCode, versionResp.ReasonPhrase, versionBody, "/version");
                MessageBox.Show(this, details, "Remote TTS Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var probeText = "connection test";
            using var queryResp = await CloudTtsTestClient.PostAsync(
                $"{url}/audio_query?text={Uri.EscapeDataString(probeText)}&speaker={Result.VoicevoxSpeakerStyleId}",
                content: null);
            var queryBody = await queryResp.Content.ReadAsStringAsync();
            if (!queryResp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(queryBody))
            {
                var details = BuildRemoteTtsErrorDetails(url, queryResp.StatusCode, queryResp.ReasonPhrase, queryBody, "/audio_query");
                MessageBox.Show(this, details, "Remote TTS Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using var synthReq = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post,
                $"{url}/synthesis?speaker={Result.VoicevoxSpeakerStyleId}")
            {
                Content = new System.Net.Http.StringContent(queryBody, System.Text.Encoding.UTF8, "application/json")
            };
            synthReq.Headers.TryAddWithoutValidation("accept", "audio/wav");
            using var synthResp = await CloudTtsTestClient.SendAsync(synthReq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var wavBytes = await synthResp.Content.ReadAsByteArrayAsync();

            if (!synthResp.IsSuccessStatusCode || wavBytes.Length == 0)
            {
                var synthBody = string.Empty;
                try { synthBody = System.Text.Encoding.UTF8.GetString(wavBytes); } catch { }
                var details = BuildRemoteTtsErrorDetails(url, synthResp.StatusCode, synthResp.ReasonPhrase, synthBody, "/synthesis");
                MessageBox.Show(this, details, "Remote TTS Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var version = (versionBody ?? string.Empty).Trim().Trim('"');
            MessageBox.Show(
                this,
                $"Connection successful.\nEngine version: {version}\nSynthesis: OK ({wavBytes.Length} bytes)",
                "Remote TTS Connection",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Remote TTS connection failed:\n{ex.Message}", "Connection Test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestCloudTtsButton.IsEnabled = true;
            TestCloudTtsButton.Content = "Test Connection";
        }
    }
}
