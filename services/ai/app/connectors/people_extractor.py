"""
People Profiles extractor.

Runs after each connector sync. Looks at messages.db, groups messages by contact,
calls Qwen3 8B to extract a structured persona, stores in people.db.

This is what lets Buddy say:
  "Priya usually replies quickly and keeps things brief — she mentioned her presentation
   deadline twice this week. Should I draft something short?"
"""
import json
from datetime import datetime

import httpx

from ..db.messages import get_conn as get_messages_conn
from ..db.people import upsert_person, add_interaction, get_person_by_identifier

OLLAMA_URL = "http://127.0.0.1:11434/api/generate"
EXTRACTION_MODEL = "qwen3:8b"


async def extract_profiles_from_source(source: str, limit_contacts: int = 20) -> int:
    """
    For each contact in messages.db (from `source`), gather their recent messages
    and ask Qwen3 to build a profile. Returns count of profiles upserted.
    """
    conn = get_messages_conn()
    contacts = conn.execute("""
        SELECT c.id, c.source_id, c.display_name
        FROM contacts c
        WHERE c.source = ?
        ORDER BY c.last_seen DESC
        LIMIT ?
    """, (source, limit_contacts)).fetchall()
    conn.close()

    profiles_built = 0
    for contact in contacts:
        try:
            profile = await _build_profile_for_contact(source, contact["id"], contact["source_id"], contact["display_name"])
            if profile:
                profiles_built += 1
        except Exception as e:
            print(f"[PeopleExtractor] Failed for {contact['display_name']}: {e}")

    return profiles_built


async def _build_profile_for_contact(source: str, contact_db_id: int, source_id: str, display_name: str | None) -> dict | None:
    """Fetch messages for one contact and extract a profile via LLM."""
    conn = get_messages_conn()
    messages = conn.execute("""
        SELECT m.direction, m.body, m.sent_at, t.title as thread_title
        FROM messages m
        LEFT JOIN threads t ON m.thread_id = t.id
        WHERE m.contact_id = ? AND m.body IS NOT NULL
        ORDER BY m.sent_at DESC
        LIMIT 40
    """, (contact_db_id,)).fetchall()
    conn.close()

    if not messages:
        return None

    # Format messages for the prompt
    msg_text = "\n".join([
        f"[{m['sent_at'][:10]}] {'Me' if m['direction'] == 'outbound' else (display_name or 'Them')}: {(m['body'] or '')[:200]}"
        for m in reversed(messages)
    ])

    prompt = f"""You are analyzing communication history to build a persona profile.
Here are messages between me and {display_name or 'this person'} via {source}:

{msg_text}

Based ONLY on these messages, extract a JSON profile with these fields:
{{
  "relationship": "friend|family|colleague|acquaintance|romantic|unknown",
  "communication_style": "brief description of how they communicate",
  "key_facts": ["list", "of", "notable", "facts", "mentioned"],
  "topics": ["recurring", "topics"],
  "sentiment": "generally positive|neutral|mixed|negative",
  "contact_frequency": "daily|weekly|monthly|rarely",
  "bio": "1-2 sentence summary of who this person is based on conversations"
}}

Return ONLY valid JSON, no explanation."""

    try:
        async with httpx.AsyncClient(timeout=30) as client:
            resp = await client.post(OLLAMA_URL, json={
                "model": EXTRACTION_MODEL,
                "prompt": prompt,
                "stream": False,
                "format": "json",
            })
            result = resp.json()
            extracted = json.loads(result.get("response", "{}"))
    except Exception as e:
        print(f"[PeopleExtractor] LLM call failed: {e}")
        return None

    if not extracted:
        return None

    # Determine identifier key by source
    id_key = {
        "gmail": "email",
        "whatsapp_web": "wa_jid",
        "imessage": "phone",
    }.get(source, "source_id")

    person_id = upsert_person(
        canonical_name=display_name or source_id,
        identifiers={id_key: source_id},
        relationship=extracted.get("relationship"),
        tags=extracted.get("topics", []),
        bio=extracted.get("bio"),
        communication_style=extracted.get("communication_style"),
        key_facts=extracted.get("key_facts", []),
        sources=[source],
    )

    # Add a summary interaction for recent messages
    if messages:
        most_recent = messages[0]
        add_interaction(
            person_id=person_id,
            source=source,
            summary=f"Recent {source} conversation. Topics: {', '.join(extracted.get('topics', [])[:3]) or 'general'}.",
            happened_at=most_recent["sent_at"],
            sentiment=extracted.get("sentiment"),
            topics=extracted.get("topics", []),
        )

    return extracted


async def run_extraction_pipeline() -> dict:
    """Run extraction for all sources that have data. Called after sync."""
    sources = ["gmail", "whatsapp_web", "imessage"]
    total = 0
    results = {}
    for source in sources:
        try:
            count = await extract_profiles_from_source(source)
            results[source] = count
            total += count
        except Exception as e:
            results[source] = f"error: {e}"
    return {"total_profiles": total, "by_source": results}
