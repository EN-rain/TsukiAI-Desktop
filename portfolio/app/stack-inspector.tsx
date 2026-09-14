"use client";

import { useState } from "react";

const STACK = {
  bridge: {
    title: "Discord voice bridge",
    copy: "The doorway into the room. It turns live Discord audio into clean, intentional turns for Tsuki to understand.",
    detail: "Node.js / VAD / @discordjs/voice",
  },
  brain: {
    title: "Brain and memory",
    copy: "The quiet middle layer. It keeps her persona coherent and reaches for older context only when it helps.",
    detail: "ASP.NET Core 8 / provider failover / ChromaDB",
  },
  voice: {
    title: "Voice",
    copy: "A short path from words to presence: transcription, translation, and synthesis tuned for conversation.",
    detail: "Groq Whisper / Qwen3-TTS / DeepL",
  },
  home: {
    title: "Home",
    copy: "A self-hosted room in Azure, composed with Docker and kept ready for the next late-night conversation.",
    detail: "Azure Tsuki's Bedroom / Docker Compose / always on",
  },
} as const;

const STACK_KEYS = ["bridge", "brain", "voice", "home"] as const;
type StackKey = (typeof STACK_KEYS)[number];

export default function StackInspector() {
  const [stackKey, setStackKey] = useState<StackKey>("bridge");
  const currentStack = STACK[stackKey];

  return (
    <div className="stack-layout">
      <ul className="stack-list">
        {STACK_KEYS.map((key) => {
          const selected = key === stackKey;
          return (
            <li key={key}>
              <button
                className={`stack-trigger${selected ? " is-selected" : ""}`}
                type="button"
                data-stack={key}
                aria-expanded={selected}
                aria-controls="stack-inspector"
                onClick={() => setStackKey(key)}
              >
                <span>
                  <strong>{STACK[key].title}</strong>
                  <small>{STACK[key].detail}</small>
                </span>
                <span className="stack-arrow" aria-hidden="true">&#8599;</span>
              </button>
            </li>
          );
        })}
      </ul>

      <aside className="stack-inspector" id="stack-inspector" aria-live="polite">
        <span className="detail-kicker">selected layer</span>
        <h3 id="stack-title">{currentStack.title}</h3>
        <p id="stack-copy">{currentStack.copy}</p>
      </aside>
    </div>
  );
}
