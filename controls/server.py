"""Local-only TsukiAI VPS admin panel; no shell or raw Docker API is exposed."""
from __future__ import annotations

import base64
import datetime
import hashlib
import hmac
import json
import os
import re
import secrets
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path("/opt/tsukiai")
HOST = "controls.tsukihime.dpdns.org"
ACTIONS = {
    "restart_bridge": ("up", "-d", "--no-deps", "--force-recreate", "bridge"),
    "restart_api": ("restart", "api"),
    "restart_caddy": ("restart", "caddy"),
    "restart_chromadb": ("restart", "chromadb"),
}
SERVICES = ("bridge", "api", "caddy", "chromadb")
PROVIDERS = {
    "molab": "https://molab-tts.tsukihime.dpdns.org/health",
    "colab": "https://colab-tts.tsukihime.dpdns.org/health",
}
GROQ_KEY_PATTERN = re.compile(r"(?:gsk_|sk[-_])[A-Za-z0-9_-]{16,252}\Z")
GROQ_KEY_LIMIT = 128
LITEROUTER_KEY_PATTERN = re.compile(r"[A-Za-z0-9._-]{8,512}\Z")
LITEROUTER_KEY_LIMIT = 128
INFERENCE_PROVIDERS = ("groq", "cerebras", "gemini", "github", "mistral",
                       "literouter")
INFERENCE_KEY_ENV = {
    "groq": "TSUKI_GROQ_API_KEY",
    "cerebras": "TSUKI_CEREBRAS_API_KEY",
    "gemini": "TSUKI_GEMINI_API_KEY",
    "github": "TSUKI_GITHUB_API_KEY",
    "mistral": "TSUKI_MISTRAL_API_KEY",
    "literouter": "TSUKI_LITEROUTER_API_KEY",
}
INFERENCE_MODEL_ENV = {
    "groq": "TSUKI_GROQ_MODEL",
    "literouter": "TSUKI_LITEROUTER_MODEL",
}
INFERENCE_MODELS_URL = {
    "groq": "https://api.groq.com/openai/v1/models",
    "literouter": "https://api.literouter.com/v1/models",
}
MODEL_ID_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.:/-]{0,127}\Z")
BOT_FIELDS = {
    "guild_id": "GUILD_ID",
    "voice_channel_id": "VOICE_CHANNEL_ID",
    "text_channel_id": "TEXT_CHANNEL_ID",
    "text_reply_mode": "TEXT_REPLY_MODE",
    "text_voice_mode": "TEXT_VOICE_MODE",
    "text_voice_keywords": "TEXT_VOICE_KEYWORDS",
    "voice_engagement_mode": "VOICE_ENGAGEMENT_MODE",
    "chat_engagement_mode": "CHAT_ENGAGEMENT_MODE",
    "engagement_aliases": "ENGAGEMENT_ALIASES",
    "engagement_followup_ms": "ENGAGEMENT_FOLLOWUP_MS",
    "classifier_model": "ENGAGEMENT_CLASSIFIER_MODEL",
    "tts_provider": "TSUKI_TTS_PROVIDER",
    "personality_injection": "TSUKI_PERSONALITY_INJECTION_B64",
    "base_personality": "__MODELFILE_PERSONALITY__",
}
BOT_ENV_FIELDS = {environment: field for field, environment in BOT_FIELDS.items()
                  if field not in ("personality_injection", "base_personality")}
# Keep the dashboard default aligned with discord-voice-bridge/text-voice-policy.js.
DEFAULT_TEXT_VOICE_KEYWORDS = "voice,say it,say this,say that,speak,talk,sing,read it"
SESSIONS: dict[str, tuple[float, str]] = {}
ATTEMPTS: dict[str, list[float]] = {}
LOCK = threading.Lock()
CONFIG_LOCK = threading.Lock()
CONFIG_JOB_LOCK = threading.Lock()
CONFIG_JOBS: dict[str, dict] = {}
PASSWORD = ""
AUTH_FILE = ROOT / ".controls-auth.json"
TRUSTED_DEVICES_FILE = ROOT / ".controls-trusted-devices.json"
TRUSTED_DEVICE_TTL = 30 * 24 * 60 * 60
TRUSTED_DEVICE_LIMIT = 12
AUTH_ITERATIONS = 600_000
GUILD_CACHE = {"checked_at": 0.0, "items": []}
CHANNEL_CACHE = {}
DISCORD_CACHE_LOCK = threading.Lock()
DISCORD_CACHE_SECONDS = 60
IDENTITY_CACHE = {"checked_at": 0.0, "value": None}
IDENTITY_CACHE_LOCK = threading.Lock()
# The controls process can reach the bridge's container-only loopback via
# a fixed docker exec command. No Docker API, token, arbitrary URL or shell
# argument is exposed to the browser. Speech text travels over stdin only.
BRIDGE_RPC_JS = """
const fs = require('node:fs');
(async () => {
  const input = JSON.parse(fs.readFileSync(0, 'utf8'));
  const port = Number.parseInt(process.env.BRIDGE_HTTP_PORT || '3001', 10);
  if (!Number.isInteger(port) || port < 1 || port > 65535 ||
      !['/voice-session', '/leave-voice', '/play-tts', '/focus', '/analytics'].includes(input.path)) {
    throw new Error('Invalid bridge request');
  }
  const response = await fetch('http://127.0.0.1:' + port + input.path, {
    method: input.method,
    headers: {'Content-Type': 'application/json'},
    ...(input.method === 'POST' ? {body: JSON.stringify(input.body)} : {}),
    signal: AbortSignal.timeout(115000),
  });
  const body = await response.json();
  process.stdout.write(JSON.stringify({status: response.status, body}));
})().catch(() => {process.exitCode = 1;});
"""


def _unquote(value: str) -> str:
    value = value.strip()
    if value[:1] in ("'", '"') and value[-1:] == value[:1]:
        return value[1:-1]
    return value


def _env_value(path: Path, name: str) -> str:
    rows = path.read_text(encoding="utf-8-sig").splitlines()
    values = [row.split("=", 1)[1] for row in rows if row.startswith(name + "=")]
    if len(values) != 1:
        return ""
    return _unquote(values[0])


def load_password() -> str:
    if AUTH_FILE.exists():
        return ""  # Salted controls hash supersedes the legacy web password.
    value = os.environ.get("TSUKI_CONTROLS_PASSWORD", "")
    if not value:
        value = _env_value(ROOT / ".env", "TSUKI_WEB_PASSWORD")
    if len(value) < 12:
        raise RuntimeError("Controls password must be at least 12 characters")
    return value


def compose(*args: str, timeout: int = 20):
    # ProtectHome=yes hides ~/.docker/config.json from this systemd service.
    # Give the Docker CLI a private, empty config directory outside HOME so
    # it can discover the system-installed Compose plugin. No login tokens or
    # user Docker settings are copied into this directory.
    docker_config = ROOT / ".docker-cli-controls"
    docker_config.mkdir(mode=0o700, parents=True, exist_ok=True)
    return subprocess.run(["docker", "compose", *args], cwd=ROOT,
                          capture_output=True, text=True, timeout=timeout,
                          env={**os.environ, "DOCKER_CONFIG": str(docker_config)})


def bridge_rpc(path: str, body: dict | None = None, timeout: int = 12):
    if path not in ("/voice-session", "/leave-voice", "/play-tts", "/focus", "/analytics"):
        raise ValueError("Unsupported bridge operation")
    if path in ("/voice-session", "/analytics") and body is not None:
        raise ValueError("Status must be read-only")
    if path not in ("/voice-session", "/analytics", "/focus") and not isinstance(body, dict):
        raise ValueError("Voice command requires a JSON body")
    docker_config = ROOT / ".docker-cli-controls"
    docker_config.mkdir(mode=0o700, parents=True, exist_ok=True)
    result = subprocess.run(
        ["docker", "compose", "exec", "-T", "bridge", "node", "-e", BRIDGE_RPC_JS],
        cwd=ROOT, capture_output=True, text=True, timeout=timeout,
        input=json.dumps({"path": path, "method": "GET" if body is None else "POST",
                          "body": body}),
        env={**os.environ, "DOCKER_CONFIG": str(docker_config)},
    )
    if result.returncode != 0 or len(result.stdout) > 16384:
        raise RuntimeError("Bridge voice control unavailable")
    try:
        reply = json.loads(result.stdout)
        if not isinstance(reply, dict) or not isinstance(reply.get("body"), dict):
            raise ValueError("Invalid bridge response")
        if not isinstance(reply.get("status"), int) or not 100 <= reply["status"] <= 599:
            raise ValueError("Invalid bridge status")
        return reply["status"], reply["body"]
    except (ValueError, TypeError) as error:
        raise RuntimeError("Bridge voice control unavailable") from error


def checked_voice_command(values, speech=False):
    if not isinstance(values, dict) or set(values) != (
            {"guild_id", "text"} if speech else {"guild_id"}):
        raise ValueError("Invalid voice command")
    guild_id = values["guild_id"]
    if not isinstance(guild_id, str) or not re.fullmatch(r"[0-9]{17,20}", guild_id):
        raise ValueError("Invalid Discord server")
    if guild_id != read_bot_config().get("guild_id"):
        raise ValueError("Voice controls are available only for the active server")
    if not speech:
        return {"guild_id": guild_id}
    raw_text = values["text"]
    if not isinstance(raw_text, str):
        raise ValueError("Speech must be text")
    spoken = re.sub(r"\s+", " ", raw_text).strip()
    if not spoken or len(spoken) > 280:
        raise ValueError("Enter 1–280 characters for custom TTS")
    if read_bot_config().get("tts_provider") == "off":
        raise ValueError("Select Molab or Colab before speaking")
    return {"guild_id": guild_id, "text": spoken}


def checked_focus_command(values):
    if not isinstance(values, dict):
        raise ValueError('Invalid focus command')
    action = values.get('action')
    keys = {'guild_id', 'action'} if action == 'clear' else {'guild_id', 'action', 'user_id'}
    if set(values) != keys or action not in ('add', 'remove', 'clear'):
        raise ValueError('Invalid focus command')
    guild = values['guild_id']
    if not isinstance(guild, str) or not re.fullmatch(r'[0-9]{17,20}', guild):
        raise ValueError('Invalid server ID')
    if guild != read_bot_config().get('guild_id'):
        raise ValueError('Focus is only available for the active server')
    if action != 'clear':
        user_id = values['user_id']
        if not isinstance(user_id, str) or not re.fullmatch(r'[0-9]{17,20}', user_id):
            raise ValueError('Enter a valid Discord user ID')
    return dict(values)


def _run_voice_tts_job(job_id: str, payload: dict):
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id]["state"] = "running"
    try:
        code, result = bridge_rpc("/play-tts", payload, timeout=120)
        if code != 200 or not result.get("ok"):
            raise RuntimeError("Could not play speech in the active voice channel")
        outcome = {"state": "done", "detail": "Speech playback completed"}
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        print("[controls] voice_tts_failed type=" + type(error).__name__, flush=True)
        outcome = {"state": "failed", "error":
                   "Voice playback failed. Check the selected notebook and live voice connection."}
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id].update(outcome)
        CONFIG_JOBS[job_id]["finished_at"] = time.time()


