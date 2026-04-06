using System.Collections.Concurrent;
using System.Text;

namespace TsukiAI.Core.Services;

/// <summary>In-memory dev log for debugging. Thread-safe.</summary>
public static class DevLog
{
    private static readonly ConcurrentQueue<string> _lines = new();
    private const int MaxLines = 2000;

    /// <summary>Fired when a new line is added (may be from any thread).</summary>
    public static event Action? LogUpdated;

    public static void WriteLine(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        _lines.Enqueue(stamped);
        while (_lines.Count > MaxLines)
            _lines.TryDequeue(out _);
        LogUpdated?.Invoke();
    }

    public static void WriteLine(string format, params object[] args)
    {
        WriteLine(string.Format(format, args));
    }

    /// <summary>Returns all log lines joined by newline.</summary>
    public static string GetText()
    {
        var sb = new StringBuilder(_lines.Count * 80);
        foreach (var line in _lines)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line);
        }
        return sb.ToString();
    }

    public static void Clear()
    {
        while (_lines.TryDequeue(out _)) { }
        LogUpdated?.Invoke();
    }
}
