import { useEffect, useState } from "react";
import { getSettings, updateSettings } from "../api";
import type { WebSettings } from "../types";

interface EditableSettings {
  model_name: string;
  reply_tone_preset: string;
  generation: WebSettings["generation"];
  tts: WebSettings["tts"];
  translation: WebSettings["translation"];
  memory: WebSettings["memory"];
  stt: WebSettings["stt"];
}

export function SettingsView() {
  const [settings, setSettings] = useState<EditableSettings | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const loaded = await getSettings();
        if (!cancelled) setSettings(toEditable(loaded));
      } catch (err) {
        if (!cancelled) setLoadError(err instanceof Error ? err.message : "Failed to load settings");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  function toEditable(s: WebSettings): EditableSettings {
    return {
      model_name: s.model_name,
      reply_tone_preset: s.reply_tone_preset,
      generation: { ...s.generation },
      tts: { ...s.tts },
      translation: { ...s.translation },
      memory: { ...s.memory },
      stt: { ...s.stt },
    };
  }

  async function handleSave(event: React.FormEvent) {
    event.preventDefault();
    if (!settings || saving) return;
    setSaving(true);
    setSaved(false);
    setSaveError(null);
    try {
      await updateSettings(settings);
      setSaved(true);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Failed to save settings");
    } finally {
      setSaving(false);
    }
  }

  if (loading) {
    return (
      <div className="flex h-full items-center justify-center" role="status" aria-label="Loading settings">
        <div className="h-4 w-48 animate-pulse rounded bg-ink-700" aria-busy="true" />
      </div>
    );
  }

  if (loadError || !settings) {
    return (
      <div className="flex h-full items-center justify-center" role="alert">
        <p className="text-sm text-rose-alert">{loadError ?? "Settings unavailable"}</p>
      </div>
    );
  }

  const patch = (fn: (draft: EditableSettings) => void) => {
    setSettings((prev) => {
      if (!prev) return prev;
      const next = toEditable({ ...(prev as WebSettings) } as WebSettings);
      fn(next);
      return next;
    });
  };

  return (
    <form onSubmit={handleSave} className="h-full space-y-6 overflow-y-auto py-4 pb-8">
      <section aria-labelledby="settings-model">
        <h2 id="settings-model" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Model
        </h2>
        <div className="mt-3 grid gap-4 sm:grid-cols-2">
          <Field label="Model name" hint="Provider model ID, e.g. openai/gpt-oss-120b">
            <input
              type="text"
              value={settings.model_name}
              onChange={(e) => patch((d) => (d.model_name = e.target.value))}
              className={inputClass}
            />
          </Field>
          <Field label="Reply tone preset" hint="How Tsuki sounds in text replies">
            <select
              value={settings.reply_tone_preset}
              onChange={(e) => patch((d) => (d.reply_tone_preset = e.target.value))}
              className={inputClass}
            >
              <option value="natural">natural</option>
              <option value="playful">playful</option>
              <option value="calm">calm</option>
              <option value="concise">concise</option>
            </select>
          </Field>
        </div>
      </section>

      <section aria-labelledby="settings-generation">
        <h2 id="settings-generation" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Generation
        </h2>
        <div className="mt-3 grid gap-4 sm:grid-cols-3">
          <Field label="Max tokens">
            <input
              type="number"
              min={1}
              value={settings.generation.max_tokens}
              onChange={(e) => patch((d) => (d.generation.max_tokens = Number(e.target.value)))}
              className={inputClass}
            />
          </Field>
          <Field label="Temperature">
            <input
              type="number"
              step={0.05}
              min={0}
              max={2}
              value={settings.generation.temperature}
              onChange={(e) => patch((d) => (d.generation.temperature = Number(e.target.value)))}
              className={inputClass}
            />
          </Field>
          <Field label="Top P">
            <input
              type="number"
              step={0.05}
              min={0}
              max={1}
              value={settings.generation.top_p}
              onChange={(e) => patch((d) => (d.generation.top_p = Number(e.target.value)))}
              className={inputClass}
            />
          </Field>
          <Field label="Top K">
            <input
              type="number"
              min={1}
              value={settings.generation.top_k}
              onChange={(e) => patch((d) => (d.generation.top_k = Number(e.target.value)))}
              className={inputClass}
            />
          </Field>
          <Field label="Repeat penalty">
            <input
              type="number"
              step={0.01}
              min={1}
              value={settings.generation.repeat_penalty}
              onChange={(e) => patch((d) => (d.generation.repeat_penalty = Number(e.target.value)))}
              className={inputClass}
            />
          </Field>
          <Field label="Max reply chars" hint="Caps the visible reply length">
            <input
              type="number"
              min={1}
              value={settings.generation.max_reply_chars}
              onChange={(e) => patch((d) => (d.generation.max_reply_chars = Number(e.target.value)))}
              className={inputClass}
            />
          </Field>
        </div>
      </section>

      <section aria-labelledby="settings-tts">
        <h2 id="settings-tts" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Voice (TTS)
        </h2>
        <div className="mt-3 grid gap-4 sm:grid-cols-3">
          <Field label="TTS mode">
            <select
              value={settings.tts.mode}
              onChange={(e) => patch((d) => (d.tts.mode = e.target.value))}
              className={inputClass}
            >
              <option value="LocalVoiceVox">VOICEVOX</option>
              <option value="CloudRemote">Cloud remote</option>
            </select>
          </Field>
          <Field label="VOICEVOX URL">
            <input
              type="url"
              value={settings.tts.voicevox_base_url}
              onChange={(e) => patch((d) => (d.tts.voicevox_base_url = e.target.value))}
              className={inputClass}
            />
          </Field>
          <Field label="Speaker style ID">
            <input
              type="number"
              min={0}
              value={settings.tts.speaker_style_id}
              onChange={(e) => patch((d) => (d.tts.speaker_style_id = Number(e.target.value)))}
              className={inputClass}
            />
          </Field>
        </div>
      </section>

      <section aria-labelledby="settings-translation">
        <h2 id="settings-translation" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Translation
        </h2>
        <div className="mt-3 space-y-2">
          <Toggle
            label="Translate TTS output to Japanese"
            checked={settings.translation.voice_translate_to_japanese}
            onChange={(v) => patch((d) => (d.translation.voice_translate_to_japanese = v))}
          />
          <Toggle
            label="Use DeepL for translation"
            checked={settings.translation.use_deepl}
            onChange={(v) => patch((d) => (d.translation.use_deepl = v))}
          />
          <Toggle
            label="Use DeepL free API"
            checked={settings.translation.use_deepl_free_api}
            onChange={(v) => patch((d) => (d.translation.use_deepl_free_api = v))}
          />
        </div>
      </section>

      <section aria-labelledby="settings-memory">
        <h2 id="settings-memory" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Memory
        </h2>
        <div className="mt-3 space-y-2">
          <Toggle
            label="Semantic memory (ChromaDB)"
            checked={settings.memory.semantic_memory_enabled}
            onChange={(v) => patch((d) => (d.memory.semantic_memory_enabled = v))}
          />
        </div>
      </section>

      <section aria-labelledby="settings-stt">
        <h2 id="settings-stt" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Speech recognition
        </h2>
        <div className="mt-3 grid gap-4 sm:grid-cols-2">
          <Field label="Language code" hint='"auto", "en", "ja", …'>
            <input
              type="text"
              value={settings.stt.language_code}
              onChange={(e) => patch((d) => (d.stt.language_code = e.target.value))}
              className={inputClass}
            />
          </Field>
        </div>
      </section>

      <p className="text-xs text-mist-500">
        API keys are configured on the server (environment variables) and are never exposed to the
        browser.
      </p>

      <div className="flex items-center gap-3">
        <button
          type="submit"
          disabled={saving}
          className="rounded-md bg-moon-400 px-4 py-2 text-sm font-medium text-ink-950 transition-colors hover:bg-moon-300 disabled:opacity-60"
        >
          {saving ? "Saving…" : "Save settings"}
        </button>
        {saved && (
          <p role="status" className="text-sm text-moon-300">
            Saved
          </p>
        )}
        {saveError && (
          <p role="alert" className="text-sm text-rose-alert">
            {saveError}
          </p>
        )}
      </div>
    </form>
  );
}

const inputClass =
  "mt-1.5 w-full rounded-md border border-ink-600 bg-ink-900 px-3 py-2 text-sm text-mist-100 placeholder:text-mist-500";

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <label className="block text-sm text-mist-400">
      {label}
      {children}
      {hint && <span className="mt-1 block text-xs text-mist-500">{hint}</span>}
    </label>
  );
}

function Toggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <label className="flex items-center gap-3 text-sm text-mist-100">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="h-4 w-4 accent-moon-400"
      />
      {label}
    </label>
  );
}