def begin_voice_tts_job(values: dict) -> str | None:
    payload = checked_voice_command(values, speech=True)
    with CONFIG_JOB_LOCK:
        if any(job["state"] in ("queued", "running")
               for job in CONFIG_JOBS.values()):
            return None
        for key, job in list(CONFIG_JOBS.items()):
            if time.time() - job["created_at"] > 600:
                CONFIG_JOBS.pop(key, None)
        job_id = secrets.token_urlsafe(18)
        CONFIG_JOBS[job_id] = {"state": "queued", "created_at": time.time()}
    threading.Thread(target=_run_voice_tts_job, args=(job_id, payload),
                     daemon=True, name="tsuki-voice-tts").start()
    return job_id


def checked_bot_config(values):
    if not isinstance(values, dict) or not values or any(
            key not in BOT_FIELDS for key in values):
        raise ValueError("Unsupported bot setting")
    result = {}
    for key, value in values.items():
        if not isinstance(value, str):
            raise ValueError("Bot setting must be text")
        value = value.strip().lower() if key in {
            "text_reply_mode", "text_voice_mode", "tts_provider",
            "voice_engagement_mode", "chat_engagement_mode"
        } else value.strip()
        if key.endswith("_id"):
            if key == "text_channel_id" and not value:
                result[BOT_FIELDS[key]] = ""
                continue
            if not re.fullmatch(r"[0-9]{17,20}", value):
                raise ValueError("Channel and guild IDs must contain 17–20 digits")
        elif key == "text_reply_mode" and value not in ("any", "mention"):
            raise ValueError("Invalid text reply mode")
        elif key == "text_voice_mode" and value not in ("keywords", "off"):
            raise ValueError("Invalid text voice mode")
        elif key == "text_voice_keywords":
            terms = [term.strip().lower() for term in value.split(",")]
            if (len(value) > 240 or any(ord(char) < 32 for char in value) or
                    not 1 <= len(terms) <= 16 or
                    any(not 2 <= len(term) <= 40 or
                        any(ord(char) < 32 for char in term) or "=" in term
                        for term in terms)):
                raise ValueError("Specify 1–16 comma-separated keywords (2–40 characters each)")
            value = ",".join(dict.fromkeys(terms))
        elif key in ("voice_engagement_mode", "chat_engagement_mode"):
            if value not in ("wake_only", "context_aware", "proactive"):
                raise ValueError("Invalid conversation-awareness mode")
            if value == "proactive" and not values.get("classifier_model", _env_value(
                    ROOT / "discord-voice-bridge" / ".env",
                    "ENGAGEMENT_CLASSIFIER_MODEL")):
                raise ValueError("Proactive mode requires a configured classifier model")
        elif key == "engagement_followup_ms":
            if not re.fullmatch(r"(?:0|[1-9][0-9]{0,4})", value) or int(value) > 30000:
                raise ValueError("Follow-up duration must be 0–30000 ms")
        elif key == "engagement_aliases":
            names = [name.strip().lower() for name in value.split(",")]
            if (len(value) > 240 or not 1 <= len(names) <= 8 or
                    any(not 2 <= len(name) <= 30 or not
                        re.fullmatch(r"[\w -]+", name, re.UNICODE) for name in names)):
                raise ValueError("Specify 1–8 bot names (2–30 characters each)")
            value = ",".join(dict.fromkeys(names))
        elif key == "classifier_model":
            if value and not re.fullmatch(r"[\w./:-]{1,100}", value, re.ASCII):
                raise ValueError("Classifier model must be an exact Groq model ID (up to 100 characters)")
        elif key == "tts_provider" and value not in ("molab", "colab", "off"):
            raise ValueError("TTS provider must be Molab, Colab, or Off")
        elif key == "base_personality":
            if (not value or len(value) > 4000 or '"""' in value or
                    any((ord(char) < 32 and char not in "\r\n\t") or
                        ord(char) == 127 for char in value)):
                raise ValueError("Existing Modelfile personality must contain 1–4000 characters, without triple quotes or control characters")
        elif key == "personality_injection":
            if (len(value) > 4000 or
                    any(ord(char) < 32 and char not in "\r\n\t" for char in value) or
                    "\x7f" in value):
                raise ValueError("Personality instructions must be 0–4000 characters without control characters")
            value = base64.b64encode(value.encode("utf-8")).decode("ascii")
        result[BOT_FIELDS[key]] = value
    if result.get("ENGAGEMENT_CLASSIFIER_MODEL") == "":
        for mode in ("voice_engagement_mode", "chat_engagement_mode"):
            effective = values.get(mode, _env_value(
                ROOT / "discord-voice-bridge" / ".env", BOT_FIELDS[mode]))
            if effective == "proactive":
                raise ValueError("Choose a non-proactive mode before disabling its classifier")
    return result


def write_private(path: Path, body: bytes):
    temporary = path.with_name(path.name + ".controls-" + secrets.token_hex(6))
    try:
        temporary.write_bytes(body)
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _replace_env_value(path: Path, name: str, value: str) -> bytes:
    if "\n" in value or "\r" in value:
        raise ValueError("Invalid value")
    original = path.read_bytes()
    rows = original.decode("utf-8-sig").splitlines()
    updated, found = [], False
    for row in rows:
        if row.startswith(name + "="):
            if found:
                raise ValueError("Duplicate setting")
            updated.append(name + "=" + value)
            found = True
        else:
            updated.append(row)
    if not found:
        updated.append(name + "=" + value)
    write_private(path, ("\n".join(updated) + "\n").encode())
    return original


def _private_backup(name: str, body: bytes) -> Path:
    backup = ROOT / ".deploy-backups" / "controls-config"
    backup.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(backup, 0o700)
    path = backup / (name + "-" + time.strftime("%Y%m%dT%H%M%S") +
                     "-" + secrets.token_hex(3) + ".env")
    path.write_bytes(body)
    os.chmod(path, 0o600)
    return path


def verify_bot_guild_config(changed: dict, original: bytes):
    if not any(key in changed for key in
               ("GUILD_ID", "VOICE_CHANNEL_ID", "TEXT_CHANNEL_ID")):
        return
    present = {}
    for line in original.decode("utf-8-sig").splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            if key in ("GUILD_ID", "VOICE_CHANNEL_ID", "TEXT_CHANNEL_ID"):
                present[key] = _unquote(value)
    guild = changed.get("GUILD_ID", present.get("GUILD_ID", ""))
    voice = changed.get("VOICE_CHANNEL_ID", present.get("VOICE_CHANNEL_ID", ""))
    text = changed.get("TEXT_CHANNEL_ID", present.get("TEXT_CHANNEL_ID", ""))
    if not all(re.fullmatch(r"[0-9]{17,20}", value) for value in
               (guild, voice)):
        raise ValueError("Select a server and voice channel")
    if text and not re.fullmatch(r"[0-9]{17,20}", text):
        raise ValueError("Invalid optional text channel")
    channels = discord_server_channels(guild)
    if channels["guild_id"] != guild:
        raise ValueError("Discord channel list belongs to another server")
    if voice not in {ch["id"] for ch in channels["voice"]}:
        raise ValueError("Voice channel is not available in this joined server")
    if text and text not in {ch["id"] for ch in channels["text"]}:
        raise ValueError("Text channel is not available in this joined server")


def _env_rows(original: bytes) -> dict[str, str]:
    current = {}
    for row in original.decode("utf-8-sig").splitlines():
        if "=" in row:
            key, value = row.split("=", 1)
            if key in current:
                raise ValueError("Duplicate setting in VPS configuration")
            current[key] = _unquote(value)
    return current


def _env_with_changes(original: bytes, changes: dict[str, str]) -> bytes:
    found, updated = set(), []
    for row in original.decode("utf-8-sig").splitlines():
        key = row.split("=", 1)[0]
        if key in changes:
            if key in found:
                raise ValueError("Duplicate setting in VPS configuration")
            updated.append(key + "=" + changes[key])
            found.add(key)
        else:
            updated.append(row)
    for key in changes:
        if key not in found:
            updated.append(key + "=" + changes[key])
    return ("\n".join(updated) + "\n").encode()


MODELFILE_SYSTEM = re.compile(
    r'(?ms)^SYSTEM[ \t]+"""\r?\n(?P<body>.*?)\r?\n"""[ \t]*(?=\r?\n|\Z)'
)


def _modelfile_personality(original: bytes) -> tuple[str, tuple[int, int]]:
    """Find the one real SYSTEM block, not a guessed or separately invented persona."""
    document = original.decode("utf-8")
    matches = list(MODELFILE_SYSTEM.finditer(document))
    if len(matches) != 1:
        raise ValueError("Expected exactly one triple-quoted Modelfile SYSTEM block")
    content = matches[0].group("body").strip().replace("\r\n", "\n")
    if not content or len(content) > 4000:
        raise ValueError("Existing Modelfile personality exceeds the supported editor size")
    return content, matches[0].span("body")


def _modelfile_with_personality(original: bytes, personality: str) -> bytes:
    _, (start, end) = _modelfile_personality(original)
    newline = "\r\n" if b"\r\n" in original else "\n"
    body = personality.strip().replace("\r\n", "\n").replace("\r", "\n")
    body = body.replace("\n", newline)
    document = original.decode("utf-8")
    return (document[:start] + body + document[end:]).encode("utf-8")


def _recreate(service: str):
    result = compose("up", "-d", "--no-deps", "--force-recreate", service,
                     timeout=90)
    if result.returncode == 125:
        time.sleep(2)
        result = compose("up", "-d", "--no-deps", "--force-recreate",
                         service, timeout=90)
    if result.returncode != 0:
        # Never log Docker stdout/stderr: it may include private environment data.
        print("[controls] " + service + "_recreate_failed exit=" +
              str(result.returncode), flush=True)
        raise RuntimeError("Docker could not recreate " + service + " (exit " +
                           str(result.returncode) + ")")


def _groq_key_path() -> Path:
    """Use the host-side file mounted into BOTH API and bridge by Compose."""
    root_env = ROOT / ".env"
    configured = (_env_value(root_env, "GROQ_KEYS_HOST_PATH")
                  if root_env.exists() else "")
    path = Path(configured) if configured else Path("discord-voice-bridge/groq.keys")
    if not path.is_absolute():
        path = ROOT / path
    # Avoid writing through symlinks or turning a directory mount into a file.
    if path.is_symlink() or (path.exists() and not path.is_file()):
        raise ValueError("Configured Groq key file must be a regular file")
    return path


def _parse_groq_keys(raw: str) -> list[str]:
    """Match the existing Groq pools' newline/comma and assignment handling."""
    result = []
    for chunk in re.split(r"[,\r\n]", raw):
        value = chunk.strip()
        if not value or value.startswith("#"):
            continue
        assignment = re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*\s*=\s*(.*)", value)
        if assignment:
            value = assignment.group(1).strip()
        value = value.strip().strip("\"'")
        if value.lower().startswith("bearer "):
            value = value[7:].strip()
        if value and value not in result:
            result.append(value)
    return result


