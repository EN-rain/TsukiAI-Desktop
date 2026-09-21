"""Browser-level controls regression, fully mocked; never touches Discord/VPS."""
from __future__ import annotations

import json
import threading
import unittest
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent
GUILD = "1307370943416500314"
VOICE = "1307370944255229956"
TEXT = "1545789527556104393"


class SilentFiles(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(ROOT), **kwargs)

    def log_message(self, *args):
        pass


class BrowserUiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.http = ThreadingHTTPServer(("127.0.0.1", 0), SilentFiles)
        cls.thread = threading.Thread(target=cls.http.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f"http://127.0.0.1:{cls.http.server_port}"
        cls.playwright = sync_playwright().start()
        cls.browser = cls.playwright.chromium.launch(
            executable_path="C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
            headless=True,
        )

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()
        cls.http.shutdown()
        cls.http.server_close()
        cls.thread.join()

    def setUp(self):
        self.bot = {
            "guild_id": GUILD, "voice_channel_id": VOICE,
            "text_channel_id": "", "text_reply_mode": "mention",
            "text_voice_mode": "keywords",
            "text_voice_keywords": "voice,say it", "tts_provider": "molab",
            "llm_provider": "api", "llm_config_consistent": True,
        }
        self.inference_selected = ["groq"]
        self.inference_supported = [
            {"id": name, "model": model, "credential_configured": True,
             "selected_model": ("openai/gpt-oss-120b" if name == "groq"
                                else "" if name == "literouter" else "")}
            for name, model in (
                ("groq", "openai/gpt-oss-120b"),
                ("cerebras", "llama3.1-8b"),
                ("gemini", "gemini-2.0-flash"),
                ("github", "openai/gpt-4o-mini"),
                ("mistral", "mistral-small-latest"),
                ("literouter", "Choose from your available models"))
        ]
        self.groq_keys = [
            {"id": "a" * 64, "masked": "sk-...23hew", "calls_today": None}
        ]
        self.literouter_keys = []
        self.requests = []
        self.personality = {"base_personality": "You are Tsuki. Existing SYSTEM.",
                            "base_source": "assets/Modelfile · SYSTEM",
                            "personality_injection": "", "customized": False}
        self.voice_channels = [{"id": VOICE, "name": "General Voice"}]
        self.text_channels = [{"id": TEXT, "name": "Bot Chat"}]
        self.channel_refreshes = 0
        self.channel_rate_limited = False
        self.focus_list = []
        self.connected = True
        self.context = self.browser.new_context(viewport={"width": 1440, "height": 900})
        self.page = self.context.new_page()
        self.errors = []
        self.page.on("pageerror", lambda error: self.errors.append(str(error)))
        self.page.on("dialog", lambda dialog: dialog.accept())
        self.page.route("**/api/**", self.api)

    def tearDown(self):
        self.assertFalse(self.errors, "JavaScript errors: " + repr(self.errors))
        self.context.close()

    def respond(self, route, code, body):
        route.fulfill(status=code, content_type="application/json",
                      body=json.dumps(body))

    def api(self, route):
        req = route.request
        path = urlsplit(req.url).path
        verb = req.method
        payload = req.post_data_json if verb == "POST" else None
        self.requests.append((verb, path, payload))
        if path == "/api/session":
            return self.respond(route, 200, {"authenticated": True})
        if path == "/api/inference-providers":
            if verb == "POST":
                self.inference_selected = list(payload["providers"])
                return self.respond(route, 202, {"job_id": "inference-job", "state": "queued"})
            return self.respond(route, 200, {
                "selected": list(self.inference_selected), "multi_enabled": True,
                "legacy_override": "api", "providers": self.inference_supported,
                "usage": {"total_calls_today": None, "coverage": "unavailable"},
            })
        if path == "/api/inference-models":
            provider = urlsplit(req.url).query.split("=", 1)[-1]
            return self.respond(route, 200, {
                "provider": provider,
                "models": (
                    ["openai/gpt-oss-120b", "llama-3.3-70b-versatile"]
                    if provider == "groq"
                    else ["deepseek-v4-flash:free", "mistral-small-2603:free",
                          "some-premium:metered"]),
                "no_prompt_sent": True,
            })
        if path == "/api/inference-model":
            for entry in self.inference_supported:
                if entry["id"] == payload["provider"]:
                    entry["selected_model"] = payload["model"]
            return self.respond(route, 202, {
                "job_id": "inference-model-job", "state": "queued",
            })
        if path == "/api/inference-key":
            for entry in self.inference_supported:
                if entry["id"] == payload["provider"]:
                    entry["credential_configured"] = bool(payload.get("key"))
                    entry["credential_masked"] = (
                        payload["key"][:4] + "..." + payload["key"][-5:]
                        if payload.get("key") else "")
            return self.respond(route, 202, {
                "job_id": "inference-key-job", "state": "queued",
            })
        if path == "/api/groq-keys":
            if verb == "POST":
                if payload["action"] == "add":
                    self.groq_keys.append({
                        "id": "b" * 64, "masked": payload["key"][:4] +
                        "..." + payload["key"][-5:], "calls_today": None})
                else:
                    self.groq_keys = [key for key in self.groq_keys
                                      if key["id"] != payload["id"]]
                return self.respond(route, 202, {"job_id": "keys-job", "state": "queued"})
            return self.respond(route, 200, {
                "provider": "groq", "source": "file",
                "total_keys": len(self.groq_keys), "keys": self.groq_keys,
                "calls_today": None, "date": "2026-09-21", "timezone": "UTC",
                "coverage": "unavailable",
            })
        if path == "/api/literouter-keys":
            if verb == "POST":
                if payload["action"] == "add":
                    key = payload["key"]
                    self.literouter_keys.append({
                        "id": str(len(self.literouter_keys) + 1) * 64,
                        "masked": key[:4] + "..." + key[-5:]})
                else:
                    self.literouter_keys = [
                        entry for entry in self.literouter_keys
                        if entry["id"] != payload["id"]]
                return self.respond(route, 202, {
                    "job_id": "literouter-key-job", "state": "queued"})
            return self.respond(route, 200, {
                "provider": "literouter", "managed": True,
                "keys": self.literouter_keys,
                "total_keys": len(self.literouter_keys)})
        if path == "/api/status":
            return self.respond(route, 200, {
                "csrf": "test-csrf", "updated_at": "2026-09-20 03:00:00 UTC",
                "selected_provider": self.bot["tts_provider"], "bot": dict(self.bot),
                "llm": {"provider": self.bot["llm_provider"], "ready": None,
                        "status": "Inference not verified"},
                "providers": {
                    "Molab": {"selected": self.bot["tts_provider"] == "molab", "online": True},
                    "Colab": {"selected": self.bot["tts_provider"] == "colab", "online": True},
                },
                "services": [{"service": name, "state": "running"}
                             for name in ("bridge", "api", "caddy", "chromadb")],
                "docker_stats": {},
                "avatar": {"id": "123", "avatar_hash": "test", "username": "Tsuki",
                           "has_avatar": False},
                "system": {"uptime_seconds": 800, "load": [0.1, 0.2, 0.3],
                           "memory_total": 8 * 2**30, "memory_available": 4 * 2**30,
                           "disk_total": 100 * 2**30, "disk_used": 50 * 2**30},
            })
        if path == "/api/guilds":
            return self.respond(route, 200, {
                "guilds": [{"id": GUILD, "name": "Test Guild"}]})
        if path.endswith("/channels"):
            refresh = urlsplit(req.url).query == "refresh=1"
            if refresh:
                self.channel_refreshes += 1
            return self.respond(route, 200, {
                "guild_id": GUILD,
                "voice": list(self.voice_channels),
                "text": list(self.text_channels),
                "stale": self.channel_rate_limited and refresh,
            })
        if path == "/api/voice-focus":
            if verb == "POST":
                if payload["action"] == "add" and payload["user_id"] not in self.focus_list:
                    self.focus_list.append(payload["user_id"])
                elif payload["action"] == "remove":
                    self.focus_list = [x for x in self.focus_list if x != payload["user_id"]]
                elif payload["action"] == "clear":
                    self.focus_list.clear()
            return self.respond(route, 200, {
                "guild_id": GUILD, "channel_id": VOICE if self.connected else None,
                "mode": "selected users only" if self.focus_list else "everyone",
                "user_ids": list(self.focus_list),
                "members": [{"id": "854185919731859496", "name": "Sample listener",
                             "focused": "854185919731859496" in self.focus_list}],
            })
        if path == "/api/voice-analytics":
            return self.respond(route, 200, {
                "since": "2026-09-20T00:00:00Z",
                "stages": {
                    "stt": {"timed_samples": 2, "avg_ms": 115, "attempts": 2, "failures": 0},
                    "llm": {"timed_samples": 0, "avg_ms": None, "attempts": 0, "failures": 0},
                    "tts": {"timed_samples": 3, "avg_ms": 3500, "attempts": 4, "failures": 1},
                }, "completed_replies": 2,
                "drops": {"busy": 1, "cooldown": 0},
                "tts": {"generated_audio_seconds": 7.25,
                        "audio_cache": "not implemented"},
            })
        if path == "/api/molab-metrics":
            return self.respond(route, 200, {
                "successes": 3, "avg_generation_ms": 3400,
                "avg_queue_ms": 120, "real_time_factor": 1.2,
                "gpu_batch_calls": 2, "synthesized_utterances": 7,
                "attention_backend": "sdpa",
            })
        if path == "/api/voice-session":
            return self.respond(route, 200, {
                "guild_id": GUILD if self.connected else "",
                "channel_id": VOICE if self.connected else "",
                "configured_guild_id": GUILD, "connected": self.connected,
            })
        if path == "/api/provider-health":
            return self.respond(route, 200, {
                "selected_provider": self.bot["tts_provider"],
                "providers": {"Molab": {"selected": True, "online": True},
                              "Colab": {"selected": False, "online": None}},
                "checked_at": "03:00:00 UTC",
            })
        if path == "/api/personality":
            if verb == "POST":
                self.personality.update(payload)
                return self.respond(route, 202, {"job_id": "persona-job", "state": "queued"})
            return self.respond(route, 200, dict(self.personality))
        if path == "/api/bot-config":
            self.bot.update(payload)
            return self.respond(route, 202, {"job_id": "bot-job", "state": "queued"})
        if path == "/api/voice-tts":
            return self.respond(route, 202, {"job_id": "voice-job", "state": "queued"})
        if path.startswith("/api/config-jobs/"):
            return self.respond(route, 200, {"state": "done", "updated": ["TEXT_REPLY_MODE"]})
        if path == "/api/voice-leave":
            self.connected = False
            return self.respond(route, 200, {"ok": True, "left_channel_id": VOICE})
        if path == "/api/action":
            return self.respond(route, 200, {"ok": True, "action": payload["action"]})
        if path == "/api/logout":
            return self.respond(route, 200, {"ok": True})
        if path == "/api/avatar":
            return self.respond(route, 200, {"ok": True, "avatar": {
                "id": "123", "username": "Tsuki"}})
        if path == "/api/avatar-image":
            return route.fulfill(status=404, body="")
        return self.respond(route, 404, {"error": "Unhandled mocked API route: " + path})

    def open_panel(self):
        self.page.goto(self.base + "/", wait_until="networkidle")
        self.page.locator(".side-nav a[data-tab=discord-voice]").click()
        self.page.locator("#guild-config").wait_for(state="visible")
        self.page.wait_for_function(
            "() => document.querySelector('#voice_channel_id').value !== ''")

    def test_detect_servers_also_refreshes_new_channels_preserving_unsaved_edits(self):
        self.open_panel()
        original = self.page.locator("#voice_channel_id").input_value()
        self.page.locator('input[name="text_reply_mode"][value="any"]').check()
        self.page.locator("#text_channel_id").select_option(TEXT)
        self.page.locator("#text_voice_keywords").fill("voice,custom")
        self.assertEqual(self.page.locator("#guild-config-state").inner_text(), "UNSAVED")

        voice_new = "1545789527556104395"
        text_new = "1545789527556104396"
        self.voice_channels.append({"id": voice_new, "name": "New Voice"})
        self.text_channels.append({"id": text_new, "name": "New Text"})
        self.page.locator("#reload-guilds").click()
        self.page.wait_for_function(
            "() => [...document.querySelector('#voice_channel_id').options]"
            ".some(option => option.textContent.includes('New Voice'))")
        self.assertEqual(self.channel_refreshes, 1)
        self.assertEqual(self.page.locator("#voice_channel_id").input_value(), original)
        self.assertEqual(self.page.locator("#text_channel_id").input_value(), TEXT)
        self.assertEqual(
            self.page.locator('input[name="text_reply_mode"]:checked').input_value(), "any")
        self.assertEqual(self.page.locator("#text_voice_keywords").input_value(), "voice,custom")
        self.assertEqual(self.page.locator("#guild-config-state").inner_text(), "UNSAVED")
        self.assertIn("2 voice and 2 text channels", self.page.locator("#guild-config-hint").inner_text())
        self.assertFalse(any(path == "/api/bot-config" for _, path, _ in self.requests))
        self.page.locator("#voice_channel_id").select_option(voice_new)
        self.page.locator("#text_channel_id").select_option(text_new)
        self.assertEqual(self.page.locator("#guild-config-state").inner_text(), "UNSAVED")

    def test_rate_limited_discovery_discloses_stale_channel_list(self):
        self.open_panel()
        self.channel_rate_limited = True
        self.page.locator("#reload-guilds").click()
        self.page.wait_for_function(
            "() => document.querySelector('#guild-config-hint').textContent.includes('rate-limited')")
        self.assertEqual(self.channel_refreshes, 1)
        self.assertEqual(self.page.locator("#voice_channel_id").input_value(), VOICE)
        self.assertIn("cached channels", self.page.locator("#toast-text").inner_text())

    def test_radio_settings_persist_scope_and_keyword_visibility(self):
        self.open_panel()
        self.assertEqual(self.page.locator('input[name="text_reply_mode"]:checked').input_value(), "mention")
        self.assertEqual(self.page.locator('input[name="text_voice_mode"]:checked').input_value(), "keywords")
        self.assertTrue(self.page.locator("#voice-keywords-row").is_visible())
        self.page.get_by_label("All messages in voice-channel chat").check()
        self.page.locator('input[name="text_voice_mode"][value="off"]').check()
        self.assertFalse(self.page.locator("#voice-keywords-row").is_visible())
        self.assertEqual(self.page.locator("#guild-config-state").inner_text(), "UNSAVED")
        self.page.locator("#text_channel_id").select_option(TEXT)
        self.assertEqual(self.page.locator("#reply-any-label").inner_text(), "All messages in both chats")
        self.page.locator("#bot-settings button[type=submit]").click()
        self.page.wait_for_function(
            "() => document.querySelector('#guild-config-state').textContent === 'ACTIVE SERVER'")
        saved = [data for verb, path, data in self.requests
                 if verb == "POST" and path == "/api/bot-config"]
        self.assertEqual(len(saved), 1)
        self.assertEqual(saved[0]["text_reply_mode"], "any")
        self.assertEqual(saved[0]["text_voice_mode"], "off")
        self.assertEqual(saved[0]["text_channel_id"], TEXT)
        self.assertEqual(self.page.locator('input[name="text_reply_mode"]:checked').input_value(), "any")
        self.assertEqual(self.page.locator('input[name="text_voice_mode"]:checked').input_value(), "off")
        self.assertFalse(self.page.locator("#voice-keywords-row").is_visible())

    def test_provider_live_tts_leave_restart_and_logout(self):
        self.open_panel()
        self.page.locator("#custom-tts-text").fill("Testing the selected voice")
        self.page.locator("#custom-tts-form button[type=submit]").click()
        self.page.wait_for_function(
            "() => document.querySelector('#custom-tts-text').value === ''")
        spoken = [data for verb, path, data in self.requests if path == "/api/voice-tts"]
        self.assertEqual(spoken[0]["text"], "Testing the selected voice")
        self.page.locator("#leave-voice").click()
        self.page.wait_for_function(
            "() => document.querySelector('#leave-voice').disabled")
        self.assertFalse(self.connected)
        self.assertEqual(self.bot["voice_channel_id"], VOICE)
        self.page.locator(".side-nav a[data-tab=discord-voice]").click()
        self.page.locator('[data-action="restart_bridge"]').click()
        self.page.wait_for_function(
            "() => document.querySelector('#bridge-summary').textContent === 'Running'")
        self.assertTrue(any(path == "/api/action" for _, path, _ in self.requests))
        self.page.locator("#logout").click()
        self.page.locator("#login").wait_for(state="visible")
        self.assertTrue(any(path == "/api/logout" for _, path, _ in self.requests))

    def test_provider_switch_and_off_disable_live_speech(self):
        self.open_panel()
        self.page.locator(".side-nav a[data-tab=discord-voice]").click()
        self.page.locator('#provider-form label.provider-option:has(input[value="off"])').click()
        self.page.wait_for_function(
            "() => document.querySelector('#voice-summary').textContent.includes('Off')")
        self.assertTrue(self.page.locator("#custom-tts-form button[type=submit]").is_disabled())
        self.assertEqual(self.bot["tts_provider"], "off")
        self.page.locator('#provider-form label.provider-option:has(input[value="colab"])').click()
        self.page.wait_for_function(
            "() => document.querySelector('#voice-summary').textContent.includes('Colab')")
        self.assertFalse(self.page.locator("#custom-tts-form button[type=submit]").is_disabled())
        self.assertEqual(self.bot["tts_provider"], "colab")
        settings = [data for verb, path, data in self.requests
                    if verb == "POST" and path == "/api/bot-config"]
        self.assertEqual(settings, [{"tts_provider": "off"}, {"tts_provider": "colab"}])

    def test_llm_api_provider_list_is_independent_of_tts_and_adds_supported_provider(self):
        self.open_panel()
        self.page.locator(".side-nav a[data-tab=ai]").click()
        self.assertEqual(self.page.locator('input[name="tts_provider"]:checked').input_value(),
                         "molab")
        self.page.locator('#inference-provider-settings summary').click()
        self.page.locator('#inference-provider-list').get_by_text('GROQ').wait_for()
        self.assertEqual(self.page.locator('#inference-provider-list [role=listitem]').count(), 1)
        self.page.locator('#inference-add-provider').select_option("cerebras")
        self.page.locator('#inference-add').click()
        self.assertEqual(self.page.locator('#inference-provider-list [role=listitem]').count(), 2)
        self.page.locator('#inference-save').click()
        self.page.wait_for_function(
            "() => document.querySelector('#inference-save').disabled")
        self.assertEqual(self.inference_selected, ["groq", "cerebras"])
        self.assertEqual(self.bot["tts_provider"], "molab")
        self.assertEqual(self.page.locator('input[name="tts_provider"]:checked').input_value(),
                         "molab")
        self.assertEqual([data for method, path, data in self.requests if
                          method == "POST" and path == "/api/inference-providers"],
                         [{"providers": ["groq", "cerebras"]}])
        self.assertFalse(any(path == "/api/bot-config" for _, path, _ in self.requests))

    def test_groq_masked_scrollable_roster_adds_key_without_exposing_it(self):
        self.open_panel()
        self.page.locator(".side-nav a[data-tab=ai]").click()
        self.page.locator("#groq-key-list").get_by_text("sk-...23hew").wait_for()
        self.assertEqual(self.page.locator("#groq-total-calls").inner_text(), "Unavailable")
        self.assertIn("not yet verifiable",
                      self.page.locator("#groq-calls-coverage").inner_text())
        self.assertEqual(
            self.page.locator("#groq-key-list").evaluate(
                "element => getComputedStyle(element).overflowY"), "auto")
        key = "gsk_" + "A" * 31 + "56789"
        self.page.locator("#groq-new-key").fill(key)
        self.page.locator('#groq-add-form button[type=submit]').click()
        self.page.locator("#groq-key-list").get_by_text("gsk_...56789").wait_for()
        self.assertNotIn(key, self.page.locator("body").inner_text())
        self.assertEqual(self.page.locator("#groq-new-key").input_value(), "")
        self.assertEqual([data for method, path, data in self.requests if
                          method == "POST" and path == "/api/groq-keys"],
                         [{"action": "add", "key": key}])

    def test_groq_and_literouter_models_are_independently_selectable_from_lists_only(self):
        self.open_panel()
        self.page.locator(".side-nav a[data-tab=ai]").click()
        self.page.locator("#groq-model-load").click()
        self.page.locator("#groq-model-select option[value='llama-3.3-70b-versatile']").wait_for(state="attached")
        self.assertEqual(self.page.locator("#groq-model-select").input_value(),
                         "openai/gpt-oss-120b")
        self.page.locator("#groq-model-select").select_option("llama-3.3-70b-versatile")
        self.page.locator("#groq-model-save").click()
        self.page.locator("#groq-model-current").get_by_text(
            "Current model: llama-3.3-70b-versatile").wait_for()
        self.page.locator("#literouter-provider-details summary").click()
        self.page.locator("#literouter-model-load").click()
        self.page.locator("#literouter-model-select option[value='deepseek-v4-flash:free']").wait_for(state="attached")
        self.assertEqual(self.page.locator("#literouter-model-select option[value='some-premium:metered']").count(), 0)
        self.assertIn("free model IDs", self.page.locator("#literouter-model-status").inner_text())
        self.page.locator("#literouter-model-select").select_option("deepseek-v4-flash:free")
        self.page.locator("#literouter-model-save").click()
        self.page.locator("#literouter-model-current").get_by_text(
            "Saved model (inactive): deepseek-v4-flash:free").wait_for()
        self.page.reload(wait_until="networkidle")
        self.page.locator("#groq-model-current").get_by_text(
            "Current model: llama-3.3-70b-versatile").wait_for()
        self.page.locator("#literouter-provider-details summary").click()
        self.page.locator("#literouter-model-current").get_by_text(
            "Saved model (inactive): deepseek-v4-flash:free").wait_for()
        changes = [body for verb, path, body in self.requests
                   if verb == "POST" and path == "/api/inference-model"]
        self.assertEqual(changes, [
            {"provider": "groq", "model": "llama-3.3-70b-versatile"},
            {"provider": "literouter", "model": "deepseek-v4-flash:free"},
        ])
        self.assertEqual(self.bot["tts_provider"], "molab")
        self.assertFalse(any(path == "/api/chat" or "/chat/completions" in path
                             for _, path, _ in self.requests))
        self.assertEqual(len([1 for verb, path, _ in self.requests if
                              verb == "GET" and path == "/api/inference-models"]), 2)

    def test_personality_edit_saves_vps_source_without_changing_provider_settings(self):
        self.open_panel()
        self.page.locator(".side-nav a[data-tab=ai]").click()
        editor = self.page.locator("#personality-editor")
        editor.wait_for(state="visible")
        self.assertEqual(editor.input_value(), "You are Tsuki. Existing SYSTEM.")
        self.assertNotIn("The existing SYSTEM text below",
                         self.page.locator("#personality").inner_text())
        self.assertEqual(self.page.locator("#personality-status").inner_text(),
                         "Saved on VPS.")
        replacement = "You are Tsuki. Adjusted personality, same model."
        editor.fill(replacement)
        self.assertTrue(self.page.locator("#personality-save").is_enabled())
        self.page.locator("#personality-save").click()
        self.page.wait_for_function(
            "() => document.querySelector('#personality-status').textContent === 'Saved on VPS.'")
        self.assertEqual(self.personality["base_personality"], replacement)
        self.assertEqual(self.inference_selected, ["groq"])
        self.assertEqual(self.bot["tts_provider"], "molab")
        writes = [payload for verb, path, payload in self.requests
                  if verb == "POST" and path == "/api/personality"]
        self.assertEqual(writes, [{"base_personality": replacement}])
        self.page.reload(wait_until="networkidle")
        self.page.locator(".side-nav a[data-tab=ai]").click()
        self.page.wait_for_function(
            "() => document.querySelector('#personality-editor').value === " +
            json.dumps(replacement))
        self.assertFalse(self.page.locator("#personality-save").is_enabled())

    def test_key_sections_and_model_change_follow_daily_usage(self):
        self.open_panel()
        self.page.locator('.side-nav a[data-tab=ai]').click()
        self.page.locator('#literouter-provider-details summary').click()
        self.page.locator('#literouter-key-preview').get_by_text(
            '0 LiteRouter key(s) configured.').wait_for()
        structure = self.page.evaluate("""() => {
          const llm = document.querySelector('#llm');
          const groq = llm.querySelector('.groq-provider-section');
          const lite = llm.querySelector('#literouter-provider-details');
          const order = [llm.querySelector('#groq-provider-details'), lite]
            .map(el => [...llm.children].indexOf(el));
          return {
            order, sectionCount: llm.querySelectorAll('.provider-key-section').length,
            topHeading: llm.querySelector('#llm-title').textContent,
            routingLast: llm.lastElementChild.id === 'inference-provider-settings',
            routingClosed: !llm.lastElementChild.open,
            standalone: llm.querySelectorAll('.usage-model-section, .inference-model-grid').length,
            modelHeading: llm.textContent.includes('Model selection'),
            groqModel: groq.contains(llm.querySelector('#groq-model-select')),
            groqUsage: groq.contains(llm.querySelector('#groq-total-calls')),
            liteModel: lite.contains(llm.querySelector('#literouter-model-select')),
            liteKey: lite.contains(llm.querySelector('#literouter-key-form')),
            groqOrder: [
              groq.querySelector('#groq-model-select'),
              groq.querySelector('#groq-key-list'),
              groq.querySelector('#groq-add-form'),
              groq.querySelector('#groq-total-calls')
            ].map(el => [...groq.querySelectorAll('*')].indexOf(el))
          };
        }""")
        self.assertEqual(structure['order'], sorted(structure['order']))
        self.assertEqual(structure['sectionCount'], 1)
        self.assertEqual(structure['topHeading'], 'Groq API keys')
        self.assertTrue(structure['routingLast'])
        self.assertTrue(structure['routingClosed'])
        self.assertEqual(structure['standalone'], 0)
        self.assertFalse(structure['modelHeading'])
        self.assertTrue(all(structure[k] for k in
                            ('groqModel', 'groqUsage', 'liteModel', 'liteKey')))
        self.assertEqual(structure['groqOrder'], sorted(structure['groqOrder']))
        self.assertTrue(self.page.locator('#literouter-key-form').is_hidden())
        self.assertEqual(self.page.locator('#literouter-key-toggle').get_attribute(
            'aria-expanded'), 'false')
        self.page.locator('#literouter-key-toggle').click()
        self.assertTrue(self.page.locator('#literouter-key-form').is_visible())
        self.assertEqual(self.page.locator('#literouter-key-toggle').get_attribute(
            'aria-expanded'), 'true')
        self.page.locator('#literouter-model-key').fill('temporary-private-key')
        self.page.locator('#literouter-key-toggle').click()
        self.assertTrue(self.page.locator('#literouter-key-form').is_hidden())
        self.assertEqual(self.page.locator('#literouter-model-key').input_value(), '')
        self.assertFalse(any(path == '/api/literouter-keys' and verb == 'POST'
                             for verb, path, _ in self.requests))
        self.assertEqual(self.page.locator(
            '.groq-provider-section #groq-model-select').count(), 1)
        self.assertEqual(self.page.locator(
            '#literouter-provider-details #literouter-model-select').count(), 1)

    def test_provider_fold_headers_and_toggles_fit_every_screen_width(self):
        self.open_panel()
        for width, height in ((320, 568), (360, 640), (390, 844),
                              (600, 700), (900, 700), (1440, 900),
                              (1920, 1080)):
            self.page.set_viewport_size({"width": width, "height": height})
            self.page.goto(self.base + "/#ai", wait_until="networkidle")
            for name in ("groq", "literouter"):
                details = self.page.locator("#" + name + "-provider-details")
                toggle = self.page.locator("#" + name + "-provider-toggle")
                bounds = toggle.bounding_box()
                self.assertIsNotNone(bounds, (name, width))
                self.assertGreaterEqual(bounds["x"], -1, (name, width, bounds))
                self.assertLessEqual(bounds["x"] + bounds["width"], width + 1,
                                     (name, width, bounds))
                self.assertTrue(toggle.is_visible(), (name, width))
                self.assertTrue(details.locator("summary").is_visible(),
                                (name, width))
            self.assertLessEqual(
                self.page.evaluate("document.documentElement.scrollWidth"),
                self.page.evaluate("document.documentElement.clientWidth") + 1,
                width)
            lite = self.page.locator("#literouter-provider-details")
            lite.locator("summary").click()
            self.assertTrue(self.page.locator("#literouter-model-select").is_visible())
            lite.locator("summary").click()
            self.assertFalse(self.page.locator("#literouter-model-select").is_visible())
            self.assertTrue(self.page.locator("#literouter-provider-toggle").is_visible())

    def test_collapsible_provider_toggles_are_exclusive_and_persisted(self):
        self.inference_supported[-1]["selected_model"] = "deepseek-v4-flash:free"
        self.literouter_keys = [{"id": "1" * 64, "masked": "sk-l...ecret"}]
        self.open_panel()
        self.page.locator(".side-nav a[data-tab=ai]").click()
        groq = self.page.locator("#groq-provider-details")
        lite = self.page.locator("#literouter-provider-details")
        groq_toggle = self.page.locator("#groq-provider-toggle")
        lite_toggle = self.page.locator("#literouter-provider-toggle")
        self.assertTrue(groq.evaluate("el => el.open"))
        self.assertFalse(lite.evaluate("el => el.open"))
        self.assertEqual(groq_toggle.get_attribute("aria-pressed"), "true")
        self.assertEqual(lite_toggle.get_attribute("aria-pressed"), "false")
        self.assertTrue(lite_toggle.is_visible())
        self.assertFalse(self.page.locator("#literouter-model-select").is_visible())
        lite_toggle.click()
        self.page.wait_for_function(
            "() => document.querySelector('#literouter-provider-toggle').getAttribute('aria-pressed') === 'true'")
        self.assertEqual(self.inference_selected, ["literouter"])
        self.assertEqual(groq_toggle.get_attribute("aria-pressed"), "false")
        self.assertEqual(self.page.locator("#literouter-provider-state").inner_text(), "Active")
        self.assertTrue(lite_toggle.is_visible())
        self.assertFalse(lite.evaluate("el => el.open"))
        lite_toggle.click()
        self.assertEqual(self.inference_selected, ["literouter"])
        self.assertIn("At least one API inference provider",
                      self.page.locator("#toast-text").inner_text())
        groq.locator("summary").click()
        self.assertFalse(groq.evaluate("el => el.open"))
        self.assertTrue(groq_toggle.is_visible())
        groq_toggle.click()
        self.page.wait_for_function(
            "() => document.querySelector('#groq-provider-toggle').getAttribute('aria-pressed') === 'true'")
        self.assertEqual(self.inference_selected, ["groq"])
        self.assertEqual(lite_toggle.get_attribute("aria-pressed"), "false")
        self.assertFalse(groq.evaluate("el => el.open"))
        self.page.locator("#inference-provider-settings summary").click()
        self.page.locator("#inference-add-provider").select_option("literouter")
        self.page.locator("#inference-add").click()
        self.assertEqual(
            [x.inner_text().splitlines()[0] for x in
             self.page.locator("#inference-provider-list [role=listitem]").all()],
            ["1. LITEROUTER"])
        self.page.locator("#inference-save").click()
        self.page.wait_for_function(
            "() => document.querySelector('#literouter-provider-toggle').getAttribute('aria-pressed') === 'true'")
        self.assertEqual(self.inference_selected, ["literouter"])
        posts = [body for verb, path, body in self.requests
                 if verb == "POST" and path == "/api/inference-providers"]
        self.assertEqual(posts, [
            {"providers": ["literouter"]}, {"providers": ["groq"]},
            {"providers": ["literouter"]}])
        self.assertEqual(self.bot["tts_provider"], "molab")

    def test_literouter_key_roster_can_add_multiple_remove_one_without_exposure(self):
        self.open_panel()
        self.page.locator(".side-nav a[data-tab=ai]").click()
        self.page.locator('#literouter-provider-details summary').click()
        self.page.locator("#literouter-key-preview").get_by_text(
            "0 LiteRouter key(s) configured.").wait_for()
        self.assertEqual(self.page.locator("#literouter-key-list").evaluate(
            "element => getComputedStyle(element).overflowY"), "auto")
        first = "sk-literouter-test-secret"
        second = "sk-literouter-second-secret"
        for key in (first, second):
            self.page.locator("#literouter-key-toggle").click()
            self.assertTrue(self.page.locator("#literouter-key-form").is_visible())
            self.page.locator("#literouter-model-key").fill(key)
            self.page.locator("#literouter-key-form button[type=submit]").click()
            self.page.locator("#literouter-key-list").get_by_text(
                key[:4] + "..." + key[-5:]).wait_for()
            self.page.wait_for_function(
                "() => document.querySelector('#literouter-model-key').value === ''")
            self.assertNotIn(key, self.page.locator("body").inner_text())
        self.assertEqual(self.page.locator("#literouter-key-list [role=listitem]").count(), 2)
        self.page.locator("#literouter-key-list [role=listitem]").first.get_by_role(
            "button", name="Remove LiteRouter key").click()
        self.page.wait_for_function(
            "() => document.querySelectorAll('#literouter-key-list [role=listitem]').length === 1")
        self.assertEqual(self.page.locator("#literouter-key-list").inner_text().count(
            "sk-l...ecret"), 1)
        self.assertEqual(self.inference_selected, ["groq"])
        self.assertEqual(self.bot["tts_provider"], "molab")
        self.assertEqual([data for method, path, data in self.requests if
                          method == "POST" and path == "/api/literouter-keys"],
                         [{"action": "add", "key": first},
                          {"action": "add", "key": second},
                          {"action": "remove", "id": "1" * 64}])

    def test_sidebar_tabs_keep_controls_and_use_live_values(self):
        self.open_panel()
        self.page.locator('input[name="text_reply_mode"][value="any"]').check()
        self.page.locator(".side-nav a[data-tab=overview]").click()
        self.page.wait_for_function(
            "() => document.querySelector('#page-title').textContent === 'Overview'")
        self.assertEqual(self.page.locator("#page-title").inner_text(), "Overview")
        self.assertFalse(self.page.locator(".overview-bridge").is_visible())
        self.assertEqual(self.page.locator("#overview-health-services").inner_text(), "4 / 4 running")
        self.assertEqual(self.page.locator("#guilds-summary").inner_text(), "1 joined")
        self.assertNotIn("42 ms", self.page.locator("body").inner_text())
        for tab, title in [
            ("discord-voice", "Discord & Voice"),
            ("ai", "AI & Personality"),
            ("vps", "VPS Overview"), ("account", "Account"),
        ]:
            self.page.locator('.side-nav a[data-tab="' + tab + '"]').click()
            self.page.wait_for_function(
                "(title) => document.querySelector('#page-title').textContent === title",
                arg=title)
            self.assertEqual(
                self.page.locator('.side-nav [aria-current="page"]').count(), 1)
            self.assertEqual(self.page.locator("#dashboard").get_attribute("data-active-tab"), tab)
        self.assertEqual(
            self.page.locator('input[name="text_reply_mode"]:checked').input_value(), "any")

    def test_discord_voice_is_one_page_and_old_links_remain_usable(self):
        self.open_panel()
        self.assertEqual(self.page.locator(".side-nav a[data-tab]").count(), 5)
        self.assertEqual(self.page.locator(".side-nav [data-tab=discord-voice]").count(), 1)
        for anchor, expected in (
            ("voice", "#provider-form"), ("llm", "#llm-title"), ("discord", "#bot"),
            ("discord-settings", "#bot-settings"),
            ("context-awareness", "#context-awareness-title"),
            ("personality", "#personality-editor"),
        ):
            self.page.goto(self.base + "/#" + anchor, wait_until="networkidle")
            self.page.locator(expected).wait_for(state="visible")
            self.assertEqual(self.page.locator("#dashboard").get_attribute("data-active-tab"),
                             "ai" if anchor in ("llm", "context-awareness", "personality") else "discord-voice")
            self.assertEqual(self.page.locator(".side-nav [aria-current=page]").count(), 1)
            nav = ".discord-voice-jump[data-view=\"ai\"]" if anchor in (
                "llm", "context-awareness", "personality") else \
                ".discord-voice-jump[data-view=\"discord-voice\"]"
            self.assertTrue(self.page.locator(nav).is_visible())
        self.assertIn("DISCORD ONLY", self.page.locator("#context-awareness").inner_text())
        self.assertIn("Apply awareness settings",
                      self.page.locator("#context-awareness").inner_text())
        self.page.goto(self.base + "/#discord-voice", wait_until="networkidle")
        self.assertTrue(self.page.locator("#voice").is_visible())
        self.assertTrue(self.page.locator("#discord").is_visible())
        self.assertTrue(self.page.locator("#discord-settings").is_visible())
        self.assertFalse(self.page.locator("#context-awareness").is_visible())
        self.page.goto(self.base + "/#ai", wait_until="networkidle")
        self.assertTrue(self.page.locator("#context-awareness").is_visible())
        self.assertTrue(self.page.locator("#personality").is_visible())

    def test_direct_tabs_and_mobile_menu_escape(self):
        self.page.goto(self.base + "/#vps", wait_until="networkidle")
        self.page.locator("#vps").wait_for(state="visible")
        self.assertEqual(self.page.locator("#page-title").inner_text(), "VPS Overview")
        self.assertEqual(self.page.locator('#dashboard').get_attribute("data-active-tab"), "vps")
        self.assertEqual(self.page.locator(".sidebar").evaluate(
            "(el) => Math.round(el.getBoundingClientRect().width)"), 246)
        self.assertEqual(self.page.evaluate(
            "getComputedStyle(document.body).backgroundColor"), "rgb(10, 10, 10)")
        self.page.set_viewport_size({"width": 390, "height": 844})
        self.page.locator("#menu-toggle").click()
        self.assertEqual(self.page.locator("#menu-toggle").get_attribute("aria-expanded"), "true")
        self.page.keyboard.press("Escape")
        self.assertEqual(self.page.locator("#menu-toggle").get_attribute("aria-expanded"), "false")
        self.page.locator("#menu-toggle").click()
        self.page.locator(".side-nav a[data-tab=vps]").click()
        self.assertEqual(self.page.locator("#menu-toggle").get_attribute("aria-expanded"), "false")

    def test_compact_layout_and_live_controls_appear_before_saved_settings(self):
        self.open_panel()
        for width in (1440, 1280, 1024, 390, 320):
            self.page.set_viewport_size({"width": width, "height": 850})
            self.page.goto(self.base + "/#overview", wait_until="networkidle")
            overview = self.page.locator(".overview-workspace").bounding_box()
            self.assertIsNotNone(overview)
            self.assertLessEqual(overview["height"], 1100 if width <= 390 else 650)
            if width >= 1280:
                self.assertLessEqual(overview["height"], 570)
            self.page.goto(self.base + "/#discord-settings", wait_until="networkidle")
            self.page.locator("#guild-config").wait_for(state="visible")
            panel = self.page.locator("#discord-settings").bounding_box()
            live = self.page.locator(".live-voice-controls").bounding_box()
            settings = self.page.locator("#bot-settings").bounding_box()
            self.assertIsNotNone(panel)
            self.assertIsNotNone(live)
            self.assertIsNotNone(settings)
            self.assertEqual(self.page.locator("#live-voice-title").inner_text(), "Live voice")
            self.assertLessEqual(
                panel["height"], 1200 if width >= 1280 else
                (1700 if width > 780 else 2300))
            if width >= 1280:
                self.assertLessEqual(abs(live["y"] - settings["y"]), 2)
                self.assertLess(settings["x"] + settings["width"], live["x"] + 1)
            else:
                self.assertLess(live["y"] + live["height"], settings["y"] + 1)
            self.page.evaluate("window.scrollTo(0, 0)")
            bounds = self.page.evaluate("""() => ({
              width: document.documentElement.clientWidth,
              scrollWidth: document.documentElement.scrollWidth,
              top: document.querySelector('.page-head').getBoundingClientRect().top,
              bar: document.querySelector('.topbar').getBoundingClientRect().bottom
            })""")
            self.assertLessEqual(bounds["scrollWidth"], bounds["width"] + 1)
            self.assertGreaterEqual(bounds["top"], bounds["bar"] - 1)

    def test_voice_and_account_layouts_are_usable_at_all_sizes(self):
        self.open_panel()
        for width in (1440, 1280, 1024, 390, 320):
            self.page.set_viewport_size({"width": width, "height": 850})
            self.page.goto(self.base + "/#voice", wait_until="networkidle")
            self.page.locator("#voice").wait_for(state="visible")
            route = self.page.locator("#voice").bounding_box()
            self.assertIsNotNone(route)
            self.assertTrue(self.page.locator("#provider-form").is_visible())
            self.assertTrue(self.page.locator("#voice-health-text").is_visible())
            self.assertTrue(self.page.locator("#provider-feedback").is_visible())

            self.page.goto(self.base + "/#account", wait_until="networkidle")
            avatar = self.page.locator(".account-identity").bounding_box()
            security = self.page.locator(".account-security").bounding_box()
            self.assertTrue(self.page.locator("#avatar-editor-image").is_visible())
            self.assertLessEqual(avatar["y"] + avatar["height"], security["y"] + 2)
            session = self.page.locator(".account-session").bounding_box()
            if width > 780:
                self.assertLessEqual(abs(avatar["y"] - session["y"]), 2)
                self.assertLessEqual(avatar["x"] + avatar["width"], session["x"] + 2)
            else:
                self.assertLessEqual(security["y"] + security["height"], session["y"] + 2)
            security_details = self.page.locator(".account-security")
            if security_details.get_attribute("open") is None:
                security_details.locator("summary").click()
            self.assertTrue(self.page.locator("#password-form").is_visible())
            identity_details = self.page.locator(".account-identity")
            if identity_details.get_attribute("open") is None:
                identity_details.locator("summary").click()
            self.assertTrue(self.page.locator("#avatar-file").count() == 1)
            self.assertTrue(self.page.locator("#avatar-editor-name").is_visible())
            self.assertLessEqual(
                self.page.evaluate("document.documentElement.scrollWidth"),
                self.page.evaluate("document.documentElement.clientWidth") + 1,
            )

    def test_moonlit_board_navigation_search_and_real_actions(self):
        self.open_panel()
        self.page.goto(self.base + "/#overview", wait_until="networkidle")
        self.assertEqual(self.page.locator(".side-nav a[data-tab]").count(), 5)
        self.assertEqual(self.page.locator("#page-title").inner_text(), "Overview")
        self.page.locator("#page-search").fill("custom tts")
        self.assertTrue(self.page.locator("#search-results").is_visible())
        self.assertEqual(self.page.locator("#search-results button").first.inner_text(),
                         "Discord & Voice")
        self.page.locator("#page-search").press("Enter")
        self.page.wait_for_function(
            "() => document.querySelector('#dashboard').dataset.activeTab === 'discord-voice'")
        self.assertFalse(self.page.locator("#search-results").is_visible())
        self.assertEqual(self.page.locator("#page-search").input_value(), "")
        self.page.keyboard.press("Control+k")
        self.assertTrue(self.page.locator("#page-search").evaluate(
            "el => document.activeElement === el"))
        self.page.locator("#page-search").fill("password")
        self.page.locator("#page-search").press("Enter")
        self.page.wait_for_function(
            "() => document.querySelector('#dashboard').dataset.activeTab === 'account'")
        self.assertTrue(self.page.locator(".account-session").is_visible())
        self.assertEqual(self.page.locator(".secondary-grid > *").count(), 2)
        self.assertEqual(self.page.locator(".secondary-grid .panel").count(), 3)
        before = sum(path == "/api/status" for _, path, _ in self.requests)
        self.page.locator("#session-refresh").click()
        self.assertGreaterEqual(sum(path == "/api/status" for _, path, _ in self.requests),
                                before + 1)
        self.page.locator("#session-logout").click()
        self.page.locator("#login").wait_for(state="visible")
        self.assertTrue(any(path == "/api/logout" for _, path, _ in self.requests))


    def test_strict_monochrome_theme_without_glows(self):
        import re
        css = (ROOT / "style.css").read_text(encoding="utf-8")
        colors = re.findall(r"(?<![\w-])#([0-9a-fA-F]{3,8})(?![\w-])", css)
        self.assertGreater(len(colors), 200)
        for original in colors:
            value = "".join(ch * 2 for ch in original) if len(original) in (3, 4) else original
            if value[:6].lower() in {"39ff14", "ffe600", "ff3030"}:
                continue  # Status indicator dots/icons only.
            self.assertEqual(value[:2], value[2:4], original)
            self.assertEqual(value[2:4], value[4:6], original)
        self.assertNotIn("channel-picker", css)
        self.assertNotIn("channelPickers", (ROOT / "app.js").read_text(encoding="utf-8"))
        self.open_panel()
        self.assertEqual(self.page.evaluate(
            "getComputedStyle(document.body).backgroundColor"), "rgb(10, 10, 10)")
        for tab in ("overview", "discord-voice", "ai", "vps", "account"):
            self.page.locator('.side-nav a[data-tab="' + tab + '"]').click()
            selected = self.page.locator('.side-nav a[data-tab="' + tab + '"]')
            self.assertEqual(selected.evaluate(
                "el => getComputedStyle(el).color"), "rgb(255, 255, 255)")
            self.assertEqual(selected.evaluate(
                "el => getComputedStyle(el).textShadow"), "none")

    def test_only_status_indicators_have_green_yellow_red(self):
        self.open_panel()
        green, yellow, red = ("rgb(57, 255, 20)", "rgb(255, 230, 0)",
                              "rgb(255, 48, 48)")
        dot = self.page.locator("#header-health .private-dot")
        self.assertEqual(dot.evaluate(
            "el => getComputedStyle(el).backgroundColor"), green)
        self.page.locator("#header-health").evaluate(
            "el => el.dataset.state = 'unknown'")
        self.assertEqual(dot.evaluate(
            "el => getComputedStyle(el).backgroundColor"), yellow)
        self.assertEqual(self.page.locator("#header-health").evaluate(
            "el => getComputedStyle(el).color"), "rgb(255, 255, 255)")
        self.page.goto(self.base + "/#voice", wait_until="networkidle")
        indicator = self.page.locator("#voice-health-indicator")
        for status, expected in (("ready", green), ("pending", yellow),
                                 ("disconnected", red), ("disabled", "rgb(188, 188, 188)")):
            indicator.evaluate(
                "(el, value) => el.className = 'voice-health-indicator ' + value", status)
            self.assertEqual(indicator.evaluate(
                "el => getComputedStyle(el).color"), expected, status)
        for status, expected in (("ready", green), ("pending", yellow),
                                 ("offline", red), ("off", "rgb(188, 188, 188)")):
            self.page.locator(".voice-panel .route-feedback").evaluate(
                "(el, value) => el.dataset.state = value", status)
            self.assertEqual(self.page.locator(".voice-panel .route-feedback-dot").evaluate(
                "el => getComputedStyle(el).backgroundColor"), expected, status)
            self.assertEqual(self.page.locator("#provider-feedback").evaluate(
                "el => getComputedStyle(el).color"), "rgb(255, 255, 255)")
        self.page.goto(self.base + "/#vps", wait_until="networkidle")
        row = self.page.locator(".service-row").first
        dot = row.locator(".state-dot")
        for status, expected in (("running", green), ("unknown", yellow),
                                 ("stopped", red)):
            row.evaluate("(el, value) => el.className = 'service-row ' + value", status)
            self.assertEqual(dot.evaluate(
                "el => getComputedStyle(el).borderTopColor"), expected)
            self.assertEqual(row.locator(".service-state").evaluate(
                "el => getComputedStyle(el).color"), "rgb(255, 255, 255)")
        self.page.goto(self.base + "/#overview", wait_until="networkidle")
        icon = self.page.locator("#attention-mark")
        for tone, expected in (("good", green), ("warn", yellow), ("bad", red)):
            self.page.locator("#attention").evaluate(
                "(el, value) => el.dataset.tone = value", tone)
            self.assertEqual(icon.evaluate(
                "el => getComputedStyle(el).color"), expected)
            self.assertEqual(self.page.locator("#attention-title").evaluate(
                "el => getComputedStyle(el).color"), "rgb(255, 255, 255)")

    def test_focus_controls_and_measured_analytics_use_live_endpoints(self):
        self.open_panel()
        self.page.locator("#voice-focus-disclosure summary").click()
        self.page.locator("#voice-focus-status").wait_for(state="visible")
        self.page.wait_for_function(
            "() => document.querySelector('#voice-focus-status').textContent.includes('everyone')")
        user = "854185919731859496"
        self.page.locator("#voice-focus-user").select_option(user)
        self.page.locator("#focus-add").click()
        self.page.wait_for_function(
            "() => document.querySelector('#voice-focus-status').textContent.includes('1 selected')")
        self.assertEqual(self.focus_list, [user])
        self.assertIn("Sample listener", self.page.locator(".voice-focus-entry").inner_text())
        self.page.locator(".voice-focus-entry button").click()
        self.page.wait_for_function(
            "() => document.querySelector('#voice-focus-status').textContent.includes('everyone')")
        self.assertEqual(self.focus_list, [])
        self.page.locator("#voice-focus-id").fill(user)
        self.page.locator("#focus-add").click()
        self.page.wait_for_function(
            "() => document.querySelector('#voice-focus-status').textContent.includes('1 selected')")
        self.page.locator("#focus-clear").click()
        self.page.wait_for_function(
            "() => document.querySelector('#voice-focus-status').textContent.includes('everyone')")
        self.assertEqual(self.focus_list, [])
        actions = [(verb, path) for verb, path, _ in self.requests]
        self.assertIn(("POST", "/api/voice-focus"), actions)
        self.page.goto(self.base + "/#voice", wait_until="networkidle")
        self.page.locator(".voice-analytics summary").click()
        self.page.locator("#reload-analytics").click()
        self.page.wait_for_function(
            "() => document.querySelector('#voice-analytics-results').textContent.includes('3500 ms')")
        text = self.page.locator("#voice-analytics-results").inner_text()
        self.assertIn("3500 ms (3 measured)", text)
        self.assertIn("No timed samples", text)
        self.assertIn("3400 ms (3 samples)", text)
        self.assertIn("True GPU batch calls", text)
        self.assertIn("Generated utterances", text)
        self.assertIn("7", text)
        self.assertIn("not implemented", text)
        self.assertIn("1.2", text)
        self.assertIn("sdpa", self.page.locator("#voice-analytics-source").inner_text())

    def test_native_channel_selects_and_saved_settings_still_work(self):
        self.open_panel()
        self.page.goto(self.base + "/#discord-settings", wait_until="networkidle")
        self.page.locator("#guild-config").wait_for(state="visible")
        self.assertEqual(self.page.locator(".channel-picker").count(), 0)
        voice = self.page.locator("#voice_channel_id")
        text = self.page.locator("#text_channel_id")
        self.assertEqual(voice.evaluate("el => getComputedStyle(el).appearance"), "auto")
        self.assertTrue(voice.is_visible())
        self.assertTrue(text.is_visible())
        self.assertEqual(voice.input_value(), VOICE)
        text.select_option(TEXT)
        self.assertEqual(text.input_value(), TEXT)
        self.assertEqual(self.page.locator("#guild-config-state").inner_text(), "UNSAVED")
        self.assertFalse(voice.evaluate("el => el.parentElement.classList.contains('channel-picker')"))
        self.assertEqual(text.evaluate(
            "el => getComputedStyle(el).color"), "rgb(255, 255, 255)")

    def test_flat_hover_and_status_text(self):
        self.open_panel()
        self.page.goto(self.base + "/#discord-settings", wait_until="networkidle")
        self.page.locator("#guild-config").wait_for(state="visible")
        selected = self.page.locator(".guild-choice.selected").first
        selected.hover()
        self.assertEqual(selected.evaluate(
            "el => getComputedStyle(el).borderTopColor"), "rgb(255, 255, 255)")
        self.assertEqual(selected.evaluate(
            "el => getComputedStyle(el).textShadow"), "none")
        self.page.goto(self.base + "/#overview", wait_until="networkidle")
        for selector in ("#bridge-summary", "#voice-summary", "#attention"):
            self.assertEqual(self.page.locator(selector).evaluate(
                "el => getComputedStyle(el).textShadow"), "none")

    def test_mobile_and_desktop_do_not_overflow(self):
        self.open_panel()
        for width in (1440, 390, 320):
            self.page.set_viewport_size({"width": width, "height": 850})
            self.page.wait_for_timeout(100)
            metrics = self.page.evaluate("""() => ({
              body: document.documentElement.scrollWidth,
              viewport: document.documentElement.clientWidth,
              radios: [...document.querySelectorAll('.choice-option input[type=radio]')].length
            })""")
            self.assertLessEqual(metrics["body"], metrics["viewport"] + 1, metrics)
            self.assertEqual(metrics["radios"], 4)
        self.page.set_viewport_size({"width": 390, "height": 844})
        self.page.locator("#menu-toggle").click()
        self.assertEqual(self.page.locator("#menu-toggle").get_attribute("aria-expanded"), "true")
        self.page.locator(".side-nav a[data-tab=overview]").click()
        self.page.wait_for_function(
            "() => document.querySelector('#menu-toggle').getAttribute('aria-expanded') === 'false'")
        self.assertEqual(self.page.locator("#page-title").inner_text(), "Overview")


    def test_compact_cards_fluid_ai_and_tablet_navigation(self):
        self.open_panel()
        self.page.set_viewport_size({"width": 1920, "height": 1080})
        self.page.goto(self.base + "/#ai", wait_until="networkidle")
        personality = self.page.locator("#personality").bounding_box()
        editor = self.page.locator("#personality-editor").bounding_box()
        self.assertGreaterEqual(personality["width"], 1250)
        self.assertLessEqual(personality["width"], 1441)
        self.assertLessEqual(editor["height"], 291)
        models = self.page.locator(".inference-model-row").all()
        self.assertEqual(len(models), 2)
        self.page.locator("#literouter-provider-details summary").click()
        self.assertLess(models[0].bounding_box()["y"], models[1].bounding_box()["y"])
        self.assertLessEqual(abs(models[0].bounding_box()["x"] -
                                 models[1].bounding_box()["x"]), 2)

        self.page.goto(self.base + "/#overview", wait_until="networkidle")
        links = self.page.locator(".action-links a").all()
        self.assertEqual(len(links), 5)
        tops = [round(link.bounding_box()["y"]) for link in links]
        self.assertEqual(len(set(tops)), 1, tops)
        self.assertLessEqual(max(link.bounding_box()["height"] for link in links), 85)

        self.page.goto(self.base + "/#discord-voice", wait_until="networkidle")
        self.assertLessEqual(self.page.locator(".provider-option").first.bounding_box()["height"], 100)
        self.page.set_viewport_size({"width": 900, "height": 600})
        self.assertTrue(self.page.locator("#menu-toggle").is_visible())
        self.assertGreaterEqual(self.page.locator(".shell").bounding_box()["width"], 880)
        self.assertEqual(self.page.locator(".topbar").bounding_box()["height"], 60)
        self.page.locator("#menu-toggle").click()
        self.assertEqual(self.page.locator("#menu-toggle").get_attribute("aria-expanded"), "true")
        self.page.keyboard.press("Escape")
        self.assertEqual(self.page.locator("#menu-toggle").get_attribute("aria-expanded"), "false")

    def test_screenshot_sections_use_balanced_cards_not_stretched_grid_rows(self):
        self.open_panel()
        for width in (1440, 1920):
            self.page.set_viewport_size({"width": width, "height": 900})
            self.page.goto(self.base + "/#discord-voice", wait_until="networkidle")
            self.page.locator("#guild-config").wait_for(state="visible")
            identity = self.page.locator(".discord-panel .discord-identity").bounding_box()
            runtime = self.page.locator(".discord-panel .runtime-information").bounding_box()
            actions = self.page.locator(".discord-panel .runtime-actions").bounding_box()
            reply = self.page.locator("#text_reply_mode").bounding_box()
            voice = self.page.locator("#text_voice_mode").bounding_box()
            self.assertLess(identity["height"], 150, identity)
            self.assertLess(runtime["height"], 240, runtime)
            self.assertLess(actions["height"], 90, actions)
            self.assertLess(identity["x"], runtime["x"])
            self.assertGreaterEqual(actions["y"], max(
                identity["y"] + identity["height"],
                runtime["y"] + runtime["height"]) - 2)
            self.assertLessEqual(abs(reply["y"] - voice["y"]), 2)
            self.assertLess(reply["x"], voice["x"])

            self.page.goto(self.base + "/#ai", wait_until="networkidle")
            self.page.locator("#literouter-provider-details summary").click()
            groq = self.page.locator("[data-inference-model=groq]").bounding_box()
            lite = self.page.locator("[data-inference-model=literouter]").bounding_box()
            self.assertLess(groq["y"] + groq["height"], lite["y"] + 2)
            self.assertLessEqual(abs(groq["x"] - lite["x"]), 2)

            self.page.goto(self.base + "/#account", wait_until="networkidle")
            self.assertEqual(self.page.locator("#account .secondary-heading").count(), 0)
            avatar = self.page.locator(".account-identity").bounding_box()
            security = self.page.locator(".account-security").bounding_box()
            session = self.page.locator(".account-session").bounding_box()
            self.assertLess(avatar["height"], 125, avatar)
            self.assertLess(security["height"], 125, security)
            self.assertLessEqual(avatar["y"] + avatar["height"], security["y"] + 2)
            self.assertLess(avatar["x"] + avatar["width"], session["x"] + 2)

    def test_login_layout_reflows_across_screen_sizes(self):
        self.page.goto(self.base + "/", wait_until="networkidle")
        self.page.evaluate("""() => {
          document.querySelector('#dashboard').hidden = true;
          document.querySelector('#login').hidden = false;
        }""")
        for width, height in ((320, 568), (390, 844), (600, 700),
                              (800, 600), (1024, 768), (1440, 900),
                              (1920, 1080)):
            self.page.set_viewport_size({"width": width, "height": height})
            metrics = self.page.evaluate("""() => {
              const login = document.querySelector('#login').getBoundingClientRect();
              const form = document.querySelector('#login-form').getBoundingClientRect();
              const input = document.querySelector('#password').getBoundingClientRect();
              return {
                scroll: document.documentElement.scrollWidth,
                viewport: document.documentElement.clientWidth,
                loginHeight: login.height, formHeight: form.height,
                formLeft: form.left, formRight: form.right,
                inputLeft: input.left, inputRight: input.right
              };
            }""")
            self.assertLessEqual(metrics["scroll"], metrics["viewport"] + 1,
                                 (width, height, metrics))
            self.assertGreaterEqual(metrics["formLeft"], -1, metrics)
            self.assertLessEqual(metrics["formRight"], width + 1, metrics)
            self.assertGreaterEqual(metrics["inputLeft"], -1, metrics)
            self.assertLessEqual(metrics["inputRight"], width + 1, metrics)
            if height <= 600:
                self.assertLessEqual(metrics["loginHeight"], max(height, metrics["formHeight"] + 240),
                                     (width, height, metrics))

    def test_all_dashboard_tabs_reflow_without_horizontal_overflow(self):
        self.open_panel()
        for width, height in ((320, 568), (360, 640), (390, 844),
                              (600, 700), (780, 720), (800, 600),
                              (900, 700), (1024, 768), (1280, 720),
                              (1440, 900), (1920, 1080), (2560, 1440)):
            self.page.set_viewport_size({"width": width, "height": height})
            for tab in ("overview", "discord-voice", "ai", "vps", "account"):
                self.page.goto(self.base + "/#" + tab, wait_until="networkidle")
                metrics = self.page.evaluate("""() => ({
                  viewport: document.documentElement.clientWidth,
                  scroll: document.documentElement.scrollWidth,
                  shell: document.querySelector('.shell').getBoundingClientRect().width,
                  activeTab: document.querySelector('#dashboard').dataset.activeTab,
                  clipped: [...document.querySelectorAll('#dashboard input, #dashboard select, #dashboard textarea, #dashboard button')]
                    .filter(el => el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden')
                    .filter(el => { const r = el.getBoundingClientRect();
                      return r.width > 0 && (r.left < -1 || r.right > innerWidth + 1); })
                    .slice(0, 3).map(el => el.id || el.className || el.tagName)
                })""")
                self.assertEqual(metrics["activeTab"], tab)
                self.assertLessEqual(metrics["scroll"], metrics["viewport"] + 1,
                                     (width, height, tab, metrics))
                self.assertFalse(metrics["clipped"], (width, height, tab, metrics))


if __name__ == "__main__":
    unittest.main()
