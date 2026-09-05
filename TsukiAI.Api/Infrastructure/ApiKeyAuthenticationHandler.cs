using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace TsukiAI.Api.Infrastructure;

/// <summary>
/// API-key auth for headless machine clients (discord-voice-bridge on the VPS).
/// A request carrying a correct X-Api-Key header is authenticated without a
/// browser session cookie. Enabled only when TSUKI_API_KEY is set on the server;
/// otherwise this scheme never authenticates anyone.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expectedKey = Options.ApiKey;
        if (string.IsNullOrWhiteSpace(expectedKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!Request.Headers.TryGetValue(HeaderName, out var provided) ||
            string.IsNullOrWhiteSpace(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!string.Equals(provided.ToString().Trim(), expectedKey, StringComparison.Ordinal))
        {
            Logger.LogWarning("Rejected request with invalid API key from {RemoteIp}",
                Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "bridge")], SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    public string? ApiKey { get; set; }
}
