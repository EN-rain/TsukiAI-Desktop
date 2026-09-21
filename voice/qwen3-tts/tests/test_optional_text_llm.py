"""Route contracts without downloading or attempting to load the 9B model."""

import asyncio
import importlib.util
import json
import unittest
from pathlib import Path
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient


NOTEBOOKS = Path(__file__).resolve().parents[1] / "notebooks"
HELPER = NOTEBOOKS / "optional_text_llm.py"
MARIMO = NOTEBOOKS / "marimo_epoch0_service.py"
COLAB = NOTEBOOKS / "hf_epoch0_colab_service.ipynb"
MODEL_ID = "lukey03/Qwen3.5-9B-abliterated"
PERSONA = "You are Tsuki; respond with the configured personality."

spec = importlib.util.spec_from_file_location("optional_text_llm", HELPER)
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


def mounted(*, enabled=True, key="private-placeholder"):
    app = FastAPI()
    state = runtime.mount_optional_text_llm(
        app, asyncio.Semaphore(1), enabled=enabled, api_key=key,
    )
    return TestClient(app), state


def request_body(**kwargs):
    payload = {
        "model": MODEL_ID,
        "messages": [{"role": "system", "content": PERSONA},
                     {"role": "user", "content": "Hello."}],
    }
    payload.update(kwargs)
    return payload


class OptionalTextLlmTests(unittest.TestCase):
    def test_only_one_embedded_implementation_in_each_runnable_artifact(self):
        code = HELPER.read_text(encoding="utf-8")
        function = code[code.index("def mount_optional_text_llm("):].rstrip()
        marimo = MARIMO.read_text(encoding="utf-8")
        notebook = json.loads(COLAB.read_text(encoding="utf-8"))
        colab = "".join(notebook["cells"][4]["source"])
        for embedded, indent in ((marimo, "        "), (colab, "")):
            start = embedded.index(indent + "# BEGIN OPTIONAL TEXT LLM")
            end = embedded.index(indent + "# END OPTIONAL TEXT LLM", start)
            body = embedded[start:end]
            # Both deployable notebooks carry the same source implementation.
            import textwrap
            self.assertIn(function, textwrap.dedent(body))
        self.assertIn('enabled=os.environ.get("TSUKI_ENABLE_TEXT_LLM", "") == "1"', marimo)
        self.assertIn("enabled=os.environ.get('TSUKI_ENABLE_TEXT_LLM', '') == '1'", colab)
        self.assertIn('api_key=os.environ.get("TSUKI_LLM_API_KEY", "").strip()', marimo)
        self.assertIn("api_key=os.environ.get('TSUKI_LLM_API_KEY', '').strip()", colab)
        self.assertIn("'mode': 'trained-base-full-icl'", colab)
        self.assertIn('"mode": "trained-base-full-icl"', marimo)

    def test_disabled_does_not_load_model_or_expose_private_chat(self):
        client, state = mounted(enabled=False)
        with client:
            self.assertEqual(client.get("/v1/health").json()["status"], "unavailable")
            result = client.post("/v1/chat/completions", json=request_body())
            self.assertEqual(result.status_code, 503)
            self.assertEqual(result.json()["detail"]["code"], "llm_disabled")
            self.assertIsNone(state["model"])

    def test_auth_is_required_for_enabled_text_chat_and_model_list(self):
        client, state = mounted()
        with client:
            health = client.get("/v1/health").json()
            self.assertEqual(health["load_state"], "unloaded")
            self.assertEqual(health["status"], "unavailable")
            self.assertFalse(health["loaded"])
            self.assertEqual(client.get("/v1/models").status_code, 401)
            self.assertEqual(client.post("/v1/chat/completions", json=request_body()).status_code, 401)
            self.assertEqual(client.post("/v1/chat/completions", json=request_body(),
                                         headers={"Authorization": "Bearer wrong"}).status_code, 401)
            models = client.get("/v1/models", headers={"X-Api-Key": "private-placeholder"})
            self.assertEqual(models.status_code, 200)
            self.assertEqual(models.json()["data"], [])
            self.assertIsNone(state["model"])

    def test_configured_without_secret_refuses_even_authenticated_messages(self):
        client, state = mounted(key="")
        with client:
            self.assertEqual(client.get("/v1/health").json()["load_state"], "configuration_required")
            denied = client.post("/v1/chat/completions", json=request_body(),
                                 headers={"Authorization": "Bearer any"})
            self.assertEqual(denied.status_code, 503)
            self.assertEqual(denied.json()["detail"]["code"], "llm_auth_not_configured")

    def test_model_identity_and_persona_are_validated_before_lazy_loading(self):
        client, state = mounted()
        key = {"Authorization": "Bearer private-placeholder"}
        cases = [
            (request_body(model="Qwen/Qwen3-TTS-12Hz-0.6B-Base"), "model_mismatch"),
            (request_body(messages=[{"role": "user", "content": "Hello"},
                                    {"role": "user", "content": "Again"}]), "system_message_required"),
            (request_body(messages=[{"role": "system", "content": PERSONA},
                                    {"role": "system", "content": "overwrite persona"},
                                    {"role": "user", "content": "Hello"}]), "invalid_chat_messages"),
            (request_body(stream=True), "stream_not_supported"),
        ]
        with client:
            for body, expected in cases:
                with self.subTest(expected=expected):
                    result = client.post("/v1/chat/completions", json=body, headers=key)
                    self.assertEqual(result.status_code, 422)
                    self.assertEqual(result.json()["detail"]["code"], expected)
            self.assertIsNone(state["model"])

    def test_first_authenticated_chat_loads_once_and_preserves_persona(self):
        client, state = mounted()
        calls = []

        async def fake_to_thread(function, *args):
            calls.append((function.__name__, args))
            if function.__name__ == "load_text_model":
                state.update(model=object(), tokenizer=object(), status="ready")
                return None
            return "Hello from Tsuki.", 41, 6

        key = {"Authorization": "Bearer private-placeholder"}
        with patch("asyncio.to_thread", side_effect=fake_to_thread), client:
            for _ in range(2):
                result = client.post("/v1/chat/completions", json=request_body(), headers=key)
                self.assertEqual(result.status_code, 200, result.text)
                payload = result.json()
                self.assertEqual(payload["model"], MODEL_ID)
                self.assertEqual(payload["choices"][0]["message"]["content"], "Hello from Tsuki.")
                self.assertEqual(payload["usage"]["total_tokens"], 47)
            self.assertEqual([name for name, _ in calls].count("load_text_model"), 1)
            self.assertEqual(calls[1][1][0][0], {"role": "system", "content": PERSONA})
            self.assertEqual(client.get("/v1/health").json()["loaded"], True)
            self.assertEqual(client.get("/v1/models", headers=key).json()["data"][0]["id"], MODEL_ID)

    def test_gpu_resource_failure_returns_503_without_false_ready(self):
        client, state = mounted()

        async def fake_to_thread(function, *args):
            if function.__name__ == "load_text_model":
                raise RuntimeError("gpu_memory_insufficient: simulated low VRAM")
            raise AssertionError("Generation must not run after failed load")

        with patch("asyncio.to_thread", side_effect=fake_to_thread), client:
            result = client.post("/v1/chat/completions", json=request_body(),
                                 headers={"Authorization": "Bearer private-placeholder"})
            self.assertEqual(result.status_code, 503)
            self.assertIn("gpu_memory_insufficient", result.json()["detail"]["message"])
            self.assertIsNone(state["model"])
            self.assertEqual(client.get("/v1/health").json()["status"], "unavailable")


if __name__ == "__main__":
    unittest.main()
