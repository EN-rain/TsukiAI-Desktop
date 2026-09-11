import { readFileSync } from 'node:fs';

const AUTH_FAILURE_COOLDOWN_MS = 15 * 60 * 1000;
const RATE_LIMIT_COOLDOWN_MS = 30 * 1000;
const TRANSIENT_FAILURE_COOLDOWN_MS = 5 * 1000;

/**
 * Accept newline/comma-separated values, assignment-style env values, and
 * optional Bearer prefixes. The values are never logged by this module.
 */
export function parseAssemblyApiKeys(raw) {
  const values = Array.isArray(raw)
    ? raw
    : String(raw ?? '').split(/[\r\n,]+/);

  const keys = [];
  for (const item of values) {
    let value = String(item ?? '').trim();
    if (!value || value.startsWith('#')) continue;

    const assignment = value.match(/^[A-Za-z_][A-Za-z0-9_]*\s*=\s*(.*)$/);
    if (assignment) value = assignment[1].trim();
    value = value.replace(/^['"]|['"]$/g, '').trim();
    if (value.toLowerCase().startsWith('bearer ')) value = value.slice(7).trim();

    if (value && !keys.includes(value)) keys.push(value);
  }

  return keys;
}

export function loadAssemblyApiKeys(filePath, fallbackRaw) {
  if (filePath) {
    try {
      const fileKeys = parseAssemblyApiKeys(readFileSync(filePath, 'utf8'));
      if (fileKeys.length > 0) return fileKeys;
    } catch {
      // The optional file falls back to the environment value.
    }
  }

  return parseAssemblyApiKeys(fallbackRaw);
}

/**
 * The SDK reports some WebSocket failures as text rather than HTTP errors.
 * Normalize the common auth/rate-limit signals so the pool can cool the bad
 * key without ever exposing it in logs.
 */
export function assemblyFailureStatus(error) {
  const explicit = Number(error?.statusCode ?? error?.status);
  if (Number.isFinite(explicit) && explicit > 0) return explicit;

  const code = Number(error?.code);
  if (Number.isFinite(code) && code >= 4000) return code;

  const message = String(error?.message ?? error ?? '').toLowerCase();
  if (/401|unauthori[sz]ed|invalid api|invalid token|not authorized/.test(message)) return 401;
  if (/429|rate.?limit|too many/.test(message)) return 429;
  return 0;
}

/**
 * Keeps the last known-good key first and temporarily moves rejected keys out
 * of the rotation. If every key is cooling down, all are returned once so a
 * recovered provider can be rediscovered.
 */
export class AssemblyKeyPool {
  #keys;
  #cursor = 0;
  #cooldownUntil = new Map();

  constructor(rawKeys) {
    this.#keys = parseAssemblyApiKeys(rawKeys);
  }

  get size() {
    return this.#keys.length;
  }

  candidates(now = Date.now()) {
    if (this.#keys.length === 0) return [];

    const ordered = this.#orderedKeys();
    const available = ordered.filter((key) => (this.#cooldownUntil.get(key) ?? 0) <= now);
    return available.length > 0 ? available : ordered;
  }

  markSuccess(key) {
    const index = this.#keys.indexOf(key);
    if (index < 0) return;
    this.#cursor = index;
    this.#cooldownUntil.delete(key);
  }

  markFailure(key, statusCode) {
    const index = this.#keys.indexOf(key);
    if (index < 0) return;

    this.#cursor = (index + 1) % this.#keys.length;
    const status = Number(statusCode);
    const cooldown = status === 401 || status === 403 || status === 4001
      ? AUTH_FAILURE_COOLDOWN_MS
      : status === 429 || status === 4029
        ? RATE_LIMIT_COOLDOWN_MS
        : TRANSIENT_FAILURE_COOLDOWN_MS;
    this.#cooldownUntil.set(key, Date.now() + cooldown);
  }

  #orderedKeys() {
    return this.#keys.map((_, offset) => this.#keys[(this.#cursor + offset) % this.#keys.length]);
  }
}
