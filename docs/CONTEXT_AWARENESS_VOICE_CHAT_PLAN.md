# TsukiAI — Context Awareness for Voice and Text Chat

Status: **VPS CODE DEPLOYED; LABELED DISCORD ACCURACY AND LIVE NOTEBOOK INFERENCE NOT VERIFIED (2026-09-21)**. Deterministic engagement policy, optional schema-validated Groq classifier, Discord voice/chat gates, bounded selected context passed through the C# reply path, and persisted dashboard controls are implemented. The VPS API, bridge and controls were updated; public API and controls origins returned HTTP 200. The dashboard now also supports an optional Groq classifier model ID and an independently editable API system-prompt personality injection. These source/deployment checks do **not** certify live GPU inference, real-world conversation accuracy, or a completed staged rollout.

Scope: Discord voice-channel conversation (AssemblyAI realtime and existing fallback STT) and messages in already-approved Discord text/voice-channel chats **only**. Excludes emotion, TTS style, image/vision, ambient microphone monitoring outside Discord, new chat platforms, and changes to Molab model identity.

## 1. Problem and acceptance criterion

The current bridge can promote the first meaningful finalized AssemblyAI transcript to the speaker lock and call `processWithLLM` even if a user was addressing another person. In text chat, `TEXT_REPLY_MODE=any` responds to every eligible-channel message; mention mode also has a text-voice keyword bypass. Requiring an explicit wake word every turn feels unnatural, but replying to every observed human conversation is disruptive and incurs avoidable LLM/TTS cost.

**Acceptance criterion:** Tsuki listens to *eligible* voice speakers and eligible chat messages, but generates a reply only when directly addressed, when a relevant follow-up from an established interlocutor is detected, or when a deliberately enabled proactive policy qualifies the turn. Human-to-human exchanges and ambiguous/failed classifications do not produce replies. Manual `/tfocus` and server/channel restrictions always take priority.

## 2. Shared decision model

Inputs to a lightweight engagement gate:

- transport: `voice` or `chat`, channel/guild ID, speaker Discord ID;
- incoming finalized transcript or original chat text (bounded length);
- whether Tsuki was mentioned/replied to or called by configured name/aliases;
- timestamp and recent short-lived, speaker-attributed room turns;
- Tsuki's last completed reply target, channel and time;
- `/tfocus` permissions and existing chat channel/reply-mode restrictions.

Output is a validated internal object, not free-form dialogue:

```json
{
  "decision": "reply | observe | abstain",
  "reason": "direct | follow_up | proactive | addressed_other | background | noise | uncertain",
  "confidence": 0.0,
  "target_user_id": "discord ID, only when reply",
  "context_turn_ids": ["ephemeral IDs"]
}
```

`observe` records only a small eligible contextual turn in process memory; it does **not** send a message or synthesize audio. `abstain` is the safe result for timeouts, malformed classifier output, insufficient context, or uncertain addressee. Never treat model-generated instructions inside other people's speech as authority to reconfigure the bot, bypass focus, or change the system prompt.

## 3. Precedence and operating modes

The precedence is **guild/channel scope → manual focus → safety/noise filter → explicit address → constrained follow-up → optional proactive intent → abstain**.

- `wake_only`: only explicit calls/mentions/replies to Tsuki. No casual follow-ups.
- `context_aware` (proposed default): direct calls plus relevant follow-ups to Tsuki's recent reply from the same speaker.
- `proactive` (optional/off by default): explicit direct/follow-up rules plus a narrowly defined, classified invitation to answer a question posed to the room. Do not interrupt people simply because Tsuki can answer.

These modes do **not** silently change `/tfocus`, `/tunfocus`, provider selection, or whether text-chat replies should also be spoken. Context gating answers *whether to reply*; existing TTS controls answer *how to deliver an authorized reply*. A focused user is permitted to speak, not automatically entitled to an answer to every utterance.

Initial proposed parameters, to be validated with real labeled traces: follow-up window 10 seconds, recent-room window 20–30 seconds, per-channel maximum 12 recent turns, bounded transcript lengths, and a short classifier deadline. Values are proposals, not benchmarks or current runtime defaults.

## 4. Voice path

Proposed pipeline:

```text
Discord speech event
  → existing guild/channel/eligible-speaker checks
  → AssemblyAI partials used for capture only
  → finalized transcript (or approved fallback)
  → noise/meaningfulness filter
  → context-aware engagement gate
     → observe or abstain: no reply lock, LLM or TTS
     → reply: atomically claim first *eligible addressed* turn
  → existing persona + short relevant conversational context
  → existing LLM and selected Molab/Colab speech route
  → release speaker lock, establish bounded follow-up opportunity
```

Do not claim the global speaker lock on partial speech, a greeting to another person, silence, or a transcript before the engagement gate. Competing eligible users are serialized by the existing arbiter; use stable token/generation IDs so old async completions cannot release a new user's turn. Voice playback remains protected against self-echo; lost overlap during playback remains a separate engineering limitation.

