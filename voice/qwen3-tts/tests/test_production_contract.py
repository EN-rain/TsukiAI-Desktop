import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from artifact_digest import digest_artifact_tree
from deployment_identity import DeploymentIdentity
from production_artifact import verify_approved_artifact
from runtime_config import (
    BASE_MODEL_NAME,
    resolve_approval_status,
    resolve_deployment_stage,
    resolve_mode,
    resolve_model_name,
)


class ProductionRuntimeContractTests(unittest.TestCase):
    def test_production_defaults_to_trained_custom(self):
        self.assertEqual(resolve_mode({}), "trained-custom")
        self.assertEqual(resolve_deployment_stage({}), "production")

    def test_voice_clone_mode_is_rejected_by_the_production_config(self):
        with self.assertRaisesRegex(ValueError, "trained-custom only"):
            resolve_mode({"QWEN_TTS_MODE": "full-icl"})

    def test_model_must_be_an_explicit_non_base_checkpoint(self):
        with self.assertRaisesRegex(ValueError, "explicitly point to an approved trained checkpoint"):
            resolve_model_name({})
        with self.assertRaisesRegex(ValueError, "official Qwen Base"):
            resolve_model_name({"QWEN_TTS_MODEL": BASE_MODEL_NAME})

    def test_approval_status_is_required_and_stage_has_a_safe_default(self):
        with self.assertRaisesRegex(ValueError, "QWEN_TTS_APPROVAL_STATUS"):
            resolve_approval_status({})
        self.assertEqual(
            resolve_approval_status({"QWEN_TTS_APPROVAL_STATUS": "APPROVED_FOR_STAGING"}),
            "APPROVED_FOR_STAGING",
        )

    def test_deployment_identity_requires_an_explicit_checkpoint_for_runtime(self):
        with self.assertRaisesRegex(ValueError, "QWEN_TTS_CHECKPOINT_ID"):
            DeploymentIdentity.from_env("/models/tsuki", {}, require_explicit=True)
        identity = DeploymentIdentity.from_env(
            "/models/tsuki",
            {
                "QWEN_TTS_CHECKPOINT_ID": "tsuki-final-1",
                "QWEN_TTS_GENERATION_VERSION": "trained-custom-v2",
            },
            require_explicit=True,
        )
        self.assertEqual(identity.generation_version, "trained-custom-v2")


class ApprovedArtifactTests(unittest.TestCase):
    def _manifest(self, root: Path, *, status="APPROVED_FOR_PRODUCTION", eligible=True):
        files = []
        for path in sorted(root.rglob("*")):
            if path.is_file():
                files.append(
                    {
                        "path": f"checkpoint-epoch-0/{path.relative_to(root).as_posix()}",
                        "size": path.stat().st_size,
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    }
                )
        return {
            "approval_status": status,
            "production_eligible": eligible,
            "checkpoint": "checkpoint-epoch-0",
            "checkpoint_id": "tsuki-final-1",
            "checkpoint_tree_sha256": digest_artifact_tree(root).sha256,
            "files": files,
        }

    def test_approved_manifest_matches_the_exact_checkpoint_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "checkpoint-epoch-0"
            root.mkdir()
            (root / "config.json").write_text('{"tts_model_type":"custom_voice"}', encoding="utf-8")
            (root / "model.safetensors").write_bytes(b"weights")
            manifest_path = Path(directory) / "manifest.json"
            manifest_path.write_text(json.dumps(self._manifest(root)), encoding="utf-8")

            verified = verify_approved_artifact(
                root,
                manifest_path,
                stage="production",
                checkpoint_id="tsuki-final-1",
            )

            self.assertEqual(verified.checkpoint, "checkpoint-epoch-0")
            self.assertEqual(verified.tree_sha256, digest_artifact_tree(root).sha256)

    def test_staging_approval_cannot_start_the_production_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "checkpoint-epoch-0"
            root.mkdir()
            (root / "config.json").write_text("{}", encoding="utf-8")
            manifest_path = Path(directory) / "manifest.json"
            manifest_path.write_text(
                json.dumps(self._manifest(root, status="APPROVED_FOR_STAGING", eligible=False)),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "APPROVED_FOR_PRODUCTION"):
                verify_approved_artifact(
                    root,
                    manifest_path,
                    stage="production",
                    checkpoint_id="tsuki-final-1",
                )

    def test_modified_checkpoint_bytes_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "checkpoint-epoch-0"
            root.mkdir()
            (root / "config.json").write_text("{}", encoding="utf-8")
            manifest_path = Path(directory) / "manifest.json"
            manifest_path.write_text(json.dumps(self._manifest(root)), encoding="utf-8")
            (root / "config.json").write_text('{"changed":true}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "tree SHA-256"):
                verify_approved_artifact(
                    root,
                    manifest_path,
                    stage="production",
                    checkpoint_id="tsuki-final-1",
                )


if __name__ == "__main__":
    unittest.main()
