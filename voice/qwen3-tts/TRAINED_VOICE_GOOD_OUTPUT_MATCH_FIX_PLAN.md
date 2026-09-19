# TsukiAI Qwen3-TTS — Good-Output Match Recovery Plan

Status: **Authoritative focused fix plan for the remaining voice-match problem**

Date: 2026-09-17

Project root: `D:\New folder (2)\TsukiAI 1.0`

This plan assumes the following requirement is fixed and must not be reinterpreted:

- the training corpus remains Japanese-only;
- the final production system uses the trained `tsukihime` custom voice model directly;
- no runtime voice cloning is used in the final trained-custom production path;
- a **separate isolated Azure zero-shot benchmark VPS** is explicitly authorized as a parallel experiment and may use Qwen zero-shot/full-ICL voice cloning;
- the zero-shot VPS must never silently replace or overwrite the trained-custom production service;
- no English training dataset is required for this recovery attempt;
- the three supplied good English outputs are the target behavior that must be recovered.

The supplied Hugging Face/Qwen demo screenshot is the reference for the **zero-shot benchmark workflow only**. It is not TsukiAI UI and it is not the trained-custom production architecture.

---

# 1. Target behavior

The repaired trained model must reproduce the voice family demonstrated by these exact files:

```text
C:\Users\LENOVO\Downloads\goodoutputtts.ogg
C:\Users\LENOVO\Downloads\voice-message (1).ogg
C:\Users\LENOVO\Downloads\voice-message (2).ogg
```

Known SHA-256 values:

```text
goodoutputtts.ogg
62465ca6357c3bbf7bff7d5f83d5f153407fe380493fef6e8e21977d1f670b82

voice-message (1).ogg
8f256177569e9c90da5402d8fd9fc058095f1e115fe10f06bd1d966ab54dcc18

voice-message (2).ogg
97bb657bb99585496a6acc43bb0f9855543503268f438a7d1341352ab8caeacc
```

`goodoutputtts.ogg` is byte-identical to the older `voice-message.ogg` from the known-good period, so it is a valid historical target and not a newly generated comparison artifact.

The target is not merely "Japanese accent." It is the specific repeatable Tsuki English voice behavior present across all three files:

- same Tsuki voice family;
- Japanese-accented English;
- similar pitch register;
- similar articulation timing;
- similar phrase pacing;
- similar spectral/timbre character;
- stable harmonic voice quality;
- no generic native/American Qwen English character.

---

# 2. Verified current state

The catastrophic training corruption has already been repaired.

Current v19 properties:

```text
training samples: 450
holdout samples: 0
speaker: tsukihime
checkpoint: checkpoint-epoch-0
learning rate: 2e-6
epochs: 1
alignment: repaired PR #278-compatible contract
runtime mode: trained-custom
runtime reference audio: none
runtime voice clone: none
```

The final v19 model is acoustically healthy and passes EOS/raw-WAV gates.

Therefore the remaining problem is no longer the old noisy/aperiodic checkpoint failure.

The remaining problem is **voice identity / pronunciation / prosody mismatch relative to the historical good outputs**.

---

# 3. What the three good outputs actually prove

Live repository history and file timestamps materially clarified the provenance of the three historical good files.

Verified committed Qwen history:

```text
789fd7c
2026-09-13 10:51:04 +0800
"replace OpenVoice with Qwen3-TTS voice cloning"
```

At that commit, and still in the September 14 committed service, `voice/qwen3-tts/api.py` explicitly used:

```text
Qwen/Qwen3-TTS-12Hz-0.6B-Base
+
C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3 equivalent deployed reference
+
exact Japanese reference transcript
+
create_voice_clone_prompt(... x_vector_only_mode=False)
+
generate_voice_clone(...)
+
full ICL
```

The historical good file timestamps are:

```text
voice-message.ogg
2026-09-14 23:04:19
SHA-256 = 62465ca6357c3bbf7bff7d5f83d5f153407fe380493fef6e8e21977d1f670b82

goodoutputtts.ogg
byte-identical to voice-message.ogg

voice-message (1).ogg
2026-09-16 12:57:29

voice-message (2).ogg
2026-09-16 12:57:31
```

The current working-tree conversion of `voice/qwen3-tts/api.py` to trained-custom was modified later:

```text
2026-09-16 17:21:22
```

and that trained-custom conversion is not present in the historical committed Git revision that generated the earlier runtime path.

Therefore the strongest supported provenance is:

```text
the three historical good English outputs belong to the
Qwen3-TTS 0.6B Base full-ICL / zero-shot reference-conditioned era,
not to the later repaired trained-custom v19 path.
```

This means the three good files prove:

```text
Japanese Tsuki reference audio + exact Japanese transcript
+
Qwen3-TTS 0.6B Base full ICL
CAN produce
the desired English Tsuki/Japanese-accent behavior.
```

They do **not** prove that a Japanese-only trained-custom checkpoint previously reproduced those exact outputs.

The user requirement still remains that the trained-custom recovery track must not add an English training dataset. That requirement stands independently of historical-output provenance.

Practical consequence:

- the zero-shot/full-ICL track is the most direct historical reproduction track;
- the trained-custom track is a separate attempt to approximate or recover the same target voice without runtime reference conditioning;
- results from the two paths must not be conflated.

---

# 4. Good-output acoustic cluster

Using the same analysis method across all three good outputs:

| File | F0 median | Periodicity | ZCR | Spectral centroid |
| --- | ---: | ---: | ---: | ---: |
| `goodoutputtts.ogg` | ~279.2 Hz | ~0.973 | ~0.0500 | ~1471 Hz |
| `voice-message (1).ogg` | ~276.9 Hz | ~0.958 | ~0.0542 | ~1693 Hz |
| `voice-message (2).ogg` | ~282.2 Hz | ~0.948 | ~0.0594 | ~1690 Hz |

The three good files cluster tightly around the same pitch family:

```text
276.9 Hz
279.2 Hz
282.2 Hz
```

and all retain strong harmonic periodicity:

```text
0.948 – 0.973
```

This is evidence of a repeatable historical voice configuration, not a one-off stochastic lucky sample.

---

# 5. v19 is healthy but not the same voice behavior

Equivalent v19 exact-target outputs:

| Mode | F0 median | Periodicity | ZCR | Spectral centroid |
| --- | ---: | ---: | ---: | ---: |
| English | ~273.2 Hz | ~0.967 | ~0.0521 | ~1776 Hz |
| Auto | ~268.2 Hz | ~0.962 | ~0.0542 | ~1879 Hz |
| Japanese | ~277.8 Hz | ~0.957 | ~0.0521 | ~1842 Hz |

This means:

