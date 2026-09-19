"""Deterministic Gate 1/2 comparison and decision reporting.

This helper consumes the existing benchmark CSV schema.  It deliberately keeps
audio quality outside the timing data: callers must supply an external quality
verdict, and unqualified rehearsal evidence can never produce a production
winner. Qualified evidence must identify the trained Tsukihime custom voice;
historical Base/full-ICL rows are not accepted by this decision helper.
"""

from __future__ import annotations

import argparse
import re
import statistics
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence

from benchmark_evidence import (
    EVIDENCE_SCOPE_GATE0,
    EVIDENCE_SCOPE_REHEARSAL,
    BenchmarkResult,
    load_results_csv,
)


QUALITY_VERDICTS = ("pending", "pass", "reject")
_ZERO_SWAP_EPSILON_MB = 1e-9
_ATTESTATION_DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


@dataclass(frozen=True)
class ProbeSpec:
    label: str
    text_id: str


PROBES = (
    ProbeSpec("1 sentence", "os-01"),
    ProbeSpec("2 sentences", "ts-01"),
    ProbeSpec("3 sentences", "3s-01"),
    ProbeSpec("6 sentences", "ld-05"),
)


@dataclass(frozen=True)
class ProbeMetrics:
    label: str
    text_id: str
    row_count: int
    generation_seconds: float
    ttfa_seconds: float | None
    audio_duration_seconds: float | None
    rtf: float | None


@dataclass(frozen=True)
class ProbeComparison:
    label: str
    text_id: str
    reference: ProbeMetrics
    candidate: ProbeMetrics
    generation_improvement_pct: float


@dataclass(frozen=True)
class DecisionReport:
    reference_variant: str
    candidate_variant: str
    evidence_scope: str
    checkpoint_id: str
    corpus_version: str
    sampling_profile: str
    quality_verdict: str
    probe_comparisons: tuple[ProbeComparison, ...]
    macro_generation_improvement_pct: float | None
    speed_leader: str
    production_selection: str
    validation_errors: tuple[str, ...]
    decision_reasons: tuple[str, ...]

    @property
    def comparison_valid(self) -> bool:
        return not self.validation_errors


def _unique_text(rows: Sequence[BenchmarkResult], field: str) -> str | None:
    values = {str(getattr(row, field)).strip() for row in rows}
    if len(values) != 1:
        return None
    return next(iter(values))


def _mean(values: Iterable[float | None]) -> float | None:
    present = [value for value in values if value is not None]
    return statistics.fmean(present) if present else None


def _dataset_errors(rows: Sequence[BenchmarkResult], role: str) -> list[str]:
    if not rows:
        return [f"{role}: CSV contains no benchmark rows"]

    errors: list[str] = []
    for field, label in (
        ("variant_id", "variant ID"),
        ("checkpoint_id", "checkpoint identity"),
        ("corpus_version", "corpus version"),
        ("sampling_profile", "sampling profile"),
        ("evidence_scope", "evidence scope"),
    ):
        if _unique_text(rows, field) is None:
            errors.append(f"{role}: rows do not share one {label}")

    for index, row in enumerate(rows, start=1):
        if row.status != "success":
            errors.append(f"{role}: row {index} ({row.text_id}) status is {row.status!r}, not success")
        if not row.evidence_valid:
            errors.append(f"{role}: row {index} ({row.text_id}) is not evidence-valid")
        if row.swap_mb is None:
            errors.append(f"{role}: row {index} ({row.text_id}) has no swap measurement")
        elif abs(row.swap_mb) > _ZERO_SWAP_EPSILON_MB:
            errors.append(f"{role}: row {index} ({row.text_id}) used {row.swap_mb:g} MB swap")
        if row.generation_seconds is None or row.generation_seconds <= 0:
            errors.append(f"{role}: row {index} ({row.text_id}) has no positive generation time")

    scope = _unique_text(rows, "evidence_scope")
    if scope == EVIDENCE_SCOPE_GATE0:
        if any(not row.gate0_qualified for row in rows):
            errors.append(f"{role}: Gate0 evidence contains rows not marked gate0_qualified")
        if any(not row.gate0_attestation.strip() for row in rows):
            errors.append(f"{role}: Gate0 evidence contains rows without a Gate0 attestation")
        elif any(_ATTESTATION_DIGEST.fullmatch(row.gate0_attestation.strip()) is None for row in rows):
            errors.append(f"{role}: Gate0 evidence contains an invalid production-identity attestation digest")
        elif _unique_text(rows, "gate0_attestation") is None:
            errors.append(f"{role}: Gate0 evidence rows do not share one production-identity attestation")
    elif scope == EVIDENCE_SCOPE_REHEARSAL:
        if any(row.gate0_qualified for row in rows):
            errors.append(f"{role}: rehearsal evidence contains contradictory gate0_qualified rows")
    elif scope is not None:
        errors.append(f"{role}: unsupported evidence scope {scope!r}")

    return errors


