"""Deterministically fingerprint a deployed Qwen model artifact directory.

This utility intentionally accepts only a local filesystem path.  Gate 0 uses
it after the deployment's model source has been resolved to the exact local
snapshot/directory, so the resulting digest describes bytes that are actually
present on the host rather than a guessed model label.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ArtifactDigest:
    sha256: str
    file_count: int
    total_bytes: int


def digest_artifact_tree(root: Path) -> ArtifactDigest:
    root = root.expanduser().resolve()
    if not root.is_dir():
        raise ValueError(f"artifact path is not a directory: {root}")

    files = sorted(path for path in root.rglob("*") if path.is_file())
    if not files:
        raise ValueError(f"artifact directory contains no files: {root}")

    manifest = hashlib.sha256()
    total_bytes = 0
    for path in files:
        relative = path.relative_to(root).as_posix().encode("utf-8")
        file_digest = hashlib.sha256()
        size = 0
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                size += len(block)
                file_digest.update(block)

        total_bytes += size
        manifest.update(len(relative).to_bytes(4, "big"))
        manifest.update(relative)
        manifest.update(size.to_bytes(8, "big"))
        manifest.update(file_digest.digest())

    return ArtifactDigest(
        sha256=manifest.hexdigest(),
        file_count=len(files),
        total_bytes=total_bytes,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Fingerprint a local deployed Qwen artifact directory")
    parser.add_argument("artifact_path", type=Path)
    args = parser.parse_args()
    digest = digest_artifact_tree(args.artifact_path)
    print(
        json.dumps(
            {
                "artifact_sha256": digest.sha256,
                "file_count": digest.file_count,
                "total_bytes": digest.total_bytes,
            },
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
