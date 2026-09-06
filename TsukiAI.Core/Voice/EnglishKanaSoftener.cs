using System.Text;
using System.Text.RegularExpressions;

namespace TsukiAI.VoiceChat.Services;

/// <summary>
/// Softens VOICEVOX's English accent. VOICEVOX's dictionary renders English
/// words as awkward katakana (e.g. "everyone" -> エヴェリョオネ); this pre-pass
/// swaps known English words for tuned katakana, and approximates unknown
/// words with letter-combination rules (tion -> ション, oo -> ウー, ...).
/// Spaces are kept — VOICEVOX turns them into natural micro-pauses (stripping
/// them produces dense sibilant clusters that read as static).
/// </summary>
public static partial class EnglishKanaSoftener
{
    [GeneratedRegex(@"[A-Za-z][A-Za-z'’]*")]
    private static partial Regex WordRegex();

    // Multi-word phrases first, then single words — tuned by ear against the
    // real voice. Extend freely: one line per word, deployed instantly.
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // phrases
        ["nice to meet you"] = "ナイストゥーミーチュー",
        ["how are you"] = "ハウアーユー",
        ["how's it going"] = "ハウズイットゴーイング",
        ["thank you"] = "サンキュー",
        ["good morning"] = "グッドモーニング",
        ["good afternoon"] = "グッドアフタヌーン",
        ["good evening"] = "グッドイブニング",
        ["good night"] = "グッドナイト",
        ["see you"] = "シーユー",
        ["talk to you"] = "トークトゥーユー",
        ["right now"] = "ライトナウ",
        ["of course"] = "オブコース",
        ["as always"] = "アズオールウェイズ",
        ["next to"] = "ネクストゥー",
        // pronouns / core
        ["everyone"] = "エブリワン",
        ["everybody"] = "エブリバディ",
        ["i"] = "アイ",
        ["you"] = "ユー",
        ["your"] = "ヨア",
        ["yours"] = "ヨアーズ",
        ["my"] = "マイ",
        ["me"] = "ミー",
        ["we"] = "ウィー",
        ["he"] = "ヒー",
        ["she"] = "シー",
        ["they"] = "ゼイ",
        ["them"] = "ゼム",
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
        ["cannot"] = "キャノット",
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
        ["raining"] = "レイニング",
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
        ["friends"] = "フレンズ",
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
        // extra conversational words
        ["about"] = "アバウト",
        ["after"] = "アフター",
        ["again"] = "アゲイン",
        ["all"] = "オール",
        ["also"] = "オールソー",
        ["anything"] = "エニシング",
        ["back"] = "バック",
        ["before"] = "ビフォー",
        ["better"] = "ベター",
        ["call"] = "コール",
        ["chat"] = "チャット",
        ["come"] = "カム",
        ["cool"] = "クール",
        ["done"] = "ダン",
        ["even"] = "イーブン",
        ["every"] = "エブリ",
        ["feel"] = "フィール",
        ["feelings"] = "フィーリングス",
        ["find"] = "ファインド",
        ["first"] = "ファースト",
        ["fun"] = "ファン",
        ["getting"] = "ゲッティング",
        ["give"] = "ギブ",
        ["going"] = "ゴーイング",
        ["great"] = "グレイト",
        ["haha"] = "ハハ",
        ["hear"] = "ヒア",
        ["here"] = "ヒア",
        ["hope"] = "ホープ",
        ["hour"] = "アワー",
        ["just"] = "ジャスト",
        ["know"] = "ノウ",
        ["later"] = "レイター",
        ["let"] = "レット",
        ["little"] = "リトル",
        ["long"] = "ロング",
        ["looking"] = "ルッキング",
        ["made"] = "メイド",
        ["make"] = "メイク",
        ["maybe"] = "メイビー",
        ["much"] = "マッチ",
        ["need"] = "ニード",
        ["never"] = "ネバー",
        ["new"] = "ニュー",
        ["now"] = "ナウ",
        ["one"] = "ワン",
        ["only"] = "オンリー",
        ["other"] = "アザー",
        ["our"] = "アワー",
        ["people"] = "ピープル",
        ["play"] = "プレイ",
        ["please"] = "プリーズ",
        ["sure"] = "シュア",
        ["talking"] = "トーキング",
        ["tell"] = "テル",
        ["text"] = "テキスト",
        ["thanks"] = "サンクス",
        ["there"] = "ゼア",
        ["thing"] = "シング",
        ["things"] = "シングズ",
        ["think"] = "シンク",
        ["thinking"] = "シンキング",
        ["thought"] = "ソート",
        ["time"] = "タイム",
        ["tired"] = "タイアード",
        ["true"] = "トゥルー",
        ["want"] = "ウォント",
        ["watching"] = "ウォッチング",
        ["well"] = "ウェル",
        ["were"] = "ワー",
        ["when"] = "ホエン",
        ["will"] = "ウィル",
        ["with"] = "ウィズ",
        ["wonderful"] = "ワンダフル",
        ["world"] = "ワールド",
        ["yeah"] = "イェア",
        ["year"] = "イヤー",
        ["yes"] = "イエス",
        // names
        ["tsuki"] = "ツキ",
        ["rain"] = "レイン",
    };

    // Letter-combination rules for words not in the map — ordered longest first.
    private static readonly (string Pattern, string Kana)[] Rules =
    {
        ("tion", "ション"), ("sion", "ジョン"), ("cious", "シャス"), ("tious", "シャス"),
        ("ture", "チャー"), ("sure", "ジャー"), ("ment", "メント"), ("ness", "ネス"),
        ("ing", "イング"), ("able", "エイブル"), ("ible", "イブル"),
        ("ough", "オー"), ("augh", "オー"),
        ("ee", "イー"), ("ea", "イー"), ("oo", "ウー"), ("ou", "アウ"), ("ow", "オウ"),
        ("ai", "エイ"), ("ay", "エイ"), ("ei", "エイ"), ("ey", "イー"), ("oa", "オウ"),
        ("oi", "オイ"), ("oy", "オイ"), ("au", "オー"), ("aw", "オー"),
        ("eu", "ユウ"), ("ew", "ユー"), ("ui", "ウイ"),
        ("ur", "アー"), ("ir", "アー"), ("ar", "アー"), ("or", "オー"), ("er", "アー"),
        ("sh", "シュ"), ("ch", "チ"), ("th", "ズ"), ("ph", "フ"), ("wh", "ワ"),
        ("ck", "ック"), ("qu", "クウォ"), ("ng", "ング"), ("nk", "ンク"),
        ("tt", "ト"), ("pp", "プ"), ("ss", "ス"), ("ll", "ル"), ("mm", "ム"),
        ("nn", "ン"), ("bb", "ブ"), ("dd", "ド"), ("gg", "グ"), ("rr", "ル"),
        ("nd", "ンド"), ("nt", "ント"), ("st", "スト"), ("mp", "ンプ"), ("lk", "ク"), ("mb", "ム"),
        ("j", "ジ"), ("v", "ヴ"), ("f", "フ"), ("w", "ウ"), ("x", "クス"), ("z", "ズ"),
        ("y", "イー"),
        ("a", "ア"), ("e", "エ"), ("i", "イ"), ("o", "オ"), ("u", "ウ"),
        ("b", "ブ"), ("c", "ク"), ("d", "ド"), ("g", "グ"), ("k", "ク"), ("p", "プ"),
        ("r", "ル"), ("s", "ス"), ("t", "ト"), ("h", "ハ"), ("l", "ル"), ("m", "ム"), ("n", "ン"),
    };

    /// <summary>
    /// Applies the tuned word map, then approximates remaining Latin words with
    /// the rule table. Words must be seen in isolation: strip the trailing
    /// silent "e" first so "nice" doesn't become ニケ.
    /// </summary>
    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Regex.IsMatch(text, @"[A-Za-z]"))
            return text;

        var replaced = WordRegex().Replace(text, match =>
        {
            var word = match.Value;
            return Map.TryGetValue(word.TrimEnd('\'', '’'), out var kana) ? kana : word;
        });

        // Rules pass for any remaining Latin words (unknown to the map).
        return WordRegex().Replace(replaced, match =>
        {
            var word = match.Value;
            if (word.Length <= 1)
                return word;

            var lower = word.ToLowerInvariant().TrimEnd('e');
            if (lower.Length == 0)
                return word;

            var sb = new StringBuilder();
            var pos = 0;
            while (pos < lower.Length)
            {
                var matched = false;
                foreach (var (pattern, kana) in Rules)
                {
                    if (lower.Length - pos < pattern.Length ||
                        string.CompareOrdinal(lower, pos, pattern, 0, pattern.Length) != 0)
                        continue;
                    sb.Append(kana);
                    pos += pattern.Length;
                    matched = true;
                    break;
                }
                if (!matched)
                {
                    sb.Append(lower[pos]);
                    pos++;
                }
            }
            return sb.ToString();
        });
    }
}
