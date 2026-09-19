import json
import math
import re
import tempfile
import unittest
import wave
from array import array
from collections import Counter
from dataclasses import replace
from pathlib import Path

from benchmark_corpus import CORPUS, CORPUS_VERSION, EXPECTED_BUCKET_COUNTS
from benchmark_evidence import (
    EVIDENCE_SCOPE_GATE0,
    EVIDENCE_SCOPE_REHEARSAL,
    RESULT_COLUMNS,
    RESULT_SCHEMA_PATH,
    BenchmarkResult,
    cache_evidence_valid,
    cache_request_headers,
    cache_state_from_headers,
    count_words,
    inspect_wav,
    load_results_csv,
    metadata_from_health,
    resolve_evidence_scope,
    render_quality_scaffold,
    render_results_markdown,
    verify_trained_custom_health,
    write_results_csv,
)


def _result(**overrides):
    base = BenchmarkResult(
        timestamp="2026-09-14T06:00:00+00:00",
        run_id="run-1",
        corpus_version=CORPUS_VERSION,
        host="tts-main",
        cloud="azure",
        region="malaysiawest",
        vm_sku="Standard_B4as_v2",
        cpu_model="Example CPU",
        backend="qwen3-tts",
        backend_version="1.0",
        checkpoint_id="checkpoint-a",
        evidence_scope=EVIDENCE_SCOPE_REHEARSAL,
        gate0_qualified=False,
        gate0_attestation="",
        variant_id="gate1-B",
        precision_quantization="float32",
        attention_implementation="sdpa",
        intra_op_threads=3,
        inter_op_threads=1,
        max_new_tokens=2048,
        sampling_profile="production-v1",
        model_load_seconds=12.5,
        idle_rss_mb=3900.0,
        text_id="S01",
        corpus_bucket="one_sentence",
        language="English",
        iteration=1,
        character_count=48,
        word_count=10,
        generation_seconds=2.5,
        request_seconds=2.6,
        audio_duration_seconds=3.0,
        rtf=0.833,
        ttfa_seconds=2.55,
        peak_rss_mb=4100.0,
        swap_mb=0.0,
        cpu_average_pct=84.0,
        cpu_peak_pct=99.0,
        cache_state="miss_reported",
        cache_miss_mode="force-miss",
        evidence_valid=True,
        status="success",
        http_status=200,
        quality_result="pending_review",
        audio_path="audio/run-1-S01-r1.wav",
        evidence_note="",
        error="",
    )
    return replace(base, **overrides)


def _production_health(**overrides):
    health = {
        "status": "ready",
        "model": "/opt/tsuki-qwen3-tts/checkpoints/tsukihime/checkpoint-epoch-0",
        "checkpoint_id": "tsuki-trained-checkpoint-a",
        "checkpoint_id_source": "env",
        "generation_version": "trained-custom-v2",
        "mode": "trained-custom",
        "speaker": "tsukihime",
        "deployment_stage": "production",
        "approval_status": "APPROVED_FOR_PRODUCTION",
        "reference_required": False,
        "runtime_lora_loaded": False,
        "artifact_tree_sha256": "a" * 64,
        "manifest_sha256": "b" * 64,
        "conditioning": {
            "reference_required": False,
            "reference_sha256": None,
            "x_vector_only_mode": None,
        },
    }
    health.update(overrides)
    return health


def _attestation_payload(health):
    conditioning = health["conditioning"]
    return {
        "gate0_status": "passed",
        "model": health["model"],
        "checkpoint_id": health["checkpoint_id"],
        "speaker": health["speaker"],
        "artifact_tree_sha256": health["artifact_tree_sha256"],
        "manifest_sha256": health["manifest_sha256"],
        "approval_status": health["approval_status"],
        "conditioning": {
            "reference_required": False,
            "reference_sha256": None,
            "x_vector_only_mode": None,
        },
        "runtime_lora_loaded": False,
        "generation_version": health["generation_version"],
    }