def _groq_keys() -> tuple[Path, bytes | None, list[str], str]:
    path = _groq_key_path()
    original = path.read_bytes() if path.exists() else None
    from_file = (_parse_groq_keys(original.decode("utf-8-sig"))
                 if original is not None else [])
    if from_file:
        return path, original, from_file, "file"
    # File-first fallback is the same as the runtime. Move existing inline
    # keys into the file when adding another key so none silently disappear.
    root_env = ROOT / ".env"
    bridge_env = ROOT / "discord-voice-bridge" / ".env"
    root_inline = (_env_value(root_env, "TSUKI_GROQ_API_KEYS")
                   if root_env.exists() else "")
    root_single = (_env_value(root_env, "TSUKI_GROQ_API_KEY")
                   if root_env.exists() else "")
    bridge_inline = (_env_value(bridge_env, "GROQ_API_KEYS")
                     if bridge_env.exists() else "")
    legacy = (_env_value(bridge_env, "GROQ_API_KEY")
              if bridge_env.exists() else "")
    return path, original, _parse_groq_keys(
        root_inline + "\n" + root_single + "\n" +
        bridge_inline + "\n" + legacy), "inline" if (
            root_inline or root_single or bridge_inline or legacy) else "empty"


def _key_identity(key: str) -> str:
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def _literouter_keys() -> tuple[list[str], bool]:
    """Resolve the *actual* C# LiteRouter pool without exposing credentials.

    An explicit provider-owned file takes precedence in the runtime. The
    controls cannot rewrite a container-only path, so refuse mutations while
    it is configured instead of claiming to update keys that are ignored.
    """
    settings = _env_rows((ROOT / ".env").read_bytes())
    externally_managed = bool(settings.get("TSUKI_LITEROUTER_API_KEYS_FILE"))
    values = _parse_groq_keys(
        settings.get("TSUKI_LITEROUTER_API_KEYS", "") + "\n" +
        settings.get("TSUKI_LITEROUTER_API_KEY", ""))
    return values, externally_managed


def read_literouter_keys() -> dict:
    keys, external = _literouter_keys()
    if external:
        # The real pool comes from the external file, not these fallback env
        # values. Do not display fallback keys as if they were active.
        return {"provider": "literouter", "keys": [], "total_keys": None,
                "managed": False, "status":
                    "LiteRouter keys use an external file; manage that file before switching to dashboard keys."}
    return {"provider": "literouter", "total_keys": len(keys),
            "managed": True, "keys": [
                {"id": _key_identity(key), "masked": _masked_key(key)}
                for key in keys]}


def checked_literouter_change(values: dict) -> dict:
    if not isinstance(values, dict):
        raise ValueError("Invalid LiteRouter key change")
    if values.get("action") == "add" and set(values) == {"action", "key"}:
        key = values["key"]
        if not isinstance(key, str) or not LITEROUTER_KEY_PATTERN.fullmatch(key):
            raise ValueError("Enter one LiteRouter key (8–512 URL-safe characters)")
        return {"action": "add", "key": key}
    if values.get("action") == "remove" and set(values) == {"action", "id"}:
        identifier = values["id"]
        if isinstance(identifier, str) and re.fullmatch(r"[0-9a-f]{64}", identifier):
            return {"action": "remove", "id": identifier}
        raise ValueError("Invalid LiteRouter key identifier")
    raise ValueError("Unsupported LiteRouter key action")


def update_literouter_keys(values: dict) -> tuple[str, ...]:
    change = checked_literouter_change(values)
    with CONFIG_LOCK:
        path = ROOT / ".env"
        original = path.read_bytes()
        current = _env_rows(original)
        keys, external = _literouter_keys()
        if external:
            raise ValueError("LiteRouter has an external key file; dashboard changes would not affect the active pool")
        if change["action"] == "add":
            if change["key"] in keys:
                return ()
            if len(keys) >= LITEROUTER_KEY_LIMIT:
                raise ValueError("LiteRouter key limit reached")
            keys.append(change["key"])
        else:
            matches = [key for key in keys if hmac.compare_digest(
                _key_identity(key), change["id"])]
            if len(matches) != 1:
                raise ValueError("LiteRouter key no longer exists; refresh keys")
            keys.remove(matches[0])
            if not keys and (
                current.get("TSUKI_LLM_PROVIDER", "api") == "api" and
                current.get("TSUKI_USE_MULTIPLE_PROVIDERS", "").lower() in
                ("true", "yes", "on", "1") and
                "literouter" in {item.strip().lower() for item in
                                 current.get("TSUKI_MULTI_PROVIDERS_CSV", "").split(",")}):
                raise ValueError("Remove LiteRouter from the active provider order before deleting its final key")
            if not keys and urllib.parse.urlsplit(
                    current.get("TSUKI_REMOTE_INFERENCE_URL", "")).hostname == "api.literouter.com":
                raise ValueError("Change the single-provider inference URL before deleting the final LiteRouter key")
        # Normalize existing CSV+legacy single into one source of truth; do
        # not leave the single key as an invisible fallback when deleting it.
        changes = {"TSUKI_LITEROUTER_API_KEYS": ",".join(keys),
                   "TSUKI_LITEROUTER_API_KEY": ""}
        updated = _env_with_changes(original, changes)
        if updated == original:
            return ()
        _private_backup("api-env", original)
        try:
            write_private(path, updated)
            _recreate("api")
        except Exception:
            write_private(path, original)
            try:
                _recreate("api")
            except Exception:
                print("[controls] literouter_keys_rollback_failed", flush=True)
            raise
    return ("TSUKI_LITEROUTER_API_KEYS", "TSUKI_LITEROUTER_API_KEY")


def _run_literouter_job(job_id: str, values: dict):
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id]["state"] = "running"
    try:
        updated = update_literouter_keys(values)
        result = {"state": "done", "updated": list(updated)}
    except ValueError as error:
        result = {"state": "failed", "error": str(error)}
    except Exception as error:
        print("[controls] literouter_keys_failed type=" +
              type(error).__name__, flush=True)
        result = {"state": "failed", "error":
                  "LiteRouter keys could not be applied. Check API state and refresh keys."}
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id].update(result)
        CONFIG_JOBS[job_id]["finished_at"] = time.time()


def begin_literouter_job(values: dict) -> str | None:
    checked_literouter_change(values)
    with CONFIG_JOB_LOCK:
        if any(job["state"] in ("queued", "running")
               for job in CONFIG_JOBS.values()):
            return None
        for key, job in list(CONFIG_JOBS.items()):
            if time.time() - job["created_at"] > 600:
                CONFIG_JOBS.pop(key, None)
        job_id = secrets.token_urlsafe(18)
        CONFIG_JOBS[job_id] = {"state": "queued", "created_at": time.time()}
    threading.Thread(target=_run_literouter_job, args=(job_id, values),
                     daemon=True, name="tsuki-literouter-keys").start()
    return job_id


def read_groq_keys() -> dict:
    _, _, keys, source = _groq_keys()
    usage = _groq_usage_snapshot()
    return {
        "provider": "groq", "source": source, "total_keys": len(keys),
        "keys": [
            {"id": _key_identity(key),
             "masked": _masked_key(key),
             "calls_today": (
                 usage["per_key"].get(_key_identity(key), 0)
                 if usage["coverage"] == "complete" else None),
             "recorded_calls_today": usage["per_key"].get(_key_identity(key), 0)}
            for key in keys
        ],
        "calls_today": usage["total_calls_today"],
        "recorded_calls_today": usage["recorded_calls_today"],
        "date": usage["date"],
        "timezone": "UTC",
        "coverage": usage["coverage"],
    }


def _groq_usage_snapshot(now: datetime.datetime | None = None) -> dict:
    """Actual Groq HTTP attempts from the API/bridge persistent JSONL files.

    Both services emit separate events onto their shared /data volume. A
    missing/invalid source or a tracker started after midnight makes today's
    *total* unknowable: surface recorded counts as partial, never as zero.
    """
    at = now or datetime.datetime.now(datetime.timezone.utc)
    if at.tzinfo is None:
        raise ValueError("Usage time must be timezone-aware")
    utc = at.astimezone(datetime.timezone.utc)
    day = utc.date()
    start = datetime.datetime.combine(
        day, datetime.time.min, tzinfo=datetime.timezone.utc)
    end = start + datetime.timedelta(days=1)
    totals: dict[str, int] = {}
    complete = True
    for service, filename in (
            ("api", "groq-usage-api.jsonl"),
            ("bridge", "groq-usage-bridge.jsonl")):
        try:
            result = compose("exec", "-T", service, "cat",
                             "/data/" + filename, timeout=9)
        except (OSError, subprocess.TimeoutExpired):
            complete = False
            continue
        output = getattr(result, "stdout", None)
        if (result.returncode != 0 or not isinstance(output, str) or
                len(output) > 16 * 1024 * 1024):
            complete = False
            continue
        first_started = None
        for line in output.splitlines():
            if not line.strip():
                continue
            try:
                item = json.loads(line)
                stamp = datetime.datetime.fromisoformat(
                    item["timestamp_utc"].replace("Z", "+00:00"))
                if stamp.utcoffset() != datetime.timedelta(0):
                    raise ValueError("Non-UTC timestamp")
                if item.get("type") == "tracking_started":
                    if first_started is None or stamp < first_started:
                        first_started = stamp
                    continue
                identifier = item.get("key_id")
                if (item.get("type") != "attempt" or
                        not isinstance(identifier, str) or
                        not re.fullmatch(r"[0-9a-f]{64}", identifier) or
                        item.get("kind") not in ("llm", "stt", "other")):
                    raise ValueError("Invalid attempt record")
                if start <= stamp < end:
                    totals[identifier] = totals.get(identifier, 0) + 1
            except (TypeError, AttributeError, KeyError, ValueError,
                    json.JSONDecodeError):
                complete = False
        if first_started is None or first_started > start:
            complete = False
    recorded = sum(totals.values())
    return {
        "date": day.isoformat(), "timezone": "UTC",
        "coverage": "complete" if complete else "partial",
        "total_calls_today": recorded if complete else None,
        "recorded_calls_today": recorded, "per_key": totals,
    }


