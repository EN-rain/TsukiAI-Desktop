# TsukiAI VPS controls

`controls.tsukihime.dpdns.org` uses the existing `tsukis-bedroom` Cloudflare
tunnel and routes to `http://127.0.0.1:8099` on the VPS. The service binds only
to loopback and runs as the unprivileged `azureuser` systemd service
`tsuki-controls.service`.

## Security

Login uses the existing `TSUKI_WEB_PASSWORD` from `/opt/tsukiai/.env` unless a
service-level `TSUKI_CONTROLS_PASSWORD` override is explicitly configured. The
panel issues a Secure, HttpOnly, SameSite=Strict cookie and requires both a
valid session and CSRF token for state-changing requests. Optional **Remember this
browser** creates a separate 30-day, browser-scoped opaque credential in an
HttpOnly, Secure, SameSite=Strict cookie; only its SHA-256 digest and expiry
are stored in the private mode-600 `.controls-trusted-devices.json`. This
survives service restarts and requires no machine fingerprint or JavaScript
storage. Sign out revokes the browser credential; changing the controls password
revokes all remembered browsers and normal sessions. Limit: 12 remembered
browsers. Do not select this on shared computers. Login attempts are
rate-limited. The UI never exposes the Discord token, API key, notebook tunnel
credentials, full environment, raw Docker socket, arbitrary shell, or raw logs.

First login accepts the existing `TSUKI_WEB_PASSWORD`. Changing the controls
password then stores a salted PBKDF2-SHA256 hash in a private mode-600
`/opt/tsukiai/.controls-auth.json`. The new password is **controls-only**:
the API's separate `TSUKI_WEB_PASSWORD`, API container and Discord bot are
untouched. All controls sessions are invalidated immediately. Passwords may
contain spaces, punctuation or Unicode and must be 12–128 characters.

## TTS provider

The Voice Routing control writes `TSUKI_TTS_PROVIDER` to the bridge `.env` and
recreates only the Discord bridge. Values are:

- `molab` — use/poll only `molab-tts.tsukihime.dpdns.org`.
- `colab` — use/poll only `colab-tts.tsukihime.dpdns.org`.
- `off` — disable Discord TTS; text replies remain available.

There is no Molab↔Colab TTS fallback. Controls checks only the selected
notebook endpoint; the unselected provider is displayed as standby and is not
polled by the dashboard. Molab and Colab remain manually started, standalone
notebook runtimes on separate Cloudflare tunnels.

## LLM API providers and Groq keys

**AI & Personality → LLM API providers** is separate from voice/TTS routing.
It edits the existing C# inference configuration:
`TSUKI_USE_MULTIPLE_PROVIDERS=true` and ordered
`TSUKI_MULTI_PROVIDERS_CSV`. Supported names in the actual C# provider
switcher are `groq,cerebras,gemini,github,mistral,literouter`. Add, remove and reorder
these names, then **Save LLM providers**. The C# API can advance through the
saved order after rate limits. Saving also resets the previously misapplied
`TSUKI_LLM_PROVIDER` notebook override to `api`; it recreates only the API
container and leaves `TSUKI_TTS_PROVIDER` and the bridge unchanged.

Groq and LiteRouter each have an independent model selector. **Available
models** calls only the provider's authenticated read-only
`GET https://api.groq.com/openai/v1/models` or
`GET https://api.literouter.com/v1/models`. No sample prompts, chat
completions, generation or claims of model readiness are involved. Saving
requires the exact ID to appear in the account-specific catalog and stores
`TSUKI_GROQ_MODEL` or `TSUKI_LITEROUTER_MODEL` in the private root
`.env`; only the C# API is recreated. LiteRouter has no fabricated default:
add its key, read the model list, select a model, then add that provider to
the configured inference order. IDs ending in `:free` are listed first;
the dashboard never silently selects a paid model. Configure the LiteRouter
key in the dedicated model panel even before enabling that provider. The
server refuses to activate LiteRouter in the provider order without an API
key and a selected catalog-backed model, avoiding a broken first request.

For Cerebras, Gemini, GitHub and Mistral the panel can set or clear the
corresponding `TSUKI_<PROVIDER>_API_KEY` in the private root `.env`.
These providers currently accept one key each (they are not represented as
multi-key rotators). Groq and LiteRouter each have a scrollable, masked
multi-key list with individual **Add key** and **Remove** controls.
LiteRouter uses `TSUKI_LITEROUTER_API_KEYS` in the root `.env`; an existing
`TSUKI_LITEROUTER_API_KEY` is migrated into that pool when a key is added
or removed, then cleared so a removed key cannot return as an invisible
fallback. Changes recreate only the API container. When an external
`TSUKI_LITEROUTER_API_KEYS_FILE` is configured, the dashboard reports
that the external file must be managed separately instead of changing
ignored fallback settings. The final key cannot be deleted while LiteRouter
is in the active provider order.