- harmonic quality is now healthy;
- basic pitch range is close;
- the old ~0.3-periodicity corruption is gone;
- but the voice is spectrally brighter and the English delivery is different.

The remaining mismatch cannot be solved by merely checking periodicity or average pitch.

---

# 6. Exact same-transcript evidence

For the exact three-sentence English target in `goodoutputtts.ogg`, v19 Japanese-conditioned output speaks the same text with substantially different articulation timing.

Sentence-by-sentence spoken durations:

| Sentence | Good output | v19 Japanese | Ratio |
| --- | ---: | ---: | ---: |
| 1 | ~4.52 s | ~3.72 s | ~82.3% |
| 2 | ~3.60 s | ~2.82 s | ~78.3% |
| 3 | ~4.54 s | ~3.56 s | ~78.4% |

Matched-word timing shows the same pattern.

Examples:

```text
"sentence"
good: ~0.68 s
v19:  ~0.44 s

"custom"
good: ~0.38 s
v19:  ~0.26 s

"Collab"
good: ~0.54 s
v19:  ~0.26 s
```

This is not a claim that total duration itself causes the mismatch.

It proves that the model's articulation, vowel/consonant timing, and cadence differ materially from the good-output voice.

---

# 7. Prosody mismatch

After time-normalizing corresponding sentences, F0 contour similarity between the good output and v19 Japanese-conditioned output is only moderate:

```text
sentence 1: ~0.677 correlation
sentence 2: ~0.200 correlation
sentence 3: ~0.628 correlation
```

Energy-contour correlations are also weak:

```text
sentence 1: ~0.447
sentence 2: ~0.244
sentence 3: ~0.378
```

Therefore:

```text
similar average F0 != same prosody
```

The current model reaches approximately the correct pitch register but does not reproduce the historical pitch movement, emphasis, and phrasing.

---

# 8. Japanese language conditioning is not the original accent mechanism

Do not treat:

```python
language="Japanese"
```

as equivalent to:

```text
English spoken with Tsuki's historical Japanese accent
```

Qwen's generation source shows that this parameter directly selects a language codec ID.

Conceptually:

```text
language="English"
-> English codec language ID

language="Japanese"
-> Japanese codec language ID

language="Auto"
-> no explicit language codec ID
```

The v19 checkpoint has:

```text
spk_is_dialect["tsukihime"] = false
```

So there is no special dialect-routing rule that maps Tsukihime to a Japanese-accented-English mode.

Japanese conditioning is only an A/B diagnostic. It must not be assumed to reproduce the historical good accent.

The exact target demonstrates this:

```text
good output recognition:
"Azure"

v19 Japanese-conditioned recognition:
"agile"
```

That is a different phonetic realization, not simply a stronger accent.

---

# 9. Verified speaker-reference behavior: fixed reference ID 0001

A live inspection of the Kaggle notebook and Qwen dataset code corrected the earlier speaker-anchor hypothesis.

Qwen SFT does compute the custom speaker embedding from `ref_mels`:

```python
speaker_embedding = model.speaker_encoder(ref_mels).detach()

if target_speaker_embedding is None:
    target_speaker_embedding = speaker_embedding
```

and saves element `[0]` into custom speaker ID `3000`:

```python
state_dict['talker.model.codec_embedding.weight'][3000] = (
    target_speaker_embedding[0]
)
```

However, in this Tsuki notebook **every training row uses the same fixed reference audio**.

The live notebook constructs:

```python
reference = next(row for row in rows if row["id"] == "0001")
shutil.copy2(dataset_root / reference["audio_file"], DATA / "ref.wav")

raw_rows = [
    {
        "audio": str(dataset_root / row["audio_file"]),
        "text": row["text"],
        "ref_audio": str(DATA / "ref.wav"),
    }
    for row in train
]
```

Qwen's `TTSDataset.__getitem__` then reads `item["ref_audio"]` and converts that fixed file to `ref_mels`.

Therefore all batches use the same speaker-reference recording:

```text
mochiko_0001.wav
SHA-256: ce6a38a0613a36a18a96362aabf672734f93d1377b9b9c366fb94aba7978c918
duration: 7.648 s
sample rate: 24000 Hz
channels: 1
```

Reference transcript:

```text
朝起きてカーテンを開けると、やわらかな日差しが部屋いっぱいに広がり、眠っていた気持ちまで明るくなった。
```

This means DataLoader shuffle order does **not** change the saved speaker token in this training recipe.

---

# 10. Correction: v19 is not anchored to mochiko_0043

The previous inference that deterministic seed 42 caused `mochiko_0043.wav` to become speaker ID 3000 was incorrect.

Seed 42 does determine which **training sample** appears first, but the speaker encoder does not use that sample's `audio` field. It uses the shared `ref_audio` field, which always points to `DATA/ref.wav` copied from `mochiko_0001.wav`.

Therefore:

```text
first shuffled training row
!=
speaker-reference audio
```

and:

```text
v19 speaker token 3000
-> speaker_encoder(mochiko_0001.wav)
```

The historical 425/25 notebook also uses the same fixed reference ID `0001`.

The speaker-anchor/shuffle theory is therefore **rejected for the current and historical notebook recipes** unless later evidence shows a different historical notebook variant.

---

# 11. Verified training-state differences between historical recipe and v19

Both the older 425/25 notebook and v19 use the fixed `mochiko_0001.wav` speaker reference.

The important verified differences are instead:

| Variable | Older notebook | Repaired v19 |
| --- | --- | --- |
| Training rows | 425 | 450 |
| Holdout | 25 | 0 |
| Learning rate | `2e-5` | `2e-6` |
| Epochs | 3 | 1 |
| Qwen source | base commit `022e286...` | PR #278 repair `09364dcc...` |
| Talker alignment | old/broken double-shift path | repaired alignment |
| DataLoader seed | not explicitly seeded | explicitly seeded 42 |
| Speaker reference | `mochiko_0001.wav` | `mochiko_0001.wav` |

The alignment change is required because the old recipe produced catastrophic checkpoint failures. It must not be reverted merely to mimic historical behavior.

The remaining controlled question is whether the repaired v19 checkpoint is **undertrained** or otherwise differs in training state from the historical good voice.

---

# 12. Highest-priority fix strategy: repaired epoch-2 A/B

Because the speaker-reference variable is fixed across old and new notebooks, the next experiment is a controlled repaired training-state A/B.

Keep constant:

```text
450 Japanese samples
0 holdout
speaker reference = mochiko_0001.wav
learning rate = 2e-6
PR #278 alignment repair
batch size = 2
gradient accumulation = 4
weight decay = 0.01
gradient clip = 1.0
seed = 42
speaker = tsukihime
```

Change only:

```text
epoch count: 1 -> 2
```

