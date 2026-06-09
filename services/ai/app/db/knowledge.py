"""
Knowledge persistence layer - Phase 3.

Chunks are stored in LanceDB for vector similarity search.
Source metadata (folders/files the user has indexed) is stored in SQLite
so we can list and delete sources without scanning LanceDB.

LanceDB table schema (chunks):
    id          TEXT  - uuid
    source_id   TEXT  - uuid of the parent source
    file_path   TEXT  - absolute path of the file this chunk came from
    chunk_index INT   - position within the file (0-based)
    content     TEXT  - raw text of the chunk
    vector      LIST  - float32 embedding (768 dims for nomic-embed-text)

SQLite table schema (sources):
    id          TEXT PRIMARY KEY
    path        TEXT  - folder or file path the user added
    name        TEXT  - display name (basename)
    file_count  INT
    chunk_count INT
    status      TEXT  - "indexing" | "ready" | "error"
    created_at  TEXT
"""

import os
import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

import httpx
import lancedb
import pyarrow as pa

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

EMBED_TIMEOUT = httpx.Timeout(connect=5.0, read=120.0, write=5.0, pool=5.0)
OLLAMA_BASE = "http://127.0.0.1:11434"
DEFAULT_EMBED_MODEL = "nomic-embed-text"

SUPPORTED_EXTENSIONS = {
    ".txt", ".md", ".py", ".js", ".ts", ".tsx", ".jsx",
    ".rs", ".toml", ".json", ".csv", ".html", ".css", ".yaml", ".yml",
}

CHUNK_SIZE = 1800       # characters per chunk
CHUNK_OVERLAP = 200     # overlap between adjacent chunks


def _data_dir() -> Path:
    d = os.environ.get("LOCALMIND_DATA_DIR", "")
    if d:
        p = Path(d)
    else:
        import tempfile
        p = Path(tempfile.gettempdir()) / "localmind_dev"
    p.mkdir(parents=True, exist_ok=True)
    return p


def _lance_dir() -> str:
    return str(_data_dir() / "knowledge.lance")


def _sqlite_path() -> Path:
    return _data_dir() / "knowledge_sources.db"


# ---------------------------------------------------------------------------
# SQLite sources DB
# ---------------------------------------------------------------------------

