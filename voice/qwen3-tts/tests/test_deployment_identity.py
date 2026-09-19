import unittest

from deployment_identity import DEFAULT_GENERATION_VERSION, DeploymentIdentity


class DeploymentIdentityTests(unittest.TestCase):
    def test_explicit_checkpoint_and_generation_define_cache_namespace(self):
        identity = DeploymentIdentity.from_env(
            "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
            {
                "QWEN_TTS_CHECKPOINT_ID": "tsuki-trained-sha256:abc123",
                "QWEN_TTS_GENERATION_VERSION": "trained-custom-v2",
            },
        )
        same = DeploymentIdentity.from_env(
            "ignored/model/path",
            {
                "QWEN_TTS_CHECKPOINT_ID": "tsuki-trained-sha256:abc123",
                "QWEN_TTS_GENERATION_VERSION": "trained-custom-v2",
            },
        )

        self.assertEqual(identity.checkpoint_id_source, "env")
        self.assertEqual(identity.checkpoint_id, "tsuki-trained-sha256:abc123")
        self.assertEqual(identity.generation_version, "trained-custom-v2")
        self.assertEqual(identity.audio_cache_namespace, same.audio_cache_namespace)

    def test_checkpoint_or_generation_change_rotates_cache_namespace(self):
        base = DeploymentIdentity.from_env(
            "model",
            {
                "QWEN_TTS_CHECKPOINT_ID": "checkpoint-a",
                "QWEN_TTS_GENERATION_VERSION": "trained-custom-v1",
            },
        )
        checkpoint_changed = DeploymentIdentity.from_env(
            "model",
            {
                "QWEN_TTS_CHECKPOINT_ID": "checkpoint-b",
                "QWEN_TTS_GENERATION_VERSION": "trained-custom-v1",
            },
        )
        generation_changed = DeploymentIdentity.from_env(
            "model",
            {
                "QWEN_TTS_CHECKPOINT_ID": "checkpoint-a",
                "QWEN_TTS_GENERATION_VERSION": "trained-custom-v2",
            },
        )

        self.assertNotEqual(base.audio_cache_namespace, checkpoint_changed.audio_cache_namespace)
        self.assertNotEqual(base.audio_cache_namespace, generation_changed.audio_cache_namespace)

    def test_missing_checkpoint_id_is_rejected_for_runtime_identity(self):
        with self.assertRaisesRegex(ValueError, "explicitly recorded"):
            DeploymentIdentity.from_env(
                "checkpoint-epoch-1",
                {},
                require_explicit=True,
            )

    def test_missing_checkpoint_id_fallback_remains_a_non_runtime_utility(self):
        identity = DeploymentIdentity.from_env(" checkpoint-epoch-1 ", {})

        self.assertEqual(identity.checkpoint_id, "checkpoint-epoch-1")
        self.assertEqual(identity.checkpoint_id_source, "model-name-fallback")
        self.assertEqual(identity.generation_version, DEFAULT_GENERATION_VERSION)

    def test_empty_model_name_is_rejected(self):
        with self.assertRaises(ValueError):
            DeploymentIdentity.from_env("   ", {})


if __name__ == "__main__":
    unittest.main()
