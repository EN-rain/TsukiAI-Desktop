import unittest
from dataclasses import replace

from benchmark_decision import PROBES, compare_results, render_markdown
from benchmark_evidence import EVIDENCE_SCOPE_GATE0, EVIDENCE_SCOPE_REHEARSAL, BenchmarkResult


def _base_row(**overrides):
    row = BenchmarkResult(
        timestamp="2026-09-14T07:00:00+00:00",
        run_id="run-reference",
        corpus_version="v1-sha256-test",
        host="tts-main",
        cloud="azure",
        region="malaysiawest",
        vm_sku="Standard_B4as_v2",
        cpu_model="AMD EPYC",
        backend="qwen3-tts-12hz-0.6b",
        backend_version="1.0",
        checkpoint_id="Qwen/Qwen3-TTS-12Hz-0.6B-Base",
        evidence_scope=EVIDENCE_SCOPE_GATE0,
        gate0_qualified=True,
        gate0_attestation="sha256:" + "a" * 64,
        variant_id="gate1-A",
        precision_quantization="float32",
        attention_implementation="eager",
        intra_op_threads=4,
        inter_op_threads=4,
        max_new_tokens=2048,
        sampling_profile="production-v1",
        model_load_seconds=12.0,
        idle_rss_mb=3900.0,
        text_id="os-01",
        corpus_bucket="one_sentence",
        language="English",
        iteration=1,
        character_count=48,
        word_count=10,
        generation_seconds=10.0,
        request_seconds=10.1,
        audio_duration_seconds=2.0,
        rtf=5.0,
        ttfa_seconds=10.05,
        peak_rss_mb=4100.0,
        swap_mb=0.0,
        cpu_average_pct=85.0,
        cpu_peak_pct=99.0,
        cache_state="miss_declared_direct_inference",
        cache_miss_mode="direct-inference",
        evidence_valid=True,
        status="success",
        http_status=200,
        quality_result="pass",
        audio_path="",
        evidence_note="",
        error="",
    )
    return replace(row, **overrides)


def _variant_rows(
    variant_id,
    times,
    *,
    scope=EVIDENCE_SCOPE_GATE0,
    gate0_qualified=True,
    checkpoint_id="Qwen/Qwen3-TTS-12Hz-0.6B-Base",
    corpus_version="v1-sha256-test",
    sampling_profile="production-v1",
):
    rows = []
    buckets = {
        "os-01": "one_sentence",
        "ts-01": "two_sentence",
        "3s-01": "three_sentence",
        "ld-05": "long_detail",
    }
    for probe, generation in zip(PROBES, times, strict=True):
        rows.append(
            _base_row(
                run_id=f"run-{variant_id}",
                variant_id=variant_id,
                evidence_scope=scope,
                gate0_qualified=gate0_qualified,
                gate0_attestation=("sha256:" + "a" * 64) if gate0_qualified else "",
                checkpoint_id=checkpoint_id,
                corpus_version=corpus_version,
                sampling_profile=sampling_profile,
                text_id=probe.text_id,
                corpus_bucket=buckets[probe.text_id],
                generation_seconds=generation,
                request_seconds=generation + 0.1,
                ttfa_seconds=generation + 0.05,
                audio_duration_seconds=max(1.0, generation / 5.0),
                rtf=5.0,
            )
        )
    return rows


