"""Build an OpenVoice V2 CPU converter without loading the optional watermark."""

from __future__ import annotations

from typing import Any


def create_cpu_converter(config_path: str) -> Any:
    """Initialize the current official converter base on CPU.

    The current upstream checkout forwards ``enable_watermark`` into its base
    constructor, which rejects that keyword. Constructing the official base
    explicitly preserves its model initialization while keeping watermark
    inference disabled for this private service.
    """

    from openvoice.api import OpenVoiceBaseClass, ToneColorConverter

    converter = ToneColorConverter.__new__(ToneColorConverter)
    OpenVoiceBaseClass.__init__(converter, config_path, device="cpu")
    converter.watermark_model = None
    converter.version = getattr(converter.hps, "_version_", "v1")
    return converter
