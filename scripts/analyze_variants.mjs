const fs = await import("fs");
const base = "http://voicevox:50021";
const variants = [
  ["a_spaces_latin", "ハロー、エブリワン！アイ アム Tsuki、ヨア コンパニオン。ナイス トゥー ミート ユー。", 1.26],
  ["b_nospace_latin", "ハロー、エブリワン！アイアムTsuki、ヨアコンパニオン。ナイストゥーミートユー。", 1.26],
  ["c_nospace_katakana", "ハロー、エブリワン！アイアムツキ、ヨアコンパニオン。ナイストゥーミートユー。", 1.26],
];
for (const [name, text, speed] of variants) {
  let res = await fetch(base + "/audio_query?text=" + encodeURIComponent(text) + "&speaker=20", { method: "POST" });
  const q = await res.json();
  q.speedScale = speed;
  res = await fetch(base + "/synthesis?speaker=20", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(q) });
  const wav = Buffer.from(await res.arrayBuffer());
  fs.writeFileSync("/tmp/mx_" + name + ".wav", wav);
  const sr = wav.readUInt32LE(24);
  let pos = 12, dataStart = 44, dataSize = 0;
  while (pos + 8 <= wav.length) {
    const id = wav.toString("ascii", pos, pos + 4);
    const size = wav.readUInt32LE(pos + 4);
    if (id === "data") { dataStart = pos + 8; dataSize = size; break; }
    pos += 8 + size + (size % 2);
  }
  const samples = dataSize / 2;
  const win = Math.floor(sr / 10);
  const rmsList = [];
  for (let s = 0; s + win < samples; s += win) {
    let sum = 0;
    for (let i = 0; i < win; i++) { const v = Math.abs(wav.readInt16LE(dataStart + (s + i) * 2)); sum += v * v; }
    rmsList.push(Math.sqrt(sum / win));
  }
  rmsList.sort((a, b) => a - b);
  const quietRms = Math.round(rmsList[Math.floor(rmsList.length * 0.1)]);
  let noiseSamples = 0, total = 0;
  for (let i = 0; i < samples; i += 7) {
    const v = Math.abs(wav.readInt16LE(dataStart + i * 2));
    total++;
    if (v > 30 && v < 800) noiseSamples++;
  }
  console.log(name, "| dur:", (samples / sr).toFixed(2) + "s | quietRMS:", quietRms, "| noise-band %:", Math.round(100 * noiseSamples / total));
}
