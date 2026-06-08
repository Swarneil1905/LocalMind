"""
Memory persistence layer using Python's built-in sqlite3.

The DB file is created at:
    {LOCALMIND_DATA_DIR}/memories.db

If LOCALMIND_DATA_DIR is not set (e.g. running tests outside Tauri), falls back
to a temp file so the import never fails.

Schema:
    memories(id TEXT PK, content TEXT, created_at TEXT)
"""

import os
import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path


def _db_path() -> Path:
    data_dir = os.environ.get("LOCALMIND_DATA_DIR", "")
    if data_dir:
        p = Path(data_dir)
    else:
        import tempfile
        p = Path(tempfile.gettempdir()) / "localmind_dev"
    p.mkdir(parents=True, exist_ok=True)
    return p / "memories.db"


def _get_conn() -> sqlite3.Connection:
    conn = sqlite3.connect(str(_db_path()))
    conn.row_factory = sqlite3.Row
    return conn


def _init_schema(conn: sqlite3.Connection) -> None:
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS memories (
            id         TEXT PRIMARY KEY,
            content    TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """
    )
    conn.commit()


def _ensure_schema() -> sqlite3.Connection:
    conn = _get_conn()
    _init_schema(conn)
    return conn


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def insert_memories(contents: list[str]) -> list[dict]:
    """Insert a batch of memory strings, skipping near-duplicates and junk.

    A memory is skipped if:
    - It is shorter than 6 words (too vague)
    - Its lowercased text already exists in the DB
    """
    if not contents:
        return []
    conn = _ensure_schema()

    # Load existing content for dedup check
    existing_lower = {
        row["content"].lower()
        for row in conn.execute("SELECT content FROM memories").fetchall()
    }

    now = datetime.now(timezone.utc).isoformat()
    rows = []
    for content in contents:
        content = content.strip()
        if len(content.split()) < 6:
            continue  # too short - likely a fragment or single word
        if content.lower() in existing_lower:
            continue  # duplicate
        mid = str(uuid.uuid4())
        conn.execute(
            "INSERT INTO memories (id, content, created_at) VALUES (?, ?, ?)",
            (mid, content, now),
        )
        existing_lower.add(content.lower())
        rows.append({"id": mid, "content": content, "created_at": now})
    conn.commit()
    conn.close()
    return rows


def list_all() -> list[dict]:
    """Return all memories, newest first."""
    conn = _ensure_schema()
    cursor = conn.execute(
        "SELECT id, content, created_at FROM memories ORDER BY created_at DESC"
    )
    rows = [dict(r) for r in cursor.fetchall()]
    conn.close()
    return rows


def delete_by_id(memory_id: str) -> bool:
    """Delete a memory by id. Returns True if a row was deleted."""
    conn = _ensure_schema()
    cursor = conn.execute("DELETE FROM memories WHERE id = ?", (memory_id,))
    conn.commit()
    deleted = cursor.rowcount > 0
    conn.close()
    return deleted
