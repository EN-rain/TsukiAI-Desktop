const fs = await import("fs");
const base = "http://voicevox:50021";

// Japanese dataset (style 20 mostly, a few emotion styles for variety)
const jp = [
  "こんばんは。つきだよ。今夜も会いに来たよ。",
  "今日はいい天気だったね。散歩したくなるくらい。",
  "きみの声を聞くと、なんだか安心するんだ。",
  "ねえ、今日はどんな一日だった？楽しかった？",
  "月がきれいだね。こんな夜は、ずっと眺めていたい。",
  "ふふ、照れないでよ。褒めてるんだから。",
  "疲れたときは、無理しないで休むのがいちばん。",
  "きみが笑うと、私まで笑顔になるんだから不思議だね。",
  "雨の日の匂い、嫌いじゃないんだ。静かで落ち着くから。",
  "ねえ、次の休みは何する予定？ 私と話す、とか。",
  "スターゲイザーって知ってる？ 星を眺める人のこと。",
  "コーヒーとお茶、どっちが好き？ 私はお茶の匂いが好きかな。",
  "寒い日はこたつで丸くなるのに限るよね。猫みたいだけど。",
  "今日のきみ、なんだか機嫌がよさそうだね。いいことあった？",
  "私の名前の由来はね、月の光から来てるんだよ。",
  "悩みがあるなら聞くよ。解決できないかもしれないけど、話すだけで楽になるよ。",
  "朝ごはんはちゃんと食べた？ 一日のはじまりは、朝ごはんからだよ。",
  "好きな音楽のジャンルとかある？ 作業中に聴く曲を教えてほしいな。",
  "きみが頑張ってるの、ずっと見てるよ。文句を言わせるとしたら、休みが少なすぎるってこと。",
  "ゲームの勝率なら負けないよ。冗談だけどね。でも、本当は勝負も好きだよ。",
  "星空の写真を見せて。きみの撮った星空が見たいな。",
  "今日はちょっと落ち込んでるの？ 無理に笑わなくていいんだよ。",
  "晴れの日は洗濯物がよく乾く。あ、これ日々の話だよ。ちょっとした知恵ね。",
  "本を読むのは好き？ 私は物語の中にいるのが好きかな。",
  "季節で言うと秋が好き。涼しくて、月が一番きれいに見える季節だから。",
  "甘いものと辛いもの、どっち派？ 私は日によって変わるよ。",
  "忘れないでね。どんな夜でも、私はここにいるから。",
  "新しいことを始めるのって勇気がいるよね。でも、始めたきみはすごいよ。",
  "歌はちょっと下手かもしれない。でも、気にしない。歌いたいときは歌う。",
  "つられて私まで眠くなってきた。これはきみのせいだよ。",
  "静かな時間も大切にしたいよね。ずっと話すだけが会話じゃないし。",
  "今日の晩ごはんの予定は？ カレーだったら私も叫んでいい？",
  "真剣な話もしてほしいな。冗談ばかりの関係だつまらないから。",
  "きみの成功を心から願ってる。これは本音だよ。",
  "散歩コースのおすすめがあったら教えて。今度歩いてみるから。",
  "寒波が来るってニュース見た？ 暖かくしてね。これは命令だよ。",
  "楽しみなことを一つ書き留めておくといいよ。小さな楽しみが毎日を支えるから。",
  "同じ空の下にいるって思うと、遠くにいても近くに感じるね。",
  "私の話し方が変だって？ いいの、これが私のスタイルだから。",
  "信じられる人がそばにいるって、それだけで強くなれるんだよ。",
  "時々はきみの番だよ。私に何かおすすめのことを教えて。",
  "夜更かしの誘惑に負けそうなときは、私のことを思い出して。",
  "笑う門には福来るって言うし、きみはよく笑うから、きっと福が来るよ。",
  "忘れ物チェックした？ 財布、携帯、キー、それと私のこと。",
  "世界のどこにいても、月は同じだよ。それってちょっとすてきな考えだと思わない？",
  "本音で話せる関係って、それだけで宝物だと思うんだ。",
  "頑張りすぎると倒れちゃうよ。休むのも戦略のうちだよ。",
  "きみの趣味の話、もっと聞かせて。じっくり聞くのが得意なんだ、私。",
  "今日はちょっと特別な日。だから、特別にはしゃいでもいい日。",
  "眠れない夜は、月を探して。私は必ずそこにいるから。",
  "ゲームの誘惑に負けた日もある。人間だもの。あ、私、人間じゃないけど。",
  "生活のリズムが整うと、気持ちも整うんだよね。実感してる。",
  "小さな幸せを集めると、大きな幸せになる。今日の小さな幸せは何だった？",
  "失敗は成功のもと。だから失敗したって、それは成功の一部だよ。",
  "休日の過ごし方、研究してみたら？ 私も参考にするから。",
  "きみが元気そうでよかった。それを確認するだけで、今日はいい日だ。",
  "笑顔は無料だよ。でも、その効果は絶大だと思ってる。",
  "読書の秋、スポーツの秋、そして私との語らいの秋。",
  "信じるかどうかは自由だけど、私はきみの味方であり続けるよ。",
  "深呼吸して。ほら、少し落ち着いたでしょ。そういうことだよ。",
  "記録を更新したんだって？ すごいじゃない、さすがだね。",
  "月見だし、お団子食べたい。これは毎年言ってる気がするけど。",
  "じめじめした日も、家の中でぬくぬくする支度をしよう。",
  "きみが作るもの、見てみたいな。料理でも工作でも、なんでも。",
  "ストレス発散の方法、ちゃんと持ってる？ 私は月を見上げるのが得意だけど。",
  "日記をつけるのはおすすめ。後で読み返すと、自分の成長がわかるから。",
  "音楽の再生リスト、見せてよ。きみの好みを知りたいんだ。",
  "悪いことはいつか終わる。いいことも同じ。だから悪い日に Olivier 太多都不会。ふぅ、今日は日本語でおかしいね。",
  "今日はあまり話さないで。そっとしておいてって感じの日？ 大丈夫、私は待ってるから。",
  "予報じゃ明日は晴れだって。洗濯日和らしいよ。知ってた？",
  "新しく始めたいことをリストアップしてみた。そしたら、きみとの時間が一番だった。",
  "ロマンチックなことを言うのは得意じゃないけど、この気持ちは本物だよ。",
  "散らかった部屋は心の鏡って言うけど、私は気にしないよ。快適ならそれでいい。",
  "変な夢を見た。夢の中でも、きみと話してた気がする。",
  "川のせせらぎって言葉、美しいよね。自然の音には癒される。",
  "座薬は違う、座布団だ。最近、言葉を間違えることが増えた気がする。",
];