class BenchmarkCorpusTests(unittest.TestCase):
    def test_corpus_has_locked_bucket_counts_and_unique_ids(self):
        self.assertEqual(len(CORPUS), 55)
        self.assertEqual(Counter(sample.bucket for sample in CORPUS), Counter(EXPECTED_BUCKET_COUNTS))
        self.assertEqual(len({sample.text_id for sample in CORPUS}), len(CORPUS))

    def test_corpus_is_nonempty_and_within_current_worker_text_limit(self):
        for sample in CORPUS:
            with self.subTest(text_id=sample.text_id):
                self.assertTrue(sample.text.strip())
                self.assertIn(sample.language, {"English", "Japanese"})
                self.assertLessEqual(len(sample.text), 280)
                self.assertGreater(count_words(sample.text), 0)

    def test_language_buckets_are_explicit(self):
        for sample in CORPUS:
            if sample.bucket == "japanese":
                self.assertEqual(sample.language, "Japanese")
            if sample.bucket == "mixed_language":
                self.assertIn(sample.language, {"English", "Japanese"})

    def test_gate1_six_sentence_probe_is_present_without_changing_corpus_counts(self):
        sample = next(item for item in CORPUS if item.text_id == "ld-05")
        self.assertEqual(sample.bucket, "long_detail")
        self.assertEqual(len(re.findall(r"[.!?](?:\s|$)", sample.text)), 6)


