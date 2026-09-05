export interface AuthStatus {
  authenticated: boolean;
  public_mode: boolean;
}

export interface HistoryMessage {
  role: string;
  content: string;
  timestamp: string;
  speaker_id?: string | null;
}

export interface ChatTurnResult {
  text: string;
}

export interface VoiceProcessResult {
  correlation_id: string;
  text: string;
  audio: string | null;
  error?: string;
}

export interface TranscriptionResult {
  correlation_id: string;
  text: string;
  language: string;
  confidence: number;
}

export interface WebSettings {
  model_name: string;
  inference_mode: string;
  use_multiple_providers: boolean;
  multi_providers_csv: string;
  active_provider: string | null;
  reply_tone_preset: string;
  generation: {
    max_tokens: number;
    temperature: number;
    top_p: number;
    top_k: number;
    repeat_penalty: number;
    max_reply_chars: number;
  };
  tts: {
    mode: string;
    voicevox_base_url: string;
    speaker_style_id: number;
  };
  translation: {
    voice_translate_to_japanese: boolean;
    use_deepl: boolean;
    use_deepl_free_api: boolean;
  };
  memory: {
    semantic_memory_enabled: boolean;
  };
  stt: {
    mode: string;
    language_code: string;
  };
}

export interface MemoryHit {
  id: string;
  text: string;
  source: string;
  distance: number;
}
