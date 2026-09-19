using TsukiAI.VoiceChat.Services;
using Xunit;

namespace TsukiAI.Core.Tests;

public sealed class StreamingClauseChunkerTests
{
    [Fact]
    public void AppendPartial_waits_for_stable_completed_clause_and_never_emits_tokens()
    {
        var chunker = new StreamingClauseChunker();

        Assert.Empty(chunker.AppendPartial("One"));
        Assert.Empty(chunker.AppendPartial("One two three"));
        Assert.Empty(chunker.AppendPartial("One two three four five six."));

        var emitted = chunker.AppendPartial("One two three four five six. Next thought");

        Assert.Equal(["One two three four five six."], emitted);
        Assert.Equal(["Next thought"], chunker.Flush());
    }

    [Fact]
    public void AppendPartial_uses_shorter_first_target_then_larger_later_target()
    {
        var chunker = new StreamingClauseChunker();
        const string first = "One two three four five six. ";

        Assert.Equal(
            ["One two three four five six."],
            chunker.AppendPartial(first));

        const string laterTenWords =
            "Seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen. ";
        Assert.Empty(chunker.AppendPartial(first + laterTenWords));

        const string laterTwelveWords = laterTenWords + "Seventeen eighteen. Continue";
        Assert.Equal(
            ["Seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen. Seventeen eighteen."],
            chunker.AppendPartial(first + laterTwelveWords));
    }

    [Fact]
    public void AppendPartial_prefers_clause_punctuation_once_first_target_is_reached()
    {
        var chunker = new StreamingClauseChunker();

        var emitted = chunker.AppendPartial(
            "One two three four five six, seven eight nine ten. More");

        Assert.Equal(["One two three four five six,"], emitted);
    }

    [Fact]
    public void AppendPartial_preserves_internal_text_and_order_while_trimming_boundaries()
    {
        var chunker = new StreamingClauseChunker();
        const string partial = "  One  two three four five six.   Seven  eight nine";

        Assert.Equal(
            ["One  two three four five six."],
            chunker.AppendPartial(partial));
        Assert.Equal(["Seven  eight nine"], chunker.Flush());
    }

    [Fact]
    public void AppendPartial_treats_abbreviations_and_decimals_conservatively()
    {
        var chunker = new StreamingClauseChunker();

        Assert.Empty(chunker.AppendPartial("Dr."));
        Assert.Empty(chunker.AppendPartial("Dr. Smith measured 3."));

        var emitted = chunker.AppendPartial(
            "Dr. Smith measured 3.14 volts very carefully today. Next");

        Assert.Equal(["Dr. Smith measured 3.14 volts very carefully today."], emitted);
    }

    [Fact]
    public void AppendPartial_does_not_split_dotted_abbreviations()
    {
        var chunker = new StreamingClauseChunker();

        var emitted = chunker.AppendPartial(
            "Use e.g. this U.S. example very carefully today. Next");

        Assert.Equal(["Use e.g. this U.S. example very carefully today."], emitted);
    }

    [Theory]
    [InlineData("今日はとてもいい天気ですね。次", "今日はとてもいい天気ですね。")]
    [InlineData("今日はとてもいい天気ですね！次", "今日はとてもいい天気ですね！")]
    [InlineData("今日はとてもいい天気ですね？次", "今日はとてもいい天気ですね？")]
    public void AppendPartial_supports_japanese_terminal_punctuation(
        string partial,
        string expected)
    {
        var chunker = new StreamingClauseChunker();

        Assert.Equal([expected], chunker.AppendPartial(partial));
    }

    [Fact]
    public void AppendPartial_handles_mixed_english_and_japanese_without_reordering_text()
    {
        var chunker = new StreamingClauseChunker();
        const string partial = "今日はgood timingで進められそう。 We can continue carefully from here. Next";

        var emitted = chunker.AppendPartial(partial);

        Assert.Equal(["今日はgood timingで進められそう。"], emitted);
        Assert.Equal(["We can continue carefully from here. Next"], chunker.Flush());
    }

    [Fact]
    public void Flush_emits_unpunctuated_remainder_and_resets_for_next_response()
    {
        var chunker = new StreamingClauseChunker();

        Assert.Empty(chunker.AppendPartial("A final short remainder"));
        Assert.Equal(["A final short remainder"], chunker.Flush());

        Assert.Equal(
            ["Fresh response has exactly six spoken words."],
            chunker.AppendPartial("Fresh response has exactly six spoken words. Next"));
    }

    [Fact]
    public void AppendPartial_ignores_duplicate_snapshot_and_rejects_rewrites()
    {
        var chunker = new StreamingClauseChunker();

        Assert.Empty(chunker.AppendPartial("One two three"));
        Assert.Empty(chunker.AppendPartial("One two three"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            chunker.AppendPartial("One two changed"));

        Assert.Contains("without rewriting", error.Message);
    }
}
