namespace TsukiAI.Core.Models;

/// <summary>
/// Defines the interaction mode for the TsukiAI interface.
/// </summary>
public enum InteractionMode
{
    /// <summary>
    /// Text chat mode - User can input text, LLM responds with text + TTS
    /// </summary>
    Chat,

    /// <summary>
    /// Voice chat mode - Supports both Discord voice (STT -> LLM -> TTS) and local TTS testing
    /// Input can be enabled/disabled based on user preference
    /// </summary>
    VoiceChat
}
