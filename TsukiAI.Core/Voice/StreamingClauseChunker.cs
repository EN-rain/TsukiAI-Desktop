namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Incrementally turns cumulative LLM partial text into stable TTS clauses.
/// The chunker has no cancellation or pipeline dependencies and preserves the
/// original text inside each emitted clause, trimming only boundary whitespace.
/// </summary>
public sealed class StreamingClauseChunker
{
    public const int FirstClauseMinimumSpokenWords = 6;
    public const int FirstClauseTargetMaximumSpokenWords = 12;
    public const int LaterClauseMinimumSpokenWords = 12;
    public const int LaterClauseTargetMaximumSpokenWords = 25;

    private static readonly HashSet<string> CommonAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc",
        "e.g", "i.e", "a.m", "p.m", "u.s", "u.k", "no", "fig"
    };

    private string _lastPartial = string.Empty;
    private string _buffer = string.Empty;
    private int _emittedCount;

    /// <summary>
    /// Accepts the latest cumulative partial reply from the LLM and returns any
    /// newly stable clauses. Repeated snapshots are harmless. A snapshot that
    /// rewrites earlier text is rejected because already emitted speech cannot
    /// be deterministically revised.
    /// </summary>
    public IReadOnlyList<string> AppendPartial(string? partialText)
    {
        partialText ??= string.Empty;

        if (!partialText.StartsWith(_lastPartial, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Streaming partial text must extend the previously accepted partial without rewriting it.");
        }

        if (partialText.Length == _lastPartial.Length)
            return Array.Empty<string>();

        _buffer += partialText[_lastPartial.Length..];
        _lastPartial = partialText;
        return DrainStableClauses(allowBoundaryAtEnd: false);
    }

    /// <summary>
    /// Emits all remaining text at end of stream and resets the chunker so the
    /// same instance can be reused for a later response.
    /// </summary>
    public IReadOnlyList<string> Flush()
    {
        var emitted = DrainStableClauses(allowBoundaryAtEnd: true).ToList();
        var remainder = _buffer.Trim();
        if (remainder.Length > 0)
            emitted.Add(remainder);

        Reset();
        return emitted;
    }

    public void Reset()
    {
        _lastPartial = string.Empty;
        _buffer = string.Empty;
        _emittedCount = 0;
    }

    private IReadOnlyList<string> DrainStableClauses(bool allowBoundaryAtEnd)
    {
        var emitted = new List<string>();

        while (TryFindClauseBoundary(_buffer, allowBoundaryAtEnd, out var endExclusive))
        {
            var clause = _buffer[..endExclusive].Trim();
            _buffer = _buffer[endExclusive..].TrimStart();

            if (clause.Length == 0)
                continue;

            emitted.Add(clause);
            _emittedCount++;
        }

        return emitted;
    }

    private bool TryFindClauseBoundary(string text, bool allowBoundaryAtEnd, out int endExclusive)
    {
        endExclusive = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var minimum = _emittedCount == 0
            ? FirstClauseMinimumSpokenWords
            : LaterClauseMinimumSpokenWords;
        var targetMaximum = _emittedCount == 0
            ? FirstClauseTargetMaximumSpokenWords
            : LaterClauseTargetMaximumSpokenWords;

        BoundaryCandidate? nearMinimumFallback = null;

        for (var index = 0; index < text.Length; index++)
        {
            if (!TryGetBoundaryEnd(text, index, allowBoundaryAtEnd, out var candidateEnd))
                continue;

            var spokenWords = CountSpokenWords(text.AsSpan(0, candidateEnd));
            var candidate = new BoundaryCandidate(candidateEnd, spokenWords);

            if (spokenWords < minimum)
            {
                // Keep a close punctuation boundary available if waiting for the
                // next one would overshoot the target by a large margin.
                if (spokenWords >= Math.Max(2, minimum - 2))
                    nearMinimumFallback = candidate;
                index = candidateEnd - 1;
                continue;
            }

            if (spokenWords <= targetMaximum)
            {
                endExclusive = candidateEnd;
                return true;
            }

            endExclusive = nearMinimumFallback?.EndExclusive ?? candidateEnd;
            return true;
        }

        return false;
    }

    private static bool TryGetBoundaryEnd(
        string text,
        int index,
        bool allowBoundaryAtEnd,
        out int endExclusive)
    {
        endExclusive = 0;
        var current = text[index];
        if (!IsBoundaryPunctuation(current))
            return false;

        if (current == '.' && !IsConservativeSentencePeriod(text, index))
            return false;

        if (IsNumericInternalPunctuation(text, index, current))
            return false;

        var end = index + 1;
        while (end < text.Length && IsCompatibleTrailingPunctuation(text[end]))
            end++;
        while (end < text.Length && IsClosingDelimiter(text[end]))
            end++;

        if (!allowBoundaryAtEnd && end == text.Length)
            return false;

        endExclusive = end;
        return true;
    }

    private static bool IsBoundaryPunctuation(char value) => value is
        '.' or '!' or '?' or ',' or ';' or ':' or
        '。' or '！' or '？' or '，' or '、' or '；' or '：';

    private static bool IsCompatibleTrailingPunctuation(char value) => value is
        '.' or '!' or '?' or '。' or '！' or '？';

    private static bool IsClosingDelimiter(char value) => value is
        '"' or '\'' or '’' or '”' or ')' or ']' or '}' or '」' or '』' or '】' or '）';

    private static bool IsNumericInternalPunctuation(string text, int index, char punctuation)
    {
        if (punctuation is not ('.' or ',' or ':'))
            return false;
        if (index == 0 || index + 1 >= text.Length)
            return false;

        return char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]);
    }

    private static bool IsConservativeSentencePeriod(string text, int index)
    {
        if (index > 0 && text[index - 1] == '.')
        {
            // The last dot of an ellipsis may end a clause; earlier dots may not.
            return index + 1 >= text.Length || text[index + 1] != '.';
        }

        if (index + 1 < text.Length && text[index + 1] == '.')
            return false;

        if (index + 1 < text.Length &&
            !char.IsWhiteSpace(text[index + 1]) &&
            !IsClosingDelimiter(text[index + 1]))
        {
            // Covers decimal/version fragments and dotted names/URLs while the
            // next character is already available.
            return false;
        }

        var tokenStart = index - 1;
        while (tokenStart >= 0 && (char.IsLetter(text[tokenStart]) || text[tokenStart] == '.'))
            tokenStart--;
        tokenStart++;

        var token = text[tokenStart..index].Trim('.');
        if (token.Length == 0)
            return true;

        if (CommonAbbreviations.Contains(token))
            return false;

        if (token.Length == 1 && char.IsLetter(token[0]))
            return false;

        if (token.Contains('.') && IsDottedAbbreviation(token))
            return false;

        return true;
    }

    private static bool IsDottedAbbreviation(string token)
    {
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts.All(part =>
            part.Length is >= 1 and <= 3 && part.All(char.IsLetter));
    }

    private static int CountSpokenWords(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inLatinWord = false;

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (IsJapaneseCharacter(current))
            {
                count++;
                inLatinWord = false;
                continue;
            }

            if (char.IsLetterOrDigit(current))
            {
                if (!inLatinWord)
                {
                    count++;
                    inLatinWord = true;
                }
                continue;
            }

            if ((current is '\'' or '’') &&
                inLatinWord &&
                index + 1 < text.Length &&
                char.IsLetterOrDigit(text[index + 1]))
            {
                continue;
            }

            inLatinWord = false;
        }

        return count;
    }

    private static bool IsJapaneseCharacter(char value) =>
        (value >= '\u3040' && value <= '\u30ff') ||
        (value >= '\u3400' && value <= '\u4dbf') ||
        (value >= '\u4e00' && value <= '\u9fff');

    private sealed record BoundaryCandidate(int EndExclusive, int SpokenWords);
}
