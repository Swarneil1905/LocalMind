"""
messages.db — unified message store for all connectors.
Shared by Gmail, WhatsApp Web, iMessage, Telegram.
"""
import sqlite3
from pathlib import Path
from datetime import datetime

DB_PATH = Path.home() / ".localmind" / "messages.db"


def get_conn() -> sqlite3.Connection:
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(DB_PATH), check_same_thread=False)
    conn.row_factory = sqlite3.Row
    return conn


def init_db() -> None:
    conn = get_conn()
    conn.executescript("""
        PRAGMA journal_mode=WAL;

        -- One row per contact across all connectors
        CREATE TABLE IF NOT EXISTS contacts (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            source      TEXT NOT NULL,          -- 'gmail' | 'whatsapp' | 'imessage'
            source_id   TEXT NOT NULL,          -- email address, phone number, WA JID
            display_name TEXT,
            avatar_url  TEXT,
            first_seen  TEXT NOT NULL,
            last_seen   TEXT NOT NULL,
            UNIQUE(source, source_id)
        );

        -- Conversation threads (email threads, WhatsApp chats)
        CREATE TABLE IF NOT EXISTS threads (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            source      TEXT NOT NULL,
            source_id   TEXT NOT NULL,          -- Gmail thread_id, WA chat JID, etc.
            title       TEXT,                   -- email subject or chat name
            is_group    INTEGER DEFAULT 0,
            updated_at  TEXT NOT NULL,
            UNIQUE(source, source_id)
        );

        -- Individual messages
        CREATE TABLE IF NOT EXISTS messages (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            source      TEXT NOT NULL,          -- 'gmail' | 'whatsapp' | 'imessage'
            source_id   TEXT NOT NULL,          -- original message ID from source
            thread_id   INTEGER REFERENCES threads(id),
            contact_id  INTEGER REFERENCES contacts(id),
            direction   TEXT NOT NULL,          -- 'inbound' | 'outbound'
            body        TEXT,
            body_html   TEXT,                   -- for emails
            sent_at     TEXT NOT NULL,          -- ISO8601
            is_read     INTEGER DEFAULT 0,
            has_attachment INTEGER DEFAULT 0,
            raw_json    TEXT,                   -- full raw payload for re-processing
            UNIQUE(source, source_id)
        );

        CREATE INDEX IF NOT EXISTS idx_messages_sent_at ON messages(sent_at DESC);
        CREATE INDEX IF NOT EXISTS idx_messages_contact ON messages(contact_id);
        CREATE INDEX IF NOT EXISTS idx_messages_thread ON messages(thread_id);
        CREATE INDEX IF NOT EXISTS idx_messages_source ON messages(source);

        -- Sync state per connector (cursor for incremental sync)
        CREATE TABLE IF NOT EXISTS sync_cursors (
            source      TEXT PRIMARY KEY,
            cursor      TEXT,                   -- opaque: page token, timestamp, etc.
            last_sync   TEXT,
            items_total INTEGER DEFAULT 0
        );
    """)
    conn.commit()
    conn.close()


# ── Upsert helpers ────────────────────────────────────────────────────────────

def upsert_contact(source: str, source_id: str, display_name: str | None = None) -> int:
    conn = get_conn()
    now = datetime.utcnow().isoformat()
    conn.execute("""
        INSERT INTO contacts (source, source_id, display_name, first_seen, last_seen)
        VALUES (?, ?, ?, ?, ?)
        ON CONFLICT(source, source_id) DO UPDATE SET
            display_name = COALESCE(excluded.display_name, display_name),
            last_seen = excluded.last_seen
    """, (source, source_id, display_name, now, now))
    conn.commit()
    row = conn.execute(
        "SELECT id FROM contacts WHERE source=? AND source_id=?", (source, source_id)
    ).fetchone()
    conn.close()
    return row["id"]


def upsert_thread(source: str, source_id: str, title: str | None = None, is_group: bool = False) -> int:
    conn = get_conn()
    now = datetime.utcnow().isoformat()
    conn.execute("""
        INSERT INTO threads (source, source_id, title, is_group, updated_at)
        VALUES (?, ?, ?, ?, ?)
        ON CONFLICT(source, source_id) DO UPDATE SET
            title = COALESCE(excluded.title, title),
            updated_at = excluded.updated_at
    """, (source, source_id, title, int(is_group), now))
    conn.commit()
    row = conn.execute(
        "SELECT id FROM threads WHERE source=? AND source_id=?", (source, source_id)
    ).fetchone()
    conn.close()
    return row["id"]


def upsert_message(
    source: str,
    source_id: str,
    thread_id: int,
    contact_id: int,
    direction: str,
    body: str | None,
    sent_at: str,
    body_html: str | None = None,
    is_read: bool = False,
    has_attachment: bool = False,
    raw_json: str | None = None,
) -> int:
    conn = get_conn()
    conn.execute("""
        INSERT INTO messages
            (source, source_id, thread_id, contact_id, direction, body, body_html,
             sent_at, is_read, has_attachment, raw_json)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(source, source_id) DO NOTHING
    """, (source, source_id, thread_id, contact_id, direction, body, body_html,
          sent_at, int(is_read), int(has_attachment), raw_json))
    conn.commit()
    row = conn.execute(
        "SELECT id FROM messages WHERE source=? AND source_id=?", (source, source_id)
    ).fetchone()
    conn.close()
    return row["id"] if row else -1


def get_cursor(source: str) -> str | None:
    conn = get_conn()
    row = conn.execute("SELECT cursor FROM sync_cursors WHERE source=?", (source,)).fetchone()
    conn.close()
    return row["cursor"] if row else None


def set_cursor(source: str, cursor: str, items_total: int = 0) -> None:
    conn = get_conn()
    now = datetime.utcnow().isoformat()
    conn.execute("""
        INSERT INTO sync_cursors (source, cursor, last_sync, items_total)
        VALUES (?, ?, ?, ?)
        ON CONFLICT(source) DO UPDATE SET
            cursor = excluded.cursor,
            last_sync = excluded.last_sync,
            items_total = items_total + excluded.items_total
    """, (source, cursor, now, items_total))
    conn.commit()
    conn.close()


def recent_messages(source: str | None = None, limit: int = 50) -> list[sqlite3.Row]:
    """Fetch recent messages for Buddy's context window."""
    conn = get_conn()
    if source:
        rows = conn.execute("""
            SELECT m.*, c.display_name, t.title as thread_title
            FROM messages m
            LEFT JOIN contacts c ON m.contact_id = c.id
            LEFT JOIN threads t ON m.thread_id = t.id
            WHERE m.source = ?
            ORDER BY m.sent_at DESC LIMIT ?
        """, (source, limit)).fetchall()
    else:
        rows = conn.execute("""
            SELECT m.*, c.display_name, t.title as thread_title
            FROM messages m
            LEFT JOIN contacts c ON m.contact_id = c.id
            LEFT JOIN threads t ON m.thread_id = t.id
            ORDER BY m.sent_at DESC LIMIT ?
        """, (limit,)).fetchall()
    conn.close()
    return rows
