"""
iMessage connector — macOS only, direct SQLite access to chat.db.

Requires: System Preferences → Security & Privacy → Full Disk Access → LocalMind

No export needed. Reads Apple's own database directly.
Apple's epoch starts 2001-01-01, not Unix epoch — all date math accounts for this.
"""
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .base import BaseConnector, ConnectorMeta, ConnectorStatus, SyncResult
from ..db.messages import (
    init_db, upsert_contact, upsert_thread, upsert_message,
    get_cursor, set_cursor,
)

CHAT_DB = Path.home() / "Library" / "Messages" / "chat.db"
# Apple epoch offset: seconds between 2001-01-01 and 1970-01-01
APPLE_EPOCH_OFFSET = 978307200


def apple_ts_to_iso(apple_ts: int) -> str:
    """Convert Apple nanosecond timestamp to ISO8601 string."""
    unix_ts = (apple_ts / 1_000_000_000) + APPLE_EPOCH_OFFSET
    return datetime.fromtimestamp(unix_ts, tz=timezone.utc).isoformat()


def iso_to_apple_ts(iso: str) -> int:
    """Convert ISO8601 string back to Apple nanosecond timestamp."""
    dt = datetime.fromisoformat(iso)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    unix_ts = dt.timestamp()
    return int((unix_ts - APPLE_EPOCH_OFFSET) * 1_000_000_000)


class IMessageConnector(BaseConnector):
    meta = ConnectorMeta(
        id="imessage",
        name="iMessage",
        description="Read your iMessages and SMS. macOS only. Requires Full Disk Access.",
        icon="🍏",
        requires_browser=False,
        platform="macos",
    )

    def _can_read_db(self) -> bool:
        try:
            conn = sqlite3.connect(str(CHAT_DB))
            conn.execute("SELECT 1 FROM chat LIMIT 1")
            conn.close()
            return True
        except Exception:
            return False

    async def status(self) -> ConnectorStatus:
        if not CHAT_DB.exists():
            return ConnectorStatus.DISCONNECTED
        if self._can_read_db():
            return ConnectorStatus.CONNECTED
        return ConnectorStatus.ERROR  # DB exists but can't read — no Full Disk Access

    async def connect(self, **kwargs) -> dict[str, Any]:
        if not CHAT_DB.exists():
            return {
                "type": "error",
                "message": "iMessage database not found. This feature is macOS only.",
            }
        if not self._can_read_db():
            return {
                "type": "permission_required",
                "message": (
                    "LocalMind needs Full Disk Access to read iMessages. "
                    "Open System Settings → Privacy & Security → Full Disk Access → enable LocalMind."
                ),
                "action": "open_system_preferences",
            }
        return {"type": "ready"}

    async def disconnect(self) -> None:
        # Nothing to revoke — just clear our cursor
        from ..db.messages import set_cursor
        set_cursor("imessage", "", 0)

    async def sync(self, cursor: str | None = None) -> SyncResult:
        init_db()
        if not self._can_read_db():
            return SyncResult(connector_id="imessage", items_synced=0, cursor=cursor or "", error="No Full Disk Access")

        stored_cursor = cursor or get_cursor("imessage")
        since_apple_ts = iso_to_apple_ts(stored_cursor) if stored_cursor else 0

        try:
            conn = sqlite3.connect(str(CHAT_DB))
            conn.row_factory = sqlite3.Row

            rows = conn.execute("""
                SELECT
                    m.rowid         AS msg_rowid,
                    m.text          AS body,
                    m.date          AS apple_ts,
                    m.is_from_me    AS is_from_me,
                    h.id            AS handle_id,
                    c.chat_identifier AS chat_id,
                    c.display_name  AS chat_display_name,
                    c.group_id      AS group_id
                FROM message m
                JOIN chat_message_join cmj ON m.rowid = cmj.message_id
                JOIN chat c ON cmj.chat_id = c.rowid
                LEFT JOIN handle h ON m.handle_id = h.rowid
                WHERE m.date > ?
                  AND m.text IS NOT NULL
                  AND m.text != ''
                ORDER BY m.date ASC
                LIMIT 500
            """, (since_apple_ts,)).fetchall()
            conn.close()

        except Exception as e:
            return SyncResult(connector_id="imessage", items_synced=0, cursor=stored_cursor or "", error=str(e))

        items_synced = 0
        for row in rows:
            sent_at = apple_ts_to_iso(row["apple_ts"])
            direction = "outbound" if row["is_from_me"] else "inbound"
            sender_id = row["handle_id"] or "me"
            chat_id = row["chat_id"] or sender_id
            is_group = bool(row["group_id"])
            chat_title = row["chat_display_name"] or chat_id

            contact_id = upsert_contact("imessage", sender_id)
            thread_id = upsert_thread("imessage", chat_id, title=chat_title, is_group=is_group)
            upsert_message(
                source="imessage",
                source_id=str(row["msg_rowid"]),
                thread_id=thread_id,
                contact_id=contact_id,
                direction=direction,
                body=row["body"],
                sent_at=sent_at,
            )
            items_synced += 1

        new_cursor = datetime.now(timezone.utc).isoformat()
        set_cursor("imessage", new_cursor, items_synced)
        return SyncResult(connector_id="imessage", items_synced=items_synced, cursor=new_cursor)

    async def context_for_buddy(self, query: str) -> str:
        from ..db.messages import recent_messages
        msgs = recent_messages(source="imessage", limit=10)
        if not msgs:
            return ""
        lines = ["**Recent iMessage (last 10):**"]
        for m in msgs:
            name = m["display_name"] or m["contact_id"]
            snippet = (m["body"] or "")[:80].replace("\n", " ")
            lines.append(f"  - [{m['sent_at'][:10]}] {name}: {snippet}…")
        return "\n".join(lines)