def read_inference_providers() -> dict:
    """Expose the real C# multi-provider config, not Molab/Colab TTS routes."""
    root_env = ROOT / ".env"
    values = _env_rows(root_env.read_bytes())
    configured = [value.strip().lower() for value in
                  values.get("TSUKI_MULTI_PROVIDERS_CSV", "").split(",")
                  if value.strip()]
    configured = list(dict.fromkeys(configured))
    enabled = values.get("TSUKI_USE_MULTIPLE_PROVIDERS", "").lower() in (
        "1", "true", "yes", "on")
    # A legacy notebook override can still bypass the C# API provider engine.
    override = values.get("TSUKI_LLM_PROVIDER", "api").lower()
    return {
        "selected": configured,
        "multi_enabled": enabled,
        "legacy_override": override,
        "providers": [
            {"id": name, "configured": name in configured,
             "credential_configured": (
                 bool(values.get(INFERENCE_KEY_ENV[name]))
                 or (name == "groq" and len(_groq_keys()[2]) > 0)
                 or (name == "literouter" and bool(
                     values.get("TSUKI_LITEROUTER_API_KEYS") or
                     values.get("TSUKI_LITEROUTER_API_KEYS_FILE")))),
             "credential_masked": (
                 (_masked_key(values[INFERENCE_KEY_ENV[name]])
                  if values.get(INFERENCE_KEY_ENV[name]) else "")
                 if name != "groq" else ""),
             "model": {
                 "groq": "openai/gpt-oss-120b",
                 "cerebras": "llama3.1-8b",
                 "gemini": "gemini-2.0-flash",
                 "github": "openai/gpt-4o-mini",
                 "mistral": "mistral-small-latest",
                 "literouter": "Choose from your available models"}[name],
             "selected_model": (
                 (values.get(INFERENCE_MODEL_ENV[name]) or
                  ("openai/gpt-oss-120b" if name == "groq" else ""))
                 if name in INFERENCE_MODEL_ENV else "")}
            for name in INFERENCE_PROVIDERS],
        "usage": {"total_calls_today": None, "date": time.strftime(
            "%Y-%m-%d", time.gmtime()), "timezone": "UTC",
            "coverage": "unavailable"},
    }


def read_inference_models(provider: str) -> dict:
    """List models ONLY; do not invoke completions, generate, or test prompts."""
    if provider not in INFERENCE_MODELS_URL:
        raise ValueError("Model listing is supported for Groq and LiteRouter")
    if provider == "groq":
        keys = _groq_keys()[2]
        if not keys:
            root_env = ROOT / ".env"
            key = _env_value(root_env, "TSUKI_GROQ_API_KEY")
            keys = [key] if key else []
    else:
        keys, external = _literouter_keys()
        if external:
            raise ValueError("LiteRouter uses an external key file; available models cannot be verified from dashboard credentials")
    if not keys:
        raise ValueError("Configure a private " + provider.upper() +
                         " API key before listing models")
    last_error = None
    for key in keys[:8]:
        request = urllib.request.Request(
            INFERENCE_MODELS_URL[provider],
            headers={"Authorization": "Bearer " + key,
                     "Accept": "application/json",
                     "User-Agent": "TsukiControls/1"},
            method="GET")
        try:
            with urllib.request.urlopen(request, timeout=8) as response:
                if response.status != 200:
                    raise ValueError("Provider model listing failed")
                raw = response.read(262145)
                if len(raw) > 262144:
                    raise ValueError("Model catalog too large")
                document = json.loads(raw)
            models = document.get("data") if isinstance(document, dict) else None
            if not isinstance(models, list):
                raise ValueError("Invalid model catalog")
            ids = sorted({
                item["id"] for item in models
                if isinstance(item, dict) and
                isinstance(item.get("id"), str) and
                MODEL_ID_PATTERN.fullmatch(item["id"]) and
                item.get("active", True) is not False and
                (provider != "literouter" or item["id"].lower().endswith(":free"))
            }, key=str.lower)
            if len(ids) > 2000:
                raise ValueError("Too many available models")
            return {"provider": provider, "models": ids, "count": len(ids),
                    "source": "authenticated GET " + INFERENCE_MODELS_URL[provider],
                    "no_prompt_sent": True}
        except urllib.error.HTTPError as error:
            last_error = error
            if error.code not in (401, 403, 429):
                break
        except (OSError, UnicodeError, ValueError, TypeError) as error:
            last_error = error
            break
    # Never return provider responses/exception strings: they might echo auth.
    raise ValueError("Available models could not be verified; check provider key/access") from None


def checked_inference_model_change(values: dict) -> dict:
    if (not isinstance(values, dict) or set(values) != {"provider", "model"}
            or values.get("provider") not in INFERENCE_MODEL_ENV
            or not isinstance(values.get("model"), str)
            or not MODEL_ID_PATTERN.fullmatch(values["model"])):
        raise ValueError("Choose a listed Groq or LiteRouter model")
    return dict(values)


def update_inference_model(values: dict) -> tuple[str, ...]:
    change = checked_inference_model_change(values)
    provider = change["provider"]
    # A listing is read-only, authenticated, and model-specific. Refuse
    # guesses/unavailable models rather than issuing a test chat prompt.
    catalog = read_inference_models(provider)
    if provider == "literouter" and not change["model"].lower().endswith(":free"):
        raise ValueError("LiteRouter model must be an available :free model")
    if change["model"] not in catalog["models"]:
        raise ValueError("Model is not in the provider's available-model listing")
    env_name = INFERENCE_MODEL_ENV[provider]
    with CONFIG_LOCK:
        path = ROOT / ".env"
        original = path.read_bytes()
        current = _env_rows(original)
        if current.get(env_name) == change["model"]:
            return ()
        updated = _env_with_changes(original, {env_name: change["model"]})
        _private_backup("api-env", original)
        try:
            write_private(path, updated)
            _recreate("api")
        except Exception:
            write_private(path, original)
            try:
                _recreate("api")
            except Exception:
                print("[controls] inference_model_rollback_failed", flush=True)
            raise
    return (env_name,)


def _run_inference_model_job(job_id: str, values: dict):
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id]["state"] = "running"
    try:
        updated = update_inference_model(values)
        result = {"state": "done", "updated": list(updated)}
    except ValueError as error:
        result = {"state": "failed", "error": str(error)}
    except Exception as error:
        print("[controls] inference_model_failed type=" +
              type(error).__name__, flush=True)
        result = {"state": "failed", "error":
                  "Model could not be applied. Check API status and available models."}
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id].update(result)
        CONFIG_JOBS[job_id]["finished_at"] = time.time()


def begin_inference_model_job(values: dict) -> str | None:
    checked_inference_model_change(values)
    with CONFIG_JOB_LOCK:
        if any(job["state"] in ("queued", "running")
               for job in CONFIG_JOBS.values()):
            return None
        for key, job in list(CONFIG_JOBS.items()):
            if time.time() - job["created_at"] > 600:
                CONFIG_JOBS.pop(key, None)
        job_id = secrets.token_urlsafe(18)
        CONFIG_JOBS[job_id] = {"state": "queued", "created_at": time.time()}
    threading.Thread(target=_run_inference_model_job, args=(job_id, values),
                     daemon=True, name="tsuki-inference-model-update").start()
    return job_id


def _masked_key(key: str) -> str:
    if len(key) < 12:
        return "Configured"
    prefix = "sk-" if key.startswith("sk-") else key[:4]
    return prefix + "..." + key[-5:]


def checked_inference_key_change(values: dict) -> dict:
    if not isinstance(values, dict):
        raise ValueError("Invalid inference key change")
    if (values.get("provider") not in INFERENCE_KEY_ENV or
            values["provider"] == "groq"):
        raise ValueError("Choose a supported non-Groq API provider")
    if set(values) == {"provider", "key"}:
        key = values["key"]
        # Keep .env assignments single-line without Compose expansion,
        # inline-comment parsing, shell control characters or quotes.
        if not isinstance(key, str) or not re.fullmatch(
                r"[A-Za-z0-9._-]{8,512}", key):
            raise ValueError("Enter a provider key (8–512 URL-safe characters)")
        return dict(values)
    if set(values) == {"provider", "action"} and values["action"] == "remove":
        return dict(values)
    raise ValueError("Unsupported inference key action")


def update_inference_key(values: dict) -> tuple[str, ...]:
    change = checked_inference_key_change(values)
    env_name = INFERENCE_KEY_ENV[change["provider"]]
    with CONFIG_LOCK:
        path = ROOT / ".env"
        original = path.read_bytes()
        current = _env_rows(original)
        new_value = change.get("key", "")
        if current.get(env_name, "") == new_value:
            return ()
        updated = _env_with_changes(original, {env_name: new_value})
        _private_backup("api-env", original)
        try:
            write_private(path, updated)
            _recreate("api")
        except Exception:
            write_private(path, original)
            try:
                _recreate("api")
            except Exception:
                print("[controls] inference_key_rollback_failed", flush=True)
            raise
    return (env_name,)


def _run_inference_key_job(job_id: str, values: dict):
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id]["state"] = "running"
    try:
        updated = update_inference_key(values)
        result = {"state": "done", "updated": list(updated)}
    except ValueError as error:
        result = {"state": "failed", "error": str(error)}
    except Exception as error:
        print("[controls] inference_key_failed type=" +
              type(error).__name__, flush=True)
        result = {"state": "failed", "error":
                  "Provider key could not be applied. Check API status."}
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id].update(result)
        CONFIG_JOBS[job_id]["finished_at"] = time.time()


def begin_inference_key_job(values: dict) -> str | None:
    checked_inference_key_change(values)
    with CONFIG_JOB_LOCK:
        if any(job["state"] in ("queued", "running")
               for job in CONFIG_JOBS.values()):
            return None
        for key, job in list(CONFIG_JOBS.items()):
            if time.time() - job["created_at"] > 600:
                CONFIG_JOBS.pop(key, None)
        job_id = secrets.token_urlsafe(18)
        CONFIG_JOBS[job_id] = {"state": "queued", "created_at": time.time()}
    threading.Thread(target=_run_inference_key_job, args=(job_id, values),
                     daemon=True, name="tsuki-inference-key-update").start()
    return job_id


def checked_inference_change(values: dict) -> list[str]:
    if (not isinstance(values, dict) or set(values) != {"providers"} or
            not isinstance(values["providers"], list) or not
            1 <= len(values["providers"]) <= len(INFERENCE_PROVIDERS) or
            any(not isinstance(name, str) or name not in INFERENCE_PROVIDERS
                for name in values["providers"]) or
            len(set(values["providers"])) != len(values["providers"])):
        raise ValueError("Choose 1–6 distinct supported API inference providers")
    if "groq" in values["providers"] and "literouter" in values["providers"]:
        raise ValueError("Enable Groq or LiteRouter, not both")
    return list(values["providers"])


def update_inference_providers(values: dict) -> tuple[str, ...]:
    selected = checked_inference_change(values)
    with CONFIG_LOCK:
        path = ROOT / ".env"
        original = path.read_bytes()
        current = _env_rows(original)
        if "literouter" in selected and not (
                current.get("TSUKI_LITEROUTER_API_KEY") or
                current.get("TSUKI_LITEROUTER_API_KEYS") or
                current.get("TSUKI_LITEROUTER_API_KEYS_FILE")):
            raise ValueError("Configure a LiteRouter API key before enabling the provider")
        if "literouter" in selected and not current.get("TSUKI_LITEROUTER_MODEL", "").lower().endswith(":free"):
            raise ValueError("Select and save a free LiteRouter model before enabling the provider")
        # The existing C# inference factory supports exactly these providers
        # through TSUKI_USE_MULTIPLE_PROVIDERS / TSUKI_MULTI_PROVIDERS_CSV.
        # Clear the former incorrectly exposed notebook LLM override.
        desired = {
            "TSUKI_USE_MULTIPLE_PROVIDERS": "true",
            "TSUKI_MULTI_PROVIDERS_CSV": ",".join(selected),
            "TSUKI_LLM_PROVIDER": "api",
        }
        changed = {key: value for key, value in desired.items()
                   if current.get(key) != value}
        if not changed:
            return ()
        updated = _env_with_changes(original, changed)
        _private_backup("api-env", original)
        try:
            write_private(path, updated)
            _recreate("api")
        except Exception:
            write_private(path, original)
            try:
                _recreate("api")
            except Exception:
                print("[controls] api_inference_rollback_failed", flush=True)
            raise
    return tuple(changed)


