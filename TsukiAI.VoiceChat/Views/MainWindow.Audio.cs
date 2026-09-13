using System.Windows;
using System.Windows.Input;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Services;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class MainWindow
{
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

        _settings = EnvConfiguration.ApplyToSettings(SettingsService.Load());
        var deviceNumber = _settings.VoiceOutputDeviceNumber;
        DevLog.WriteLine("[PlayHere] device={0}, tts_engine=Qwen3TtsFullIcl, qwen_tts_url={1}",
            deviceNumber, string.IsNullOrWhiteSpace(_settings.QwenTtsUrl) ? "(empty)" : _settings.QwenTtsUrl);
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
            _settings = EnvConfiguration.ApplyToSettings(SettingsService.Load());
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

    private Task<string> PrepareManualTtsTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(text.Trim());
    }

    private async Task<byte[]> SynthesizePreviewWavAsync(string text, CancellationToken ct)
    {
        using var qwenTts = new QwenTtsClient(_settings.QwenTtsUrl, _settings.QwenTtsApiKey);
        return await qwenTts.SynthesizeWavAsync(text, TtsLanguageDetector.Detect(text), ct);
    }

    private void LoadVoiceReceptionSettings()
    {
        _settings = EnvConfiguration.ApplyToSettings(SettingsService.Load());
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
