import ast
import copy
import io
import re
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import Mock, patch
from types import SimpleNamespace


SERVICE = (
    Path(r"D:\New folder (2)\TsukiAI 1.0")
    / "voice"
    / "qwen3-tts"
    / "notebooks"
    / "marimo_epoch0_service.py"
)


class MarimoEpoch0ServiceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = SERVICE.read_text(encoding="utf-8") if SERVICE.is_file() else ""
        cls.tree = ast.parse(cls.source, filename=str(SERVICE))

    def test_service_file_exists_and_parses(self):
        self.assertTrue(SERVICE.is_file(), f"missing Marimo service: {SERVICE}")
        ast.parse(self.source, filename=str(SERVICE))

    def test_service_contains_pinned_full_icl_model_contract(self):
        self.assertIn("tsukihime-exe/tsuki-qwen3-tts-models", self.source)
        self.assertIn("044462bd5ca834ba7ac44ff044d125decda6e863", self.source)
        self.assertIn("epoch-0", self.source)
        self.assertIn("create_voice_clone_prompt", self.source)
        self.assertIn("generate_voice_clone", self.source)
        self.assertIn("torch.cuda.is_available", self.source)
        self.assertIn("x_vector_only_mode=False", self.source)

    def test_model_generation_budget_has_tail_headroom(self):
        self.assertIn('MAX_NEW_TOKENS = 1024', self.source)
        self.assertIn('max(256, max(len(text) for text in texts) * 5)', self.source)
        self.assertNotIn('max(128, len(text) * 3)', self.source)

    def test_measured_metrics_and_sdpa_fallback_have_no_fake_samples(self):
        self.assertIn('attention_backend = "sdpa"', self.source)
        self.assertIn('attention_backend = "eager"', self.source)
        self.assertIn("attn_implementation=attention_backend", self.source)
        self.assertIn('@local_app.get("/metrics")', self.source)
        self.assertIn('"successes": 0', self.source)
        self.assertIn('"avg_generation_ms":', self.source)
        self.assertIn('"avg_queue_ms":', self.source)
        self.assertIn('"real_time_factor":', self.source)
        self.assertIn("audio_seconds = samples.size / int(sample_rate)", self.source)
        self.assertIn('"phrase_audio_cache": "not implemented"', self.source)
        self.assertIn('"full_icl_reference_prompt_precomputed": True', self.source)
        self.assertNotIn('max(0, len(audio) - 44)', self.source)

    def test_gpu_batch_invokes_model_once_and_reuses_the_full_icl_prompt(self):
        from contextlib import nullcontext
        import numpy as np
        import soundfile as sf
        import time

        generator = next(
            node for node in ast.walk(self.tree)
            if isinstance(node, ast.FunctionDef) and node.name == "generate_audio_many"
        )
        prompt = object()
        model = Mock()
        model.generate_voice_clone.return_value = (
            [np.full(2400, 0.2, dtype=np.float32),
             np.full(4800, 0.25, dtype=np.float32),
             np.full(7200, 0.3, dtype=np.float32)], 24000
        )
        scope = {
            "time": time, "io": io, "np": np, "sf": sf,
            "torch": SimpleNamespace(inference_mode=nullcontext),
            "model": model, "voice_clone_prompt": prompt,
            "MAX_NEW_TOKENS": 1024,
        }
        exec(compile(ast.fix_missing_locations(ast.Module(
            body=[generator], type_ignores=[])), str(SERVICE), "exec"), scope)
        outputs, rate, elapsed = scope["generate_audio_many"](
            ["First.", "Second.", "Third."],
            ["English", "English", "English"],
        )
        self.assertEqual(model.generate_voice_clone.call_count, 1)
        kwargs = model.generate_voice_clone.call_args.kwargs
        self.assertEqual(kwargs["text"], ["First.", "Second.", "Third."])
        self.assertEqual(kwargs["language"], ["English", "English", "English"])
        self.assertIs(kwargs["voice_clone_prompt"], prompt)
        self.assertEqual(rate, 24000)
        self.assertEqual([round(duration, 2) for _, duration in outputs],
                         [0.10, 0.20, 0.30])
        self.assertTrue(all(audio.startswith(b"RIFF") for audio, _ in outputs))
        self.assertGreater(elapsed, 0)
        model.generate_voice_clone.return_value = ([np.ones(2400)], 24000)
        with self.assertRaisesRegex(RuntimeError, "incomplete TTS batch"):
            scope["generate_audio_many"](["One.", "Two."],
                                         ["English", "English"])

    def test_molab_voices_are_bounded_at_three_requests(self):
        self.assertIn("inference_slots = asyncio.Semaphore(1)", self.source)
        self.assertIn("inference_slots = asyncio.Semaphore(1)", self.source)
        self.assertIn("async with inference_slots:", self.source)
        self.assertIn('"voice_concurrency_limit": 1', self.source)
        self.assertIn('"gpu_batch_size_limit": 3', self.source)
        self.assertIn('gpu_batch_endpoint": "/tts-batch"', self.source)
        self.assertIn('text=texts if len(texts) > 1 else texts[0]', self.source)
        self.assertIn('language=languages if len(texts) > 1 else languages[0]', self.source)
        self.assertIn('"gpu_batch_calls": data["gpu_batch_calls"]', self.source)
        self.assertIn('@local_app.post("/tts-batch")', self.source)
        self.assertNotIn("model_lock = asyncio.Lock()", self.source)
        self.assertNotIn("DISCORD_STARTUP_TEXT", self.source)

    def test_service_contains_local_api_and_tunnel_paths(self):
        for expected in ("FastAPI", "/health", "/tts", "uvicorn", "cloudflared",
                         "127.0.0.1:8100"):
            self.assertIn(expected, self.source)

    def test_custom_cloudflare_domain_without_molab_keys(self):
        self.assertIn('PUBLIC_HOSTNAME = "molab-tts.tsukihime.dpdns.org"', self.source)
        self.assertIn('TUNNEL_ID = "ce98db7a-1f14-4e56-a016-e5bd2f5676ea"', self.source)
        self.assertIn('TUNNEL_CREDENTIALS_FILE', self.source)
        self.assertIn('CLOUDFLARE_TUNNEL_CREDENTIALS_JSON = r\'\'\'PASTE_PRIVATE_CLOUDFLARE_TUNNEL_JSON_HERE\'\'\'', self.source)
        self.assertIn('json.loads(inline_credentials)', self.source)
        self.assertIn('os.chmod(TUNNEL_CREDENTIALS_FILE, 0o600)', self.source)
        self.assertNotIn('TTS_API_KEY', self.source)
        self.assertNotIn('CLOUDFLARE_TUNNEL_TOKEN', self.source)
        # Molab TTS stays keyless; the separate optional LLM route requires
        # authentication before it may use a public tunnel.
        self.assertIn('TSUKI_LLM_API_KEY', self.source)
        self.assertIn('X-Api-Key', self.source)
        self.assertNotIn("mo.ui", self.source)
        self.assertNotIn("start_service_button", self.source)
        self.assertNotIn("refresh_status_button", self.source)
        self.assertNotIn("tts_key_input", self.source)
        self.assertNotIn("cloudflare_token_input", self.source)
        self.assertNotIn("/content/", self.source)
        self.assertIsNone(re.search(r"(?m)^\s+response\s*=", self.source))
        self.assertIn("health_response =", self.source)

    def _setup_cell(self):
        cell = next(
            node for node in self.tree.body
            if isinstance(node, ast.FunctionDef)
            and any(
                isinstance(child, ast.FunctionDef)
                and child.name == "inspect_prerequisites"
                for child in node.body
            )
        )
        cell = copy.deepcopy(cell)
        cell.decorator_list = []
        scope = {}
        exec(
            compile(
                ast.fix_missing_locations(ast.Module(body=[cell], type_ignores=[])),
                str(SERVICE),
                "exec",
            ),
            scope,
        )
        return scope["_"]

    def test_prerequisites_install_automatically_without_button(self):
        setup = self._setup_cell()
        with (
            patch("importlib.util.find_spec", return_value=object()),
            patch("shutil.which", return_value="/usr/bin/sox"),
            patch("subprocess.run") as install,
            redirect_stdout(io.StringIO()) as output,
        ):
            (state,) = setup("test-revision")
            self.assertTrue(state["ready"])
            self.assertIn("[setup] READY", output.getvalue())
            install.assert_not_called()

        installed = False
        calls = []

        def find_spec(module):
            return None if module == "qwen_tts" and not installed else object()

        def install_missing(command, **kwargs):
            nonlocal installed
            installed = True
            calls.append(command)

        with (
            patch("importlib.util.find_spec", side_effect=find_spec),
            patch("shutil.which", return_value="/usr/bin/sox"),
            patch("subprocess.run", side_effect=install_missing),
            redirect_stdout(io.StringIO()) as output,
        ):
            (state,) = setup("test-revision")
            self.assertTrue(state["ready"])
            self.assertEqual(len(calls), 1)
            self.assertIn("--no-cache-dir", calls[0])
            self.assertEqual(calls[0][-1], "qwen-tts")
            self.assertNotIn("torch", calls[0])
            self.assertIn("Missing Python packages: qwen-tts", output.getvalue())

    def test_installation_failure_blocks_start(self):
        setup = self._setup_cell()
        with (
            patch(
                "importlib.util.find_spec",
                side_effect=lambda name: None if name == "qwen_tts" else object(),
            ),
            patch("shutil.which", return_value="/usr/bin/sox"),
            patch("subprocess.run", side_effect=OSError("simulated pip failure")),
            redirect_stdout(io.StringIO()) as output,
        ):
            (state,) = setup("test-revision")
            self.assertFalse(state["ready"])
            self.assertIn("simulated pip failure", output.getvalue())

    def test_opted_in_llm_installs_only_its_dependencies_before_tts(self):
        setup = self._setup_cell()
        calls = []
        with (
            patch.dict("os.environ", {"TSUKI_ENABLE_TEXT_LLM": "1"}),
            patch("importlib.util.find_spec", return_value=object()),
            patch("importlib.metadata.version", side_effect=lambda package:
                  "4.57.6" if package == "transformers" else "1.1.0"),
            patch("shutil.which", return_value="/usr/bin/sox"),
            patch("subprocess.run", side_effect=lambda command, **kwargs:
                  calls.append(command)),
            redirect_stdout(io.StringIO()),
        ):
            (state,) = setup("test-revision")
        self.assertTrue(state["ready"])
        self.assertEqual(len(calls), 1)
        self.assertIn("transformers>=5.2.0", calls[0])
        self.assertIn("accelerate", calls[0])
        self.assertNotIn("torch", calls[0])

    def _autostart_tail(self):
        cell = next(
            node for node in self.tree.body
            if isinstance(node, ast.FunctionDef)
            and any(
                isinstance(child, ast.FunctionDef)
                and child.name == "launch_epoch0_service"
                for child in node.body
            )
        )
        tail_start = next(
            i for i, node in enumerate(cell.body)
            if isinstance(node, ast.If)
            and isinstance(node.test, ast.UnaryOp)
            and isinstance(node.test.op, ast.Not)
            and isinstance(node.test.operand, ast.Subscript)
        )
        function = ast.FunctionDef(
            name="simulate_auto_start",
            args=ast.arguments(
                posonlyargs=[],
                args=[
                    ast.arg(arg=name)
                    for name in (
                        "runtime_state", "launch_epoch0_service", "mo",
                    )
                ],
                vararg=None, kwonlyargs=[], kw_defaults=[], kwarg=None, defaults=[],
            ),
            body=copy.deepcopy(cell.body[tail_start:]),
            decorator_list=[],
        )
        scope = {}
        exec(
            compile(
                ast.fix_missing_locations(ast.Module(body=[function], type_ignores=[])),
                str(SERVICE),
                "exec",
            ),
            scope,
        )
        return scope["simulate_auto_start"]

    def test_start_automatically_prints_progress_without_exposing_keys(self):
        auto_start = self._autostart_tail()
        def fake_launch(progress):
            progress("downloading_model", "Simulated checkpoint download")
            return {
                "device_name": "mock CUDA",
                "local_health": 200,
                "local_url": "http://127.0.0.1:8100",
                "public_health": False,
            }
        launch = Mock(side_effect=fake_launch)
        appended = []
        mo = SimpleNamespace(
            output=SimpleNamespace(append=appended.append),
            md=lambda value: value,
        )
        with redirect_stdout(io.StringIO()) as output:
            (state,) = auto_start(
                {"ready": True}, launch, mo,
            )
        self.assertEqual(state["local_health"], 200)
        launch.assert_called_once()
        self.assertEqual(len(launch.call_args.args), 1)
        self.assertIn("[startup] downloading_model", output.getvalue())
        self.assertIn("[startup] READY", output.getvalue())
        self.assertNotIn("REPLACE_WITH_YOUR_TTS_API_KEY", output.getvalue())
        self.assertTrue(any("downloading_model" in entry for entry in appended))
        self.assertTrue(any("READY" in entry for entry in appended))

    def test_vps_owns_discord_configuration(self):
        self.assertNotIn("DISCORD_BOT_TOKEN", self.source)
        self.assertNotIn("DISCORD_TEXT_CHANNEL_ID", self.source)
        self.assertNotIn("DISCORD_STARTUP_TEXT", self.source)
        self.assertNotIn("announce_discord_ready", self.source)
        self.assertIn("VPS bridge owns readiness announcements", self.source)

    def test_keyless_manual_start_does_launch(self):
        auto_start = self._autostart_tail()
        launch = Mock(return_value={
            "device_name": "mock CUDA", "local_health": 200,
            "local_url": "http://127.0.0.1:8100", "public_health": False,
        })
        with redirect_stdout(io.StringIO()):
            (state,) = auto_start(
                {"ready": True}, launch,
                SimpleNamespace(output=SimpleNamespace(append=lambda value: None),
                                md=lambda value: value),
            )
        self.assertEqual(state["local_health"], 200)
        self.assertEqual(len(launch.call_args.args), 1)


if __name__ == "__main__":
    unittest.main()
