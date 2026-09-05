using TsukiAI.Core.Models;

namespace TsukiAI.Core.Services;

/// <summary>
/// IInferenceClient that hot-reloads the active provider from
/// ProviderSwitchingService state (provider-state.json) on every call. When the
/// pipeline's 429 handler switches provider, the next turn uses the new provider
/// immediately — no process restart. Clients are created lazily per provider and
/// cached, so failover keeps warmed connections.
/// Single-provider configurations simply delegate to one client.
/// </summary>
public sealed class SwitchingInferenceClient : IInferenceClient, IDisposable
{
    private readonly AppSettings _settings;
    private readonly ISemanticMemoryService? _semanticMemory;
    private readonly ProviderSwitchingService _switcher = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, IInferenceClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public SwitchingInferenceClient(AppSettings settings, ISemanticMemoryService? semanticMemory = null)
    {
        _settings = settings;
        _semanticMemory = semanticMemory;
    }

    private IInferenceClient Active
    {
        get
        {
            var csv = _settings.MultiAiProvidersCsv;
            var provider = _switcher.GetCurrentProvider(csv);
            lock (_gate)
            {
                if (_clients.TryGetValue(provider, out var existing))
                    return existing;

                var client = new RemoteInferenceClient(
                    ProviderSwitchingService.GetProviderUrl(provider),
                    ProviderSwitchingService.GetProviderApiKey(provider, _settings),
                    ProviderSwitchingService.GetProviderModel(provider),
                    _semanticMemory,
                    _settings.GetGenerationTuning(),
                    _settings.ReplyTonePreset);
                _clients[provider] = client;
                DevLog.WriteLine("SwitchingInferenceClient: activated provider '{0}' ({1})",
                    provider, ProviderSwitchingService.GetProviderUrl(provider));
                return client;
            }
        }
    }

    public string Model => Active.Model;
    public bool IsLoaded => Active.IsLoaded;
    public bool IsWarmedUp => Active.IsWarmedUp;

    public Task<bool> IsServerReachableAsync(CancellationToken ct = default) => Active.IsServerReachableAsync(ct);

    public Task<bool> WarmupModelAsync(string? model = null, CancellationToken ct = default) => Active.WarmupModelAsync(model, ct);

    public Task<AiReply> ChatWithEmotionAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        CancellationToken ct = default,
        string? systemInstructions = null,
        string? correlationId = null)
        => Active.ChatWithEmotionAsync(userText, personaName, preferredEmotion, history, ct, systemInstructions, correlationId);

    public Task<AiReply> ChatWithEmotionStreamingAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        Action<string>? onPartialReply = null,
        CancellationToken ct = default,
        string? systemInstructions = null,
        string? correlationId = null)
        => Active.ChatWithEmotionStreamingAsync(userText, personaName, preferredEmotion, history, onPartialReply, ct, systemInstructions, correlationId);

    public void SetModel(string model) => Active.SetModel(model);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (_gate)
        {
            foreach (var client in _clients.Values)
            {
                try { client.Dispose(); }
                catch { /* best-effort */ }
            }
            _clients.Clear();
        }
    }
}
