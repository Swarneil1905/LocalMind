"""
Projects and tasks persistence layer - Phase 4.

Tables:
  projects  - named project contexts, each with a path and a generated summary
  tasks     - action items scoped to a project

Conversations are linked to projects via a project_id column added via migration.
"""

import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path
import os


# ---------------------------------------------------------------------------
# Shared data dir (same as memory / knowledge / conversations)
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
    return _data_dir() / "projects.db"


def _conv_db_path() -> Path:
    """Conversations live in a separate DB; we write project_id there."""
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
        CREATE TABLE IF NOT EXISTS projects (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            path        TEXT,
            summary     TEXT,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tasks (
            id          TEXT PRIMARY KEY,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            title       TEXT NOT NULL,
            status      TEXT NOT NULL DEFAULT 'open',
            due_at      TEXT,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_tasks_project_id
            ON tasks(project_id, status);
        """
    )
    conn.commit()
    return conn


def _ensure_conv_project_col() -> None:
    """
    Idempotently add project_id column to conversations.db if it is missing.
    SQLite ALTER TABLE ADD COLUMN is safe to call repeatedly (we catch the error).
    """
    try:
        conn = sqlite3.connect(str(_conv_db_path()))
        conn.execute("ALTER TABLE conversations ADD COLUMN project_id TEXT")
        conn.commit()
        conn.close()
    except sqlite3.OperationalError:
        # Column already exists - normal on subsequent startups
        pass


# Run migration once at import time
_ensure_conv_project_col()


# ---------------------------------------------------------------------------
# Project CRUD
# ---------------------------------------------------------------------------

def create_project(name: str, path: str | None = None) -> dict:
    now = datetime.now(timezone.utc).isoformat()
    pid = str(uuid.uuid4())
    conn = _get_conn()
    conn.execute(
        "INSERT INTO projects (id, name, path, summary, created_at, updated_at) VALUES (?, ?, ?, NULL, ?, ?)",
        (pid, name, path, now, now),
    )
    conn.commit()
    conn.close()
    return {"id": pid, "name": name, "path": path, "summary": None,
            "created_at": now, "updated_at": now}


def list_projects() -> list[dict]:
    conn = _get_conn()
    rows = conn.execute(
        "SELECT id, name, path, summary, created_at, updated_at FROM projects ORDER BY updated_at DESC"
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


def get_project(project_id: str) -> dict | None:
    conn = _get_conn()
    row = conn.execute(
        "SELECT id, name, path, summary, created_at, updated_at FROM projects WHERE id = ?",
        (project_id,),
    ).fetchone()
    conn.close()
    return dict(row) if row else None


def update_project(project_id: str, name: str | None = None,
                   path: str | None = None, summary: str | None = None) -> bool:
    now = datetime.now(timezone.utc).isoformat()
    conn = _get_conn()
    fields: list[str] = ["updated_at = ?"]
    params: list[object] = [now]
    if name is not None:
        fields.append("name = ?")
        params.append(name)
    if path is not None:
        fields.append("path = ?")
        params.append(path)
    if summary is not None:
        fields.append("summary = ?")
        params.append(summary)
    params.append(project_id)
    cursor = conn.execute(
        f"UPDATE projects SET {', '.join(fields)} WHERE id = ?", params
    )
    conn.commit()
    updated = cursor.rowcount > 0
    conn.close()
    return updated


def delete_project(project_id: str) -> bool:
    conn = _get_conn()
    cursor = conn.execute("DELETE FROM projects WHERE id = ?", (project_id,))
    conn.commit()
    deleted = cursor.rowcount > 0
    conn.close()
    return deleted


# ---------------------------------------------------------------------------
# Task CRUD
# ---------------------------------------------------------------------------

VALID_STATUSES = ("open", "in_progress", "done", "cancelled")


def create_task(project_id: str, title: str, due_at: str | None = None) -> dict:
    now = datetime.now(timezone.utc).isoformat()
    tid = str(uuid.uuid4())
    conn = _get_conn()
    conn.execute(
        "INSERT INTO tasks (id, project_id, title, status, due_at, created_at, updated_at) VALUES (?, ?, ?, 'open', ?, ?, ?)",
        (tid, project_id, title, due_at, now, now),
    )
    conn.commit()
    conn.close()
    return {"id": tid, "project_id": project_id, "title": title,
            "status": "open", "due_at": due_at, "created_at": now, "updated_at": now}


def list_tasks(project_id: str) -> list[dict]:
    conn = _get_conn()
    rows = conn.execute(
        "SELECT id, project_id, title, status, due_at, created_at, updated_at FROM tasks WHERE project_id = ? ORDER BY created_at ASC",
        (project_id,),
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


def update_task(project_id: str, task_id: str,
                title: str | None = None, status: str | None = None,
                due_at: str | None = None) -> bool:
    if status is not None and status not in VALID_STATUSES:
        raise ValueError(f"Invalid status: {status}")
    now = datetime.now(timezone.utc).isoformat()
    conn = _get_conn()
    fields: list[str] = ["updated_at = ?"]
    params: list[object] = [now]
    if title is not None:
        fields.append("title = ?")
        params.append(title)
    if status is not None:
        fields.append("status = ?")
        params.append(status)
    if due_at is not None:
        fields.append("due_at = ?")
        params.append(due_at)
    params.extend([task_id, project_id])
    cursor = conn.execute(
        f"UPDATE tasks SET {', '.join(fields)} WHERE id = ? AND project_id = ?", params
    )
    conn.commit()
    updated = cursor.rowcount > 0
    conn.close()
    return updated


def delete_task(project_id: str, task_id: str) -> bool:
    conn = _get_conn()
    cursor = conn.execute(
        "DELETE FROM tasks WHERE id = ? AND project_id = ?", (task_id, project_id)
    )
    conn.commit()
    deleted = cursor.rowcount > 0
    conn.close()
    return deleted


# ---------------------------------------------------------------------------
# Conversation association
# ---------------------------------------------------------------------------

def assign_conversation(conversation_id: str, project_id: str | None) -> bool:
    """Set or clear the project_id on a conversation."""
    conn = sqlite3.connect(str(_conv_db_path()))
    cursor = conn.execute(
        "UPDATE conversations SET project_id = ? WHERE id = ?",
        (project_id, conversation_id),
    )
    conn.commit()
    updated = cursor.rowcount > 0
    conn.close()
    return updated


def get_project_conversations(project_id: str) -> list[dict]:
    """Return all conversations linked to this project, newest first."""
    conn = sqlite3.connect(str(_conv_db_path()))
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        "SELECT id, title, created_at, updated_at FROM conversations WHERE project_id = ? ORDER BY updated_at DESC",
        (project_id,),
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]


# ---------------------------------------------------------------------------
# Memory association (read from memory.db)
# ---------------------------------------------------------------------------

def get_project_memories(project_id: str) -> list[dict]:
    """Return memories that were tagged with this project_id as source_id."""
    mem_db = _data_dir() / "memory.db"
    if not mem_db.exists():
        return []
    conn = sqlite3.connect(str(mem_db))
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        "SELECT id, content, created_at FROM memories WHERE source_id = ? ORDER BY created_at DESC",
        (project_id,),
    ).fetchall()
    conn.close()
    return [dict(r) for r in rows]
