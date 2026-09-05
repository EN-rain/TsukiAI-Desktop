import { useEffect, useState } from "react";
import { addMemory, getSettings, searchMemory } from "../api";
import type { MemoryHit } from "../types";

export function MemoryView() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<MemoryHit[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [newMemory, setNewMemory] = useState("");
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const settings = await getSettings();
        if (!cancelled) setEnabled(settings.memory.semantic_memory_enabled);
      } catch {
        if (!cancelled) setEnabled(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  async function handleSearch(event: React.FormEvent) {
    event.preventDefault();
    const q = query.trim();
    if (!q || searching) return;
    setSearching(true);
    setSearchError(null);
    try {
      setHits(await searchMemory(q));
    } catch (err) {
      setSearchError(err instanceof Error ? err.message : "Search failed");
    } finally {
      setSearching(false);
    }
  }

  async function handleAdd(event: React.FormEvent) {
    event.preventDefault();
    const text = newMemory.trim();
    if (!text || adding) return;
    setAdding(true);
    setAdded(false);
    setAddError(null);
    try {
      await addMemory(text);
      setNewMemory("");
      setAdded(true);
    } catch (err) {
      setAddError(err instanceof Error ? err.message : "Could not save memory");
    } finally {
      setAdding(false);
    }
  }

  return (
    <div className="h-full space-y-8 overflow-y-auto py-4 pb-8">
      {enabled === false && (
        <p role="note" className="rounded-lg border border-ink-700 bg-ink-800 px-4 py-3 text-sm text-mist-400">
          Semantic memory is disabled on the server, so searches return nothing. Enable it in
          Settings and set TSUKI_CHROMA_URL on the server to use it.
        </p>
      )}

      <section aria-labelledby="memory-search">
        <h2 id="memory-search" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Search memories
        </h2>
        <form onSubmit={handleSearch} className="mt-3 flex gap-2">
          <label htmlFor="memory-query" className="sr-only">
            Search query
          </label>
          <input
            id="memory-query"
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="What should Tsuki remember?"
            className="flex-1 rounded-md border border-ink-600 bg-ink-900 px-3 py-2 text-sm text-mist-100 placeholder:text-mist-500"
          />
          <button
            type="submit"
            disabled={searching || query.trim().length === 0}
            className="rounded-md bg-moon-400 px-4 py-2 text-sm font-medium text-ink-950 transition-colors hover:bg-moon-300 disabled:opacity-50"
          >
            {searching ? "Searching…" : "Search"}
          </button>
        </form>

        {searchError && (
          <p role="alert" className="mt-3 text-sm text-rose-alert">
            {searchError}
          </p>
        )}

        {hits !== null && hits.length === 0 && (
          <p className="mt-4 text-sm text-mist-400">No matching memories.</p>
        )}

        <ul role="list" className="mt-4 space-y-2">
          {hits?.map((hit) => (
            <li
              key={hit.id}
              className="rounded-lg border border-ink-700 bg-ink-800 px-4 py-3 text-sm"
            >
              <p className="text-mist-100">{hit.text}</p>
              <p className="mt-1 text-xs text-mist-500">
                {hit.source} · distance {hit.distance.toFixed(3)}
              </p>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="memory-add">
        <h2 id="memory-add" className="text-sm font-semibold uppercase tracking-wide text-mist-400">
          Teach Tsuki something
        </h2>
        <form onSubmit={handleAdd} className="mt-3">
          <label htmlFor="memory-text" className="sr-only">
            New memory
          </label>
          <textarea
            id="memory-text"
            rows={2}
            value={newMemory}
            onChange={(e) => setNewMemory(e.target.value)}
            placeholder="e.g. I usually drink coffee in the morning, not tea."
            className="w-full resize-none rounded-md border border-ink-600 bg-ink-900 px-3 py-2 text-sm text-mist-100 placeholder:text-mist-500"
          />
          <div className="mt-2 flex items-center gap-3">
            <button
              type="submit"
              disabled={adding || newMemory.trim().length === 0}
              className="rounded-md bg-ink-700 px-4 py-2 text-sm text-mist-100 transition-colors hover:bg-ink-600 disabled:opacity-50"
            >
              {adding ? "Saving…" : "Save memory"}
            </button>
            {added && (
              <p role="status" className="text-sm text-moon-300">
                Saved
              </p>
            )}
            {addError && (
              <p role="alert" className="text-sm text-rose-alert">
                {addError}
              </p>
            )}
          </div>
        </form>
      </section>
    </div>
  );
}
