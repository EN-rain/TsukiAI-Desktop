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
├── models/checkpoints_v2/       # converter/config.json and checkpoint.pth
├── voices/references/tsuki.wav  # normalized once from the supplied MP3
├── voices/embeddings/tsuki_se.pth
├── voices/embeddings/base_*.pth # derived once per MeloTTS base speaker
└── voice_registry.json
```

Install the API requirements in the CPU virtual environment, then install the
official OpenVoice checkout and MeloTTS according to their current upstream
instructions. Do not copy secrets into this repository. Put the API key in
`/opt/openvoice/openvoice.env` with mode `600`.

The historical OpenVoice V2 ZIP URL in older instructions is not treated as a
guaranteed source. The deployment must verify the converter files before
starting the service; the provisioning record should retain the exact source
and checksum used.
