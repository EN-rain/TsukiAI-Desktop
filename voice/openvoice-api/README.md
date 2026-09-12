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
│   └── base_speakers/ses/       # en-us.pth and jp.pth from OpenVoiceV2
├── voices/references/tsuki.wav  # normalized once from the supplied MP3
├── voices/embeddings/tsuki_freeai_se.pth
└── voice_registry.json
```

Install the API requirements in the CPU virtual environment, then install the
official OpenVoice checkout and MeloTTS according to their current upstream
instructions. Do not copy secrets into this repository. Put the API key in
`/opt/openvoice/openvoice.env` with mode `600`.

The original target is extracted once from the supplied reference recording
with OpenVoice's official VAD-aware `se_extractor.get_se(..., vad=True)` path.
For Free.ai matching, the registry intentionally uses the separately preserved
`tsuki_freeai_se.pth` calibration produced from the user's exact Free.ai
OpenVoice render; the original-reference embedding remains available as a
rollback artifact. Runtime requests still never upload or process reference
audio. The runtime also requires the official V2 base-speaker embeddings
matching the configured MeloTTS speakers; it deliberately fails startup if
those files are missing instead of creating a different embedding from an
arbitrary sentence.

The required base-speaker files are `en-us.pth` and `jp.pth` from the
[OpenVoiceV2 base-speaker directory](https://huggingface.co/myshell-ai/OpenVoiceV2/tree/main/base_speakers/ses).

The current English experiment intentionally routes English text through
MeloTTS language `JP` with speaker `JP` at speed `1.0`. This is a
cross-lingual diagnostic requested for the Free.ai accent reconstruction; it
is not the normal English `EN-US` path. Japanese requests continue to use the
same MeloTTS `JP` source model. Rollback is `OPENVOICE_BASE_SPEAKER_EN=EN-US`.
In the manual Chrome verification, Free.ai showed `model=openvoice`, speed `1`,
and produced a 12.400-second WAV for the exact 189-character sentence. The
previous local `EN-US` speed-1 render was 12.411 seconds. An older HAR render
was 15.120 seconds with the same visible settings, so Free.ai's backend is not
duration-deterministic; the target and base-speaker choice are the important
matching controls. The separate `EN_NEWEST` path remains available on the VM
for comparison.

The current Free.ai comparison configuration uses
`OPENVOICE_BASE_SPEAKER_EN=JP`, `OPENVOICE_SPEED_EN=1.0`,
`OPENVOICE_SPEED_JA=1.0`, and `OPENVOICE_OUTPUT_SAMPLE_RATE=24000`. After
conversion, the API applies a fixed high/low-pass, FFT denoise, and loudness
normalization pass. This is a delivery-quality step after the official V2
converter, whose native converter configuration is 22.05 kHz; the final WAV is
24 kHz mono PCM.

The historical OpenVoice V2 ZIP URL in older instructions is not treated as a
guaranteed source. The deployment must verify the converter files before
starting the service; the provisioning record should retain the exact source
and checksum used.
