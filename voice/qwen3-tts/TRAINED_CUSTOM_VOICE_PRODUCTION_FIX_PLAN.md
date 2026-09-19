# TsukiAI Qwen3-TTS Trained Custom Voice Production Fix Plan

Status: **AUTHORITATIVE REPLACEMENT PLAN**

Date: 2026-09-16

Project root: `D:\New folder (2)\TsukiAI 1.0`

This document supersedes any previous plan that treated Base/full-ICL voice cloning as the intended production architecture.

---

# 1. Non-negotiable production requirement

The final production voice must use a **trained Tsukihime custom voice checkpoint directly**.

The production path is:

```text
450 Japanese Tsukihime recordings
        ↓
Qwen3-TTS 0.6B Base used only as initial pretrained weights
        ↓
Tsukihime fine-tuning
        ↓
trained custom checkpoint
speaker = tsukihime
        ↓
production loads trained checkpoint directly
        ↓
generate_custom_voice()
        ↓
Japanese input → Japanese Tsuki voice
English input  → Tsuki speaking English with a Japanese accent
```

The following must **not** be part of the final production inference path:

```text
generate_voice_clone()
reference audio upload
reference transcript
x-vector voice cloning
full-ICL voice cloning
Base model inference fallback
built-in generic Qwen speaker fallback
runtime reference prompt creation
```

The official Base model is allowed only as the **starting checkpoint for training** and as an optional diagnostic control. It is not the production voice.

---

# 2. Why the current system does not satisfy the requirement

There are multiple independent problems.

## 2.1 The repository still contains an obsolete Base/full-ICL production contract

Verified examples:

`voice/qwen3-tts/runtime_config.py`

currently defaults to:

```python
QWEN_TTS_MODE = full-icl
```

and its comments describe Base + cached full-ICL reference prompt as the supported production voice path.

Other files also retain the obsolete architecture, including parts of:

```text
voice/qwen3-tts/README.md
voice/qwen3-tts/api.py
voice/qwen3-tts/deployment_identity.py
voice/qwen3-tts/benchmark_evidence.py
voice/qwen3-tts/GATE0_ARTIFACT_VERIFICATION.md
voice/qwen3-tts/GATE1_BASE_FULL_ICL_REHEARSAL.md
voice/qwen3-tts/GATE1_GATE2_RESULTS_20260914.md
voice/qwen3-tts/GATE3_NATIVE_BACKEND_PREFLIGHT.md
voice/qwen3-tts/systemd/qwen3-tts.service.example
voice/qwen3-tts/tests/*
```

This creates a dangerous architectural ambiguity: an agent or deployment can incorrectly restore voice-cloning behavior even though the intended final system is a trained custom speaker.

## 2.2 The screenshot is external Hugging Face/Qwen reference material, not TsukiAI UI

The supplied screenshot comes from the Hugging Face/Qwen TTS demo and was provided only as reference material.

It must **not** be interpreted as evidence that TsukiAI currently uses a voice-clone UI or voice-clone production workflow.

No TsukiAI UI replacement is required on the basis of that screenshot.

The actual production requirement remains:

```text
trained Tsukihime custom checkpoint
speaker = tsukihime
generate_custom_voice()
```

The screenshot is useful only to distinguish Qwen's separate voice-clone demo capability from the trained-custom workflow required by TsukiAI.

## 2.3 The current Kaggle run did not train on all 450 samples

Verified Kaggle dataset state:

```text
metadata_count = 450
manifest_count = 450
wav_count      = 450
language       = ja only
speaker_id     = 20 only
sample_rate    = 24000 Hz
channels       = mono
total_duration = 3850.602 seconds ≈ 64.18 minutes
```

But actual training split was:

```text
train_count   = 425
holdout_count = 25
```

Therefore:

- all 450 samples were present and audited;
- only 425 samples contributed gradients;
- 25 samples were withheld.

For the final production checkpoint, if the explicit requirement is **use the whole dataset**, then the final training run must use:

```text
TRAIN_COUNT = 450
HOLDOUT_COUNT = 0
```

Evaluation should use unseen **text prompts**, not necessarily withheld speaker recordings.

## 2.4 The current trained checkpoints are acoustically degraded

Measured behavior from Kaggle evaluation:

```text
Base median periodicity      ≈ 0.798
Epoch 0 median periodicity   ≈ 0.314
Epoch 1 median periodicity   ≈ 0.319
Epoch 2 median periodicity   ≈ 0.262
```

