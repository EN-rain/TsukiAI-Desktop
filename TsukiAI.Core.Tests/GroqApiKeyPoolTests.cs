using TsukiAI.Core.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class GroqApiKeyPoolTests
{
    [Fact]
    public void Parse_deduplicates_supported_key_formats_without_logging_values()
    {
        var keys = GroqApiKeyPool.Parse("GROQ_API_KEY=gsk_first\ngsk_second, Bearer gsk_third\n# ignored");

        Assert.Equal(3, keys.Count);
        Assert.Equal("gsk_first", keys[0]);
        Assert.Equal("gsk_second", keys[1]);
        Assert.Equal("gsk_third", keys[2]);
    }

    [Fact]
    public void Failed_key_moves_to_the_back_and_success_restores_it_as_current()
    {
        var pool = new GroqApiKeyPool(["first", "second", "third"]);

        pool.MarkFailure("first", 401);
        Assert.Equal(["second", "third"], pool.GetCandidates());

        pool.MarkSuccess("third");
        Assert.Equal(["third", "second"], pool.GetCandidates());
    }
}
