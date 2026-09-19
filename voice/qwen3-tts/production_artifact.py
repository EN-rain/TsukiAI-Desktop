"""Fail-closed verification for a trained Qwen deployment artifact."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from artifact_digest import digest_artifact_tree


_SHA256 = re.compile(r"[0-9a-f]{64}\Z")


@dataclass(frozen=True)
class VerifiedArtifact:
    checkpoint: str
    checkpoint_id: str
    tree_sha256: str
    manifest_sha256: str


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _relative_manifest_path(value: object, checkpoint: str) -> str:
    path = str(value or "").replace("\\", "/").strip("/")
    marker = f"/{checkpoint}/"
    if path.startswith(f"{checkpoint}/"):
        return path[len(checkpoint) + 1 :]
    if marker in f"/{path}":
        return path.split(marker, 1)[1]
    raise ValueError(f"approved manifest file is outside checkpoint {checkpoint!r}: {value!r}")


def _read_manifest(path: Path) -> dict[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read approved artifact manifest {path}: {exc}") from exc
    if not isinstance(payload, dict):
        raise ValueError("approved artifact manifest must be a JSON object")
    return payload


def verify_approved_artifact(
    checkpoint_path: Path,
    manifest_path: Path,
    *,
    stage: str,
    checkpoint_id: str,
) -> VerifiedArtifact:
    """Verify approval, identity, and every byte in a deployed checkpoint tree."""

    checkpoint = checkpoint_path.expanduser().resolve()
    manifest_file = manifest_path.expanduser().resolve()
    if not checkpoint.is_dir():
        raise ValueError(f"approved trained checkpoint is not a directory: {checkpoint}")
    if not manifest_file.is_file():
        raise ValueError(f"approved artifact manifest does not exist: {manifest_file}")
    if stage not in {"staging", "production"}:
        raise ValueError(f"deployment stage must be staging or production, got {stage!r}")

    payload = _read_manifest(manifest_file)
    expected_status = "APPROVED_FOR_PRODUCTION" if stage == "production" else None
    approval_status = str(payload.get("approval_status", "")).strip()
    if expected_status is not None and approval_status != expected_status:
        raise ValueError(
            f"production requires APPROVED_FOR_PRODUCTION, got {approval_status or 'missing'}"
        )
    if stage == "staging" and approval_status not in {
        "APPROVED_FOR_STAGING",
        "APPROVED_FOR_PRODUCTION",
    }:
        raise ValueError(
            "staging requires APPROVED_FOR_STAGING or APPROVED_FOR_PRODUCTION, "
            f"got {approval_status or 'missing'}"
        )
    if stage == "production" and payload.get("production_eligible") is not True:
        raise ValueError("production artifact manifest must set production_eligible=true")

    checkpoint_name = str(payload.get("checkpoint", "")).strip()
    if checkpoint_name != checkpoint.name:
        raise ValueError(
            f"approved manifest checkpoint {checkpoint_name!r} does not match {checkpoint.name!r}"
        )
    manifest_checkpoint_id = str(payload.get("checkpoint_id", "")).strip()
    if not manifest_checkpoint_id or manifest_checkpoint_id != checkpoint_id.strip():
        raise ValueError("approved manifest checkpoint_id does not match the configured checkpoint identity")

    expected_tree = str(payload.get("checkpoint_tree_sha256", "")).strip().lower()
    if not _SHA256.fullmatch(expected_tree):
        raise ValueError("approved manifest checkpoint_tree_sha256 must be a real 64-hex SHA-256")
    actual_tree = digest_artifact_tree(checkpoint).sha256
    if actual_tree != expected_tree:
        raise ValueError(
            "checkpoint tree SHA-256 does not match the approved manifest: "
            f"{actual_tree} != {expected_tree}"
        )

    records = payload.get("files")
    if not isinstance(records, list) or not records:
        raise ValueError("approved manifest must contain a non-empty files list")
    expected_files: dict[str, tuple[int, str]] = {}
    for record in records:
        if not isinstance(record, dict):
            raise ValueError("approved manifest files must be objects")
        relative = _relative_manifest_path(record.get("path"), checkpoint.name)
        size = record.get("size")
        file_hash = str(record.get("sha256", "")).strip().lower()
        if not isinstance(size, int) or size < 0 or not _SHA256.fullmatch(file_hash):
            raise ValueError(f"approved manifest has invalid file record for {relative!r}")
        if relative in expected_files:
            raise ValueError(f"approved manifest contains duplicate file {relative!r}")
        expected_files[relative] = (size, file_hash)

    actual_files = {
        path.relative_to(checkpoint).as_posix(): path
        for path in checkpoint.rglob("*")
        if path.is_file()
    }
    if set(actual_files) != set(expected_files):
        missing = sorted(set(expected_files) - set(actual_files))
        extra = sorted(set(actual_files) - set(expected_files))
        raise ValueError(f"approved manifest file set differs; missing={missing}, extra={extra}")
    for relative, path in sorted(actual_files.items()):
        expected_size, expected_hash = expected_files[relative]
        actual_size = path.stat().st_size
        actual_hash = _file_sha256(path)
        if actual_size != expected_size or actual_hash != expected_hash:
            raise ValueError(f"approved manifest file hash/size mismatch for {relative}")

    return VerifiedArtifact(
        checkpoint=checkpoint.name,
        checkpoint_id=checkpoint_id.strip(),
        tree_sha256=actual_tree,
        manifest_sha256=_file_sha256(manifest_file),
    )