def _get_sqlite() -> sqlite3.Connection:
    conn = sqlite3.connect(str(_sqlite_path()))
    conn.row_factory = sqlite3.Row
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS sources (
            id          TEXT PRIMARY KEY,
            path        TEXT NOT NULL,
            name        TEXT NOT NULL,
            file_count  INTEGER NOT NULL DEFAULT 0,
            chunk_count INTEGER NOT NULL DEFAULT 0,
            status      TEXT NOT NULL DEFAULT 'indexing',
            created_at  TEXT NOT NULL
        )
        """
    )
    conn.commit()
    return conn


# ---------------------------------------------------------------------------
# LanceDB
# ---------------------------------------------------------------------------

def _get_lance_table():
    """Open (or create) the LanceDB chunks table."""
    db = lancedb.connect(_lance_dir())
    schema = pa.schema([
        pa.field("id",          pa.utf8()),
        pa.field("source_id",   pa.utf8()),
        pa.field("file_path",   pa.utf8()),
        pa.field("chunk_index", pa.int32()),
        pa.field("content",     pa.utf8()),
        pa.field("vector",      pa.list_(pa.float32(), 768)),
    ])
    if "chunks" not in db.table_names():
        db.create_table("chunks", schema=schema)
    return db.open_table("chunks")


# ---------------------------------------------------------------------------
# Embedding
# ---------------------------------------------------------------------------

async def embed_text(text: str, model: str = DEFAULT_EMBED_MODEL) -> list[float]:
    """Call Ollama /api/embeddings and return the embedding vector."""
    async with httpx.AsyncClient(timeout=EMBED_TIMEOUT) as client:
        resp = await client.post(
            f"{OLLAMA_BASE}/api/embeddings",
            json={"model": model, "prompt": text},
        )
        resp.raise_for_status()
        return resp.json()["embedding"]


# ---------------------------------------------------------------------------
# Chunking
# ---------------------------------------------------------------------------

def _chunk_text(text: str) -> list[str]:
    """Split text into overlapping chunks by character count."""
    chunks = []
    start = 0
    while start < len(text):
        end = start + CHUNK_SIZE
        chunks.append(text[start:end])
        start += CHUNK_SIZE - CHUNK_OVERLAP
    return [c for c in chunks if c.strip()]


def _collect_files(path: str) -> list[Path]:
    """Return all supported files under path (file or directory)."""
    p = Path(path)
    if p.is_file():
        return [p] if p.suffix.lower() in SUPPORTED_EXTENSIONS else []
    files = []
    for f in p.rglob("*"):
        if f.is_file() and f.suffix.lower() in SUPPORTED_EXTENSIONS:
            # Skip hidden files and common noise dirs
            parts = f.parts
            if any(part.startswith(".") or part in ("node_modules", "__pycache__", "target", "dist") for part in parts):
                continue
            files.append(f)
    return files


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

async def index_path(path: str, embed_model: str = DEFAULT_EMBED_MODEL) -> dict:
    """
    Index a folder or file. Creates a source record, embeds all chunks,
    and writes them to LanceDB.

    Returns: {"source_id": str, "file_count": int, "chunk_count": int}
    """
    source_id = str(uuid.uuid4())
    name = Path(path).name or path
    now = datetime.now(timezone.utc).isoformat()

    conn = _get_sqlite()
    conn.execute(
        "INSERT INTO sources (id, path, name, file_count, chunk_count, status, created_at) VALUES (?, ?, ?, 0, 0, 'indexing', ?)",
        (source_id, path, name, now),
    )
    conn.commit()

    files = _collect_files(path)
    table = _get_lance_table()

    rows = []
    for f in files:
        try:
            text = f.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue
        chunks = _chunk_text(text)
        for i, chunk in enumerate(chunks):
            try:
                vector = await embed_text(chunk, model=embed_model)
            except Exception:
                continue
            rows.append({
                "id":          str(uuid.uuid4()),
                "source_id":   source_id,
                "file_path":   str(f),
                "chunk_index": i,
                "content":     chunk,
                "vector":      vector,
            })

    if rows:
        table.add(rows)

    file_count = len(files)
    chunk_count = len(rows)

    conn.execute(
        "UPDATE sources SET file_count=?, chunk_count=?, status='ready' WHERE id=?",
        (file_count, chunk_count, source_id),
    )
    conn.commit()
    conn.close()

    return {"source_id": source_id, "file_count": file_count, "chunk_count": chunk_count}


async def search(query: str, limit: int = 5, embed_model: str = DEFAULT_EMBED_MODEL) -> list[dict]:
    """
    Semantic search over indexed chunks.
    Returns up to `limit` chunks sorted by relevance.
    """
    try:
        table = _get_lance_table()
        if table.count_rows() == 0:
            return []
        vector = await embed_text(query, model=embed_model)
        results = (
            table.search(vector)
            .limit(limit)
            .select(["id", "source_id", "file_path", "chunk_index", "content"])
            .to_list()
        )
        return [
            {
                "id":         r["id"],
                "source_id":  r["source_id"],
                "file_path":  r["file_path"],
                "chunk_index": r["chunk_index"],
                "content":    r["content"],
            }
            for r in results
        ]
    except Exception:
        return []


def list_sources() -> list[dict]:
    """Return all indexed sources, newest first."""
    conn = _get_sqlite()
    rows = conn.execute(
        "SELECT id, path, name, file_count, chunk_count, status, created_at FROM sources ORDER BY created_at DESC"
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


def delete_source(source_id: str) -> bool:
    """Delete a source and all its chunks. Returns True if the source existed."""
    conn = _get_sqlite()
    cursor = conn.execute("DELETE FROM sources WHERE id = ?", (source_id,))
    conn.commit()
    deleted = cursor.rowcount > 0
    conn.close()

    if deleted:
        try:
            table = _get_lance_table()
            table.delete(f"source_id = '{source_id}'")
        except Exception:
            pass  # LanceDB delete failure is non-fatal

    return deleted
