using System.Net;
using System.Windows;
using System.Windows.Controls;

namespace TsukiAI.VoiceChat.Views;

public partial class SettingsWindow
{
    private void AiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SettingsVm vm || CmbAiProvider.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var tag = (item.Tag as string ?? "openrouter").ToLowerInvariant();
        vm.AiProvider = tag;

        if (GetInferenceModeSelectionTag() != "provider")
        {
            return;
        }

        vm.RemoteInferenceUrl = EnsureProviderUrl(tag, vm.RemoteInferenceUrl);
        vm.ModelName = EnsureProviderModel(tag, vm.ModelName);
    }

    private async void BtnTestRemoteConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
        {
            return;
        }

        var isProviderMode = GetInferenceModeSelectionTag() == "provider";
        var provider = (CmbAiProvider?.SelectedItem as ComboBoxItem)?.Tag as string ?? "custom";
        var url = isProviderMode
            ? EnsureProviderUrl(provider, vm.RemoteInferenceUrl?.Trim())
            : (vm.RemoteInferenceUrl?.Trim() ?? string.Empty);
        vm.RemoteInferenceUrl = url;
        if (string.IsNullOrEmpty(url))
        {
            TxtConnectionStatus.Text = "Please enter a URL";
            TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        TxtConnectionStatus.Text = isProviderMode ? "Testing API key..." : "Testing tunnel server...";
        TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;
        BtnTestRemoteConnection.IsEnabled = false;

        try
        {
            (bool ok, string message) result;
            if (isProviderMode)
            {
                var providerApiInput = GetProviderApiInput(provider);
                var providerApiKey = ResolveProviderApiKeyForTest(provider, providerApiInput);
                var probe = await TestApiKeyAsync(provider, url, providerApiKey, 60);
                result = (probe.Ok, probe.Message);
            }
            else
            {
                using var client = new TsukiAI.Core.Services.RemoteInferenceClient(url, string.Empty);
                var reachable = await client.IsServerReachableAsync();
                result = reachable
                    ? (true, "Tunnel server reachable.")
                    : (false, "Tunnel server not reachable.");
            }

            var (ok, message) = result;
            TxtConnectionStatus.Text = message;
            TxtConnectionStatus.Foreground = ok
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9B, 0x7B, 0xFF))
                : System.Windows.Media.Brushes.Red;
        }
        catch (Exception ex)
        {
            TxtConnectionStatus.Text = $"Error: {ex.Message}";
            TxtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            BtnTestRemoteConnection.IsEnabled = true;
        }
    }

    private void EditProviderApi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        string provider = button.Tag?.ToString()?.Split('|')[0] ?? "";

        var panel = provider switch
        {
            "cerebras" => PanelCerebrasApiKey,
            "groq" => PanelGroqApiKey,
            "gemini" => PanelGeminiApiKey,
            "github" => PanelGitHubApiKey,
            "mistral" => PanelMistralApiKey,
            _ => null
        };

        if (panel != null)
        {
            bool isNowVisible = panel.Visibility != Visibility.Visible;
            panel.Visibility = isNowVisible ? Visibility.Visible : Visibility.Collapsed;
            button.Tag = isNowVisible ? $"{provider}|active" : provider;
        }
    }

    private async void TestProviderConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string provider)
        {
            return;
        }

        var (apiKeyBox, statusText, url) = provider switch
        {
            "cerebras" => (TxtCerebrasApiKey, TxtCerebrasStatus, "https://api.cerebras.ai/v1"),
            "groq" => (TxtGroqApiKey, TxtGroqStatus, "https://api.groq.com/openai/v1"),
            "gemini" => (TxtGeminiApiKey, TxtGeminiStatus, "https://generativelanguage.googleapis.com/v1beta/openai"),
            "github" => (TxtGitHubApiKey, TxtGitHubStatus, "https://models.github.ai/inference"),
            "mistral" => (TxtMistralApiKey, TxtMistralStatus, "https://api.mistral.ai/v1"),
            _ => (null, null, null)
        };

        if (apiKeyBox == null || statusText == null || url == null)
        {
            return;
        }

        var apiKey = ResolveProviderApiKeyForTest(provider, apiKeyBox.Password);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            statusText.Text = "Please enter an API key";
            statusText.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        button.IsEnabled = false;
        button.Content = "Testing API...";
        statusText.Text = "Testing API key...";
        statusText.Foreground = System.Windows.Media.Brushes.Gray;

        try
        {
            var probe = await TestApiKeyAsync(provider, url, apiKey, 60);
            _testedProviders.Add(provider);
            statusText.Text = probe.Ok ? $"✓ {probe.Message}" : $"✗ {probe.Message}";
            statusText.Foreground = probe.Ok
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9B, 0x7B, 0xFF))
                : System.Windows.Media.Brushes.Red;
            UpdateProviderStatusIndicators();
        }
        catch (Exception ex)
        {
            statusText.Text = $"✗ Error: {ex.Message}";
            statusText.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = "Test Connection";
        }
    }

    private sealed record ApiProbeResult(bool Ok, string Message);

    private async Task<ApiProbeResult> TestApiKeyAsync(string provider, string baseUrl, string apiKey, int fallbackCooldownSeconds)
    {
        var normalizedApiKey = NormalizeApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(normalizedApiKey))
        {
            return new(false, "Please enter an API key.");
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new(false, "Please enter a provider URL.");
        }

        var candidates = BuildModelProbeUrls(baseUrl, provider);
        var attempted = new List<string>();
        foreach (var endpoint in candidates)
        {
            try
            {
                attempted.Add(endpoint);
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, endpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", normalizedApiKey);
                using var response = await ProviderProbeClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return new(true, "API key validated.");
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    continue;
                }

                if ((int)response.StatusCode == 429)
                {
                    return new(false, "API rate-limited. Please try again later.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync();
                var statusCode = (int)response.StatusCode;
                var snippet = string.IsNullOrWhiteSpace(body)
                    ? $"HTTP {statusCode}"
                    : $"HTTP {statusCode}: {body[..Math.Min(body.Length, 120)]}";
                return new(false, $"API test failed. {snippet}");
            }
            catch (TaskCanceledException)
            {
                return new(false, "API test timed out.");
            }
            catch (Exception ex)
            {
                return new(false, $"Network/API error: {ex.Message}");
            }
        }

        var chatProbe = await TryOpenAiCompatibleChatProbeAsync(ProviderProbeClient, baseUrl, provider, normalizedApiKey, fallbackCooldownSeconds);
        if (chatProbe.ok)
        {
            return new(chatProbe.ok, chatProbe.message);
        }

        var attempts = attempted.Count == 0 ? "(none)" : string.Join(" | ", attempted.Take(3));
        return new(false, $"{chatProbe.message} Tried: {attempts}");
    }

    private void LoadMultiProviderApiKeys()
    {
        if (TxtCerebrasApiKey != null) TxtCerebrasApiKey.Password = MaskApiKey(Result.CerebrasApiKey);
        if (TxtGroqApiKey != null) TxtGroqApiKey.Password = MaskApiKey(Result.GroqApiKey);
        if (TxtGeminiApiKey != null) TxtGeminiApiKey.Password = MaskApiKey(Result.GeminiApiKey);
        if (TxtGitHubApiKey != null) TxtGitHubApiKey.Password = MaskApiKey(Result.GitHubApiKey);
        if (TxtMistralApiKey != null) TxtMistralApiKey.Password = MaskApiKey(Result.MistralApiKey);

        ValidateProviderApiKey("cerebras", Result.CerebrasApiKey, ChkCerebras);
        ValidateProviderApiKey("groq", Result.GroqApiKey, ChkGroq);
        ValidateProviderApiKey("gemini", Result.GeminiApiKey, ChkGemini);
        ValidateProviderApiKey("github", Result.GitHubApiKey, ChkGitHub);
        ValidateProviderApiKey("mistral", Result.MistralApiKey, ChkMistral);
    }

    private void UpdateProviderStatusIndicators()
    {
        UpdateProviderStatus("cerebras", StatusCerebras, Result.CerebrasApiKey, ChkCerebras);
        UpdateProviderStatus("groq", StatusGroq, Result.GroqApiKey, ChkGroq);
        UpdateProviderStatus("gemini", StatusGemini, Result.GeminiApiKey, ChkGemini);
        UpdateProviderStatus("github", StatusGitHub, Result.GitHubApiKey, ChkGitHub);
        UpdateProviderStatus("mistral", StatusMistral, Result.MistralApiKey, ChkMistral);
    }
}