The experimental Kaggle kernel is:

```text
actual Kaggle slug:
edriannieves/tsuki-qwen3-tts-0-6b-epoch2-a-b

notebook experiment label:
edriannieves/tsuki-qwen3-tts-0-6b-epoch2-ab
```

Kaggle normalized the slug during creation. Record both names and do not treat the internal label as the canonical Kaggle URL.

The kernel is private and must not overwrite the existing v19 kernel or production artifact.

---

# 13. Epoch-2 experiment acceptance matrix

The 2-epoch run should produce:

```text
checkpoint-epoch-0
checkpoint-epoch-1
```

Compare them directly.

For each checkpoint, preserve/generate:

```text
exact Japanese target
exact English target with English conditioning
exact English target with Auto conditioning
exact English target with Japanese conditioning
the standard Japanese evaluation prompt set
```

Compare both checkpoints against all three historical good English files.

The first pass may use the notebook's deterministic evaluation seeds. Any checkpoint that appears materially better must then be repeated at least five times with recorded seeds before human approval.

Do not select epoch 2 because its training loss is lower. Select only on generated voice quality and stability.

---

# 14. Current v19 training evidence

The live Kaggle v19 run records:

```text
GPU: Nvidia Tesla T4
qwen_tts: 0.1.1
torch: 2.10.0+cu128
transformers: 4.57.3
accelerate: 1.12.0
CUDA: 12.8
attention: sdpa
dtype: bfloat16
training time for one epoch: 332.653 s
```

Observed logged loss remains noisy but trends downward late in epoch 1:

```text
step 0:   2.5799
step 80:  2.4806
step 130: 2.3726
step 170: 2.2740
step 220: 2.3284
```

This does not prove epoch 2 will sound better, but it makes a second repaired epoch a justified controlled experiment.

---

# 15. Speaker-token experiments are now lower priority

Do not spend the next Kaggle run sweeping arbitrary speaker anchors, mean embeddings, or medoids.

The live notebook proves both the older and repaired recipes intentionally use the same fixed `mochiko_0001.wav` reference for every training row.

A speaker-token experiment is only justified later if there is a separate, explicit hypothesis such as:

```text
"Would a different fixed Japanese reference recording improve cross-lingual behavior?"
```

That would be a new experiment, not a recovery of a shuffle-induced difference.

---

# 16. Exact evaluation texts

Every candidate must generate the exact historical target texts.

## 16.1 One-sentence target

Use the exact transcript associated with:

```text
voice-message (1).ogg
```

Known recognition/reference wording:

```text
This is the one-sentence Azure and Collab voice test.
```

Use the original application text if it exists in logs/source and differs in punctuation/casing. Prefer the actual original request text over ASR reconstruction.

## 16.2 Two-sentence target

Use the exact original text associated with:

```text
voice-message (2).ogg
```

Known recognition/reference wording:

```text
This is the two-sentence Azure and Collab voice test. The second sentence confirms that longer text is also being synthesized.
```

Again, use the original source text if recoverable rather than relying on ASR spelling.

## 16.3 Three-sentence target

Use the user-locked exact text:

```text
This is the three sentence Azure and the Colab voice test. The custom domain is carrying the request to Colab. The Discord bridge is sending the result as a real voice message.
```

Reference:

```text
C:\Users\LENOVO\Downloads\goodoutputtts.ogg
```

---

# 17. Language-conditioning matrix

For each **checkpoint candidate**, generate the three target texts under:

```text
English
Auto
Japanese
```

This is a controlled diagnostic matrix.

Do not declare `Japanese` the production mode in advance.

Select the final language policy based on which combination of:

```text
checkpoint / training state
+
language conditioning
```

best reproduces the three historical good files.

---

# 18. Repetition requirement

Qwen generation is stochastic.

The notebook's first checkpoint comparison may use its deterministic evaluation seeds to cheaply identify whether epoch 2 moves in the correct direction.

Any checkpoint that appears materially better must then be tested with at least:

```text
5 seeds / repetitions
x
3 target texts
x
3 language modes
```

Do not approve a checkpoint based on one lucky output.

Record the exact sampling seed for every WAV.

---

# 19. Freeze generation parameters during checkpoint testing

During the epoch/checkpoint experiment, keep generation parameters fixed.

Current baseline values observed in Qwen:

```text
do_sample = true
top_k = 50
top_p = 1.0
temperature = 0.9
subtalker_dosample = true
subtalker_top_k = 50
subtalker_top_p = 1.0
subtalker_temperature = 0.9
repetition_penalty = 1.05
```

Do not change temperature while comparing epoch 1 vs epoch 2.

Otherwise the experiment cannot isolate the training-state effect.

---

# 20. Comparison metrics

For every candidate output record:

```text
duration
active speech duration
word timing
pause positions
pause durations
RMS
peak
periodicity
ZCR
spectral centroid
spectral flatness
high-frequency energy ratio
F0 median
F0 p10
F0 p90
F0 contour
energy contour
HNR
voiced-frame ratio
MFCC/spectral-envelope comparison
ASR word error / confidence
```

The reference set consists of all three good outputs, not only `goodoutputtts.ogg`.

---

# 21. Important acceptance dimensions

Do not reduce the selection to one scalar score.

A passing candidate must improve all important categories:

## Identity

- sounds like the same Tsuki voice family;
- stable across 1-, 2-, and 3-sentence requests.

## Accent / pronunciation

- retains the historical Japanese-accented English character;
- does not become generic American/native Qwen English;
- does not produce obvious wrong-word phonetic substitutions.

## Cadence

- word lengths are closer to the good outputs;
- phrase timing is closer;
- sentence pacing is closer;
- pauses occur in similar linguistic locations.

## Prosody

- F0 contour shape is closer;
- emphasis pattern is closer;
- sentence-final behavior is closer.

## Timbre

- spectral envelope is closer;
- brightness is not substantially harsher than the good-output cluster.

## Stability

- repeated generations do not drift dramatically;
- no EOS/runaway failure;
- no periodicity collapse.

---

# 22. Suggested ranking logic

Use a two-stage gate.

## Stage 1: reject unhealthy outputs

Reject if any of the following occurs:

```text
runaway generation
EOS failure
known-bad periodicity collapse
severe clipping
empty audio
major word corruption
unstable speaker identity
```

## Stage 2: rank only healthy candidates by target match

Among healthy candidates, compare against all three good files using:

```text
human A/B preference
speaker identity
accent match
word-duration similarity
pause-pattern similarity
F0-contour similarity
spectral-envelope similarity
ASR correctness
```

Human A/B review is authoritative for the final voice-match decision.

---

# 23. If repaired epoch 2 succeeds