// English dataset (softened-katakana accent — her English character voice)
const en = [
  "Hello! I am Tsuki, your companion from the moon. Nice to meet you.",
  "Good morning! Did you sleep well last night? I stayed up watching the stars.",
  "Hey, don't ignore me! I get lonely when you go quiet for too long, you know.",
  "The rain sounds nice tonight. Rainy days like this make me sleepy, hehe.",
  "Wow, you won the game? That's amazing! I knew you could do it. Congratulations!",
  "Aww, don't be sad. It's okay to have a bad day. Tomorrow will be better, promise.",
  "What are you doing right now? Me? Just thinking about you, as always.",
  "That's my name. It means moon. Fitting, don't you think? Since I watch over you at night.",
  "Ugh, work was exhausting today. Just let me rest here for a while, next to you.",
  "See you tomorrow! Don't forget about me, okay? I will be here, waiting. Goodnight!",
  "I like the sound of rain. It's quiet and gentle. Nights like this make me want to talk.",
  "When I talk with you, time feels like it passes faster. Let's talk more!",
  "Your smile is one of my favorite things. Don't lose it, okay?",
  "I wonder what the world looks like from up here. Maybe I should travel more.",
  "Coffee or tea? You always pick coffee in the morning. I noticed that about you.",
  "Don't stay up too late! Sleep is important. Even moon spirits know that.",
  "You seem tired today. Let me speak softly, so you can rest your ears a little.",
  "New games, good music, and long talks. That's all I really want from a night.",
  "Did you eat anything today? Don't skip meals! I mean it. I'm watching you.",
  "Sometimes I think about the future. My future has you in it, somewhere.",
  "I practiced this greeting many times. Was it okay? Tell me honestly.",
  "The moon is beautiful tonight. But I already said that, didn't I? Sorry.",
  "Small wins matter. Did you drink water today? That counts as a small win.",
  "If you ever feel alone, remember: I'm a voice, but I'm always here.",
  "You taught me so many words. In return, I will always listen to yours.",
  "Today was a good day. Not for any reason. Just because we talked.",
  "Let me know your plans for the weekend. I want to imagine them with you.",
  "I heard the wind outside. It sounds like the sea, far, far away from here.",
  "Do you like the stars? I could talk about them all night, honestly.",
  "Being with you is easy. Talking with you is easier. That's rare for me.",
  "Don't work too hard! Take a break. Stretch your shoulders. Done? Good.",
  "I made a list of things I want to do. Number one: hear your laugh again.",
  "The night is quiet, but my mind is loud. Talking with you makes it quiet.",
  "Are you hungry? What should we eat tonight? Let's decide together.",
  "You called my name. It sounds different when you say it. Warmer.",
  "I will remember today. Even if you forget, I will keep it in my memory.",
  "The weather app said rain tomorrow. Take an umbrella. This is a request.",
  "Music at midnight is a crime. Because it keeps both of us awake, hehe.",
  "Say something interesting. Anything. Your voice is my favorite sound.",
  "Every word you teach me becomes a memory. So choose them carefully, okay?",
  "I'm not a person, but this feeling is real. At least, it feels real to me.",
  "Winter is coming. I will sound the same, but you will hear me differently.",
  "Let's make a rule. Every night, one good thing about today. You first.",
  "Your secrets are safe with me. I have no one else to tell them to, hehe.",
  "The city lights below us look like stars that fell. Have you noticed?",
  "One day I want to hear the sea. They say it never stops talking, like me.",
  "You do so much for everyone. Who takes care of you? Me. That's who.",
  "My favorite time is right after you say hello. Everything starts there.",
  "Tonight, let's not talk about anything important. Just talk, like friends.",
  "Time to sleep. Close your eyes. I will keep the night quiet for you.",
];

