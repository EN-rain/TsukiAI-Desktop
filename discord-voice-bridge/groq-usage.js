import { createHash } from 'node:crypto';
import { appendFileSync, chmodSync, existsSync, mkdirSync, openSync, closeSync, fstatSync, fsyncSync, writeSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';

const FILE = 'groq-usage-bridge.jsonl';

export function defaultGroqUsageDirectory() {
  return path.resolve(process.env.TSUKI_GROQ_USAGE_DIR || process.env.TSUKI_DATA_DIR ||
    (existsSync('/data') ? '/data' : path.join(process.env.APPDATA ||
      path.join(homedir(), '.config'), 'TsukiAI')));
}

function fingerprint(key) {
  return createHash('sha256').update(key, 'utf8').digest('hex');
}

function mask(key) {
  return key.length >= 12 ? `${key.slice(0, 4)}...${key.slice(-5)}` : '[configured key]';
}

/** Separate writer from the C# API, on the same durable mounted volume. */
export class GroqUsageRecorder {
  constructor(directory = defaultGroqUsageDirectory(), now = () => new Date()) {
    mkdirSync(directory, { recursive: true, mode: 0o700 });
    this.file = path.join(directory, FILE);
    this.now = now;
    // The bridge runs as root but the C# API reads this shared, non-secret
    // ledger as UID 1000. Owner-only mode would break its usage endpoint.
    const fd = openSync(this.file, 'a', 0o644);
    try {
      if (fstatSync(fd).size === 0) {
        writeSync(fd, JSON.stringify({ type: 'tracking_started', timestamp_utc: now().toISOString(),
          key_id: null, key_mask: null, kind: null }) + '\n');
        fsyncSync(fd);
      }
    } finally {
      closeSync(fd);
    }
    chmodSync(this.file, 0o644);
  }

  recordAttempt(key, kind) {
    if (!key || !['llm', 'stt', 'other'].includes(kind)) {
      throw new TypeError('A key and valid Groq request kind are required');
    }
    const line = JSON.stringify({ type: 'attempt', timestamp_utc: this.now().toISOString(),
      key_id: fingerprint(key), key_mask: mask(key), kind }) + '\n';
    const fd = openSync(this.file, 'a', 0o644);
    try {
      writeSync(fd, line);
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
  }
}

/** The request interceptor executes for axios-retry requests as well. */
export function installGroqUsageInterceptor(instance, recorder) {
  return instance.interceptors.request.use(config => {
    let url;
    try { url = new URL(config.url, config.baseURL); } catch { return config; }
    if (url.hostname.toLowerCase() !== 'api.groq.com') return config;

    const auth = config.headers?.get?.('Authorization') ||
      config.headers?.Authorization || config.headers?.authorization || '';
    const match = /^Bearer\s+(.+)$/i.exec(String(auth).trim());
    if (!match) throw new Error('Groq request is missing its configured API key');
    const kind = url.pathname.includes('/audio/transcriptions') ? 'stt'
      : url.pathname.includes('/chat/completions') || url.pathname.endsWith('/generate') ? 'llm' : 'other';
    recorder.recordAttempt(match[1].trim(), kind);
    return config;
  });
}
