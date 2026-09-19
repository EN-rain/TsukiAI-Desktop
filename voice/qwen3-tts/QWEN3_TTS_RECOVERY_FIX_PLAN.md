# TsukiAI Qwen3-TTS Recovery and Repair Plan

> **SUPERSEDED:** Do not use this document as the production architecture plan. The authoritative replacement is `TRAINED_CUSTOM_VOICE_PRODUCTION_FIX_PLAN.md`, which reflects the clarified requirement that production must use the trained `tsukihime` custom checkpoint directly, not Base/full-ICL voice cloning.

Status: **Historical audit / superseded plan based on verified Kaggle, Azure VPS, source-code, and audio evidence collected on 2026-09-16**

Project root: `D:\New folder (2)\TsukiAI 1.0`

Primary goal: restore a human-like Tsuki voice and prevent another rejected TTS checkpoint from being deployed.

---

## 1. Executive summary

The current production TTS problem is not primarily caused by Discord, Opus, 24 kHz audio, sentence duration, or the 450-sample dataset itself.

The strongest evidence points to the **fine-tuning recipe and checkpoint-selection/deployment pipeline**.

Verified observations:

- The Qwen3-TTS Base model produces acoustically healthy evaluation audio.
- Every trained checkpoint examined (`epoch-0`, `epoch-1`, `epoch-2`) shows severe acoustic degradation compared with Base.
- The trained checkpoints fail normal termination during Kaggle evaluation and produce ~163.76-second outputs for short prompts, consistent with broken autoregressive/EOS behavior.
- Kaggle's own `checkpoint_status.json` marks all three trained checkpoints as `REJECTED_OTHER`.
- Despite this, the notebook packages `checkpoint-epoch-1` unconditionally.
- Azure is currently running that exact Kaggle epoch-1 artifact.
- SHA-256 hashes of the live Azure checkpoint match the Kaggle artifact exactly.
- The user's bad TTS recording acoustically matches the Kaggle epoch-1 output much more closely than it matches the Base model or the user's good recordings.
- The current training recipe uses `lr=2e-5`; the repair baseline should use `2e-6`.
- The pinned fine-tuning path still contains the known talker/sub-talker label-alignment problem. The current notebook fixes the 0.6B `text_projection` shape issue, but does not fix the full loss-alignment issue.

Therefore, the next action must **not** be another training run with the current notebook. Fix the training objective, lower the learning rate, fix checkpoint gating, then retrain under controlled evaluation.

The previously healthy local reference was accidentally deleted. Do not assume it can be restored. A new control reference must be selected and verified from available clean speaker material if Base/full-ICL is used as a comparison control.

---

## 2. Current verified production state

### 2.1 Kaggle notebook

Relevant Kaggle kernel:

`edriannieves/tsuki-qwen3-tts-0-6b-sft`

Current published run reports:

- Model: `Qwen/Qwen3-TTS-12Hz-0.6B-Base`
- Training samples: 425
- Holdout samples: 25
- Epochs: 3
- Packaged artifact: `tsukihime_qwen06b_checkpoint-epoch-1.zip`
- Artifact size: `2,498,388,730` bytes
- Artifact SHA-256: `6fa38afa302d3ca7e89d86455dd009147b47654f075e2d6a4a5a705f4ad973cf`

The notebook's checkpoint report states:

```json
{
  "epoch-0": "REJECTED_OTHER",
  "epoch-1": "REJECTED_OTHER",
  "epoch-2": "REJECTED_OTHER"
}
```

However, packaging is hard-coded to:

```python
ARTIFACT_CHECKPOINT = "checkpoint-epoch-1"
```

and `package_artifact()` selects it without requiring the evaluation status to be valid.

This is a release-gate bug.

### 2.2 Azure VPS

Live Qwen environment observed:

```text
QWEN_TTS_MODE=trained-custom
QWEN_TTS_MODEL=/opt/tsuki-qwen3-tts/checkpoints/tsuki-qwen3-tts-0-6b-sft/checkpoint-epoch-1
QWEN_TTS_CHECKPOINT_ID=tsukiai-tsukihime-qwen3-tts-0.6b-sft-kaggle-clean-20260916-epoch-1
QWEN_TTS_GENERATION_VERSION=clean-kaggle-epoch-1-v1
QWEN_TTS_SPEAKER=tsukihime
QWEN_TTS_DEVICE=cpu
QWEN_TTS_ATTN_IMPLEMENTATION=eager
QWEN_TTS_MAX_NEW_TOKENS=2048
```

The live checkpoint hashes matched Kaggle:

```text
main model:
6a2da7e5004be6e7b0500b56221d0ca34cd6b8e627aea776c2c8870ea7d78353

speech tokenizer:
836b7b357f5ea43e889936a3709af68dfe3751881acefe4ecf0dbd30ba571258
```

Therefore this is not an artifact-copy mismatch. The rejected Kaggle epoch-1 checkpoint is the deployed checkpoint.

---

## 3. Acoustic evidence

### 3.1 User recordings

| Audio | Periodicity | ZCR | Spectral centroid |
|---|---:|---:|---:|
| Good outputs | ~0.766-0.785 | ~0.052-0.058 | ~1398-1504 Hz |
| Bad output | ~0.360 | ~0.0877 | ~1937 Hz |

### 3.2 Kaggle evaluation

Across three prompts:

| Model | Median periodicity | Median ZCR | Median spectral centroid |
|---|---:|---:|---:|
| Base | ~0.798 | ~0.0522 | ~1465 Hz |
| Epoch 0 | ~0.314 | ~0.1211 | ~2738 Hz |
| Epoch 1 | ~0.319 | ~0.0919 | ~2170 Hz |
| Epoch 2 | ~0.262 | ~0.2735 | ~4033 Hz |

For one direct comparison:

```text
User bad output:
periodicity = ~0.360
ZCR         = ~0.0877
centroid    = ~1937 Hz

Kaggle epoch-1 prompt_01:
periodicity = ~0.332
ZCR         = ~0.0877
centroid    = ~1931 Hz
```

The bad recording is far closer to epoch-1 than to Base.

### 3.3 Generation termination evidence

For the same short evaluation prompt:

```text
Base model:   ~3.92 seconds
Epoch 0:     ~163.76 seconds
Epoch 1:     ~163.76 seconds
Epoch 2:     ~163.76 seconds
```

All trained checkpoints hitting essentially the same extreme duration is consistent with broken EOS/autoregressive generation rather than normal speaker/style variation.

The Azure service currently applies a runtime cap that prevents the full runaway, but a cap does not repair the underlying model distribution. It only truncates the failure.

---

## 4. Root-cause assessment

### 4.1 High confidence: training objective / alignment is wrong

The pinned Qwen SFT path manually shifts talker inputs and labels before passing them into a causal LM loss path that performs its own shifting.

Current pattern in the pinned SFT code:

```python
outputs = model.talker(
    inputs_embeds=input_embeddings[:, :-1, :],
    attention_mask=attention_mask[:, :-1],
    labels=codec_0_labels[:, 1:],
    output_hidden_states=True,
)
```

This is the area covered by the known upstream talker/sub-talker alignment correction. The repaired implementation must follow a verified upstream-compatible alignment rather than hand-applying an extra label shift.

The notebook already contains a separate 0.6B text-projection repair:

```python
model.talker.text_projection(
    model.talker.model.text_embedding(input_text_ids)
)
```

Keep the 0.6B projection repair. Do not confuse it with the loss-alignment repair; they solve different problems.

### 4.2 High confidence: learning rate is too aggressive for the repair baseline

Current notebook:

```text
learning_rate = 2e-5
```

Repair baseline:

```text
learning_rate = 2e-6
```

Do not change multiple optimization parameters at the same time during the first repaired run. Keep batch size, gradient accumulation, weight decay, gradient clipping, and dataset constant unless a verified incompatibility requires a change.