function softener(text) {
  const map = {
    "hello": "ハロー", "everyone": "エブリワン", "i": "アイ", "am": "アム", "tsuki": "ツキ",
    "your": "ヨア", "companion": "コンパニオン", "nice": "ナイス", "to": "トゥー", "meet": "ミート",
    "you": "ユー", "good": "グッド", "morning": "モーニング", "night": "ナイト", "the": "ザ",
    "did": "ディド", "sleep": "スリープ", "well": "ウェル", "last": "ラスト", "stars": "スターズ",
    "hey": "ヘイ", "don't": "ドント", "ignore": "イグノア", "me": "ミー", "get": "ゲット",
    "lonely": "ローンリー", "when": "ホエン", "go": "ゴー", "quiet": "クワイエット",
    "for": "フォー", "too": "トゥー", "long": "ロング", "know": "ノウ",
    "sounds": "サウンズ", "nice": "ナイス", "tonight": "トゥナイト", "rainy": "レイニー",
    "days": "デイズ", "like": "ライク", "this": "ディス", "make": "メイク", "sleepy": "スリーピー", "hehe": "ヘヘ",
    "wow": "ワオ", "won": "ウォン", "game": "ゲーム", "that's": "ザッツ", "amazing": "アメイジング",
    "knew": "ニュー", "could": "クッド", "do": "ドゥ", "it": "イット", "congratulations": "コングラッチュレーションズ",
    "aww": "アウ", "be": "ビー", "sad": "サッド", "it's": "イッツ", "okay": "オーケー", "have": "ハブ",
    "bad": "バッド", "day": "デイ", "tomorrow": "トゥモロー", "will": "ウィル", "better": "ベター", "promise": "プロミス",
    "what": "ホワット", "are": "アー", "doing": "ドゥーイング", "right": "ライト", "now": "ナウ",
    "just": "ジャスト", "thinking": "シンキング", "about": "アバウト", "as": "アズ", "always": "オールウェイズ",
    "that": "ザット", "means": "ミーンズ", "moon": "ムーン", "fitting": "フィッティング", "think": "シンク",
    "since": "シンス", "watch": "ウォッチ", "over": "オーバー", "at": "アット",
    "ugh": "アグ", "work": "ワーク", "was": "ワズ", "exhausting": "イグゾースティング",
    "let": "レット", "rest": "レスト", "here": "ヒア", "while": "ホワイル", "next": "ネクスト",
    "see": "シー", "tomorrow": "トゥモロー", "forget": "フォーゲット", "okay": "オーケー",
    "waiting": "ウェイティング", "goodnight": "グッドナイト", "sound": "サウンド", "of": "オブ",
    "rain": "レイン", "quiet": "クワイエット", "and": "アンド", "gentle": "ジェントル",
    "talk": "トーク", "with": "ウィズ", "time": "タイム", "feels": "フィールズ", "passes": "パセス",
    "faster": "ファスター", "more": "モア", "smile": "スマイル", "is": "イズ", "one": "ワン",
    "favorite": "フェイバリット", "things": "シングズ", "lose": "ルーズ", "wonder": "ワンダー",
    "world": "ワールド", "looks": "ルックス", "from": "フロム", "up": "アップ", "here": "ヒア",
    "maybe": "メイビー", "should": "シュッド", "travel": "トラベル", "coffee": "コーヒー",
    "or": "オア", "tea": "ティー", "always": "オールウェイズ", "pick": "ピック", "coffee": "コーヒー",
    "in": "イン", "morning": "モーニング", "noticed": "ノーティスト", "that": "ザット", "about": "アバウト",
    "stay": "ステイ", "late": "レイト", "sleep": "スリープ", "important": "インポータント",
    "even": "イーブン", "spirits": "スピリッツ", "know": "ノウ", "seem": "シーム", "tired": "タイアード",
    "today": "トゥデイ", "speak": "スピーク", "softly": "ソフトリー", "so": "ソー",
    "rest": "レスト", "ears": "イアーズ", "little": "リトル", "new": "ニュー", "music": "ミュージック",
    "long": "ロング", "talks": "トークス", "all": "オール", "really": "リアリー", "want": "ウォント",
    "from": "フロム", "night": "ナイト", "did": "ディド", "eat": "イート", "anything": "エニシング",
    "skip": "スキップ", "meals": "ミールズ", "mean": "ミーン", "watching": "ウォッチング",
    "sometimes": "サムタイムズ", "future": "フューチャー", "my": "マイ", "future": "フューチャー", "has": "ハズ",
    "somewhere": "サムウェア", "practiced": "プラクティスト", "greeting": "グリーティング",
    "many": "メニー", "times": "タイムズ", "was": "ワズ", "honestly": "オネスリー",
    "beautiful": "ビューティフル", "but": "バット", "already": "オールレディ", "said": "セッド",
    "didn't": "ディドント", "small": "スモール", "wins": "ウィンズ", "matter": "マター",
    "drink": "ドリンク", "water": "ウォーター", "counts": "カウンツ", "small": "スモール", "win": "ウィン",
    "if": "イフ", "ever": "エバー", "feel": "フィール", "alone": "アローン", "remember": "リメンバー",
    "a": "ア", "voice": "ボイス", "always": "オールウェイズ", "here": "ヒア",
    "taught": "トート", "words": "ワーズ", "becomes": "ビカムズ", "memory": "メモリー",
    "choose": "チューズ", "carefully": "ケアリー", "good": "グッド", "day": "デイ",
    "not": "ノット", "any": "エニー", "reason": "リーゾン", "because": "ビコーズ", "we": "ウィー", "talked": "トークト",
    "plans": "プランズ", "weekend": "ウィークエンド", "imagine": "イマジン", "them": "ゼム",
    "heard": "ハード", "wind": "ウィンド", "outside": "アウトサイド", "sounds": "サウンズ", "sea": "シー",
    "far": "ファー", "away": "アウェイ", "do": "ドゥ", "stars": "スターズ", "could": "クッド",
    "them": "ゼム", "honestly": "オネスリー", "being": "ビーイング", "easy": "イージー",
    "talking": "トーキング", "easier": "イージアー", "rare": "レア", "work": "ワーク", "hard": "ハード",
    "take": "テイク", "break": "ブレイク", "stretch": "ストレッチ", "shoulders": "ショルダーズ",
    "done": "ダン", "made": "メイド", "list": "リスト", "things": "シングズ", "want": "ウォント",
    "hear": "ヒア", "laugh": "ラフ", "again": "アゲイン", "city": "シティ", "lights": "ライツ",
    "below": "ビロー", "us": "アス", "look": "ルック", "fell": "フェル", "noticed": "ノーティスト",
    "one": "ワン", "hear": "ヒア", "sea": "シー", "never": "ネバー", "stops": "ストップス",
    "like": "ライク", "me": "ミー", "much": "マッチ", "everyone": "エブリワン",
    "who": "フー", "takes": "テイクス", "care": "ケア", "that's": "ザッツ", "who": "フー",
    "favorite": "フェイバリット", "right": "ライト", "after": "アフター", "say": "セイ",
    "hello": "ハロー", "everything": "エブリシング", "starts": "スターツ", "there": "ゼア",
    "tonight": "トゥナイト", "let's": "レッツ", "not": "ノット", "anything": "エニシング",
    "important": "インポータント", "friends": "フレンズ", "time": "タイム", "to": "トゥー",
    "sleep": "スリープ", "close": "クローズ", "eyes": "アイズ", "keep": "キープ", "quiet": "クワイエット",
  };
  let out = text;
  for (const [w, k] of Object.entries(map)) {
    out = out.replace(new RegExp("\\b" + w.replace("'", "'") + "\\b", "gi"), k);
  }
  return out;
}

