// Molab-only Discord voice channel. Colab and text-chat voice never use this.
const TERMINALS = new Set(['.', '!', '?', '。', '！', '？']);
const CLOSERS = new Set(['"', "'", '”', '’', ')', ']', '}', '」', '』', '）']);
const ABBREVIATIONS = new Set([
  'mr.', 'mrs.', 'ms.', 'dr.', 'prof.', 'sr.', 'jr.', 'st.',
  'vs.', 'etc.', 'e.g.', 'i.e.', 'a.m.', 'p.m.', 'u.s.', 'u.k.',
]);
const CJK = /[\u3040-\u30ff\u3400-\u9fff]/u;
const PCM_BYTES_PER_SECOND = 48000 * 2 * 2;

function sentenceParts(text) {
  const source = String(text || '').trim();
  if (!source) return [];
  const parts = [];
  let start = 0;
  for (let index = 0; index < source.length; index += 1) {
    const ch = source[index];
    if (!TERMINALS.has(ch)) continue;
    if (ch === '.' && /\d/.test(source[index - 1] || '') &&
        /\d/.test(source[index + 1] || '')) continue;
    if (ch === '.' && source[index + 1] === '.') continue;
    if (ch === '.') {
      const prefix = source.slice(start, index + 1);
      const token = prefix.match(/[\w.]+\.$/u)?.[0]?.toLowerCase();
      if (ABBREVIATIONS.has(token)) continue;
      if (source[index + 1] && !/\s/u.test(source[index + 1])) continue;
    }
    let end = index + 1;
    while (TERMINALS.has(source[end])) end += 1;
    while (CLOSERS.has(source[end])) end += 1;
    const sentence = source.slice(start, end).trim();
    if (sentence) parts.push(sentence);
    start = end;
    while (/\s/u.test(source[start] || '') && start < source.length) start += 1;
    index = start - 1;
  }
  const remainder = source.slice(start).trim();
  if (remainder) parts.push(remainder);
  return parts;
}

function splitLongSentence(sentence, maxChars) {
  const parts = [];
  let remaining = sentence;
  const clauseSeparators = ['、', '，', ',', '；', ';', '：', ':'];
  while (remaining.length > maxChars) {
    let cut = -1;
    for (const separator of clauseSeparators) {
      const index = remaining.lastIndexOf(separator, maxChars - 1);
      if (index >= Math.floor(maxChars * 0.35) && index + 1 > cut) cut = index + 1;
    }
    if (cut < 1) cut = remaining.lastIndexOf(' ', maxChars);
    if (cut < 1) cut = maxChars;
    // Never split a UTF-16 surrogate pair (emoji, supplementary Han).
    if (cut < remaining.length && /[\uD800-\uDBFF]/u.test(remaining[cut - 1])) cut -= 1;
    if (cut < 1) cut = maxChars;
    const part = remaining.slice(0, cut).trim();
    if (part) parts.push(part);
    remaining = remaining.slice(cut).trimStart();
  }
  if (remaining) parts.push(remaining);
  return parts;
}

export function splitMolabVoiceSentences(text, maxChars = 280) {
  if (!Number.isInteger(maxChars) || maxChars < 1) throw new RangeError('Invalid sentence limit');
  return sentenceParts(text).flatMap(sentence => splitLongSentence(sentence, maxChars));
}

const wordSegmenter = new Intl.Segmenter('ja', { granularity: 'word' });
export function countMolabWords(text) {
  return [...wordSegmenter.segment(String(text || ''))]
    .filter(part => part.isWordLike).length;
}

function joinAdjacent(left, right) {
  const adjacentJapanese = /[。！？]$/u.test(left) && CJK.test(right[0] || '');
  return left + (adjacentJapanese ? '' : ' ') + right;
}

// Initial policy from the production plan: <=8 short, 9-20 medium, >20 long.
// Short text is combined until speech is substantial without sending a giant
// utterance to the 0.6B model; Japanese also has a character cap.
export function batchMolabVoiceSentences(text, maxChars = 280) {
  const sentences = splitMolabVoiceSentences(text, maxChars);
  const batches = [];
  const maxCombinedChars = Math.min(maxChars, 180);
  for (let index = 0; index < sentences.length;) {
    let batch = sentences[index++];
    let words = countMolabWords(batch);
    if (words <= 8 && batch.length <= maxCombinedChars) {
      while (index < sentences.length) {
        const next = sentences[index];
        const nextWords = countMolabWords(next);
        if (nextWords > 20) break;
        const combined = joinAdjacent(batch, next);
        if (combined.length > maxCombinedChars || words + nextWords > 20) break;
        batch = combined;
        words += nextWords;
        index += 1;
        if (words >= 9 || batch.length >= 70) break;
      }
    }
    batches.push(batch);
  }
  return batches;
}

