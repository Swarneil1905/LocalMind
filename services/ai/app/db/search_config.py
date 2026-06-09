"""
Web search configuration DB - Phase 5.

Stores provider choice, API key (encrypted at rest via OS keychain via Rust;
here we store only non-secret settings), privacy mode, and enabled flag.
"""

import sqlite3
from pathlib import Path

_DB_NAME = "search_config.db"


def _db_path() -> Path:
    import os
    base = os.environ.get("LOCALMIND_DATA_DIR", str(Path.home() / ".localmind"))
    return Path(base) / _DB_NAME


def _conn() -> sqlite3.Connection:
    conn = sqlite3.connect(str(_db_path()))
    conn.row_factory = sqlite3.Row
    return conn


def _init() -> None:
    conn = _conn()
    conn.execute("""
        CREATE TABLE IF NOT EXISTS search_config (
            id          INTEGER PRIMARY KEY CHECK(id = 1),
            provider    TEXT NOT NULL DEFAULT 'duckduckgo',
            privacy_mode TEXT NOT NULL DEFAULT 'standard',
            enabled     INTEGER NOT NULL DEFAULT 0
        )
    """)
    # Ensure exactly one row exists
    conn.execute("""
        INSERT OR IGNORE INTO search_config (id, provider, privacy_mode, enabled)
        VALUES (1, 'duckduckgo', 'standard', 0)
    """)
    conn.commit()
    conn.close()


_init()


def get_config() -> dict:
    conn = _conn()
    row = conn.execute("SELECT provider, privacy_mode, enabled FROM search_config WHERE id = 1").fetchone()
    conn.close()
    return {
        "provider": row["provider"],
        "privacy_mode": row["privacy_mode"],
        "enabled": bool(row["enabled"]),
    }


def update_config(
    provider: str | None = None,
    privacy_mode: str | None = None,
    enabled: bool | None = None,
) -> dict:
    conn = _conn()
    if provider is not None:
        conn.execute("UPDATE search_config SET provider = ? WHERE id = 1", (provider,))
    if privacy_mode is not None:
        conn.execute("UPDATE search_config SET privacy_mode = ? WHERE id = 1", (privacy_mode,))
    if enabled is not None:
        conn.execute("UPDATE search_config SET enabled = ? WHERE id = 1", (int(enabled),))
    conn.commit()
    conn.close()
    return get_config()
