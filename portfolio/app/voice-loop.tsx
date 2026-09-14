"use client";

import { useEffect, useRef, useState } from "react";

const PIPELINE = {
  listen: {
    kicker: "voice input",
    title: "She listens",
    copy: "Voice activity detection splits the channel audio into turns, which are transcribed by Whisper.",
    signal: "ambient / always listening",
  },
  think: {
    kicker: "context engine",
    title: "She thinks",
    copy: "A persona-tuned LLM builds the reply, with semantic search over long-term memory when the moment calls for it.",
    signal: "memory / context intact",
  },
  speak: {
    kicker: "voice output",
    title: "She speaks",
    copy: "Her reply is synthesized into a natural voice and streamed back into the voice channel.",
    signal: "voice / ready to answer",
  },
} as const;

const PIPELINE_KEYS = ["listen", "think", "speak"] as const;
type PipelineKey = (typeof PIPELINE_KEYS)[number];

function announcePipeline(key: PipelineKey) {
  window.dispatchEvent(
    new CustomEvent("tsuki:pipeline", {
      detail: { signal: PIPELINE[key].signal },
    }),
  );
}

export default function VoiceLoop() {
  const [pipelineKey, setPipelineKey] = useState<PipelineKey>("listen");
  const [sequenceState, setSequenceState] = useState<"ready" | "running" | "complete">("ready");
  const [reduceMotion, setReduceMotion] = useState(false);
  const sequenceTimers = useRef<number[]>([]);

  useEffect(() => {
    const query = window.matchMedia("(prefers-reduced-motion: reduce)");
    const syncMotionPreference = () => setReduceMotion(query.matches);
    syncMotionPreference();
    query.addEventListener("change", syncMotionPreference);
    return () => query.removeEventListener("change", syncMotionPreference);
  }, []);

  useEffect(() => {
    return () => sequenceTimers.current.forEach((timer) => window.clearTimeout(timer));
  }, []);

  const currentPipeline = PIPELINE[pipelineKey];
  const activePipelineIndex = PIPELINE_KEYS.indexOf(pipelineKey);

  function clearSequence() {
    sequenceTimers.current.forEach((timer) => window.clearTimeout(timer));
    sequenceTimers.current = [];
  }

  function choosePipeline(nextKey: PipelineKey) {
    clearSequence();
    setPipelineKey(nextKey);
    setSequenceState("ready");
    announcePipeline(nextKey);
  }

  function runSequence() {
    clearSequence();
    setSequenceState("running");

    const delay = reduceMotion ? 40 : 620;
    PIPELINE_KEYS.forEach((nextKey, index) => {
      const timer = window.setTimeout(() => {
        setPipelineKey(nextKey);
        setSequenceState(index === PIPELINE_KEYS.length - 1 ? "complete" : "running");
        announcePipeline(nextKey);
      }, delay * index);
      sequenceTimers.current.push(timer);
    });
  }

  return (
    <div className="pipeline-shell">
      <ol className="pipeline" aria-label="Tsuki's voice loop">
        {PIPELINE_KEYS.map((key, index) => {
          const active = key === pipelineKey;
          const complete = sequenceState === "complete" || index < activePipelineIndex;
          return (
            <li className={`process-step${active ? " is-active" : ""}${complete ? " is-complete" : ""}`} data-step={key} key={key}>
              <button
                className="process-trigger"
                type="button"
                aria-expanded={active}
                aria-controls="pipeline-detail"
                onClick={() => choosePipeline(key)}
              >
                <span className="step-glyph" aria-hidden="true">
                  {index === 0 ? "◉" : index === 1 ? "✦" : "⌁"}
                </span>
                <span>{PIPELINE[key].title}</span>
                <span className="step-arrow" aria-hidden="true">&#8599;</span>
              </button>
            </li>
          );
        })}
      </ol>

      <article className="detail-panel" id="pipeline-detail" aria-live="polite">
        <span className="detail-kicker" id="detail-kicker">{currentPipeline.kicker}</span>
        <h3 id="detail-title">{currentPipeline.title}</h3>
        <p id="detail-copy">{currentPipeline.copy}</p>
        <div className="trace" aria-hidden="true">
          <span className="trace-line" />
          <span className="trace-pulse" />
        </div>
        <div className="detail-footer">
          <button
            className="button button-secondary"
            id="run-sequence"
            type="button"
            aria-busy={sequenceState === "running"}
            disabled={sequenceState === "running"}
            onClick={runSequence}
          >
            {sequenceState === "complete" ? "Run it again" : "Run the sequence"}
            <span aria-hidden="true">{sequenceState === "complete" ? "↻" : "▶"}</span>
          </button>
          <p id="sequence-status" role="status">
            {sequenceState === "complete"
              ? "Sequence complete"
              : sequenceState === "running"
                ? `${currentPipeline.title}...`
                : "Ready when you are"}
          </p>
        </div>
      </article>
    </div>
  );
}