### 4.3 High confidence: checkpoint packaging ignores failed evaluation

Current behavior:

```text
train checkpoint
    -> evaluate
    -> mark REJECTED_OTHER
    -> ignore rejection
    -> package hard-coded epoch-1
    -> deploy
```

Required behavior:

```text
train checkpoint
    -> automatic acoustic/EOS evaluation
    -> human review
    -> PASS only
    -> package selected PASS checkpoint
    -> stage
    -> production
```

### 4.4 Medium confidence: retraining reproduces the failure because the recipe is repeated

Version 13 using the same dataset does not prove the dataset is bad. If the same model, code, loss alignment, LR, and similar seeds are used again, the same failure mechanism is expected to recur.

### 4.5 Historical version 11 remains unresolved

The user reports that Kaggle version 11 previously sounded good.

Do not overwrite this observation, but do not claim a verified code-level explanation yet.

Historical source retrieval was not reliable during this audit. Version-qualified output/report queries returned byte-identical current artifacts, so they cannot be used as trustworthy historical source evidence.

Possible explanations to investigate only if historical files/backups become available:

- version 11 used a different training script;
- version 11 used different Qwen package/repository revisions;
- version 11 used a different learning rate;
- version 11 used Base/full-ICL rather than trained-custom for the remembered good sample;
- version 11 used a different checkpoint or speaker embedding;
- version 11 had different generation settings.

Do not choose one of these as fact without the real historical notebook/source.

---

# 5. Repair plan

## PHASE A - Preserve evidence and freeze the failing state

### A1. Preserve the current failed artifact

Keep the current epoch-1 checkpoint for regression comparison. Label it clearly as:

`KNOWN_BAD_DO_NOT_DEPLOY`

Record:

- checkpoint SHA-256;
- speech-tokenizer SHA-256;
- model config;
- generation config;
- Kaggle kernel/run identifier;
- notebook source hash;
- package versions;
- Qwen repository commit;
- dataset text SHA-256;
- evaluation metrics;
- representative bad raw WAVs.

### A2. Preserve representative audio

Keep at least:

- 3 Base evaluation WAVs;
- 3 current epoch-1 bad evaluation WAVs;
- the user's bad sample if available;
- the user's good samples if available.

These become regression fixtures.

### A3. Do not overwrite Azure production files during diagnosis

Any candidate model must first run as a separate staging service/port.

---

## PHASE B - Fix the fine-tuning notebook

### B1. Fix talker/sub-talker alignment

Do not invent a custom shift. Backport the verified upstream alignment fix compatible with the exact pinned Qwen package/repository version.

Required semantic outcome:

- do not manually shift talker inputs/labels and then allow the causal loss to shift them again;
- align hidden states and codec labels exactly once;
- apply the matching sub-talker alignment correction;
- if the chosen upstream implementation uses explicit `shift_labels`, preserve that exact contract.

Add a smoke/unit test that verifies tensor lengths and masks align without an extra one-token displacement.

### B2. Keep the 0.6B text projection fix

The repaired notebook must retain the 0.6B projection logic.

### B3. Lower learning rate

Set:

```text
2e-6
```

for the first repaired training run.

Do not start at `2e-5`.

### B4. Train one epoch first

Initial repair run:

```text
num_epochs = 1
```

Only increase to epoch 2 after the first epoch passes objective acoustic/EOS gates and human review.

### B5. Make training reproducible

Record and set, where applicable:

```text
Python random seed
NumPy seed
Torch CPU seed
Torch CUDA seed
DataLoader shuffle seed/generator
Kaggle environment/package versions
Qwen repository commit
qwen-tts version
transformers version
accelerate version
Torch/CUDA version
```

### B6. Keep the dataset fixed for the first repaired experiment

Do not edit transcripts, replace samples, alter speaker IDs, change reference selection, and change optimizer behavior simultaneously.

The first experiment should answer one question:

> Does fixing alignment + reducing LR repair generation using the existing dataset?

Only investigate dataset changes if the repaired training recipe still fails.

---

