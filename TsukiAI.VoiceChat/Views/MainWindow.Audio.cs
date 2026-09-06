using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Services;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class MainWindow
{
    private static readonly HttpClient PreviewTtsClient = CreatePreviewTtsClient();

    private static HttpClient CreatePreviewTtsClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("ngrok-skip-browser-warning", "true");
        return client;
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

    private void ActivityFeedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox feedBox)
        {
            feedBox.ScrollToEnd();
        }
    }

    private async void TtsTestPlayHere_Click(object sender, RoutedEventArgs e)
    {
        var text = TtsTestInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text == TtsPlaceholder)
        {
            MessageBox.Show(this, "Enter text to test TTS.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (DataContext is TsukiAI.VoiceChat.ViewModels.VoiceChatViewModel vm)
        {
            vm.NotifyManualTtsQueued(text);
        }

        _settings = SettingsService.Load();
        var deviceNumber = _settings.VoiceOutputDeviceNumber;
        DevLog.WriteLine("[PlayHere] device={0}, tts_mode={1}, cloud_url={2}",
            deviceNumber, _settings.TtsMode, string.IsNullOrWhiteSpace(_settings.CloudTtsUrl) ? "(empty)" : _settings.CloudTtsUrl);
        await PlayVoicePreviewAsync(text, deviceNumber);
    }

    private async void PlatformPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        var text = TtsTestInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text == TtsPlaceholder)
        {
            MessageBox.Show(this, "Enter text to test TTS.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (DataContext is TsukiAI.VoiceChat.ViewModels.VoiceChatViewModel vm)
        {
            vm.NotifyManualTtsQueued(text);
        }

        // Local playback only: VRChat routes through the voice-chat output device,
        // Other platforms test the selected voice output device.
        await PlayVoicePreviewAsync(text, _settings.VoiceChatOutputDeviceNumber);
    }

    private async Task PlayVoicePreviewAsync(string text, int outputDeviceNumber)
    {
        try
        {
            _settings = SettingsService.Load();
            var preparedText = await PrepareManualTtsTextAsync(text, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(preparedText))
            {
                return;
            }

            var wav = await SynthesizePreviewWavAsync(preparedText, CancellationToken.None);
            if (wav.Length == 0)
            {
                MessageBox.Show(this, "TTS returned empty audio.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using var playback = new TtsPlaybackService();
            playback.SetOutputDeviceNumber(outputDeviceNumber);
            await playback.PlayWavAsync(wav);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Audio playback failed: {ex.Message}",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<string> PrepareManualTtsTextAsync(string text, CancellationToken ct)
    {
        var input = text.Trim();
        if (!ShouldTranslateManualTts())
        {
            return input;
        }

        using var translationService = new TranslationService(_settings);
        if (!translationService.IsEnabled)
        {
            MessageBox.Show(
                this,
                "English to Japanese manual TTS requires DeepL translation to be enabled in Main Settings.",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return string.Empty;
        }

        return await translationService.TranslateToJapaneseAsync(input, ct);
    }

    private bool ShouldTranslateManualTts() => _settings.VoiceTranslateToJapanese;

    private async Task<byte[]> SynthesizePreviewWavAsync(string text, CancellationToken ct)
    {
        if (_settings.TtsMode == TtsMode.CloudRemote)
        {
            if (string.IsNullOrWhiteSpace(_settings.CloudTtsUrl))
            {
                return Array.Empty<byte>();
            }

            try
            {
                var baseUrl = _settings.CloudTtsUrl.TrimEnd('/');
                using var queryResp = await PreviewTtsClient.PostAsync(
                    $"{baseUrl}/audio_query?text={Uri.EscapeDataString(text)}&speaker={_settings.VoicevoxSpeakerStyleId}",
                    content: null,
                    ct);
                queryResp.EnsureSuccessStatusCode();
                var queryJson = await queryResp.Content.ReadAsStringAsync(ct);

                using var synthReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/synthesis?speaker={_settings.VoicevoxSpeakerStyleId}")
                {
                    Content = new StringContent(queryJson, Encoding.UTF8, "application/json")
                };
                using var synthResp = await PreviewTtsClient.SendAsync(synthReq, ct);
                synthResp.EnsureSuccessStatusCode();
                return await synthResp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("[MainWindow][CloudTTS Preview] failed: {0}", ex.GetBaseException().Message);
                return Array.Empty<byte>();
            }
        }

        using var voicevox = new VoicevoxClient(_settings.VoicevoxBaseUrl);
        return await voicevox.SynthesizeWavAsync(text, _settings.VoicevoxSpeakerStyleId, ct);
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

        ApplyPlatformUiState();
    }

    private void VoiceReceptionToggle_Checked(object sender, RoutedEventArgs e) => PersistVoiceReceptionState(true);

    private void VoiceReceptionToggle_Unchecked(object sender, RoutedEventArgs e) => PersistVoiceReceptionState(false);

    private void PersistVoiceReceptionState(bool enabled)
    {
        _settings = _settings with { VoiceTextReceptionEnabled = enabled };
        SettingsService.Save(_settings);
    }
}
