import { useEffect, useState } from "react";
import { getAuthStatus, login, logout, unauthorizedEvent } from "./api";
import { LoginView } from "./components/LoginView";
import { ChatView } from "./components/ChatView";
import { VoiceView } from "./components/VoiceView";
import { SettingsView } from "./components/SettingsView";
import { MemoryView } from "./components/MemoryView";

type Tab = "chat" | "voice" | "memory" | "settings";
type Phase = "loading" | "login" | "ready";

const TABS: { id: Tab; label: string }[] = [
  { id: "chat", label: "Chat" },
  { id: "voice", label: "Voice" },
  { id: "memory", label: "Memory" },
  { id: "settings", label: "Settings" },
];

export function App() {
  const [phase, setPhase] = useState<Phase>("loading");
  const [publicMode, setPublicMode] = useState(true);
  const [tab, setTab] = useState<Tab>("chat");

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const status = await getAuthStatus();
        if (cancelled) return;
        setPublicMode(status.public_mode);
        if (status.authenticated) {
          setPhase("ready");
        } else if (!status.public_mode) {
          // Local mode: no password needed, just pick up the session cookie.
          await login("");
          if (!cancelled) setPhase("ready");
        } else {
          setPhase("login");
        }
      } catch {
        if (!cancelled) setPhase("login");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const onUnauthorized = () => setPhase("login");
    window.addEventListener(unauthorizedEvent, onUnauthorized);
    return () => window.removeEventListener(unauthorizedEvent, onUnauthorized);
  }, []);

  if (phase === "loading") {
    return (
      <div className="flex h-full items-center justify-center" role="status" aria-label="Loading TsukiAI">
        <MoonMark className="h-10 w-10 animate-pulse text-moon-400" />
      </div>
    );
  }

  if (phase === "login") {
    return (
      <LoginView
        onSuccess={() => setPhase("ready")}
        onAutoLocal={async () => {
          await login("");
          setPhase("ready");
        }}
      />
    );
  }

  return (
    <div className="flex h-full flex-col">
      <header className="border-b border-ink-700 bg-ink-950">
        <div className="mx-auto flex w-full max-w-4xl items-center gap-4 px-4 py-3">
          <MoonMark className="h-7 w-7 shrink-0 text-moon-400" />
          <h1 className="text-lg font-semibold tracking-wide">TsukiAI</h1>
          <nav aria-label="Primary" className="ml-auto">
            <ul className="flex flex-wrap items-center gap-1">
              {TABS.map(({ id, label }) => (
                <li key={id}>
                  <button
                    type="button"
                    aria-current={tab === id ? "page" : undefined}
                    onClick={() => setTab(id)}
                    className={`rounded-md px-3 py-1.5 text-sm transition-colors ${
                      tab === id
                        ? "bg-ink-700 text-moon-300"
                        : "text-mist-400 hover:bg-ink-800 hover:text-mist-100"
                    }`}
                  >
                    {label}
                  </button>
                </li>
              ))}
              {publicMode && (
                <li>
                  <button
                    type="button"
                    onClick={async () => {
                      await logout().catch(() => undefined);
                      setPhase("login");
                    }}
                    className="ml-2 rounded-md px-3 py-1.5 text-sm text-mist-400 hover:bg-ink-800 hover:text-mist-100"
                  >
                    Sign out
                  </button>
                </li>
              )}
            </ul>
          </nav>
        </div>
      </header>

      <main className="mx-auto w-full max-w-4xl flex-1 overflow-hidden px-4">
        {tab === "chat" && <ChatView />}
        {tab === "voice" && <VoiceView />}
        {tab === "memory" && <MemoryView />}
        {tab === "settings" && <SettingsView />}
      </main>
    </div>
  );
}

export function MoonMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" className={className} aria-hidden="true">
      <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z" />
    </svg>
  );
}
