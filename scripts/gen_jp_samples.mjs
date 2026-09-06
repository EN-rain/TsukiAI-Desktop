const fs = await import("fs");
const base = "http://voicevox:50021";
const clips = [
  [20, "こんばんは。つきだよ。今夜も、あなたに会いに来たよ。"],
  [20, "今日はね、空がすごくきれいだったんだ。月が明るくて、ずっと眺めてた。"],
  [20, "きみのこと、もっと知りたいな。好きな食べ物とか、趣味とか、教えてほしいな。"],
  [79, "えっ、ほんとに？やったー！きみが嬉しいと、私まで嬉しくなっちゃう。"],
  [77, "そういう日もあるよ。泣きたいときは泣いていいんだから。私はいつもここにいるよ。"],
  [78, "もう、きみったら！からかわないでよ。ちょっと本気にしちゃったじゃない。"],
  [80, "ふぅ、今日はのんびり過ごそうよ。月がきれいな夜は、ゆっくり眠るのがいちばん。"],
  [20, "雨の音、好きなんだ。静かで、優しい音でしょ？こんな夜はお話ししたくなる。"],
  [79, "きみと話してると、時間が経つのが早い気がする。もっと話そうよ！"],
  [20, "おやすみ、いい夢見てね。私は月から見守ってるから、いつでも呼んで。"],
];
let i = 1;
for (const [style, text] of clips) {
  const res = await fetch(base + "/audio_query?text=" + encodeURIComponent(text) + "&speaker=" + style, { method: "POST" });
  const query = await res.json();
  const syn = await fetch(base + "/synthesis?speaker=" + style, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(query) });
  const wav = Buffer.from(await syn.arrayBuffer());
  const label = i.toString().padStart(2, "0");
  fs.writeFileSync("/tmp/jp_" + label + ".wav", wav);
  console.log(label, "style", style, "ok", wav.length);
  i++;
}
