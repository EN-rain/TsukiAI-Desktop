using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace TsukiAI.Core.Services;

public sealed class ChromaSqliteSemanticMemoryService : ISemanticMemoryService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dbDir;
    private readonly string _scriptPath;
    private readonly string? _pythonExe;
    private readonly bool _persistentWorkerEnabled;
    private readonly int _failureThreshold;
    private readonly TimeSpan _circuitCooldown;
    private readonly TimeSpan _requestTimeout;
    private readonly SemanticMemoryWorkerClient? _workerClient;

    private int _consecutiveFailures;
    private DateTimeOffset? _circuitOpenUntilUtc;
    private bool _disposed;

    public ChromaSqliteSemanticMemoryService(string baseDir, string scriptPath)
    {
        _dbDir = Path.Combine(baseDir, "semantic-memory", "chroma");
        Directory.CreateDirectory(_dbDir);
        _scriptPath = scriptPath;
        _pythonExe = ResolvePythonExecutable();
        _persistentWorkerEnabled = GetBoolEnv("TSUKI_SEMANTIC_PERSISTENT_WORKER", defaultValue: true);
        _failureThreshold = Math.Max(1, GetIntEnv("TSUKI_SEMANTIC_CB_FAILURES", 5));
        _circuitCooldown = TimeSpan.FromMilliseconds(Math.Max(1000, GetIntEnv("TSUKI_SEMANTIC_CB_COOLDOWN_MS", 30000)));
        _requestTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, GetIntEnv("TSUKI_SEMANTIC_REQUEST_TIMEOUT_MS", 8000)));

        if (_persistentWorkerEnabled && !string.IsNullOrWhiteSpace(_pythonExe) && File.Exists(_scriptPath))
        {
            _workerClient = new SemanticMemoryWorkerClient(_pythonExe!, _scriptPath, _dbDir, _requestTimeout);
        }

        DevLog.WriteLine(
            "SemanticMemory: init db={0}, script_exists={1}, python={2}, persistent_worker={3}, cb_threshold={4}, cb_cooldown_ms={5}, request_timeout_ms={6}",
            _dbDir,
            File.Exists(_scriptPath),
            string.IsNullOrWhiteSpace(_pythonExe) ? "missing" : _pythonExe,
            _persistentWorkerEnabled,
            _failureThreshold,
            (int)_circuitCooldown.TotalMilliseconds,
            (int)_requestTimeout.TotalMilliseconds);
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (!CanRun() || IsCircuitOpen())
        {
            DevLog.WriteLine("SemanticMemory: ensure skipped (runner unavailable or circuit open)");
            return false;
        }

        try
        {
            if (_workerClient is not null)
            {
                var response = await _workerClient.ExecuteAsync("ensure", new { }, ct);
                var ok = response.Ok && response.Error.Length == 0;
                if (ok)
                {
                    RecordSuccess();
                    DevLog.WriteLine("SemanticMemory: ensure ready (worker)");
                    return true;
                }

                RecordFailure("ensure(worker)", response.Error);
                return false;
            }

            var result = await RunCliAsync($"\"{_scriptPath}\" ensure --db \"{_dbDir}\"", ct);
            if (result.ExitCode == 0)
            {
                RecordSuccess();
                DevLog.WriteLine("SemanticMemory: ensure ready (cli)");
                return true;
            }

            RecordFailure("ensure(cli)", Truncate(result.StdErr));
            return false;
        }
        catch (Exception ex)
        {
            RecordFailure("ensure(exception)", ex.Message);
            return false;
        }
    }

    public async Task AddMemoryAsync(string text, string source = "voicechat", CancellationToken ct = default)
    {
        if (!CanRun() || string.IsNullOrWhiteSpace(text) || IsCircuitOpen())
            return;

        try
        {
            if (_workerClient is not null)
            {
                var response = await _workerClient.ExecuteAsync("add", new { text, source }, ct);
                if (!response.Ok)
                {
                    RecordFailure("add(worker)", response.Error);
                    return;
                }

                RecordSuccess();
                return;
            }

            var escapedText = EscapeArg(text);
            var escapedSource = EscapeArg(source);
            var result = await RunCliAsync($"\"{_scriptPath}\" add --db \"{_dbDir}\" --text \"{escapedText}\" --source \"{escapedSource}\"", ct);
            if (result.ExitCode == 0)
            {
                RecordSuccess();
                return;
            }

            RecordFailure("add(cli)", Truncate(result.StdErr));
        }
        catch (Exception ex)
        {
            RecordFailure("add(exception)", ex.Message);
        }
    }

    public async Task<IReadOnlyList<SemanticMemoryHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (!CanRun() || string.IsNullOrWhiteSpace(query) || IsCircuitOpen())
            return [];

        try
        {
            var k = Math.Max(1, Math.Min(20, topK));

            if (_workerClient is not null)
            {
                var response = await _workerClient.ExecuteAsync("search", new { query, k }, ct);
                if (!response.Ok)
                {
                    RecordFailure("search(worker)", response.Error);
                    return [];
                }

                try
                {
                    var hits = response.Data.ValueKind == JsonValueKind.Undefined || response.Data.ValueKind == JsonValueKind.Null
                        ? []
                        : JsonSerializer.Deserialize<List<SemanticMemoryHit>>(response.Data.GetRawText(), JsonOptions) ?? [];
                    RecordSuccess();
                    return hits;
                }
                catch (Exception ex)
                {
                    RecordFailure("search(parse)", ex.Message);
                    return [];
                }
            }

            var escapedQuery = EscapeArg(query);
            var result = await RunCliAsync($"\"{_scriptPath}\" search --db \"{_dbDir}\" --query \"{escapedQuery}\" --k {k}", ct);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            {
                if (result.ExitCode != 0)
                {
                    RecordFailure("search(cli)", Truncate(result.StdErr));
                }
                else
                {
                    RecordFailure("search(cli)", "empty stdout");
                }

                return [];
            }

            try
            {
                var hits = JsonSerializer.Deserialize<List<SemanticMemoryHit>>(result.StdOut, JsonOptions) ?? [];
                RecordSuccess();
                return hits;
            }
            catch (Exception ex)
            {
                RecordFailure("search(parse-cli)", ex.Message);
                return [];
            }
        }
        catch (Exception ex)
        {
            RecordFailure("search(exception)", ex.Message);
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _workerClient?.Dispose();
    }

    private bool CanRun()
    {
        return !string.IsNullOrWhiteSpace(_pythonExe) && File.Exists(_scriptPath);
    }

    private bool IsCircuitOpen()
    {
        var openUntil = _circuitOpenUntilUtc;
        if (openUntil is null)
            return false;

        if (DateTimeOffset.UtcNow < openUntil.Value)
            return true;

        _circuitOpenUntilUtc = null;
        return false;
    }

    private void RecordSuccess()
    {
        Volatile.Write(ref _consecutiveFailures, 0);
        _circuitOpenUntilUtc = null;
    }

    private void RecordFailure(string operation, string? message)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        DevLog.WriteLine("SemanticMemory: {0} failed (count={1}): {2}", operation, failures, Truncate(message));
        if (failures >= _failureThreshold)
        {
            _circuitOpenUntilUtc = DateTimeOffset.UtcNow.Add(_circuitCooldown);
            DevLog.WriteLine("SemanticMemory: circuit opened for {0}ms", (int)_circuitCooldown.TotalMilliseconds);
            Volatile.Write(ref _consecutiveFailures, 0);
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string arguments, CancellationToken ct)
    {
        if (_pythonExe is null)
            return (-1, string.Empty, "python not found");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        await process.WaitForExitAsync(timeoutCts.Token);

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

    private static string EscapeArg(string value) => value.Replace("\"", "\\\"");

    private static string Truncate(string? value, int max = 220)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : $"{trimmed[..max]}...";
    }

    private static bool GetBoolEnv(string key, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static int GetIntEnv(string key, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private sealed class SemanticMemoryWorkerClient : IDisposable
    {
        private readonly string _pythonExe;
        private readonly string _scriptPath;
        private readonly string _dbDir;
        private readonly TimeSpan _requestTimeout;
        private readonly Channel<WorkerCommandEnvelope> _outboundQueue = Channel.CreateBounded<WorkerCommandEnvelope>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerResponse>> _pending = new();
        private readonly SemaphoreSlim _startGate = new(1, 1);

        private Process? _process;
        private StreamWriter? _stdin;
        private Task? _writerTask;
        private Task? _readerTask;
        private Task? _stderrTask;
        private CancellationTokenSource? _lifecycleCts;
        private bool _disposed;

        public SemanticMemoryWorkerClient(string pythonExe, string scriptPath, string dbDir, TimeSpan requestTimeout)
        {
            _pythonExe = pythonExe;
            _scriptPath = scriptPath;
            _dbDir = dbDir;
            _requestTimeout = requestTimeout;
        }

        public async Task<WorkerResponse> ExecuteAsync(string cmd, object args, CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SemanticMemoryWorkerClient));

            await EnsureStartedAsync(ct);

            var request = new WorkerRequest(Guid.NewGuid().ToString("N"), cmd, JsonSerializer.SerializeToElement(args));
            var tcs = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[request.Id] = tcs;

            await _outboundQueue.Writer.WriteAsync(new WorkerCommandEnvelope(request, tcs), ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_requestTimeout);
            using var reg = timeoutCts.Token.Register(() =>
            {
                if (_pending.TryRemove(request.Id, out var pending))
                {
                    pending.TrySetResult(new WorkerResponse(request.Id, false, default, "request timeout"));
                }
            });

            var response = await tcs.Task;
            return response;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                _lifecycleCts?.Cancel();
            }
            catch { }

            _outboundQueue.Writer.TryComplete();
            try { _writerTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            try { _readerTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            try { _stderrTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }

            FailAllPending("worker disposed");
            StopProcess();
            _lifecycleCts?.Dispose();
            _startGate.Dispose();
        }

        private async Task EnsureStartedAsync(CancellationToken ct)
        {
            if (_process is { HasExited: false } && _writerTask is not null && _readerTask is not null)
                return;

            await _startGate.WaitAsync(ct);
            try
            {
                if (_process is { HasExited: false } && _writerTask is not null && _readerTask is not null)
                    return;

                StartProcess();
            }
            finally
            {
                _startGate.Release();
            }
        }

        private void StartProcess()
        {
            StopProcess();
            FailAllPending("worker restarted");

            _lifecycleCts = new CancellationTokenSource();
            var psi = new ProcessStartInfo
            {
                FileName = _pythonExe,
                Arguments = $"\"{_scriptPath}\" --worker --db \"{_dbDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (_, _) =>
            {
                FailAllPending("worker process exited");
            };
            _process.Start();
            _stdin = _process.StandardInput;

            _writerTask = Task.Run(() => WriterLoopAsync(_lifecycleCts.Token));
            _readerTask = Task.Run(() => ReaderLoopAsync(_lifecycleCts.Token));
            _stderrTask = Task.Run(() => StderrLoopAsync(_lifecycleCts.Token));

            DevLog.WriteLine("SemanticMemoryWorker: started pid={0}", _process.Id);
        }

        private void StopProcess()
        {
            try
            {
                _stdin?.Dispose();
            }
            catch { }

            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                }
                catch { }
                finally
                {
                    _process.Dispose();
                    _process = null;
                }
            }
        }

        private async Task WriterLoopAsync(CancellationToken ct)
        {
            try
            {
                while (await _outboundQueue.Reader.WaitToReadAsync(ct))
                {
                    while (_outboundQueue.Reader.TryRead(out var envelope))
                    {
                        try
                        {
                            if (_stdin is null || _process is null || _process.HasExited)
                            {
                                envelope.Completion.TrySetResult(new WorkerResponse(envelope.Request.Id, false, default, "worker unavailable"));
                                continue;
                            }

                            var line = JsonSerializer.Serialize(envelope.Request);
                            await _stdin.WriteLineAsync(line);
                            await _stdin.FlushAsync();
                        }
                        catch (Exception ex)
                        {
                            envelope.Completion.TrySetResult(new WorkerResponse(envelope.Request.Id, false, default, ex.Message));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("SemanticMemoryWorker: writer loop failed: {0}", ex.Message);
            }
        }

        private async Task ReaderLoopAsync(CancellationToken ct)
        {
            if (_process is null)
                return;

            try
            {
                while (!ct.IsCancellationRequested && !_process.HasExited)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(ct);
                    if (line is null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    WorkerResponse response;
                    try
                    {
                        response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions)
                            ?? new WorkerResponse(string.Empty, false, default, "invalid worker response");
                    }
                    catch (Exception ex)
                    {
                        DevLog.WriteLine("SemanticMemoryWorker: response parse failed: {0}", ex.Message);
                        continue;
                    }

                    if (response.Id.Length == 0)
                        continue;

                    if (_pending.TryRemove(response.Id, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("SemanticMemoryWorker: reader loop failed: {0}", ex.Message);
            }
        }

        private async Task StderrLoopAsync(CancellationToken ct)
        {
            if (_process is null)
                return;

            try
            {
                while (!ct.IsCancellationRequested && !_process.HasExited)
                {
                    var line = await _process.StandardError.ReadLineAsync(ct);
                    if (line is null)
                        break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        DevLog.WriteLine("SemanticMemoryWorker[stderr]: {0}", line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                DevLog.WriteLine("SemanticMemoryWorker: stderr loop failed: {0}", ex.Message);
            }
        }

        private void FailAllPending(string message)
        {
            foreach (var kv in _pending)
            {
                if (_pending.TryRemove(kv.Key, out var tcs))
                {
                    tcs.TrySetResult(new WorkerResponse(kv.Key, false, default, message));
                }
            }
        }
    }

    private sealed record WorkerCommandEnvelope(WorkerRequest Request, TaskCompletionSource<WorkerResponse> Completion);
    private sealed record WorkerRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("cmd")] string Cmd,
        [property: JsonPropertyName("args")] JsonElement Args);
    private sealed record WorkerResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("data")] JsonElement Data,
        [property: JsonPropertyName("error")] string Error);
}
