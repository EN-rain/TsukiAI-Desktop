namespace TsukiAI.Core.Models;

/// <summary>
/// Defines the translation strategy for Discord voice conversations.
/// </summary>
public enum TranslationStrategy
{
    /// <summary>
    /// Strategy A: Translate user input to Japanese, generate the LLM response in Japanese, then synthesize it.
    /// </summary>
    TranslateInputToJapanese = 0,
    
    /// <summary>
    /// Strategy B: Generate the LLM response in English, translate the response to Japanese, then synthesize it.
    /// </summary>
    TranslateResponseToJapanese = 1,
    
    /// <summary>
    /// Strategy C: Generate LLM response in the same language as detected input without translation.
    /// </summary>
    BilingualNoTranslation = 2
}