class BenchmarkEvidenceTests(unittest.TestCase):
    def test_inspect_wav_reports_raw_audio_and_acoustic_metrics(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tone.wav"
            rate = 24000
            samples = array(
                "h",
                [int(9000 * math.sin(2 * math.pi * 220 * index / rate)) for index in range(rate)],
            )
            with wave.open(str(path), "wb") as stream:
                stream.setnchannels(1)
                stream.setsampwidth(2)
                stream.setframerate(rate)
                stream.writeframes(samples.tobytes())

            result = inspect_wav(path, include_acoustic=True)

        self.assertEqual(result["sample_rate"], 24000)
        self.assertEqual(result["channels"], 1)
        self.assertEqual(result["status"], "PASS")
        self.assertGreater(result["duration_sec"], 0.9)
        self.assertGreater(result["periodicity"], 0.8)
        self.assertGreater(result["voiced_frame_ratio"], 0.9)

    def test_required_section_19_columns_are_present(self):
        required = {
            "timestamp",
            "host",
            "cloud",
            "region",
            "vm_sku",
            "cpu_model",
            "backend",
            "backend_version",
            "checkpoint_id",
            "evidence_scope",
            "gate0_qualified",
            "gate0_attestation",
            "precision_quantization",
            "attention_implementation",
            "intra_op_threads",
            "inter_op_threads",
            "text_id",
            "character_count",
            "word_count",
            "generation_seconds",
            "audio_duration_seconds",
            "rtf",
            "ttfa_seconds",
            "peak_rss_mb",
            "swap_mb",
            "cpu_average_pct",
            "cpu_peak_pct",
            "cache_state",
            "status",
            "quality_result",
        }
        self.assertTrue(required.issubset(RESULT_COLUMNS))

    def test_machine_readable_schema_matches_csv_columns(self):
        schema = json.loads(RESULT_SCHEMA_PATH.read_text(encoding="utf-8"))
        self.assertEqual(schema["$schema"], "https://json-schema.org/draft/2020-12/schema")
        self.assertEqual(schema["required"], list(RESULT_COLUMNS))
        self.assertEqual(set(schema["properties"]), set(RESULT_COLUMNS))
        self.assertFalse(schema["additionalProperties"])

    def test_health_metadata_parsing_keeps_runtime_configuration(self):
        health = {
            "engine": "qwen3-tts-12hz",
            "model": "trained-tsuki-v7",
            "checkpoint_id": "trained-tsuki-v7-build-42",
            "runtime": {
                "precision": "float32",
                "attention_implementation": "sdpa",
                "intra_op_threads": 3,
                "inter_op_threads": 1,
            },
        }
        metadata = metadata_from_health(
            health,
            host="main",
            cloud="azure",
            region="malaysiawest",
            vm_sku="B4as_v2",
            cpu_model="AMD EPYC",
            backend_version="build-42",
        )
        self.assertEqual(metadata.backend, "qwen3-tts-12hz")
        self.assertEqual(metadata.backend_version, "build-42")
        self.assertEqual(metadata.checkpoint_id, "trained-tsuki-v7-build-42")
        self.assertEqual(metadata.attention_implementation, "sdpa")
        self.assertEqual(metadata.intra_op_threads, 3)
        self.assertEqual(metadata.inter_op_threads, 1)

    def test_trained_custom_rehearsal_scope_requires_trained_custom_health(self):
        health = _production_health()
        verify_trained_custom_health(health)
        qualified, attestation = resolve_evidence_scope(health, EVIDENCE_SCOPE_REHEARSAL, None)
        self.assertFalse(qualified)
        self.assertEqual(attestation, "")
        health["mode"] = "full-icl"
        with self.assertRaisesRegex(ValueError, "trained-custom health check failed"):
            verify_trained_custom_health(health)

    def test_gate0_scope_accepts_verified_trained_custom_production_identity(self):
        health = _production_health()
        with self.assertRaisesRegex(ValueError, "requires --gate0-attestation"):
            resolve_evidence_scope(health, EVIDENCE_SCOPE_GATE0, None)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "gate0.json"
            path.write_text(json.dumps(_attestation_payload(health)), encoding="utf-8")
            qualified, attestation = resolve_evidence_scope(health, EVIDENCE_SCOPE_GATE0, path)
        self.assertTrue(qualified)
        self.assertRegex(attestation, r"^sha256:[0-9a-f]{64}$")

    def test_gate0_scope_rejects_identity_attestation_mismatches(self):
        health = _production_health()
        cases = (
            ("gate status", {"gate0_status": "blocked"}, "gate0_status"),
            ("model", {"model": "other-model"}, "model must exactly match"),
            ("checkpoint", {"checkpoint_id": "other-checkpoint"}, "checkpoint_id must exactly match"),
            ("speaker", {"speaker": "other-speaker"}, "speaker must exactly match"),
            ("artifact", {"artifact_tree_sha256": "d" * 64}, "artifact_tree_sha256 must exactly match"),
            ("generation", {"generation_version": "other-generation"}, "generation_version must exactly match"),
            ("lora", {"runtime_lora_loaded": True}, "runtime_lora_loaded must be false"),
        )
        for name, changes, expected in cases:
            with self.subTest(case=name), tempfile.TemporaryDirectory() as directory:
                payload = _attestation_payload(health)
                payload.update(changes)
                path = Path(directory) / "gate0.json"
                path.write_text(json.dumps(payload), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, expected):
                    resolve_evidence_scope(health, EVIDENCE_SCOPE_GATE0, path)

    def test_gate0_scope_rejects_bad_conditioning_or_reference_health(self):
        base_health = _production_health()
        cases = (
            (
                "bad health reference",
                _production_health(conditioning={"reference_required": False, "reference_sha256": "not-null", "x_vector_only_mode": None}),
                _attestation_payload(base_health),
                "conditioning.reference_sha256 must be null",
            ),
            (
                "reference health",
                _production_health(conditioning={"reference_required": True, "reference_sha256": None, "x_vector_only_mode": None}),
                _attestation_payload(base_health),
                "conditioning.reference_required must be false",
            ),
            (
                "runtime lora health",
                _production_health(runtime_lora_loaded=True),
                _attestation_payload(base_health),
                "runtime_lora_loaded must be false",
            ),
            (
                "missing generation version",
                _production_health(generation_version=""),
                _attestation_payload(base_health),
                "generation_version must be recorded",
            ),
        )
        for name, health, payload, expected in cases:
            with self.subTest(case=name), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "gate0.json"
                path.write_text(json.dumps(payload), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, expected):
                    resolve_evidence_scope(health, EVIDENCE_SCOPE_GATE0, path)

        health = _production_health()
        for field, value, expected in (
            ("mode", "x-vector-only", "trained-custom"),
            ("x_vector_only_mode", True, "conditioning.x_vector_only_mode must not be enabled"),
        ):
            with self.subTest(attestation_field=field), tempfile.TemporaryDirectory() as directory:
                payload = _attestation_payload(health)
                if field == "mode":
                    payload["conditioning"]["reference_required"] = True
                else:
                    payload["conditioning"][field] = value
                path = Path(directory) / "gate0.json"
                path.write_text(json.dumps(payload), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, expected):
                    resolve_evidence_scope(health, EVIDENCE_SCOPE_GATE0, path)

    def test_force_miss_is_not_claimed_without_response_evidence(self):
        headers = cache_request_headers("force-miss", "run:S01:1")
        self.assertEqual(headers["X-Benchmark-Force-Cache-Miss"], "1")
        state = cache_state_from_headers({}, "force-miss")
        self.assertEqual(state, "unverified_force_miss")
        self.assertFalse(cache_evidence_valid(state, "force-miss"))

    def test_cache_hit_invalidates_inference_evidence(self):
        state = cache_state_from_headers({"X-Cache": "HIT"}, "direct-inference")
        self.assertEqual(state, "hit")
        self.assertFalse(cache_evidence_valid(state, "direct-inference"))
        state = cache_state_from_headers({"CF-Cache-Status": "MISS"}, "force-miss")
        self.assertEqual(state, "miss_reported")
        self.assertTrue(cache_evidence_valid(state, "force-miss"))

    def test_direct_inference_mode_is_an_explicit_operator_declaration(self):
        state = cache_state_from_headers({}, "direct-inference")
        self.assertEqual(state, "miss_declared_direct_inference")
        self.assertTrue(cache_evidence_valid(state, "direct-inference"))

    def test_csv_round_trip_preserves_optional_metrics(self):
        results = [_result(), _result(text_id="JA01", peak_rss_mb=None, cpu_peak_pct=None, http_status=None)]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "benchmark-results.csv"
            write_results_csv(path, results)
            loaded = load_results_csv(path)
        self.assertEqual(loaded, results)

    def test_csv_parser_rejects_incomplete_schema(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "broken.csv"
            path.write_text("timestamp,text_id\nnow,S01\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "missing required columns"):
                load_results_csv(path)

    def test_markdown_aggregates_only_valid_uncached_rows(self):
        markdown = render_results_markdown(
            [
                _result(generation_seconds=2.0),
                _result(text_id="S02", generation_seconds=100.0, evidence_valid=False, cache_state="hit"),
            ]
        )
        self.assertIn("Valid uncached timing rows: **1**", markdown)
        self.assertIn("Gate0-qualified rows: **0**", markdown)
        self.assertIn("REHEARSAL ONLY", markdown)
        self.assertIn("| trained_custom_rehearsal | one_sentence | 1 | 2.000", markdown)
        self.assertIn("| hit | 1 |", markdown)

    def test_quality_scaffold_contains_all_locked_review_dimensions(self):
        markdown = render_quality_scaffold([_result()])
        for label in (
            "Voice identity",
            "Tsuki accent/style",
            "Pronunciation",
            "Prosody",
            "Pitch stability",
            "Rhythm",
            "Noise/artifacts",
            "Sentence endings",
            "Japanese quality",
            "English quality",
        ):
            self.assertIn(label, markdown)
        self.assertIn("audio/run-1-S01-r1.wav", markdown)


if __name__ == "__main__":
    unittest.main()