## PHASE C - Rebuild a control reference

The old healthy local reference was deleted. Treat it as unavailable.

A replacement is needed only to build a Base/full-ICL control; SFT repair itself does not require recovering the exact deleted file.

### C1. Select 3-5 reference candidates from clean source material

Requirements:

- lossless WAV;
- 24 kHz mono preferred;
- exact transcript available;
- no clipping;
- low background noise;
- no music;
- no heavy reverb;
- stable speaker identity;
- neutral/natural delivery;
- no extreme emotion;
- preferably 5-15 seconds;
- clean start/end boundaries.

Do not automatically use `mochiko_0001.wav` just because the current notebook hard-coded it.

### C2. Evaluate reference candidates

Generate the same fixed prompts with Base/full-ICL using each candidate.

Compare:

- speaker identity;
- pronunciation;
- periodicity;
- HNR/noise;
- ZCR;
- spectral centroid;
- clipping;
- phrase continuity;
- subjective naturalness.

Select one control reference only after A/B review.

### C3. Preserve the selected reference permanently

Store:

```text
reference WAV
exact transcript
SHA-256
sample rate
channel count
duration
selection reason
```

Keep copies in at least two independent locations.

---

## PHASE D - Build a proper evaluation gate

### D1. Use fixed held-out prompts

Keep the 20 existing non-training evaluation prompts unless a verified reason requires replacement.

Use exactly the same prompt set for:

- Base control;
- repaired epoch 1;
- repaired epoch 2 if tested;
- Azure staging.

### D2. Generate raw WAV first

All quality decisions must be made on raw model WAV output before:

- Discord conversion;
- Opus encoding;
- concatenation;
- resampling;
- application volume processing.

### D3. Fail on runaway generation

For short evaluation sentences:

- no candidate may generate ~160-second output;
- any output exceeding the existing 20-second sanity boundary must be rejected/reviewed;
- if generation reaches `max_new_tokens` instead of terminating naturally, mark the prompt failed;
- repeated EOS/cap failures reject the checkpoint.

The runtime cap may remain as a safety mechanism, but it must never convert a failed model into a passing model.

### D4. Add acoustic metrics

At minimum record for every evaluation WAV:

```text
duration
sample rate
channels
peak
RMS
clipping ratio
silence ratio
DC offset
periodicity/autocorrelation voicing score
zero-crossing rate
spectral centroid
high-frequency energy ratio
```

Optional but useful:

```text
HNR
F0 coverage
F0 median / percentile range
spectral flatness
voiced-frame ratio
```

### D5. Initial provisional acceptance thresholds

These are project-specific starting gates based on the observed healthy-vs-failed separation. Recalibrate as more verified good samples become available.

Hard reject if any of the following occurs repeatedly:

```text
median periodicity < 0.60
multiple outputs > 20 s for short prompts
multiple generation-cap/EOS failures
clipping ratio > 0.001
unreadable/empty audio
wrong sample format
```

Relative-to-Base reject/review criteria:

```text
candidate median periodicity < 80% of Base median
candidate median ZCR > 1.35x Base median
candidate median spectral centroid > 1.5x Base median without a clear phonetic reason
```

Observed separation:

```text
healthy Base periodicity ~0.80
known-bad epoch-1 periodicity ~0.32
```

A checkpoint around `0.3-0.4` must never pass.

Do not use a single metric alone. Combine automatic metrics with raw-audio review.

### D6. Human review is mandatory

For any candidate that passes automatic checks, manually review at least 5-10 fixed prompts for:

- Tsuki speaker identity;
- Japanese pronunciation;
- natural phrasing;
- pitch movement;
- breathiness/noise;
- metallic/AI texture;
- word repetition;
- truncation;
- EOS behavior.

Training loss must never be used as the quality selector.

---

## PHASE E - Fix checkpoint selection and packaging

### E1. Remove hard-coded deployment selection

Remove the unconditional release choice:

```python
ARTIFACT_CHECKPOINT = "checkpoint-epoch-1"
```