def _run_inference_job(job_id: str, values: dict):
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id]["state"] = "running"
    try:
        updated = update_inference_providers(values)
        result = {"state": "done", "updated": list(updated)}
    except ValueError as error:
        result = {"state": "failed", "error": str(error)}
    except Exception as error:
        print("[controls] inference_config_failed type=" +
              type(error).__name__, flush=True)
        result = {"state": "failed", "error":
                  "Inference provider settings could not be applied. Check API status."}
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id].update(result)
        CONFIG_JOBS[job_id]["finished_at"] = time.time()


def begin_inference_job(values: dict) -> str | None:
    checked_inference_change(values)
    with CONFIG_JOB_LOCK:
        if any(job["state"] in ("queued", "running")
               for job in CONFIG_JOBS.values()):
            return None
        for key, job in list(CONFIG_JOBS.items()):
            if time.time() - job["created_at"] > 600:
                CONFIG_JOBS.pop(key, None)
        job_id = secrets.token_urlsafe(18)
        CONFIG_JOBS[job_id] = {"state": "queued", "created_at": time.time()}
    threading.Thread(target=_run_inference_job, args=(job_id, values),
                     daemon=True, name="tsuki-inference-update").start()
    return job_id


def checked_groq_change(values: dict) -> dict:
    if not isinstance(values, dict):
        raise ValueError("Invalid Groq key change")
    action = values.get("action")
    if action == "add" and set(values) == {"action", "key"}:
        key = values["key"]
        if not isinstance(key, str):
            raise ValueError("Enter a Groq API key")
        key = key.strip()
        if not GROQ_KEY_PATTERN.fullmatch(key):
            raise ValueError("Enter one valid-looking Groq key (gsk_… or sk-…)")
        return {"action": "add", "key": key}
    if action == "remove" and set(values) == {"action", "id"}:
        identity = values["id"]
        if isinstance(identity, str) and re.fullmatch(r"[0-9a-f]{64}", identity):
            return {"action": "remove", "id": identity}
        raise ValueError("Invalid key identifier")
    raise ValueError("Unsupported Groq key action")


def update_groq_keys(values: dict) -> tuple[str, ...]:
    changed = checked_groq_change(values)
    with CONFIG_LOCK:
        path, original, keys, source = _groq_keys()
        if changed["action"] == "add":
            if changed["key"] in keys:
                return ()
            if len(keys) >= GROQ_KEY_LIMIT:
                raise ValueError("Groq key limit reached")
            keys.append(changed["key"])
        else:
            matches = [key for key in keys if hmac.compare_digest(
                _key_identity(key), changed["id"])]
            if len(matches) != 1:
                raise ValueError("Groq key no longer exists; refresh the list")
            keys.remove(matches[0])
            if source == "inline":
                raise ValueError("Migrate legacy inline Groq keys before removing one")
        if not path.parent.is_dir():
            raise ValueError("The configured Groq key directory is unavailable")
        if original is not None:
            _private_backup("groq-keys", original)
        # A zero-key file may activate a legacy inline fallback in either
        # process, resurrecting the key the operator just removed.
        if not keys:
            root_env = ROOT / ".env"
            bridge_env = ROOT / "discord-voice-bridge" / ".env"
            fallback = any(
                path_env.exists() and _env_value(path_env, name)
                for path_env, name in (
                    (root_env, "TSUKI_GROQ_API_KEYS"),
                    (root_env, "TSUKI_GROQ_API_KEY"),
                    (bridge_env, "GROQ_API_KEYS"),
                    (bridge_env, "GROQ_API_KEY")))
            if fallback:
                raise ValueError("Remove legacy inline Groq fallbacks on the VPS before deleting the final key")
        body = ("\n".join(keys) + "\n").encode() if keys else b"# Empty key pool\n"
        api_attempted = False
        bridge_attempted = False
        try:
            write_private(path, body)
            api_attempted = True
            _recreate("api")
            bridge_attempted = True
            _recreate("bridge")
        except Exception:
            if original is None:
                path.unlink(missing_ok=True)
            else:
                write_private(path, original)
            for service, attempted in (("api", api_attempted),
                                       ("bridge", bridge_attempted)):
                if attempted:
                    try:
                        _recreate(service)
                    except Exception:
                        print("[controls] groq_" + service +
                              "_rollback_recreate_failed", flush=True)
            raise
    return ("GROQ_KEYS",)


def _run_groq_key_job(job_id: str, values: dict):
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id]["state"] = "running"
    try:
        updated = update_groq_keys(values)
        result = {"state": "done", "updated": list(updated),
                  "detail": "Groq keys applied" if updated else "Key already configured"}
    except ValueError as error:
        result = {"state": "failed", "error": str(error)}
    except Exception as error:
        print("[controls] groq_keys_failed type=" + type(error).__name__, flush=True)
        result = {"state": "failed", "error":
                  "Groq keys could not be applied. Check both service states and refresh keys."}
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id].update(result)
        CONFIG_JOBS[job_id]["finished_at"] = time.time()


def begin_groq_key_job(values: dict) -> str | None:
    checked_groq_change(values)
    with CONFIG_JOB_LOCK:
        if any(job["state"] in ("queued", "running")
               for job in CONFIG_JOBS.values()):
            return None
        for key, job in list(CONFIG_JOBS.items()):
            if time.time() - job["created_at"] > 600:
                CONFIG_JOBS.pop(key, None)
        job_id = secrets.token_urlsafe(18)
        CONFIG_JOBS[job_id] = {"state": "queued", "created_at": time.time()}
    threading.Thread(target=_run_groq_key_job, args=(job_id, values),
                     daemon=True, name="tsuki-groq-update").start()
    return job_id


def update_bot_config(values):
    requested = checked_bot_config(values)
    with CONFIG_LOCK:
        bridge_path = ROOT / "discord-voice-bridge" / ".env"
        bridge_original = bridge_path.read_bytes()
        bridge_current = _env_rows(bridge_original)
        personality_requested = requested.pop("TSUKI_PERSONALITY_INJECTION_B64", None)
        base_requested = requested.pop("__MODELFILE_PERSONALITY__", None)
        base_path = ROOT / "assets" / "Modelfile"
        base_original = base_path.read_bytes() if base_requested is not None else None
        base_mode = (base_path.stat().st_mode & 0o777) if base_original is not None else None
        base_updated = (_modelfile_with_personality(base_original, base_requested)
                        if base_original is not None else None)
        if base_updated == base_original:
            base_updated = None
        bridge_changed = {key: value for key, value in requested.items()
                          if bridge_current.get(key) != value}
        if bridge_current.get("TEXT_VOICE_MODE") == "all":
            bridge_changed.setdefault("TEXT_VOICE_MODE", "keywords")
        effective_mode = bridge_changed.get("TEXT_VOICE_MODE",
                                            bridge_current.get("TEXT_VOICE_MODE", "keywords"))
        effective_terms = bridge_changed.get("TEXT_VOICE_KEYWORDS",
                                             bridge_current.get("TEXT_VOICE_KEYWORDS",
                                                                DEFAULT_TEXT_VOICE_KEYWORDS))
        if effective_mode == "keywords" and not effective_terms.strip():
            raise ValueError("Enter trigger words before enabling Keywords only")
        root_path = ROOT / ".env"
        root_original = None
        root_changed = {}
        if personality_requested is not None:
            root_original = root_path.read_bytes()
            root_current = _env_rows(root_original)
        if personality_requested is not None:
            if root_current.get("TSUKI_PERSONALITY_INJECTION_B64", "") != personality_requested:
                root_changed["TSUKI_PERSONALITY_INJECTION_B64"] = personality_requested
        if not bridge_changed and not root_changed and base_updated is None:
            return ()
        verify_bot_guild_config(bridge_changed, bridge_original)
        # Read and validate both files before mutating either; this also rejects
        # duplicate keys and allows a failed recreation to restore exact bytes.
        bridge_updated = _env_with_changes(bridge_original, bridge_changed)
        root_updated = (_env_with_changes(root_original, root_changed)
                        if root_changed else None)
        _private_backup("bridge-env", bridge_original)
        if root_changed:
            _private_backup("api-env", root_original)
        if base_updated is not None:
            _private_backup("modelfile", base_original)
        api_attempted = False
        bridge_attempted = False
        try:
            if root_updated is not None:
                write_private(root_path, root_updated)
            if base_updated is not None:
                write_private(base_path, base_updated)
                os.chmod(base_path, base_mode)
            if bridge_changed:
                write_private(bridge_path, bridge_updated)
            if root_changed or base_updated is not None:
                api_attempted = True
                _recreate("api")
            if bridge_changed:
                bridge_attempted = True
                _recreate("bridge")
        except Exception:
            if root_updated is not None:
                write_private(root_path, root_original)
            if base_updated is not None:
                write_private(base_path, base_original)
                os.chmod(base_path, base_mode)
            if bridge_changed:
                write_private(bridge_path, bridge_original)
            for service, attempted in (("api", api_attempted),
                                       ("bridge", bridge_attempted)):
                if attempted:
                    try:
                        _recreate(service)
                    except Exception:
                        print("[controls] " + service +
                              "_rollback_recreate_failed", flush=True)
            raise
    return tuple(dict.fromkeys((*root_changed, *bridge_changed,
                                 *(("__MODELFILE_PERSONALITY__",) if base_updated is not None else ()))))


def _run_bot_config_job(job_id: str, values: dict):
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id]["state"] = "running"
    try:
        updated = update_bot_config(values)
        result = {"state": "done", "updated": list(updated),
                  "detail": "Settings saved" if updated else "Already configured"}
    except ValueError as error:
        result = {"state": "failed", "error": str(error)}
    except urllib.error.HTTPError as error:
        result = {"state": "failed", "error":
                  "Discord is rate-limiting channel checks. Try again shortly."
                  if error.code == 429 else
                  "Discord could not verify the selected server and channels."}
    except subprocess.TimeoutExpired:
        result = {"state": "failed", "error":
                  "Service reload timed out; check API and bridge status before retrying."}
    except (OSError, RuntimeError) as error:
        print("[controls] config_job_failed type=" + type(error).__name__,
              flush=True)
        safe_reason = str(error)
        result = {"state": "failed", "error":
                  (safe_reason + "; previous settings restored. Check both service statuses."
                   if safe_reason.startswith("Docker could not recreate ")
                   else "Configuration could not be applied; previous settings restored.")}
    except Exception as error:
        print("[controls] config_job_unexpected type=" + type(error).__name__,
              flush=True)
        result = {"state": "failed", "error": "Configuration update failed."}
    with CONFIG_JOB_LOCK:
        CONFIG_JOBS[job_id].update(result)
        CONFIG_JOBS[job_id]["finished_at"] = time.time()


