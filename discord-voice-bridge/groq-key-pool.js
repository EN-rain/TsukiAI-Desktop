import { readFileSync } from 'node:fs';

const AUTH_FAILURE_COOLDOWN_MS = 15 * 60 * 1000;
const RATE_LIMIT_COOLDOWN_MS = 30 * 1000;
const TRANSIENT_FAILURE_COOLDOWN_MS = 5 * 1000;

/**
 * Accepts a newline/comma-separated value, an assignment-style env value, or
 * an array. Values are normalized without ever logging or exposing them.
 */
export function parseGroqApiKeys(raw) {
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

export function loadGroqApiKeys(filePath, fallbackRaw) {
  if (filePath) {
    try {
      const fileKeys = parseGroqApiKeys(readFileSync(filePath, 'utf8'));
      if (fileKeys.length > 0) return fileKeys;
    } catch {
      // A missing optional file falls back to the environment value.
    }
  }

  return parseGroqApiKeys(fallbackRaw);
}

/**
 * Keeps the last known-good key first and temporarily moves rejected keys out
 * of the rotation. If every key is cooling down, all keys are returned once so
 * a recovered provider can be rediscovered.
 */
export class GroqKeyPool {
  #keys;
  #cursor = 0;
  #cooldownUntil = new Map();

  constructor(rawKeys) {
    this.#keys = parseGroqApiKeys(rawKeys);
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
    const cooldown = status === 401 || status === 403
      ? AUTH_FAILURE_COOLDOWN_MS
      : status === 429
        ? RATE_LIMIT_COOLDOWN_MS
        : TRANSIENT_FAILURE_COOLDOWN_MS;
    this.#cooldownUntil.set(key, Date.now() + cooldown);
  }

  #orderedKeys() {
    return this.#keys.map((_, offset) => this.#keys[(this.#cursor + offset) % this.#keys.length]);
  }
}