If `checkpoint-epoch-1` from the repaired 2-epoch run materially restores the historical good-output voice behavior while remaining healthy:

1. repeat the winning checkpoint across at least five seeds for the exact historical target texts;
2. verify Japanese output remains healthy;
3. record the checkpoint tree/model hashes;
4. preserve `checkpoint-epoch-0` as the one-epoch control;
5. package the selected checkpoint as a staging-only artifact;
6. rerun Azure staging;
7. compare whole-turn synthesis against sentence-worker concatenation;
8. compare again against all three good outputs;
9. require human A/B approval before any production promotion.

Do not select epoch 2 merely because its loss is lower.

---

# 24. If repaired epoch 2 does NOT solve the gap

If repaired epoch 2 remains materially worse than the historical good outputs, do not add English data and do not randomly tune parameters.

Move to the next controlled variables in this order:

```text
1. low-LR variants around the repaired value
2. controlled sampling sweep after checkpoint selection
3. whole-turn vs sentence-split runtime comparison
4. only then investigate historical training-recipe reproduction in more detail
```

Keep the fixed `mochiko_0001.wav` speaker reference unchanged while testing training-state variables.

---

# 25. Controlled epoch experiment — CURRENTLY RUNNING

The controlled repaired epoch experiment is now active on Kaggle.

Constants:

```text
450 Japanese samples
0 holdout
speaker reference = mochiko_0001.wav
alignment = PR #278 repair
learning rate = 2e-6
batch size = 2
gradient accumulation = 4
weight decay = 0.01
gradient clip = 1.0
seed = 42
package/runtime family = same as v19 notebook
```

Changed variable:

```text
epoch count: 1 -> 2
```

Experimental Kaggle kernel:

```text
edriannieves/tsuki-qwen3-tts-0-6b-epoch2-a-b
```

Status at launch: `RUNNING`.

Evaluate `checkpoint-epoch-0` and `checkpoint-epoch-1` against all three good outputs.

Do not select by loss.

Select by generated voice match and stability.

---

# 26. Sampling sweep comes after identity/training isolation

Only after selecting the best speaker token and best healthy checkpoint should sampling be tuned.

Suggested controlled sweep:

```text
temperature: 0.7 / 0.8 / 0.9
```

Keep other sampling parameters constant first.

Then, only if necessary, test limited changes to:

```text
top_k
repetition_penalty
subtalker_temperature
```

The goal is to reduce stochastic drift while preserving the good-output voice character.

Do not use sampling changes to hide a bad speaker/checkpoint.

---

# 27. Production sentence-splitting issue

The normal TsukiAI worker pool still synthesizes different sentences independently and concatenates the WAVs:

```text
sentence 1 -> worker A
sentence 2 -> worker B
sentence 3 -> worker C
-> concatenate
```

This can damage turn-level cadence and prosody even after the model itself is fixed.

The supplied three-sentence target should therefore be tested in two ways:

```text
A. one complete request synthesized by one worker
B. current sentence-split multi-worker pipeline
```

If A matches the good target substantially better than B, production should prefer whole-turn synthesis or larger clause groups for voice quality.

Do not modify this before finishing the repaired checkpoint/epoch experiment; keep variables isolated.

---

# 28. Do not change these things during the first recovery experiment

Do not:

- add English training data;
- replace the 450 Japanese corpus;
- switch the trained-custom path to voice cloning;
- use a Base/full-ICL runtime path inside the trained-custom experiment;
- change the fixed `mochiko_0001.wav` reference while testing epoch count;
- change learning rate while testing epoch count;
- change sampling temperature while testing epoch count;
- use Japanese language conditioning as a substitute for the historical accent;
- approve based only on periodicity/F0 averages;
- approve based only on training loss.

---

# 29. Required experiment outputs

For every checkpoint candidate write:

```text
candidate ID
Kaggle kernel/version
checkpoint name
checkpoint tree SHA-256
model.safetensors SHA-256
fixed speaker reference SHA-256
training epoch
learning rate
alignment source/commit
language conditioning
generation seed
sampling parameters
raw WAV SHA-256
ASR transcript
word timings
acoustic metrics
comparison metrics vs each matching good file
human A/B result
```

Store raw WAVs unchanged.

---

# 30. Proposed implementation sequence

This sequence remains the internal order for the **trained-custom recovery track**. For the next project phase, Section 43's mandatory parallel Subagent A/Subagent B execution order takes precedence over running this section serially by the parent agent.

Execute the trained-custom track in this exact order:

```text
1. Preserve v19 checkpoint as healthy one-epoch control
2. Preserve the three good-output files + SHA-256
3. Recover original source text for good #1 and good #2 if available
4. Verify fixed training speaker reference = mochiko_0001.wav
5. Reject the old mochiko_0043/shuffle-anchor hypothesis
6. Run repaired 2-epoch Kaggle A/B with all other training variables fixed
7. Preserve checkpoint-epoch-0 and checkpoint-epoch-1
8. Generate exact 1/2/3-sentence targets under English/Auto/Japanese
9. Compute word timing, F0 contour, spectral, MFCC and ASR comparisons
10. Compare epoch 1 vs epoch 2 against all three good outputs
11. For any improved checkpoint, repeat >=5 seeds with recorded generation settings
12. Human A/B against all three good outputs
13. If epoch 2 wins: package as staging-only candidate
14. If epoch 2 does not win: test selected low-LR variants with epoch/reference fixed
15. Only after checkpoint selection: controlled sampling sweep
16. Compare whole-turn synthesis vs sentence-worker concatenation
17. Azure staging
18. Final A/B against all three good outputs
19. Production approval only after human match is satisfactory
```

---

# 31. Definition of done

The recovery is complete only when:

- [ ] the model still uses only the Japanese Tsuki training corpus;
- [ ] production remains trained-custom only;
- [ ] no runtime voice clone/reference conditioning is used in the **trained-custom production path**; the separately isolated zero-shot benchmark is explicitly exempt and must remain non-production;
- [ ] the three historical good files remain the acceptance reference set;
- [ ] speaker-token construction is deterministic and documented;
- [x] the prior first-shuffled-sample speaker-anchor concern was proven irrelevant for these notebook recipes because every row uses the same fixed `mochiko_0001.wav` `ref_audio`;
- [ ] the exact one-sentence test matches the historical voice family closely;
- [ ] the exact two-sentence test matches the historical voice family closely;
- [ ] the exact three-sentence test matches `goodoutputtts.ogg` closely;
- [ ] Japanese-accented English is preserved naturally by the trained model;
- [ ] word timing/cadence is materially closer to the good-output cluster;
- [ ] F0 contour/prosody is materially closer;
- [ ] spectral/timbre character is materially closer;
- [ ] repeated generations remain stable;
- [ ] Japanese output remains healthy;
- [ ] no EOS/runaway regression occurs;
- [ ] Azure staging reproduces the local/Kaggle result;
- [ ] the normal TsukiAI application path preserves the selected voice quality;
- [ ] human review approves the result before production promotion.

