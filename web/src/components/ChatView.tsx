import { useEffect, useRef, useState } from "react";
import { getHistory, sendChat, synthesizeTts } from "../api";
import { playPcm48k } from "../audio";
import type { HistoryMessage } from "../types";

interface ChatMessage {
  role: string;
  content: string;
}

export function ChatView() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [speakingIndex, setSpeakingIndex] = useState<number | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const history = await getHistory();
        if (cancelled) return;
        const restored = history.messages
          .filter((m: HistoryMessage) => m.role === "user" || m.role === "assistant")
          .map((m: HistoryMessage) => ({ role: m.role, content: m.content }));
        setMessages(restored);
      } catch (err) {
        if (!cancelled) setLoadError(err instanceof Error ? err.message : "Failed to load history");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const list = listRef.current;
    if (list) list.scrollTop = list.scrollHeight;
  }, [messages, sending]);

  async function handleSend(event: React.FormEvent) {
    event.preventDefault();
    const text = draft.trim();
    if (!text || sending) return;

    setDraft("");
    setSending(true);
    setSendError(null);
    setMessages((prev) => [...prev, { role: "user", content: text }]);

    try {
      const result = await sendChat(text);
      setMessages((prev) => [...prev, { role: "assistant", content: result.text }]);
    } catch (err) {
      setSendError(err instanceof Error ? err.message : "Failed to send message");
    } finally {
      setSending(false);
      inputRef.current?.focus();
    }
  }

  async function handleSpeak(index: number, text: string) {
    if (speakingIndex !== null) return;
    setSpeakingIndex(index);
    try {
      const result = await synthesizeTts(text);
      if (result.audio) {
        await playPcm48k(result.audio);
      }
    } catch {
      // TTS is a bonus; failures shouldn't disrupt the chat.
    } finally {
      setSpeakingIndex(null);
    }
  }

  if (loading) {
    return (
      <div className="flex h-full items-center justify-center" role="status" aria-label="Loading conversation">
        <div className="space-y-3" aria-busy="true">
          <div className="h-4 w-48 animate-pulse rounded bg-ink-700" />
          <div className="h-4 w-36 animate-pulse rounded bg-ink-700" />
        </div>
      </div>
    );
  }

  if (loadError) {
    return (
      <div className="flex h-full items-center justify-center px-4" role="alert">
        <div className="max-w-sm text-center">
          <p className="text-sm text-rose-alert">{loadError}</p>
          <button
            type="button"
            onClick={() => window.location.reload()}
            className="mt-4 rounded-md bg-ink-700 px-4 py-2 text-sm hover:bg-ink-600"
          >
            Retry
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col">
      <div
        ref={listRef}
        className="flex-1 space-y-3 overflow-y-auto py-4"
        role="log"
        aria-label="Conversation with Tsuki"
        aria-live="polite"
      >
        {messages.length === 0 && (
          <div className="py-16 text-center">
            <p className="text-sm text-mist-400">Nothing here yet.</p>
            <p className="mt-1 text-sm text-mist-500">Say hi to Tsuki below.</p>
          </div>
        )}
        {messages.map((message, index) => (
          <div
            key={index}
            className={`flex ${message.role === "user" ? "justify-end" : "justify-start"}`}
          >
            <div
              className={`max-w-[80%] rounded-lg px-4 py-2.5 text-sm leading-relaxed ${
                message.role === "user"
                  ? "bg-moon-400 text-ink-950"
                  : "border border-ink-700 bg-ink-800 text-mist-100"
              }`}
            >
              <p className="whitespace-pre-wrap">{message.content}</p>
              {message.role === "assistant" && (
                <button
                  type="button"
                  onClick={() => handleSpeak(index, message.content)}
                  disabled={speakingIndex !== null}
                  className="mt-1.5 inline-flex items-center gap-1.5 text-xs text-mist-400 hover:text-moon-300 disabled:opacity-50"
                  aria-label={speakingIndex === index ? "Speaking" : "Play this reply out loud"}
                >
                  <SpeakerIcon className="h-3.5 w-3.5" />
                  {speakingIndex === index ? "Speaking…" : "Listen"}
                </button>
              )}
            </div>
          </div>
        ))}
        {sending && (
          <div className="flex justify-start">
            <div className="rounded-lg border border-ink-700 bg-ink-800 px-4 py-3" role="status">
              <span className="sr-only">Tsuki is thinking</span>
              <span className="flex gap-1" aria-hidden="true">
                <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-mist-400" />
                <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-mist-400 [animation-delay:150ms]" />
                <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-mist-400 [animation-delay:300ms]" />
              </span>
            </div>
          </div>
        )}
      </div>

      {sendError && (
        <p role="alert" className="pb-2 text-sm text-rose-alert">
          {sendError}
        </p>
      )}

      <form onSubmit={handleSend} className="flex gap-2 pb-4">
        <label htmlFor="chat-input" className="sr-only">
          Message to Tsuki
        </label>
        <textarea
          ref={inputRef}
          id="chat-input"
          rows={1}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void handleSend(e);
            }
          }}
          placeholder="Type a message… (Enter to send, Shift+Enter for a new line)"
          className="flex-1 resize-none rounded-md border border-ink-600 bg-ink-900 px-3 py-2 text-sm text-mist-100 placeholder:text-mist-500"
        />
        <button
          type="submit"
          disabled={sending || draft.trim().length === 0}
          className="rounded-md bg-moon-400 px-4 py-2 text-sm font-medium text-ink-950 transition-colors hover:bg-moon-300 disabled:opacity-50"
        >
          Send
        </button>
      </form>
    </div>
  );
}

function SpeakerIcon({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" className={className} aria-hidden="true">
      <path d="M13 4.5v15a1 1 0 0 1-1.64.77L6.9 16.5H4a1 1 0 0 1-1-1v-7a1 1 0 0 1 1-1h2.9l4.46-3.77A1 1 0 0 1 13 4.5Zm4.5 2.6a1 1 0 0 1 1.4.16A8.9 8.9 0 0 1 20.5 13a8.9 8.9 0 0 1-1.6 5.74 1 1 0 1 1-1.56-1.25A6.9 6.9 0 0 0 18.5 13a6.9 6.9 0 0 0-1.16-3.74 1 1 0 0 1 .16-1.4Zm-2.1 2.8a1 1 0 0 1 1.34.44A4.9 4.9 0 0 1 17.4 13c0 1-.27 1.9-.76 2.66a1 1 0 1 1-1.7-1.05c.3-.48.46-1.03.46-1.61s-.16-1.13-.46-1.61a1 1 0 0 1 .44-1.34Z" />
    </svg>
  );
}
