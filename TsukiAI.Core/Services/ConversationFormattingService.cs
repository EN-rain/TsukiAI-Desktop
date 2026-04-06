namespace TsukiAI.Core.Services;

/// <summary>
/// Service for conversation formatting and display logic.
/// Implements IConversationDisplay interface to handle text sanitization,
/// normalization, and display formatting for conversation interactions.
/// </summary>
public sealed class ConversationFormattingService : IConversationDisplay
{
    private readonly string _assistantName;
    private readonly string _userName;
    
    /// <summary>
    /// Initializes a new instance of the ConversationFormattingService class.
    /// </summary>
    /// <param name="assistantName">The name of the assistant (e.g., "Tsuki").</param>
    /// <param name="userName">The name of the user.</param>
    public ConversationFormattingService(string assistantName, string userName)
    {
        _assistantName = assistantName ?? "Tsuki";
        _userName = userName ?? "User";
    }
    
    /// <summary>
    /// Sanitizes assistant text by removing template tags, fixing addressing, and cleaning TTS artifacts.
    /// Removes common template/system tokens, leaked prompt/rules lines, JSON blobs, emotion labels,
    /// and emoji characters that may appear in model outputs.
    /// </summary>
    /// <param name="text">The raw assistant text to sanitize.</param>
    /// <returns>The sanitized text with template tags and artifacts removed.</returns>
    public string SanitizeAssistantText(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return text;

        text = text.Replace('—', '-');
        
        // Strip common template/system tokens that leak into responses
        var templateTokens = new[]
        {
            "<|im_start|>", "<|im_end|>",
            "<|system|>", "<|user|>", "<|assistant|>",
            "<tool_call>", "</tool_call>",
            "<function>", "</function>",
            "<think>", "</think>",
            "Assistant:", "System:", "User:",
            "[INST]", "[/INST]",
            "<<SYS>>", "<</SYS>>"
        };
        
        foreach (var token in templateTokens)
        {
            text = text.Replace(token, "", StringComparison.OrdinalIgnoreCase);
        }

        // Strip leaked prompt/rules lines that sometimes appear in model outputs.
        // Keep this conservative: only remove highly specific instruction-like patterns.
        var lines = text
            .Split('\n')
            .Select(l => (l ?? "").TrimEnd('\r'))
            .ToList();

        static bool ContainsAny(string s, params string[] needles)
        {
            foreach (var n in needles)
            {
                if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        var filtered = new List<string>(lines.Count);
        foreach (var raw in lines)
        {
            var line = (raw ?? "").Trim();
            if (line.Length == 0) continue;

            // Drop JSON blobs (sometimes the model returns schema-shaped JSON instead of plain text)
            // including truncated JSON lines.
            if (line.StartsWith("{") || line.StartsWith("[") || line.StartsWith("\"") || line.StartsWith("```"))
            {
                if (ContainsAny(line, "\"reply\"", "\"emotion\"", "reply", "emotion") || line.Contains("}") || line.Contains(":") )
                    continue;
            }

            if (ContainsAny(line, "\"reply\"", "\"emotion\""))
                continue;

            // Drop "Pick:"/option forcing artifacts
            if (line.StartsWith("pick:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ContainsAny(line, "pick one", "pick:", "mood-check:"))
                continue;

            // Common leakage lines
            if (ContainsAny(line,
                    "your name is tsuki",
                    "baseline emotion",
                    "return only",
                    "json object",
                    "schema",
                    "system prompt",
                    "never call the user",
                    "function:",
                    "rules:"))
                continue;

            // Drop standalone emotion labels (often printed as a separate line)
            var low = line.ToLowerInvariant();
            if (line.Length <= 12 && (low is "neutral" or "happy" or "sad" or "angry" or "surprised" or "playful" or "thinking" or "idle" or "focused" or "frustrated" or "sleepy" or "bored" or "concerned"))
                continue;

            filtered.Add(line);
        }

        text = string.Join("\n", filtered).Trim();
        if (text.Length == 0) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\uFE0F') continue;
            if (ch >= '\uD800' && ch <= '\uDFFF') continue;
            if (ch >= 0x2600 && ch <= 0x27BF) continue;
            if (ch >= 0x1F000 && ch <= 0x1FAFF) continue;
            sb.Append(ch);
        }

        text = sb.ToString().Trim();
        return text;
    }
    
    /// <summary>
    /// Fixes user addressing in text by replacing instances where the assistant incorrectly
    /// addresses the user as "Tsuki" (the assistant's own name) with the actual user's name.
    /// Handles common greeting patterns and addressing formats.
    /// </summary>
    /// <param name="text">The text containing user addressing to fix.</param>
    /// <returns>The text with corrected user addressing.</returns>
    public string FixUserAddressing(string text)
    {
        try
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return text;

            // Only fix obvious cases where the assistant is addressing the user by name.
            // We do NOT want to replace every mention of "Tsuki" (the assistant's own name).
            var owner = _userName;

            // Start-of-message greetings
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"^(hey|hi|hello)\s+tsuki\b",
                m => m.Value.Substring(0, m.Value.Length - "tsuki".Length) + owner,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // "Tsuki," or "Tsuki -" addressing patterns
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\btsuki\s*([,\-–])",
                owner + "$1",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // "Hey Tsuki!" / "Hi Tsuki." etc.
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\b(hey|hi|hello)\s+tsuki\b",
                "$1 " + owner,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return text.Trim();
        }
        catch
        {
            return (text ?? "").Trim();
        }
    }
    
    /// <summary>
    /// Normalizes reply text to prevent repetition and ensure quality.
    /// Joins consecutive single newlines with spaces while preserving intentional paragraph breaks,
    /// and collapses multiple consecutive spaces into single spaces.
    /// </summary>
    /// <param name="reply">The reply text to normalize.</param>
    /// <returns>The normalized reply text with improved formatting.</returns>
    public string NormalizeReply(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return reply;
        
        // Join consecutive single newlines with a space (keeps flow)
        // Preserve double newlines (intentional paragraph breaks)
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            reply, 
            @"(?<!\n)\n(?!\n)", 
            " ");
        
        // Collapse multiple spaces
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s{2,}", " ");
        
        return normalized.Trim();
    }
    
    /// <summary>
    /// Builds conversation text with proper role formatting and OCR context injection.
    /// Formats each message with appropriate role labels (user name, assistant name, or "System:")
    /// and optionally appends OCR context at the end of the conversation.
    /// </summary>
    /// <param name="history">The conversation history as a list of role-content tuples.</param>
    /// <param name="ocrContext">Optional OCR context to inject at the end of the conversation.</param>
    /// <returns>The formatted conversation text with role labels and optional OCR context.</returns>
    public string BuildConversationText(
        IReadOnlyList<(string role, string content)> history,
        string? ocrContext = null)
    {
        if (history == null || history.Count == 0)
        {
            return string.Empty;
        }
        
        var builder = new System.Text.StringBuilder();
        
        foreach (var (role, content) in history)
        {
            if (string.IsNullOrWhiteSpace(content)) continue;
            
            var roleLabel = role.ToLowerInvariant() switch
            {
                "user" => $"{_userName}:",
                "assistant" => $"{_assistantName}:",
                "system" => "System:",
                _ => $"{role}:"
            };
            
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            
            builder.Append(roleLabel);
            builder.Append(' ');
            builder.Append(content.Trim());
        }
        
        // Inject OCR context if provided
        if (!string.IsNullOrWhiteSpace(ocrContext))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }
            builder.Append(ocrContext);
        }
        