---

# 32. Current strongest hypothesis

The current highest-value hypothesis is:

```text
The historical and repaired notebooks use the same fixed Japanese speaker reference:
mochiko_0001.wav.

Therefore the remaining mismatch is more plausibly in the learned model/training state
than in a shuffled speaker-anchor selection.

v19 uses the repaired PR #278 alignment, but it also reduced training from the older
3-epoch / 2e-5 recipe to a conservative 1-epoch / 2e-6 repaired recipe.

The repaired checkpoint is acoustically healthy, but one epoch may not yet have moved
the model far enough toward the historical Tsuki cadence/timbre/cross-lingual behavior.
```

This is a testable hypothesis, not a conclusion.

The current next action is the **controlled repaired epoch-2 Kaggle A/B** already launched under:

```text
edriannieves/tsuki-qwen3-tts-0-6b-epoch2-a-b
```

No English training data is being added and the fixed Japanese reference remains unchanged.

---

# 33. Mandatory subagent orchestration

The local/parent agent **MUST use subagents** for the next phase.

Minimum split:

```text
PARENT / LOCAL ORCHESTRATOR
    |
    +-- SUBAGENT A: isolated Azure Qwen zero-shot benchmark
    |
    +-- SUBAGENT B: trained-custom good-output recovery/fix
```

Subagent A and Subagent B must have separate scopes and separate artifacts. The parent agent owns coordination, evidence collection, collision prevention, and final comparison. If subagent capability is genuinely unavailable, the parent must state that before implementation rather than claiming subagents were used.

Subagent A must not modify the main trained-custom VPS, current v19 control, production service, or Subagent B's files. Subagent B must not modify the new zero-shot VPS.

---

# 34. Subagent A — create separate Azure VPS matching the main VPS

The user has fully authorized creation of a **new Azure VPS** for the zero-shot benchmark.

Before creation, Subagent A must query the actual main TTS VPS and record rather than guess:

```text
Azure VM size / SKU
vCPU count
RAM class
OS image / distro / version
CPU architecture
OS disk type and size
region
relevant NIC/network characteristics
```

Create the new zero-shot VPS with the same VM size/SKU and a compatible OS image. If exact parity is unavailable, record the difference before provisioning.

The new VPS must have independent:

```text
VM name
service identity
systemd service
Python virtual environment
model/cache directory
API key
service port
logs
evidence directory
network endpoint
```

Do not resize/delete/overwrite the main VPS, reuse its API key, repoint production DNS, or route production TsukiAI traffic to the new VPS automatically.

---

# 35. Exact zero-shot workflow from the supplied Qwen demo screenshot

The screenshot is an external Qwen/Hugging Face demo reference. For **Subagent A only**, reproduce its semantics:

```text
Reference Audio
+
Reference Text = exact transcript of reference audio
+
Target Text
+
Language = Auto for the initial baseline
+
Model Size = 0.6B
+
X-vector-only = OFF
        |
        v
full zero-shot / full-ICL voice-clone generation
```

This is **not x-vector-only cloning**.

The benchmark must use the full reference-audio + reference-text path. Conceptually:

```python
model.generate_voice_clone(
    text=TARGET_TEXT,
    language="Auto",
    ref_audio=REFERENCE_AUDIO,
    ref_text=REFERENCE_TEXT,
    x_vector_only_mode=False,
)
```

The exact installed Qwen function signature must be inspected before implementation. The pseudocode above defines the required behavior, not guaranteed exact parameter names.

Required semantic contract:

```text
Qwen3-TTS 0.6B Base
+ reference audio
+ exact reference transcript
+ full reference/ICL conditioning
+ target text
```

---

# 36. Locked zero-shot reference audio

Subagent A must use exactly:

```text
C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3
```

Do not silently substitute another file.

Before deployment:

1. verify the file exists;
2. record SHA-256;
3. record actual decoded duration, sample rate, channels, codec, RMS, peak, clipping, and silence ratio;
4. transfer it unchanged to the isolated VPS;
5. verify remote SHA-256 equals local SHA-256.

The actual decoded duration is about 28.6 seconds; use measured duration rather than the `25s` filename as evidence.

---

# 37. Locked zero-shot reference transcript

Use this exact transcript with `tsuki_25s_clip.mp3`:

```text
こんにちは。月だよ。今夜も君と話せて嬉しいな。月の光が綺麗な夜は心が静かになるんだ。君はどう？君の声好きだよ。聞くたびに安心するんだ。今日はどんな一日だった？ゆっくり聞かせてね。夜風が気持ちいい季節だね。これからの時間楽しもう。君が笑うと私まで嬉しくなるんだ。いつでもここにいるからね。夜が終わっても。
```

Do not replace it with ASR output unless the user explicitly changes it. Record the transcript SHA-256 and treat the audio/text pair as immutable benchmark input.

---

# 38. Zero-shot model identity

Subagent A must use the Qwen3-TTS **0.6B Base zero-shot-capable model**, not the trained `tsukihime` custom checkpoint.

Record:

```text
model repository ID
resolved revision / commit
qwen-tts version
transformers version
torch version
attention implementation
device
model/tree digest where practical
```

Do not use v19 trained-custom weights as the zero-shot Base model. Do not enable x-vector-only mode.

---

# 39. Zero-shot benchmark texts

Use the same historical targets used for trained-model comparison.

One-sentence target: recover the exact original source text from logs if possible; known wording is approximately:

```text
This is the one-sentence Azure and Colab voice test.
```

Two-sentence target: recover the exact original source text from logs if possible; known wording is approximately:

```text
This is the two-sentence Azure and Colab voice test. The second sentence confirms that longer text is also being synthesized.
```

Three-sentence target is locked exactly to:

```text
This is the three sentence Azure and the Colab voice test. The custom domain is carrying the request to Colab. The Discord bridge is sending the result as a real voice message.
```

Reference target audio:

```text
C:\Users\LENOVO\Downloads\goodoutputtts.ogg
```

---

# 40. Zero-shot baseline and repeatability

The screenshot-equivalent first baseline must use:

```text
Language = Auto
Model Size = 0.6B
X-vector-only = OFF
full reference text enabled
```

Subagent A must inspect and record the installed Qwen default generation parameters before tuning anything.

For the locked three-sentence target, generate at least **5 repetitions/seeds** and preserve every raw WAV plus seed/config.

Historical-runtime replay must also test:

```text
Language = English
```

