using System.Text.RegularExpressions;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Softens VOICEVOX's English accent. VOICEVOX's dictionary renders English
/// words as awkward katakana (e.g. "everyone" -> エヴェリョオネ); this pre-pass
/// swaps common English words for deliberately-chosen katakana that sounds
/// closer to real English pronunciation before synthesis. Unknown words fall
/// back to VOICEVOX's own reading.
/// </summary>
public static partial class EnglishKanaSoftener
{
    [GeneratedRegex(@"[A-Za-z][A-Za-z'’]*")]
    private static partial Regex WordRegex();

    // Multi-word phrases first, then single words. Katakana chosen to mimic
    // natural English pronunciation within Japanese phonotactics.
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // phrases
        ["nice to meet you"] = "ナイストゥーミーチュー",
        ["how are you"] = "ハウアーユー",
        ["thank you"] = "サンキュー",
        ["good morning"] = "グッドモーニング",
        ["good night"] = "グッドナイト",
        ["see you"] = "シーユー",
        ["talk to you"] = "トークトゥーユー",
        // pronouns / common
        ["everyone"] = "エブリワン",
        ["everybody"] = "エブリバディ",
        ["i"] = "アイ",
        ["you"] = "ユー",
        ["your"] = "ヨア",
        ["my"] = "マイ",
        ["me"] = "ミー",
        ["we"] = "ウィー",
        ["he"] = "ヒー",
        ["she"] = "シー",
        ["it"] = "イット",
        // verbs / common words
        ["am"] = "アム",
        ["is"] = "イズ",
        ["are"] = "アー",
        ["was"] = "ワズ",
        ["hello"] = "ハロー",
        ["hi"] = "ハイ",
        ["hey"] = "ヘイ",
        ["companion"] = "コンパニオン",
        ["can"] = "キャン",
        ["understand"] = "アンダースタンド",
        ["nice"] = "ナイス",
        ["meet"] = "ミート",
        ["moon"] = "ムーン",
        ["moonlight"] = "ムーンライト",
        ["night"] = "ナイト",
        ["tonight"] = "トゥナイト",
        ["today"] = "トゥデイ",
        ["tomorrow"] = "トゥモロー",
        ["rain"] = "レイン",
        ["star"] = "スター",
        ["stars"] = "スターズ",
        ["game"] = "ゲーム",
        ["games"] = "ゲームズ",
        ["work"] = "ワーク",
        ["happy"] = "ハッピー",
        ["sad"] = "サッド",
        ["love"] = "ラブ",
        ["like"] = "ライク",
        ["waiting"] = "ウェイティング",
        ["wait"] = "ウェイト",
        ["speak"] = "スピーク",
        ["speaking"] = "スピークング",
        ["english"] = "イングリッシュ",
        ["japanese"] = "ジャパニーズ",
        ["voice"] = "ボイス",
        ["lonely"] = "ローンリー",
        ["amazing"] = "アメイジング",
        ["congratulations"] = "コングラッチュレーションズ",
        ["okay"] = "オーケー",
        ["sorry"] = "ソーリー",
        ["really"] = "リアリー",
        ["pretty"] = "プリティ",
        ["very"] = "ベリー",
        ["good"] = "グッド",
        ["bad"] = "バッド",
        ["day"] = "デイ",
        ["sleep"] = "スリープ",
        ["sleepy"] = "スリーピー",
        ["rest"] = "レスト",
        ["friend"] = "フレンド",
        ["together"] = "トゥギャザー",
        ["always"] = "オールウェイズ",
        ["forever"] = "フォーエバー",
        ["because"] = "ビコーズ",
        ["but"] = "バット",
        ["and"] = "アンド",
        ["what"] = "ホワット",
        ["where"] = "ウェア",
        ["why"] = "ホワイ",
        ["how"] = "ハウ",
        ["the"] = "ザ",
        ["a"] = "ア",
    };

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Regex.IsMatch(text, @"[A-Za-z]"))
            return text;

        return WordRegex().Replace(text, match =>
        {
            var word = match.Value;
            return Map.TryGetValue(word.TrimEnd('\'', '’'), out var kana) ? kana : word;
        });
    }
}