def _probe_metrics(rows: Sequence[BenchmarkResult], spec: ProbeSpec, role: str) -> tuple[ProbeMetrics | None, str | None]:
    selected = [row for row in rows if row.text_id == spec.text_id]
    if not selected:
        return None, f"{role}: missing locked {spec.label} probe {spec.text_id}"
    if any(row.generation_seconds is None or row.generation_seconds <= 0 for row in selected):
        return None, f"{role}: locked probe {spec.text_id} lacks positive generation timing"

    generation = statistics.fmean(float(row.generation_seconds) for row in selected if row.generation_seconds is not None)
    return (
        ProbeMetrics(
            label=spec.label,
            text_id=spec.text_id,
            row_count=len(selected),
            generation_seconds=generation,
            ttfa_seconds=_mean(row.ttfa_seconds for row in selected),
            audio_duration_seconds=_mean(row.audio_duration_seconds for row in selected),
            rtf=_mean(row.rtf for row in selected),
        ),
        None,
    )


def _improvement_pct(reference_seconds: float, candidate_seconds: float) -> float:
    if reference_seconds <= 0:
        raise ValueError("reference generation time must be positive")
    return ((reference_seconds - candidate_seconds) / reference_seconds) * 100.0


def compare_results(
    reference_rows: Sequence[BenchmarkResult],
    candidate_rows: Sequence[BenchmarkResult],
    *,
    quality_verdict: str,
) -> DecisionReport:
    """Compare one reference variant with one candidate variant.

    `quality_verdict` is intentionally external to the CSV.  Per-row
    `quality_result` values are never used to promote a candidate.
    """

    verdict = quality_verdict.strip().lower()
    if verdict not in QUALITY_VERDICTS:
        raise ValueError(f"quality_verdict must be one of: {', '.join(QUALITY_VERDICTS)}")

    validation_errors = _dataset_errors(reference_rows, "reference")
    validation_errors.extend(_dataset_errors(candidate_rows, "candidate"))

    reference_variant = _unique_text(reference_rows, "variant_id") or "(invalid reference)"
    candidate_variant = _unique_text(candidate_rows, "variant_id") or "(invalid candidate)"
    reference_scope = _unique_text(reference_rows, "evidence_scope") or "unknown"
    candidate_scope = _unique_text(candidate_rows, "evidence_scope") or "unknown"
    reference_checkpoint = _unique_text(reference_rows, "checkpoint_id") or "unknown"
    candidate_checkpoint = _unique_text(candidate_rows, "checkpoint_id") or "unknown"
    reference_corpus = _unique_text(reference_rows, "corpus_version") or "unknown"
    candidate_corpus = _unique_text(candidate_rows, "corpus_version") or "unknown"
    reference_sampling = _unique_text(reference_rows, "sampling_profile") or "unknown"
    candidate_sampling = _unique_text(candidate_rows, "sampling_profile") or "unknown"
    reference_attestation = _unique_text(reference_rows, "gate0_attestation") or ""
    candidate_attestation = _unique_text(candidate_rows, "gate0_attestation") or ""

    if reference_scope != candidate_scope:
        validation_errors.append(
            f"comparison: evidence scope mismatch ({reference_scope!r} vs {candidate_scope!r})"
        )
    if reference_checkpoint != candidate_checkpoint:
        validation_errors.append(
            f"comparison: checkpoint identity mismatch ({reference_checkpoint!r} vs {candidate_checkpoint!r})"
        )
    if reference_corpus != candidate_corpus:
        validation_errors.append(
            f"comparison: corpus version mismatch ({reference_corpus!r} vs {candidate_corpus!r})"
        )
    if reference_sampling != candidate_sampling:
        validation_errors.append(
            f"comparison: sampling profile mismatch ({reference_sampling!r} vs {candidate_sampling!r})"
        )
    if reference_scope == EVIDENCE_SCOPE_GATE0 and reference_attestation != candidate_attestation:
        validation_errors.append("comparison: Gate0 production-identity attestation mismatch")

    probe_comparisons: list[ProbeComparison] = []
    for spec in PROBES:
        reference_metrics, reference_error = _probe_metrics(reference_rows, spec, "reference")
        candidate_metrics, candidate_error = _probe_metrics(candidate_rows, spec, "candidate")
        if reference_error:
            validation_errors.append(reference_error)
        if candidate_error:
            validation_errors.append(candidate_error)
        if reference_metrics is None or candidate_metrics is None:
            continue
        probe_comparisons.append(
            ProbeComparison(
                label=spec.label,
                text_id=spec.text_id,
                reference=reference_metrics,
                candidate=candidate_metrics,
                generation_improvement_pct=_improvement_pct(
                    reference_metrics.generation_seconds,
                    candidate_metrics.generation_seconds,
                ),
            )
        )

    macro_improvement: float | None = None
    speed_leader = "undetermined"
    if len(probe_comparisons) == len(PROBES):
        reference_macro = statistics.fmean(item.reference.generation_seconds for item in probe_comparisons)
        candidate_macro = statistics.fmean(item.candidate.generation_seconds for item in probe_comparisons)
        macro_improvement = _improvement_pct(reference_macro, candidate_macro)
        if candidate_macro < reference_macro:
            speed_leader = "candidate"
        elif candidate_macro > reference_macro:
            speed_leader = "reference"
        else:
            speed_leader = "tie"

    decision_reasons: list[str] = []
    production_selection = "none"
    if validation_errors:
        decision_reasons.append("production selection refused because comparison evidence failed validation")
    elif reference_scope == EVIDENCE_SCOPE_REHEARSAL:
        decision_reasons.append(
            "production selection refused: trained_custom_rehearsal evidence is rehearsal-only and cannot establish a production winner"
        )
    elif reference_scope != EVIDENCE_SCOPE_GATE0:
        decision_reasons.append(f"production selection refused for unsupported evidence scope {reference_scope!r}")
    elif verdict == "pending":
        decision_reasons.append("production selection refused until an external voice-quality verdict is provided")
    elif verdict == "reject":
        production_selection = "reference"
        decision_reasons.append("candidate rejected by the external voice-quality gate; reference is retained")
    elif speed_leader == "candidate":
        production_selection = "candidate"
        decision_reasons.append("candidate is faster on the four locked probes and passed the external voice-quality gate")
    elif speed_leader in {"reference", "tie"}:
        production_selection = "reference"
        decision_reasons.append("candidate did not beat the reference on macro generation time; reference is retained")
    else:
        decision_reasons.append("production selection refused because probe timing is incomplete")

    return DecisionReport(
        reference_variant=reference_variant,
        candidate_variant=candidate_variant,
        evidence_scope=reference_scope,
        checkpoint_id=reference_checkpoint,
        corpus_version=reference_corpus,
        sampling_profile=reference_sampling,
        quality_verdict=verdict,
        probe_comparisons=tuple(probe_comparisons),
        macro_generation_improvement_pct=macro_improvement,
        speed_leader=speed_leader,
        production_selection=production_selection,
        validation_errors=tuple(validation_errors),
        decision_reasons=tuple(decision_reasons),
    )


