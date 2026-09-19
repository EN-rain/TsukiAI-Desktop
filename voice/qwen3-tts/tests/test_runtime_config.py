import json
import unittest
from pathlib import Path

from benchmark_variants import verify_health_runtime
from runtime_config import (
    BASE_MODEL_NAME,
    RuntimeTuning,
    resolve_device,
    resolve_approval_status,
    resolve_approved_manifest,
    resolve_deployment_stage,
    resolve_mode,
    resolve_model_name,
    resolve_speaker,
)


class RuntimeTuningTests(unittest.TestCase):
    def test_default_mode_is_trained_custom_but_model_must_be_explicit(self):
        self.assertEqual(resolve_mode({}), "trained-custom")
        with self.assertRaisesRegex(ValueError, "approved trained checkpoint"):
            resolve_model_name({})

    def test_custom_model_is_allowed_without_an_experimental_opt_in(self):
        self.assertEqual(
            resolve_model_name({"QWEN_TTS_MODEL": "checkpoint-epoch-1"}),
            "checkpoint-epoch-1",
        )

    def test_full_icl_mode_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "trained-custom only"):
            resolve_mode({"QWEN_TTS_MODE": "full-icl"})

    def test_trained_custom_mode_requires_a_non_base_checkpoint(self):
        environment = {
            "QWEN_TTS_MODE": "trained-custom",
            "QWEN_TTS_MODEL": "/content/tsukiai_tsukihime_qwen06b_inference/model/"
            "tsukiai_tsukihime_qwen06b/checkpoints/checkpoint-epoch-1",
        }
        self.assertEqual(resolve_mode(environment), "trained-custom")
        self.assertEqual(resolve_model_name(environment), environment["QWEN_TTS_MODEL"])

        with self.assertRaisesRegex(ValueError, "trained checkpoint"):
            resolve_model_name({"QWEN_TTS_MODE": "trained-custom"})

    def test_trained_custom_mode_cannot_use_the_base_model(self):
        with self.assertRaisesRegex(ValueError, "official Qwen Base model"):
            resolve_model_name(
                {
                    "QWEN_TTS_MODE": "trained-custom",
                    "QWEN_TTS_MODEL": BASE_MODEL_NAME,
                }
            )

    def test_trained_custom_speaker_is_explicit_and_non_empty(self):
        self.assertEqual(resolve_speaker({"QWEN_TTS_SPEAKER": "tsukihime"}), "tsukihime")
        with self.assertRaisesRegex(ValueError, "QWEN_TTS_SPEAKER"):
            resolve_speaker({"QWEN_TTS_SPEAKER": ""})
        with self.assertRaisesRegex(ValueError, "tsukihime"):
            resolve_speaker({"QWEN_TTS_SPEAKER": "other"})

    def test_stage_and_approval_are_explicit(self):
        self.assertEqual(resolve_deployment_stage({}), "production")
        self.assertEqual(resolve_deployment_stage({"QWEN_TTS_DEPLOYMENT_STAGE": "stage"}), "staging")
        with self.assertRaisesRegex(ValueError, "QWEN_TTS_APPROVAL_STATUS"):
            resolve_approval_status({})
        self.assertEqual(
            resolve_approval_status({"QWEN_TTS_APPROVAL_STATUS": "approved_for_staging"}),
            "APPROVED_FOR_STAGING",
        )
        with self.assertRaisesRegex(ValueError, "QWEN_TTS_APPROVED_MANIFEST"):
            resolve_approved_manifest({})

    def test_cuda_alias_is_normalized_and_invalid_devices_are_rejected(self):
        self.assertEqual(resolve_device({"QWEN_TTS_DEVICE": "cuda"}), "cuda:0")
        with self.assertRaisesRegex(ValueError, "QWEN_TTS_DEVICE"):
            resolve_device({"QWEN_TTS_DEVICE": "metal"})

    def test_trained_runtime_defaults_are_bounded(self):
        tuning = RuntimeTuning.from_env({})
        self.assertEqual(tuning.attention_implementation, "eager")
        self.assertEqual(tuning.intra_op_threads, 4)
        self.assertEqual(tuning.inter_op_threads, 4)
        self.assertEqual(tuning.max_new_tokens, 2048)

    def test_legacy_thread_setting_remains_compatible(self):
        tuning = RuntimeTuning.from_env({"QWEN_TTS_THREADS": "3"})
        self.assertEqual(tuning.intra_op_threads, 3)
        self.assertEqual(tuning.inter_op_threads, 3)

    def test_gate1_variants_resolve_to_declared_runtime(self):
        variants_path = Path(__file__).resolve().parents[1] / "benchmark_variants.json"
        payload = json.loads(variants_path.read_text(encoding="utf-8"))
        gate1 = [item for item in payload["variants"] if item["gate"] == 1]
        self.assertEqual([item["id"] for item in gate1], ["gate1-A", "gate1-B", "gate1-C", "gate1-D", "gate1-E"])
        self.assertEqual(
            [
                (
                    item["id"],
                    item["expected_runtime"]["precision"],
                    item["expected_runtime"]["attention_implementation"],
                    item["expected_runtime"]["intra_op_threads"],
                    item["expected_runtime"]["inter_op_threads"],
                    item["expected_runtime"]["max_new_tokens"],
                    item["expected_runtime"]["sampling_profile"],
                )
                for item in gate1
            ],
            [
                ("gate1-A", "float32", "eager", 4, 4, 2048, "trained-custom-v2"),
                ("gate1-B", "float32", "sdpa", 4, 1, 2048, "trained-custom-v2"),
                ("gate1-C", "float32", "sdpa", 3, 1, 2048, "trained-custom-v2"),
                ("gate1-D", "float32", "sdpa", 2, 1, 2048, "trained-custom-v2"),
                ("gate1-E", "float32", "eager", 4, 1, 2048, "trained-custom-v2"),
            ],
        )
        for item in gate1:
            with self.subTest(variant=item["id"]):
                self.assertEqual(
                    set(item["env"]),
                    {
                        "QWEN_TTS_ATTN_IMPLEMENTATION",
                        "QWEN_TTS_INTRA_OP_THREADS",
                        "QWEN_TTS_INTER_OP_THREADS",
                        "QWEN_TTS_MAX_NEW_TOKENS",
                    },
                )
                tuning = RuntimeTuning.from_env(item["env"])
                expected = item["expected_runtime"]
                self.assertEqual(tuning.attention_implementation, expected["attention_implementation"])
                self.assertEqual(tuning.intra_op_threads, expected["intra_op_threads"])
                self.assertEqual(tuning.inter_op_threads, expected["inter_op_threads"])
                self.assertEqual(tuning.max_new_tokens, expected["max_new_tokens"])

    def test_gate2_token_matrix_is_exact_and_sampling_is_not_a_runtime_knob(self):
        variants_path = Path(__file__).resolve().parents[1] / "benchmark_variants.json"
        payload = json.loads(variants_path.read_text(encoding="utf-8"))
        gate2 = [item for item in payload["variants"] if item["gate"] == 2]
        self.assertEqual(
            [item["expected_runtime"]["max_new_tokens"] for item in gate2],
            [2048, 1536, 1024, 768, 512],
        )
        for item in gate2:
            self.assertEqual(set(item["env"]), {"QWEN_TTS_MAX_NEW_TOKENS"})
            self.assertEqual(item["expected_runtime"]["sampling_profile"], "trained-custom-v2")

    def test_variant_label_is_rejected_when_health_does_not_match(self):
        health = {
            "runtime": {
                "precision": "float32",
                "attention_implementation": "eager",
                "intra_op_threads": 4,
                "inter_op_threads": 4,
                "max_new_tokens": 2048,
                "sampling_profile": "trained-custom-v2",
            }
        }
        verify_health_runtime("gate1-A", health)
        health["runtime"]["inter_op_threads"] = 1
        with self.assertRaisesRegex(ValueError, "does not match /health"):
            verify_health_runtime("gate1-A", health)

    def test_invalid_attention_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "QWEN_TTS_ATTN_IMPLEMENTATION"):
            RuntimeTuning.from_env({"QWEN_TTS_ATTN_IMPLEMENTATION": "flash_attention_2"})


if __name__ == "__main__":
    unittest.main()
