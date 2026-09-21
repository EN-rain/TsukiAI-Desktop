import importlib.util
import hashlib
import http.client
import io
import json
import unittest
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest import mock
import urllib.error
import urllib.request
from pathlib import Path

spec = importlib.util.spec_from_file_location("tsuki_controls", Path(__file__).with_name("server.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MemoryHTTPConnection:
    """One HTTP request/response pair using the real handler without TCP."""

    def __init__(self, request: bytes):
        self.incoming = io.BytesIO(request)
        self.outgoing = io.BytesIO()

    def makefile(self, mode: str, buffering: int = -1):
        if mode != "rb":
            raise AssertionError("Unexpected handler socket mode")
        return self.incoming

    def sendall(self, body: bytes):
        self.outgoing.write(body)


class ControlsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Never test against or change the real VPS controls authentication.
        cls.auth_dir = TemporaryDirectory()
        cls.addClassCleanup(cls.auth_dir.cleanup)
        cls.auth_patch = mock.patch.object(
            module, "AUTH_FILE", Path(cls.auth_dir.name) / ".controls-auth.json")
        cls.auth_patch.start()
        cls.addClassCleanup(cls.auth_patch.stop)
        cls.trusted_patch = mock.patch.object(
            module, "TRUSTED_DEVICES_FILE", Path(cls.auth_dir.name) / ".controls-trusted-devices.json")
        cls.trusted_patch.start()
        cls.addClassCleanup(cls.trusted_patch.stop)
        module.PASSWORD = "a-local-test-password"
        cls.local_server = SimpleNamespace(
            server_name="localhost", server_port=0)

    def setUp(self):
        with module.LOCK:
            module.ATTEMPTS.clear()
        with module.DISCORD_CACHE_LOCK:
            module.GUILD_CACHE["checked_at"] = 0.0
            module.GUILD_CACHE["items"] = []
            module.CHANNEL_CACHE.clear()

    def wire(self, path, data=None, cookie=None, csrf=None, origin=None):
        # The production BaseHTTPRequestHandler parses the complete HTTP/1.1
        # request, headers and JSON. No Windows loopback listener/socket is
        # needed for mocked controls API tests, so a host firewall cannot
        # abort unrelated authentication assertions with WinError 10053.
        headers = {"Host": "controls.tsukihime.dpdns.org"}
        body = b""
        if data is not None:
            body = json.dumps(data).encode()
            headers.update({"Content-Type": "application/json",
                            "Origin": origin or "https://controls.tsukihime.dpdns.org",
                            "Content-Length": str(len(body))})
        if cookie:
            headers["Cookie"] = cookie
        if csrf:
            headers["X-CSRF-Token"] = csrf
        request = (("\r\n".join(
            [("POST" if data is not None else "GET") + " " + path +
             " HTTP/1.1", *(key + ": " + value for key, value in headers.items()),
             "", ""])).encode() + body)
        connection = MemoryHTTPConnection(request)
        module.Handler(connection, ("127.0.0.1", 0), self.local_server)
        head, separator, content = connection.outgoing.getvalue().partition(
            b"\r\n\r\n")
        self.assertTrue(separator, "Handler did not terminate HTTP headers")
        status_line, _, header_lines = head.partition(b"\r\n")
        self.assertTrue(status_line.startswith(b"HTTP/"),
                        "Handler did not produce an HTTP status line")
        status = int(status_line.split(b" ", 2)[1])
        parsed_headers = http.client.parse_headers(
            io.BytesIO(header_lines + b"\r\n\r\n"))
        length = int(parsed_headers["Content-Length"])
        self.assertEqual(len(content), length, "Handler sent a partial body")
        return status, parsed_headers, content

    def call(self, path, data=None, cookie=None, csrf=None, origin=None,
             all_cookies=False):
        status, headers, content = self.wire(
            path, data, cookie, csrf, origin)
        cookies = (headers.get_all("Set-Cookie") if all_cookies
                   else headers.get("Set-Cookie"))
        return status, json.loads(content), cookies

    def test_http_fixture_uses_real_handler_and_parses_auth_headers(self):
        self.assertEqual(self.call("/api/action", {"action": "restart_api"})[0], 401)
        _, _, cookie = self.call(
            "/api/login", {"password": "a-local-test-password"})
        session = cookie.split(";", 1)[0]
        self.assertEqual(self.call("/api/action", {
            "action": "restart_api"}, cookie=session)[0], 403)
        status, headers, body = self.wire("/api/session", cookie=session)
        self.assertEqual(status, 200)
        self.assertIn("Content-Length", headers)
        self.assertEqual(json.loads(body), {"authenticated": True})

    def test_login_required_and_bad_origin_rejected(self):
        self.assertEqual(self.call("/api/status")[0], 401)
        self.assertEqual(self.call("/api/action", {"action": "stop_bridge"})[0], 401)
        self.assertEqual(self.call("/api/login", {"password": "a-local-test-password"},
                                   origin="https://evil.invalid")[0], 403)

    def test_password_and_session_cookie(self):
        self.assertEqual(self.call("/api/login", {"password": "wrong"})[0], 401)
        status, answer, cookie = self.call("/api/login", {"password": "a-local-test-password"})
        self.assertEqual(status, 200)
        self.assertTrue(answer["ok"])
        self.assertIn("Secure", cookie)
        self.assertIn("HttpOnly", cookie)
        self.assertIn("SameSite=Strict", cookie)
        self.assertIn("tsuki_controls=", cookie)

    def test_trusted_browser_survives_service_restart_and_logout_revokes_it(self):
        status, result, cookies = self.call(
            "/api/login", {"password": "a-local-test-password", "remember": True},
            all_cookies=True)
        self.assertEqual(status, 200)
        self.assertTrue(result["ok"])
        self.assertEqual(len(cookies), 2)
        self.assertTrue(all("HttpOnly" in value and "Secure" in value
                            and "SameSite=Strict" in value for value in cookies))
        cookie_header = "; ".join(value.split(";", 1)[0] for value in cookies)
        trusted = next(value.split(";", 1)[0].split("=", 1)[1]
                       for value in cookies if value.startswith("tsuki_trusted="))
        record = module.TRUSTED_DEVICES_FILE.read_text()
        self.assertNotIn(trusted, record)
        self.assertIn(module._trusted_digest(trusted), record)
        self.assertEqual(self.call("/api/session", cookie=cookie_header)[1]["authenticated"], True)

        with module.LOCK:
            module.SESSIONS.clear()
        self.assertEqual(self.call("/api/session", cookie=cookie_header)[1]["authenticated"], True)
        csrf = hashlib.sha256(("tsuki-controls-csrf-v1:" + trusted).encode()).hexdigest()
        status, _, cleared = self.call("/api/logout", {}, cookie=cookie_header,
                                       csrf=csrf, all_cookies=True)
        self.assertEqual(status, 200)
        self.assertEqual(len(cleared), 2)
        self.assertTrue(all("Max-Age=0" in value for value in cleared))
        self.assertEqual(self.call("/api/session", cookie=cookie_header)[1]["authenticated"], False)

    def test_unremembered_login_has_no_persistent_token(self):
        status, _, cookies = self.call(
            "/api/login", {"password": "a-local-test-password", "remember": False},
            all_cookies=True)
        self.assertEqual(status, 200)
        self.assertEqual(len(cookies), 1)
        self.assertTrue(cookies[0].startswith("tsuki_controls="))
        self.assertEqual(self.call("/api/login", {"password": "a-local-test-password",
                                                   "remember": "yes"})[0], 400)

    def test_invalid_action_and_csrf_do_not_execute(self):
        _, _, cookie = self.call("/api/login", {"password": "a-local-test-password"})
        cookie = cookie.split(";", 1)[0]
        self.assertEqual(self.call("/api/action", {"action": "restart_api"}, cookie=cookie)[0], 403)
        self.assertEqual(self.call("/api/action", {"action": "rm -rf /"}, cookie=cookie,
                                   csrf=module.SESSIONS[cookie.split('=', 1)[1]][1])[0], 400)

    def test_bot_settings_validate_exact_allowlist(self):
        good = module.checked_bot_config({
            "voice_channel_id": "1307370944255229956",
            "text_reply_mode": "mention",
            "text_voice_mode": "keywords",
            "text_channel_id": "",
            "tts_provider": "colab",
        })
        self.assertEqual(good["VOICE_CHANNEL_ID"], "1307370944255229956")
        self.assertEqual(good["TSUKI_TTS_PROVIDER"], "colab")
        self.assertEqual(good["TEXT_CHANNEL_ID"], "")
        keywords = module.checked_bot_config({"text_voice_keywords": "Voice, SAY IT, voice"})
        self.assertEqual(keywords["TEXT_VOICE_KEYWORDS"], "voice,say it")
        for value in (
            {"voice_channel_id": "invalid"},
            {"voice_channel_id": "123; rm -rf /"},
            {"text_voice_mode": "something-else"},
            {"text_voice_mode": "all"},
            {"text_voice_keywords": ""},
            {"text_voice_keywords": "hi,\nBAD"},
            {"text_voice_keywords": "long" * 30},
            {"tts_provider": "fallback"},
            {"llm_provider": "fallback"},
            {"llm_provider": "molab"},
            {"llm_provider": "off"},
            {"llm_provider": "molab\nTSUKI_API_KEY=stolen"},
            {"DISCORD_TOKEN": "arbitrary"},
        ):
            with self.assertRaises(ValueError):
                module.checked_bot_config(value)

    def test_engagement_settings_validate_and_proactive_needs_model(self):
        good = module.checked_bot_config({
            "voice_engagement_mode": "context_aware",
            "chat_engagement_mode": "wake_only",
            "engagement_aliases": "Tsuki, Tsukihime, tsuki",
            "engagement_followup_ms": "30000",
        })
        self.assertEqual(good["VOICE_ENGAGEMENT_MODE"], "context_aware")
        self.assertEqual(good["CHAT_ENGAGEMENT_MODE"], "wake_only")
        self.assertEqual(good["ENGAGEMENT_ALIASES"], "tsuki,tsukihime")
        self.assertEqual(good["ENGAGEMENT_FOLLOWUP_MS"], "30000")
        for values in (
            {"voice_engagement_mode": "all"},
            {"chat_engagement_mode": "unknown"},
            {"engagement_followup_ms": "30001"},
            {"engagement_followup_ms": "-1"},
            {"engagement_followup_ms": "5;shutdown"},
            {"engagement_aliases": "T"},
            {"engagement_aliases": "Tsuki\\nBAD"},
        ):
            with self.assertRaises(ValueError):
                module.checked_bot_config(values)
        with mock.patch.object(module, "_env_value", return_value=""):
            with self.assertRaises(ValueError):
                module.checked_bot_config({"voice_engagement_mode": "proactive"})
        with mock.patch.object(module, "_env_value", return_value="model-id"):
            result = module.checked_bot_config({"voice_engagement_mode": "proactive"})
            self.assertEqual(result["VOICE_ENGAGEMENT_MODE"], "proactive")

    def test_optional_text_channel_validates_and_saves_without_text_channel(self):
        guild = "1307370943416500314"
        voice = "1307370944255229956"
        text = "1545789527556104393"
        original = (f"GUILD_ID={guild}\nVOICE_CHANNEL_ID={voice}\n"
                    f"TEXT_CHANNEL_ID={text}\n").encode()
        available = {"guild_id": guild,
                     "voice": [{"id": voice, "name": "General"}],
                     "text": [{"id": text, "name": "tsuki-ai"}]}
        with mock.patch.object(module, "discord_server_channels",
                               return_value=available) as discover:
            module.verify_bot_guild_config({"TEXT_CHANNEL_ID": ""}, original)
            discover.assert_called_once_with(guild)
            with self.assertRaisesRegex(ValueError, "not available"):
                module.verify_bot_guild_config(
                    {"TEXT_CHANNEL_ID": "11111111111111111"}, original)
            with self.assertRaisesRegex(ValueError, "voice channel"):
                module.verify_bot_guild_config(
                    {"VOICE_CHANNEL_ID": ""}, original)

        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_bytes(
                original + b"TEXT_VOICE_MODE=keywords\n"
                + b"TEXT_VOICE_KEYWORDS=voice,speak\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "discord_server_channels", return_value=available), mock.patch.object(
                    module, "compose",
                    return_value=SimpleNamespace(returncode=0)) as compose:
                self.assertEqual(module.update_bot_config({"text_channel_id": ""}),
                                 ("TEXT_CHANNEL_ID",))
                self.assertEqual(module.read_bot_config()["text_channel_id"], "")
                self.assertEqual(compose.call_count, 1)

    def test_legacy_every_reply_mode_is_migrated_to_keywords(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_text("TEXT_VOICE_MODE=all\n"
                                          "TEXT_VOICE_KEYWORDS=voice,speak\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose",
                    return_value=SimpleNamespace(returncode=0)) as compose:
                self.assertEqual(module.read_bot_config()["text_voice_mode"], "keywords")
                self.assertEqual(module.update_bot_config({"text_voice_mode": "keywords"}),
                                 ("TEXT_VOICE_MODE",))
                self.assertIn("TEXT_VOICE_MODE=keywords", (bridge / ".env").read_text())
                compose.assert_called_once()

    def test_missing_voice_settings_default_to_keywords_with_default_triggers(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_text(
                "DISCORD_TOKEN=preserve-this\nTEXT_REPLY_MODE=mention\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose",
                    return_value=SimpleNamespace(returncode=0)) as compose:
                bot = module.read_bot_config()
                self.assertEqual(bot["text_voice_mode"], "keywords")
                self.assertEqual(bot["text_voice_keywords"],
                                 module.DEFAULT_TEXT_VOICE_KEYWORDS)
                self.assertEqual(module.update_bot_config({
                    "text_reply_mode": "any",
                }), ("TEXT_REPLY_MODE",))
                self.assertEqual(compose.call_count, 1)

    def test_keyword_mode_requires_configured_triggers(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_text("TEXT_VOICE_MODE=all\nTEXT_VOICE_KEYWORDS=\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose") as compose:
                with self.assertRaisesRegex(ValueError, "Enter trigger words"):
                    module.update_bot_config({"text_voice_mode": "keywords"})
                compose.assert_not_called()

    def test_keyword_mode_persists_with_triggers_and_restarts_bridge(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_text(
                "TEXT_VOICE_MODE=all\nTEXT_REPLY_MODE=mention\n"
                "DISCORD_TOKEN=preserve-this\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose",
                    return_value=SimpleNamespace(returncode=0)) as compose:
                updated = module.update_bot_config({
                    "text_reply_mode": "any",
                    "text_voice_mode": "keywords",
                    "text_voice_keywords": "VOICE, speak, say it",
                })
                saved = module.read_bot_config()
            self.assertEqual(saved["text_reply_mode"], "any")
            self.assertEqual(saved["text_voice_mode"], "keywords")
            self.assertEqual(saved["text_voice_keywords"], "voice,speak,say it")
            self.assertEqual(updated, (
                "TEXT_REPLY_MODE", "TEXT_VOICE_MODE", "TEXT_VOICE_KEYWORDS"))
            self.assertEqual(compose.call_count, 1)
            self.assertIn("DISCORD_TOKEN=preserve-this", (bridge / ".env").read_text())

    def test_compose_uses_private_config_outside_protected_home(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module.subprocess, "run",
                    return_value=SimpleNamespace(returncode=0, stdout="")) as run:
                result = module.compose("ps", "--format", "json", timeout=7)
                self.assertEqual(result.returncode, 0)
                self.assertEqual(run.call_args.args[0],
                                 ["docker", "compose", "ps", "--format", "json"])
                self.assertEqual(run.call_args.kwargs["cwd"], root)
                self.assertEqual(run.call_args.kwargs["timeout"], 7)
                self.assertEqual(run.call_args.kwargs["env"]["DOCKER_CONFIG"],
                                 str(root / ".docker-cli-controls"))
                self.assertTrue((root / ".docker-cli-controls").is_dir())
                self.assertFalse((root / ".docker-cli-controls" /
                                  "config.json").exists())

    def test_config_update_preserves_secrets_and_uses_recreate(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            original = b"DISCORD_TOKEN=leave-untouched\nVOICE_CHANNEL_ID=1307370944255229956\n"
            (bridge / ".env").write_bytes(original)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                module, "compose",
                return_value=SimpleNamespace(returncode=0),
            ) as compose, mock.patch.object(module, "verify_bot_guild_config"):
                result = module.update_bot_config({
                    "voice_channel_id": "1545789527556104393",
                    "text_reply_mode": "any",
                })
            content = (bridge / ".env").read_text()
            self.assertIn("DISCORD_TOKEN=leave-untouched", content)
            self.assertIn("VOICE_CHANNEL_ID=1545789527556104393", content)
            self.assertIn("TEXT_REPLY_MODE=any", content)
            self.assertEqual(result, ("VOICE_CHANNEL_ID", "TEXT_REPLY_MODE"))
            self.assertIn("--force-recreate", compose.call_args.args)
            self.assertEqual(len(list(
                (root / ".deploy-backups" / "controls-config").glob("*.env")
            )), 1)

    def test_config_update_rolls_back_on_docker_failure(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            original = b"DISCORD_TOKEN=secret\nVOICE_CHANNEL_ID=1307370944255229956\n"
            (bridge / ".env").write_bytes(original)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                module, "compose",
                return_value=SimpleNamespace(returncode=1),
            ), mock.patch.object(module, "verify_bot_guild_config"):
                with self.assertRaises(RuntimeError):
                    module.update_bot_config({
                        "voice_channel_id": "1545789527556104393",
                    })
            self.assertEqual((bridge / ".env").read_bytes(), original)

    def test_guild_configuration_requires_joined_server_and_its_own_channels(self):
        original = (
            b"GUILD_ID=1307370943416500314\n"
            b"VOICE_CHANNEL_ID=1307370944255229956\n"
            b"TEXT_CHANNEL_ID=1545789527556104393\n"
        )
        allowed = {
            "guild_id": "1307370943416500314",
            "voice": [{"id": "1307370944255229956", "name": "Voice"}],
            "text": [{"id": "1545789527556104393", "name": "Chat"}],
        }
        with mock.patch.object(module, "discord_server_channels", return_value=allowed) as channels:
            module.verify_bot_guild_config({"GUILD_ID": allowed["guild_id"]}, original)
            self.assertEqual(channels.call_count, 1)
            with self.assertRaises(ValueError):
                module.verify_bot_guild_config(
                    {"VOICE_CHANNEL_ID": "1545789527556104393"}, original)
            with self.assertRaises(ValueError):
                module.verify_bot_guild_config(
                    {"GUILD_ID": "346070665184542720"}, original)
        with mock.patch.object(module, "discord_server_channels") as channels:
            module.verify_bot_guild_config({"TSUKI_TTS_PROVIDER": "molab"}, original)
            channels.assert_not_called()

    def test_bot_config_reads_actual_colab_selection_not_molab_default(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_text(
                "TSUKI_TTS_PROVIDER=colab\nGUILD_ID=1307370943416500314\n")
            with mock.patch.object(module, "ROOT", root):
                bot = module.read_bot_config()
                self.assertEqual(bot["tts_provider"], "colab")
                self.assertEqual(bot["guild_id"], "1307370943416500314")
                self.assertEqual(module._selected_provider(bot), "colab")
                self.assertNotIn("tsuki_tts_provider", bot)

    def test_provider_health_checks_only_selected_runtime(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            path = root / "discord-voice-bridge"
            path.mkdir()
            (path / ".env").write_text("TSUKI_TTS_PROVIDER=colab\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "_provider_status", return_value={
                        "Colab": {"selected": True, "online": False},
                        "Molab": {"selected": False, "online": None}}) as check:
                _, _, cookie = self.call("/api/login", {"password": "a-local-test-password"})
                code, response, _ = self.call("/api/provider-health", cookie=cookie.split(";", 1)[0])
                self.assertEqual(code, 200)
                self.assertEqual(response["selected_provider"], "colab")
                self.assertFalse(response["providers"]["Colab"]["online"])
                check.assert_called_once_with("colab")
        self.assertEqual(self.call("/api/provider-health")[0], 401)

    def test_docker_125_gets_one_retry_before_reporting_failure(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            path = root / "discord-voice-bridge"
            path.mkdir()
            (path / ".env").write_text("TSUKI_TTS_PROVIDER=molab\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", side_effect=[
                        SimpleNamespace(returncode=125),
                        SimpleNamespace(returncode=0)]) as compose, mock.patch.object(
                    module.time, "sleep"):
                self.assertEqual(module.update_bot_config({"tts_provider": "colab"}),
                                 ("TSUKI_TTS_PROVIDER",))
                self.assertEqual(compose.call_count, 2)
            self.assertIn("TSUKI_TTS_PROVIDER=colab", (path / ".env").read_text())

    def test_identical_settings_never_restart_the_bridge(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            original = b"TSUKI_TTS_PROVIDER=molab\n"
            (bridge / ".env").write_bytes(original)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                module, "compose") as compose, mock.patch.object(
                module, "verify_bot_guild_config") as verify:
                result = module.update_bot_config({"tts_provider": "molab"})
            self.assertEqual(result, ())
            self.assertEqual((bridge / ".env").read_bytes(), original)
            verify.assert_not_called()
            compose.assert_not_called()

    def test_classifier_model_is_editable_with_proactive_and_fail_closed_validation(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (root / ".env").write_text("KEEP=root\n")
            (bridge / ".env").write_text("VOICE_ENGAGEMENT_MODE=context_aware\n"
                                          "CHAT_ENGAGEMENT_MODE=context_aware\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                with self.assertRaisesRegex(ValueError, "requires a configured classifier"):
                    module.checked_bot_config({"voice_engagement_mode": "proactive"})
                with self.assertRaisesRegex(ValueError, "Classifier model"):
                    module.checked_bot_config({"classifier_model": "bad model;rm"})
                self.assertEqual(module.update_bot_config({
                    "voice_engagement_mode": "proactive",
                    "classifier_model": "test/model-v1",
                }), ("VOICE_ENGAGEMENT_MODE", "ENGAGEMENT_CLASSIFIER_MODEL"))
                self.assertEqual(compose.call_args.args[-1], "bridge")
                self.assertEqual(module.read_bot_config()["classifier_model"],
                                 "test/model-v1")
                with self.assertRaisesRegex(ValueError, "non-proactive"):
                    module.checked_bot_config({"classifier_model": ""})
                changed = module.update_bot_config({
                    "voice_engagement_mode": "context_aware",
                    "classifier_model": "",
                })
                self.assertEqual(changed, (
                    "VOICE_ENGAGEMENT_MODE", "ENGAGEMENT_CLASSIFIER_MODEL"))
                self.assertFalse(module.read_bot_config()["classifier_model_configured"])
                self.assertEqual((root / ".env").read_text(), "KEEP=root\n")

    def test_existing_modelfile_is_the_visible_editable_personality_source(self):
        source = (Path(__file__).resolve().parents[1] /
                  "assets" / "Modelfile").read_bytes()
        base, span = module._modelfile_personality(source)
        self.assertIn("You are Tsuki", base)
        self.assertIn("PERSONALITY:", base)
        self.assertIn("CRITICAL: NEVER use emojis", base)
        self.assertTrue(span[1] > span[0])
        self.assertEqual(module._modelfile_with_personality(source, base), source)
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "assets").mkdir()
            modelfile = root / "assets" / "Modelfile"
            modelfile.write_bytes(source)
            (root / ".env").write_text("KEEP=root\n")
            (root / "discord-voice-bridge").mkdir()
            bridge_env = root / "discord-voice-bridge" / ".env"
            bridge_env.write_text("TSUKI_TTS_PROVIDER=off\n")
            replacement = base.replace("joyful, casually witty",
                                       "gentle, occasionally witty")
            self.assertNotEqual(base, replacement)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                self.assertEqual(module.read_personality()["base_personality"], base)
                self.assertEqual(module.read_personality()["personality_injection"], "")
                self.assertEqual(module.update_bot_config({
                    "base_personality": replacement}),
                    ("__MODELFILE_PERSONALITY__",))
                compose.assert_called_once()
                self.assertEqual(compose.call_args.args[-1], "api")
                self.assertEqual(module.read_personality()["base_personality"],
                                 replacement)
                self.assertEqual(module.update_bot_config({
                    "base_personality": replacement}), ())
                self.assertEqual(compose.call_count, 1)
                self.assertEqual(module.update_bot_config({
                    "base_personality": base}), ("__MODELFILE_PERSONALITY__",))
                self.assertEqual(modelfile.read_bytes(), source)
                self.assertEqual(compose.call_count, 2)
            self.assertEqual(bridge_env.read_text(), "TSUKI_TTS_PROVIDER=off\n")
            self.assertEqual((root / ".env").read_text(), "KEEP=root\n")

    def test_modelfile_write_is_strict_and_failed_api_recreation_restores_original(self):
        for invalid in ('', 'x' * 4001, '"""', 'abc\x00def', 'abc\x7fdef'):
            with self.assertRaisesRegex(ValueError, "Modelfile"):
                module.checked_bot_config({"base_personality": invalid})
        for broken in (b'FROM qwen\n', b'SYSTEM """\nmissing close',
                       b'SYSTEM """\nA\n"""\nSYSTEM """\nB\n"""\n'):
            with self.assertRaisesRegex(ValueError, "Modelfile"):
                module._modelfile_personality(broken)
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "assets").mkdir()
            original = b'FROM qwen\nPARAMETER top_p 0.9\nSYSTEM """\nTsuki base\n"""\n'
            path = root / "assets" / "Modelfile"
            path.write_bytes(original)
            (root / ".env").write_text("KEEP=root\n")
            (root / "discord-voice-bridge").mkdir()
            (root / "discord-voice-bridge" / ".env").write_text("KEEP=bridge\n")
            outcomes = [SimpleNamespace(returncode=1), SimpleNamespace(returncode=0)]
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", side_effect=outcomes) as compose:
                with self.assertRaisesRegex(RuntimeError, "api"):
                    module.update_bot_config({"base_personality": "Tsuki changed"})
                self.assertEqual(compose.call_count, 2)
            self.assertEqual(path.read_bytes(), original)
            self.assertEqual((root / ".env").read_text(), "KEEP=root\n")

    def test_personality_is_private_round_trip_and_restarts_only_api(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (root / ".env").write_text("TSUKI_WEB_PASSWORD=existing\nKEEP=unchanged\n")
            (root / "assets").mkdir()
            (root / "assets" / "Modelfile").write_text(
                'FROM qwen2.5:3b\nSYSTEM """\nExisting Tsuki personality\n"""\n')
            (bridge / ".env").write_text("TSUKI_TTS_PROVIDER=molab\n")
            original_bridge = (bridge / ".env").read_bytes()
            instructions = "Be gentle.\nUse quiet humor. 月"
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                self.assertEqual(
                    module.update_bot_config({"personality_injection": instructions}),
                    ("TSUKI_PERSONALITY_INJECTION_B64",))
                compose.assert_called_once()
                self.assertEqual(compose.call_args.args[-1], "api")
                self.assertEqual(module.read_personality()["personality_injection"], instructions)
                self.assertNotIn(instructions, (root / ".env").read_text())
                self.assertEqual((bridge / ".env").read_bytes(), original_bridge)
                self.assertNotIn("personality_injection", module.read_bot_config())
                self.assertNotIn(instructions, str(module.read_bot_config()))
                self.assertEqual(
                    module.update_bot_config({"personality_injection": instructions}), ())
                self.assertEqual(compose.call_count, 1)
                self.assertEqual(
                    module.update_bot_config({"personality_injection": ""}),
                    ("TSUKI_PERSONALITY_INJECTION_B64",))
                self.assertEqual(module.read_personality(), {
                    "base_personality": "Existing Tsuki personality",
                    "base_source": "assets/Modelfile · SYSTEM",
                    "personality_injection": "", "customized": False})
                self.assertEqual(compose.call_count, 2)
            self.assertIn("KEEP=unchanged", (root / ".env").read_text())

    def test_personality_invalid_input_and_failed_restart_rollback(self):
        for text in ("x" * 4001, "abc\x00def", "abc\x7fdef"):
            with self.assertRaisesRegex(ValueError, "Personality"):
                module.checked_bot_config({"personality_injection": text})
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            original = b"TSUKI_LLM_PROVIDER=api\nKEEP=unchanged\n"
            (root / ".env").write_bytes(original)
            (bridge / ".env").write_text("TSUKI_TTS_PROVIDER=molab\n")
            responses = [SimpleNamespace(returncode=1), SimpleNamespace(returncode=0)]
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", side_effect=responses) as compose:
                with self.assertRaisesRegex(RuntimeError, "api"):
                    module.update_bot_config({"personality_injection": "a change"})
                self.assertEqual(compose.call_count, 2)
            self.assertEqual((root / ".env").read_bytes(), original)

    def test_personality_endpoint_requires_session_and_csrf_and_has_no_status_leak(self):
        self.assertEqual(self.call("/api/personality")[0], 401)
        self.assertEqual(self.call("/api/personality", {
            "personality_injection": "test"})[0], 401)
        _, _, cookie = self.call("/api/login", {"password": "a-local-test-password"})
        cookie = cookie.split(";", 1)[0]
        token = cookie.split("=", 1)[1]
        csrf = module.SESSIONS[token][1]
        self.assertEqual(self.call("/api/personality", {
            "personality_injection": "test"}, cookie=cookie)[0], 403)
        self.assertEqual(self.call("/api/personality", {
            "other_setting": "x"}, cookie=cookie, csrf=csrf)[0], 400)
        with mock.patch.object(module, "read_personality",
                               return_value={"base_personality": "Existing SYSTEM",
                                             "base_source": "assets/Modelfile · SYSTEM",
                                             "personality_injection": "secret persona",
                                             "customized": True}):
            code, state, _ = self.call("/api/personality", cookie=cookie)
            self.assertEqual(code, 200)
            self.assertEqual(state["personality_injection"], "secret persona")
            self.assertEqual(state["base_personality"], "Existing SYSTEM")
        self.assertEqual(self.call("/api/personality")[0], 401)
        module.CONFIG_JOBS.clear()
        with mock.patch.object(module, "update_bot_config",
                               return_value=("TSUKI_PERSONALITY_INJECTION_B64",)) as update:
            code, state, _ = self.call("/api/personality", {
                "personality_injection": "test"}, cookie=cookie, csrf=csrf)
            self.assertEqual(code, 202)
            for _ in range(100):
                code, job, _ = self.call(
                    "/api/config-jobs/" + state["job_id"], cookie=cookie)
                if job.get("state") == "done":
                    break
                import time
                time.sleep(.01)
            self.assertEqual(job["state"], "done")
            update.assert_called_once_with({"personality_injection": "test"})
        module.CONFIG_JOBS.clear()
        with mock.patch.object(module, "update_bot_config",
                               return_value=("__MODELFILE_PERSONALITY__",)) as update:
            code, state, _ = self.call("/api/personality", {
                "base_personality": "New existing SYSTEM"}, cookie=cookie, csrf=csrf)
            self.assertEqual(code, 202)
            for _ in range(100):
                code, job, _ = self.call(
                    "/api/config-jobs/" + state["job_id"], cookie=cookie)
                if job.get("state") == "done":
                    break
                import time
                time.sleep(.01)
            self.assertEqual(job["state"], "done")
            update.assert_called_once_with({"base_personality": "New existing SYSTEM"})
        module.CONFIG_JOBS.clear()

    def test_inference_api_provider_order_uses_actual_csharp_settings_not_tts(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_text("TSUKI_TTS_PROVIDER=colab\n")
            (root / ".env").write_text(
                "TSUKI_USE_MULTIPLE_PROVIDERS=false\n"
                "TSUKI_MULTI_PROVIDERS_CSV=groq\n"
                "TSUKI_LLM_PROVIDER=molab\nOTHER=preserved\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                changed = module.update_inference_providers(
                    {"providers": ["groq", "cerebras", "mistral"]})
                self.assertEqual(
                    changed,
                    ("TSUKI_USE_MULTIPLE_PROVIDERS", "TSUKI_MULTI_PROVIDERS_CSV",
                     "TSUKI_LLM_PROVIDER"))
                self.assertEqual([call.args[-1] for call in compose.call_args_list],
                                 ["api"])
                self.assertEqual(module.update_inference_providers({
                    "providers": ["groq", "cerebras", "mistral"]}), ())
                self.assertEqual(compose.call_count, 1)
                state = module.read_inference_providers()
                self.assertEqual(state["selected"], ["groq", "cerebras", "mistral"])
                self.assertTrue(state["multi_enabled"])
                self.assertEqual(state["legacy_override"], "api")
                self.assertEqual(state["usage"]["total_calls_today"], None)
            self.assertEqual((bridge / ".env").read_text(),
                             "TSUKI_TTS_PROVIDER=colab\n")
            self.assertIn("OTHER=preserved", (root / ".env").read_text())
            for invalid in ([], ["molab"], ["colab"], ["groq", "groq"],
                            ["groq", "unknown"], ["groq"] * 6):
                with self.assertRaises(ValueError):
                    module.checked_inference_change({"providers": invalid})
            with self.assertRaisesRegex(ValueError, "not both"):
                module.checked_inference_change({"providers": ["groq", "literouter"]})
            with self.assertRaises(ValueError):
                module.checked_bot_config({"llm_provider": "molab"})

    def test_inference_api_provider_failure_restores_env(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            original = b"TSUKI_MULTI_PROVIDERS_CSV=groq\nTSUKI_LLM_PROVIDER=molab\n"
            (root / ".env").write_bytes(original)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", side_effect=[
                        SimpleNamespace(returncode=1),
                        SimpleNamespace(returncode=0)]) as compose:
                with self.assertRaisesRegex(RuntimeError, "api"):
                    module.update_inference_providers({"providers": ["cerebras"]})
                self.assertEqual(compose.call_count, 2)
            self.assertEqual((root / ".env").read_bytes(), original)

    def test_literouter_must_have_key_and_chosen_model_before_activation(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / ".env"
            path.write_text("TSUKI_MULTI_PROVIDERS_CSV=groq\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                with self.assertRaisesRegex(ValueError, "key"):
                    module.update_inference_providers({
                        "providers": ["literouter"]})
                path.write_text(path.read_text() +
                                "TSUKI_LITEROUTER_API_KEY=private-test-key\n")
                with self.assertRaisesRegex(ValueError, "model"):
                    module.update_inference_providers({
                        "providers": ["literouter"]})
                path.write_text(path.read_text() +
                                "TSUKI_LITEROUTER_MODEL=deepseek-v4-flash:free\n")
                self.assertIn("TSUKI_MULTI_PROVIDERS_CSV",
                              module.update_inference_providers({
                                  "providers": ["literouter"]}))
                self.assertEqual(compose.call_count, 1)

    def test_literouter_add_remove_preserves_other_keys_and_migrates_legacy_single(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / ".env"
            first = "sk-existing-provider-key001"
            second = "sk-existing-provider-key002"
            third = "sk-new-provider-key000003"
            path.write_text(
                "TSUKI_LITEROUTER_API_KEY=" + first + "\n" +
                "TSUKI_LITEROUTER_API_KEYS=" + second + "\n" +
                "OTHER=keep\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                inventory = module.read_literouter_keys()
                self.assertEqual(inventory["total_keys"], 2)
                self.assertEqual(len(inventory["keys"]), 2)
                self.assertNotIn(first, str(inventory))
                self.assertNotIn(second, str(inventory))
                self.assertTrue(all(len(key["id"]) == 64
                                    for key in inventory["keys"]))
                self.assertEqual(module.update_literouter_keys({
                    "action": "add", "key": third}),
                    ("TSUKI_LITEROUTER_API_KEYS", "TSUKI_LITEROUTER_API_KEY"))
                self.assertEqual(module.update_literouter_keys({
                    "action": "add", "key": third}), ())
                self.assertEqual(compose.call_count, 1)
                state = module._env_rows(path.read_bytes())
                self.assertEqual(state["TSUKI_LITEROUTER_API_KEYS"],
                                 second + "," + first + "," + third)
                self.assertEqual(state["TSUKI_LITEROUTER_API_KEY"], "")
                self.assertEqual(state["OTHER"], "keep")
                self.assertEqual(module.update_literouter_keys({
                    "action": "remove", "id": module._key_identity(first)}),
                    ("TSUKI_LITEROUTER_API_KEYS", "TSUKI_LITEROUTER_API_KEY"))
                self.assertEqual(module._literouter_keys()[0], [second, third])
                self.assertNotIn(first, str(module.read_literouter_keys()))
                self.assertEqual(module.update_literouter_keys({
                    "action": "remove", "id": module._key_identity(second)}),
                    ("TSUKI_LITEROUTER_API_KEYS", "TSUKI_LITEROUTER_API_KEY"))
                self.assertEqual(module._literouter_keys()[0], [third])
                self.assertEqual([call.args[-1] for call in compose.call_args_list],
                                 ["api", "api", "api"])
                self.assertEqual(module.update_literouter_keys({
                    "action": "remove", "id": module._key_identity(third)}),
                    ("TSUKI_LITEROUTER_API_KEYS", "TSUKI_LITEROUTER_API_KEY"))
                self.assertEqual(module.read_literouter_keys()["total_keys"], 0)
                with self.assertRaisesRegex(ValueError, "no longer exists"):
                    module.update_literouter_keys({
                        "action": "remove", "id": module._key_identity(first)})
            for invalid in ("short", "key\nEVIL=1", "key$secret", "key,another"):
                with self.assertRaises(ValueError):
                    module.checked_literouter_change({
                        "action": "add", "key": invalid})

    def test_literouter_final_key_active_and_external_file_are_protected(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / ".env"
            key = "sk-literouter-key-example"
            path.write_text(
                "TSUKI_LLM_PROVIDER=api\n"
                "TSUKI_USE_MULTIPLE_PROVIDERS=true\n"
                "TSUKI_MULTI_PROVIDERS_CSV=groq,literouter\n"
                "TSUKI_LITEROUTER_API_KEY=" + key + "\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                with self.assertRaisesRegex(ValueError, "active provider order"):
                    module.update_literouter_keys({
                        "action": "remove", "id": module._key_identity(key)})
                compose.assert_not_called()
                path.write_text(path.read_text() +
                                "TSUKI_LITEROUTER_API_KEYS_FILE=/run/secrets/literouter\n")
                external = module.read_literouter_keys()
                self.assertFalse(external["managed"])
                self.assertIsNone(external["total_keys"])
                self.assertEqual(external["keys"], [])
                with self.assertRaisesRegex(ValueError, "external key file"):
                    module.update_literouter_keys({
                        "action": "add", "key": "sk-another-provider-key"})
                compose.assert_not_called()

    def test_literouter_key_change_rollback_restores_private_env(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / ".env"
            original = b"OTHER=preserved\nTSUKI_LITEROUTER_API_KEYS=sk-before-12345678\n"
            path.write_bytes(original)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", side_effect=[
                        SimpleNamespace(returncode=1),
                        SimpleNamespace(returncode=0)]) as compose:
                with self.assertRaisesRegex(RuntimeError, "api"):
                    module.update_literouter_keys({
                        "action": "add", "key": "sk-new-123456789012"})
                self.assertEqual(compose.call_count, 2)
            self.assertEqual(path.read_bytes(), original)

    def test_inference_non_groq_key_is_private_and_recreates_api_only(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".env").write_text("OTHER=keep\n")
            secret = "sk-alpha1234567890TOKEN"
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                self.assertEqual(module.update_inference_key({
                    "provider": "cerebras", "key": secret}), ("TSUKI_CEREBRAS_API_KEY",))
                self.assertEqual(module.update_inference_key({
                    "provider": "cerebras", "key": secret}), ())
                state = module.read_inference_providers()
                cerebras = next(item for item in state["providers"]
                                if item["id"] == "cerebras")
                self.assertTrue(cerebras["credential_configured"])
                self.assertEqual(cerebras["credential_masked"], "sk-...TOKEN")
                self.assertNotIn(secret, str(state))
                self.assertEqual([call.args[-1] for call in compose.call_args_list],
                                 ["api"])
                self.assertEqual(module.update_inference_key({
                    "provider": "cerebras", "action": "remove"}),
                    ("TSUKI_CEREBRAS_API_KEY",))
                self.assertFalse(next(item for item in module.read_inference_providers()[
                    "providers"] if item["id"] == "cerebras")["credential_configured"])
            for invalid in ("x\nINJECT=value", "abc$DEF", "short"):
                with self.assertRaises(ValueError):
                    module.checked_inference_key_change({
                        "provider": "gemini", "key": invalid})
            with self.assertRaises(ValueError):
                module.checked_inference_key_change({
                    "provider": "unknown", "key": secret})

    def test_inference_models_read_only_and_account_specific(self):
        class ModelResponse:
            status = 200
            def __enter__(self):
                return self
            def __exit__(self, *_):
                return False
            def read(self, *_):
                return json.dumps({"data": [
                    {"id": "mistral-small-2603:free"},
                    {"id": "deepseek-v4-flash:free"},
                    {"id": "deepseek-v4-flash:free"},
                    {"id": "other:metered"},
                    {"id": "retired", "active": False}
                ]}).encode()
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".env").write_text("TSUKI_LITEROUTER_API_KEY=private-test-key\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module.urllib.request, "urlopen",
                    return_value=ModelResponse()) as fetch:
                catalog = module.read_inference_models("literouter")
                self.assertEqual(catalog["models"], [
                    "deepseek-v4-flash:free", "mistral-small-2603:free"])
                self.assertTrue(catalog["no_prompt_sent"])
                self.assertEqual(catalog["count"], 2)
                request = fetch.call_args.args[0]
                self.assertEqual(request.get_method(), "GET")
                self.assertEqual(request.full_url,
                                 "https://api.literouter.com/v1/models")
                self.assertEqual(
                    request.headers["Authorization"], "Bearer private-test-key")
                self.assertNotIn("private-test-key", str(catalog))
            with self.assertRaises(ValueError):
                module.read_inference_models("molab")

    def test_inference_model_saved_only_when_verified_on_catalog_and_rolls_back(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            original = b"TSUKI_LITEROUTER_API_KEY=private-test-key\nOTHER=keep\n"
            (root / ".env").write_bytes(original)
            catalog = {
                "provider": "literouter",
                "models": ["mistral-small-2603:free", "deepseek-v4-flash:free"],
                "no_prompt_sent": True}
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "read_inference_models", return_value=catalog), mock.patch.object(
                    module, "compose",
                    return_value=SimpleNamespace(returncode=0)) as compose:
                with self.assertRaisesRegex(ValueError, "not in"):
                    module.update_inference_model({
                        "provider": "literouter", "model": "invented:free"})
                with self.assertRaisesRegex(ValueError, ":free"):
                    module.update_inference_model({
                        "provider": "literouter", "model": "other:metered"})
                compose.assert_not_called()
                self.assertEqual((root / ".env").read_bytes(), original)
                self.assertEqual(module.update_inference_model({
                    "provider": "literouter", "model": "mistral-small-2603:free"}),
                    ("TSUKI_LITEROUTER_MODEL",))
                self.assertEqual([call.args[-1] for call in compose.call_args_list],
                                 ["api"])
                self.assertEqual(module.update_inference_model({
                    "provider": "literouter", "model": "mistral-small-2603:free"}), ())
            self.assertIn("TSUKI_LITEROUTER_MODEL=mistral-small-2603:free",
                          (root / ".env").read_text())
            with mock.patch.object(module, "ROOT", root):
                persisted = module.read_inference_providers()["providers"]
                self.assertEqual(next(item for item in persisted if item["id"] == "literouter")["selected_model"],
                                 "mistral-small-2603:free")
                self.assertEqual(next(item for item in persisted if item["id"] == "groq")["selected_model"],
                                 "openai/gpt-oss-120b")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "read_inference_models", return_value=catalog), mock.patch.object(
                    module, "compose", side_effect=[
                        SimpleNamespace(returncode=1),
                        SimpleNamespace(returncode=0)]):
                saved = (root / ".env").read_bytes()
                with self.assertRaisesRegex(RuntimeError, "api"):
                    module.update_inference_model({
                        "provider": "literouter", "model": "deepseek-v4-flash:free"})
                self.assertEqual((root / ".env").read_bytes(), saved)

    def test_groq_model_listing_and_model_config_are_separate_from_tts(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (bridge / ".env").write_text("TSUKI_TTS_PROVIDER=colab\n")
            (root / ".env").write_text(
                "TSUKI_GROQ_API_KEYS=gsk_" + "a" * 24 + "\n")
            class Models:
                status = 200
                def __enter__(self):
                    return self
                def __exit__(self, *_):
                    return False
                def read(self, *_):
                    return b'{"data":[{"id":"openai/gpt-oss-120b"}]}'
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module.urllib.request, "urlopen", return_value=Models()) as fetch:
                models = module.read_inference_models("groq")
                self.assertEqual(models["models"], ["openai/gpt-oss-120b"])
                self.assertEqual(fetch.call_args.args[0].get_method(), "GET")
                self.assertEqual(fetch.call_args.args[0].full_url,
                                 "https://api.groq.com/openai/v1/models")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "read_inference_models", return_value=models), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)):
                self.assertEqual(module.update_inference_model({
                    "provider": "groq", "model": "openai/gpt-oss-120b"}),
                    ("TSUKI_GROQ_MODEL",))
            self.assertEqual((bridge / ".env").read_text(),
                             "TSUKI_TTS_PROVIDER=colab\n")
            self.assertEqual(module.checked_inference_model_change({
                "provider": "literouter", "model": "deepseek-v4-flash:free"})[
                    "model"], "deepseek-v4-flash:free")
            for invalid in ("gpt oss", "x\nHOST=evil", "$SECRET", ""):
                with self.assertRaises(ValueError):
                    module.checked_inference_model_change({
                        "provider": "groq", "model": invalid})

    def test_groq_keys_masked_migrated_and_missing_daily_usage_is_unavailable(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            original = "gsk_" + "a" * 22
            added = "gsk_" + "b" * 22
            (root / ".env").write_text("TSUKI_GROQ_API_KEYS=" + original + "\n")
            (bridge / ".env").write_text("TSUKI_TTS_PROVIDER=molab\n")
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", return_value=SimpleNamespace(returncode=0)) as compose:
                before = module.read_groq_keys()
                self.assertEqual(before["source"], "inline")
                self.assertEqual(before["total_keys"], 1)
                self.assertNotIn(original, str(before))
                self.assertEqual(before["calls_today"], None)
                self.assertEqual(module.update_groq_keys({
                    "action": "add", "key": added}), ("GROQ_KEYS",))
                self.assertEqual(module.update_groq_keys({
                    "action": "add", "key": added}), ())
                self.assertEqual([call.args[-1] for call in compose.call_args_list
                                  if call.args[:3] == ("up", "-d", "--no-deps")],
                                 ["api", "bridge"])
                file_body = (bridge / "groq.keys").read_text()
                self.assertIn(original, file_body)
                self.assertIn(added, file_body)
                state = module.read_groq_keys()
                self.assertEqual(state["total_keys"], 2)
                self.assertEqual(state["source"], "file")
                self.assertTrue(all(len(item["id"]) == 64 for item in state["keys"]))
                self.assertNotIn(original, str(state))
                self.assertNotIn(added, str(state))
                self.assertEqual(state["timezone"], "UTC")
                self.assertEqual(module.update_groq_keys({
                    "action": "remove", "id": module._key_identity(added)}),
                    ("GROQ_KEYS",))
                self.assertNotIn(added, (bridge / "groq.keys").read_text())
                self.assertEqual((bridge / ".env").read_text(),
                                 "TSUKI_TTS_PROVIDER=molab\n")
            for invalid in ("sk-short", "gsk_a\nTSUKI_API_KEY=bad"):
                with self.assertRaises(ValueError):
                    module.checked_groq_change({"action": "add", "key": invalid})

    def test_groq_key_recreate_failure_restores_original_bytes(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            bridge = root / "discord-voice-bridge"
            bridge.mkdir()
            (root / ".env").write_text("OTHER=keep\n")
            (bridge / ".env").write_text("TSUKI_TTS_PROVIDER=molab\n")
            original = ("gsk_" + "x" * 22 + "\n").encode()
            (bridge / "groq.keys").write_bytes(original)
            with mock.patch.object(module, "ROOT", root), mock.patch.object(
                    module, "compose", side_effect=[
                        SimpleNamespace(returncode=0), SimpleNamespace(returncode=1),
                        SimpleNamespace(returncode=0), SimpleNamespace(returncode=0)]) as compose:
                with self.assertRaisesRegex(RuntimeError, "bridge"):
                    module.update_groq_keys({
                        "action": "add", "key": "gsk_" + "z" * 22})
                self.assertEqual([call.args[-1] for call in compose.call_args_list],
                                 ["api", "bridge", "api", "bridge"])
            self.assertEqual((bridge / "groq.keys").read_bytes(), original)

    def test_groq_usage_counts_actual_api_and_bridge_attempts_only_with_coverage(self):
        utc = module.datetime.timezone.utc
        now = module.datetime.datetime(2026, 9, 21, 12, tzinfo=utc)
        key_id = module._key_identity("gsk_" + "a" * 22)
        marker = {"type": "tracking_started",
                  "timestamp_utc": "2026-09-20T12:00:00Z",
                  "key_id": None, "key_mask": None, "kind": None}
        attempt = {"type": "attempt",
                   "timestamp_utc": "2026-09-21T01:00:00Z",
                   "key_id": key_id, "key_mask": "gsk_...aaaaa",
                   "kind": "llm"}
        one = json.dumps(marker) + "\n" + json.dumps(attempt) + "\n"
        mock_outputs = [
            SimpleNamespace(returncode=0, stdout=one),
            SimpleNamespace(returncode=0, stdout=one),
        ]
        with mock.patch.object(module, "compose", side_effect=mock_outputs) as compose:
            actual = module._groq_usage_snapshot(now)
        self.assertEqual(actual["coverage"], "complete")
        self.assertEqual(actual["total_calls_today"], 2)
        self.assertEqual(actual["per_key"][key_id], 2)
        self.assertEqual(actual["date"], "2026-09-21")
        self.assertEqual(actual["timezone"], "UTC")
        self.assertEqual([call.args[-1] for call in compose.call_args_list],
                         ["/data/groq-usage-api.jsonl",
                          "/data/groq-usage-bridge.jsonl"])
        late_marker = {**marker,
                       "timestamp_utc": "2026-09-21T06:00:00Z"}
        late = json.dumps(late_marker) + "\n" + json.dumps(attempt) + "\n"
        with mock.patch.object(module, "compose", side_effect=[
                SimpleNamespace(returncode=0, stdout=late),
                SimpleNamespace(returncode=0, stdout=one)]):
            partial = module._groq_usage_snapshot(now)
        self.assertEqual(partial["coverage"], "partial")
        self.assertIsNone(partial["total_calls_today"])
        self.assertEqual(partial["recorded_calls_today"], 2)
        with mock.patch.object(module, "compose", side_effect=[
                SimpleNamespace(returncode=0, stdout=one),
                SimpleNamespace(returncode=1, stdout="")]):
            missing = module._groq_usage_snapshot(now)
        self.assertIsNone(missing["total_calls_today"])
        self.assertEqual(missing["recorded_calls_today"], 1)

    def test_inference_inventory_routes_require_session_and_csrf(self):
        self.assertEqual(self.call("/api/inference-providers")[0], 401)
        self.assertEqual(self.call("/api/groq-keys")[0], 401)
        self.assertEqual(self.call("/api/literouter-keys")[0], 401)
        self.assertEqual(self.call("/api/inference-providers", {
            "providers": ["groq"]})[0], 401)
        self.assertEqual(self.call("/api/literouter-keys", {
            "action": "add", "key": "sk-key-not-sent"} )[0], 401)
        _, _, cookie = self.call(
            "/api/login", {"password": "a-local-test-password"})
        cookie = cookie.split(";", 1)[0]
        csrf = module.SESSIONS[cookie.split("=", 1)[1]][1]
        self.assertEqual(self.call("/api/groq-keys", {
            "action": "add", "key": "gsk_" + "a" * 22}, cookie=cookie)[0], 403)
        self.assertEqual(self.call("/api/literouter-keys", {
            "action": "add", "key": "sk-test-provider-key"}, cookie=cookie)[0], 403)
        self.assertEqual(self.call("/api/inference-key", {
            "provider": "gemini", "key": "AIza123456789"}, cookie=cookie)[0], 403)
        self.assertEqual(self.call("/api/inference-providers", {
            "providers": ["molab"]}, cookie=cookie, csrf=csrf)[0], 400)
        self.assertEqual(self.call("/api/groq-keys", {
            "action": "add", "key": "short"}, cookie=cookie, csrf=csrf)[0], 400)
        self.assertEqual(self.call("/api/literouter-keys", {
            "action": "remove", "id": "invalid"}, cookie=cookie, csrf=csrf)[0], 400)

    def test_config_job_is_accepted_and_reports_completion(self):
        with mock.patch.object(module, "update_bot_config", return_value=(
                "TSUKI_TTS_PROVIDER",)) as update:
            module.CONFIG_JOBS.clear()
            _, _, cookie = self.call("/api/login", {"password": "a-local-test-password"})
            cookie = cookie.split(";", 1)[0]
            csrf = module.SESSIONS[cookie.split("=", 1)[1]][1]
            code, response, _ = self.call("/api/bot-config", {
                "tts_provider": "colab"
            }, cookie=cookie, csrf=csrf)
            self.assertEqual(code, 202)
            self.assertIn("job_id", response)
            import time
            result = {}
            for _ in range(100):
                code, result, _ = self.call(
                    "/api/config-jobs/" + response["job_id"], cookie=cookie)
                if result.get("state") == "done":
                    break
                time.sleep(.01)
            self.assertEqual(code, 200)
            self.assertEqual(result["updated"], ["TSUKI_TTS_PROVIDER"])
            update.assert_called_once_with({"tts_provider": "colab"})
            self.assertEqual(self.call(
                "/api/config-jobs/" + response["job_id"])[0], 401)
            module.CONFIG_JOBS.clear()

    def test_config_job_reports_failure_without_resending_changes(self):
        with mock.patch.object(module, "update_bot_config",
                               side_effect=RuntimeError("internal private diagnostic")):
            module.CONFIG_JOBS.clear()
            job_id = module.begin_bot_config_job({"tts_provider": "off"})
            import time
            for _ in range(100):
                with module.CONFIG_JOB_LOCK:
                    state = dict(module.CONFIG_JOBS[job_id])
                if state["state"] == "failed":
                    break
                time.sleep(.01)
            self.assertEqual(state["state"], "failed")
            self.assertNotIn("private diagnostic", state["error"])
            module.CONFIG_JOBS.clear()

    def test_private_csp_and_no_transform_html(self):
        with mock.patch.object(module, "ROOT", Path(__file__).resolve().parents[1]):
            status, headers, payload = self.wire("/")
            self.assertEqual(status, 200)
            html = payload.decode()
            self.assertIn("no-transform", headers["Cache-Control"])
            self.assertIn("script-src 'self'", headers["Content-Security-Policy"])
            self.assertIn("copy-invite", html)
            self.assertIn("persona-source-layout-22", html)
            self.assertIn('id="page-search"', html)
            self.assertIn('id="session-refresh"', html)
            self.assertIn('<meta name="theme-color" content="#0a0a0a">', html)
            self.assertIn("persona-source-layout-22", html)
            self.assertIn('id="voice_channel_id"', html)
            self.assertIn('id="text_channel_id"', html)
            self.assertIn('id="sidebar"', html)
            self.assertIn('data-tab="overview"', html)
            self.assertIn('data-tab="discord-voice"', html)
            self.assertIn('data-tab="ai"', html)
            self.assertNotIn('data-tab="discord-settings"', html)
            self.assertNotIn('data-tab="discord"', html)
            self.assertNotIn('data-tab="voice"', html)
            self.assertIn('id="context-awareness"', html)
            self.assertIn('id="personality-settings"', html)
            self.assertIn('id="personality-editor"', html)
            self.assertIn('name="base_personality"', html)
            self.assertIn('id="personality-override-editor"', html)
            self.assertIn('data-view="ai" id="personality"', html)
            self.assertIn('data-view="ai" id="context-awareness"', html)
            self.assertIn('data-view="ai" id="llm"', html)
            self.assertLess(html.index('id="personality"'), html.index('id="llm"'))
            self.assertIn('data-view="discord-voice" id="discord"', html)
            self.assertIn('href="/layout-v22.css?v=exclusive-folds-28"', html)
            self.assertNotIn("Additional personality and behavior instructions", html)
            self.assertIn('href="#personality"', html)
            self.assertNotIn("PLAN ONLY", html)
            self.assertIn('id="engagement-settings"', html)
            self.assertIn('id="voice_engagement_mode"', html)
            self.assertIn('id="chat_engagement_mode"', html)
            self.assertIn('id="engagement_followup_ms"', html)
            self.assertIn('id="classifier_model"', html)
            self.assertIn('id="engagement-metrics"', html)
            self.assertIn('data-view="overview"', html)
            self.assertIn('fieldset class="choice-group" id="text_reply_mode"', html)
            self.assertIn('fieldset class="choice-group" id="text_voice_mode"', html)
            self.assertNotIn('<select id="text_reply_mode"', html)
            self.assertNotIn('<select id="text_voice_mode"', html)
            self.assertIn('name="text_reply_mode" value="mention"', html)
            self.assertIn('name="text_reply_mode" value="any"', html)
            self.assertIn('name="text_voice_mode" value="keywords"', html)
            self.assertIn('name="text_voice_mode" value="off"', html)
            self.assertIn('id="leave-voice"', html)
            self.assertIn('id="custom-tts-form"', html)
            self.assertIn("id=\"channel-scope-note\"", html)
            self.assertIn("class=\"optional-inline\"", html)
            self.assertNotIn("Every reply", html)
            self.assertIn("text_channel_id", html)
            css_status, css_headers, css_bytes = self.wire("/layout-v22.css")
            self.assertEqual(css_status, 200)
            self.assertEqual(css_headers["Content-Type"], "text/css; charset=utf-8")
            self.assertIn(b"data-active-tab=\"ai\"", css_bytes)
            self.assertIn(b".overview-health", css_bytes)

    def test_session_probe_is_safe_before_login(self):
        self.assertEqual(self.call("/api/session")[0], 200)
        self.assertFalse(self.call("/api/session")[1]["authenticated"])
        status, _, cookie = self.call("/api/login", {"password": "a-local-test-password"})
        self.assertEqual(status, 200)
        code, body, _ = self.call("/api/session", cookie=cookie.split(";", 1)[0])
        self.assertEqual(code, 200)
        self.assertTrue(body["authenticated"])

    def test_joined_discord_servers_and_channels_are_from_discord(self):
        calls = []
        def fake_request(method, path, payload=None, timeout=15):
            calls.append(path)
            if path == "/users/@me/guilds":
                return 200, [
                    {"id": "346070665184542720", "name": "Tunafied"},
                    {"id": "1307370943416500314", "name": "Hideout"},
                ]
            if path == "/guilds/1307370943416500314/channels":
                return 200, [
                    {"id": "1307370944255229956", "name": "General Voice", "type": 2},
                    {"id": "1545789527556104393", "name": "Bot Chat", "type": 0},
                    {"id": "1545789527556104394", "name": "Announcements", "type": 5},
                    {"id": "1545789527556104395", "name": "Private", "type": 4},
                ]
            raise AssertionError("Unexpected Discord endpoint: " + path)
        with mock.patch.object(module, "_discord_request", side_effect=fake_request):
            guilds = module.discord_servers()
            self.assertEqual(len(guilds), 2)
            self.assertEqual(guilds[0]["name"], "Hideout")
            channels = module.discord_server_channels("1307370943416500314")
            self.assertEqual([item["name"] for item in channels["voice"]],
                             ["General Voice"])
            self.assertEqual(len(channels["text"]), 2)
            with self.assertRaises(ValueError):
                module.discord_server_channels("1545789527556104395")
            with self.assertRaises(ValueError):
                module.discord_server_channels("../invalid")
        self.assertNotIn("/guilds/1545789527556104395/channels", calls)

    def test_discord_cache_avoids_rate_limit_and_uses_stale_data(self):
        calls = []
        def fake(method, path, payload=None, timeout=15):
            calls.append(path)
            if path.endswith("/channels"):
                return 200, [{"id": "1307370944255229956", "name": "Voice", "type": 2}]
            return 200, [{"id": "1307370943416500314", "name": "Hideout"}]
        with mock.patch.object(module, "_discord_request", side_effect=fake):
            self.assertEqual(len(module.discord_servers()), 1)
            self.assertEqual(len(module.discord_servers()), 1)
            channels = module.discord_server_channels("1307370943416500314")
            self.assertEqual(channels, module.discord_server_channels("1307370943416500314"))
        self.assertEqual(calls, [
            "/users/@me/guilds",
            "/guilds/1307370943416500314/channels",
        ])
        module.GUILD_CACHE["checked_at"] = module.time.monotonic() - module.DISCORD_CACHE_SECONDS - 1
        with mock.patch.object(module, "_discord_request",
                               side_effect=urllib.error.HTTPError(
                                   "https://discord.test", 429, "Rate limited", {}, None)):
            self.assertEqual(module.discord_servers()[0]["name"], "Hideout")

    def test_forced_guild_refresh_detects_new_joined_servers(self):
        state = {"new": False}
        calls = []

        def fake(method, path, payload=None, timeout=15):
            calls.append(path)
            self.assertEqual(path, "/users/@me/guilds")
            result = [{"id": "1307370943416500314", "name": "First"}]
            if state["new"]:
                result.append({"id": "1545789527556104395", "name": "New Server"})
            return 200, result

        with mock.patch.object(module, "_discord_request", side_effect=fake):
            self.assertEqual(len(module.discord_servers()), 1)
            state["new"] = True
            self.assertEqual(len(module.discord_servers()), 1)
            self.assertEqual(len(module.discord_servers(force=True)), 2)
        self.assertEqual(calls.count("/users/@me/guilds"), 2)

    def test_forced_channel_refresh_detects_new_channels_and_handles_429(self):
        guild = "1307370943416500314"
        first_voice = "1307370944255229956"
        next_voice = "1545789527556104395"
        next_text = "1545789527556104393"
        state = {"new": False, "rate_limited": False}
        calls = []

        def fake(method, path, payload=None, timeout=15):
            calls.append(path)
            if path == "/users/@me/guilds":
                return 200, [{"id": guild, "name": "Test Guild"}]
            if path == "/guilds/" + guild + "/channels":
                if state["rate_limited"]:
                    raise urllib.error.HTTPError(
                        "https://discord.test", 429, "Rate limited", {}, None)
                channels = [{"id": first_voice, "name": "Original voice", "type": 2}]
                if state["new"]:
                    channels.extend([
                        {"id": next_voice, "name": "New voice", "type": 2},
                        {"id": next_text, "name": "New text", "type": 0},
                    ])
                return 200, channels
            raise AssertionError(path)

        with mock.patch.object(module, "_discord_request", side_effect=fake):
            before = module.discord_server_channels(guild)
            self.assertEqual(len(before["voice"]), 1)
            state["new"] = True
            self.assertEqual(len(module.discord_server_channels(guild)["voice"]), 1)
            updated = module.discord_server_channels(guild, force=True)
            self.assertEqual(len(updated["voice"]), 2)
            self.assertEqual(updated["text"][0]["name"], "New text")
            self.assertEqual(module.discord_server_channels(guild), updated)
            state["rate_limited"] = True
            stale = module.discord_server_channels(guild, force=True)
            self.assertTrue(stale["stale"])
            self.assertEqual(len(stale["voice"]), 2)
        self.assertEqual(calls.count("/guilds/" + guild + "/channels"), 3)

    def test_refresh_channel_endpoint_requires_auth_and_bypasses_cache(self):
        guild = "1307370943416500314"
        endpoint = "/api/guilds/" + guild + "/channels?refresh=1"
        self.assertEqual(self.call(endpoint)[0], 401)
        _, _, cookie = self.call(
            "/api/login", {"password": "a-local-test-password"})
        result = {"guild_id": guild, "voice": [], "text": []}
        with mock.patch.object(module, "discord_server_channels",
                               return_value=result) as channels:
            code, body, _ = self.call(endpoint, cookie=cookie.split(";", 1)[0])
        self.assertEqual(code, 200)
        self.assertEqual(body, result)
        channels.assert_called_once_with(guild, force=True)

    def test_joined_servers_endpoints_require_login(self):
        self.assertEqual(self.call("/api/guilds")[0], 401)
        self.assertEqual(self.call(
            "/api/guilds/1307370943416500314/channels")[0], 401)

    def test_compose_fallback_recovers_all_running_states(self):
        output = "\n".join([
            "tsukiai-bridge-1|Up 2 hours",
            "tsukiai-api-1|Up 1 hour",
            "tsukiai-caddy-1|Up 1 hour",
            "tsukiai-chromadb-1|Exited (1) 3 minutes ago",
            "unrelated-container|Up 2 hours",
        ])
        with mock.patch.object(module.subprocess, "run",
                               return_value=SimpleNamespace(returncode=0, stdout=output)):
            states = module._docker_fallback_states()
        self.assertEqual(states["bridge"]["state"], "running")
        self.assertEqual(states["api"]["state"], "running")
        self.assertEqual(states["caddy"]["state"], "running")
        self.assertEqual(states["chromadb"]["state"], "stopped")
        self.assertEqual(len(states), 4)

    def test_bridge_is_always_enabled_and_has_no_stop_action(self):
        compose_file = Path(__file__).resolve().parents[1] / "docker-compose.yml"
        text = compose_file.read_text(encoding="utf-8")
        self.assertIn("bridge:\n    build: ./discord-voice-bridge", text)
        self.assertIn("restart: always", text)
        self.assertNotIn("stop_bridge", module.ACTIONS)
        self.assertNotIn("start_bridge", module.ACTIONS)
        self.assertIn("restart_bridge", module.ACTIONS)

    def test_compose_ps_accepts_array_and_line_json(self):
        array = '[{"Service":"bridge","State":"running"},{"Service":"api","State":"running"}]'
        lines = '{"Service":"bridge","State":"running"}\n{"Service":"api","State":"running"}'
        self.assertEqual([row["Service"] for row in module._parse_compose_ps(array)],
                         ["bridge", "api"])
        self.assertEqual([row["Service"] for row in module._parse_compose_ps(lines)],
                         ["bridge", "api"])

    def test_password_change_hashes_only_controls_password_and_invalidates_sessions(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            auth_file = root / ".controls-auth.json"
            initial_env = "TSUKI_WEB_PASSWORD=a-local-test-password\nTSUKI_API_KEY=leave-me-alone\n"
            root.joinpath(".env").write_text(initial_env)
            module.PASSWORD = "a-local-test-password"
            module.SESSIONS["session"] = (9999999999, "csrf")
            module.remember_device("synthetic-remembered-device")
            self.assertTrue(module._trusted_records())
            with mock.patch.object(module, "AUTH_FILE", auth_file), mock.patch.object(
                module, "ROOT", root), mock.patch.object(module, "compose") as compose:
                module.change_password("a-local-test-password", "changed#password $123")
                self.assertTrue(module.verify_password("changed#password $123"))
                self.assertFalse(module.verify_password("a-local-test-password"))
                self.assertFalse(module.verify_password("incorrect-password"))
                self.assertEqual(module.load_password(), "")
            self.assertEqual(root.joinpath(".env").read_text(), initial_env)
            record = json.loads(auth_file.read_text())
            self.assertEqual(record["version"], 1)
            self.assertNotIn("changed#password $123", auth_file.read_text())
            self.assertEqual(module.PASSWORD, "")
            self.assertEqual(module.SESSIONS, {})
            self.assertEqual(module._trusted_records(), {})
            compose.assert_not_called()
            module.PASSWORD = "a-local-test-password"

    def test_password_validation_preserves_current_password(self):
        with TemporaryDirectory() as temporary:
            with mock.patch.object(module, "AUTH_FILE", Path(temporary) / "auth.json"):
                module.PASSWORD = "a-local-test-password"
                for invalid in ("short", "a" * 129, "a-local-test-password"):
                    with self.assertRaises(ValueError):
                        module.change_password("a-local-test-password", invalid)
                with self.assertRaises(PermissionError):
                    module.change_password("incorrect-password", "new-valid-password")
                self.assertTrue(module.verify_password("a-local-test-password"))

    def test_http_password_change_and_relogin(self):
        with TemporaryDirectory() as temporary:
            with mock.patch.object(module, "AUTH_FILE", Path(temporary) / "auth.json"):
                module.PASSWORD = "a-local-test-password"
                status, _, cookie = self.call("/api/login", {"password": "a-local-test-password"})
                self.assertEqual(status, 200)
                cookie = cookie.split(";", 1)[0]
                csrf = module.SESSIONS[cookie.split("=", 1)[1]][1]
                replacement = "a-new-local-password!"
                status, _, _ = self.call("/api/password", {
                    "current": "a-local-test-password", "new": replacement
                }, cookie=cookie, csrf=csrf)
                self.assertEqual(status, 200)
                self.assertEqual(self.call("/api/status", cookie=cookie)[0], 401)
                self.assertEqual(self.call("/api/login", {"password": replacement})[0], 200)
                module.PASSWORD = "a-local-test-password"
                module.SESSIONS.clear()

    def test_provider_status_only_polls_selected_runtime(self):
        fake = mock.MagicMock()
        fake.status = 200
        fake.read.return_value = b'{"status":"ready"}'
        fake.__enter__.return_value = fake
        fake.__exit__.return_value = None
        with mock.patch.object(module.urllib.request, "urlopen", return_value=fake) as urlopen:
            status = module._provider_status("colab")
        self.assertTrue(status["Colab"]["online"])
        self.assertIsNone(status["Molab"]["online"])
        self.assertEqual(urlopen.call_count, 1)
        self.assertIn("colab-tts", urlopen.call_args.args[0].full_url)

    def test_voice_controls_require_session_csrf_and_active_guild(self):
        guild = "1307370943416500314"
        with mock.patch.object(module, "read_bot_config", return_value={
                "guild_id": guild, "tts_provider": "molab"}):
            self.assertEqual(self.call("/api/voice-session")[0], 401)
            self.assertEqual(self.call("/api/voice-leave", {"guild_id": guild})[0], 401)
            _, _, cookie = self.call(
                "/api/login", {"password": "a-local-test-password"})
            cookie = cookie.split(";", 1)[0]
            csrf = module.SESSIONS[cookie.split("=", 1)[1]][1]
            self.assertEqual(self.call("/api/voice-leave", {
                "guild_id": guild}, cookie=cookie)[0], 403)
            with mock.patch.object(module, "bridge_rpc", return_value=(
                    200, {"ok": True, "left_channel_id": "1307370944255229956"})) as rpc:
                self.assertEqual(self.call("/api/voice-leave", {
                    "guild_id": "346070665184542720"}, cookie=cookie, csrf=csrf)[0], 400)
                rpc.assert_not_called()
                status, result, _ = self.call("/api/voice-leave", {
                    "guild_id": guild}, cookie=cookie, csrf=csrf)
                self.assertEqual(status, 200)
                self.assertEqual(result["left_channel_id"], "1307370944255229956")
                rpc.assert_called_once_with("/leave-voice", {"guild_id": guild})
            with mock.patch.object(module, "bridge_rpc", return_value=(
                    200, {"connected": False})) as rpc:
                self.assertEqual(self.call("/api/voice-session", cookie=cookie)[1],
                                 {"connected": False})
                rpc.assert_called_once_with("/voice-session")

    def test_focus_and_analytics_are_authenticated_validated_and_never_restart(self):
        guild = "1307370943416500314"
        user = "854185919731859496"
        self.assertEqual(self.call("/api/voice-focus")[0], 401)
        self.assertEqual(self.call("/api/voice-analytics")[0], 401)
        self.assertEqual(self.call("/api/molab-metrics")[0], 401)
        self.assertEqual(self.call("/api/voice-focus", {
            "guild_id": guild, "action": "add", "user_id": user})[0], 401)
        with mock.patch.object(module, "read_bot_config", return_value={
                "guild_id": guild, "tts_provider": "molab"}):
            _, _, cookie = self.call(
                "/api/login", {"password": "a-local-test-password"})
            cookie = cookie.split(";", 1)[0]
            csrf = module.SESSIONS[cookie.split("=", 1)[1]][1]
            self.assertEqual(self.call("/api/voice-focus", {
                "guild_id": guild, "action": "add", "user_id": user},
                cookie=cookie)[0], 403)
            for invalid in (
                {"guild_id": guild, "action": "add", "user_id": "junk"},
                {"guild_id": "346070665184542720", "action": "clear"},
                {"guild_id": guild, "action": "clear", "user_id": user},
                {"guild_id": guild, "action": "delete"},
            ):
                self.assertEqual(self.call("/api/voice-focus", invalid,
                    cookie=cookie, csrf=csrf)[0], 400)
            state = {"mode": "selected users only", "user_ids": [user], "members": []}
            with mock.patch.object(module, "bridge_rpc",
                    return_value=(200, state)) as rpc:
                self.assertEqual(self.call("/api/voice-focus", {
                    "guild_id": guild, "action": "add", "user_id": user},
                    cookie=cookie, csrf=csrf)[1], state)
                rpc.assert_called_once_with("/focus", {
                    "guild_id": guild, "action": "add", "user_id": user})
            with mock.patch.object(module, "bridge_rpc",
                    return_value=(200, state)) as rpc:
                self.assertEqual(self.call("/api/voice-focus", cookie=cookie)[1], state)
                rpc.assert_called_once_with("/focus")
            metrics = {"since": "2026-09-20T00:00:00Z",
                       "stages": {"tts": {"timed_samples": 0, "avg_ms": None}}}
            with mock.patch.object(module, "bridge_rpc",
                    return_value=(200, metrics)) as rpc:
                self.assertEqual(self.call("/api/voice-analytics", cookie=cookie)[1],
                                 metrics)
                rpc.assert_called_once_with("/analytics")

    def test_custom_tts_validates_and_tracks_playback_without_restarting(self):
        guild = "1307370943416500314"
        with mock.patch.object(module, "read_bot_config", return_value={
                "guild_id": guild, "tts_provider": "molab"}), mock.patch.object(
                module, "bridge_rpc", return_value=(200, {"ok": True})) as rpc:
            _, _, cookie = self.call(
                "/api/login", {"password": "a-local-test-password"})
            cookie = cookie.split(";", 1)[0]
            csrf = module.SESSIONS[cookie.split("=", 1)[1]][1]
            for invalid in ("", "a" * 281):
                self.assertEqual(self.call("/api/voice-tts", {
                    "guild_id": guild, "text": invalid}, cookie=cookie, csrf=csrf)[0], 400)
            self.assertEqual(self.call("/api/voice-tts", {
                "guild_id": "346070665184542720", "text": "hello"},
                cookie=cookie, csrf=csrf)[0], 400)
            self.assertEqual(self.call("/api/voice-tts", {
                "guild_id": guild, "text": "hello"}, cookie=cookie)[0], 403)
            status, result, _ = self.call("/api/voice-tts", {
                "guild_id": guild, "text": "  Hello   from   controls!  "},
                cookie=cookie, csrf=csrf)
            self.assertEqual(status, 202)
            import time
            for _ in range(100):
                _, job, _ = self.call("/api/config-jobs/" + result["job_id"],
                                      cookie=cookie)
                if job["state"] == "done":
                    break
                time.sleep(.01)
            self.assertEqual(job["state"], "done")
            rpc.assert_called_once_with("/play-tts", {
                "guild_id": guild, "text": "Hello from controls!"}, timeout=120)
            self.assertNotIn("Hello from controls!", str(job))
            with mock.patch.object(module, "compose") as compose:
                compose.assert_not_called()
            module.CONFIG_JOBS.clear()

    def test_bridge_rpc_uses_fixed_docker_exec_and_stdin(self):
        fake = SimpleNamespace(returncode=0, stdout=json.dumps({
            "status": 200, "body": {"connected": False}}))
        with TemporaryDirectory() as temporary, mock.patch.object(
                module, "ROOT", Path(temporary)), mock.patch.object(
                module.subprocess, "run", return_value=fake) as run:
            self.assertEqual(module.bridge_rpc("/voice-session")[1],
                             {"connected": False})
            args = run.call_args.args[0]
            self.assertEqual(args[:6],
                             ["docker", "compose", "exec", "-T", "bridge", "node"])
            self.assertNotIn("voice-session", str(args[:6]))
            self.assertEqual(json.loads(run.call_args.kwargs["input"])["path"],
                             "/voice-session")
            self.assertEqual(run.call_args.kwargs["timeout"], 12)
            with self.assertRaises(ValueError):
                module.bridge_rpc("/arbitrary-url", {"text": "no"})
            self.assertEqual(run.call_count, 1)


if __name__ == "__main__":
    unittest.main()