Checkpoint selection must consume evaluation results.

### E2. Use explicit statuses

Recommended statuses:

```text
REJECTED_GENERATION
REJECTED_AUDIO
REJECTED_METRICS
REJECTED_HUMAN
VALID_REVIEW
APPROVED_FOR_STAGING
APPROVED_FOR_PRODUCTION
```

A checkpoint may only be packaged for production after all required gates pass.

### E3. Packaging must fail closed

Required contract:

```python
if selected_status not in {"APPROVED_FOR_STAGING", "APPROVED_FOR_PRODUCTION"}:
    raise RuntimeError("Refusing to package rejected/unreviewed checkpoint")
```

Do not silently fall back to epoch-1 or the newest checkpoint.

### E4. Artifact manifest must contain quality evidence

Include:

```text
checkpoint ID
checkpoint SHA-256
training notebook SHA-256
Qwen repo commit
package versions
dataset SHA-256
training arguments
seed
learning rate
epoch count
evaluation prompt set SHA-256
automatic metrics summary
human-review status
approval status
```

---

## PHASE F - Controlled retraining experiment

### F1. Run 1: repaired epoch 1

Target configuration:

```text
Model: Qwen/Qwen3-TTS-12Hz-0.6B-Base
Dataset: current verified 450-sample dataset
Train: 425
Holdout: 25
Epochs: 1
LR: 2e-6
Alignment: repaired upstream-compatible talker/sub-talker logic
0.6B projection fix: enabled
```

Do not change the dataset during the same experiment.

### F2. Evaluate immediately

Compare repaired epoch 1 against Base/control with the exact same prompts.

If any of these recur:

```text
~160 s runaway generation
periodicity collapse toward ~0.3
large ZCR increase
large spectral-centroid increase
repetitive/metallic/noisy raw speech
```

stop. Do not proceed to epoch 2.

### F3. Run 2 only if Run 1 passes

If epoch 1 is objectively and subjectively healthy, optionally train/evaluate epoch 2.

Choose between epoch 1 and epoch 2 by evaluation, not by lower loss.

---

## PHASE G - Azure staging

### G1. Do not replace production immediately

Create a separate candidate deployment, for example:

```text
qwen3-tts-candidate.service
127.0.0.1:8101
```

Keep the existing service untouched until the candidate is verified.

### G2. Verify artifact identity

Before starting candidate service:

```text
compare Kaggle artifact SHA-256
compare main model SHA-256
compare speech tokenizer SHA-256
verify config/checkpoint ID
```

No deployment should proceed on filename alone.

### G3. Run the fixed prompt suite through Azure

Generate the same held-out prompts through the staging HTTP API.

Save raw 24 kHz WAV responses and compare with Kaggle candidate behavior.

### G4. Test repeated generations

Because generation is stochastic, synthesize key prompts multiple times.

Recommended minimum:

```text
5 prompts x 3 generations each
```

Reject a candidate that occasionally collapses into noisy/aperiodic speech even if one sample sounds good.

### G5. Test application path only after raw WAV passes

Then test:

```text
raw Qwen WAV
-> TsukiAI HTTP client
-> resampling
-> Discord PCM
-> Opus/playback
```

If raw WAV is healthy but final playback degrades, investigate application audio processing separately.

---

## PHASE H - Production promotion

Production promotion requires all of the following:

- training objective repaired;
- LR reduced and recorded;
- checkpoint terminates normally;
- automatic evaluation passes;
- human review passes;
- Kaggle artifact hashes recorded;
- Azure staging hashes match;
- repeated staging generations remain stable;
- application playback verified;
- rollback artifact/config preserved.

Only then change the live environment to the new checkpoint.

After promotion, verify `/health` and generate a production smoke sample.

---

## 6. What not to do

Do not:

