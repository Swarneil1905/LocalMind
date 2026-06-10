"""
Memory persistence layer using Python's built-in sqlite3.

The DB file is created at:
    {LOCALMIND_DATA_DIR}/memories.db

If LOCALMIND_DATA_DIR is not set (e.g. running tests outside Tauri), falls back
to a temp file so the import never fails.

Schema:
    memories(id TEXT PK, content TEXT, created_at TEXT)
    memory_links(id TEXT PK, from_id TEXT FK, to_id TEXT FK, relation TEXT, created_at TEXT)

Relation types:
    related_to   - general semantic relationship
    part_of      - from_id is a component/detail of to_id
    elaborates   - from_id adds detail to to_id
    contradicts  - from_id conflicts with to_id
    follows_from - from_id is a consequence/follow-up of to_id
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


VALID_RELATIONS = {"related_to", "part_of", "elaborates", "contradicts", "follows_from"}


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
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS memory_links (
            id         TEXT PRIMARY KEY,
            from_id    TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            to_id      TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            relation   TEXT NOT NULL DEFAULT 'related_to',
            created_at TEXT NOT NULL,
            UNIQUE(from_id, to_id, relation)
        )
        """
    )
    conn.execute("PRAGMA foreign_keys = ON")
    conn.commit()


def _ensure_schema() -> sqlite3.Connection:
    conn = _get_conn()
    conn.execute("PRAGMA foreign_keys = ON")
    _init_schema(conn)
    return conn


# ---------------------------------------------------------------------------
# Memories API
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

    existing_lower = {
        row["content"].lower()
        for row in conn.execute("SELECT content FROM memories").fetchall()
    }

    now = datetime.now(timezone.utc).isoformat()
    rows = []
    for content in contents:
        content = content.strip()
        if len(content.split()) < 6:
            continue
        if content.lower() in existing_lower:
            continue
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


# ---------------------------------------------------------------------------
# Memory links API
# ---------------------------------------------------------------------------

def create_link(from_id: str, to_id: str, relation: str = "related_to") -> dict | None:
    """
    Create a directed link between two memories.

    Returns the new link row, or None if either memory does not exist or the
    relation is invalid. Silently ignores duplicate (from_id, to_id, relation).
    """
    if relation not in VALID_RELATIONS:
        return None
    if from_id == to_id:
        return None

    conn = _ensure_schema()
    exists = conn.execute(
        "SELECT COUNT(*) FROM memories WHERE id IN (?, ?)", (from_id, to_id)
    ).fetchone()[0]
    if exists < 2:
        conn.close()
        return None

    link_id = str(uuid.uuid4())
    now = datetime.now(timezone.utc).isoformat()
    try:
        conn.execute(
            "INSERT OR IGNORE INTO memory_links (id, from_id, to_id, relation, created_at) "
            "VALUES (?, ?, ?, ?, ?)",
            (link_id, from_id, to_id, relation, now),
        )
        conn.commit()
    except Exception:
        conn.close()
        return None

    row = conn.execute(
        "SELECT id, from_id, to_id, relation, created_at FROM memory_links "
        "WHERE from_id = ? AND to_id = ? AND relation = ?",
        (from_id, to_id, relation),
    ).fetchone()
    conn.close()
    return dict(row) if row else None


def get_links_for_memory(memory_id: str) -> list[dict]:
    """
    Return all links where memory_id is either the source or target.
    Each row includes the content of both linked memories.
    """
    conn = _ensure_schema()
    rows = conn.execute(
        """
        SELECT
            ml.id, ml.from_id, ml.to_id, ml.relation, ml.created_at,
            mf.content AS from_content,
            mt.content AS to_content
        FROM memory_links ml
        JOIN memories mf ON mf.id = ml.from_id
        JOIN memories mt ON mt.id = ml.to_id
        WHERE ml.from_id = ? OR ml.to_id = ?
        ORDER BY ml.created_at DESC
        """,
        (memory_id, memory_id),
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


def list_all_links() -> list[dict]:
    """Return every link with content of both ends, newest first."""
    conn = _ensure_schema()
    rows = conn.execute(
        """
        SELECT
            ml.id, ml.from_id, ml.to_id, ml.relation, ml.created_at,
            mf.content AS from_content,
            mt.content AS to_content
        FROM memory_links ml
        JOIN memories mf ON mf.id = ml.from_id
        JOIN memories mt ON mt.id = ml.to_id
        ORDER BY ml.created_at DESC
        """
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


def delete_link(link_id: str) -> bool:
    """Delete a link by id. Returns True if deleted."""
    conn = _ensure_schema()
    cursor = conn.execute("DELETE FROM memory_links WHERE id = ?", (link_id,))
    conn.commit()
    deleted = cursor.rowcount > 0
    conn.close()
    return deleted
