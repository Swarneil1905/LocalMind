"""
people.db — structured persona profiles for every person Buddy knows about.
Populated by the People Profiles extractor (runs over Gmail + WhatsApp data).
LanceDB vector index lives alongside for semantic search.
"""
import json
import sqlite3
from pathlib import Path
from datetime import datetime

DB_PATH = Path.home() / ".localmind" / "people.db"


def get_conn() -> sqlite3.Connection:
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(DB_PATH), check_same_thread=False)
    conn.row_factory = sqlite3.Row
    return conn


def init_db() -> None:
    conn = get_conn()
    conn.executescript("""
        PRAGMA journal_mode=WAL;

        -- Core profile record
        CREATE TABLE IF NOT EXISTS people (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            canonical_name  TEXT NOT NULL,
            aliases         TEXT DEFAULT '[]',     -- JSON array of alternate names
            identifiers     TEXT DEFAULT '{}',     -- JSON: {email, phone, wa_jid, ...}
            relationship    TEXT,                  -- 'friend' | 'family' | 'colleague' | 'acquaintance' | ...
            tags            TEXT DEFAULT '[]',     -- JSON array: ['work', 'uni', 'close friend']
            bio             TEXT,                  -- Buddy's 1-paragraph summary of who this person is
            communication_style TEXT,             -- 'brief and direct' | 'warm and chatty' | ...
            key_facts       TEXT DEFAULT '[]',     -- JSON array of notable facts Buddy extracted
            last_contact    TEXT,                  -- ISO8601
            contact_frequency TEXT,               -- 'daily' | 'weekly' | 'monthly' | 'rarely'
            sources         TEXT DEFAULT '[]',     -- JSON: which connectors this came from
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_people_name ON people(canonical_name);

        -- Interaction log: key moments extracted from messages
        CREATE TABLE IF NOT EXISTS interactions (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            person_id   INTEGER REFERENCES people(id) ON DELETE CASCADE,
            source      TEXT NOT NULL,             -- 'gmail' | 'whatsapp'
            summary     TEXT NOT NULL,             -- AI-generated 1-2 sentence summary of this interaction
            happened_at TEXT NOT NULL,
            sentiment   TEXT,                      -- 'positive' | 'neutral' | 'negative'
            topics      TEXT DEFAULT '[]'          -- JSON array
        );

        CREATE INDEX IF NOT EXISTS idx_interactions_person ON interactions(person_id);
        CREATE INDEX IF NOT EXISTS idx_interactions_date ON interactions(happened_at DESC);
    """)
    conn.commit()
    conn.close()


# ── CRUD ──────────────────────────────────────────────────────────────────────

def upsert_person(
    canonical_name: str,
    identifiers: dict,
    relationship: str | None = None,
    tags: list[str] | None = None,
    bio: str | None = None,
    communication_style: str | None = None,
    key_facts: list[str] | None = None,
    sources: list[str] | None = None,
) -> int:
    conn = get_conn()
    now = datetime.utcnow().isoformat()

    # Check if person already exists by any identifier
    existing_id = None
    for key, val in identifiers.items():
        row = conn.execute(
            "SELECT id, identifiers FROM people WHERE json_extract(identifiers, ?) = ?",
            (f"$.{key}", val)
        ).fetchone()
        if row:
            existing_id = row["id"]
            break

    if existing_id:
        # Merge new data in
        existing = conn.execute("SELECT * FROM people WHERE id=?", (existing_id,)).fetchone()
        merged_identifiers = {**json.loads(existing["identifiers"]), **identifiers}
        merged_tags = list(set(json.loads(existing["tags"]) + (tags or [])))
        merged_facts = list(set(json.loads(existing["key_facts"]) + (key_facts or [])))
        merged_sources = list(set(json.loads(existing["sources"]) + (sources or [])))

        conn.execute("""
            UPDATE people SET
                identifiers = ?,
                relationship = COALESCE(?, relationship),
                tags = ?,
                bio = COALESCE(?, bio),
                communication_style = COALESCE(?, communication_style),
                key_facts = ?,
                sources = ?,
                updated_at = ?
            WHERE id = ?
        """, (
            json.dumps(merged_identifiers),
            relationship,
            json.dumps(merged_tags),
            bio,
            communication_style,
            json.dumps(merged_facts),
            json.dumps(merged_sources),
            now,
            existing_id,
        ))
        conn.commit()
        conn.close()
        return existing_id
    else:
        cursor = conn.execute("""
            INSERT INTO people
                (canonical_name, identifiers, relationship, tags, bio,
                 communication_style, key_facts, sources, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, (
            canonical_name,
            json.dumps(identifiers),
            relationship,
            json.dumps(tags or []),
            bio,
            communication_style,
            json.dumps(key_facts or []),
            json.dumps(sources or []),
            now,
            now,
        ))
        conn.commit()
        person_id = cursor.lastrowid
        conn.close()
        return person_id


def add_interaction(
    person_id: int,
    source: str,
    summary: str,
    happened_at: str,
    sentiment: str | None = None,
    topics: list[str] | None = None,
) -> None:
    conn = get_conn()
    conn.execute("""
        INSERT INTO interactions (person_id, source, summary, happened_at, sentiment, topics)
        VALUES (?, ?, ?, ?, ?, ?)
    """, (person_id, source, summary, happened_at, sentiment, json.dumps(topics or [])))
    conn.commit()
    conn.close()


def get_person_by_name(name: str) -> sqlite3.Row | None:
    conn = get_conn()
    row = conn.execute(
        "SELECT * FROM people WHERE canonical_name LIKE ? LIMIT 1", (f"%{name}%",)
    ).fetchone()
    conn.close()
    return row


def get_person_by_identifier(key: str, value: str) -> sqlite3.Row | None:
    conn = get_conn()
    row = conn.execute(
        "SELECT * FROM people WHERE json_extract(identifiers, ?) = ?",
        (f"$.{key}", value)
    ).fetchone()
    conn.close()
    return row


def get_recent_interactions(person_id: int, limit: int = 5) -> list[sqlite3.Row]:
    conn = get_conn()
    rows = conn.execute(
        "SELECT * FROM interactions WHERE person_id=? ORDER BY happened_at DESC LIMIT ?",
        (person_id, limit)
    ).fetchall()
    conn.close()
    return rows


def all_people(limit: int = 100) -> list[sqlite3.Row]:
    conn = get_conn()
    rows = conn.execute(
        "SELECT * FROM people ORDER BY last_contact DESC NULLS LAST LIMIT ?", (limit,)
    ).fetchall()
    conn.close()
    return rows


def person_context_for_buddy(name: str) -> str:
    """Return a short text block Buddy can use when the user mentions someone."""
    person = get_person_by_name(name)
    if not person:
        return ""
    interactions = get_recent_interactions(person["id"], limit=3)
    facts = json.loads(person["key_facts"] or "[]")
    tags = json.loads(person["tags"] or "[]")

    lines = [f"**{person['canonical_name']}**"]
    if person["relationship"]:
        lines.append(f"Relationship: {person['relationship']}")
    if tags:
        lines.append(f"Tags: {', '.join(tags)}")
    if person["bio"]:
        lines.append(f"Bio: {person['bio']}")
    if person["communication_style"]:
        lines.append(f"Communication style: {person['communication_style']}")
    if facts:
        lines.append("Key facts: " + "; ".join(facts[:5]))
    if interactions:
        lines.append("Recent interactions:")
        for i in interactions:
            lines.append(f"  - [{i['happened_at'][:10]}] {i['summary']}")
    return "\n".join(lines)
