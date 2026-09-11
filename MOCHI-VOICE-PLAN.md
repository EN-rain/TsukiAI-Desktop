# TsukiAI Tsuki Voice — Plan (v5)

**Workflow:**

```
JP path (unchanged): VOICEVOX Tsuki → already in bridge, no work needed

EN path (new):
  Tsuki JP ref → upload to MiniMax voice clone
    → MiniMax TTS × 250 EN sentences → download audio
    → Kaggle training (GPT-SoVITS)
    → ONNX export → FastAPI server on VPS
    → Discord bridge uses it
```

MiniMax is used **once** to generate the EN dataset, then never again for inference. JP stays on the existing VOICEVOX integration.

---

## Phase 1 — MiniMax voice clone + EN dataset generation

### 1a. Upload Tsuki ref to MiniMax voice clone
- Use the ref audio from `mochi-voice/ref/` (curated Tsuki samples)
- POST to MiniMax voice clone API → receive `voice_id`
- Save `voice_id` to `mochi-voice/.env` as `MOCHI_CLONE_ID`

### 1b. Generate 250 EN audio clips
- Read sentences from `mochi-voice/data/sentences_en.txt`
- Call MiniMax TTS with `voice_id = MOCHI_CLONE_ID` for each
- Save audio to `mochi-voice/data/wav/0001.wav` … `0250.wav`
- Run `mochi-voice/data/gen_en_dataset.py` (calls MiniMax API)

---

## Phase 1 — DONE (2026-09-08)

F5-TTS zero-shot cross-lingual (replaced MiniMax — clone quality was poor).

- Kaggle kernel `edriannieves/mochi-f5tts-en-dataset` (v18, 18 iterations of debugging)
- Recipe: Tsuki relax-003 ref (true Whisper transcript) @ speed 1.15, raw English text
- **Output: `edriannieves/mochi-train-en-v2`** — 251 clips, 19.3 min, 251/251 success
- Key fixes along the way: torch 2.4.1+cu121 for P100, torchvision pin, show_info=lambda, file_wave (not file_spec), true ref transcript (fixed fast+ringing), accent attempt reverted per user

---

## Phase 2 — Kaggle training (~5–7 hr)

**Notebook:** `mochi-voice/kaggle/mochi_gpt_sovits_train.ipynb`

1. Setup + clone GPT-SoVITS
2. Mount `edriannieves/mochi-train-en-v2` (251 EN clips + train.list)
3. Mount `edriannieves/mochi-ref-voice` (JP refs)
4. Preprocess: ASR labels, audio segmentation, HuBERT features (~20 min)
5. Train SoVITS acoustic model (~2 hr, GPU)
6. Train GPT timbre/language (~2 hr, GPU)
7. Test inference in-notebook (1 EN + 1 JP)
8. Package → Kaggle Dataset `edriannieves/mochi-model-v1`

---

## Phase 3 — VPS deployment (CPU inference)

1. `kaggle datasets download` → `/opt/mochi-voice/models/`
2. FastAPI server in `mochi-voice/server/`: `POST /synth {text, lang}` → WAV
3. docker-compose, port 5180, healthcheck

---

## Phase 4 — TsukiAI bridge

`engines/mochi_voice.py` — httpx client to `:5180/synth`.

```
EN → mochi_voice (primary) → minimax (fallback)
JP → voicevox (unchanged)
```

---

## Phase 5 — A/B + ship

20 held-out EN sentences, latency check, fallback test, 5–10 real Discord replies. Ship if accent clean + timbre holds + latency <8 s.

---

## Phase 1 — MiniMax voice clone + English dataset generation

1. Upload Tsuki ref audio to MiniMax voice clone API → get `voice_id`
2. Generate 250 English sentences via MiniMax TTS using that voice_id:
   ```
   POST /v1/text_to_speech
     voice_id: <tsukisan_clone>
     text: <EN sentence>
     → audio bytes (WAV)
   ```
3. Save each as `Data/mochi_en/wav/0001.wav` … `0250.wav`
4. Source sentences from public EN corpus (LJSpeech metadata text only, or write by hand)
5. **Result:** 250 English audio clips with Tsuki timbre, ready for training

Run locally or on the VPS (MiniMax API is just HTTP). ~30 min for 250 calls.

---

## Phase 2 — Kaggle training (~5–7 hr)

**Notebook:** `mochi-voice/kaggle/mochi_finetune.ipynb`

Single session, end-to-end:

1. Setup + clone base model repo (GPT-SoVITS or OpenVoice v2)
2. Mount `mochi-ref-voice` (JP) + upload 250 MiniMax-generated EN clips
3. Build `train.list`:
   ```
   wav/0001.wav|mochi|EN|<sentence text>
   wav/0002.wav|mochi|EN|<sentence text>
   ...
   wav/0501.wav|mochi|JP|<sentence text>   # JP ref clips
   ```
4. Preprocess (HuBERT features, ~15 min)
5. Train (~3–5 hr):
   - SoVITS acoustic model: ~2 hr
   - GPT timbre/language: ~2 hr
6. Test inference in-notebook (1 JP + 1 EN)
7. ONNX export
8. Package → Kaggle Dataset `mochi-model-v1`

The model now knows: Tsuki JP from ref audio + Tsuki English from MiniMax clips.

---

## Phase 3 — VPS deployment (CPU inference)

1. Pull `mochi-model-v1` from Kaggle to `/opt/mochi-voice/models/`
2. FastAPI server in `mochi-voice/server/`:
   ```
   POST /synth {text, lang} → 24 kHz WAV
   ```
3. docker-compose, port 5180, healthcheck, restart policy

CPU latency: 2–7 s for short sentences.

---

## Phase 4 — TsukiAI bridge

`engines/mochi_voice.py` — httpx client to `:5180/synth`.

Routing:
```
EN → mochi_voice (primary) → minimax (fallback)
JP → mochi_voice (primary) → voicevox (fallback)
```

---

## Phase 5 — A/B + ship

1. 20 held-out EN sentences — accent check
2. 10 held-out JP sentences — timbre check
3. Latency on VPS
4. Fallback test (kill server, minimax picks up)
5. 5–10 Discord replies in test channel

Ship if accent clean + timbre holds + latency <8 s.

---

## File layout

```
mochi-voice/
├── ref/                        # Tsuki JP samples (Phase 0)
├── KLPR_NOTES.md
├── kaggle/mochi_finetune.ipynb # Phase 2 training
├── server/                     # Phase 3 FastAPI
├── eng_mochi_voice.py          # MiniMax EN dataset generator
└── README.md
```

---

## Timeline

| Week | Phase | Cost |
|---|---|---|
| 1 | 0 + 1 (ref + MiniMax dataset) | MiniMax credits, Kaggle free |
| 2 | 2 (Kaggle train) | Kaggle free |
| 3 | 3 + 4 (deploy + bridge) | $0 |
| 4 | 5 (A/B, ship) | $0 |

End state: zero cloud dependency for TTS, single Tsuki voice across JP+EN.

---

## Open

1. **MiniMax voice clone slot** — confirm you have a free slot / credits for cloning + 250 TTS calls
2. **License** — read Tsuki's `policy.md` and confirm derivative training allowed
3. **Base model** — GPT-SoVITS or OpenVoice v2 for Phase 2