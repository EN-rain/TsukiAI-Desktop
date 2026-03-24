using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace TsukiAI.Core.Services;

/// <summary>In-memory dev log for debugging. Thread-safe.</summary>
public static class DevLog
{
    private static readonly ConcurrentQueue<string> _lines = new();
    private static readonly Channel<string> _fileQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private static readonly string LogPath;
    private static readonly Task _fileWriterTask;
    private const int MaxLines = 2000;
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static int _pendingFileWrites;

    /// <summary>Fired when a new line is added (may be from any thread).</summary>
    public static event Action? LogUpdated;

    static DevLog()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TsukiAI");
        Directory.CreateDirectory(baseDir);
        LogPath = Path.Combine(baseDir, "dev.log");
        _fileWriterTask = Task.Run(FileWriterLoopAsync);
    }

    public static void WriteLine(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        _lines.Enqueue(stamped);
        while (_lines.Count > MaxLines)
            _lines.TryDequeue(out _);
        if (_fileQueue.Writer.TryWrite(stamped))
            Interlocked.Increment(ref _pendingFileWrites);
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

    public static string GetLogPath() => LogPath;

    public static async Task FlushAsync(CancellationToken ct = default)
    {
        while (Volatile.Read(ref _pendingFileWrites) > 0 && !ct.IsCancellationRequested)
        {
            await Task.Delay(25, ct);
        }
    }

    private static async Task FileWriterLoopAsync()
    {
        try
        {
            await foreach (var line in _fileQueue.Reader.ReadAllAsync())
            {
                try
                {
                    RotateIfNeeded();
                    await File.AppendAllTextAsync(LogPath, line + Environment.NewLine);
                }
                catch
                {
                    // Keep logging non-fatal.
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingFileWrites);
                }
            }
        }
        catch
        {
            // Ignore background logger failures.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxLogBytes)
                return;

            var archivePath = LogPath + ".1";
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            File.Move(LogPath, archivePath);
        }
        catch
        {
            // Ignore rotation failures.
        }
    }
}