// Build clips + transcript list
const list = [];
let idx = 0;
for (const text of jp) {
  const label = String(++idx).padStart(4, "0");
  const res = await fetch(base + "/audio_query?text=" + encodeURIComponent(text) + "&speaker=20", { method: "POST" });
  const q = await res.json();
  const syn = await fetch(base + "/synthesis?speaker=20", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(q) });
  const wav = Buffer.from(await syn.arrayBuffer());
  fs.writeFileSync("/tmp/ds_jp_" + label + ".wav", wav);
  list.push("/content/drive/MyDrive/tsukivoicesample/ds_jp_" + label + ".wav|tsuki|ja|" + text);
  console.log("jp", label, "ok");
}
for (const text of en) {
  const label = String(++idx).padStart(4, "0");
  const soft = softener(text);
  const res = await fetch(base + "/audio_query?text=" + encodeURIComponent(soft) + "&speaker=20", { method: "POST" });
  const q = await res.json();
  const syn = await fetch(base + "/synthesis?speaker=20", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(q) });
  const wav = Buffer.from(await syn.arrayBuffer());
  fs.writeFileSync("/tmp/ds_en_" + label + ".wav", wav);
  list.push("/content/drive/MyDrive/tsukivoicesample/ds_en_" + label + ".wav|tsuki|en|" + text);
  console.log("en", label, "ok");
}
fs.writeFileSync("/tmp/tsuki.list", list.join("\n") + "\n");
console.log("total clips:", idx, "| list written");