The current bad user output measured approximately:

```text
periodicity ≈ 0.360
ZCR         ≈ 0.0877
centroid    ≈ 1937 Hz
```

Kaggle epoch-1 measured approximately:

```text
periodicity ≈ 0.332
ZCR         ≈ 0.0877
centroid    ≈ 1931 Hz
```

The bad user output is therefore acoustically consistent with the currently deployed trained epoch-1 model.

## 2.5 The trained checkpoints fail normal termination in Kaggle evaluation

For a short prompt:

```text
Base:     ~3.92 seconds
Epoch 0: ~163.76 seconds
Epoch 1: ~163.76 seconds
Epoch 2: ~163.76 seconds
```

This strongly indicates broken autoregressive/EOS behavior in the trained checkpoints.

The Azure runtime cap hides the runaway duration but does not repair the underlying model.

## 2.6 Kaggle marks all trained checkpoints rejected, but packages epoch-1 anyway

Current report:

```json
{
  "epoch-0": "REJECTED_OTHER",
  "epoch-1": "REJECTED_OTHER",
  "epoch-2": "REJECTED_OTHER"
}
```

Current packaging logic still hard-codes:

```python
ARTIFACT_CHECKPOINT = "checkpoint-epoch-1"
```

This is a release-gate defect.

A rejected checkpoint must never become a deployable production artifact.

## 2.7 Azure is currently running the same rejected epoch-1 checkpoint

Verified live mode:

```text
QWEN_TTS_MODE=trained-custom
QWEN_TTS_MODEL=/opt/tsuki-qwen3-tts/checkpoints/tsuki-qwen3-tts-0-6b-sft/checkpoint-epoch-1
QWEN_TTS_SPEAKER=tsukihime
```

Verified live checkpoint identity matches Kaggle by SHA-256.

Therefore the current bad production voice is not caused by Azure loading the wrong file. It is loading the intended epoch-1 artifact, but that artifact is itself bad.

---

# 3. Correct production architecture

## 3.1 Runtime must be trained-custom only

Production must load:

```text
trained Tsukihime custom checkpoint
```

and call:

```python
model.generate_custom_voice(
    text=...,
    language=...,
    speaker="tsukihime",
    ...
)
```

Production must not call:

```python
model.generate_voice_clone(...)
```

## 3.2 Production startup must fail closed

The service must refuse to start if any of the following is true:

```text
QWEN_TTS_MODE != trained-custom
QWEN_TTS_MODEL points to official Base model
loaded model tts_model_type != custom_voice
speaker tsukihime is absent
checkpoint ID is missing
checkpoint hashes are missing/unverified
checkpoint approval status is not APPROVED_FOR_PRODUCTION
```

No silent fallback.

No automatic switch to Base.

No voice clone fallback.

## 3.3 Remove production reference dependencies

In trained-custom production mode, the service should not require or read:

```text
QWEN_TTS_REFERENCE
QWEN_TTS_REF_TEXT
QWEN_TTS_REF_TEXT_FILE
QWEN_TTS_PROMPT_PATH
cached ICL prompts
```

Those may remain only in isolated diagnostic tooling if needed, but they must be outside the production contract.

---

# 4. Training repair

The first priority is to produce a **healthy trained checkpoint**.

Do not start another Kaggle training run with the current notebook unchanged.

## 4.1 Repair talker label alignment

The pinned SFT code currently performs manual input/label shifting:

```python
inputs_embeds=input_embeddings[:, :-1, :]
attention_mask=attention_mask[:, :-1]
labels=codec_0_labels[:, 1:]
```

The underlying causal loss path already performs label shifting.

The repaired implementation must follow the verified upstream-compatible alignment contract and ensure labels are shifted exactly once.

Required semantic result:

```text
text/codec input sequence
       ↓
correctly aligned talker logits
       ↓
one causal shift only
       ↓
correct codec-0 target
```

## 4.2 Repair sub-talker alignment

The sub-talker path must use hidden states and codec labels aligned to the same prediction positions.

Do not fix only the main talker and leave sub-talker indexing unchanged.

Add assertions in the training script that verify:

```text
number of selected hidden states
==
number of selected sub-talker codec targets
```

and verify there is no one-token displacement.

## 4.3 Keep the 0.6B text projection correction

