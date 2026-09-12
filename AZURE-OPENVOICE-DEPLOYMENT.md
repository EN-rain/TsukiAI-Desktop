# Azure OpenVoice V2 deployment

TsukiAI uses one TTS backend: OpenVoice V2 on a private Azure
`Standard_B4as_v2` VM. The verified VM is:

```text
resource group: b4as-test-rg
VM:             b4as-test
display name:   Tsuki's Bedroom
region:         Malaysia West
size:           Standard_B4as_v2
OS:             Ubuntu 22.04.5 LTS
network:        private-only; no public IP
```

Azure keeps the resource name `b4as-test`; the friendly display name is stored
as the `displayName` tag because Azure VM resource names cannot be renamed
in-place. The temporary `tsuki-vps` resource group in East Asia has been
removed, leaving this as the only Azure VM for TsukiAI.

Southeast Asia was tested and returned Azure `SkuNotAvailable` capacity
failures for this SKU. Malaysia West provisioned successfully.

## Runtime layout

The VM keeps the API on `127.0.0.1:8000`. The C# container reaches the host
service through `host.docker.internal`; no public TTS port is opened.

The reference file is normalized once to
`/opt/openvoice/voices/references/tsuki.wav`. The original target embedding is
created with the official VAD-aware OpenVoice extractor; the active registry
uses the preserved Free.ai-render calibration
`/opt/openvoice/voices/embeddings/tsuki_freeai_se.pth` to match the supplied
HAR output. Requests never upload or re-process the reference audio.

The runtime uses the official V2 MeloTTS source embeddings at
`/opt/openvoice/models/checkpoints_v2/base_speakers/ses/en-us.pth` and
`jp.pth`. English is explicitly pinned to the `EN-US` speaker at speed `0.8`;
startup fails if either artifact is absent; it does not synthesize a replacement
embedding from an arbitrary sentence. The API masters the generated WAV with
the tested denoise/loudness filter before it reaches Discord or the C# audio
pipeline.

## Required environment

Set these server-side values in the root `.env`:

```dotenv
TSUKI_OPENVOICE_URL=http://host.docker.internal:8000
TSUKI_OPENVOICE_API_KEY=<same-key-used-by-the-openvoice-service>
```

The OpenVoice service keeps its key in `/opt/openvoice/openvoice.env` with mode
`600`. Do not commit either file.

## Service checks

```bash
curl -fsS http://127.0.0.1:8000/health
curl -fsS -H "X-Api-Key: $OPENVOICE_API_KEY" http://127.0.0.1:8000/voices
```

The API accepts only `POST /tts` with text, language (`EN` or `JA`), and the
fixed `tsuki` voice. Inference is serialized while the CPU baseline is
benchmarked.

## Official sources

- [OpenVoice](https://github.com/myshell-ai/OpenVoice)
- [OpenVoice usage instructions](https://github.com/myshell-ai/OpenVoice/blob/main/docs/USAGE.md)
- [MeloTTS](https://github.com/myshell-ai/MeloTTS)
- [OpenVoice V2 converter model](https://huggingface.co/myshell-ai/OpenVoiceV2)
- [OpenVoice V2 base-speaker embeddings](https://huggingface.co/myshell-ai/OpenVoiceV2/tree/main/base_speakers/ses)
