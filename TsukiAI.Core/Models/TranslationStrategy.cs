namespace TsukiAI.Core.Models;

/// <summary>
/// Defines the translation strategy for Discord voice conversations.
/// </summary>
public enum TranslationStrategy
{
    /// <summary>
    /// Strategy A: Translate user input to Japanese, generate LLM response in Japanese, output via VoiceVox.
    /// </summary>
    TranslateInputToJapanese = 0,
    
    /// <summary>
    /// Strategy B: Generate LLM response in English, translate response to Japanese, output via VoiceVox.
    /// </summary>
    TranslateResponseToJapanese = 1,
    
    /// <summary>
    /// Strategy C: Generate LLM response in the same language as detected input without translation.
    /// </summary>
    BilingualNoTranslation = 2
}