The current 0.6B projection repair must remain.

Conceptually:

```python
input_text_embedding = model.talker.text_projection(
    model.talker.model.text_embedding(input_text_ids)
) * text_embedding_mask
```

Do not remove this while fixing alignment.

## 4.4 Reduce learning rate

Current:

```text
2e-5
```

Initial repaired baseline:

```text
2e-6
```

Do not change five optimization variables at once.

First controlled repair run should keep the rest stable unless required by the corrected upstream code.

## 4.5 Train one epoch first

Initial repair experiment:

```text
num_epochs = 1
```

Do not immediately train 3 epochs.

The current epoch-0/first-epoch output is already bad, so the repaired first epoch must prove the training objective is fixed before more training is allowed.

## 4.6 Use all 450 samples for the final production checkpoint

There are two stages:

### Debug/validation stage

It is acceptable to keep a small deterministic holdout while validating the corrected training recipe.

Example:

```text
425 train
25 holdout
```

### Final production stage

Once the recipe is proven healthy, train the final checkpoint using:

```text
450 train
0 holdout
```

All 450 Japanese Tsukihime recordings must contribute gradients.

Evaluation remains possible because the evaluation prompts are unseen text, and training quality is also checked through fixed raw-audio generation tests.

## 4.7 Preserve Japanese speaker identity

The final custom model must register:

```text
speaker = tsukihime
```

and the resulting config must contain:

```text
spk_id["tsukihime"]
```

The checkpoint must not rely on an uploaded runtime voice sample to determine speaker identity.

---

# 5. Japanese-accented English requirement

This is a separate requirement from merely fixing acoustic corruption.

A healthy trained checkpoint can still speak English too natively if the inference path forces normal English language conditioning.

## 5.1 Current behavior that must be changed

C# currently detects the text language:

```csharp
var language = TtsLanguageDetector.Detect(text);
```

For English text it returns:

```text
EN
```

The Python worker normalizes that to:

```text
English
```

and calls:

```python
generate_custom_voice(
    text=english_text,
    language="English",
    speaker="tsukihime"
)
```

Qwen then injects the English codec language ID.

This encourages normal English pronunciation and can weaken the Japanese accent.

## 5.2 Do not equate speaker timbre with accent

The trained speaker embedding/custom speaker identity controls who the voice sounds like.

The language conditioning strongly affects pronunciation behavior.

Therefore this requirement:

```text
Tsuki identity + Japanese-accented English
```

must be tested explicitly.

It cannot be assumed from Japanese-only speaker training.

## 5.3 Build an accent evaluation matrix

For the same 10-20 English sentences, test the trained checkpoint with:

```text
A. language="English"
B. language="Auto"
C. language="Japanese"
```

Record raw WAV output for every mode.

Evaluate:

```text
speaker identity
Japanese accent strength
English intelligibility
rhythm
vowel realization
consonant realization
prosody
noise/artifacts
EOS behavior
```

Do not change checkpoint or sampling settings during this comparison.

## 5.4 Allow Auto in the local validator for controlled testing

Current `validation.py` only accepts English/Japanese aliases.

For the controlled accent experiment, add:

```text
auto -> Auto
```

Do not make Auto the production default until it is evaluated.

## 5.5 Production English policy must be explicit

After the matrix is reviewed, choose a specific policy.

Example final configuration shape:

```text
Japanese text:
  Qwen language = Japanese

English text:
  Qwen language = <verified best accent mode>
```

Do not continue using generic language detection as the final pronunciation policy if it produces an American/native-English voice.

## 5.6 If Japanese-only training cannot preserve the desired English accent

Then add a controlled **same-speaker Japanese-accented English training subset**.

This is the strongest way to teach the model the desired English pronunciation style.

Requirements for such additions:

```text
same Tsukihime speaker
same recording chain/quality
English text spoken naturally with intended Japanese accent
accurate transcripts
24 kHz mono lossless WAV
no unrelated speakers
no synthetic accent conversion
```

Do not replace the Japanese corpus.

The model should remain primarily Japanese-trained.

The English-accent subset exists only to teach the cross-lingual pronunciation behavior.

## 5.7 Do not rely on 0.6B instruction prompting for accent

Do not design the solution around a runtime instruction such as:

```text
"Speak English with a Japanese accent"
```

The 0.6B CustomVoice path should be treated as requiring the accent to come from:

