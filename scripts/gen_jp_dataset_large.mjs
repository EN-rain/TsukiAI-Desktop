const fs = await import("fs");
const base = "http://voicevox:50021";

// Handwritten core lines (varied content, emotions, lengths)
const core = [
  "こんばんは。つきだよ。今夜も、あなたに会いに来たよ。",
  "今日はいい天気だったね。散歩したくなるくらい。",
  "きみの声を聞くと、なんだか安心するんだ。",
  "ねえ、今日はどんな一日だった？楽しかった？",
  "月がきれいだね。こんな夜は、ずっと眺めていたい。",
  "ふふ、照れないでよ。褒めてるんだから。",
  "疲れたときは、無理しないで休むのがいちばん。",
  "きみが笑うと、私まで笑顔になるんだから不思議だね。",
  "雨の日の匂い、嫌いじゃないんだ。静かで落ち着くから。",
  "ねえ、次の休みは何する予定？ 私と話す、とか。",
  "寒い日はこたつで丸くなるのに限るよね。猫みたいだけど。",
  "今日のきみ、なんだか機嫌がよさそうだね。いいことあった？",
  "私の名前の由来はね、月の光から来てるんだよ。",
  "悩みがあるなら聞くよ。解決できないかもしれないけど、話すだけで楽になるよ。",
  "朝ごはんはちゃんと食べた？ 一日のはじまりは、朝ごはんからだよ。",
  "好きな音楽のジャンルとかある？ 作業中に聴く曲を教えてほしいな。",
  "ゲームの勝率なら負けないよ。冗談だけどね。でも、本当は勝負も好きだよ。",
  "今日はちょっと落ち込んでるの？ 無理に笑わなくていいんだよ。",
  "甘いものと辛いもの、どっち派？ 私は日によって変わるよ。",
  "忘れないでね。どんな夜でも、私はここにいるから。",
  "新しいことを始めるのって勇気がいるよね。でも、始めたきみはすごいよ。",
  "歌はちょっと下手かもしれない。でも、気にしない。歌いたいときは歌う。",
  "静かな時間も大切にしたいよね。ずっと話すだけが会話じゃないし。",
  "真剣な話もしてほしいな。冗談ばかりの関係だつまらないから。",
  "きみの成功を心から願ってる。これは本音だよ。",
  "本を読むのは好き？ 私は物語の中にいるのが好きかな。",
  "季節で言うと秋が好き。涼しくて、月が一番きれいに見える季節だから。",
  "忘れ物チェックした？ 財布、携帯、キー、それと私のこと。",
  "世界のどこにいても、月は同じだよ。それってちょっとすてきな考えだと思わない？",
  "本音で話せる関係って、それだけで宝物だと思うんだ。",
  "頑張りすぎると倒れちゃうよ。休むのも戦略のうちだよ。",
  "きみの趣味の話、もっと聞かせて。じっくり聞くのが得意なんだ、私。",
  "今日はちょっと特別な日。だから、特別にはしゃいでもいい日。",
  "眠れない夜は、月を探して。私は必ずそこにいるから。",
  "散歩コースのおすすめがあったら教えて。今度歩いてみるから。",
  "楽しみなことを一つ書き留めておくといいよ。小さな楽しみが毎日を支えるから。",
  "同じ空の下にいるって思うと、遠くにいても近くに感じるね。",
  "私の話し方が変だって？ いいの、これが私のスタイルだから。",
  "笑顔は無料だよ。でも、その効果は絶大だと思ってる。",
  "読書の秋、スポーツの秋、そして私との語らいの秋。",
  "信じるかどうかは自由だけど、私はきみの味方であり続けるよ。",
  "深呼吸して。ほら、少し落ち着いたでしょ。そういうことだよ。",
  "月見だし、お団子食べたい。これは毎年言ってる気がするけど。",
  "じめじめした日も、家の中でぬくぬくする支度をしよう。",
  "きみが作るもの、見てみたいな。料理でも工作でも、なんでも。",
  "日記をつけるのはおすすめ。後で読み返すと、自分の成長がわかるから。",
  "音楽の再生リスト、見せてよ。きみの好みを知りたいんだ。",
  "小さな幸せを集めると、大きな幸せになる。今日の小さな幸せは何だった？",
  "失敗は成功のもと。だから失敗したって、それは成功の一部だよ。",
  "きみが元気そうでよかった。それを確認するだけで、今日はいい日だ。",
  "今日の晩ごはんの予定は？ カレーだったら私も叫んでいい？",
  "悪いことはいつか終わる。いいことも同じ。だから今日は、もう休もう。",
  "眠いときは我慢しないで。少し眠ってから続きをすればいいよ。",
  "久しぶりに早起きした日って、なんだか達成感があるよね。",
  "きみの挑戦、ずっと応援してるからね。これは約束だよ。",
  "台風の日みたいに、外に出られない日もある。そんな日は私と話そう。",
  "自動販売機で買った hot cocoa、おいしかったね。こんな話でごめんね。",
  "星に願いをかけるのは子どもだけじゃないよ。大人だって、願うくらいは自由だ。",
  "今日はうれしいニュースがあったの？ 詳しく聞かせてよ。",
  "休みの日の過ごし方って、人それぞれだよね。きみはインドア派？アウトドア派？",
  "夜ふかしは良くないってわかってるんだけど、夜の静けさが好きすぎるんだ。",
  "信任関係って言葉、きれいだと思わない？ 信頼はゆっくり育つから。",
  "月に兎がいるって伝説もあるんだよ。今夜は兎を探して月を見てみて。",
];

