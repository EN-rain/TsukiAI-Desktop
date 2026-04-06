#!/usr/bin/env python3
"""
Semantic memory service using ChromaDB for vector search.
Supports one-shot CLI calls and long-lived worker mode (JSON lines RPC).
"""

import argparse
import hashlib
import json
import sys
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


def ensure_chromadb() -> bool:
    try:
        import chromadb  # noqa: F401
        return True
    except ImportError:
        print(json.dumps({"error": "chromadb not installed. Run: pip install chromadb"}), file=sys.stderr)
        return False


def get_collection(db_path: str):
    import chromadb
    from chromadb.config import Settings

    db_dir = Path(db_path).parent
    db_dir.mkdir(parents=True, exist_ok=True)

    client = chromadb.PersistentClient(
        path=str(db_dir),
        settings=Settings(anonymized_telemetry=False),
    )
    return client.get_or_create_collection(
        name="conversation_memory",
        metadata={"description": "Semantic memory for TsukiAI conversations"},
    )


def init_db(db_path: str) -> bool:
    try:
        _ = get_collection(db_path)
        return True
    except Exception as e:
        print(json.dumps({"error": f"Failed to initialize ChromaDB: {str(e)}"}), file=sys.stderr)
        return False


def add_memory(db_path: str, text: str, source: str = "chat") -> bool:
    try:
        # Validate input length to prevent memory issues
        if len(text) > 10000:
            print(json.dumps({"error": "Text too long (max 10000 chars)"}), file=sys.stderr)
            return False
        
        if not text.strip():
            print(json.dumps({"error": "Empty text"}), file=sys.stderr)
            return False

        collection = get_collection(db_path)

        text_hash = hashlib.md5(text.encode("utf-8")).hexdigest()[:10]
        now = datetime.now()
        timestamp = now.isoformat()
        doc_id = f"{source}_{text_hash}_{int(now.timestamp())}"

        collection.add(
            documents=[text],
            metadatas=[{"source": source, "timestamp": timestamp}],
            ids=[doc_id],
        )
        return True
    except Exception as e:
        print(json.dumps({"error": f"Failed to add memory: {str(e)}"}), file=sys.stderr)
        return False


def search_memory(db_path: str, query: str, limit: int = 5) -> List[Dict[str, Any]]:
    try:
        # Validate query length
        if len(query) > 1000:
            print(json.dumps({"error": "Query too long (max 1000 chars)"}), file=sys.stderr)
            return []
        
        if not query.strip():
            return []

        collection = get_collection(db_path)
        results = collection.query(query_texts=[query], n_results=limit)

        hits: List[Dict[str, Any]] = []
        if not results:
            return hits

        docs = (results.get("documents") or [[]])[0]
        metadatas = (results.get("metadatas") or [[]])[0]
        distances = (results.get("distances") or [[]])[0]
        ids = (results.get("ids") or [[]])[0]

        for i, doc in enumerate(docs):
            metadata = metadatas[i] if i < len(metadatas) else {}
            distance = float(distances[i]) if i < len(distances) else 1.0
            hit_id = ids[i] if i < len(ids) else f"hit_{i}"
            hits.append(
                {
                    "id": str(hit_id),
                    "text": doc,
                    "source": metadata.get("source", "unknown"),
                    "distance": distance,
                }
            )

        return hits
    except Exception as e:
        print(json.dumps({"error": f"Failed to search memory: {str(e)}"}), file=sys.stderr)
        return []


def write_worker_response(request_id: str, ok: bool, data: Any = None, error: str = "") -> None:
    payload = {"id": request_id, "ok": ok, "data": data, "error": error}
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def run_worker(db_path: str) -> int:
    if not ensure_chromadb():
        return 1

    for line in sys.stdin:
        raw = (line or "").strip()
        if not raw:
            continue

        request_id = ""
        try:
            req = json.loads(raw)
            request_id = str(req.get("id", ""))
            cmd = str(req.get("cmd", "")).strip().lower()
            args = req.get("args") or {}

            if cmd == "ensure":
                ok = init_db(db_path)
                write_worker_response(request_id, ok, {"success": ok}, "" if ok else "ensure failed")
            elif cmd == "add":
                text = str(args.get("text", ""))
                source = str(args.get("source", "voicechat"))
                if not text.strip():
                    write_worker_response(request_id, False, None, "missing text")
                    continue
                ok = add_memory(db_path, text, source)
                write_worker_response(request_id, ok, {"success": ok}, "" if ok else "add failed")
            elif cmd == "search":
                query = str(args.get("query", ""))
                top_k = int(args.get("k", 5))
                if not query.strip():
                    write_worker_response(request_id, False, [], "missing query")
                    continue
                hits = search_memory(db_path, query, top_k)
                write_worker_response(request_id, True, hits, "")
            else:
                write_worker_response(request_id, False, None, f"unknown cmd: {cmd}")
        except Exception as e:
            write_worker_response(request_id, False, None, str(e))

    return 0


def main() -> None:
    parser = argparse.ArgumentParser(description="Semantic memory service using ChromaDB")
    parser.add_argument("command", nargs="?", choices=["ensure", "add", "search"], help="Command to execute")
    parser.add_argument("--db", required=True, help="Database directory path")
    parser.add_argument("--text", help="Text to add (for add command)")
    parser.add_argument("--source", default="voicechat", help="Source identifier (for add command)")
    parser.add_argument("--query", help="Search query (for search command)")
    parser.add_argument("--k", type=int, default=5, help="Number of results (for search command)")
    parser.add_argument("--worker", action="store_true", help="Run in persistent worker mode (JSON lines RPC)")
    args = parser.parse_args()

    if args.worker:
        sys.exit(run_worker(args.db))

    if not ensure_chromadb():
        sys.exit(1)

    if args.command == "ensure":
        success = init_db(args.db)
        print(json.dumps({"success": success}))
        sys.exit(0 if success else 1)

    if args.command == "add":
        if not args.text:
            print(json.dumps({"error": "Missing --text argument"}), file=sys.stderr)
            sys.exit(1)
        success = add_memory(args.db, args.text, args.source)
        print(json.dumps({"success": success}))
        sys.exit(0 if success else 1)

    if args.command == "search":
        if not args.query:
            print(json.dumps({"error": "Missing --query argument"}), file=sys.stderr)
            sys.exit(1)
        hits = search_memory(args.db, args.query, args.k)
        print(json.dumps(hits))
        sys.exit(0)

    print(json.dumps({"error": "No command provided"}), file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    main()