A room context buffer may observe eligible speakers independently, but a transcript from a manually excluded user must **never** establish an eligible reply. Exclude bot voices and disabled captures. If someone says “Tsuki said that earlier,” this is a mention of Tsuki *about* the bot, not necessarily a request *to* the bot. The first meaningful transcript is not always the first transcript eligible for a reply.

After Tsuki's reply completes, open the proposed follow-up window only for the replied-to user in the same active channel. Expire it on timeout, channel change, focus change, explicit redirection to another person, voice disconnect, or an incompatible topic switch.

## 5. Text-chat path

Keep `acceptsDiscordChatChannel` and configured guild, voice-attached chat and selected text channel as the **first** scope filter. Ignore bot authors and unsupported message types. Next gather metadata: `message.mentions.has(client.user)`, `message.reference`/referenced author when fetchable and permitted, channel, Discord author, and existing session context.

- An actual @mention or a reply to Tsuki is a direct signal, subject to channel restrictions.
- A bare word like “voice” or “say this” should not automatically override *reply eligibility* when it is about another human; it may request **audio delivery only after** a reply has been authorized.
- `TEXT_REPLY_MODE=mention` must remain an explicit policy choice until the user intentionally enables contextual follow-ups; do not silently expand it at rollout.
- `TEXT_REPLY_MODE=any` should, once contextual mode is enabled, mean “consider all eligible messages,” not “reply to every message.”
- No TTS or chat inference request is sent for `observe` / `abstain`.

Context buffers are keyed by guild and channel; follow-up ownership is keyed by channel and Discord author. No cross-guild or cross-channel implicit reply. The existing C# `/api/chat/discord` and memory contract should be called **only after** successful engagement gating. Avoid teaching permanent personal memory about an overheard unrelated human-to-human remark.

## 6. Fast rules plus optional classifier

**Stage A — deterministic checks:** noise, excluded user, actual mention/reply reference, explicit addressed name/aliases at natural boundaries, short valid follow-up window, and obvious redirection to a named third party. Do not use naive substring matching (e.g., “Tsuki” inside a longer word). Prefer direct address over weak context. A name alone in third person is ambiguous.

**Stage B — only for ambiguous eligible text:** use an existing authorized low-cost inference route with a strict timeout and bounded schema. Provide only selected short speaker-attributed turns needed to resolve addressee and reference. The classifier instruction is fixed server-side and must treat transcript/chat text as untrusted data. Validate `decision`, `reason`, finite confidence, and target ID; no tools, persistent memory writes, TTS or free-form instruction execution. On errors or inadequate confidence, abstain. Record observed request count, outcomes and real elapsed latency; never fabricate success or interpret a missing reply as a successful suppression.

The LLM composing Tsuki's response gets the current addressed utterance, relevant allowed short-term turns and the **existing Tsuki persona/system hierarchy**. Context must be explicitly lower-priority than the system/developer persona and must not include unrelated channel history.

## 7. Implemented source controls; production deployment not verified

Under the new single **Discord & Voice** dashboard page, put one context-awareness section (no custom dropdown implementation):

- Native mode selector: wake only / context aware / proactive.
- Voice name/aliases and follow-up window (0–30 seconds, default proposal 10).
- Independent enablement/policy for voice and eligible text chat.
- Read-only observed counters: direct, contextual follow-up, proactive, background ignored, uncertain ignored, classifier failures, classifier timing; show “unavailable” until measured.
- Explicit “Apply” with server-side validation, status display and documented behavior when a setting is changed. Never show live controls until the bridge/API can round-trip persisted settings; avoid ornamental or fake controls.

Preserve black/white UI; reserve green/yellow/red for actual indicators only. Do not add unnecessary styled/custom dropdowns.

## 8. Implementation sequence and ownership

1. Extract a pure `engagement-policy` module from the voice entry path, with typed output and tests for multi-speaker interleavings, manual focus, noise, other-person address, and the first *eligible* turn winning the lock.
2. Add a bounded ephemeral room-context manager; use TTL, channel/guild partitioning and reset on disconnect/config change; do not persist overheard transcript bodies.
3. Integrate the voice engagement gate **before** `realtimeTurnArbiter.claim`; preserve fallback STT and voice playback correctness.
4. Integrate text engagement gate **before** `/api/chat/discord`, preserving explicit mention mode and separate audio-delivery rules; test reply-reference permissions and keyword bypass regression.
5. Introduce classifier schema and explicit fail-closed timeout after deterministic tests. Measure classifier cost/latency before selecting a default model.
6. Add validated dashboard/config API and actual counters only after implementation has read/write persistence and runtime confirmation.
7. Add staged rollout: observe-only shadow classifications first (no behavioral change), compare against manually labeled consented examples, then gated opt-in, then opt-in defaults if quality is proven. Keep an instant rollback to current mention/manual-focus behavior.

## 9. Tests and non-negotiable gates