because the committed September Qwen client detected English target text and sent `English` to the full-ICL service. Therefore:

- `Auto` is the screenshot-equivalent baseline;
- `English` is the historically accurate TsukiAI runtime replay for English text.

Keep all other reference/prompt/sampling settings identical between those two language modes.

Only after these baselines exist may additional `Japanese` language-conditioning or sampling comparisons begin.

Do not vary reference audio, reference text, language conditioning, and sampling parameters simultaneously.

---

# 41. Shared comparison against the three historical good outputs

Both subagents must be compared against:

```text
C:\Users\LENOVO\Downloads\goodoutputtts.ogg
C:\Users\LENOVO\Downloads\voice-message (1).ogg
C:\Users\LENOVO\Downloads\voice-message (2).ogg
```

Record for every candidate output:

```text
duration
active speech duration
word timing
pause positions/durations
RMS
peak
periodicity
ZCR
spectral centroid
spectral flatness
high-frequency energy ratio
F0 median / p10 / p90
F0 contour
energy contour
HNR
voiced-frame ratio
MFCC / spectral-envelope comparison
ASR transcript and word confidence
```

The parent agent must prepare a direct A/B matrix:

```text
historical good outputs
vs
Subagent A: 0.6B zero-shot full-reference/full-ICL
vs
Subagent B: repaired trained-custom model
```

Do not select based on a single scalar metric. Human listening remains required.

---

# 42. Zero-shot VPS service contract

The new VPS must identify itself as a non-production zero-shot benchmark. Its health response should expose non-secret fields equivalent to:

```json
{
  "mode": "zero-shot-full-icl",
  "model_size": "0.6B",
  "reference_required": true,
  "x_vector_only": false,
  "reference_sha256": "<sha256>",
  "reference_text_sha256": "<sha256>",
  "production": false
}
```

Do not expose API keys, Azure subscription IDs, tokens, or other secrets. The service should fail closed if the configured reference file/transcript pair does not match the approved digests.

---

# 43. Mandatory parallel execution order

```text
PARENT / LOCAL AGENT
|
+-- START SUBAGENT A
|   1. query main Azure VPS specs
|   2. create matching isolated Azure VPS
|   3. install Qwen3-TTS 0.6B Base zero-shot stack
|   4. transfer tsuki_25s_clip.mp3
|   5. lock exact Japanese reference transcript
|   6. verify x-vector-only OFF
|   7. generate Auto / 0.6B screenshot-equivalent baseline
|   8. generate 1/2/3-sentence historical benchmark texts
|   9. repeat 3-sentence target >=5 times
|  10. return raw WAVs + hashes + metrics + configs
|
+-- START SUBAGENT B CONCURRENTLY
|   1. preserve v19 healthy one-epoch control
|   2. verify fixed speaker reference = mochiko_0001.wav
|   3. run repaired epoch-2 Kaggle A/B with all other variables fixed
|   4. compare checkpoint-epoch-0 vs checkpoint-epoch-1 against all three good outputs
|   5. repeat any improved checkpoint across >=5 generation seeds
|   6. keep trained-custom path reference-free at runtime
|
+-- PARENT JOINS RESULTS
    1. verify both subagent reports
    2. verify no service/artifact collision
    3. compare both tracks using identical target texts
    4. prepare human A/B listening set
    5. do not alter production automatically
```

Subagent A and Subagent B should run independently/concurrently where possible.

---

# 44. Updated definition of done for this parallel phase

## Subagent A

- [ ] a subagent was actually used;
- [ ] main VPS specs were queried, not guessed;
- [ ] separate Azure VPS exists with matching compute specs or documented differences;
- [ ] Qwen3-TTS 0.6B Base is installed;
- [ ] exact `tsuki_25s_clip.mp3` is the reference audio;
- [ ] exact supplied Japanese transcript is the reference text;
- [ ] local/remote reference SHA-256 matches;
- [ ] full reference-text-conditioned zero-shot/full-ICL generation is used;
- [ ] x-vector-only is OFF;
- [ ] first baseline uses Language=Auto;
- [ ] 1/2/3-sentence English benchmark outputs exist;
- [ ] three-sentence target has at least 5 repeated raw generations;
- [ ] raw WAVs, hashes, metrics, model revision, and service identity are archived;
- [ ] new VPS remains isolated from production.

## Subagent B

- [ ] a subagent was actually used;
- [ ] Japanese-only 450-sample requirement remains unchanged;
- [ ] trained-custom architecture remains separate from zero-shot;
- [x] the speaker-anchor/shuffle hypothesis was checked against the live notebook and rejected because all rows use the same fixed `mochiko_0001.wav` reference;
- [ ] outputs are compared against all three historical good files;
- [ ] repaired epoch-2 checkpoint-state work is completed and driven by generated-audio evidence.

## Parent/local orchestrator

- [ ] both subagent reports are collected;
- [ ] no artifact/service collision occurred;
- [ ] zero-shot and trained-custom outputs are compared on the same target texts;
- [ ] human A/B material is prepared;
- [ ] main production service remains unchanged pending explicit review.

---

# 45. Architecture distinction — do not merge these paths

```text
PATH A — isolated zero-shot benchmark VPS

Qwen3-TTS 0.6B Base
+ tsuki_25s_clip.mp3
+ exact Japanese reference transcript
+ full ICL / zero-shot voice clone
+ x-vector-only OFF
+ Language=Auto baseline
        |
        v
zero-shot comparison WAVs
```

```text
PATH B — trained-custom recovery

450 Japanese training recordings
+ repaired SFT
+ trained tsukihime custom speaker
+ generate_custom_voice
+ no runtime reference audio
        |
        v
trained-model comparison WAVs
```

Path A answers: **how closely can Qwen3-TTS 0.6B full-reference zero-shot reproduce the historical good Tsuki outputs using `tsuki_25s_clip.mp3`?**

Path B answers: **what changed in the trained custom model, and how do we recover the historical good trained voice behavior?**

Do not merge these architectures in code, service configuration, evidence, or conclusions.

---

# 46. Parallel execution status — 2026-09-17

This section records the first real parent/subagent execution pass. It is an evidence log, not an approval to promote either path.

## Parent status table

