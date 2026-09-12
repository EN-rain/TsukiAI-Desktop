# OpenVoice V2 CPU service

This is the only TTS backend used by TsukiAI. It runs privately on the Azure
`Standard_B4as_v2` VM and exposes:

- `GET /health`
- `GET /voices` (requires `X-Api-Key`)
- `POST /tts` (requires `X-Api-Key`, JSON `{ "text": "...", "language": "EN" }`)

The service loads the cached `tsuki` speaker embedding at startup. Runtime
requests cannot upload or select a reference file.

## Official runtime sources

Use the official [OpenVoice repository](https://github.com/myshell-ai/OpenVoice)
and [MeloTTS repository](https://github.com/myshell-ai/MeloTTS). OpenVoice V2's
documented multilingual base-TTS workflow uses MeloTTS and the V2 tone-color
converter. The official V2 usage notebook is the reference for the conversion
sequence.

## VM layout

```text
/opt/openvoice/
├── app/                         # this service plus the OpenVoice checkout
├── models/checkpoints_v2/       # converter plus official V2 base speakers
│   └── base_speakers/ses/       # en-newest.pth and jp.pth from OpenVoiceV2
├── voices/references/tsuki.wav  # normalized once from the supplied MP3
├── voices/embeddings/tsuki_se.pth
└── voice_registry.json
```

Install the API requirements in the CPU virtual environment, then install the
official OpenVoice checkout and MeloTTS according to their current upstream
instructions. Do not copy secrets into this repository. Put the API key in
`/opt/openvoice/openvoice.env` with mode `600`.

The target voice is extracted once from the supplied reference recording with
OpenVoice's official VAD-aware `se_extractor.get_se(..., vad=True)` path. The
runtime uses that original-reference embedding; any external-render
calibration is kept as a separate experiment/rollback artifact and is not
silently made active. The runtime also requires the official V2 base-speaker
embeddings matching the configured MeloTTS speakers; it deliberately fails
startup if those files are missing instead of creating a different embedding
from an arbitrary sentence.

The required base-speaker files are `en-newest.pth` and `jp.pth` from the
[OpenVoiceV2 base-speaker directory](https://huggingface.co/myshell-ai/OpenVoiceV2/tree/main/base_speakers/ses).

The English runtime is explicitly configured as `EN-NEWEST`. This selects
MeloTTS's separate `EN_NEWEST` model and its `EN-Newest` speaker ID, matching
the official OpenVoice V2 multi-accent demo. The source embedding is
`en-newest.pth`; it must remain paired with that model rather than with the
older `EN` speaker map.

The current Free.ai comparison configuration uses `OPENVOICE_BASE_SPEAKER_EN=EN-NEWEST`,
`OPENVOICE_SPEED_EN=1.0`,
`OPENVOICE_SPEED_JA=1.0`, and `OPENVOICE_OUTPUT_SAMPLE_RATE=24000`. The final
24 kHz WAV is a post-processing step after the official V2 converter, whose
native converter configuration is 22.05 kHz.

The historical OpenVoice V2 ZIP URL in older instructions is not treated as a
guaranteed source. The deployment must verify the converter files before
starting the service; the provisioning record should retain the exact source
and checksum used.
