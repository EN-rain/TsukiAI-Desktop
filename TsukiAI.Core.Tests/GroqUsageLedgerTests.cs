using System.Net;
using System.Text;
using System.Text.Json;
using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class GroqUsageLedgerTests
{
    [Fact]
    public void Persists_attempts_across_recreation_without_secrets_and_marks_partial_history()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tsuki-usage-" + Guid.NewGuid().ToString("N"));
        const string key = "gsk_fake_test_credentials_12345";
        try
        {
            var ledger = new GroqUsageLedger(directory);
            ledger.RecordAttempt(key, "llm");
            ledger.RecordAttempt(key, "stt");

            var recreated = new GroqUsageLedger(directory);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var snapshot = recreated.Snapshot(today, [key]);
            Assert.Equal("UTC", snapshot.Timezone);
            Assert.Equal("partial", snapshot.Coverage);
            Assert.Null(snapshot.TotalAttempts);
            Assert.Equal(2, snapshot.RecordedTotalAttempts);
            Assert.Equal(1, snapshot.LlmAttempts);
            Assert.Equal(1, snapshot.SttAttempts);
            Assert.Equal(GroqUsageLedger.Fingerprint(key), snapshot.PerKey[0].KeyId);
            Assert.Equal(2, snapshot.PerKey[0].RecordedAttempts);
            Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(directory, "groq-usage-api.jsonl")));
            Assert.DoesNotContain(key, JsonSerializer.Serialize(snapshot));
            Assert.Null(recreated.Snapshot(today.AddDays(-1), [key]).TotalAttempts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Combines_bridge_and_api_and_marks_full_day_after_both_tracking_started()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tsuki-usage-" + Guid.NewGuid().ToString("N"));
        const string key = "gsk_fake_test_credentials_12345";
        try
        {
            var ledger = new GroqUsageLedger(directory);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var yesterday = new DateTimeOffset(today.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var marker = JsonSerializer.Serialize(new
            {
                type = "tracking_started", timestamp_utc = yesterday.ToString("O"),
                key_id = (string?)null, key_mask = (string?)null, kind = (string?)null
            }) + "\n";
            File.AppendAllText(Path.Combine(directory, "groq-usage-api.jsonl"), marker);
            File.AppendAllText(Path.Combine(directory, "groq-usage-bridge.jsonl"), marker);
            ledger.RecordAttempt(key, "llm");
            var eventLine = JsonSerializer.Serialize(new
            {
                type = "attempt", timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                key_id = GroqUsageLedger.Fingerprint(key), key_mask = GroqUsageLedger.Mask(key), kind = "stt"
            }) + "\n";
            File.AppendAllText(Path.Combine(directory, "groq-usage-bridge.jsonl"), eventLine);

            var snapshot = ledger.Snapshot(today, [key]);
            Assert.Equal("complete", snapshot.Coverage);
            Assert.Equal(2, snapshot.TotalAttempts);
            Assert.Equal(2, snapshot.RecordedTotalAttempts);
            Assert.Equal(1, snapshot.LlmAttempts);
            Assert.Equal(1, snapshot.SttAttempts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Http_handler_counts_each_actual_groq_send_and_skips_other_hosts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tsuki-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = new GroqUsageLedger(directory);
            using var client = new HttpClient(new GroqUsageHttpHandler(
                new StubHandler(), ledger, "gsk_fake_test_credentials_12345"));
            using var first = await client.GetAsync("https://api.groq.com/openai/v1/models");
            using var second = await client.PostAsync("https://api.groq.com/openai/v1/chat/completions",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            using var third = await client.GetAsync("https://example.com/openai/v1/models");
            var snapshot = ledger.Snapshot(DateOnly.FromDateTime(DateTime.UtcNow));
            Assert.Equal(2, snapshot.RecordedTotalAttempts);
            Assert.Equal(1, snapshot.LlmAttempts);
            Assert.Equal(1, snapshot.OtherAttempts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}") });
    }
}
