# Qwen3-TTS Gate 1 and Gate 2 Results — 2026-09-14

> **HISTORICAL / SUPERSEDED:** These results are for the former Base/full-ICL
> diagnostic path and cannot approve the trained-custom production voice.

All runs used the production Base/full-ICL identity, the Japanese reference, the same sampling profile, and an isolated sidecar cache. Every reported result was a valid, non-silent 24 kHz mono PCM WAV and a cache miss.

Reference SHA-256:

`ce93bb53f197f0683d46ab4ad240e3d434ef6e2b2340190be98dfe382a0f6530`

## Gate 1: attention and thread matrix

Runtime values are seconds; RTF is runtime divided by generated audio duration.

| Variant | 1 sentence | 2 sentences | 3 sentences | 6 sentences |
|---|---:|---:|---:|---:|
| A eager 4/4 | 45.082 / 2.960 / 15.230 | 66.535 / 6.400 / 10.396 | 83.161 / 8.880 / 9.365 | 146.983 / 18.160 / 8.094 |
| B SDPA 4/1 | 46.393 / 3.680 / 12.607 | 58.784 / 5.920 / 9.930 | 73.794 / 8.480 / 8.702 | 127.501 / 17.680 / 7.212 |
| C SDPA 3/1 | 46.908 / 3.600 / 13.030 | 58.999 / 5.680 / 10.387 | 79.112 / 9.200 / 8.599 | 129.491 / 17.600 / 7.357 |
| D SDPA 2/1 | 51.407 / 3.760 / 13.672 | 69.825 / 6.880 / 10.149 | 82.987 / 9.040 / 9.180 | 143.330 / 18.880 / 7.592 |
| E eager 4/1 | 50.034 / 3.600 / 13.898 | 63.789 / 5.680 / 11.230 | 89.965 / 9.520 / 9.450 | 148.802 / 17.680 / 8.416 |

Tuple format: `runtime_seconds / audio_seconds / RTF`.

**Decision:** B (SDPA, 4 intra-op / 1 inter-op) was the fastest across all four probe lengths and was promoted to production.

## Gate 2: maximum-token cap

The selected B runtime path was tested with the restored 1/2/3/6 sentence matrix. All caps generated valid audio, but output duration and runtime varied because sampling is enabled. No cap produced a stable, quality-proven speed improvement.

| Max tokens | 1 sentence | 2 sentences | 3 sentences | 6 sentences |
|---:|---:|---:|---:|---:|
| 2048 | 46.393 / 3.680 / 12.607 | 58.784 / 5.920 / 9.930 | 73.794 / 8.480 / 8.702 | 127.501 / 17.680 / 7.212 |
| 1536 | 43.642 / 3.200 / 13.638 | 60.775 / 6.240 / 9.740 | 77.485 / 9.040 / 8.571 | 137.954 / 19.120 / 7.215 |
| 1024 | 47.717 / 3.920 / 12.173 | 60.516 / 6.160 / 9.824 | 76.225 / 8.880 / 8.584 | 136.536 / 19.200 / 7.111 |
| 768 | 42.892 / 3.120 / 13.747 | 61.359 / 6.320 / 9.709 | 79.514 / 9.440 / 8.423 | 143.145 / 20.160 / 7.100 |
| 512 | 50.157 / 4.320 / 11.610 | 68.182 / 7.600 / 8.971 | 72.826 / 8.320 / 8.753 | 140.041 / 19.520 / 7.174 |

**Decision:** retain `QWEN_TTS_MAX_NEW_TOKENS=2048` until a quality/transcript review supports a lower cap.

## Production promotion

Production now reports:

- attention: `sdpa`
- intra-op threads: `4`
- inter-op threads: `1`
- max new tokens: `2048`
- mode: `full-icl`
- x-vector-only mode: `false`
- runtime LoRA: `false`
- reference SHA: the value above

The live 1- and 2-sentence smoke test returned HTTP 200, valid WAV output, and cache MISS:

| Probe | Wall seconds | Audio seconds | RTF |
|---|---:|---:|---:|
| 1 sentence | 43.988 | 3.440 | 12.786 |
| 2 sentences | 58.130 | 5.840 | 9.952 |

A rollback backup was retained on the VM under `/opt/tsuki-qwen3-tts/backups/`.

## Gate 3 preflight result

The pinned `qwen3-tts-rs` revision `711ceee07cad92673f86de8997bdf54c30caa49f` built successfully with CPU support. Its ICL path was exercised with both `--ref-audio` and `--ref-text`; `--x-vector-only` was not used.

For the locked 1-sentence English probe it produced valid 24 kHz mono PCM:

- wall time: `85.755 s`
- audio duration: `3.200 s`
- RTF: `26.80`
- peak RSS: `9,457,088 KB` (approximately 9.0 GiB)
- semantic-token EOS: not reached before the generated output ended

This is slower and heavier than the promoted Python/PyTorch path, so the native candidate is rejected for production. It was never wired into the API. The complete-WAV Python worker remains the rollback path. Quantized experiments were not started after the native F32 candidate failed the speed/resource gate.
