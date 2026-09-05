import { useEffect, useRef, useState } from "react";
import { login } from "../api";
import { MoonMark } from "../App";

interface LoginViewProps {
  onSuccess: () => void;
  /** Called in local (no-password) mode instead of showing the form. */
  onAutoLocal: () => void | Promise<void>;
}

export function LoginView({ onSuccess, onAutoLocal }: LoginViewProps) {
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [localMode, setLocalMode] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    // Distinguish "wrong password" from "local mode, just enter": retry the
    // anonymous login once; if it fails, show the password form.
    let cancelled = false;
    (async () => {
      try {
        const result = await login("");
        if (cancelled) return;
        if (result.mode === "local") {
          setLocalMode(true);
        }
      } catch {
        // 401 in public mode — password required.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setError(null);
    try {
      if (localMode) {
        await onAutoLocal();
      } else {
        await login(password);
      }
      onSuccess();
    } catch {
      setError(localMode ? "Could not start a local session." : "That password didn't work. Try again.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="flex h-full items-center justify-center px-4">
      <form
        onSubmit={handleSubmit}
        className="w-full max-w-sm rounded-lg border border-ink-700 bg-ink-800 p-6"
        aria-labelledby="login-title"
      >
        <MoonMark className="mx-auto h-10 w-10 text-moon-400" />
        <h2 id="login-title" className="mt-4 text-center text-lg font-semibold">
          Welcome back
        </h2>
        <p className="mt-1 text-center text-sm text-mist-400">
          {localMode ? "TsukiAI is running locally." : "Sign in to talk to Tsuki."}
        </p>

        {!localMode && (
          <div className="mt-5">
            <label htmlFor="password" className="block text-sm text-mist-400">
              Password
            </label>
            <input
              ref={inputRef}
              id="password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="mt-1.5 w-full rounded-md border border-ink-600 bg-ink-900 px-3 py-2 text-sm text-mist-100 placeholder:text-mist-500"
              placeholder="Your TsukiAI password"
              required
            />
          </div>
        )}

        {error && (
          <p role="alert" className="mt-3 text-sm text-rose-alert">
            {error}
          </p>
        )}

        <button
          type="submit"
          disabled={submitting}
          className="mt-5 w-full rounded-md bg-moon-400 px-4 py-2 text-sm font-medium text-ink-950 transition-colors hover:bg-moon-300 disabled:opacity-60"
        >
          {submitting ? "Signing in…" : localMode ? "Enter" : "Sign in"}
        </button>
      </form>
    </div>
  );
}
