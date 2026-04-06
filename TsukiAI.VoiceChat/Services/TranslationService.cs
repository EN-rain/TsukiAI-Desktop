using System.Net.Http;
using System.Text.Json;

namespace TsukiAI.VoiceChat.Services;

public sealed class TranslationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly bool _enabled;

    public TranslationService(TsukiAI.Core.Models.AppSettings settings)
    {
        _apiKey = settings.DeepLApiKey?.Trim() ?? "";
        _enabled = settings.UseDeepLTranslate && !string.IsNullOrWhiteSpace(_apiKey);
        _baseUrl = settings.UseDeepLFreeApi ? "https://api-free.deepl.com/v2" : "https://api.deepl.com/v2";

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (_enabled)
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {_apiKey}");
    }

    public bool IsEnabled => _enabled;

    public async Task<string> TranslateToJapaneseAsync(string text, CancellationToken ct = default)
    {
        return await TranslateAsync(text, "JA", "EN", ct);
    }

    public async Task<string> TranslateToEnglishAsync(string text, CancellationToken ct = default)
    {
        return await TranslateAsync(text, "EN", null, ct);
    }

    public async Task<string> TranslateAsync(string text, string targetLang, string? sourceLang = null, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return string.Empty;
        if (!_enabled) return text;

        try
        {
            var form = new Dictionary<string, string>
            {
                ["text"] = text,
                ["target_lang"] = targetLang
            };
            if (!string.IsNullOrWhiteSpace(sourceLang))
                form["source_lang"] = sourceLang;

            using var content = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync($"{_baseUrl}/translate", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                TsukiAI.Core.Services.DevLog.WriteLine($"[Translation] DeepL failed: {(int)response.StatusCode} {err}");
                return text;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<DeepLResponse>(body);
            return result?.translations?.FirstOrDefault()?.text?.Trim() ?? text;
        }
        catch (Exception ex)
        {
            TsukiAI.Core.Services.DevLog.WriteLine($"[Translation] DeepL exception: {ex.Message}");
            return text;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class DeepLResponse
    {
        public DeepLTranslation[]? translations { get; set; }
    }

    private sealed class DeepLTranslation
    {
        public string? text { get; set; }
    }
}
