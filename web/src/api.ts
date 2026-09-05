import type {
  AuthStatus,
  ChatTurnResult,
  HistoryMessage,
  MemoryHit,
  TranscriptionResult,
  VoiceProcessResult,
  WebSettings,
} from "./types";

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

export const unauthorizedEvent = "tsuki:unauthorized";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    credentials: "same-origin",
    headers: init?.body && !(init.body instanceof FormData)
      ? { "Content-Type": "application/json", ...init?.headers }
      : init?.headers,
    ...init,
  });

  if (response.status === 401) {
    window.dispatchEvent(new Event(unauthorizedEvent));
    throw new ApiError(401, "Not signed in");
  }

  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try {
      const body = await response.json();
      if (body?.error) message = body.error;
    } catch {
      // non-JSON error body; keep the generic message
    }
    throw new ApiError(response.status, message);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export function getAuthStatus() {
  return request<AuthStatus>("/auth/status");
}

export function login(password: string) {
  return request<{ status: string; mode: string }>("/auth/login", {
    method: "POST",
    body: JSON.stringify({ password }),
  });
}

export function logout() {
  return request<{ status: string }>("/auth/logout", { method: "POST" });
}

export function getHistory() {
  return request<{ messages: HistoryMessage[]; last_updated: string | null }>("/api/history");
}

export function sendChat(text: string) {
  return request<ChatTurnResult>("/api/chat", {
    method: "POST",
    body: JSON.stringify({ text }),
  });
}

export function transcribeAudio(blob: Blob, language?: string) {
  const form = new FormData();
  const name = blob.type.includes("webm") ? "audio.webm" : "audio.wav";
  form.append("file", blob, name);
  if (language) form.append("language", language);
  return request<TranscriptionResult>("/api/voice/stt-audio", {
    method: "POST",
    body: form,
  });
}

export function processVoice(text: string) {
  return request<VoiceProcessResult>("/api/voice/process", {
    method: "POST",
    body: JSON.stringify({ text }),
  });
}

export function synthesizeTts(text: string) {
  return request<{ correlation_id: string; audio: string | null }>("/api/voice/test-tts", {
    method: "POST",
    body: JSON.stringify({ text }),
  });
}

export function getSettings() {
  return request<WebSettings>("/api/settings");
}

export function updateSettings(patch: unknown) {
  return request<{ status: string }>("/api/settings", {
    method: "PUT",
    body: JSON.stringify(patch),
  });
}

export function searchMemory(q: string, k = 5) {
  return request<MemoryHit[]>(`/api/memory/search?q=${encodeURIComponent(q)}&k=${k}`);
}

export function addMemory(text: string, source = "web") {
  return request<{ status: string }>("/api/memory/add", {
    method: "POST",
    body: JSON.stringify({ text, source }),
  });
}