def _fmt(value: float | None, digits: int = 3) -> str:
    return "N/A" if value is None else f"{value:.{digits}f}"


def render_markdown(report: DecisionReport) -> str:
    if report.production_selection == "candidate":
        selection = f"candidate `{report.candidate_variant}`"
    elif report.production_selection == "reference":
        selection = f"reference `{report.reference_variant}` retained"
    else:
        selection = "none"

    lines = [
        "# Gate 1/2 Benchmark Decision Report",
        "",
        f"- Evidence scope: `{report.evidence_scope}`",
        f"- Checkpoint: `{report.checkpoint_id}`",
        f"- Corpus: `{report.corpus_version}`",
        f"- Sampling profile: `{report.sampling_profile}`",
        f"- Reference variant: `{report.reference_variant}`",
        f"- Candidate variant: `{report.candidate_variant}`",
        f"- External voice-quality verdict: **{report.quality_verdict}**",
        f"- Speed leader: **{report.speed_leader}**",
        f"- Production selection: **{selection}**",
        "",
        "The benchmark CSV `quality_result` field is informational only here. Production selection requires the external voice-quality verdict supplied to this decision run.",
        "",
        "## Locked sentence probes",
        "",
        "| Probe | Locked text ID | Ref gen (s) | Candidate gen (s) | Improvement | Ref TTFA (s) | Candidate TTFA (s) | Ref RTF | Candidate RTF |",
        "|---|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for item in report.probe_comparisons:
        lines.append(
            f"| {item.label} | `{item.text_id}` | {_fmt(item.reference.generation_seconds)} | "
            f"{_fmt(item.candidate.generation_seconds)} | {item.generation_improvement_pct:+.2f}% | "
            f"{_fmt(item.reference.ttfa_seconds)} | {_fmt(item.candidate.ttfa_seconds)} | "
            f"{_fmt(item.reference.rtf)} | {_fmt(item.candidate.rtf)} |"
        )
    if not report.probe_comparisons:
        lines.append("| no comparable probes |  |  |  |  |  |  |  |  |")

    lines.extend(
        [
            "",
            f"Macro generation improvement across the four locked probes: **{_fmt(report.macro_generation_improvement_pct, 2)}%**",
            "",
            "## Rejections and decision reasons",
            "",
        ]
    )
    if report.validation_errors:
        for error in report.validation_errors:
            lines.append(f"- REJECT: {error}")
    for reason in report.decision_reasons:
        lines.append(f"- {reason}")
    if not report.validation_errors and not report.decision_reasons:
        lines.append("- None.")
    lines.append("")
    return "\n".join(lines)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Compare deterministic Gate 1/2 benchmark CSV evidence.")
    parser.add_argument("--reference", type=Path, required=True, help="reference benchmark-results.csv")
    parser.add_argument("--candidate", type=Path, required=True, help="candidate benchmark-results.csv")
    parser.add_argument(
        "--quality-verdict",
        choices=QUALITY_VERDICTS,
        required=True,
        help="external A/B voice-quality verdict; CSV quality_result values do not satisfy this gate",
    )
    parser.add_argument("--output", type=Path, default=Path("benchmark-comparison.md"))
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    report = compare_results(
        load_results_csv(args.reference),
        load_results_csv(args.candidate),
        quality_verdict=args.quality_verdict,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(render_markdown(report), encoding="utf-8")
    return 0 if report.comparison_valid else 2


if __name__ == "__main__":
    raise SystemExit(main())