class BenchmarkDecisionTests(unittest.TestCase):
    def test_rehearsal_can_show_speed_but_never_selects_production_winner(self):
        reference = _variant_rows(
            "gate1-A",
            [10.0, 20.0, 30.0, 60.0],
            scope=EVIDENCE_SCOPE_REHEARSAL,
            gate0_qualified=False,
            checkpoint_id="Qwen/Qwen3-TTS-12Hz-0.6B-Base",
        )
        candidate = _variant_rows(
            "gate1-B",
            [8.0, 16.0, 24.0, 48.0],
            scope=EVIDENCE_SCOPE_REHEARSAL,
            gate0_qualified=False,
            checkpoint_id="Qwen/Qwen3-TTS-12Hz-0.6B-Base",
        )
        report = compare_results(reference, candidate, quality_verdict="pass")
        self.assertTrue(report.comparison_valid)
        self.assertEqual(report.speed_leader, "candidate")
        self.assertEqual(report.production_selection, "none")
        self.assertIn("rehearsal-only", " ".join(report.decision_reasons))

    def test_gate0_candidate_can_win_only_with_external_quality_pass(self):
        reference = _variant_rows("gate1-A", [10.0, 20.0, 30.0, 60.0])
        candidate = _variant_rows("gate1-B", [8.0, 16.0, 24.0, 48.0])

        pending = compare_results(reference, candidate, quality_verdict="pending")
        self.assertEqual(pending.production_selection, "none")
        self.assertIn("external voice-quality verdict", " ".join(pending.decision_reasons))

        passed = compare_results(reference, candidate, quality_verdict="pass")
        self.assertEqual(passed.production_selection, "candidate")
        self.assertAlmostEqual(passed.macro_generation_improvement_pct, 20.0)

    def test_csv_quality_result_never_substitutes_for_external_gate(self):
        reference = [replace(row, quality_result="pass") for row in _variant_rows("gate1-A", [10, 20, 30, 60])]
        candidate = [replace(row, quality_result="pass") for row in _variant_rows("gate1-B", [8, 16, 24, 48])]
        report = compare_results(reference, candidate, quality_verdict="pending")
        self.assertEqual(report.production_selection, "none")

    def test_same_checkpoint_corpus_and_sampling_are_required(self):
        reference = _variant_rows("gate1-A", [10, 20, 30, 60])
        cases = (
            (_variant_rows("gate1-B", [8, 16, 24, 48], checkpoint_id="other-build"), "checkpoint identity mismatch"),
            (_variant_rows("gate1-B", [8, 16, 24, 48], corpus_version="other-corpus"), "corpus version mismatch"),
            (_variant_rows("gate1-B", [8, 16, 24, 48], sampling_profile="other-sampling"), "sampling profile mismatch"),
        )
        for candidate, expected in cases:
            with self.subTest(expected=expected):
                report = compare_results(reference, candidate, quality_verdict="pass")
                self.assertFalse(report.comparison_valid)
                self.assertEqual(report.production_selection, "none")
                self.assertIn(expected, " ".join(report.validation_errors))

    def test_gate0_scope_requires_qualified_rows_and_matching_checkpoint(self):
        reference = _variant_rows("gate1-A", [10, 20, 30, 60])
        candidate = _variant_rows("gate1-B", [8, 16, 24, 48])
        candidate[2] = replace(candidate[2], gate0_qualified=False)
        report = compare_results(reference, candidate, quality_verdict="pass")
        self.assertFalse(report.comparison_valid)
        self.assertIn("not marked gate0_qualified", " ".join(report.validation_errors))

    def test_gate0_scope_requires_same_production_identity_attestation(self):
        reference = _variant_rows("gate1-A", [10, 20, 30, 60])
        candidate = _variant_rows("gate1-B", [8, 16, 24, 48])
        candidate = [replace(row, gate0_attestation="sha256:" + "b" * 64) for row in candidate]
        report = compare_results(reference, candidate, quality_verdict="pass")
        self.assertFalse(report.comparison_valid)
        self.assertEqual(report.production_selection, "none")
        self.assertIn("production-identity attestation mismatch", " ".join(report.validation_errors))

    def test_gate0_scope_rejects_invalid_attestation_digest_in_csv(self):
        reference = _variant_rows("gate1-A", [10, 20, 30, 60])
        candidate = _variant_rows("gate1-B", [8, 16, 24, 48])
        candidate[0] = replace(candidate[0], gate0_attestation="passed")
        report = compare_results(reference, candidate, quality_verdict="pass")
        self.assertFalse(report.comparison_valid)
        self.assertIn("invalid production-identity attestation digest", " ".join(report.validation_errors))

    def test_zero_swap_success_and_evidence_valid_are_hard_requirements(self):
        reference = _variant_rows("gate1-A", [10, 20, 30, 60])
        mutations = (
            ("swap", {"swap_mb": 1.0}, "used 1 MB swap"),
            ("missing-swap", {"swap_mb": None}, "has no swap measurement"),
            ("failure", {"status": "failure"}, "not success"),
            ("invalid", {"evidence_valid": False}, "not evidence-valid"),
        )
        for name, changes, expected in mutations:
            with self.subTest(case=name):
                candidate = _variant_rows("gate1-B", [8, 16, 24, 48])
                candidate[0] = replace(candidate[0], **changes)
                report = compare_results(reference, candidate, quality_verdict="pass")
                self.assertFalse(report.comparison_valid)
                self.assertIn(expected, " ".join(report.validation_errors))

    def test_locked_probe_metrics_and_improvements_are_deterministic(self):
        reference = _variant_rows("gate1-A", [10.0, 20.0, 30.0, 60.0])
        candidate = _variant_rows("gate1-B", [9.0, 15.0, 24.0, 42.0])
        report = compare_results(reference, candidate, quality_verdict="pass")
        self.assertEqual([item.text_id for item in report.probe_comparisons], ["os-01", "ts-01", "3s-01", "ld-05"])
        self.assertAlmostEqual(report.probe_comparisons[0].generation_improvement_pct, 10.0)
        self.assertAlmostEqual(report.probe_comparisons[1].generation_improvement_pct, 25.0)
        self.assertAlmostEqual(report.probe_comparisons[2].generation_improvement_pct, 20.0)
        self.assertAlmostEqual(report.probe_comparisons[3].generation_improvement_pct, 30.0)

    def test_missing_locked_probe_rejects_selection_and_report_explains_why(self):
        reference = _variant_rows("gate1-A", [10, 20, 30, 60])
        candidate = _variant_rows("gate1-B", [8, 16, 24, 48])[:-1]
        report = compare_results(reference, candidate, quality_verdict="pass")
        markdown = render_markdown(report)
        self.assertFalse(report.comparison_valid)
        self.assertIn("missing locked 6 sentences probe ld-05", markdown)
        self.assertIn("Production selection: **none**", markdown)
        self.assertIn("external voice-quality verdict", markdown)
        self.assertIn("REJECT:", markdown)

    def test_external_quality_reject_retains_reference(self):
        reference = _variant_rows("gate1-A", [10, 20, 30, 60])
        candidate = _variant_rows("gate1-B", [8, 16, 24, 48])
        report = compare_results(reference, candidate, quality_verdict="reject")
        self.assertTrue(report.comparison_valid)
        self.assertEqual(report.production_selection, "reference")
        self.assertIn("quality gate", " ".join(report.decision_reasons))


if __name__ == "__main__":
    unittest.main()
