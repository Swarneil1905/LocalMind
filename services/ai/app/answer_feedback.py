"""Opt-in local feedback. No background uploads, model calls or training hooks."""
from contextlib import closing
from datetime import datetime, timezone
import re
from uuid import uuid4
from app.db import conversations

CATEGORIES = ("incorrect", "off_topic", "too_long", "too_brief", "unhelpful", "useful")


def _connection():
    connection = conversations._get_conn()
    connection.execute("""CREATE TABLE IF NOT EXISTS answer_feedback (
        id TEXT PRIMARY KEY, message_id TEXT NOT NULL UNIQUE
        REFERENCES conversation_messages(id) ON DELETE CASCADE,
        category TEXT NOT NULL, correction TEXT NOT NULL, updated_at TEXT NOT NULL)""")
    connection.commit()
    return connection


def record(conversation_id: str, content: str, category: str, correction: str) -> dict:
    if category not in CATEGORIES or len(correction) > 4000 or not content or len(content) > 1_000_000:
        raise ValueError("Choose a feedback category and keep the correction under 4,000 characters.")
    with closing(_connection()) as connection, connection:
        row = connection.execute(
            "SELECT id FROM conversation_messages WHERE conversation_id=? AND role='assistant' AND content=? ORDER BY rowid DESC LIMIT 1",
            (conversation_id, content),
        ).fetchone()
        if not row:
            raise ValueError("Save this completed response before adding feedback.")
        connection.execute(
            "INSERT INTO answer_feedback VALUES (?,?,?,?,?) ON CONFLICT(message_id) DO UPDATE SET category=excluded.category, correction=excluded.correction, updated_at=excluded.updated_at",
            (str(uuid4()), row["id"], category, correction.strip(), datetime.now(timezone.utc).isoformat()),
        )
        result = connection.execute("SELECT * FROM answer_feedback WHERE message_id=?", (row["id"],)).fetchone()
    return dict(result)


def list_feedback() -> list[dict]:
    with closing(_connection()) as connection:
        return [dict(row) for row in connection.execute(
            "SELECT f.*, m.conversation_id FROM answer_feedback f JOIN conversation_messages m ON m.id=f.message_id ORDER BY f.updated_at DESC LIMIT 500")]


def category_counts() -> dict:
    with closing(_connection()) as connection:
        return {row["category"]: row["total"] for row in connection.execute(
            "SELECT category, count(*) AS total FROM answer_feedback GROUP BY category")}


def delete(identifier: str):
    with closing(_connection()) as connection, connection:
        connection.execute("DELETE FROM answer_feedback WHERE id=?", (identifier,))


def export_selected(identifiers: list[str]) -> dict:
    if not identifiers or len(identifiers) > 200 or len(set(identifiers)) != len(identifiers):
        raise ValueError("Select between 1 and 200 distinct feedback records.")
    rows = {row["id"]: row for row in list_feedback()}
    if any(identifier not in rows for identifier in identifiers):
        raise ValueError("Some selected feedback no longer exists. Refresh and try again.")
    def redact(text):
        text = re.sub(r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[email removed]", text)
        return re.sub(r"(?i)\b(?:sk-|AIza|ghp_|github_pat_)[A-Za-z0-9_-]+", "[key removed]", text)
    return {"schema_version": 1, "purpose": "local-quality-review-not-training",
            "notice": "Review before sharing. Automatic redaction cannot find every private detail.",
            "records": [{"category": rows[key]["category"], "correction": redact(rows[key]["correction"])}
                        for key in identifiers]}