- train another version with the current broken notebook;
- assume the dataset is bad solely because a retrain sounds bad;
- use training loss as proof that TTS quality improved;
- automatically deploy epoch-1;
- deploy any checkpoint marked `REJECTED_*`;
- hide EOS failure using only a runtime duration/token cap;
- change dataset, optimizer, reference, model version, sampling, and deployment simultaneously;
- use Discord/Opus output as the first quality diagnostic;
- treat the deleted old reference as recoverable unless a real backup is found;
- claim version 11's exact code is known without retrieving its real historical source.

---

## 7. Recommended implementation order

Use this exact order:

```text
1. Preserve current bad checkpoint + evidence
2. Patch SFT talker/sub-talker alignment
3. Set LR to 2e-6
4. Add deterministic run metadata/seeds
5. Make rejected checkpoint packaging impossible
6. Add automatic raw-WAV quality metrics
7. Select/rebuild a clean control reference
8. Run repaired training for 1 epoch
9. Compare Base/control vs repaired epoch 1
10. Human-review fixed prompts
11. Only if pass: optionally evaluate epoch 2
12. Package selected PASS checkpoint
13. Deploy to Azure staging on separate service/port
14. Run repeated raw-WAV staging tests
15. Test TsukiAI/Discord path
16. Promote to production
17. Preserve rollback + manifest
```

---

## 8. Minimum acceptance checklist

### Training

- [ ] verified alignment fix applied
- [ ] 0.6B `text_projection` fix retained
- [ ] LR = `2e-6`
- [ ] first repaired run = 1 epoch
- [ ] dataset unchanged for first repair experiment
- [ ] package versions recorded
- [ ] Qwen commit recorded
- [ ] random seeds recorded/set

### Generation

- [ ] no repeated EOS failures
- [ ] no ~160 s outputs for short prompts
- [ ] no candidate selected merely because loss is lower
- [ ] raw WAVs generated for fixed evaluation suite

### Acoustic quality

- [ ] periodicity does not collapse toward known-bad ~0.3
- [ ] ZCR remains reasonably close to healthy Base/control
- [ ] spectral centroid does not show known-bad noise shift
- [ ] clipping within threshold
- [ ] speaker identity acceptable
- [ ] pronunciation acceptable
- [ ] no metallic/buzzy/unstable voice failures

### Packaging

- [ ] selected checkpoint status explicitly PASS/APPROVED
- [ ] package function rejects failed/unreviewed checkpoint
- [ ] artifact SHA-256 recorded
- [ ] evaluation report shipped with artifact

### Azure

- [ ] staging service used first
- [ ] hashes match Kaggle artifact
- [ ] repeated generations tested
- [ ] raw WAV passes before Discord processing
- [ ] production switch occurs only after staging approval

---

## 9. Definition of done

The problem is considered fixed only when all of the following are true:

1. A newly trained checkpoint produces short held-out prompts with normal termination.
2. Its periodicity/noise profile is close to healthy Base/control behavior and far from the known-bad epoch-1 signature.
3. Multiple repeated generations do not intermittently collapse into noisy/aperiodic speech.
4. Human review confirms natural Japanese pronunciation, stable Tsuki identity, and acceptable prosody.
5. The same checkpoint behaves the same way on Azure staging.
6. The final TsukiAI playback path preserves that quality.
7. The deployment system cannot package or promote a rejected checkpoint.
8. Every production artifact is reproducible from a recorded notebook/code revision, dataset hash, package versions, training arguments, and checkpoint hash.

---

## 10. Key diagnosis to preserve for future agents

Do not restart this investigation from the assumption that the 450-sample dataset is the cause.

The current evidence shows:

```text
healthy Base generation
        ↓
current fine-tuning recipe
        ↓
loss decreases
        ↓
trained generation/EOS breaks
        ↓
periodicity collapses from ~0.80 to ~0.3
        ↓
noise/ZCR/spectral centroid increase
        ↓
Kaggle marks checkpoints REJECTED_OTHER
        ↓
notebook ignores rejection and packages epoch-1
        ↓
Azure deploys exact rejected artifact
        ↓
user hears bad/AI-like TTS
```

The immediate repair target is the **training + evaluation + release pipeline**, not another blind retrain.