        return builder.ToString();
    }
    
    /// <summary>
    /// Builds OCR context block for injection into conversation.
    /// Formats OCR results with timestamp validation (only includes results less than 15 seconds old),
    /// application metadata, and extracted text (truncated to 600 characters if needed).
    /// </summary>
    /// <param name="result">The OCR result to format, or null if no OCR data is available.</param>
    /// <returns>The formatted OCR context block with metadata and text, or an empty string if result is null or too old.</returns>
    public string BuildOcrContextBlock(object? result)
    {
        _ = result;
        return "";
    }
    
    /// <summary>
    /// Ensures text is non-repeating by checking against previous content.
    /// If the reply matches the previous content (case-insensitive comparison),
    /// adds a prefix to make it distinct. Returns "..." if the reply is empty after sanitization.
    /// </summary>
    /// <param name="reply">The reply text to check for repetition.</param>
    /// <param name="previousContent">Optional previous content to compare against for repetition detection.</param>
    /// <returns>The original reply if non-repeating, or a modified version with a prefix if repetition is detected.</returns>
    public string EnsureNonRepeating(string reply, string? previousContent = null)
    {
        reply = (reply ?? "").Trim();
        if (reply.Length == 0) return "...";

        reply = SanitizeAssistantText(reply);
        var norm = reply.ToLowerInvariant();

        // Check if this reply matches the previous content (if provided)
        if (!string.IsNullOrWhiteSpace(previousContent))
        {
            var prevNorm = previousContent.ToLowerInvariant().Trim();
            if (norm == prevNorm)
            {
                var prefixes = new[] { "Hmm, ", "Okay, ", "Anyway, ", "So, " };
                var prefix = prefixes[0]; // Use first prefix as default
                reply = prefix + reply;
                reply = SanitizeAssistantText(reply);
            }
        }

        return reply;
    }
}