```text
trained voice behavior
+
validated language conditioning
+
optional Japanese-accented English training examples
```

not a fragile runtime instruction.

---

# 6. Evaluation must test the trained model, not voice cloning

## 6.1 Remove clone-based evaluation from the production gate

The trained checkpoint evaluation must call:

```python
generate_custom_voice(
    text=prompt,
    language=language,
    speaker="tsukihime"
)
```

It must not call:

```python
generate_voice_clone(...)
```

for candidate checkpoint selection.

## 6.2 Fixed Japanese prompt suite

Keep a stable Japanese evaluation set with prompts that do not appear verbatim in the training corpus.

Cover:

```text
short statements
questions
emotional but natural lines
numbers
times
proper nouns
longer conversational sentences
soft speech
energetic speech
```

## 6.3 Fixed English accent suite

Create a permanent English prompt suite covering:

```text
r/l contrasts
th sounds
v/f contrasts
consonant clusters
word-final consonants
stress-heavy English words
common conversational sentences
names
numbers
time expressions
short emotional lines
```

The goal is not to exaggerate a stereotype.

The goal is consistent English spoken with the same Japanese-accent character identity the user expects from Tsuki.

## 6.4 Always evaluate raw WAV first

Quality gate order:

```text
trained model raw WAV
    ↓
acoustic metrics
    ↓
human listening review
    ↓
Azure staging raw WAV
    ↓
TsukiAI audio conversion
    ↓
Discord/Opus playback
```

Do not diagnose the trained checkpoint from final Discord audio first.

---

# 7. Automatic quality gates

The previous pipeline allowed a rejected checkpoint to deploy. This must be impossible.

## 7.1 EOS / generation termination gate

Reject candidate if short prompts repeatedly:

```text
hit max_new_tokens
run far longer than expected
exceed 20 seconds without a legitimate reason
repeat indefinitely
produce trailing garbage
```

A runtime cap is a safety guard, not a quality pass.

## 7.2 Acoustic metrics

Record at minimum:

```text
duration
sample rate
channel count
peak
RMS
clipping ratio
silence ratio
DC offset
periodicity / autocorrelation voicing score
zero-crossing rate
spectral centroid
high-frequency energy ratio
```

Recommended additional metrics:

```text
HNR
F0 coverage
median F0
F0 10th/90th percentile
spectral flatness
voiced-frame ratio
```

## 7.3 Known-bad regression signature

Known bad trained epoch-1 is approximately:

```text
periodicity ~0.32-0.36
ZCR         ~0.088 or higher
centroid    ~1.9-2.2 kHz or higher on comparable speech
```

A new checkpoint that moves toward this signature must fail.

## 7.4 Initial hard reject threshold

Use as provisional starting point:

```text
median periodicity < 0.60 -> reject
```

The known healthy examples are around ~0.77-0.80, so 0.60 still leaves margin while clearly excluding the ~0.3 failure regime.

Do not rely on periodicity alone.

## 7.5 Relative quality gate

Compare candidate against the same fixed evaluation suite and previous approved checkpoint when available.

Reject/review if candidate shows:

```text
large periodicity collapse
large ZCR increase
large spectral-centroid increase
new clipping
new long-tail noise
new EOS failures
speaker identity drift
accent-policy failure
```

---

# 8. Human acceptance gate

Automatic metrics cannot determine whether Tsuki sounds correct.

Human review is required.

## 8.1 Japanese review

Score/pass-fail:

```text
speaker identity
naturalness
Japanese pronunciation
pitch/prosody
breath/noise
metallic artifacts
sentence endings
word repetition
emotion consistency
```

## 8.2 English review

Explicitly review:

```text
same Tsuki voice identity
Japanese accent is present
accent is consistent across sentences
English remains intelligible
not generic American pronunciation
not generic Qwen English speaker
not caricatured/exaggerated
no language-switch artifacts
```

## 8.3 Compare to the user's supplied good outputs

The user's supplied good audio should remain a subjective quality target.

Important limitation:

Acoustic similarity metrics can establish waveform quality similarities, but they do not prove historical model provenance.

Do not claim the good sample came from Base or from a particular historical checkpoint unless artifact provenance is recovered.

---

# 9. Correct checkpoint selection

## 9.1 Remove hard-coded epoch-1 selection

Delete the assumption:

```python
ARTIFACT_CHECKPOINT = "checkpoint-epoch-1"
```

as a production-selection mechanism.

## 9.2 Candidate statuses

Use explicit states such as:

```text
TRAINED
REJECTED_GENERATION
REJECTED_AUDIO
REJECTED_IDENTITY
REJECTED_ACCENT
VALID_AUTOMATIC
VALID_HUMAN_REVIEW
APPROVED_FOR_STAGING
APPROVED_FOR_PRODUCTION
```

## 9.3 Packaging must fail closed

Production package function must require:

```text
APPROVED_FOR_PRODUCTION
```

Anything else should raise an exception.

No fallback to:

```text
epoch-1
latest checkpoint
Base model
voice clone
```

---

# 10. Correct final-training workflow

Use two stages.

## 10.1 Recipe-validation run

Purpose: prove the fixed SFT code.

Suggested configuration:

```text
Model start: Qwen/Qwen3-TTS-12Hz-0.6B-Base
Train samples: 425
Holdout samples: 25
Epochs: 1
LR: 2e-6
Speaker: tsukihime
Alignment fix: enabled
0.6B text projection fix: enabled
```

Then run Japanese + English-accent evaluation.

If it fails, stop.

Do not train more epochs.

## 10.2 Final production run

Only after the recipe-validation run passes:

```text
Train samples: 450
Holdout samples: 0
Epochs: begin with 1
LR: verified value from successful repair run
Speaker: tsukihime
```

Use all 450 Japanese recordings.

If epoch 1 passes strongly, optionally evaluate epoch 2.

Do not assume epoch 2 is better.

Select based on quality.

---

# 11. Reproducibility requirements

Every final run must record:

```text
Kaggle kernel version
notebook SHA-256
Qwen repository commit
qwen-tts version
transformers version
accelerate version
Torch version
CUDA version
GPU type
training arguments
learning rate
epoch count
batch size
gradient accumulation
weight decay
gradient clipping
all random seeds
dataset manifest SHA-256
dataset text SHA-256
number of training samples
speaker name
checkpoint SHA-256
speech tokenizer SHA-256
```

Never allow a production checkpoint whose exact training recipe cannot be reconstructed.

---

# 12. Azure production contract

## 12.1 Environment

Production should require values equivalent to:

```text
QWEN_TTS_MODE=trained-custom
QWEN_TTS_MODEL=/opt/tsuki-qwen3-tts/checkpoints/<approved-checkpoint>
QWEN_TTS_SPEAKER=tsukihime
QWEN_TTS_CHECKPOINT_ID=<approved immutable ID>
QWEN_TTS_GENERATION_VERSION=<trained generation version>
```

## 12.2 Startup validation

At startup verify:

```text
path exists
config.json exists
model.safetensors exists
speech tokenizer exists
tts_model_type == custom_voice
speaker tsukihime exists
checkpoint hashes match approved manifest
mode == trained-custom
```

If any check fails:

```text
service startup fails
```

Do not fall back.

## 12.3 Health endpoint

`/health` must clearly state:

```json
{
  "mode": "trained-custom",
  "speaker": "tsukihime",
  "reference_required": false,
  "reference_sha256": null,
  "runtime_lora_loaded": false,
  "model": "<trained checkpoint path>",
  "checkpoint_id": "<immutable checkpoint ID>"
}
```

It should never advertise full-ICL as the production identity.

---

# 13. Repository changes required

## 13.1 `runtime_config.py`

Change production default from:

```text
full-icl
```

to:

```text
trained-custom
```

Prefer even stricter behavior: require explicit trained checkpoint configuration and fail if absent.

Remove comments that describe Base/full-ICL as production.

## 13.2 `api.py`

Production behavior should be trained-custom only.

Recommended structure:

```text
Production engine:
  trained custom only

Optional diagnostic clone tooling:
  separate module/service/tool
```

Do not keep both architectures silently switchable under one production endpoint unless there is a strong operational reason.

If both remain in the same file temporarily, production config must prohibit full-ICL.

## 13.3 `validation.py`

Add controlled support for:

```text
Auto
```

for accent experiments.

After testing, define explicit production language policy.

## 13.4 `TtsLanguageDetector`

Do not let generic text language detection directly determine the final Qwen pronunciation mode.

Separate:

```text
text script/language detection
```

from:

```text
Tsuki pronunciation policy
```

Example conceptual API:

```csharp
TsukiPronunciationMode ResolvePronunciationMode(string text)
```

with explicit modes such as:

```text
Japanese
JapaneseAccentedEnglish
```

## 13.5 `VoiceConversationPipeline`

Pass the explicit Tsuki pronunciation policy rather than raw generic `EN` / `JA` detection result.

## 13.6 README and architecture documentation

Rewrite top-level Qwen docs so the first statement is:

```text
Production uses the trained Tsukihime custom checkpoint directly.
```

Move Base/full-ICL material into a clearly marked historical/diagnostic section or remove it if obsolete.

## 13.7 Benchmark tests

Replace tests that require:

```text
mode=full-icl
```

with tests requiring:

```text
mode=trained-custom
speaker=tsukihime
reference_required=false
```

Add a test that fails if production health reports a Base model.

## 13.8 Systemd example

Change description/config from full-ICL service to trained Tsukihime custom model service.

## 13.9 Deployment identity defaults

Remove defaults such as:

```text
full-icl-v1
```

Use a trained-checkpoint generation identity.

---

# 14. UI scope clarification

The supplied Hugging Face/Qwen screenshot is **not** part of TsukiAI and does not create a UI task for this repair.

Do not implement, remove, or redesign TsukiAI UI based on that screenshot unless separately requested.

This repair is about the actual model/runtime/training path:

```text
trained Tsukihime custom checkpoint
generate_custom_voice()
Japanese voice identity
Japanese-accented English behavior
```

If TsukiAI has any existing internal/debug UI that exposes model state, it may show checkpoint/runtime information, but UI work is not required for this TTS repair plan.

---

# 15. Accent-specific acceptance test

Create a fixed test file, for example:

```text
voice/qwen3-tts/tests/english_accent_prompts.json
```

Include 10-20 lines such as ordinary conversational English with phonemes that expose accent behavior.

Do not encode offensive stereotypes.

For every candidate checkpoint generate each line under:

```text
English
Auto
Japanese
```

Human reviewer selects which conditioning mode best preserves:

```text
Tsuki identity
Japanese accent
intelligibility
naturalness
```

Then lock that policy in code and tests.

## 15.1 Mandatory post-fix comparison against the user's two reference targets

After the repaired trained-custom model is complete and before production approval, compare the repaired model against these exact user-provided reference files.

### English target reference

File:

```text
C:\Users\LENOVO\Downloads\goodoutputtts.ogg
```

Locked transcript:

```text
This is the three sentence Azure and the Colab voice test. The custom domain is carrying the request to Colab. The Discord bridge is sending the result as a real voice message.
```

Purpose:

- this is the English quality/accent target;
- synthesize this exact English transcript through the repaired **trained `tsukihime` checkpoint only**;
- do not use reference-audio cloning to reproduce it;
- compare the repaired output against `goodoutputtts.ogg` for Tsuki identity, Japanese-accented English, naturalness, harmonic stability, pitch/prosody, noise, and phrase continuity;
- the repaired result must not drift toward a generic/native American Qwen English voice.

### Japanese target reference

File:

```text
C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3
```

Locked transcript:

```text
こんにちは。月だよ。今夜も君と話せて嬉しいな。月の光が綺麗な夜は心が静かになるんだ。君はどう？君の声好きだよ。聞くたびに安心するんだ。今日はどんな一日だった？ゆっくり聞かせてね。夜風が気持ちいい季節だね。これからの時間楽しもう。君が笑うと私まで嬉しくなるんだ。いつでもここにいるからね。夜が終わっても。
```

Purpose:

- this is the Japanese identity/timbre/prosody target;
- synthesize this exact Japanese transcript through the repaired **trained `tsukihime` checkpoint only**;
- do not use this MP3 as runtime reference conditioning;
- compare the repaired output against `tsuki_25s_clip.mp3` for speaker identity, pitch range, phrasing, rhythm, spectral character, naturalness, and artifact/noise behavior.

### Required comparison procedure

For both reference targets:

1. Verify the source file exists and record its SHA-256 before comparison.
2. Generate the exact locked transcript with the repaired trained-custom checkpoint.
3. Save the raw model WAV before resampling, Discord conversion, or Opus encoding.
4. Decode/reference-normalize only for analysis; do not alter the candidate used for listening review.
5. Compare duration-independent voiced-signal properties so different utterance lengths do not bias the result.
6. Record at minimum:

```text
sample rate
channels
duration
peak
RMS
clipping ratio
silence ratio
periodicity / voiced autocorrelation
voiced-frame ratio
HNR when available
F0 median and 10th/90th percentiles
zero-crossing rate
spectral centroid
spectral flatness
high-frequency energy ratio
```

7. Perform a human A/B review of the repaired model versus the corresponding reference file.
8. Store the generated comparison WAVs and a machine-readable metrics report with the checkpoint artifact.

### Comparison interpretation

Do not use waveform/acoustic similarity alone to claim that a historical model or checkpoint produced either reference file.

Use the two files as **quality and voice-behavior targets**:

```text
goodoutputtts.ogg
→ English Tsuki identity + Japanese-accented English + naturalness target

tsuki_25s_clip.mp3
→ Japanese Tsuki identity + timbre + rhythm/prosody target
```

The repaired checkpoint passes this final comparison only if:

```text
Japanese output is recognizably the same Tsuki voice family as tsuki_25s_clip.mp3
English output retains that Tsuki identity
English output has the intended Japanese accent
English output does not sound like generic/native American Qwen English
no known-bad ~0.3-periodicity / noisy acoustic collapse is present
no runaway EOS behavior is present
human review judges the repaired output materially closer to the supplied good targets than the current rejected checkpoint
```

If either exact local reference file is missing at execution time, stop the final comparison and report the missing file. Do not silently substitute another recording.

---

# 16. Preventing future accidental regression

Add CI/runtime tests for the following.

## 16.1 Production cannot use Base

Test fails if:

```text
QWEN_TTS_MODEL == Qwen/Qwen3-TTS-12Hz-0.6B-Base
```

in production mode.

## 16.2 Production cannot use voice clone

Test that production synthesis never calls:

```text
generate_voice_clone
```

## 16.3 Reference files are not required

Production startup test should succeed with no reference audio configured when trained checkpoint is valid.

## 16.4 Speaker must be Tsukihime

Fail if configured checkpoint does not contain:

```text
tsukihime
```

## 16.5 Rejected artifact cannot package

Unit test:

```text
checkpoint status = REJECTED_*
→ package_artifact must fail
```

## 16.6 All-450 final dataset gate

For final production training mode:

```text
metadata rows = 450
wav files = 450
train rows = 450
```

must be asserted.

No silent 425/25 split.

## 16.7 Accent policy regression test

The application must have an explicit test proving English does not automatically map to generic Qwen English mode unless that mode has been chosen by the accent evaluation.

---

# 17. Recommended implementation sequence

Execute in this order.

```text
1. Freeze current bad checkpoint as regression evidence
2. Mark old Base/full-ICL production docs as obsolete
3. Make trained-custom the only production architecture
4. Remove/disable legacy production voice-clone/reference code paths if they are still reachable; do not infer UI work from the external Hugging Face screenshot
5. Fix SFT talker alignment
6. Fix SFT sub-talker alignment
7. Keep 0.6B text_projection fix
8. Lower LR to 2e-6 for repair validation
9. Train one validation epoch
10. Evaluate Japanese raw WAV quality
11. Evaluate English accent under English/Auto/Japanese conditioning
12. If training recipe passes, prepare final 450/0 run
13. Train final model using all 450 Japanese samples
14. Evaluate epoch 1
15. Optionally evaluate epoch 2 only if epoch 1 is healthy
16. Select checkpoint based on objective + human quality
17. Mark selected checkpoint APPROVED_FOR_STAGING
18. Package only approved checkpoint
19. Deploy to separate Azure staging service/port
20. Verify artifact hashes on Azure
21. Run Japanese staging tests
22. Run English Japanese-accent staging tests
23. Run the mandatory exact-file comparison against `goodoutputtts.ogg` and `tsuki_25s_clip.mp3`
24. Save raw comparison WAVs + metrics + human A/B verdict with the artifact
25. Test TsukiAI audio path
26. Approve production only if both reference-target comparisons pass
27. Promote exact staged artifact
28. Verify /health shows trained-custom only
29. Preserve artifact + manifest + rollback package
```

---

# 18. Stop conditions

Stop training/deployment immediately if any of these occurs:

```text
periodicity collapses toward ~0.3
short prompts hit token cap
short prompts generate extreme durations
checkpoint is marked REJECTED
speaker tsukihime is absent
model type is not custom_voice
English sounds like a generic/native American Qwen voice
Japanese speaker identity drifts
raw WAV becomes noisy/buzzy/metallic
checkpoint hash differs between Kaggle and Azure
production tries to use reference audio or voice cloning
```

Do not work around these failures by increasing caps or hiding them with post-processing.

---

# 19. Definition of done

The TTS repair is complete only when all conditions below are true.

## Architecture

- [ ] production loads a trained custom Tsukihime checkpoint only
- [ ] Base model is not a production inference fallback
- [ ] `generate_voice_clone()` is not used in production
- [ ] no runtime reference audio is required
- [ ] no runtime reference transcript is required
- [ ] `/health` reports `trained-custom`
- [ ] speaker is `tsukihime`

## Training

- [ ] talker alignment fixed
- [ ] sub-talker alignment fixed
- [ ] 0.6B text projection fix retained
- [ ] repaired learning rate validated
- [ ] recipe-validation run passes
- [ ] final production run uses all 450 Japanese samples
- [ ] exact training environment recorded

## Japanese quality

- [ ] no runaway EOS behavior
- [ ] periodicity remains in healthy range
- [ ] no known-bad acoustic signature
- [ ] natural Japanese pronunciation
- [ ] stable Tsuki identity
- [ ] exact transcript from `C:\Users\LENOVO\Downloads\tsuki_25s_clip.mp3` has been regenerated with the trained checkpoint
- [ ] repaired Japanese output has been A/B compared against `tsuki_25s_clip.mp3`
- [ ] output subjectively matches the user's good-quality target much more closely than the current bad output

## English accent

- [ ] English uses the trained Tsuki speaker
- [ ] English does not sound like generic American/native Qwen English
- [ ] Japanese accent is clearly present but intelligible
- [ ] accent behavior is consistent
- [ ] exact transcript from `C:\Users\LENOVO\Downloads\goodoutputtts.ogg` has been regenerated with the trained checkpoint
- [ ] repaired English output has been A/B compared against `goodoutputtts.ogg`
- [ ] chosen conditioning mode is documented and locked in tests
- [ ] if needed, same-speaker Japanese-accented English training data has been added intentionally

## Release safety

- [ ] rejected checkpoint cannot package
- [ ] staging required before production
- [ ] Kaggle/Azure hashes match
- [ ] repeated generations tested
- [ ] both exact reference files were verified by SHA-256 before final comparison
- [ ] raw comparison WAVs, metrics report, and human A/B verdict are archived with the candidate artifact
- [ ] rollback artifact preserved

---

# 20. Key facts future agents must not reinterpret

1. The final system is a **trained voice model**, not a runtime voice clone.
2. Base Qwen is only the pretrained starting point for training, not the final production voice.
3. The current Kaggle dataset contains 450 Japanese samples, but the previous run trained on only 425.
4. The final production training requirement is to use all 450 samples.
5. The current epoch-1 artifact is acoustically degraded and was marked `REJECTED_OTHER`.
6. Azure is loading that exact rejected artifact.
7. The current English route explicitly uses Qwen `language="English"`, which can push pronunciation toward generic/native English.
8. Japanese-accented English must be treated as an explicit production requirement and tested separately.
9. The repo still contains obsolete Base/full-ICL production assumptions and must be cleaned up.
10. The user's good audio is a quality target, but waveform metrics alone do not prove which historical model produced it.

---

# 21. Final target architecture

```text
                  DATASET
       450 Japanese Tsukihime WAVs
                  │
                  ▼
       Qwen3-TTS 0.6B pretrained base
         (initial weights only)
                  │
                  ▼
          FIXED CUSTOM SFT
     - corrected label alignment
     - corrected sub-talker alignment
     - 0.6B text projection
     - validated LR
                  │
                  ▼
       TRAINED TSUKIHIME CHECKPOINT
         tts_model_type=custom_voice
         speaker=tsukihime
                  │
                  ▼
            PRODUCTION API
          generate_custom_voice
                  │
          ┌───────┴────────┐
          │                │
     Japanese text     English text
          │                │
     Japanese mode     verified Japanese-
                       accented English mode
          │                │
          └───────┬────────┘
                  ▼
            RAW TSUKI WAV
                  │
             quality gate
                  │
                  ▼
         TsukiAI / Discord output
```

This is the architecture to implement and preserve.
