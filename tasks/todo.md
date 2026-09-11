# OpenVoice V2 CPU Deployment Tasks

- [x] Add isolated OpenVoice API service implementing the existing `/health` and `/tts` contract.
- [x] Add validation/unit tests for request limits, language allow-list, voice registry, and WAV response handling.
- [x] Remove alternate TTS provider registrations, routing modes, clients, deployment settings, and stale active documentation.
- [x] Install the CPU runtime and system dependencies on the private Azure VM.
- [x] Upload and normalize the Tsuki reference without committing it.
- [x] Download and verify the official OpenVoice V2 converter and MeloTTS model artifacts.
- [x] Extract and persist `tsuki` target speaker embedding once.
- [x] Start the service with a protected systemd unit and verify health/authentication.
- [x] Generate authenticated English and Japanese samples and validate RIFF/WAVE output.
- [x] Benchmark short/medium/long English and Japanese requests and record CPU/RAM/RTF.
- [x] Wire the existing TsukiAI application directly to OpenVoice and verify no alternate TTS route remains.
- [x] Remove the temporary `tsuki-vps` Azure resource group after verifying its resources.
- [x] Label the surviving `b4as-test` VM and resource group as `Tsuki's Bedroom`.
- [x] Review diff, scan staged files for secrets/audio artifacts, and create an atomic commit for only this feature.