def begin_bot_config_job(values: dict) -> str | None:
    checked_bot_config(values)  # Reject invalid payloads synchronously.
    with CONFIG_JOB_LOCK:
        if any(job["state"] in ("queued", "running")
               for job in CONFIG_JOBS.values()):
            return None
        for key, job in list(CONFIG_JOBS.items()):
            if time.time() - job["created_at"] > 600:
                CONFIG_JOBS.pop(key, None)
        job_id = secrets.token_urlsafe(18)
        CONFIG_JOBS[job_id] = {"state": "queued", "created_at": time.time()}
    threading.Thread(target=_run_bot_config_job, args=(job_id, values),
                     daemon=True, name="tsuki-config-update").start()
    return job_id


def verify_password(candidate: str) -> bool:
    if not isinstance(candidate, str):
        return False
    if AUTH_FILE.exists():
        try:
            record = json.loads(AUTH_FILE.read_text(encoding="utf-8"))
            iterations = record["iterations"]
            if not isinstance(iterations, int) or not 300_000 <= iterations <= 2_000_000:
                return False
            expected = bytes.fromhex(record["hash"])
            salt = bytes.fromhex(record["salt"])
            actual = hashlib.pbkdf2_hmac("sha256", candidate.encode("utf-8"),
                                         salt, iterations)
            return hmac.compare_digest(actual, expected)
        except (OSError, UnicodeError, ValueError, KeyError, TypeError):
            return False
    return bool(PASSWORD) and hmac.compare_digest(candidate, PASSWORD)


def change_password(current: str, new: str):
    global PASSWORD
    if not verify_password(current):
        raise PermissionError("Current password is incorrect")
    if not isinstance(new, str) or not 12 <= len(new) <= 128:
        raise ValueError("New password must be 12–128 characters")
    if hmac.compare_digest(current, new):
        raise ValueError("New password must be different")
    # Controls password is separate from the API web password after changing.
    # No .env interpolation, API recreation, or plaintext credential on disk.
    with CONFIG_LOCK:
        if not verify_password(current):
            raise PermissionError("Current password has changed")
        salt = secrets.token_bytes(24)
        hashed = hashlib.pbkdf2_hmac("sha256", new.encode("utf-8"),
                                     salt, AUTH_ITERATIONS)
        data = json.dumps({"version": 1, "iterations": AUTH_ITERATIONS,
                           "salt": salt.hex(), "hash": hashed.hex()}).encode()
        write_private(AUTH_FILE, data)
        PASSWORD = ""
        with LOCK:
            SESSIONS.clear()
            # Password changes revoke every long-lived browser, too.
            write_private(TRUSTED_DEVICES_FILE, b"{}")


def _trusted_records():
    try:
        content = json.loads(TRUSTED_DEVICES_FILE.read_text(encoding="utf-8"))
        if not isinstance(content, dict):
            return {}
        return {key: expires for key, expires in content.items()
                if isinstance(key, str) and isinstance(expires, (int, float))
                and expires > time.time()}
    except FileNotFoundError:
        return {}
    except (OSError, ValueError, UnicodeError):
        # Fail closed if the local device registry cannot be read.
        return {}


def _trusted_digest(token):
    return hashlib.sha256(("tsuki-controls-device-v1:" + token).encode()).hexdigest()


def remember_device(token):
    with LOCK:
        records = _trusted_records()
        if len(records) >= TRUSTED_DEVICE_LIMIT:
            oldest = min(records, key=records.get)
            records.pop(oldest)
        records[_trusted_digest(token)] = time.time() + TRUSTED_DEVICE_TTL
        write_private(TRUSTED_DEVICES_FILE, json.dumps(records).encode())


def revoke_device(token):
    if not token:
        return
    with LOCK:
        records = _trusted_records()
        records.pop(_trusted_digest(token), None)
        write_private(TRUSTED_DEVICES_FILE, json.dumps(records).encode())


def _discord_token() -> str:
    token = _env_value(ROOT / "discord-voice-bridge" / ".env", "DISCORD_TOKEN")
    if not token:
        raise RuntimeError("Discord token is not configured")
    return token


