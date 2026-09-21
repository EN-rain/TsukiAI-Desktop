"""Embed the canonical optional LLM implementation into both standalone runtimes.

Run this after changing optional_text_llm.py. It preserves the Marimo TTS cells
and Colab's existing notebook metadata and pinned TTS identity.
"""

import json
import textwrap
from pathlib import Path


NOTEBOOKS = Path(__file__).resolve().parents[1] / "notebooks"
HELPER = NOTEBOOKS / "optional_text_llm.py"
MARIMO = NOTEBOOKS / "marimo_epoch0_service.py"
COLAB = NOTEBOOKS / "hf_epoch0_colab_service.ipynb"
BEGIN = "# BEGIN OPTIONAL TEXT LLM (synced from optional_text_llm.py)"
END = "# END OPTIONAL TEXT LLM"


def replace_block(source, insert, indent=""):
    start_marker, end_marker = indent + BEGIN, indent + END
    if start_marker in source:
        beginning = source.index(start_marker)
        ending = source.index(end_marker, beginning) + len(end_marker)
        return source[:beginning] + insert + source[ending:]
    raise RuntimeError("Optional LLM insertion marker missing")


def main():
    helper = HELPER.read_text(encoding="utf-8")
    assert "def mount_optional_text_llm(" in helper
    definition = helper[helper.index("def mount_optional_text_llm("):].rstrip()
    marimo = MARIMO.read_text(encoding="utf-8")
    marimo_block = textwrap.indent(
        BEGIN + "\n" + definition + "\n" + END, "        "
    )
    marimo = replace_block(marimo, marimo_block, "        ")
    MARIMO.write_text(marimo, encoding="utf-8")

    notebook = json.loads(COLAB.read_text(encoding="utf-8"))
    setup_cell = notebook["cells"][1]
    setup_code = "".join(setup_cell["source"])
    setup_code = setup_code.split("\n# OPTIONAL_QWEN35_DEPS", 1)[0]
    setup_code += (
            "\n# OPTIONAL_QWEN35_DEPS: only install when enabled before loading TTS.\n"
            "import importlib.metadata as _tsuki_package_metadata\n"
            "import os as _tsuki_os\n"
            "import subprocess as _tsuki_subprocess\n"
            "import sys as _tsuki_sys\n"
            "if _tsuki_os.environ.get('TSUKI_ENABLE_TEXT_LLM', '') == '1':\n"
            "    try:\n"
            "        _tsuki_transformers_version = tuple(int(part) for part in\n"
            "            _tsuki_package_metadata.version('transformers').split('.')[:2])\n"
            "    except (_tsuki_package_metadata.PackageNotFoundError, ValueError):\n"
            "        _tsuki_transformers_version = (0, 0)\n"
            "    try:\n"
            "        _tsuki_package_metadata.version('accelerate')\n"
            "        _tsuki_accelerate_installed = True\n"
            "    except _tsuki_package_metadata.PackageNotFoundError:\n"
            "        _tsuki_accelerate_installed = False\n"
            "    if _tsuki_transformers_version < (5, 2) or not _tsuki_accelerate_installed:\n"
            "        _tsuki_subprocess.run(\n"
            "            [_tsuki_sys.executable, '-m', 'pip', 'install', '--no-cache-dir',\n"
            "             '--upgrade', 'transformers>=5.2.0', 'accelerate'],\n"
            "            check=True, timeout=900,\n"
            "        )\n"
    )
    setup_cell["source"] = setup_code.splitlines(keepends=True)
    introduction = notebook["cells"][0]
    text = "".join(introduction["source"])
    if "TSUKI_LLM_API_KEY" not in text:
        text = text.split("\nOptional text chat:", 1)[0]
        text += (
            "\nOptional text chat: before executing the first cell, set "
            "`TSUKI_ENABLE_TEXT_LLM=1` in the Colab runtime environment. "
            "Set a separate private `TSUKI_LLM_API_KEY` in the runtime "
            "environment and on the VPS; do not paste the key into this "
            "notebook. The text route accepts Bearer or X-Api-Key "
            "authentication. `GET /v1/health` gives "
            "its independent load state; the 9B model is downloaded only "
            "after the first authorized chat request. It needs at least "
            "19 GiB free GPU memory after TTS loads; T4 usually cannot host "
            "both unquantized models. Keep this option off for normal TTS.\n"
        )
        introduction["source"] = text.splitlines(keepends=True)
    app_cell = notebook["cells"][4]
    source = "".join(app_cell["source"])
    source = source.replace(
        "    api_key=API_KEY,\n",
        "    api_key=os.environ.get('TSUKI_LLM_API_KEY', '').strip(),\n",
    )
    if BEGIN not in source:
        anchor = "app = FastAPI(title='Tsuki epoch-0 Base/full-ICL Colab TTS', version=GENERATION_VERSION)\n"
        if source.count(anchor) != 1:
            raise RuntimeError("Colab FastAPI insertion anchor missing or ambiguous")
        source = source.replace(anchor, anchor + "\n" + BEGIN + "\n" + END + "\n"
                                + "mount_optional_text_llm(\n"
                                + "    app, MODEL_LOCK,\n"
                                + "    enabled=os.environ.get('TSUKI_ENABLE_TEXT_LLM', '') == '1',\n"
                                + "    api_key=os.environ.get('TSUKI_LLM_API_KEY', '').strip(),\n"
                                + ")\n", 1)
    source = replace_block(source, BEGIN + "\n" + definition + "\n" + END)
    app_cell["source"] = source.splitlines(keepends=True)
    COLAB.write_text(json.dumps(notebook, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