Groq uses a dedicated scrollable, masked key list because its existing
LLM/STT rotation pool accepts multiple keys.
The list reads the host file mounted by `GROQ_KEYS_HOST_PATH` (or
`discord-voice-bridge/groq.keys` by default); when adding a key, legacy
inline Groq keys are migrated to that same file. The controls process
recreates API and bridge after a Groq key change; it restores the prior file
if recreation fails. Only a SHA-256 key identifier and an abbreviated
prefix/suffix are ever sent to the browser. No plaintext keys are returned
in inventory or daily usage responses.

**Total API calls today** counts actual outbound Groq API HTTP attempts,
including key-rotation retries, across C# LLM/STT and Discord bridge
STT/classifier. The two services persist separate non-secret JSONL records
to the shared `tsuki-data:/data` volume. The controls service reads those
fixed paths through read-only Docker exec calls and aggregates by UTC date
and SHA-256 key ID. Until *both* trackers have covered the full UTC day,
the total is **Unavailable** and the UI separately labels the attempts it
has observed. Historic calls before instrumentation cannot be recovered.
The counter measures attempted HTTP requests, not successful replies or
provider-reported billing usage.

## Existing personality and optional override

**AI & Personality → Tsuki’s personality** loads the existing VPS
`assets/Modelfile` `SYSTEM` text. This is the preexisting Tsuki persona—not a
blank newly invented field. The editor can read and edit its complete supported
SYSTEM block (up to 4,000 characters). `FROM`, `PARAMETER`, and the rest of
the Modelfile stay byte-for-byte intact. The API prompt builder now uses the
full SYSTEM text, rather than extracting only the `PERSONALITY`, `SPEECH`,
no-question and no-emoji fragments. Discord voice/text and the API/Molab/Colab
inference routes all receive the persona in the API system message.

Saving the existing source uses authenticated `GET /api/personality` and a
CSRF-protected `POST /api/personality`, plus the tracked configuration job.
The original Modelfile is privately backed up; write and API recreation are
transactional, and a failed restart restores the original file. The API
container receives `./assets/Modelfile:/app/assets/Modelfile:ro` so it reads
the host source on recreation without rebuilding the image for each edit.

The optional **Additional personality override** is collapsed under the main
editor. This maintains the previously introduced
`TSUKI_PERSONALITY_INJECTION_B64` setting independently of the actual
Modelfile. Clear and save the override to rely only on the existing persona.
Base64 encodes private text in the root mode-600 `.env` but is not encryption.
Both editable sources are returned only by the authenticated personality
endpoint; general `/api/status`, bridge configuration, and persistent
conversation memory do not expose them. API output format and safety rules
remain intact. The interface does not claim it has generated an inference
test response.

The sidebar now separates Discord & Voice from AI & Personality. The overview
does not display duplicate bridge/voice/health panels under the snapshot; the
voice route includes its own status instead of another full card, and voice
analytics are collapsed by default. Force-refresh a previously opened browser
to discard older dashboard assets.

## Conversation awareness settings

Under **AI & Personality → Conversation awareness**, native selectors separately set
voice and eligible text chat to wake-only, context-aware, or classifier-enabled
proactive mode; aliases and 0–30000 ms follow-up timeout are saved with the
existing authenticated bridge-config job. The panel displays process-lifetime
measured gate decisions, classifier requests/failures, and measured latency only
when the updated bridge reports them. The optional Groq classifier model ID
can now be edited in the same settings form; clearing it is allowed only after
both modes leave proactive. The existing private Groq key pool must still be
configured separately. A model ID does **not** certify key access, successful
inference, or proactive accuracy; classifier errors abstain.

`TEXT_REPLY_MODE=mention` still limits text replies to actual bot @mentions or
verified replies to Tsuki. Set `any` to consider unmentioned chat messages through
the engagement gate. The text voice keyword list changes **audio delivery only**
and does not bypass reply authorization. Focus and allowed guild/channels take
precedence. Follow-ups open only after a successful reply and expire on timeout,
focus change, disconnect or bridge recreation. The VPS API, bridge, and controls
source was deployed on 2026-09-21; labeled multi-speaker Discord conversation
accuracy and live notebook inference remain unverified.

## Dashboard functions

The dashboard shows the actual Docker Compose state, per-container CPU/memory,
host load/RAM/disk/uptime, Discord guild/channel configuration, selected TTS
provider readiness, and the current Discord bot avatar. It supports:

