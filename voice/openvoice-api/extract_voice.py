"""One-time OpenVoice V2 target speaker-embedding extraction."""

from __future__ import annotations

import argparse
import os
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--model-dir", type=Path, default=Path(os.getenv("OPENVOICE_MODEL_DIR", "/opt/openvoice/models/checkpoints_v2")))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--processed-dir",
        type=Path,
        default=Path(os.getenv("OPENVOICE_PROCESSED_DIR", "/tmp/openvoice-processed")),
        help="temporary directory used by the official VAD-based extractor",
    )
    parser.add_argument("--device", default=os.getenv("OPENVOICE_DEVICE", "cpu"))
    args = parser.parse_args()

    if args.device.lower() != "cpu":
        raise SystemExit("This deployment only permits --device cpu")
    if not args.reference.is_file():
        raise SystemExit(f"reference not found: {args.reference}")

    import torch
    from converter_factory import create_cpu_converter
    from openvoice import se_extractor

    torch.set_num_threads(int(os.getenv("OPENVOICE_TORCH_THREADS", "4")))
    converter_config = args.model_dir / "converter" / "config.json"
    converter_checkpoint = args.model_dir / "converter" / "checkpoint.pth"
    if not converter_config.is_file() or not converter_checkpoint.is_file():
        raise SystemExit("OpenVoice V2 converter artifacts are incomplete")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.processed_dir.mkdir(parents=True, exist_ok=True)
    converter = create_cpu_converter(str(converter_config))
    converter.load_ckpt(str(converter_checkpoint))

    # This is the official OpenVoice extraction path. It applies the same
    # VAD-aware reference conditioning used by the upstream V2 demo instead
    # of embedding the raw file as one unfiltered segment.
    target_se, _ = se_extractor.get_se(
        str(args.reference),
        converter,
        target_dir=str(args.processed_dir),
        vad=True,
    )
    torch.save(target_se, args.output)
    if not args.output.is_file():
        raise SystemExit(f"embedding was not written: {args.output}")
    print(f"wrote {args.output} ({args.output.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