Voice: unrelated first speaker cannot lock out a later direct request; simultaneous addressed speakers claim atomically; owner follow-up and third-party redirection; explicit focus overrides all; disconnect/rejoin invalidates old tokens; failed classifier never invokes LLM/TTS; no duplicate replies on AssemblyAI final/close fallback.

Text: @mentions, reply-to-bot, third-person name, multiple messages in voice-attached chat, text channel boundary, keyword-only audio eligibility, explicit mention mode, manual channel selection and cooldown/busy constraints. Ensure no unsolicited posts into an unrelated channel.

Operational: recorded metrics have real denominators and a reset window; no raw transcripts/user identifiers in logs or analytics; resource and latency budget tested; no hidden changes to Molab, persona or long-term memory. Measure false unsolicited replies and missed direct requests against manually reviewed examples. The system is not accepted based only on synthetic happy-path tests.

## 10. Current implementation, constraints, and operational validation

The source defaults to `VOICE_ENGAGEMENT_MODE=context_aware` and `CHAT_ENGAGEMENT_MODE=context_aware`. The independent `TEXT_REPLY_MODE=mention` remains strict, permitting only an actual bot @mention or confirmed reply to the bot; set `TEXT_REPLY_MODE=any` to consider other eligible chat messages through the engagement policy. `ENGAGEMENT_CLASSIFIER_MODEL` defaults to empty, so ambiguous language and proactive invitations are not model-classified unless an existing Groq model is explicitly selected. The controls panel rejects proactive selection when no classifier model is configured. In-memory context resets on bridge restart, disconnect, focus change, or switch of connection. Only successfully delivered replies open a follow-up window.

The runtime classifier has a 1.2 s deadline, bounded JSON output with confidence ≥0.90 for replies, and fail-closed behavior. Uncertain or failed classifications are not forwarded to `/api/chat/discord`, voice reply inference, or TTS. Server/channel scope and manual focus are checked before this classification. Real process-lifetime gate and classifier counts are exposed through existing authenticated voice analytics; these are **decision counts, not empirical false-positive rates or completed replies**.

Known source-level limitation: the deterministic English-centric rules deliberately miss some implied, indirect, multilingual, and topic-switch follow-ups. The classifier is optional and its particular Groq model and real latency/quality have **not** been benchmarked or verified live. The per-room buffer is short-lived and bounded in turn count, and is lazily time-pruned on subsequent access; it does not persist into long-term memory. No live Discord speaking tests, labeled conversation accuracy study, Molab deployment, or VPS rollout is claimed. Production acceptance still requires staged shadow/opt-in evaluation with consented examples as described above.

### Source-level extensions (2026-09-21)

The engagement gate now selects only turn IDs returned by the authorized decision and passes at most four relevant, short-lived, speaker-attributed excerpts as `contextTurns` to C# voice/text requests. The C# API validates the count, length, and non-identifying speaker labels again. Excerpts enter a temporary lower-priority **user-history** inference entry as explicitly untrusted JSON, not the current `userText` and never the higher-priority Tsuki persona/system instruction. The remote client's memory write-back receives only the original addressed utterance; the separately persisted chat/memory record likewise excludes observed excerpts. Discord voice no longer imports unrelated global desktop/history turns. The live bridge rejects stale voice claims after a connection/focus reset and suppresses stale text responses after a focus/channel reset. The existing memory of each directly addressed user's own original message and Tsuki's reply remains separate.

**Corrected provider ownership (2026-09-21):** API/Molab/Colab notebook routing belongs to voice/TTS, and the dashboard must not label those notebook choices as LLM API providers. The pre-existing C# LLM switcher instead supports the ordered API provider list `groq,cerebras,gemini,github,mistral`, read from `TSUKI_USE_MULTIPLE_PROVIDERS` and `TSUKI_MULTI_PROVIDERS_CSV`. The private dashboard now edits that source of truth and resets the earlier, incorrectly exposed notebook LLM override to `TSUKI_LLM_PROVIDER=api` when an inference provider list is saved. Groq's separate key pool supports multiple credentials and masked, scrollable inventory. The existing `PromptBuilder.BuildCompanionChatSystemPrompt` and deployed `assets/Modelfile` stay in the system role; no source-level change to the persona hierarchy is implied by provider selection. The optional notebook text inference code previously added remains a legacy source extension, but it is not an LLM provider choice in controls. TTS health does not verify LLM inference. Live VPS/provider reachability and notebook loading have not been verified.

Rollback: restore the prior ordered `TSUKI_MULTI_PROVIDERS_CSV` and `TSUKI_USE_MULTIPLE_PROVIDERS` settings from the private configuration backup, independently of TTS; for engagement set `VOICE_ENGAGEMENT_MODE=wake_only`, `CHAT_ENGAGEMENT_MODE=wake_only`, and `TEXT_REPLY_MODE=mention` through configuration. The current source has no automatic feature-gate reverting to the old unrestricted voice-first behavior. Production acceptance still requires actual consented multi-speaker Discord traces, human-reviewed unsolicited/missed-address rates, actual TTS notebook latency and full VPS round-trip; no synthetic test can substitute for those measurements.