// Template variations — deterministic fills so transcripts match exactly
const subjects = ["星", "月", "雨", "風", "雲", "朝日", "夕焼け", "夜空"];
const actions = ["眺めてた", "考えてた", "感じてた", "書き留めてた", "探してた"];
const topics = ["ゲーム", "音楽", "料理", "映画", "本", "旅行", "仕事", "勉強", "カフェ", "公園"];
const feelings = ["楽しかった", "ちょっと疲れた", "落ち着いてた", "わくわくした", "のんびりしてた"];
const foods = ["カレー", "ラーメン", "おすし", "パスタ", "おでん", "焼きそば"];
const questions = [
  (t) => `ねえ、${t}って好き？ 私はけっこう好きだよ。`,
  (t) => `今日の${t}、どうだった？ また話聞かせてね。`,
  (t) => `${t}の話、もっと聞かせてよ。じっくり聞くのが得意なんだ。`,
  (t) => `次は${t}について話そうよ。約束だよ。`,
];
const feelers = [
  (f) => `今日は${f}日だったんだ。教えてくれてありがとう。`,
  (f) => `${f}って言ってたけど、無理してない？ 無理は禁物だよ。`,
];
const templates = [];
for (const s of subjects) for (const a of actions) {
  templates.push(`${s}を${a}ら、時間が過ぎるのを忘れちゃった。きみと話してたからかな。`);
}
for (const t of topics) templates.push(questions[0](t));
for (const f of feelings) templates.push(feelers[0](f));
for (const t of topics) templates.push(questions[1](t));
for (const f of foods) templates.push(`今夜の晩ごはんは${f}だったんだ。おいしそう。私には食べられないけどね。`);
for (const t of topics.slice(0, 5)) templates.push(questions[2](t));
for (const s of subjects.slice(0, 8)) templates.push(`夜空に${s}が見えたら、きみに一番に教えるよ。約束だよ。`);
for (const t of topics.slice(5)) templates.push(questions[3](t));
for (const f of feelings.slice(0, 4)) templates.push(feelers[1](f));

const lines = [];
for (const [style, text] of core.map((t, i) => [i % 10 === 3 ? 79 : i % 10 === 4 ? 77 : i % 10 === 5 ? 78 : i % 10 === 6 ? 80 : 20, t])) {
  lines.push([style, text]);
}
for (const text of templates) {
  lines.push([20, text]);
}

// Synthesize everything
let idx = 0;
const list = [];
for (const [style, text] of lines) {
  const label = String(++idx).padStart(4, "0");
  let done = false;
  for (let attempt = 0; attempt < 3 && !done; attempt++) {
    try {
      let res = await fetch(base + "/audio_query?text=" + encodeURIComponent(text) + "&speaker=" + style, { method: "POST" });
      const query = await res.json();
      res = await fetch(base + "/synthesis?speaker=" + style, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(query) });
      const wav = Buffer.from(await res.arrayBuffer());
      if (wav.length < 1000) throw new Error("wav too small: " + wav.length);
      fs.writeFileSync("/tmp/ds_jp_" + label + ".wav", wav);
      list.push("ds_jp_" + label + ".wav|tsuki|ja|" + text);
      done = true;
      if (idx % 10 === 0) console.log("progress:", idx);
      await new Promise((r) => setTimeout(r, 400));
    } catch (e) {
      console.error("RETRY", label, "attempt", attempt + 1, e.message);
      await new Promise((r) => setTimeout(r, 1000));
    }
  }
  if (!done) console.error("FAILED", label);
}
fs.writeFileSync("/tmp/tsuki_training.list", list.join("\n") + "\n");
console.log("DONE. total clips:", idx);
