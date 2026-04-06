using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Net;
using NAudio.Wave;
using TsukiAI.Core.Models;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Infrastructure;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }
    private readonly Action? _clearHistory;
    private readonly Dictionary<string, DateTimeOffset> _providerRateLimitedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _testedProviders = new(StringComparer.OrdinalIgnoreCase);
    private const int DefaultProviderCooldownSeconds = 60;
    private int _providerCooldownFallbackSeconds = DefaultProviderCooldownSeconds;
    private bool _isCapturingVoiceReceptionKey;

    public SettingsWindow(
        AppSettings? initial = null,
        string? fiveMinuteSummaries = null,
        string? hourlySummaries = null,
        Window? owner = null,
        Action? clearHistory = null
    )
    {
        if (owner != null)
        {
            Owner = owner;
        }

        InitializeComponent();
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;

        Result = initial ?? SettingsService.Load();
        _clearHistory = clearHistory;

        var vm = new SettingsVm
        {
            IsActivityLoggingEnabled = Result.IsActivityLoggingEnabled,
            SampleIntervalMinutesText = Result.SampleIntervalMinutes.ToString(),
            SummarizeIntervalMinutesText = Result.SummarizeIntervalMinutes.ToString(),
            RetentionHoursForRawText = Result.RetentionHoursForRaw.ToString(),
            CaptureModeIndex = Result.CaptureMode == ScreenshotCaptureMode.ActiveWindow ? 1 : 0,
            ModelName = Result.ModelName,
            AutoStartOllama = Result.AutoStartOllama,
            StopOllamaOnExit = Result.StopOllamaOnExit,
            StartupGreetingEnabled = Result.StartupGreetingEnabled,
            ProactiveMessagesEnabled = Result.ProactiveMessagesEnabled,
            ProactiveMessageAfterMinutesText = Result.ProactiveMessageAfterMinutes.ToString(),
            ProactiveMessageMaxMinutesText = Result.ProactiveMessageMaxMinutes.ToString(),
            UseGpu = Result.UseGpu,
            ModelDirectory = Result.ModelDirectory ?? string.Empty,
            VoiceEnabled = Result.VoiceEnabled,
            VoicevoxBaseUrl = Result.VoicevoxBaseUrl ?? "http://127.0.0.1:50021",
            CloudTtsUrl = NormalizeCloudTtsUrl(Result.CloudTtsUrl),
            VoicevoxSpeakerStyleIdText = Result.VoicevoxSpeakerStyleId.ToString(),
            VoiceTranslateToJapanese = Result.VoiceTranslateToJapanese,
            UseDeepLTranslate = Result.UseDeepLTranslate,
            UseDeepLFreeApi = Result.UseDeepLFreeApi,
            VoiceOutputDeviceNumber = Result.VoiceOutputDeviceNumber,
            AudioDevices = GetOutputDevices(),
            VoicePlayBeforeTypewriter = Result.VoicePlayBeforeTypewriter,
            TessdataDirectory = Result.TessdataDirectory ?? string.Empty,
            FiveMinuteSummariesText = fiveMinuteSummaries ?? string.Empty,
            HourlySummariesText = hourlySummaries ?? string.Empty,
            RemoteInferenceUrl = Result.RemoteInferenceUrl ?? string.Empty,
            RemoteInferenceApiKey = Result.RemoteInferenceApiKey ?? string.Empty,
            AiProvider = InferProviderFromUrl(Result.RemoteInferenceUrl),
            UseMultipleProviders = Result.UseMultipleAiProviders,
            MultiAiProvidersCsv = Result.MultiAiProvidersCsv ?? string.Empty,
            AutoSwitchProviderOnRateLimit = Result.AutoSwitchProviderOnRateLimit,
            ProviderRateLimitCooldownSecondsText = Result.ProviderRateLimitCooldownSeconds.ToString(),
            VoiceChatInputDeviceNumber = Result.VoiceChatInputDeviceNumber,
            VoiceChatOutputDeviceNumber = Result.VoiceChatOutputDeviceNumber,
            VoiceChatInputDevices = GetInputDevices(),
            VoiceChatOutputDevices = GetVoiceChatOutputDevices(),
            DiscordTranslationStrategyIndex = (int)Result.DiscordTranslationStrategy,
            UseMicrophoneInput = Result.UseMicrophoneInput,
            MicrophonePushToTalk = Result.MicrophonePushToTalk,
            VoiceReceptionToggleKeyText = Result.VoiceReceptionToggleKey
        };
        DataContext = vm;

        switch (Result.TtsMode)
        {
            case TtsMode.CloudRemote:
                RadioCloudTts.IsChecked = true;
                break;
            default:
                RadioLocalTts.IsChecked = true;
                break;
        }

        UpdateTtsPanelVisibility(Result.TtsMode);
        var normalizedRemoteApiKey = NormalizeApiKey(Result.RemoteInferenceApiKey);
        TxtRemoteApiKey.Password = normalizedRemoteApiKey;
        vm.RemoteInferenceApiKey = normalizedRemoteApiKey;
        TxtRemoteApiKey.PasswordChanged += (_, _) => UpdateApiKeyStatus();
        UpdateApiKeyStatus();
        SetAiProviderSelection(vm.AiProvider);
        SetInferenceModeSelection(vm.AiProvider == "custom" ? "custom" : "provider");
        if (GetInferenceModeSelectionTag() == "provider")
        {
            vm.ModelName = EnsureProviderModel(vm.AiProvider, vm.ModelName);
        }
        SetProviderModeSelection(vm.UseMultipleProviders ? "multiple" : "one");
        ApplyMultiProviderSelections(vm.MultiAiProvidersCsv);
        
        // Load multi-provider API keys with masking
        LoadMultiProviderApiKeys();
        
        // Add password change handlers to update status indicators
        if (TxtCerebrasApiKey != null) TxtCerebrasApiKey.PasswordChanged += (_, _) => { _testedProviders.Remove("cerebras"); UpdateProviderStatus("cerebras", StatusCerebras, TxtCerebrasApiKey.Password, ChkCerebras); };
        if (TxtGroqApiKey != null) TxtGroqApiKey.PasswordChanged += (_, _) => { _testedProviders.Remove("groq"); UpdateProviderStatus("groq", StatusGroq, TxtGroqApiKey.Password, ChkGroq); };
        if (TxtGeminiApiKey != null) TxtGeminiApiKey.PasswordChanged += (_, _) => { _testedProviders.Remove("gemini"); UpdateProviderStatus("gemini", StatusGemini, TxtGeminiApiKey.Password, ChkGemini); };
        if (TxtGitHubApiKey != null) TxtGitHubApiKey.PasswordChanged += (_, _) => { _testedProviders.Remove("github"); UpdateProviderStatus("github", StatusGitHub, TxtGitHubApiKey.Password, ChkGitHub); };
        
        // Update status indicators
        UpdateProviderStatusIndicators();
        
        if (ChkAutoSwitchOnRateLimit != null)
        {
            ChkAutoSwitchOnRateLimit.IsChecked = vm.AutoSwitchProviderOnRateLimit;
        }
        UpdateInferenceModeUi();
        RefreshProviderLimitRings();
        PopulateMicrophoneDevices(Result.MicrophoneDeviceId);
        _providerCooldownFallbackSeconds = Result.ProviderRateLimitCooldownSeconds > 0
            ? Result.ProviderRateLimitCooldownSeconds
            : DefaultProviderCooldownSeconds;
    }

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

    private static List<AudioDeviceItem> GetVoiceChatOutputDevices()
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

    private void UpdateApiKeyStatus()
    {
        if (TxtConnectionStatus == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(NormalizeApiKey(TxtRemoteApiKey.Password)))
        {
            TxtConnectionStatus.Text = "API Key is configured";
            TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
            return;
        }

        TxtConnectionStatus.Text = string.Empty;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateInferenceModeUi();
        ShowTab(0, animateIndicator: false); // Show Inference tab by default
        UpdateProviderRateLimitIndicator();
        UpdateVoiceReceptionCaptureUi();
        await AutoValidateCheckedProvidersAsync();
    }

    private void CaptureVoiceReceptionKey_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingVoiceReceptionKey = !_isCapturingVoiceReceptionKey;
        UpdateVoiceReceptionCaptureUi();
        if (_isCapturingVoiceReceptionKey)
        {
            Focus();
        }
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isCapturingVoiceReceptionKey || DataContext is not SettingsVm vm)
        {
            return;
        }

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.None
            or System.Windows.Input.Key.LeftShift
            or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftCtrl
            or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt
            or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin
            or System.Windows.Input.Key.RWin)
        {
            return;
        }

        if (key == System.Windows.Input.Key.Escape)
        {
            _isCapturingVoiceReceptionKey = false;
            UpdateVoiceReceptionCaptureUi();
            e.Handled = true;
            return;
        }

        var detectedKey = key.ToString();
        vm.VoiceReceptionToggleKeyText = detectedKey;
        if (TxtVoiceReceptionToggleKey != null)
        {
            TxtVoiceReceptionToggleKey.Text = detectedKey;
        }
        _isCapturingVoiceReceptionKey = false;
        UpdateVoiceReceptionCaptureUi();
        e.Handled = true;
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

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string tagStr)
        {
            return;
        }

        if (!int.TryParse(tagStr, out var tabIndex))
        {
            return;
        }

        ShowTab(tabIndex);
    }

    private void ShowTab(int tabIndex, bool animateIndicator = true)
    {
        // Hide all content panels
        if (InferenceContent != null) InferenceContent.Visibility = Visibility.Collapsed;
        if (VoiceContent != null) VoiceContent.Visibility = Visibility.Collapsed;
        if (AdvancedContent != null) AdvancedContent.Visibility = Visibility.Collapsed;
        if (RateLimitContent != null) RateLimitContent.Visibility = Visibility.Collapsed;

        SetTabSelected(TabInference, false);
        SetTabSelected(TabVoice, false);
        SetTabSelected(TabAdvanced, false);
        SetTabSelected(TabRateLimit, false);

        // Show selected content and highlight tab
        switch (tabIndex)
        {
            case 0:
                if (InferenceContent != null) InferenceContent.Visibility = Visibility.Visible;
                SetTabSelected(TabInference, true);
                break;
            case 1:
                if (VoiceContent != null) VoiceContent.Visibility = Visibility.Visible;
                SetTabSelected(TabVoice, true);
                break;
            case 2:
                if (AdvancedContent != null) AdvancedContent.Visibility = Visibility.Visible;
                SetTabSelected(TabAdvanced, true);
                break;
            case 3:
                if (RateLimitContent != null) RateLimitContent.Visibility = Visibility.Visible;
                SetTabSelected(TabRateLimit, true);
                break;
        }

        Dispatcher.BeginInvoke(
            () => AnimateIndicatorToTab(tabIndex, animateIndicator),
            DispatcherPriority.Loaded);
    }

    private void AnimateIndicatorToTab(int tabIndex, bool animate)
    {
        if (ActiveTabIndicator == null || ActiveTabIndicatorTransform == null || TabStripPanel == null)
        {
            return;
        }

        var selectedTab = tabIndex switch
        {
            0 => TabInference,
            1 => TabVoice,
            2 => TabAdvanced,
            3 => TabRateLimit,
            _ => null
        };

        if (selectedTab == null || selectedTab.ActualWidth <= 0)
        {
            return;
        }

        var tabStart = selectedTab.TransformToAncestor(TabStripPanel).Transform(new System.Windows.Point(0, 0));
        var targetX = tabStart.X + ((selectedTab.ActualWidth - ActiveTabIndicator.Width) / 2.0);

        if (!animate)
        {
            ActiveTabIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            ActiveTabIndicatorTransform.X = targetX;
            return;
        }

        const int durationMs = 280;
        var slide = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(durationMs)
        };
        slide.KeyFrames.Add(new SplineDoubleKeyFrame(
            targetX,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)),
            new KeySpline(0.22, 1.0, 0.36, 1.0)));

        ActiveTabIndicatorTransform.BeginAnimation(
            TranslateTransform.XProperty,
            slide,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void SetTabSelected(System.Windows.Controls.Button? tab, bool isSelected)
    {
        if (tab == null)
        {
            return;
        }

        var activeText = FindResource("AccentBrushRes") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        var inactiveText = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

        tab.Background = System.Windows.Media.Brushes.Transparent;
        tab.BorderBrush = System.Windows.Media.Brushes.Transparent;
        tab.Foreground = isSelected ? activeText : inactiveText;
        tab.FontWeight = FontWeights.Normal;
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
            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(25);
            httpClient.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true");

            using var versionResp = await httpClient.GetAsync($"{url}/version");
            var versionBody = await versionResp.Content.ReadAsStringAsync();
            if (!versionResp.IsSuccessStatusCode)
            {
                var details = BuildRemoteTtsErrorDetails(url, versionResp.StatusCode, versionResp.ReasonPhrase, versionBody, "/version");
                MessageBox.Show(this, details, "Remote TTS Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var probeText = "connection test";
            using var queryResp = await httpClient.PostAsync(
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
            using var synthResp = await httpClient.SendAsync(synthReq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        var sampleMinutes = ParseIntOr(vm.SampleIntervalMinutesText, AppSettings.Default.SampleIntervalMinutes, 1, 1440);
        var summarizeMinutes = ParseIntOr(vm.SummarizeIntervalMinutesText, AppSettings.Default.SummarizeIntervalMinutes, 1, 1440);
        var retentionHours = ParseIntOr(vm.RetentionHoursForRawText, AppSettings.Default.RetentionHoursForRaw, 1, 168);
        var proactiveAfterMinutes = ParseIntOr(vm.ProactiveMessageAfterMinutesText, AppSettings.Default.ProactiveMessageAfterMinutes, 1, 1440);
        var proactiveMaxMinutes = ParseIntOr(vm.ProactiveMessageMaxMinutesText, AppSettings.Default.ProactiveMessageMaxMinutes, 1, 1440);
        var voiceStyleId = ParseIntOr(vm.VoicevoxSpeakerStyleIdText, AppSettings.Default.VoicevoxSpeakerStyleId, 0, 9999);
        var providerCooldownSeconds = ParseIntOr(vm.ProviderRateLimitCooldownSecondsText, DefaultProviderCooldownSeconds, 5, 3600);
        var voiceReceptionToggleKey = NormalizeKeyNameOr(vm.VoiceReceptionToggleKeyText, Result.VoiceReceptionToggleKey, "F8");
        _providerCooldownFallbackSeconds = providerCooldownSeconds;

        if (proactiveMaxMinutes < proactiveAfterMinutes)
        {
            proactiveMaxMinutes = proactiveAfterMinutes;
        }

        var inferenceMode = InferenceMode.RemoteColab;
        var newMode = InteractionMode.VoiceChat;
        var ttsMode = RadioLocalTts.IsChecked == true ? TtsMode.LocalVoiceVox : TtsMode.CloudRemote;
        var captureMode = vm.CaptureModeIndex == 1 ? ScreenshotCaptureMode.ActiveWindow : ScreenshotCaptureMode.FullScreen;
        var modeChanged = Result.EnabledMode != newMode;
        if (GetInferenceModeSelectionTag() == "custom")
        {
            vm.AiProvider = "custom";
        }
        vm.UseMultipleProviders = GetProviderModeSelectionTag() == "multiple";
        if (GetInferenceModeSelectionTag() == "provider")
        {
            vm.RemoteInferenceUrl = EnsureProviderUrl(vm.AiProvider, vm.RemoteInferenceUrl);
        }
        vm.AutoSwitchProviderOnRateLimit = ChkAutoSwitchOnRateLimit?.IsChecked == true;
        vm.MultiAiProvidersCsv = GetSelectedMultiProvidersCsv();
        var deepLApiKeyFromEnv = EnvConfiguration.ApplyToSettings(Result).DeepLApiKey;
        var normalizedRemoteApiKey = NormalizeApiKey(TxtRemoteApiKey.Password);
        var updatedCerebrasApiKey = GetUpdatedApiKey(TxtCerebrasApiKey?.Password, Result.CerebrasApiKey);
        var updatedGroqApiKey = GetUpdatedApiKey(TxtGroqApiKey?.Password, Result.GroqApiKey);
        var updatedGeminiApiKey = GetUpdatedApiKey(TxtGeminiApiKey?.Password, Result.GeminiApiKey);
        var updatedGitHubApiKey = GetUpdatedApiKey(TxtGitHubApiKey?.Password, Result.GitHubApiKey);
        var updatedMistralApiKey = GetUpdatedApiKey(TxtMistralApiKey?.Password, Result.MistralApiKey);

        var runtimeApiKey = GetInferenceModeSelectionTag() == "provider"
            ? ResolveRuntimeApiKeyForProviderMode(
                vm.AiProvider,
                vm.UseMultipleProviders,
                vm.MultiAiProvidersCsv,
                updatedCerebrasApiKey,
                updatedGroqApiKey,
                updatedGeminiApiKey,
                updatedGitHubApiKey,
                updatedMistralApiKey,
                normalizedRemoteApiKey)
            : normalizedRemoteApiKey;

        var resolvedModelName = GetInferenceModeSelectionTag() == "provider"
            ? EnsureProviderModel(vm.AiProvider, vm.ModelName)
            : (string.IsNullOrWhiteSpace(vm.ModelName) ? AppSettings.Default.ModelName : vm.ModelName.Trim());

        Result = Result with
        {
            EnabledMode = newMode,
            IsActivityLoggingEnabled = vm.IsActivityLoggingEnabled,
            SampleIntervalMinutes = sampleMinutes,
            SummarizeIntervalMinutes = summarizeMinutes,
            CaptureMode = captureMode,
            RetentionHoursForRaw = retentionHours,
            ModelName = resolvedModelName,
            AutoStartOllama = vm.AutoStartOllama,
            StopOllamaOnExit = vm.StopOllamaOnExit,
            StartupGreetingEnabled = vm.StartupGreetingEnabled,
            ProactiveMessagesEnabled = vm.ProactiveMessagesEnabled,
            ProactiveMessageAfterMinutes = proactiveAfterMinutes,
            ProactiveMessageMaxMinutes = proactiveMaxMinutes,
            UseGpu = vm.UseGpu,
            ModelDirectory = vm.ModelDirectory?.Trim() ?? string.Empty,
            VoiceEnabled = vm.VoiceEnabled,
            TtsMode = ttsMode,
            VoicevoxBaseUrl = string.IsNullOrWhiteSpace(vm.VoicevoxBaseUrl) ? AppSettings.Default.VoicevoxBaseUrl : vm.VoicevoxBaseUrl.Trim(),
            CloudTtsUrl = NormalizeCloudTtsUrl(vm.CloudTtsUrl),
            VoicevoxSpeakerStyleId = voiceStyleId,
            VoiceTranslateToJapanese = vm.VoiceTranslateToJapanese,
            UseDeepLTranslate = vm.UseDeepLTranslate,
            DeepLApiKey = deepLApiKeyFromEnv,
            UseDeepLFreeApi = vm.UseDeepLFreeApi,
            VoiceOutputDeviceNumber = vm.VoiceOutputDeviceNumber,
            TessdataDirectory = vm.TessdataDirectory?.Trim() ?? string.Empty,
            VoicePlayBeforeTypewriter = vm.VoicePlayBeforeTypewriter,
            InferenceMode = inferenceMode,
            RemoteInferenceUrl = vm.RemoteInferenceUrl?.Trim() ?? string.Empty,
            RemoteInferenceApiKey = runtimeApiKey,
            UseMultipleAiProviders = vm.UseMultipleProviders,
            MultiAiProvidersCsv = vm.MultiAiProvidersCsv,
            AutoSwitchProviderOnRateLimit = vm.AutoSwitchProviderOnRateLimit,
            ProviderRateLimitCooldownSeconds = providerCooldownSeconds,
            
            // Save multi-provider API keys (only if changed from masked version)
            CerebrasApiKey = updatedCerebrasApiKey,
            GroqApiKey = updatedGroqApiKey,
            GeminiApiKey = updatedGeminiApiKey,
            GitHubApiKey = updatedGitHubApiKey,
            MistralApiKey = updatedMistralApiKey,
            
            SttMode = SttMode.CloudAssemblyAI,
            DiscordTranslationStrategy = (TranslationStrategy)vm.DiscordTranslationStrategyIndex,
            VoiceChatInputDeviceNumber = vm.VoiceChatInputDeviceNumber,
            VoiceChatOutputDeviceNumber = vm.VoiceChatOutputDeviceNumber,
            UseMicrophoneInput = vm.UseMicrophoneInput,
            MicrophonePushToTalk = vm.MicrophonePushToTalk,
            MicrophoneDeviceId = GetSelectedMicrophoneDeviceId()
            ,
            VoiceReceptionToggleKey = voiceReceptionToggleKey
        };

        SettingsService.Save(Result);

        DialogResult = true;
        Close();

        if (modeChanged)
        {
            var shouldRestart = MessageBox.Show(
                this,
                "Voice Chat Mode is now active.\nRestart now?",
                "Restart Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (shouldRestart == MessageBoxResult.Yes)
            {
                RestartApplication();
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var shouldClear = MessageBox.Show(
            this,
            "Clear all chat history? This cannot be undone.",
            "Clear Chat History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (shouldClear != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (_clearHistory != null)
            {
                _clearHistory.Invoke();
            }
            else
            {
                ConversationHistoryService.ClearChatHistory();
            }

            MessageBox.Show(this, "Chat history cleared.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to clear history: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Ollama models directory",
            ShowNewFolderButton = true,
            SelectedPath = vm.ModelDirectory ?? string.Empty
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            vm.ModelDirectory = dialog.SelectedPath;
        }
    }

    private void BrowseTessdata_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select tessdata folder (contains eng.traineddata)",
            ShowNewFolderButton = true,
            SelectedPath = vm.TessdataDirectory ?? string.Empty
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            vm.TessdataDirectory = dialog.SelectedPath;
        }
    }

    private void AiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SettingsVm vm || CmbAiProvider.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var tag = (item.Tag as string ?? "openrouter").ToLowerInvariant();
        vm.AiProvider = tag;

        if (GetInferenceModeSelectionTag() != "provider")
        {
            return;
        }

        // In provider mode, selecting a provider should map away from custom/ngrok URLs.
        // For Kimi, keep an existing moonshot .ai/.cn URL if already configured.
        vm.RemoteInferenceUrl = EnsureProviderUrl(tag, vm.RemoteInferenceUrl);
        vm.ModelName = EnsureProviderModel(tag, vm.ModelName);
    }

    private async void BtnTestRemoteConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        var isProviderMode = GetInferenceModeSelectionTag() == "provider";
        var provider = (CmbAiProvider?.SelectedItem as ComboBoxItem)?.Tag as string ?? "custom";
        var url = isProviderMode
            ? EnsureProviderUrl(provider, vm.RemoteInferenceUrl?.Trim())
            : (vm.RemoteInferenceUrl?.Trim() ?? string.Empty);
        vm.RemoteInferenceUrl = url;
        if (string.IsNullOrEmpty(url))
        {
            TxtConnectionStatus.Text = "Please enter a URL";
            TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        TxtConnectionStatus.Text = isProviderMode ? "Testing API key..." : "Testing tunnel server...";
        TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;
        BtnTestRemoteConnection.IsEnabled = false;

        try
        {
            (bool ok, string message) result;
            if (isProviderMode)
            {
                if (TryGetProviderCooldown(provider, out var remaining))
                {
                    TxtConnectionStatus.Text = $"Rate limited for {provider}. Try again in {(int)Math.Ceiling(remaining.TotalSeconds)}s.";
                    TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
                    UpdateProviderRateLimitIndicator();
                    return;
                }

                var providerApiInput = GetProviderApiInput(provider);
                var providerApiKey = ResolveProviderApiKeyForTest(provider, providerApiInput);
                var probe = await TestApiKeyAsync(provider, url, providerApiKey, _providerCooldownFallbackSeconds);
                if (probe.RateLimited)
                {
                    MarkProviderRateLimited(provider, probe.RetryAfterSeconds);
                }
                result = (probe.Ok, probe.Message);
            }
            else
            {
                using var client = new RemoteInferenceClient(url, string.Empty);
                var reachable = await client.IsServerReachableAsync();
                result = reachable
                    ? (true, "Tunnel server reachable.")
                    : (false, "Tunnel server not reachable.");
            }

            var (ok, message) = result;
            TxtConnectionStatus.Text = message;
            TxtConnectionStatus.Foreground = ok ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red;
            UpdateProviderRateLimitIndicator();
        }
        catch (Exception ex)
        {
            TxtConnectionStatus.Text = $"Error: {ex.Message}";
            TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            BtnTestRemoteConnection.IsEnabled = true;
        }
    }

    private void InferenceModeOption_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInferenceModeUi();
    }

    private void ProviderMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProviderModeUi();
        RefreshProviderLimitRings();
    }

    private void UpdateInferenceModeUi()
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        var useProvider = GetInferenceModeSelectionTag() == "provider";
        if (AiProviderPanel != null)
        {
            AiProviderPanel.Visibility = useProvider ? Visibility.Visible : Visibility.Collapsed;
        }
        if (RemoteUrlPanel != null)
        {
            RemoteUrlPanel.Visibility = useProvider ? Visibility.Collapsed : Visibility.Visible;
        }
        if (AiModelPanel != null)
        {
            AiModelPanel.Visibility = useProvider ? Visibility.Collapsed : Visibility.Visible;
        }
        if (SingleProviderApiKeyPanel != null)
        {
            SingleProviderApiKeyPanel.Visibility = useProvider ? Visibility.Visible : Visibility.Collapsed;
        }
        if (SingleProviderTestPanel != null)
        {
            SingleProviderTestPanel.Visibility = useProvider ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TxtConnectionStatus != null)
        {
            TxtConnectionStatus.Text = string.Empty;
        }

        if (!useProvider)
        {
            vm.AiProvider = "custom";
            if (RemoteSettingsPanel != null)
            {
                RemoteSettingsPanel.Visibility = Visibility.Visible;
            }
            return;
        }

        if (vm.AiProvider == "custom")
        {
            vm.AiProvider = "openrouter";
            SetAiProviderSelection(vm.AiProvider);
        }

        if (RemoteSettingsPanel != null)
        {
            RemoteSettingsPanel.Visibility = Visibility.Visible;
        }
        UpdateProviderModeUi();
    }

    private void UpdateProviderModeUi()
    {
        var isProviderMode = GetInferenceModeSelectionTag() == "provider";
        var isMulti = GetProviderModeSelectionTag() == "multiple";
        if (SingleProviderPanel != null)
        {
            SingleProviderPanel.Visibility = isMulti ? Visibility.Collapsed : Visibility.Visible;
        }
        if (SingleProviderApiKeyPanel != null)
        {
            SingleProviderApiKeyPanel.Visibility = (isProviderMode && !isMulti) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (SingleProviderTestPanel != null)
        {
            SingleProviderTestPanel.Visibility = (isProviderMode && !isMulti) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (MultipleProvidersPanel != null)
        {
            MultipleProvidersPanel.Visibility = isMulti ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateProviderRateLimitIndicator();
    }

    private string GetInferenceModeSelectionTag()
    {
        if (CmbInferenceMode?.SelectedItem is ComboBoxItem item)
        {
            return (item.Tag as string ?? "provider").ToLowerInvariant();
        }

        return "provider";
    }

    private void SetInferenceModeSelection(string modeTag)
    {
        if (CmbInferenceMode == null)
        {
            return;
        }

        var wanted = (modeTag ?? "provider").ToLowerInvariant();
        foreach (var item in CmbInferenceMode.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                CmbInferenceMode.SelectedItem = item;
                return;
            }
        }

        CmbInferenceMode.SelectedIndex = 0;
    }

    private void SetProviderModeSelection(string modeTag)
    {
        if (CmbProviderMode == null)
        {
            return;
        }

        var wanted = (modeTag ?? "one").ToLowerInvariant();
        foreach (var item in CmbProviderMode.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                CmbProviderMode.SelectedItem = item;
                return;
            }
        }

        CmbProviderMode.SelectedIndex = 0;
    }

    private string GetProviderModeSelectionTag()
    {
        if (CmbProviderMode?.SelectedItem is ComboBoxItem item)
        {
            return (item.Tag as string ?? "one").ToLowerInvariant();
        }

        return "one";
    }

    private void ApplyMultiProviderSelections(string csv)
    {
        var set = new HashSet<string>((csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()));

        if (ChkCerebras != null) ChkCerebras.IsChecked = set.Contains("cerebras");
        if (ChkGroq != null) ChkGroq.IsChecked = set.Contains("groq");
        if (ChkGemini != null) ChkGemini.IsChecked = set.Contains("gemini");
        if (ChkGitHub != null) ChkGitHub.IsChecked = set.Contains("github");
        if (ChkMistral != null) ChkMistral.IsChecked = set.Contains("mistral");
    }

    private string GetSelectedMultiProvidersCsv()
    {
        var selected = new List<string>();
        if (ChkGroq?.IsChecked == true) selected.Add("groq");
        if (ChkCerebras?.IsChecked == true) selected.Add("cerebras");
        if (ChkGemini?.IsChecked == true) selected.Add("gemini");
        if (ChkMistral?.IsChecked == true) selected.Add("mistral");
        if (ChkGitHub?.IsChecked == true) selected.Add("github");
        return string.Join(",", selected);
    }

    private void RefreshProviderLimitRings()
    {
        // Placeholder values until runtime metrics are wired from API responses/rate-limit headers.
        SetRingProgress(RingCerebras, RingCerebrasLabel, 1.0);
        SetRingProgress(RingGroq, RingGroqLabel, 1.0);
        SetRingProgress(RingGemini, RingGeminiLabel, 1.0);
        SetRingProgress(RingGitHub, RingGitHubLabel, 1.0);
        SetRingProgress(RingMistral, RingMistralLabel, 1.0);
    }

    private void ProviderCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox)
        {
            return;
        }

        // When checking, update status indicator to show warning if no API key
        if (checkBox.IsChecked == true)
        {
            var (statusIndicator, apiKeyBox) = checkBox.Name switch
            {
                "ChkCerebras" => (StatusCerebras, TxtCerebrasApiKey),
                "ChkGroq" => (StatusGroq, TxtGroqApiKey),
                "ChkGemini" => (StatusGemini, TxtGeminiApiKey),
                "ChkGitHub" => (StatusGitHub, TxtGitHubApiKey),
                "ChkMistral" => (StatusMistral, TxtMistralApiKey),
                _ => (null, null)
            };

            if (statusIndicator != null && apiKeyBox != null)
            {
                if (string.IsNullOrWhiteSpace(apiKeyBox.Password))
                {
                    // Show yellow warning indicator
                    statusIndicator.Fill = System.Windows.Media.Brushes.Gold;
                    statusIndicator.Opacity = 1.0;
                    statusIndicator.ToolTip = "⚠ No API key configured - click Edit API to add";
                }
                else
                {
                    // Update to proper status
                    var provider = checkBox.Name?.Replace("Chk", "").ToLowerInvariant() ?? "";
                    UpdateProviderStatus(provider, statusIndicator, apiKeyBox.Password);
                }
            }
        }

        // When unchecking, hide the API key panel and reset indicator
        if (checkBox.IsChecked == false)
        {
            var (statusIndicator, panel, button) = checkBox.Name switch
            {
                "ChkCerebras" => (StatusCerebras, PanelCerebrasApiKey, FindEditButton("cerebras")),
                "ChkGroq" => (StatusGroq, PanelGroqApiKey, FindEditButton("groq")),
                "ChkGemini" => (StatusGemini, PanelGeminiApiKey, FindEditButton("gemini")),
                "ChkGitHub" => (StatusGitHub, PanelGitHubApiKey, FindEditButton("github")),
                "ChkMistral" => (StatusMistral, PanelMistralApiKey, FindEditButton("mistral")),
                _ => (null, null, null)
            };

            if (panel != null)
            {
                panel.Visibility = Visibility.Collapsed;
            }

            if (button != null)
            {
                // Reset button state to inactive (remove |active suffix)
                var provider = button.Tag?.ToString()?.Split('|')[0] ?? "";
                button.Tag = provider;
            }

            if (statusIndicator != null)
            {
                // Reset to gray/dimmed when unchecked
                statusIndicator.Fill = System.Windows.Media.Brushes.Gray;
                statusIndicator.Opacity = 0.3;
                statusIndicator.ToolTip = "Not selected";
            }
        }
    }

    private System.Windows.Controls.Button? FindEditButton(string provider)
    {
        // Find the Edit API button by traversing the visual tree
        return FindName($"BtnEdit{char.ToUpper(provider[0])}{provider.Substring(1)}") as System.Windows.Controls.Button
            ?? FindButtonByTag(this, provider);
    }

    private System.Windows.Controls.Button? FindButtonByTag(System.Windows.DependencyObject parent, string tag)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            
            if (child is System.Windows.Controls.Button button && 
                button.Tag?.ToString()?.StartsWith(tag) == true)
            {
                return button;
            }

            var result = FindButtonByTag(child, tag);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private void EditProviderApi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        // Extract provider name from Tag (handle both "provider" and "provider|active" formats)
        string provider = button.Tag?.ToString()?.Split('|')[0] ?? "";
        
        var panel = provider switch
        {
            "cerebras" => PanelCerebrasApiKey,
            "groq" => PanelGroqApiKey,
            "gemini" => PanelGeminiApiKey,
            "github" => PanelGitHubApiKey,
            "mistral" => PanelMistralApiKey,
            _ => null
        };

        if (panel != null)
        {
            // Toggle visibility for this specific provider only
            bool isNowVisible = panel.Visibility != Visibility.Visible;
            panel.Visibility = isNowVisible ? Visibility.Visible : Visibility.Collapsed;
            
            // Update button Tag to reflect active state
            button.Tag = isNowVisible ? $"{provider}|active" : provider;
        }
    }

    private async void TestProviderConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string provider)
        {
            return;
        }

        var (apiKeyBox, statusText, url) = provider switch
        {
            "cerebras" => (TxtCerebrasApiKey, TxtCerebrasStatus, "https://api.cerebras.ai/v1"),
            "groq" => (TxtGroqApiKey, TxtGroqStatus, "https://api.groq.com/openai/v1"),
            "gemini" => (TxtGeminiApiKey, TxtGeminiStatus, "https://generativelanguage.googleapis.com/v1beta/openai"),
            "github" => (TxtGitHubApiKey, TxtGitHubStatus, "https://models.github.ai/inference"),
            "mistral" => (TxtMistralApiKey, TxtMistralStatus, "https://api.mistral.ai/v1"),
            _ => (null, null, null)
        };

        if (apiKeyBox == null || statusText == null || url == null)
        {
            return;
        }

        var apiKey = ResolveProviderApiKeyForTest(provider, apiKeyBox.Password);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            statusText.Text = "Please enter an API key";
            statusText.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        button.IsEnabled = false;
        button.Content = "Testing API...";
        statusText.Text = "Testing API key...";
        statusText.Foreground = System.Windows.Media.Brushes.Gray;

        try
        {
            if (TryGetProviderCooldown(provider, out var remaining))
            {
                statusText.Text = $"⚠ Rate limited. Try again in {(int)Math.Ceiling(remaining.TotalSeconds)}s";
                statusText.Foreground = System.Windows.Media.Brushes.Goldenrod;
                UpdateProviderRateLimitIndicator();
                return;
            }

            var probe = await TestApiKeyAsync(provider, url, apiKey, _providerCooldownFallbackSeconds);
            if (probe.RateLimited)
            {
                MarkProviderRateLimited(provider, probe.RetryAfterSeconds);
            }

            // Mark provider as tested
            _testedProviders.Add(provider);

            statusText.Text = probe.Ok ? $"✓ {probe.Message}" : $"✗ {probe.Message}";
            statusText.Foreground = probe.RateLimited
                ? System.Windows.Media.Brushes.Goldenrod
                : (probe.Ok ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red);
            UpdateProviderRateLimitIndicator();
            UpdateProviderStatusIndicators();
        }
        catch (Exception ex)
        {
            statusText.Text = $"✗ Error: {ex.Message}";
            statusText.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = "Test Connection";
        }
    }

    private sealed record ApiProbeResult(bool Ok, bool RateLimited, int? RetryAfterSeconds, string Message);

    private async Task<ApiProbeResult> TestApiKeyAsync(string provider, string baseUrl, string apiKey, int fallbackCooldownSeconds)
    {
        var normalizedApiKey = NormalizeApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(normalizedApiKey))
        {
            return new(false, false, null, "Please enter an API key.");
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new(false, false, null, "Please enter a provider URL.");
        }

        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", normalizedApiKey);

        // Most configured providers in this UI are OpenAI-compatible endpoints.
        var candidates = BuildModelProbeUrls(baseUrl, provider);
        var attempted = new List<string>();
        foreach (var endpoint in candidates)
        {
            try
            {
                attempted.Add(endpoint);
                using var response = await client.GetAsync(endpoint);
                if (response.IsSuccessStatusCode)
                {
                    return new(true, false, null, "API key validated.");
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // Don't give up immediately - some providers restrict /models but allow /chat/completions.
                    // Continue to try other candidates and fall back to chat probe.
                    continue;
                }

                if ((int)response.StatusCode == 429)
                {
                    var retryAfter = GetRetryAfterSeconds(response) ?? fallbackCooldownSeconds;
                    return new(true, true, retryAfter, $"API reachable but rate-limited. Retry in {retryAfter}s.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync();
                var statusCode = (int)response.StatusCode;
                var snippet = string.IsNullOrWhiteSpace(body)
                    ? $"HTTP {statusCode}"
                    : $"HTTP {statusCode}: {body[..Math.Min(body.Length, 120)]}";
                return new(false, false, null, $"API test failed. {snippet}");
            }
            catch (TaskCanceledException)
            {
                return new(false, false, null, "API test timed out.");
            }
            catch (Exception ex)
            {
                return new(false, false, null, $"Network/API error: {ex.Message}");
            }
        }

        // Fallback probe: try an OpenAI-compatible chat completion request.
        var chatProbe = await TryOpenAiCompatibleChatProbeAsync(client, baseUrl, provider, fallbackCooldownSeconds);
        if (chatProbe.ok)
        {
            return new(chatProbe.ok, chatProbe.rateLimited, chatProbe.retryAfterSeconds, chatProbe.message);
        }

        var attempts = attempted.Count == 0 ? "(none)" : string.Join(" | ", attempted.Take(3));
        return new(false, false, null, $"{chatProbe.message} Tried: {attempts}");
    }

    private static List<string> BuildModelProbeUrls(string baseUrl, string provider)
    {
        var baseCandidates = GetProbeBaseCandidates(baseUrl, provider);
        if (baseCandidates.Count == 0)
        {
            return [];
        }

        var urls = new List<string>();
        void Add(string normalizedBase, string suffix)
        {
            var full = $"{normalizedBase}{suffix}";
            if (!urls.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(full);
            }
        }

        foreach (var normalizedBase in baseCandidates)
        {
            Add(normalizedBase, "/models");
            if (!normalizedBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
                !normalizedBase.EndsWith("/v1beta/openai", StringComparison.OrdinalIgnoreCase))
            {
                Add(normalizedBase, "/v1/models");
            }
        }

        return urls;
    }

    private static List<string> GetProbeBaseCandidates(string baseUrl, string provider)
    {
        var normalizedBase = NormalizeProbeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBase))
        {
            return [];
        }

        return [normalizedBase];
    }

    private static string NormalizeProbeBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var lowered = trimmed.ToLowerInvariant();
        var suffixes = new[]
        {
            "/chat/completions",
            "/completions",
            "/responses",
            "/generate",
            "/chat_stream",
            "/models"
        };

        foreach (var suffix in suffixes)
        {
            if (lowered.EndsWith(suffix, StringComparison.Ordinal))
            {
                return trimmed[..^suffix.Length];
            }
        }

        return trimmed;
    }

    private static async Task<(bool ok, bool rateLimited, int? retryAfterSeconds, string message)> TryOpenAiCompatibleChatProbeAsync(
        System.Net.Http.HttpClient client,
        string baseUrl,
        string provider,
        int fallbackCooldownSeconds)
    {
        var baseCandidates = GetProbeBaseCandidates(baseUrl, provider);
        if (baseCandidates.Count == 0)
        {
            return (false, false, null, $"Could not find a valid API probe endpoint for '{provider}'. Check provider URL format.");
        }

        var candidates = new List<string>();
        void Add(string url)
        {
            if (!candidates.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(url);
            }
        }

        foreach (var normalizedBase in baseCandidates)
        {
            Add($"{normalizedBase}/chat/completions");
            if (!normalizedBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
                !normalizedBase.EndsWith("/v1beta/openai", StringComparison.OrdinalIgnoreCase))
            {
                Add($"{normalizedBase}/v1/chat/completions");
            }
        }

        // Use provider-specific model for probe to avoid "model not found" errors
        var probeModel = (provider ?? "").ToLowerInvariant() switch
        {
            "gemini" => "gemini-1.5-flash",
            "openai" => "gpt-3.5-turbo",
            "anthropic" => "claude-3-haiku-20240307",
            "mistral" => "mistral-small-latest",
            "deepseek" => "deepseek-chat",
            _ => "gpt-3.5-turbo" // Generic fallback for OpenAI-compatible endpoints
        };

        var payload = new
        {
            model = probeModel,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
            temperature = 0.0
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        string? lastErrorBody = null;
        HttpStatusCode? lastStatusCode = null;
        
        foreach (var endpoint in candidates)
        {
            try
            {
                using var response = await client.PostAsync(
                    endpoint,
                    new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    return (true, false, null, "API key validated.");
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // Continue to try other endpoints - some providers have different auth per endpoint
                    lastStatusCode = response.StatusCode;
                    lastErrorBody = await response.Content.ReadAsStringAsync();
                    continue;
                }

                // 400/422 usually means request/model issue after auth passed.
                if ((int)response.StatusCode is 400 or 422)
                {
                    return (true, false, null, "API reachable (request format/model rejected, auth accepted).");
                }

                if ((int)response.StatusCode == 429)
                {
                    var retryAfter = GetRetryAfterSeconds(response) ?? fallbackCooldownSeconds;
                    return (true, true, retryAfter, $"API reachable but rate-limited. Retry in {retryAfter}s.");
                }
            }
            catch
            {
                // try next endpoint
            }
        }

        // If we got 401/403 on all endpoints, report auth failure with details
        if (lastStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var errorDetail = string.IsNullOrWhiteSpace(lastErrorBody) ? "" : $" Response: {lastErrorBody.Trim()}";
            return (false, false, null, $"API key rejected (401/403). Check key/project scope and ensure key is pasted without 'Bearer ' prefix.{errorDetail}");
        }

        return (false, false, null, $"Could not find a valid API probe endpoint for '{provider}'. Check provider URL format.");
    }

    private static string NormalizeApiKey(string? apiKey)
    {
        var value = (apiKey ?? string.Empty).Trim().Trim('"', '\'');
        const string bearerPrefix = "bearer ";
        if (value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[bearerPrefix.Length..].Trim();
        }

        return value;
    }

    private static string MaskApiKey(string? apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        // Show masked version: first 4 chars + *** + last 4 chars
        if (normalized.Length <= 8)
        {
            return "********";
        }

        return $"{normalized[..4]}{'*'.ToString().PadLeft(normalized.Length - 8, '*')}{normalized[^4..]}";
    }

    private void LoadMultiProviderApiKeys()
    {
        if (TxtCerebrasApiKey != null) TxtCerebrasApiKey.Password = MaskApiKey(Result.CerebrasApiKey);
        if (TxtGroqApiKey != null) TxtGroqApiKey.Password = MaskApiKey(Result.GroqApiKey);
        if (TxtGeminiApiKey != null) TxtGeminiApiKey.Password = MaskApiKey(Result.GeminiApiKey);
        if (TxtGitHubApiKey != null) TxtGitHubApiKey.Password = MaskApiKey(Result.GitHubApiKey);
        if (TxtMistralApiKey != null) TxtMistralApiKey.Password = MaskApiKey(Result.MistralApiKey);
        
        // Uncheck providers with no API key
        ValidateProviderApiKey("cerebras", Result.CerebrasApiKey, ChkCerebras);
        ValidateProviderApiKey("groq", Result.GroqApiKey, ChkGroq);
        ValidateProviderApiKey("gemini", Result.GeminiApiKey, ChkGemini);
        ValidateProviderApiKey("github", Result.GitHubApiKey, ChkGitHub);
        ValidateProviderApiKey("mistral", Result.MistralApiKey, ChkMistral);
    }

    private void ValidateProviderApiKey(string provider, string apiKey, System.Windows.Controls.CheckBox? checkbox)
    {
        if (checkbox == null)
        {
            return;
        }

        // Don't auto-uncheck, just let the indicator show the warning
        // The checkbox state is preserved from settings
    }

    private void UpdateProviderStatusIndicators()
    {
        UpdateProviderStatus("cerebras", StatusCerebras, Result.CerebrasApiKey, ChkCerebras);
        UpdateProviderStatus("groq", StatusGroq, Result.GroqApiKey, ChkGroq);
        UpdateProviderStatus("gemini", StatusGemini, Result.GeminiApiKey, ChkGemini);
        UpdateProviderStatus("github", StatusGitHub, Result.GitHubApiKey, ChkGitHub);
        UpdateProviderStatus("mistral", StatusMistral, Result.MistralApiKey, ChkMistral);
    }

    private void UpdateProviderStatus(string provider, Ellipse? statusIndicator, string apiKey, System.Windows.Controls.CheckBox? checkbox = null)
    {
        if (statusIndicator == null)
        {
            return;
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        var isChecked = checkbox?.IsChecked == true;
        var isTested = _testedProviders.Contains(provider);
        
        // If checked but no API key, show yellow warning
        if (isChecked && !hasApiKey)
        {
            statusIndicator.Fill = System.Windows.Media.Brushes.Gold;
            statusIndicator.Opacity = 1.0;
            statusIndicator.ToolTip = "⚠ No API key configured - click Edit API to add";
            return;
        }
        
        // If not checked, show gray/dimmed
        if (!isChecked)
        {
            statusIndicator.Fill = System.Windows.Media.Brushes.Gray;
            statusIndicator.Opacity = 0.3;
            statusIndicator.ToolTip = "Not selected";
            return;
        }

        // If no API key and not checked, hide or gray out the indicator
        if (!hasApiKey)
        {
            statusIndicator.Fill = System.Windows.Media.Brushes.Gray;
            statusIndicator.Opacity = 0.3;
            statusIndicator.ToolTip = "No API key configured";
            return;
        }

        // Reset opacity for active providers
        statusIndicator.Opacity = 1.0;
        
        // If has API key but not tested, show blue
        if (!isTested)
        {
            statusIndicator.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x9E, 0xFF));
            statusIndicator.ToolTip = "Not tested - click Test Connection to verify";
            return;
        }
        
        var isRateLimited = TryGetProviderCooldown(provider, out var remaining);
        
        if (isRateLimited)
        {
            statusIndicator.Fill = System.Windows.Media.Brushes.Red;
            statusIndicator.ToolTip = $"Rate limited. Retry in {(int)Math.Ceiling(remaining.TotalSeconds)}s";
        }
        else
        {
            statusIndicator.Fill = System.Windows.Media.Brushes.LimeGreen;
            statusIndicator.ToolTip = "Available";
        }
    }

    private static string GetUpdatedApiKey(string? inputValue, string existingValue)
    {
        var normalized = NormalizeApiKey(inputValue);
        
        // If empty, keep existing
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return existingValue;
        }

        // If it's the masked version, keep existing
        var masked = MaskApiKey(existingValue);
        if (normalized == masked)
        {
            return existingValue;
        }

        // Otherwise, it's a new value
        return normalized;
    }

    private async Task AutoValidateCheckedProvidersAsync()
    {
        var providers = GetCheckedProvidersForAutoValidation();
        if (providers.Count == 0)
        {
            return;
        }

        foreach (var provider in providers)
        {
            if (TryGetProviderCooldown(provider, out _))
            {
                continue;
            }

            var apiKey = NormalizeApiKey(GetStoredApiKeyForProvider(provider));
            var baseUrl = GetProviderBaseUrl(provider);
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl))
            {
                continue;
            }

            try
            {
                var probe = await TestApiKeyAsync(provider, baseUrl, apiKey, _providerCooldownFallbackSeconds);
                if (probe.RateLimited)
                {
                    MarkProviderRateLimited(provider, probe.RetryAfterSeconds);
                }

                if (probe.Ok)
                {
                    _testedProviders.Add(provider);
                }
                else
                {
                    _testedProviders.Remove(provider);
                }
            }
            catch
            {
                _testedProviders.Remove(provider);
            }
        }

        UpdateProviderRateLimitIndicator();
        UpdateProviderStatusIndicators();
    }

    private List<string> GetCheckedProvidersForAutoValidation()
    {
        var providers = new List<string>();
        if (ChkGroq?.IsChecked == true) providers.Add("groq");
        if (ChkCerebras?.IsChecked == true) providers.Add("cerebras");
        if (ChkGemini?.IsChecked == true) providers.Add("gemini");
        if (ChkMistral?.IsChecked == true) providers.Add("mistral");
        if (ChkGitHub?.IsChecked == true) providers.Add("github");
        return providers;
    }

    private string GetStoredApiKeyForProvider(string provider)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cerebras" => Result.CerebrasApiKey,
            "groq" => Result.GroqApiKey,
            "gemini" => Result.GeminiApiKey,
            "github" => Result.GitHubApiKey,
            "mistral" => Result.MistralApiKey,
            _ => string.Empty
        };
    }

    private static string GetProviderBaseUrl(string provider)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cerebras" => "https://api.cerebras.ai/v1",
            "groq" => "https://api.groq.com/openai/v1",
            "gemini" => "https://generativelanguage.googleapis.com/v1beta/openai",
            "github" => "https://models.github.ai/inference",
            "mistral" => "https://api.mistral.ai/v1",
            _ => string.Empty
        };
    }

    private string ResolveProviderApiKeyForTest(string provider, string? inputValue)
    {
        var rawInput = (inputValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return string.Empty;
        }

        var existingValue = (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cerebras" => Result.CerebrasApiKey,
            "groq" => Result.GroqApiKey,
            "gemini" => Result.GeminiApiKey,
            "github" => Result.GitHubApiKey,
            "mistral" => Result.MistralApiKey,
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(existingValue))
        {
            var maskedExisting = MaskApiKey(existingValue);
            if (string.Equals(rawInput, maskedExisting, StringComparison.Ordinal))
            {
                return NormalizeApiKey(existingValue);
            }
        }

        return NormalizeApiKey(rawInput);
    }

    private static int? GetRetryAfterSeconds(System.Net.Http.HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header == null)
        {
            return null;
        }

        if (header.Delta.HasValue)
        {
            var secs = (int)Math.Ceiling(header.Delta.Value.TotalSeconds);
            return secs > 0 ? secs : null;
        }

        if (header.Date.HasValue)
        {
            var delta = header.Date.Value - DateTimeOffset.UtcNow;
            var secs = (int)Math.Ceiling(delta.TotalSeconds);
            return secs > 0 ? secs : null;
        }

        return null;
    }

    private bool TryGetProviderCooldown(string provider, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        var key = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!_providerRateLimitedUntil.TryGetValue(key, out var until))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (until <= now)
        {
            _providerRateLimitedUntil.Remove(key);
            return false;
        }

        remaining = until - now;
        return true;
    }

    private void MarkProviderRateLimited(string provider, int? retryAfterSeconds)
    {
        var key = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var cooldown = retryAfterSeconds.GetValueOrDefault(_providerCooldownFallbackSeconds);
        if (cooldown <= 0)
        {
            cooldown = _providerCooldownFallbackSeconds;
        }

        _providerRateLimitedUntil[key] = DateTimeOffset.UtcNow.AddSeconds(cooldown);
    }

    private void UpdateProviderRateLimitIndicator()
    {
        if (TxtProviderRateLimitIndicator == null)
        {
            return;
        }

        var active = _providerRateLimitedUntil
            .Where(kvp => kvp.Value > DateTimeOffset.UtcNow)
            .OrderBy(kvp => kvp.Value)
            .ToList();

        if (active.Count == 0)
        {
            TxtProviderRateLimitIndicator.Visibility = Visibility.Collapsed;
            TxtProviderRateLimitIndicator.Text = string.Empty;
            if (TxtRateLimitStatus != null)
            {
                TxtRateLimitStatus.Text = "No active provider cooldowns.";
                TxtRateLimitStatus.Foreground = FindResource("TextMutedBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            }
            return;
        }

        var lines = active
            .Take(3)
            .Select(kvp =>
            {
                var secs = (int)Math.Ceiling((kvp.Value - DateTimeOffset.UtcNow).TotalSeconds);
                var provider = kvp.Key;
                return $"{provider}: {Math.Max(secs, 1)}s";
            });

        TxtProviderRateLimitIndicator.Text = $"Rate-limited providers: {string.Join(" | ", lines)}";
        TxtProviderRateLimitIndicator.Foreground = System.Windows.Media.Brushes.Goldenrod;
        TxtProviderRateLimitIndicator.Visibility = Visibility.Visible;
        if (TxtRateLimitStatus != null)
        {
            TxtRateLimitStatus.Text = TxtProviderRateLimitIndicator.Text;
            TxtRateLimitStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
        }
    }

    private static string EnsureProviderUrl(string provider, string? currentUrl)
    {
        var existing = (currentUrl ?? string.Empty).Trim();
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "kimi" => existing.Contains("api.moonshot.ai", StringComparison.OrdinalIgnoreCase) ||
                      existing.Contains("api.moonshot.cn", StringComparison.OrdinalIgnoreCase)
                ? existing
                : "https://api.moonshot.ai/v1",
            "cerebras" => "https://api.cerebras.ai/v1",
            "groq" => "https://api.groq.com/openai/v1",
            "gemini" => "https://generativelanguage.googleapis.com/v1beta/openai",
            "openrouter" => "https://openrouter.ai/api/v1",
            "openai" => "https://api.openai.com/v1",
            "cohere" => "https://api.cohere.com/v1",
            "mistral" => "https://api.mistral.ai/v1",
            "deepseek" => "https://api.deepseek.com/v1",
            "anthropic" => "https://api.anthropic.com/v1",
            // Keep manual endpoint only for custom mode (ngrok/local bridge).
            _ => existing
        };
    }

    private static string EnsureProviderModel(string provider, string? currentModel)
    {
        var model = (currentModel ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(model) && !string.Equals(model, "tsuki-lora", StringComparison.OrdinalIgnoreCase))
        {
            return model;
        }

        return GetProviderDefaultModel(provider);
    }

    private static string ResolveRuntimeApiKeyForProviderMode(
        string provider,
        bool useMultipleProviders,
        string? multiProvidersCsv,
        string cerebrasApiKey,
        string groqApiKey,
        string geminiApiKey,
        string gitHubApiKey,
        string mistralApiKey,
        string fallbackApiKey)
    {
        string selectedProvider;
        if (useMultipleProviders)
        {
            selectedProvider = (multiProvidersCsv ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim().ToLowerInvariant())
                .FirstOrDefault() ?? provider;
        }
        else
        {
            selectedProvider = provider;
        }

        var providerKey = GetProviderApiKey(selectedProvider, cerebrasApiKey, groqApiKey, geminiApiKey, gitHubApiKey, mistralApiKey);
        return string.IsNullOrWhiteSpace(providerKey) ? fallbackApiKey : providerKey;
    }

    private static string GetProviderApiKey(
        string provider,
        string cerebrasApiKey,
        string groqApiKey,
        string geminiApiKey,
        string gitHubApiKey,
        string mistralApiKey)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cerebras" => NormalizeApiKey(cerebrasApiKey),
            "groq" => NormalizeApiKey(groqApiKey),
            "gemini" => NormalizeApiKey(geminiApiKey),
            "github" => NormalizeApiKey(gitHubApiKey),
            "mistral" => NormalizeApiKey(mistralApiKey),
            _ => string.Empty
        };
    }

    private string GetProviderApiInput(string provider)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cerebras" => TxtCerebrasApiKey?.Password ?? string.Empty,
            "groq" => TxtGroqApiKey?.Password ?? string.Empty,
            "gemini" => TxtGeminiApiKey?.Password ?? string.Empty,
            "github" => TxtGitHubApiKey?.Password ?? string.Empty,
            "mistral" => TxtMistralApiKey?.Password ?? string.Empty,
            _ => TxtRemoteApiKey?.Password ?? string.Empty
        };
    }

    private static string GetProviderDefaultModel(string provider)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "kimi" => "moonshot-v1-8k",
            "cerebras" => "llama3.1-8b",
            "groq" => "llama-3.1-8b-instant",
            "gemini" => "gemini-1.5-flash",
            "openrouter" => "openai/gpt-4o-mini",
            "openai" => "gpt-4o-mini",
            "cohere" => "command-r-plus",
            "mistral" => "mistral-small-latest",
            "deepseek" => "deepseek-chat",
            "anthropic" => "claude-3-5-haiku-latest",
            "github" => "gpt-4o-mini",
            _ => "gpt-4o-mini"
        };
    }

    private static void SetRingProgress(Path? ringPath, TextBlock? label, double ratio)
    {
        if (ringPath == null || label == null)
        {
            return;
        }

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        label.Text = $"{Math.Round(ratio * 100):0}%";

        const double radius = 22.0;
        var center = new System.Windows.Point(28, 28);
        var start = new System.Windows.Point(center.X, center.Y - radius);

        if (ratio <= 0.0)
        {
            ringPath.Data = Geometry.Empty;
            return;
        }

        if (ratio >= 0.999)
        {
            ringPath.Data = new EllipseGeometry(center, radius, radius);
            return;
        }

        var angle = 360.0 * ratio - 90.0;
        var radians = angle * (Math.PI / 180.0);
        var end = new System.Windows.Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        var isLargeArc = ratio > 0.5;

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new System.Windows.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ringPath.Data = geometry;
    }

    private void RestartApplication()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                MessageBox.Show(this, "Unable to determine application path for restart.", "Restart Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });

            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to restart application: {ex.Message}", "Restart Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private int GetSelectedMicrophoneDeviceId()
    {
        if (CmbMicrophoneDevice?.SelectedItem is ComboBoxItem item && item.Tag is int deviceId)
            return deviceId;
        return -1;
    }

    private void TestMicrophone_Click(object sender, RoutedEventArgs e)
    {
        var selected = CmbMicrophoneDevice?.SelectedItem as ComboBoxItem;
        MessageBox.Show($"Selected microphone: {selected?.Content}", "Microphone Test", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private static string? FindDiscordBridgeEnvPath()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "discord-voice-bridge", ".env"),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "discord-voice-bridge", ".env")),
            System.IO.Path.Combine(AppContext.BaseDirectory, "discord-voice-bridge", ".env")
        };

        foreach (var candidate in candidates)
        {
            var bridgeDir = System.IO.Path.GetDirectoryName(candidate);
            if (!string.IsNullOrWhiteSpace(bridgeDir) && System.IO.Directory.Exists(bridgeDir))
                return candidate;
        }

        return null;
    }

    private sealed class SettingsVm
    {
        public bool IsActivityLoggingEnabled { get; set; }
        public string SampleIntervalMinutesText { get; set; } = "5";
        public string SummarizeIntervalMinutesText { get; set; } = "60";
        public string RetentionHoursForRawText { get; set; } = "1";
        public int CaptureModeIndex { get; set; }
        public string ModelName { get; set; } = "qwen2.5:3b";
        public bool AutoStartOllama { get; set; } = true;
        public bool StopOllamaOnExit { get; set; } = true;
        public bool StartupGreetingEnabled { get; set; } = true;
        public bool ProactiveMessagesEnabled { get; set; } = true;
        public string ProactiveMessageAfterMinutesText { get; set; } = "1";
        public string ProactiveMessageMaxMinutesText { get; set; } = "5";
        public bool UseGpu { get; set; } = true;
        public string ModelDirectory { get; set; } = string.Empty;
        public bool VoiceEnabled { get; set; }
        public string VoicevoxBaseUrl { get; set; } = "http://127.0.0.1:50021";
        public string CloudTtsUrl { get; set; } = string.Empty;
        public string VoicevoxSpeakerStyleIdText { get; set; } = "47";
        public bool VoiceTranslateToJapanese { get; set; } = true;
        public bool UseDeepLTranslate { get; set; }
        public bool UseDeepLFreeApi { get; set; } = true;
        public int VoiceOutputDeviceNumber { get; set; } = -1;
        public List<AudioDeviceItem> AudioDevices { get; set; } = new();
        public bool VoicePlayBeforeTypewriter { get; set; }
        public string TessdataDirectory { get; set; } = string.Empty;
        public string FiveMinuteSummariesText { get; set; } = string.Empty;
        public string HourlySummariesText { get; set; } = string.Empty;
        public string RemoteInferenceUrl { get; set; } = string.Empty;
        public string RemoteInferenceApiKey { get; set; } = string.Empty;
        public string AiProvider { get; set; } = "custom";
        public bool UseMultipleProviders { get; set; }
        public string MultiAiProvidersCsv { get; set; } = string.Empty;
        public bool AutoSwitchProviderOnRateLimit { get; set; } = true;
        public string ProviderRateLimitCooldownSecondsText { get; set; } = "60";
        public int VoiceChatInputDeviceNumber { get; set; } = -1;
        public int VoiceChatOutputDeviceNumber { get; set; } = -1;
        public List<AudioDeviceItem> VoiceChatInputDevices { get; set; } = new();
        public List<AudioDeviceItem> VoiceChatOutputDevices { get; set; } = new();
        public int DiscordTranslationStrategyIndex { get; set; }
        public bool UseMicrophoneInput { get; set; }
        public bool MicrophonePushToTalk { get; set; }
        public string VoiceReceptionToggleKeyText { get; set; } = "F8";
    }

    private static string InferProviderFromUrl(string? url)
    {
        var lowered = (url ?? string.Empty).Trim().ToLowerInvariant();
        if (lowered.Contains("api.moonshot.ai") || lowered.Contains("api.moonshot.cn")) return "kimi";
        if (lowered.Contains("cerebras.ai")) return "cerebras";
        if (lowered.Contains("api.groq.com")) return "groq";
        if (lowered.Contains("generativelanguage.googleapis.com")) return "gemini";
        if (lowered.Contains("openrouter.ai")) return "openrouter";
        if (lowered.Contains("api.openai.com")) return "openai";
        if (lowered.Contains("api.cohere.com")) return "cohere";
        if (lowered.Contains("api.mistral.ai")) return "mistral";
        if (lowered.Contains("api.deepseek.com")) return "deepseek";
        if (lowered.Contains("anthropic.com")) return "anthropic";
        return "custom";
    }

    private static bool IsKnownProviderUrl(string url)
    {
        var lowered = url.Trim().ToLowerInvariant();
        return lowered.Contains("api.moonshot.ai")
            || lowered.Contains("api.moonshot.cn")
            || lowered.Contains("cerebras.ai")
            || lowered.Contains("api.groq.com")
            || lowered.Contains("generativelanguage.googleapis.com")
            || lowered.Contains("openrouter.ai")
            || lowered.Contains("api.openai.com")
            || lowered.Contains("api.cohere.com")
            || lowered.Contains("api.mistral.ai")
            || lowered.Contains("api.deepseek.com")
            || lowered.Contains("anthropic.com");
    }

    private void SetAiProviderSelection(string provider)
    {
        if (CmbAiProvider == null)
        {
            return;
        }

        var wanted = (provider ?? "custom").ToLowerInvariant();
        foreach (var item in CmbAiProvider.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                CmbAiProvider.SelectedItem = item;
                return;
            }
        }

        CmbAiProvider.SelectedIndex = 0;
    }

    private sealed class AudioDeviceItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private static int ParseIntOr(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse((text ?? string.Empty).Trim(), out var value))
        {
            value = fallback;
        }

        if (value < min)
        {
            value = min;
        }

        if (value > max)
        {
            value = max;
        }

        return value;
    }

    private static string NormalizeCloudTtsUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return value.TrimEnd('/');
    }

    private static string BuildRemoteTtsErrorDetails(string baseUrl, HttpStatusCode statusCode, string? reason, string? body, string step)
    {
        var snippet = string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : body[..Math.Min(body.Length, 220)];

        var details = $"Connection failed at {step}.\nURL: {baseUrl}\nStatus: {(int)statusCode} ({reason})";
        if (statusCode == HttpStatusCode.BadGateway)
        {
            details += "\n\nngrok returned 502 (Bad Gateway). Tunnel is up, but backend service is not reachable.";
            details += "\nCheck that VOICEVOX is running in Colab and ngrok points to port 50021.";
        }

        if (!string.IsNullOrWhiteSpace(snippet))
        {
            details += $"\n\nResponse: {snippet}";
        }

        return details;
    }

    private static string NormalizeKeyNameOr(string? input, string? fallback, string defaultValue)
    {
        var raw = (input ?? string.Empty).Trim();
        if (TryNormalizeKeyName(raw, out var normalized))
        {
            return normalized;
        }

        var fallbackRaw = (fallback ?? string.Empty).Trim();
        if (TryNormalizeKeyName(fallbackRaw, out normalized))
        {
            return normalized;
        }

        return defaultValue;
    }

    private static bool TryNormalizeKeyName(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Enum.TryParse<System.Windows.Input.Key>(raw.Trim(), true, out var key) ||
            key == System.Windows.Input.Key.None)
        {
            return false;
        }

        normalized = key.ToString();
        return true;
    }
}


// Converter for checking if a string contains a substring
public class StringContainsConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string str && parameter is string substring)
        {
            return str.Contains(substring);
        }
        return false;
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new System.NotImplementedException();
    }
}
