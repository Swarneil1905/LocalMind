"""
Web search configuration DB - Phase 5.

Stores non-secret provider configuration and a SearxNG base URL. Legacy privacy
mode and enabled fields remain for API compatibility. This store does not save
API keys or provide keychain integration.
"""

import sqlite3
from pathlib import Path
from app.storage import data_path

_DB_NAME = "search_config.db"


def _db_path() -> Path:
    return data_path(_DB_NAME)


def _conn() -> sqlite3.Connection:
    conn = sqlite3.connect(str(_db_path()))
    conn.row_factory = sqlite3.Row
    return conn


def _init() -> None:
    conn = _conn()
    # Serialize first-run schema changes when settings and chat read together.
    conn.execute("BEGIN IMMEDIATE")
    conn.execute("""
        CREATE TABLE IF NOT EXISTS search_config (
            id          INTEGER PRIMARY KEY CHECK(id = 1),
            provider    TEXT NOT NULL DEFAULT 'duckduckgo',
            privacy_mode TEXT NOT NULL DEFAULT 'standard',
            enabled     INTEGER NOT NULL DEFAULT 0,
            searxng_url TEXT NOT NULL DEFAULT 'http://localhost:8080'
        )
    """)
    columns = {row[1] for row in conn.execute("PRAGMA table_info(search_config)")}
    if "searxng_url" not in columns:
        conn.execute("ALTER TABLE search_config ADD COLUMN searxng_url TEXT NOT NULL DEFAULT 'http://localhost:8080'")
    # Ensure exactly one row exists
    conn.execute("""
        INSERT OR IGNORE INTO search_config (id, provider, privacy_mode, enabled)
        VALUES (1, 'duckduckgo', 'standard', 0)
    """)
    conn.commit()
    conn.close()


def get_config() -> dict:
    _init()
    conn = _conn()
    row = conn.execute("SELECT provider, privacy_mode, enabled, searxng_url FROM search_config WHERE id = 1").fetchone()
    conn.close()
    return {
        "provider": row["provider"],
        "privacy_mode": row["privacy_mode"],
        "enabled": bool(row["enabled"]),
        "searxng_url": row["searxng_url"],
    }


def update_config(
    provider: str | None = None,
    privacy_mode: str | None = None,
    enabled: bool | None = None,
    searxng_url: str | None = None,
) -> dict:
    _init()
    conn = _conn()
    if provider is not None:
        conn.execute("UPDATE search_config SET provider = ? WHERE id = 1", (provider,))
    if privacy_mode is not None:
        conn.execute("UPDATE search_config SET privacy_mode = ? WHERE id = 1", (privacy_mode,))
    if enabled is not None:
        conn.execute("UPDATE search_config SET enabled = ? WHERE id = 1", (int(enabled),))
    if searxng_url is not None:
        conn.execute("UPDATE search_config SET searxng_url = ? WHERE id = 1", (searxng_url,))
    conn.commit()
    conn.close()
    return get_config()