function predictedDuration(text, charactersPerSecond) {
  return Math.max(0.6, text.length / (CJK.test(text) ? Math.min(12, charactersPerSecond) : charactersPerSecond));
}

function audioInfo(value, fallbackDuration, elapsedMs) {
  const object = value && typeof value === 'object' && !Buffer.isBuffer(value)
    && 'audio' in value ? value : { audio: value };
  const audio = object.audio;
  if (audio == null) throw new Error('Molab returned no audio');
  const inferred = Buffer.isBuffer(audio) && audio.length >= PCM_BYTES_PER_SECOND / 10
    && audio.length % 4 === 0 ? audio.length / PCM_BYTES_PER_SECOND : 0;
  const seconds = Number(object.audioDurationSeconds) > 0
    ? Number(object.audioDurationSeconds) : inferred || fallbackDuration;
  // A network round trip is not model-generation time. If no model timing
  // header exists, report generation as unmeasured instead of fabricating it.
  const generationMs = Number(object.generationMs) > 0 ? Number(object.generationMs) : null;
  return {
    audio,
    audioDurationSeconds: seconds,
    generationMs,
    requestMs: Number(object.requestMs) > 0 ? Number(object.requestMs) : elapsedMs,
  };
}

// Start exactly the first batch before prefetching. After it finishes,
// schedule at most 3 simultaneous requests to the SAME model. Count only
// completed audio as buffered; in-flight requests do not guarantee audio.
// High buffer pauses new work except the next missing batch, so a long A1
// never starves A2. Timed re-evaluation crosses the 15s/8s/3s thresholds
// while Discord is still playing a long batch.
export async function playMolabSentencePipeline({
  text, synthesize, play, maxConcurrent = 3, maxChars = 280,
  onProgress = () => {}, now = () => Date.now(),
}) {
  if (!Number.isInteger(maxConcurrent) || maxConcurrent < 1 || maxConcurrent > 3) {
    throw new RangeError('Molab synthesis concurrency must be 1..3');
  }
  if (typeof synthesize !== 'function' || typeof play !== 'function') {
    throw new TypeError('Molab synthesize and play callbacks are required');
  }
  const batches = batchMolabVoiceSentences(text, maxChars);
  const sentences = splitMolabVoiceSentences(text, maxChars).length;
  if (!batches.length) return { sentences: 0, batches: 0, played: 0, maxActiveJobs: 0, rtf: null, gapSeconds: 0 };

  const pending = new Map();
  let nextToStart = 0;
  let playingIndex = -1;
  let playing = null;
  let playbackStarted = false;
  let activeJobs = 0;
  let maxActiveJobs = 0;
  let charactersPerSecond = 14; // initial guess, replaced by measured WAV length
  let rtf = null; // no fabricated model-speed measurement
  let gapSeconds = 0;
  let wakeTimer = null;
  let stopped = false;
  let jobFailure = false;

  function completedBuffer() {
    let seconds = playing
      ? Math.max(0, playing.duration - (now() - playing.startedAt) / 1000) : 0;
    for (const entry of pending.values()) {
      if (entry.value?.audio != null) seconds += entry.value.audioDurationSeconds;
    }
    return seconds;
  }

  function schedule() {
    if (wakeTimer != null) clearTimeout(wakeTimer);
    wakeTimer = null;
    if (!playbackStarted || stopped || jobFailure) return;

    const buffer = completedBuffer();
    // Initial thresholds are adjustable after real Molab benchmarks.
    const target = buffer > 15 ? 0 : buffer >= 8 ? 3 : buffer >= 3 ? 2 : 1;
    const nextRequired = playingIndex + 1;
    const nextIsMissing = nextRequired < batches.length && !pending.has(nextRequired);
    let desired = Math.min(maxConcurrent, target);
    if (nextIsMissing) desired = Math.max(1, desired);
    // Completed audio must not grow without bound. Allow one immediate next
    // request for continuity even when A1 has >15s of playback remaining.
    while (activeJobs < desired && pending.size < maxConcurrent &&
           nextToStart < batches.length) {
      if (buffer > 15 && !nextIsMissing) break;
      start();
      if (buffer > 15) break;
    }
    onProgress({
      stage: 'scheduler', bufferSeconds: +buffer.toFixed(3),
      targetActiveJobs: desired, activeJobs, pendingBatches: pending.size,
      rtf, nextBatch: nextToStart + 1, total: batches.length,
    });

    if (playing) {
      const futureReady = [...pending.values()]
        .reduce((sum, entry) => sum + (entry.value?.audioDurationSeconds || 0), 0);
      const remaining = Math.max(0, playing.duration - (now() - playing.startedAt) / 1000);
      const threshold = buffer > 15 ? 15 : buffer > 8 ? 8 : buffer > 3 ? 3 : null;
      if (threshold != null && remaining > 0 && futureReady < threshold) {
        const delayMs = Math.max(50, Math.ceil((buffer - threshold) * 1000));
        wakeTimer = setTimeout(schedule, delayMs);
        wakeTimer.unref?.();
      }
    }
  }

  function start() {
    const index = nextToStart++;
    const batch = batches[index];
    const entry = { value: null };
    pending.set(index, entry);
    activeJobs += 1;
    maxActiveJobs = Math.max(maxActiveJobs, activeJobs);
    const estimate = predictedDuration(batch, charactersPerSecond);
    const activeAtStart = activeJobs;
    onProgress({
      stage: 'synthesizing', index: index + 1, total: batches.length,
      text: batch, words: countMolabWords(batch), chars: batch.length,
      activeJobs, estimatedAudioSeconds: +estimate.toFixed(3),
      estimatedGenerationSeconds: rtf == null ? null : +(estimate * rtf).toFixed(3),
    });
    const started = now();
    entry.promise = Promise.resolve()
      .then(() => synthesize(batch, index, { activeJobs: activeAtStart }))
      .then(value => {
        entry.value = audioInfo(value, estimate, Math.max(1, now() - started));
        activeJobs -= 1;
        const sampleRtf = entry.value.generationMs == null ? null :
          entry.value.generationMs / (entry.value.audioDurationSeconds * 1000);
        if (Number.isFinite(sampleRtf) && sampleRtf > 0) {
          rtf = rtf == null ? sampleRtf : rtf * 0.7 + sampleRtf * 0.3;
        }
        const cps = batch.length / entry.value.audioDurationSeconds;
        if (Number.isFinite(cps) && cps >= 3 && cps <= 40) {
          charactersPerSecond = charactersPerSecond * 0.7 + cps * 0.3;
        }
        onProgress({
          stage: 'synthesized', index: index + 1, total: batches.length,
          words: countMolabWords(batch), chars: batch.length,
          generationMs: entry.value.generationMs, requestMs: entry.value.requestMs,
          audioDurationSeconds: +entry.value.audioDurationSeconds.toFixed(3),
          rtf: sampleRtf == null ? null : +sampleRtf.toFixed(3),
          rollingRtf: rtf == null ? null : +rtf.toFixed(3),
          activeJobs, peakActiveJobs: maxActiveJobs,
        });
        schedule();
        return entry.value;
      }, error => {
        activeJobs -= 1;
        entry.error = error;
        jobFailure = true;
        return { error }; // Never leave a later prefetched rejection unhandled.
      });
  }

  start();
  let played = 0;
  try {
    for (let index = 0; index < batches.length; index += 1) {
      const entry = pending.get(index);
      if (!entry) throw new Error('Molab scheduler lost ordered batch ' + (index + 1));
      const waitingSince = now();
      const value = await entry.promise;
      if (value.error) throw value.error;
      if (played > 0 && now() > waitingSince) {
        const gap = (now() - waitingSince) / 1000;
        gapSeconds += gap;
        onProgress({ stage: 'gap', index: index + 1, total: batches.length, gapSeconds: +gap.toFixed(3) });
      }
      pending.delete(index);
      playingIndex = index;
      playing = { duration: value.audioDurationSeconds, startedAt: now() };
      playbackStarted = true;
      // Start A2.. before awaiting A1 playback. No reordering.
      schedule();
      onProgress({
        stage: 'playing', index: index + 1, total: batches.length,
        audioDurationSeconds: +value.audioDurationSeconds.toFixed(3),
        bufferSeconds: +completedBuffer().toFixed(3), activeJobs,
      });
      await play(value.audio, index);
      played += 1;
      playing = null;
      onProgress({ stage: 'played', index: index + 1, total: batches.length, activeJobs });
      schedule();
    }
  } finally {
    stopped = true;
    if (wakeTimer != null) clearTimeout(wakeTimer);
    // Already dispatched generations may finish, but their rejections are handled.
  }
  return {
    sentences, batches: batches.length, played, maxActiveJobs,
    rtf: rtf == null ? null : +rtf.toFixed(3),
    gapSeconds: +gapSeconds.toFixed(3),
  };
}
