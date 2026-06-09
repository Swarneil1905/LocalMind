"""
Conversation persistence layer - Phase 3.5.

Two SQLite tables:
  conversations        - one row per chat session
  conversation_messages - one row per message turn

All writes go through save_turn() which atomically inserts user +
assistant messages and bumps the conversation updated_at timestamp.
"""

import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path
import os


# ---------------------------------------------------------------------------
# DB path (reuses the same data dir as memory / knowledge)
# ---------------------------------------------------------------------------

def _data_dir() -> Path:
    d = os.environ.get("LOCALMIND_DATA_DIR", "")
    if d:
        p = Path(d)
    else:
        import tempfile
        p = Path(tempfile.gettempdir()) / "localmind_dev"
    p.mkdir(parents=True, exist_ok=True)
    return p


def _db_path() -> Path:
    return _data_dir() / "conversations.db"


# ---------------------------------------------------------------------------
# Connection + schema bootstrap
# ---------------------------------------------------------------------------

def _get_conn() -> sqlite3.Connection:
    conn = sqlite3.connect(str(_db_path()))
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    conn.executescript(
        """
        CREATE TABLE IF NOT EXISTS conversations (
            id         TEXT PRIMARY KEY,
            title      TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS conversation_messages (
            id              TEXT PRIMARY KEY,
            conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
            role            TEXT NOT NULL,
            content         TEXT NOT NULL,
            thinking        TEXT,
            created_at      TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_conv_messages_conv_id
            ON conversation_messages(conversation_id, created_at);
        """
    )
    conn.commit()
    return conn


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def create_conversation(title: str) -> dict:
    """Create a new conversation. Returns the full row as a dict."""
    now = datetime.now(timezone.utc).isoformat()
    cid = str(uuid.uuid4())
    conn = _get_conn()
    conn.execute(
        "INSERT INTO conversations (id, title, created_at, updated_at) VALUES (?, ?, ?, ?)",
        (cid, title, now, now),
    )
    conn.commit()
    conn.close()
    return {"id": cid, "title": title, "created_at": now, "updated_at": now}


def list_conversations() -> list[dict]:
    """Return all conversations, newest first."""
    conn = _get_conn()
    rows = conn.execute(
        "SELECT id, title, created_at, updated_at FROM conversations ORDER BY updated_at DESC"
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


def get_messages(conversation_id: str) -> list[dict]:
    """Return all messages for a conversation in chronological order."""
    conn = _get_conn()
    rows = conn.execute(
        """
        SELECT id, conversation_id, role, content, thinking, created_at
        FROM conversation_messages
        WHERE conversation_id = ?
        ORDER BY created_at ASC
        """,
        (conversation_id,),
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


def save_turn(
    conversation_id: str,
    user_content: str,
    assistant_content: str,
    assistant_thinking: str | None = None,
) -> None:
    """
    Atomically save a user + assistant message pair and bump updated_at.
    Skips if either content is empty.
    """
    if not user_content.strip() or not assistant_content.strip():
        return

    now = datetime.now(timezone.utc).isoformat()
    conn = _get_conn()
    conn.executemany(
        """
        INSERT INTO conversation_messages (id, conversation_id, role, content, thinking, created_at)
        VALUES (?, ?, ?, ?, ?, ?)
        """,
        [
            (str(uuid.uuid4()), conversation_id, "user",      user_content,      None,               now),
            (str(uuid.uuid4()), conversation_id, "assistant", assistant_content, assistant_thinking, now),
        ],
    )
    conn.execute(
        "UPDATE conversations SET updated_at = ? WHERE id = ?",
        (now, conversation_id),
    )
    conn.commit()
    conn.close()


def delete_conversation(conversation_id: str) -> bool:
    """Delete a conversation and all its messages. Returns True if it existed."""
    conn = _get_conn()
    cursor = conn.execute("DELETE FROM conversations WHERE id = ?", (conversation_id,))
    conn.commit()
    deleted = cursor.rowcount > 0
    conn.close()
    return deleted


def rename_conversation(conversation_id: str, title: str) -> bool:
    """Rename a conversation. Returns True if it existed."""
    now = datetime.now(timezone.utc).isoformat()
    conn = _get_conn()
    cursor = conn.execute(
        "UPDATE conversations SET title = ?, updated_at = ? WHERE id = ?",
        (title, now, conversation_id),
    )
    conn.commit()
    updated = cursor.rowcount > 0
    conn.close()
    return updated
