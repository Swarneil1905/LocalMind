"""Memory "dream" pass: merge exact repeats and drop records of the chat itself.

It never rewrites a memory and never calls a model. Everything it removes is first saved to a
backup file in the profile, so a pass can be undone by hand.
"""
import json
import logging
import re
from datetime import datetime, timezone

from app.db import memories
from app.storage import data_path

logger = logging.getLogger("localmind.dream")


def _key(content: str) -> str:
    text = re.sub(r"[^a-z0-9 ]+", " ", content.lower())
    return re.sub(r"^(?:the )?user(?:s)? ", "", re.sub(r"\s+", " ", text).strip())


def dream_pass() -> dict:
    from app.api.routes.memory import _ABOUT_THE_CHAT  # same rule the save gate uses

    rows = sorted(memories.list_all(), key=lambda row: row.get("created_at") or "")
    seen: dict[tuple, str] = {}
    doomed: list[dict] = []
    for row in rows:
        content = row.get("content", "")
        if _ABOUT_THE_CHAT.search(content):
            doomed.append({**row, "why": "record of what was said"})
            continue
        # Same wording in the same scope: keep the oldest, drop the repeats.
        scope = (_key(content), row.get("project_id"), row.get("conversation_id") if row.get("project_id") is None else None)
        if scope in seen:
            doomed.append({**row, "why": f"repeat of {seen[scope]}"})
        else:
            seen[scope] = row["id"]
    if doomed:
        backup = data_path("memory-backups") / f"dream-{datetime.now(timezone.utc):%Y%m%d-%H%M%S}.json"
        backup.parent.mkdir(parents=True, exist_ok=True)
        backup.write_text(json.dumps(doomed, indent=1, ensure_ascii=False), encoding="utf-8")
        for row in doomed:
            memories.delete_by_id(row["id"])
        logger.info("[Dream] removed %d of %d memories; backup %s", len(doomed), len(rows), backup.name)
    return {"checked": len(rows), "removed": len(doomed)}