| Track | Status | Last verified evidence | Artifact/model hash | Current blocker | Next action |
| --- | --- | --- | --- | --- | --- |
| Subagent A — isolated Azure zero-shot | **BLOCKED / NOT YET TESTED** | Azure inventory found only the pre-existing `B4AS-TEST-RG/b4as-test`; no new VM or service was created. The locked MP3 exists and hashes to `ce93bb53f197f0683d46ab4ad240e3d434ef6e2b2340190be98dfe382a0f6530`. | No zero-shot artifact. Reference hash above. | Guest-side Qwen/service inspection did not succeed; SKU-capability lookup timed out; creation permission was not proven. | Resolve Azure guest inspection and provisioning authorization, then create only the isolated benchmark VPS and repeat the locked-input/hash gates. |
| Subagent B — trained-custom recovery | **BLOCKED / NOT YET TESTED** | The three historical reference hashes were verified. The checkout contains no discoverable `.safetensors`/checkpoint files and zero `.wav` files. The v19 report records checkpoint-tree hash `0194ed64608ce71a96b38c7c84927ff966b9863d01f7f74996b1785c1ac3ed38` and model hash `a2005807a1e73b09ae75957bd26dd4e424d9558eb6b400dd36a02d6a2dcc2c00`, but report-only hashes are not usable model payloads. | No candidate checkpoint artifact. Historical references: `goodoutputtts.ogg` = `62465ca6357c3bbf7bff7d5f83d5f153407fe380493fef6e8e21977d1f670b82`; `voice-message (1).ogg` = `8f256177569e9c90da5402d8fd9fc058095f1e115fe10f06bd1d966ab54dcc18`; `voice-message (2).ogg` = `97bb657bb99585496a6acc43bb0f9855543503268f438a7d1341352ab8caeacc`. | The actual v19 checkpoint and 450 Japanese WAV corpus are outside the specified checkout or unavailable; no safe speaker-anchor experiment can be run from hashes/reports alone. | Restore/provide the actual v19 checkpoint and 450 WAV corpus, then run the row-3000-only anchor experiment before any retraining. |

## Subagent A — verified, failed, and not yet tested

**VERIFIED**

- A real subagent was used for this track.
- Azure CLI was authenticated. Read-only inventory identified `b4as-test` in `B4AS-TEST-RG`, region `malaysiawest`, SKU `Standard_E4as_v5`, Canonical Ubuntu `22.04-lts-gen2`, 30-GiB `Standard_LRS` OS disk, accelerated networking disabled, and subnet `b4as-testSubnet`.
- Azure reports the VM as running. The Easv5 SKU specification is 4 vCPUs and 32 GiB RAM; the SKU family is x86-64. This is SKU/image metadata, not a successful in-guest measurement.
- The locked reference file exists locally and its SHA-256 is `CE93BB53F197F0683D46AB4AD240E3D434EF6E2B2340190BE98DFE382A0F6530`.
- A parent-side `az vm list` showed no second VM. No new resource, service, port, API key, cache, or production change was left behind.

**FAILED / BLOCKED**

- The guest-side RunCommand did not produce usable Qwen/service evidence; its retry failed with `'tts' is not recognized as an internal or external command`.
- SKU capability lookup timed out, and creation authorization was not established. Provisioning was therefore stopped before mutation.

**NOT YET TESTED**

- Decoded MP3 metrics, remote byte transfer/hash, Qwen3-TTS 0.6B Base installation/revision, exact signature, full-reference ICL generation, x-vector-only verification, benchmark WAVs, five repetitions, and comparison metrics.

## Subagent B — verified, failed, and not yet tested

**VERIFIED**

- A real subagent was used for this track and performed read-only inspection only.
- The three historical good files exist with the hashes recorded above and were not modified.
- The v19 evidence report identifies a 450-sample Japanese-only run and records the report-only checkpoint/model hashes above.
- Parent-side filesystem inspection independently found zero `.wav` files and no checkpoint/`.safetensors` payloads in `D:\New folder (2)\TsukiAI 1.0`.

**BLOCKED**

- Speaker-encoder extraction, candidate A/B/mean/medoid construction, row-3000-only verification, synthesis, acoustic/ASR comparison, and human A/B review were not run because the required source payloads are unavailable.

**NOT YET TESTED**

- Every speaker-anchor and later epoch/sampling experiment remains untested. No English training data was added, no runtime reference audio was introduced into the trained-custom path, and v19 was not overwritten.

## Missing requested addendum

`D:\New folder (2)\TsukiAI 1.0\voice\qwen3-tts\ZERO_SHOT_PARALLEL_SUBAGENT_ADDENDUM.md` was not found in the requested path or anywhere under the project tree during this pass. Its contents remain **UNKNOWN / NOT VERIFIED** and were not reconstructed.

## Parent conclusion

The mandatory parallel orchestration occurred, but neither experiment reached generation. No voice-match conclusion is justified. The next valid actions are to restore the missing trained artifacts and resolve the Azure guest/provisioning blockers; only then may the isolated generation and human A/B gates proceed. Production remains unchanged.

## 46.1 Subagent A evidence correction/supplement

The delayed Subagent A evidence package provides a more complete read-only inspection than the parent’s initial CLI pass. The following values supersede the earlier incomplete notes in this section where they overlap:

**VERIFIED**

- Live main VM guest inspection succeeded read-only: `b4as-test`, `Standard_E4as_v5`, `4` vCPU, `32768` MiB / 32 GiB class, `x86_64`, AMD EPYC 7763, Ubuntu 22.04.5 LTS, 30-GiB managed `Standard_LRS` OS disk, no data disks, and the recorded NIC/VNet/subnet configuration. Evidence: `evidence/subagent-a-zero-shot-20260917/azure-main-vps-inspection.json`.
- Azure quota is the concrete provisioning blocker: regional vCPU current/limit `4/4` and Standard EASv5 family current/limit `4/4`. Virtual-machine resource count is `1/25000`. No quota request was made and no cloud mutation occurred.
- The locked MP3 decoded as mono 44.1 kHz MP3 with duration `28.62934240362812` seconds, RMS `0.1597425802129051`, decoded peak `1.680038571357727`, sample silence ratio `0.2380310069905921`, frame silence ratio `0.1970649895178197`, leading silence `0.12` seconds, and trailing silence `0.04` seconds. These are decoded measurements; the peak count alone does not prove source-encoder clipping. Evidence: `evidence/subagent-a-zero-shot-20260917/reference-audio-metrics.json`.
- On the existing main VM only, the installed `qwen-tts` version is `0.1.1` and the inspected `generate_voice_clone` signature includes `ref_audio`, `ref_text`, and `x_vector_only_mode=False` by default. No model loading or generation was performed.

**BLOCKED / NOT YET TESTED**

- No matching isolated VPS can be created until the quota state changes. Therefore remote reference hash, isolated Qwen installation/revision, zero-shot full-ICL generation, five repetitions, raw WAVs, and all comparison metrics remain untested.

Evidence package created by Subagent A, and no production/main-VPS files were changed:

```text
D:\New folder (2)\TsukiAI 1.0\voice\qwen3-tts\evidence\subagent-a-zero-shot-20260917\azure-main-vps-inspection.json
D:\New folder (2)\TsukiAI 1.0\voice\qwen3-tts\evidence\subagent-a-zero-shot-20260917\reference-audio-metrics.json
D:\New folder (2)\TsukiAI 1.0\voice\qwen3-tts\evidence\subagent-a-zero-shot-20260917\locked-reference-transcript.txt
D:\New folder (2)\TsukiAI 1.0\voice\qwen3-tts\evidence\subagent-a-zero-shot-20260917\subagent-a-report.md
```

## 46.2 Follow-up trained-artifact identity audit

A follow-up subagent searched the separate local Kaggle handoff area after the initial D: checkout audit. This found usable-looking historical artifacts, but direct fingerprint comparison rejects them as the v19 control required by this plan.

**VERIFIED**

- The local handoff corpus contains 450 Japanese WAVs, `tsukihime_0001.wav` through `tsukihime_0450.wav`, with declared corpus SHA-256 `72360c27040fb2051c6e425c774f4b61073e2841a00963e0e45dd5444d2ec959`. Its summary reports 450 successful files, 0 rejected, mono 24 kHz, 16-bit PCM, Japanese language. This establishes structural availability only.
- The extracted historical checkpoints and ZIP archives do not match the v19 fingerprints required by this plan:

| Artifact | Tree SHA-256 | `model.safetensors` SHA-256 |
| --- | --- | --- |
| extracted `checkpoint-epoch-0` | `307d3cf2dad4b232326e5c08522db50d0af7e0962c307afedfa119dd8e9598a6` | `112c430f104d66c62fb76351f2e61e25d1b455316a8e8aaec511975cb2cece4d` |
| extracted `checkpoint-epoch-1` | `a10918b59df290531c9100e7431565a2da3ec35af10b3084afdbeb1c1fa830dc` | `266986b6de468c7dde3a7bbbe4f6f084a7424fb3a4b893bac3a02e1f3fbc0cdf` |
| `tsukiai_tsukihime_qwen06b_repaired.zip`, epoch 1 | `b3f5d83f601aa3234462f4304d3741e266c1fb97ab985e39a72bc8876b12e495` | `836b7b357f5ea43e889936a3709af68dfe3751881acefe4ecf0dbd30ba571258` |

- Required v19 fingerprints remain `0194ed64608ce71a96b38c7c84927ff966b9863d01f7f74996b1785c1ac3ed38` (tree) and `a2005807a1e73b09ae75957bd26dd4e424d9558eb6b400dd36a02d6a2dcc2c00` (model). No searched artifact matched either fingerprint.
- Archive SHA-256 values are `5f7ffd761a7c7cbb113e002d378a4ac90330a873a7382789015670927180f2de` for `tsukiai_tsukihime_qwen06b.zip` and `265c65e43d04c89aba86dedc057f69ea626ed349a1257b14b6305842e1a57514` for `tsukiai_tsukihime_qwen06b_repaired.zip`.

**BLOCKED / NOT YET TESTED**

- The handoff corpus may be related to the trained run, but cross-run identity is not verified. The `tsukihime_####` filenames versus the plan’s `mochiko_0043.wav` reference are not enough to prove equivalence or mismatch.
- The speaker-anchor experiment was not run. No candidate checkpoint was modified, no tensor-diff claim was made, and no synthesis or A/B result exists.

The exact v19 checkpoint and its run-linked 450-sample corpus remain required before the row-3000-only experiment can begin.

---

# 46.3 Superseding live Kaggle status — 2026-09-18

This section supersedes the earlier **BLOCKED** conclusion for the trained-custom track where the statements conflict.

## VERIFIED

Live authenticated Kaggle access is working through the local environment.

Current production-training kernel:

```text
edriannieves/tsuki-qwen3-tts-0-6b-sft
status = COMPLETE
GPU = Nvidia Tesla T4
dataset = edriannieves/tsukiai-mochiko-qwen3tts-06b
```

The live kernel source and selective output evidence were pulled directly from Kaggle.

The live artifact manifest confirms:

```text
samples = 450
train = 450
holdout = 0
learning rate = 2e-6
epochs = 1
speaker = tsukihime
checkpoint = checkpoint-epoch-0
alignment = Qwen3-TTS PR #278 / 09364dcc3f947130861186f222a6c58e319ae67a
model.safetensors SHA-256 =
a2005807a1e73b09ae75957bd26dd4e424d9558eb6b400dd36a02d6a2dcc2c00
checkpoint tree SHA-256 =
0194ed64608ce71a96b38c7c84927ff966b9863d01f7f74996b1785c1ac3ed38
```

The live notebook and Qwen `TTSDataset` implementation also prove that every training row uses the same fixed `ref_audio`:

```text
reference ID = 0001
reference WAV = mochiko_0001.wav
reference WAV SHA-256 =
ce6a38a0613a36a18a96362aabf672734f93d1377b9b9c366fb94aba7978c918
```

Therefore the earlier `mochiko_0043` first-shuffle speaker-anchor hypothesis is rejected for the historical and v19 notebook recipes.

## HISTORICAL GOOD-OUTPUT PROVENANCE

Repository history shows the committed Qwen service beginning at commit:

```text
789fd7c
2026-09-13 10:51:04 +0800
replace OpenVoice with Qwen3-TTS voice cloning
```

used:

```text
Qwen3-TTS 0.6B Base
+ tsuki_25s_clip.mp3
+ exact Japanese transcript
+ full ICL
+ x_vector_only_mode=False
+ generate_voice_clone()
```

The three supplied good output files were created before the working-tree trained-custom conversion of `voice/qwen3-tts/api.py` on 2026-09-16 17:21.

Therefore the historical good outputs are strongest evidence for the full-ICL/zero-shot path, not for a previously-good trained-custom checkpoint.

## ACTIVE TRAINED-CUSTOM EXPERIMENT

A separate private Kaggle experiment was launched without overwriting the v19 kernel:

```text
actual Kaggle slug:
edriannieves/tsuki-qwen3-tts-0-6b-epoch2-a-b

experiment:
450 Japanese samples
0 holdout
same mochiko_0001.wav reference
same PR #278 alignment repair
same learning rate 2e-6
same batch size / accumulation / weight decay / clip / seed
epoch count changed only: 1 -> 2
```

Current status at the time of this update:

```text
RUNNING
```

The intended comparison is:

```text
checkpoint-epoch-0
vs
checkpoint-epoch-1
vs
all three historical good outputs
```

No production service has been changed.

## SUBAGENT CONTROL NOTE

A new subagent-control attempt from this chat was refused twice with `WORKER_IDENTITY_LOST`. No new worker was claimed or fabricated. Previous local-agent subagent evidence remains historical evidence, but this live Kaggle execution is being driven directly until worker identity is available again.
