# Azure Main host baseline

> **HISTORICAL / SUPERSEDED:** The recorded Base/full-ICL host identity is a
> rollback/provenance baseline only. It is not the trained-custom production
> contract.

This file records host-level evidence that can be collected without changing
the Qwen worker. It is supplementary to the per-request benchmark CSV.

## Host identity

Observed on 2026-09-14:

```text
cloud: Azure
region: Malaysia West
VM: b4as-test
SKU: Standard_B4as_v2
vCPU: 4
RAM class: 16 GiB
CPU observed in guest: AMD EPYC 7763
```

The VM has one Linux OS disk and no attached data disks. The current Azure
subscription inventory did not expose a separate storage account or snapshot
containing a Qwen voice artifact.

## B-series CPU-credit evidence

Azure Monitor exposes both `CPU Credits Remaining` and `CPU Credits Consumed`
for this VM. During the two-hour audit window ending around 2026-09-14 07:05Z,
`CPU Credits Remaining` stayed essentially at the VM's observed ceiling of
about **2304 credits**. The machine was mostly idle during the tail of that
window.

The preceding 24-hour `Percentage CPU` history also contains sustained periods
around **95% CPU**, showing that the host has executed CPU-heavy workloads while
still retaining a full credit balance later in the audit.

### Interpretation

The currently recorded 39.9-122.8 second Base/full-ICL synthesis baseline must
not be explained away as proven B-series credit exhaustion. The audit found no
current credit starvation. Raw Qwen CPU inference remains the primary
performance suspect until a controlled benchmark demonstrates otherwise.

This does **not** make B-series equivalent to a non-burstable VM. Gate 7 still
needs the same winning runtime tested under sustained load on B4as_v2, a
non-burstable Azure 4-vCPU/16-GiB candidate, and the selected AWS candidate.
Capture CPU-credit metrics alongside every sustained B-series run.

## Network/control-plane limitation during this audit

The VM's Qwen service is intentionally bound to `127.0.0.1:8100`. Direct SSH to
the VM public IP timed out from the orchestration host, and the configured
Cloudflare public route was not healthy enough to use as a benchmark path during
this audit. Therefore no new Gate 1 synthesis timing is claimed here.

Run Gate 1 from inside the VM (or another authorized private management path)
so network/cache effects do not contaminate inference timing. Use
`GATE1_BASE_FULL_ICL_REHEARSAL.md` for the reversible worker procedure and the
benchmark runner for per-request CPU/RSS/swap evidence.
