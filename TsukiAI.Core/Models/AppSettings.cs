namespace TsukiAI.Core.Models;

public enum ScreenshotCaptureMode
{
    FullScreen = 0,
    ActiveWindow = 1,
}

public enum InferenceMode
{
    LocalNative = 0,
    LocalOllama = 1,
    RemoteColab = 2
}

public enum VoiceChatMode
{
    BotVoiceChat = 0,
    SelfVoiceChat = 1
}

public enum VoiceIntegrationPlatform
{
    Discord = 0,
    VrChat = 1,
    Other = 2
}

public enum SttMode
{
    CloudAssemblyAI = 0,
    CloudGroqWhisper = 1
}

public enum TtsMode
{
    LocalVoiceVox = 0,
    CloudRemote = 1
}

public sealed record AppSettings(
    // Mode Selection (NEW)
    InteractionMode EnabledMode = InteractionMode.Chat,
    
    // Shared Settings
    InferenceMode InferenceMode = InferenceMode.LocalNative,
    string ModelName = "tsuki-lora",
    string ModelDirectory = "",
    bool AutoStartOllama = true,
    bool StopOllamaOnExit = true,
    bool UseGpu = true,
    string RemoteInferenceUrl = "",
    string RemoteInferenceApiKey = "",
    bool UseMultipleAiProviders = false,
    string MultiAiProvidersCsv = "",
    
    // Multi-provider API keys
    string CerebrasApiKey = "",
    string GroqApiKey = "",
    string GeminiApiKey = "",
    string GitHubApiKey = "",
    string MistralApiKey = "",
    
    // Voice (VOICEVOX) - Shared
    bool VoiceEnabled = false,
    TtsMode TtsMode = TtsMode.LocalVoiceVox,
    string VoicevoxBaseUrl = "http://127.0.0.1:50021",
    string VoicevoxEnginePath = @"voicevox_engine\run.exe",
    int VoicevoxSpeakerStyleId = 47,
    string CloudTtsUrl = "",
    bool VoiceTranslateToJapanese = true,
    bool UseDeepLTranslate = false,
    string DeepLApiKey = "",
    bool UseDeepLFreeApi = true,
    int VoiceOutputDeviceNumber = -1,
    bool VoicePlayBeforeTypewriter = false,
    bool VoiceRuntimeV2Enabled = false,
    bool VoiceApiControllerEnabled = false,
    bool VoiceBargeInEnabled = false,
    
    // Chat Mode Specific Settings
    bool IsActivityLoggingEnabled = false,
    int SampleIntervalMinutes = 5,
    ScreenshotCaptureMode CaptureMode = ScreenshotCaptureMode.FullScreen,
    string TessdataDirectory = "",
    bool StartupGreetingEnabled = true,
    bool ProactiveMessagesEnabled = true,
    int ProactiveMessageAfterMinutes = 1,
    int ProactiveMessageMaxMinutes = 5,
    
    // Voice Chat Mode Specific Settings
    string DiscordBotToken = "",
    string AssemblyAIApiKey = "",
    SttMode SttMode = SttMode.CloudAssemblyAI,
    string SttLanguageCode = "auto",
    TranslationStrategy DiscordTranslationStrategy = TranslationStrategy.TranslateInputToJapanese,
    ulong DiscordDefaultGuildId = 0,
    ulong DiscordDefaultChannelId = 0,
    VoiceChatMode VoiceChatMode = VoiceChatMode.BotVoiceChat,
    VoiceIntegrationPlatform VoicePlatform = VoiceIntegrationPlatform.Discord,
    int VoiceChatInputDeviceNumber = -1,
    int VoiceChatOutputDeviceNumber = -1,
    bool SttDeepLEnabled = false,
    bool TtsDeepLEnabled = false,
    ulong DiscordFocusedUserId = ulong.MaxValue,  // 0 = all users, ulong.MaxValue = auto focus, otherwise specific user ID
    bool VoiceTextReceptionEnabled = true,  // Toggle for enabling/disabling text reception in voice mode
    string VoiceReceptionToggleKey = "F8",  // Hotkey to toggle voice reception on/off
    
    // Local Microphone Settings
    bool UseMicrophoneInput = false,  // Toggle between Discord and local microphone
    int MicrophoneDeviceId = -1,  // -1 = default device
    bool MicrophonePushToTalk = false,  // If true, only record when hotkey is pressed
    string MicrophonePushToTalkKey = "LeftCtrl",  // Hotkey for push-to-talk
    string VrChatOscHost = "127.0.0.1",
    int VrChatOscInputPort = 9000,
    int VrChatOscOutputPort = 9001,
    bool VrChatUseChatboxFallback = false,
    
    // Semantic Memory
    bool SemanticMemoryEnabled = true,
    int InferenceTimeoutSeconds = 60,
    int ModelLoadTimeoutSeconds = 120,
    int HealthCheckTimeoutSeconds = 10,
    int MaxInferenceRetries = 3,
    bool EnableInferenceRetries = true,
    string ReplyTonePreset = "natural",

    // Generation Tuning
    int GenerationMaxTokens = 80,
    float GenerationTemperature = 0.7f,
    float GenerationTopP = 0.9f,
    int GenerationTopK = 40,
    float GenerationRepeatPenalty = 1.08f,
    float GenerationPresencePenalty = 0.0f,
    float GenerationFrequencyPenalty = 0.0f,
    int GenerationMaxReplyChars = 360
)
{
    public static AppSettings Default => new();
    
    /// <summary>
    /// Creates a TimeoutConfiguration from the current settings.
    /// </summary>
    public TimeoutConfiguration GetTimeoutConfiguration() => new()
    {
        InferenceTimeout = TimeSpan.FromSeconds(InferenceTimeoutSeconds),
        ModelLoadTimeout = TimeSpan.FromSeconds(ModelLoadTimeoutSeconds),
        HealthCheckTimeout = TimeSpan.FromSeconds(HealthCheckTimeoutSeconds),
        MaxRetries = MaxInferenceRetries,
        InitialRetryDelay = TimeSpan.FromSeconds(1),
        EnableRetries = EnableInferenceRetries
    };

    public GenerationTuningSettings GetGenerationTuning() => new GenerationTuningSettings(
        GenerationMaxTokens,
        GenerationTemperature,
        GenerationTopP,
        GenerationTopK,
        GenerationRepeatPenalty,
        GenerationPresencePenalty,
        GenerationFrequencyPenalty,
        GenerationMaxReplyChars
    ).Clamp();
}
