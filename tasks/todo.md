# Qwen3-TTS 0.6B CPU Deployment Tasks

- [x] Add the isolated Qwen3-TTS full-ICL API and pure validation tests.
- [x] Wire the .NET API, desktop app, web settings, compose, and bridge docs to
      the Qwen contract.
- [x] Remove the abandoned alternate TTS implementation and its deployment
      artifacts from the repository.
- [ ] Install `qwen-tts` and the official 0.6B Base model on the private Azure
      VM with CPU/float32/eager attention.
- [ ] Transfer the exact Japanese reference and transcript.
- [ ] Build and cache the full ICL voice prompt with no training.
- [ ] Validate non-silent English WAV output and record RTF/RMS/peak/hash.
- [ ] Stop and remove the old remote TTS service and files.
- [ ] Restart TsukiAI API and Discord bridge with Qwen only.
- [ ] Send and verify a real Discord voice message in the existing chat.
- [ ] Run .NET, Node, Python, and web checks; scan the repository for stale
      backend references and secrets before committing.
