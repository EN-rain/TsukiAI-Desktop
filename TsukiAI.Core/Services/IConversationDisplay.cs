namespace TsukiAI.Core.Services;

/// <summary>
/// Interface for conversation formatting and display services.
/// Handles text sanitization, normalization, and display formatting.
/// </summary>
public interface IConversationDisplay
{
    /// <summary>
    /// Sanitizes assistant text by removing template tags, fixing addressing, and cleaning TTS artifacts.
    /// </summary>
    /// <param name="text">The raw assistant text to sanitize.</param>
    /// <returns>The sanitized text with template tags and artifacts removed.</returns>
    string SanitizeAssistantText(string text);
    
    /// <summary>
    /// Fixes user addressing in text (e.g., "User" -> actual user name).
    /// </summary>
    /// <param name="text">The text containing user addressing to fix.</param>
    /// <returns>The text with corrected user addressing.</returns>
    string FixUserAddressing(string text);
    
    /// <summary>
    /// Normalizes reply text to prevent repetition and ensure quality.
    /// </summary>
    /// <param name="reply">The reply text to normalize.</param>
    /// <returns>The normalized reply text.</returns>
    string NormalizeReply(string reply);
    
    /// <summary>
    /// Builds conversation text with proper role formatting and OCR context injection.
    /// </summary>
    /// <param name="history">The conversation history as a list of role-content tuples.</param>
    /// <param name="ocrContext">Optional OCR context to inject into the conversation.</param>
    /// <returns>The formatted conversation text.</returns>
    string BuildConversationText(
        IReadOnlyList<(string role, string content)> history,
        string? ocrContext = null);
    
    /// <summary>
    /// Builds OCR context block for injection into conversation.
    /// </summary>
    /// <param name="result">The OCR result to format, or null if no OCR data is available.</param>
    /// <returns>The formatted OCR context block, or an empty string if result is null.</returns>
    string BuildOcrContextBlock(object? result);
    
    /// <summary>
    /// Ensures text is non-repeating by checking against previous content.
    /// </summary>
    /// <param name="reply">The reply text to check for repetition.</param>
    /// <param name="previousContent">Optional previous content to compare against.</param>
    /// <returns>The original reply if non-repeating, or a modified version if repetition is detected.</returns>
    string EnsureNonRepeating(string reply, string? previousContent = null);
}