def _discord_request(method: str, path: str, payload=None, timeout=15):
    headers = {
        "Authorization": "Bot " + _discord_token(),
        "User-Agent": "TsukiControls/1",
    }
    data = None
    if payload is not None:
        data = json.dumps(payload).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request("https://discord.com/api/v10" + path,
                                 data=data, method=method, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as response:
        body = response.read(2 * 1024 * 1024)
        return response.status, json.loads(body) if body else {}


def discord_identity():
    with IDENTITY_CACHE_LOCK:
        now = time.monotonic()
        if (IDENTITY_CACHE["value"] is not None and
                now - IDENTITY_CACHE["checked_at"] < 120):
            return dict(IDENTITY_CACHE["value"])
        try:
            _, user = _discord_request("GET", "/users/@me", timeout=5)
        except (OSError, urllib.error.HTTPError):
            if IDENTITY_CACHE["value"] is not None:
                IDENTITY_CACHE["checked_at"] = now
                return dict(IDENTITY_CACHE["value"])
            raise
        avatar = user.get("avatar")
        result = {
            "id": str(user.get("id", "")),
            "username": str(user.get("username", ""))[:80],
            "has_avatar": bool(avatar),
            "avatar_hash": str(avatar or "")[:100],
        }
        IDENTITY_CACHE.update(checked_at=now, value=result)
        return dict(result)


def discord_servers(force=False):
    """Actual bot membership, cached to respect Discord's REST rate limits."""
    with DISCORD_CACHE_LOCK:
        now = time.monotonic()
        if not force and now - GUILD_CACHE["checked_at"] < DISCORD_CACHE_SECONDS:
            return list(GUILD_CACHE["items"])
        try:
            _, data = _discord_request("GET", "/users/@me/guilds")
        except urllib.error.HTTPError as error:
            if error.code == 429 and GUILD_CACHE["checked_at"]:
                GUILD_CACHE["checked_at"] = now  # Cool down instead of retry-spamming.
                return list(GUILD_CACHE["items"])
            raise
        if not isinstance(data, list):
            raise RuntimeError("Discord guild list is unavailable")
        servers = []
        for item in data[:200]:
            identifier = str(item.get("id", ""))
            if re.fullmatch(r"[0-9]{17,20}", identifier):
                servers.append({"id": identifier,
                                "name": str(item.get("name", "Unnamed server"))[:100]})
        GUILD_CACHE["items"] = sorted(servers, key=lambda item: item["name"].casefold())
        GUILD_CACHE["checked_at"] = now
        return list(GUILD_CACHE["items"])


def discord_server_channels(guild_id: str, force=False):
    if not re.fullmatch(r"[0-9]{17,20}", guild_id):
        raise ValueError("Invalid Discord server")
    if not any(item["id"] == guild_id for item in discord_servers()):
        raise ValueError("Bot has not joined this server")
    with DISCORD_CACHE_LOCK:
        cached = CHANNEL_CACHE.get(guild_id)
        if cached and not force and time.monotonic() - cached[0] < DISCORD_CACHE_SECONDS:
            return cached[1]
        try:
            _, data = _discord_request("GET", "/guilds/" + guild_id + "/channels")
        except urllib.error.HTTPError as error:
            if error.code == 429 and cached:
                CHANNEL_CACHE[guild_id] = (time.monotonic(), cached[1])
                return {**cached[1], "stale": True}
            raise
        if not isinstance(data, list):
            raise RuntimeError("Discord channel list is unavailable")
        result = {
            "guild_id": guild_id,
            "voice": [{"id": str(ch["id"]), "name": str(ch.get("name", ""))[:100]}
                      for ch in data if ch.get("type") == 2 and
                      re.fullmatch(r"[0-9]{17,20}", str(ch.get("id", "")))],
            "text": [{"id": str(ch["id"]), "name": str(ch.get("name", ""))[:100]}
                     for ch in data if ch.get("type") in (0, 5) and
                     re.fullmatch(r"[0-9]{17,20}", str(ch.get("id", "")))],
        }
        CHANNEL_CACHE[guild_id] = (time.monotonic(), result)
        return result


def discord_avatar_bytes():
    user = discord_identity()
    if user["has_avatar"]:
        url = ("https://cdn.discordapp.com/avatars/" + user["id"] + "/" +
               user["avatar_hash"] + ".png?size=256")
    else:
        url = "https://cdn.discordapp.com/embed/avatars/0.png"
    req = urllib.request.Request(url, headers={"User-Agent": "TsukiControls/1"})
    with urllib.request.urlopen(req, timeout=15) as response:
        data = response.read(2 * 1024 * 1024)
        content_type = response.headers.get("Content-Type", "image/png")
    if not data or not content_type.startswith("image/"):
        raise RuntimeError("Discord avatar unavailable")
    return data, content_type


def set_discord_avatar(data_url: str):
    if not isinstance(data_url, str):
        raise ValueError("Image is required")
    match = re.fullmatch(r"data:(image/(?:png|jpeg|webp));base64,([A-Za-z0-9+/=]+)",
                         data_url)
    if not match:
        raise ValueError("Use PNG, JPEG, or WebP")
    try:
        raw = base64.b64decode(match.group(2), validate=True)
    except ValueError as error:
        raise ValueError("Invalid image data") from error
    if len(raw) < 64 or len(raw) > 1024 * 1024:
        raise ValueError("Avatar must be between 64 bytes and 1 MiB")
    _, user = _discord_request("PATCH", "/users/@me", {"avatar": data_url}, timeout=25)
    with IDENTITY_CACHE_LOCK:
        IDENTITY_CACHE["checked_at"] = 0.0
        IDENTITY_CACHE["value"] = None
    return {"id": str(user.get("id", "")), "username": str(user.get("username", ""))[:80]}


def _parse_compose_ps(stdout: str):
    text = stdout.strip()
    if not text:
        return []
    try:
        parsed = json.loads(text)
        if isinstance(parsed, list):
            return [item for item in parsed if isinstance(item, dict)]
        if isinstance(parsed, dict):
            return [parsed]
    except ValueError:
        pass
    rows = []
    for line in text.splitlines():
        try:
            item = json.loads(line)
            if isinstance(item, dict):
                rows.append(item)
        except ValueError:
            continue
    return rows


def _docker_fallback_states():
    """Recover container state if Compose PS fails or emits partial JSON."""
    output = subprocess.run(
        ["docker", "ps", "-a", "--filter", "name=^tsukiai-",
         "--format", "{{.Names}}|{{.Status}}"],
        capture_output=True, text=True, timeout=6)
    if output.returncode != 0:
        return {}
    found = {}
    for row in output.stdout.splitlines():
        if "|" not in row:
            continue
        name, status = row.split("|", 1)
        if not name.startswith("tsukiai-") or not name.endswith("-1"):
            continue
        service = name[len("tsukiai-"):-2]
        if service in SERVICES:
            state = ("running" if status.startswith("Up") else
                     "stopped" if status.startswith("Exited") else
                     "restarting" if status.startswith("Restarting") else "unknown")
            found[service] = {"service": service, "state": state,
                              "status": status[:100]}
    return found


def read_personality():
    """Expose only to an authenticated dashboard session, never /api/status."""
    encoded = _env_value(ROOT / ".env", "TSUKI_PERSONALITY_INJECTION_B64")
    try:
        text = base64.b64decode(encoded, validate=True).decode("utf-8") if encoded else ""
        if (len(text) > 4000 or
                any((ord(c) < 32 and c not in "\r\n\t") or ord(c) == 127
                    for c in text)):
            raise ValueError("Invalid stored personality")
    except (ValueError, UnicodeError):
        raise ValueError("Stored personality instructions are invalid") from None
    base, _ = _modelfile_personality(
        (ROOT / "assets" / "Modelfile").read_bytes())
    return {"base_personality": base,
            "base_source": "assets/Modelfile · SYSTEM",
            "personality_injection": text, "customized": bool(text)}


def read_bot_config():
    bot = {}
    for line in ROOT.joinpath("discord-voice-bridge", ".env").read_text(
            encoding="utf-8-sig").splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key in BOT_ENV_FIELDS:
            bot[BOT_ENV_FIELDS[key]] = _unquote(value)
    bot.setdefault("text_reply_mode", "mention")
    if bot.get("text_voice_mode") not in ("keywords", "off"):
        bot["text_voice_mode"] = "keywords"
    bot.setdefault("text_voice_keywords", DEFAULT_TEXT_VOICE_KEYWORDS)
    bot.setdefault("tts_provider", "molab")
    for key in ("voice_engagement_mode", "chat_engagement_mode"):
        if bot.get(key) not in ("wake_only", "context_aware", "proactive"):
            bot[key] = "context_aware"
    bot.setdefault("engagement_aliases", "tsuki,tsukihime")
    bot.setdefault("engagement_followup_ms", "10000")
    bot.setdefault("classifier_model", "")
    bot["classifier_model_configured"] = bool(bot["classifier_model"])
    return bot


def _selected_provider(bot: dict) -> str:
    value = str(bot.get("tts_provider", "molab")).lower()
    return value if value in ("molab", "colab", "off") else "off"


def _provider_status(selected: str):
    providers = {
        "Molab": {"selected": selected == "molab", "online": None, "status": "standby"},
        "Colab": {"selected": selected == "colab", "online": None, "status": "standby"},
    }
    if selected == "off":
        return providers
    label = "Molab" if selected == "molab" else "Colab"
    try:
        req = urllib.request.Request(PROVIDERS[selected], headers={"User-Agent": "TsukiControls/1"})
        with urllib.request.urlopen(req, timeout=4) as response:
            health = json.loads(response.read(8192))
            ready = response.status == 200 and health.get("status") == "ready"
            providers[label] = {
                "selected": True, "online": ready,
                "status": "ready" if ready else "not ready",
            }
    except Exception:
        providers[label] = {"selected": True, "online": False, "status": "unavailable"}
    return providers


def status():
    records = {}
    try:
        result = compose("ps", "--format", "json", timeout=8)
        entries = _parse_compose_ps(result.stdout) if result.returncode == 0 else []
    except (OSError, subprocess.TimeoutExpired):
        entries = []
    for entry in entries:
        service = entry.get("Service")
        if service in SERVICES:
            state = str(entry.get("State") or entry.get("Status") or "unknown")
            if state.lower().startswith("up"):
                state = "running"
            records[service] = {
                "service": service,
                "state": state,
                "status": str(entry.get("Status", ""))[:100],
            }
    if len(records) < len(SERVICES):
        try:
            for service, record in _docker_fallback_states().items():
                records.setdefault(service, record)
        except (OSError, subprocess.TimeoutExpired):
            pass
    stats = {}
    try:
        raw = subprocess.run(
            ["docker", "stats", "--no-stream", "--format",
             "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}"],
            capture_output=True, text=True, timeout=6)
        if raw.returncode == 0:
            for line in raw.stdout.splitlines():
                parts = line.split("|", 2)
                if len(parts) == 3 and parts[0].startswith("tsukiai-"):
                    stats[parts[0]] = {"cpu": parts[1][:20], "memory": parts[2][:75]}
    except (OSError, subprocess.TimeoutExpired):
        pass
    disk = shutil.disk_usage("/")
    mem = {}
    for line in Path("/proc/meminfo").read_text().splitlines():
        if line.startswith(("MemTotal:", "MemAvailable:")):
            key, amount = line.split(":", 1)
            mem[key] = int(amount.strip().split()[0]) * 1024
    bot = read_bot_config()
    selected = _selected_provider(bot)
    try:
        avatar = discord_identity()
    except Exception:
        avatar = {"id": "", "username": "Tsukihime", "has_avatar": False, "avatar_hash": ""}
    return {
        "services": [records.get(s, {"service": s, "state": "unknown", "status": "Docker status could not be read"})
                     for s in SERVICES],
        "docker_stats": stats,
        "bot": bot,
        "providers": _provider_status(selected),
        "selected_provider": selected,
        "avatar": avatar,
        "system": {
            "uptime_seconds": int(float(Path("/proc/uptime").read_text().split()[0])),
            "load": list(os.getloadavg()),
            "disk_total": disk.total,
            "disk_used": disk.used,
            "memory_total": mem.get("MemTotal"),
            "memory_available": mem.get("MemAvailable"),
        },
        "updated_at": time.strftime("%Y-%m-%d %H:%M:%S UTC", time.gmtime()),
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "TsukiControls"
    sys_version = ""

    def log_message(self, *args):
        pass

    def common(self, cache_control="private, no-store, no-transform"):
        self.send_header("Cache-Control", cache_control)
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header(
            "Content-Security-Policy",
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
            "connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'",
        )

    def reply(self, status_code, value, set_cookie=None):
        data = json.dumps(value).encode()
        self.send_response(status_code)
        self.common()
        self.send_header("Content-Type", "application/json; charset=utf-8")
        if set_cookie:
            for cookie in (set_cookie if isinstance(set_cookie, (list, tuple)) else [set_cookie]):
                self.send_header("Set-Cookie", cookie)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def good_host(self):
        host = self.headers.get("Host", "").split(":", 1)[0].lower()
        return host == HOST or (host == "127.0.0.1" and
                                self.client_address[0] == "127.0.0.1")

    def cookies(self):
        try:
            values = SimpleCookie()
            values.load(self.headers.get("Cookie", ""))
            return values
        except ValueError:
            return SimpleCookie()

    def session(self):
        cookies = self.cookies()
        token = cookies["tsuki_controls"].value if "tsuki_controls" in cookies else ""
        if token:
            with LOCK:
                item = SESSIONS.get(token)
                if item and item[0] > time.time():
                    return token, item[1]
                SESSIONS.pop(token, None)
        trusted = cookies["tsuki_trusted"].value if "tsuki_trusted" in cookies else ""
        if not trusted or len(trusted) > 128:
            return None
        with LOCK:
            records = _trusted_records()
        if _trusted_digest(trusted) in records:
            # CSRF secret depends on the unguessable device bearer token, and
            # survives controls-service restarts without storing plaintext.
            csrf = hashlib.sha256(("tsuki-controls-csrf-v1:" + trusted).encode()).hexdigest()
            return trusted, csrf
        return None

    def body(self, limit=4096):
        try:
            size = int(self.headers.get("Content-Length", "0"))
            if not 0 < size <= limit:
                return None
            body = json.loads(self.rfile.read(size))
            return body if isinstance(body, dict) else None
        except (OSError, ValueError, TypeError):
            return None

    def do_GET(self):
        if not self.good_host():
            return self.reply(404, {"error": "Not found"})
        path = self.path.split("?", 1)[0]
        if path == "/api/session":
            return self.reply(200, {"authenticated": bool(self.session())})
        if path == "/api/groq-keys":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                return self.reply(200, read_groq_keys())
            except (OSError, UnicodeError, ValueError):
                return self.reply(503, {"error": "Groq key inventory unavailable"})
        if path == "/api/literouter-keys":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                return self.reply(200, read_literouter_keys())
            except (OSError, UnicodeError, ValueError):
                return self.reply(503, {"error": "LiteRouter key inventory unavailable"})
        if path == "/api/inference-providers":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                return self.reply(200, read_inference_providers())
            except (OSError, UnicodeError, ValueError):
                return self.reply(503, {"error": "Inference settings unavailable"})
        if path == "/api/inference-models":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            arguments = urllib.parse.parse_qs(
                urllib.parse.urlsplit(self.path).query)
            values = arguments.get("provider", [])
            if len(values) != 1 or values[0] not in INFERENCE_MODELS_URL:
                return self.reply(400, {"error": "Choose Groq or LiteRouter"})
            try:
                return self.reply(200, read_inference_models(values[0]))
            except ValueError as error:
                return self.reply(503, {"error": str(error)})
        if path.startswith("/api/config-jobs/"):
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            job_id = path.removeprefix("/api/config-jobs/")
            with CONFIG_JOB_LOCK:
                job = CONFIG_JOBS.get(job_id)
                value = dict(job) if job else None
            if not value:
                return self.reply(404, {"error": "Configuration job not found"})
            value.pop("created_at", None)
            value.pop("finished_at", None)
            return self.reply(200, value)
        if path == "/api/personality":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                return self.reply(200, read_personality())
            except (OSError, ValueError):
                return self.reply(503, {"error": "Personality settings unavailable"})
        if path == "/api/molab-metrics":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            if _selected_provider(read_bot_config()) != "molab":
                return self.reply(409, {"error": "Molab is not selected"})
            try:
                request = urllib.request.Request(
                    "https://molab-tts.tsukihime.dpdns.org/metrics",
                    headers={"User-Agent": "TsukiControls/1"})
                with urllib.request.urlopen(request, timeout=4) as response:
                    if response.status != 200:
                        raise RuntimeError("Molab metrics unavailable")
                    metrics = json.loads(response.read(4096))
                if not isinstance(metrics, dict) or not isinstance(metrics.get("requests"), int):
                    raise ValueError("Invalid Molab metrics")
                # Expose only aggregate numeric service measurements. Never
                # return text, reference prompts, model payloads or credentials.
                fields = ("since_epoch_seconds", "requests", "successes", "failures",
                          "avg_generation_ms", "avg_request_ms", "avg_queue_ms",
                          "generated_audio_seconds", "real_time_factor",
                          "gpu_batch_calls", "synthesized_utterances",
                          "full_icl_reference_prompt_precomputed",
                          "phrase_audio_cache", "attention_backend")
                return self.reply(200, {key: metrics.get(key) for key in fields})
            except (OSError, ValueError, RuntimeError, urllib.error.URLError):
                return self.reply(503, {"error": "Molab notebook metrics unavailable"})
        if path in ("/api/voice-focus", "/api/voice-analytics"):
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                code, result = bridge_rpc("/focus" if path == "/api/voice-focus" else "/analytics")
                if code != 200:
                    raise RuntimeError("Live bridge data unavailable")
                return self.reply(200, result)
            except (OSError, RuntimeError, subprocess.TimeoutExpired):
                return self.reply(503, {"error": "Live bridge data unavailable"})
        if path == "/api/voice-session":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                code, state = bridge_rpc("/voice-session")
                if code != 200:
                    raise RuntimeError("Bridge voice status unavailable")
                return self.reply(200, state)
            except (OSError, RuntimeError, subprocess.TimeoutExpired):
                return self.reply(503, {"error": "Live voice status unavailable"})
        if path == "/api/provider-health":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                selected = _selected_provider(read_bot_config())
                return self.reply(200, {"selected_provider": selected,
                                        "providers": _provider_status(selected),
                                        "checked_at": time.strftime("%H:%M:%S UTC", time.gmtime())})
            except OSError:
                return self.reply(503, {"error": "Voice provider status unavailable"})
        if path == "/api/status":
            auth = self.session()
            if not auth:
                return self.reply(401, {"error": "Sign in required"})
            try:
                result = status()
                result["csrf"] = auth[1]
                return self.reply(200, result)
            except (OSError, subprocess.TimeoutExpired):
                return self.reply(503, {"error": "VPS status unavailable"})
        if path == "/api/guilds" or (
                path.startswith("/api/guilds/") and path.endswith("/channels")):
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                if path == "/api/guilds":
                    fresh = self.path.split("?", 1)[-1] == "refresh=1"
                    return self.reply(200, {"guilds": discord_servers(force=fresh)})
                guild_id = path[len("/api/guilds/"):-len("/channels")]
                fresh = self.path.split("?", 1)[-1] == "refresh=1"
                return self.reply(200, discord_server_channels(guild_id, force=fresh))
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            except (OSError, RuntimeError, urllib.error.HTTPError):
                return self.reply(502, {"error": "Discord server/channel list unavailable"})
        if path == "/api/avatar-image":
            if not self.session():
                return self.reply(401, {"error": "Sign in required"})
            try:
                data, mime = discord_avatar_bytes()
            except Exception:
                return self.reply(502, {"error": "Avatar unavailable"})
            self.send_response(200)
            self.common()
            self.send_header("Content-Type", mime)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        static = {
            "/": ("index.html", "text/html; charset=utf-8"),
            "/app.js": ("app.js", "text/javascript; charset=utf-8"),
            "/style.css": ("style.css", "text/css; charset=utf-8"),
            "/layout-v22.css": ("layout-v22.css", "text/css; charset=utf-8"),
            "/favicon.svg": ("favicon.svg", "image/svg+xml"),
        }
        if path not in static:
            return self.reply(404, {"error": "Not found"})
        file, mime = static[path]
        try:
            data = (ROOT / "controls" / file).read_bytes()
        except OSError:
            return self.reply(503, {"error": "UI unavailable"})
        self.send_response(200)
        # The HTML shell contains no account data; no-transform prevents
        # Cloudflare injecting analytics JS blocked by this private UI\u2019s CSP.
        cache_policy = ("public, max-age=0, must-revalidate, no-transform"
                        if path == "/" else "private, no-store, no-transform")
        self.common(cache_policy)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if not self.good_host():
            return self.reply(404, {"error": "Not found"})
        if self.headers.get("Origin") not in {"https://" + HOST, "http://127.0.0.1:8099"}:
            return self.reply(403, {"error": "Invalid origin"})
        path = self.path.split("?", 1)[0]
        if path == "/api/login":
            ip = self.headers.get("CF-Connecting-IP", self.client_address[0])
            with LOCK:
                ATTEMPTS[ip] = [v for v in ATTEMPTS.get(ip, []) if time.time() - v < 300]
                if len(ATTEMPTS[ip]) >= 8:
                    return self.reply(429, {"error": "Try later"})
                ATTEMPTS[ip].append(time.time())
            body = self.body()
            password = body.get("password") if body else None
            remember = body.get("remember", False) if body else False
            if not isinstance(remember, bool):
                return self.reply(400, {"error": "Invalid remember-device setting"})
            if not verify_password(password):
                return self.reply(401, {"error": "Incorrect password"})
            token, csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
            extra_cookies = []
            if remember:
                trusted = secrets.token_urlsafe(32)
                try:
                    remember_device(trusted)
                except OSError:
                    return self.reply(503, {"error": "Remember-device storage unavailable"})
                extra_cookies.append(
                    "tsuki_trusted=" + trusted + "; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=" +
                    str(TRUSTED_DEVICE_TTL))
            with LOCK:
                SESSIONS[token] = (time.time() + 43200, csrf)
            extra_cookies.append("tsuki_controls=" + token +
                                 "; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=43200")
            return self.reply(200, {"ok": True}, extra_cookies)

        auth = self.session()
        if not auth:
            return self.reply(401, {"error": "Sign in required"})
        if not hmac.compare_digest(self.headers.get("X-CSRF-Token", ""), auth[1]):
            return self.reply(403, {"error": "Invalid CSRF token"})

        if path == "/api/logout":
            cookies = self.cookies()
            trusted = cookies["tsuki_trusted"].value if "tsuki_trusted" in cookies else ""
            try:
                revoke_device(trusted)
            except OSError:
                return self.reply(503, {"error": "Could not revoke remembered device"})
            with LOCK:
                SESSIONS.pop(auth[0], None)
            return self.reply(200, {"ok": True}, [
                "tsuki_controls=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0",
                "tsuki_trusted=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0",
            ])
        if path == "/api/password":
            body = self.body()
            try:
                change_password(body.get("current", "") if body else "",
                                body.get("new", "") if body else "")
            except PermissionError as error:
                return self.reply(401, {"error": str(error)})
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            except (OSError, RuntimeError, subprocess.TimeoutExpired):
                return self.reply(502, {"error": "Password update failed"})
            print("[controls audit] password_changed=true", flush=True)
            return self.reply(200, {"ok": True}, [
                "tsuki_controls=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0",
                "tsuki_trusted=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0",
            ])
        if path == "/api/avatar":
            body = self.body(limit=1500 * 1024)
            try:
                result = set_discord_avatar(body.get("image", "") if body else "")
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            except (OSError, RuntimeError, urllib.error.HTTPError):
                return self.reply(502, {"error": "Discord avatar update failed"})
            print("[controls audit] discord_avatar_changed=true", flush=True)
            return self.reply(200, {"ok": True, "avatar": result})
        if path == "/api/voice-focus":
            try:
                payload = checked_focus_command(self.body())
                code, result = bridge_rpc("/focus", payload)
                return self.reply(code, result if code in (200, 400, 404) else
                                  {"error": "Voice focus unavailable"})
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            except (OSError, RuntimeError, subprocess.TimeoutExpired):
                return self.reply(503, {"error": "Voice focus unavailable"})
        if path == "/api/voice-leave":
            try:
                payload = checked_voice_command(self.body())
                code, result = bridge_rpc("/leave-voice", payload)
                if code == 200:
                    print("[controls audit] voice_left=true", flush=True)
                return self.reply(code, result if code in (200, 400, 409) else
                                  {"error": "Bridge voice control unavailable"})
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            except (OSError, RuntimeError, subprocess.TimeoutExpired):
                return self.reply(503, {"error": "Bridge voice control unavailable"})
        if path == "/api/voice-tts":
            try:
                job_id = begin_voice_tts_job(self.body())
                if job_id is None:
                    return self.reply(409, {"error": "A voice or settings operation is in progress"})
                print("[controls audit] voice_tts_job_started=true", flush=True)
                return self.reply(202, {"job_id": job_id, "state": "queued"})
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            except OSError:
                return self.reply(503, {"error": "Voice controls unavailable"})
        if path == "/api/personality":
            body = self.body(limit=12000)
            try:
                if (not isinstance(body, dict) or not body or
                        not set(body).issubset({"base_personality", "personality_injection"})):
                    raise ValueError("Only existing personality and optional override can be changed here")
                job_id = begin_bot_config_job(body)
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            if job_id is None:
                return self.reply(409, {"error": "A settings change is already in progress"})
            print("[controls audit] personality_job_started=true", flush=True)
            return self.reply(202, {"job_id": job_id, "state": "queued"})
        if path == "/api/inference-key":
            try:
                job_id = begin_inference_key_job(self.body(limit=2048))
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            if job_id is None:
                return self.reply(409, {"error": "A settings change is already in progress"})
            print("[controls audit] inference_key_job_started=true", flush=True)
            return self.reply(202, {"job_id": job_id, "state": "queued"})
        if path == "/api/inference-model":
            try:
                job_id = begin_inference_model_job(self.body(limit=1024))
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            if job_id is None:
                return self.reply(409, {"error": "A settings change is already in progress"})
            print("[controls audit] inference_model_job_started=true", flush=True)
            return self.reply(202, {"job_id": job_id, "state": "queued"})
        if path == "/api/groq-keys":
            try:
                job_id = begin_groq_key_job(self.body(limit=2048))
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            if job_id is None:
                return self.reply(409, {"error": "A settings change is already in progress"})
            print("[controls audit] groq_keys_job_started=true", flush=True)
            return self.reply(202, {"job_id": job_id, "state": "queued"})
        if path == "/api/literouter-keys":
            try:
                job_id = begin_literouter_job(self.body(limit=2048))
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            if job_id is None:
                return self.reply(409, {"error": "A settings change is already in progress"})
            print("[controls audit] literouter_keys_job_started=true", flush=True)
            return self.reply(202, {"job_id": job_id, "state": "queued"})
        if path == "/api/inference-providers":
            try:
                job_id = begin_inference_job(self.body(limit=2048))
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            if job_id is None:
                return self.reply(409, {"error": "A settings change is already in progress"})
            print("[controls audit] inference_job_started=true", flush=True)
            return self.reply(202, {"job_id": job_id, "state": "queued"})
        if path == "/api/bot-config":
            values = self.body()
            try:
                job_id = begin_bot_config_job(values)
            except ValueError as error:
                return self.reply(400, {"error": str(error)})
            if job_id is None:
                return self.reply(409, {"error":
                    "A settings change is already in progress. Wait for it to finish."})
            print("[controls audit] bot_config_job_started=true", flush=True)
            return self.reply(202, {"job_id": job_id, "state": "queued"})
        if path != "/api/action":
            return self.reply(404, {"error": "Not found"})
        body = self.body()
        action = body.get("action") if body else None
        if action not in ACTIONS:
            return self.reply(400, {"error": "Unsupported action"})
        try:
            result = compose(*ACTIONS[action], timeout=90)
        except subprocess.TimeoutExpired:
            return self.reply(504, {"error": "Action timed out"})
        print("[controls audit] action=" + str(action) +
              " success=" + str(result.returncode == 0), flush=True)
        return self.reply(200 if result.returncode == 0 else 502,
                          {"ok": result.returncode == 0, "action": action})


if __name__ == "__main__":
    PASSWORD = load_password()
    print("[controls] listening on 127.0.0.1:8099", flush=True)
    ThreadingHTTPServer(("127.0.0.1", 8099), Handler).serve_forever()
