using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TsukiAI.Core.Services;

public sealed class ChromaSqliteSemanticMemoryService : ISemanticMemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dbDir;
    private readonly string _scriptPath;
    private readonly string? _pythonExe;

    public ChromaSqliteSemanticMemoryService(string baseDir, string scriptPath)
    {
        _dbDir = Path.Combine(baseDir, "semantic-memory", "chroma");
        Directory.CreateDirectory(_dbDir);
        _scriptPath = scriptPath;
        _pythonExe = ResolvePythonExecutable();
        DevLog.WriteLine(
            "SemanticMemory: init db={0}, script_exists={1}, python={2}",
            _dbDir,
            File.Exists(_scriptPath),
            string.IsNullOrWhiteSpace(_pythonExe) ? "missing" : _pythonExe);
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (!CanRun())
        {
            DevLog.WriteLine("SemanticMemory: ensure skipped (python/script unavailable)");
            return false;
        }

        var result = await RunAsync($"\"{_scriptPath}\" ensure --db \"{_dbDir}\"", ct);
        if (result.ExitCode == 0)
        {
            DevLog.WriteLine("SemanticMemory: ensure ready");
        }
        else
        {
            DevLog.WriteLine($"SemanticMemory: ensure failed (exit={result.ExitCode}): {Truncate(result.StdErr)}");
        }
        return result.ExitCode == 0;
    }

    public async Task AddMemoryAsync(string text, string source = "voicechat", CancellationToken ct = default)
    {
        if (!CanRun() || string.IsNullOrWhiteSpace(text))
            return;

        var escapedText = EscapeArg(text);
        var escapedSource = EscapeArg(source);
        var result = await RunAsync($"\"{_scriptPath}\" add --db \"{_dbDir}\" --text \"{escapedText}\" --source \"{escapedSource}\"", ct);
        if (result.ExitCode == 0)
        {
            DevLog.WriteLine("SemanticMemory: add ok (source={0})", source);
        }
        else
        {
            DevLog.WriteLine($"SemanticMemory: add failed (exit={result.ExitCode}): {Truncate(result.StdErr)}");
        }
    }

    public async Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (!CanRun() || string.IsNullOrWhiteSpace(query))
            return [];

        var escapedQuery = EscapeArg(query);
        var k = Math.Max(1, Math.Min(20, topK));
        var result = await RunAsync($"\"{_scriptPath}\" search --db \"{_dbDir}\" --query \"{escapedQuery}\" --k {k}", ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            if (result.ExitCode != 0)
            {
                DevLog.WriteLine($"SemanticMemory: search failed (exit={result.ExitCode}): {Truncate(result.StdErr)}");
            }
            else
            {
                DevLog.WriteLine("SemanticMemory: search returned no output");
            }
            return [];
        }

        try
        {
            var hits = JsonSerializer.Deserialize<List<SemanticMemoryHit>>(result.StdOut, JsonOptions) ?? [];
            DevLog.WriteLine("SemanticMemory: search hits={0} (k={1})", hits.Count, k);
            return hits;
        }
        catch
        {
            DevLog.WriteLine("SemanticMemory: search response parse failed");
            return [];
        }
    }

    private bool CanRun()
    {
        return !string.IsNullOrWhiteSpace(_pythonExe) && File.Exists(_scriptPath);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string arguments, CancellationToken ct)
    {
        if (_pythonExe is null)
            return (-1, string.Empty, "python not found");

        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string? ResolvePythonExecutable()
    {
        foreach (var candidate in new[] { "python", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = candidate == "py" ? "-3 --version" : "--version",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(psi);
                if (process is null)
                    continue;
                process.WaitForExit(3000);
                if (process.ExitCode == 0)
                    return candidate == "py" ? "py" : "python";
            }
            catch
            {
                // Ignore and try next candidate.
            }
        }

        return null;
    }

    private static string EscapeArg(string value)
    {
        return value.Replace("\"", "\\\"");
    }

    private static string Truncate(string? value, int max = 220)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : $"{trimmed[..max]}...";
    }
}
