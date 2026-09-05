import { useCallback, useEffect, useRef, useState } from "react";
import { processVoice, transcribeAudio } from "../api";
import { playPcm48k } from "../audio";

type VoicePhase = "idle" | "recording" | "transcribing" | "thinking" | "speaking";

const PHASE_LABEL: Record<VoicePhase, string> = {
  idle: "Ready — hold Space or tap the button to talk",
  recording: "Listening… release to send",
  transcribing: "Transcribing your voice…",
  thinking: "Tsuki is thinking…",
  speaking: "Tsuki is speaking…",
};

export function VoiceView() {
  const [phase, setPhase] = useState<VoicePhase>("idle");
  const [error, setError] = useState<string | null>(null);
  const [transcript, setTranscript] = useState<string | null>(null);
  const [reply, setReply] = useState<string | null>(null);
  const recorderRef = useRef<MediaRecorder | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const busyRef = useRef(false);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const processTurn = useCallback(async (blob: Blob) => {
    busyRef.current = true;
    try {
      setPhase("transcribing");
      setTranscript(null);
      setReply(null);
      setError(null);

      const transcription = await transcribeAudio(blob);
      const text = transcription.text.trim();
      setTranscript(text);

      if (!text) {
        setError("Couldn't hear anything in that recording. Try again.");
        setPhase("idle");
        return;
      }

      setPhase("thinking");
      const result = await processVoice(text);
      setReply(result.text);

      if (result.audio) {
        setPhase("speaking");
        await playPcm48k(result.audio);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Voice turn failed");
    } finally {
      busyRef.current = false;
      setPhase("idle");
    }
  }, []);

  const startRecording = useCallback(async () => {
    if (busyRef.current || recorderRef.current) return;
    try {
      setError(null);
      setTranscript(null);
      setReply(null);
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const mimeType = MediaRecorder.isTypeSupported("audio/webm;codecs=opus")
        ? "audio/webm;codecs=opus"
        : undefined;
      const recorder = mimeType ? new MediaRecorder(stream, { mimeType }) : new MediaRecorder(stream);
      chunksRef.current = [];

      recorder.ondataavailable = (event) => {
        if (event.data.size > 0) chunksRef.current.push(event.data);
      };
      recorder.onstop = () => {
        stream.getTracks().forEach((track) => track.stop());
        const blob = new Blob(chunksRef.current, { type: recorder.mimeType || "audio/webm" });
        recorderRef.current = null;
        if (blob.size > 0) {
          void processTurn(blob);
        } else {
          setPhase("idle");
        }
      };

      recorderRef.current = recorder;
      recorder.start();
      setPhase("recording");
    } catch {
      setError("Microphone access was blocked. Allow it in your browser and try again.");
      setPhase("idle");
    }
  }, [processTurn]);

  const stopRecording = useCallback(() => {
    const recorder = recorderRef.current;
    if (recorder && recorder.state !== "inactive") {
      recorder.stop();
    }
  }, []);

  // Space as push-to-talk: hold to record, release to send.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.code !== "Space" || event.repeat) return;
      const target = event.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable)) {
        return;
      }
      event.preventDefault();
      void startRecording();
    };
    const onKeyUp = (event: KeyboardEvent) => {
      if (event.code !== "Space") return;
      const target = event.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable)) {
        return;
      }
      stopRecording();
    };
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("keyup", onKeyUp);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("keyup", onKeyUp);
    };
  }, [startRecording, stopRecording]);

  useEffect(() => {
    buttonRef.current?.focus();
  }, []);

  const isRecording = phase === "recording";

  return (
    <div className="flex h-full flex-col items-center justify-center gap-8 py-8">
      <button
        ref={buttonRef}
        type="button"
        aria-pressed={isRecording}
        aria-label={isRecording ? "Stop recording and send to Tsuki" : "Start recording"}
        onClick={() => (isRecording ? stopRecording() : void startRecording())}
        disabled={phase !== "idle" && !isRecording}
        className={`flex h-28 w-28 items-center justify-center rounded-full border-2 transition-all disabled:cursor-not-allowed disabled:opacity-60 ${
          isRecording
            ? "border-moon-300 bg-moon-400/20 scale-105"
            : "border-ink-600 bg-ink-800 hover:border-moon-500"
        }`}
      >
        <MicIcon
          className={`h-10 w-10 ${isRecording ? "text-moon-300" : "text-mist-400"}`}
          pulsing={isRecording}
        />
      </button>

      <p aria-live="polite" className="text-sm text-mist-400">
        {PHASE_LABEL[phase]}
      </p>

      {error && (
        <p role="alert" className="max-w-md text-center text-sm text-rose-alert">
          {error}
        </p>
      )}

      <div className="w-full max-w-md space-y-3">
        {transcript && (
          <div className="rounded-lg border border-ink-700 bg-ink-800 px-4 py-3">
            <p className="text-xs uppercase tracking-wide text-mist-500">You said</p>
            <p className="mt-1 text-sm text-mist-100">{transcript}</p>
          </div>
        )}
        {reply && (
          <div className="rounded-lg border border-moon-500/40 bg-ink-800 px-4 py-3">
            <p className="text-xs uppercase tracking-wide text-moon-400">Tsuki</p>
            <p className="mt-1 text-sm text-mist-100">{reply}</p>
          </div>
        )}
      </div>
    </div>
  );
}

function MicIcon({ className, pulsing }: { className?: string; pulsing?: boolean }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="currentColor"
      className={`${className} ${pulsing ? "animate-pulse" : ""}`}
      aria-hidden="true"
    >
      <path d="M12 15a3 3 0 0 0 3-3V6a3 3 0 1 0-6 0v6a3 3 0 0 0 3 3Z" />
      <path d="M19 11a1 1 0 1 0-2 0 5 5 0 0 1-10 0 1 1 0 1 0-2 0 7 7 0 0 0 6 6.93V20H9a1 1 0 1 0 0 2h6a1 1 0 1 0 0-2h-2v-2.07A7 7 0 0 0 19 11Z" />
    </svg>
  );
}