- restart/reconnect the bridge; it is always enabled and starts with the VPS;
- restart API, Caddy, and ChromaDB;
- detect the bot's **actual joined Discord guilds** via Discord's API; use the
  plus button to copy the exact invite URL; select a guild to inspect its
  voice/text channels and current VPS settings. Saving a
  different guild switches this single-server bridge to that guild, after
  validation of both channels against Discord's current channel list;
- validated voice/text channel, text reply, text voice, and strict
  TTS-provider settings; Tsuki also reads and replies in the attached text
  chat of its selected Discord voice channel (under the same reply/keyword
  rules), provided it has View Channel, Send Messages and Read History there;
- **live voice controls** within the active server: check the actual ready
  Discord voice connection, leave voice without recreating the bridge, and
  type up to 280 characters for direct custom speech in the connected channel.
  Speech uses only the selected Molab/Colab notebook, does not ask the LLM
  for a reply, and waits for playback completion through a tracked job. TTS
  is unavailable when voice is disconnected or provider is Off. The saved
  default channel is unchanged by Leave; reconnecting/restarting the bridge
  may auto-join it. Use Discord `/tjoin` to join again;
- preview and replace the Discord bot avatar (PNG/JPEG/WebP, max 1 MiB);
- change the controls-only password and force re-login.

Configuration updates return HTTP 202 with a tracked job ID; the UI polls
read-only job status until Docker recreation finishes instead of keeping the
write request open. Repeated identical settings do not recreate the bridge,
and a failed status poll never triggers another configuration POST.

The private HTML is an unpersonalized static shell served with
`Cache-Control: public, max-age=0, must-revalidate, no-transform` to prevent
Cloudflare's automatic Web Analytics script injection from conflicting with
the strict same-origin CSP. Authenticated JSON stays private and no-store.

Joined Discord servers and channel lists are cached for 60 seconds so clicking
server cards does not exhaust Discord's REST allowance. The Detect servers
button can recheck after a 15-second minimum interval; on HTTP 429 the panel
uses previously verified membership instead of spamming retries.

Container parsing accepts Compose JSON arrays and line-delimited JSON and
falls back to the read-only Docker container list when Compose emits an
incomplete snapshot. Unknown is displayed as unknown, not as an outage.
The service never exposes Docker socket access or its raw output publicly.

## Dashboard layout and status contract

This authenticated surface is for operating the system, not presenting a
marketing page. The first viewport answers three questions using only the
existing authenticated status response: is the bridge container running, is
the **selected** TTS provider ready, and how many containers report running?
The action notice prioritizes stopped bridge, selected-provider outage, then
other container issues; Off is labelled intentional rather than an outage.
A Docker container being `running` does **not** verify Discord gateway or
voice-channel connectivity. The unselected TTS provider is **not polled**.

Voice selection and bridge controls appear before VPS consumption details.
Joined servers and their configurable channels have a dedicated Discord
section. Avatar replacement and password change are keyboard-operable native
disclosures in the lower settings section. Status
colors reinforce state text; they are never the sole signal. The near-black design
uses muted semantic states, restrained motion, visible keyboard focus, responsive
structural reflow, and no invented charts or dummy measurements.
No new TTS routing is introduced. New authenticated endpoints list joined
guilds and their Discord channel names, without exposing the bot token.

Text reply mode controls which messages are candidates for a text/AI response:
`mention` requires a genuine bot mention or confirmed reply-to-bot, while `any`
considers all eligible non-bot messages in the chosen text channel or attached
voice-channel chat through the engagement gate (not an automatic reply to all). The separate text
channel can be left blank. Text-chat voice is independent: `keywords` speaks
only when the **incoming** text includes a configured whole trigger word/phrase,
and `off` suppresses automatic chat voice messages. The old `all` option is
migrated to `keywords`. The
keyword list is editable, validated and saved with the bot settings; the UI
marks unsaved changes. The default mode is `keywords`, with trigger words
`voice,say it,say that,speak,talk,sing,read it`.

All bot configuration writes preserve unedited `.env` values, save a private
backup, and roll back if the bridge cannot be recreated. Discord `/tjoin` and `/tleave` remain supported. The dashboard's live Leave
and custom-TTS requests are authenticated + CSRF-checked, restricted to the
currently configured guild, and forwarded through a fixed Docker Compose exec
command to the bridge HTTP listener bound to container loopback. The TTS text
travels on stdin, not as a Docker command argument. The controls HTTPS API
requires login and CSRF protection; the bridge's own HTTP listener is not
publicly exposed. Bot tokens, shell input, and arbitrary Docker commands are
not exposed.
The dashboard reports bridge connectivity as unknown if the internal probe
fails, rather than treating the saved default voice channel as a live session.
