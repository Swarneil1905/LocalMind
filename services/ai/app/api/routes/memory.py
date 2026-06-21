"""
Memory extraction and management routes.

POST /memory/extract
    Call the speed model (1B-2B) to extract key facts from the last exchange,
    persist them, propose links between related memories, and return the updated
    full list.

GET  /memory/list
    Return all stored memories, newest first.

DELETE /memory/{id}
    Delete a single memory by id.

POST /memory/links
    Create a directed link between two memories.

GET  /memory/links
    Return all memory links.

GET  /memory/{id}/links
    Return all links for a specific memory.

DELETE /memory/link/{link_id}
    Delete a link by id.
"""

import json
import re

import httpx
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from app.db import memories as mem_db

router = APIRouter(prefix="/memory")

OLLAMA_BASE = "http://127.0.0.1:11434"
EXTRACT_TIMEOUT = httpx.Timeout(connect=5.0, read=60.0, write=5.0, pool=5.0)

EXTRACT_PROMPT = """\
Task: extract 0-2 personal facts about the USER from the conversation below, \
then propose 0-2 links between the NEW facts and EXISTING memories.

STRICT rules for facts:
1. Facts must be about the USER only, never about the assistant.
2. Each fact must be a full sentence of at least 6 words.
3. Only extract concrete facts: user's name, job, tools, preferences, projects, skills, or explicit opinions.
4. NEVER extract: single words, generic phrases, greetings, or anything the assistant said.
5. If the user message contains no personal facts, use facts=[].

Link rules:
- A link connects a NEW fact (by its 0-based index in facts[]) to an EXISTING memory (by its id).
- Only propose a link if it is clearly meaningful (elaborates, part_of, contradicts, follows_from, related_to).
- If no meaningful links exist, use links=[].

Existing memories (id: content):
{existing_memories}

User said: {user_message}
Assistant said: {assistant_message}

Return ONLY a raw JSON object with this exact shape — no markdown, no explanation:
{{"facts": [...], "links": [{{"from_new": 0, "to_id": "<existing_memory_id>", "relation": "elaborates"}}]}}"""


LINK_PROMPT = """\
Given these two memory facts, choose the single best relation type from:
  related_to, part_of, elaborates, contradicts, follows_from

Memory A: {memory_a}
Memory B: {memory_b}

Reply with ONLY the relation word, nothing else."""


class ExtractRequest(BaseModel):
    user_message: str
    assistant_message: str
    speed_model: str = "qwen2.5:1.5b"


class CreateLinkRequest(BaseModel):
    from_id: str
    to_id: str
    relation: str = "related_to"


def _parse_json_array(text: str) -> list[str]:
    """Try to extract a JSON array from model output (legacy helper)."""
    text = text.strip()
    try:
        result = json.loads(text)
        if isinstance(result, list):
            return [str(s) for s in result if s]
    except json.JSONDecodeError:
        pass
    match = re.search(r"\[.*?\]", text, re.DOTALL)
    if match:
        try:
            result = json.loads(match.group())
            if isinstance(result, list):
                return [str(s) for s in result if s]
        except json.JSONDecodeError:
            pass
    return []


def _parse_extract_response(text: str) -> tuple[list[str], list[dict]]:
    """
    Parse the new extraction response format:
        {"facts": [...], "links": [{"from_new": 0, "to_id": "...", "relation": "..."}]}

    Falls back gracefully:
    - If it's still a plain JSON array, treat it as facts with no links.
    - If parsing fails entirely, return ([], []).
    """
    text = text.strip()

    # Try to find a JSON object first
    obj_match = re.search(r"\{.*\}", text, re.DOTALL)
    if obj_match:
        try:
            data = json.loads(obj_match.group())
            facts = [str(s) for s in data.get("facts", []) if s]
            links = [
                lnk for lnk in data.get("links", [])
                if isinstance(lnk, dict)
                and isinstance(lnk.get("from_new"), int)
                and isinstance(lnk.get("to_id"), str)
                and isinstance(lnk.get("relation"), str)
            ]
            return facts, links
        except (json.JSONDecodeError, AttributeError):
            pass

    # Fallback: plain array (old format or partial model output)
    return _parse_json_array(text), []


@router.post("/extract")
async def extract_memories(request: ExtractRequest) -> dict:
    # Build existing memories summary for the prompt (cap at 20 most recent)
    existing = mem_db.list_all()[:20]
    existing_str = "\n".join(f"{m['id']}: {m['content']}" for m in existing) or "(none)"

    prompt = EXTRACT_PROMPT.format(
        user_message=request.user_message,
        assistant_message=request.assistant_message,
        existing_memories=existing_str,
    )

    payload = {
        "model": request.speed_model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
    }

    extracted_texts: list[str] = []
    proposed_links: list[dict] = []
    try:
        async with httpx.AsyncClient(timeout=EXTRACT_TIMEOUT) as client:
            resp = await client.post(f"{OLLAMA_BASE}/api/chat", json=payload)
            resp.raise_for_status()
            data = resp.json()
            raw = data.get("message", {}).get("content", "")
            extracted_texts, proposed_links = _parse_extract_response(raw)
    except Exception:
        pass  # Extraction failure is non-fatal

    new_rows = mem_db.insert_memories(extracted_texts)

    # Create links proposed by the model (from_new index => existing memory id)
    created_links: list[dict] = []
    for lnk in proposed_links:
        idx = lnk.get("from_new", -1)
        to_id = lnk.get("to_id", "")
        relation = lnk.get("relation", "related_to")
        if 0 <= idx < len(new_rows) and to_id:
            result = mem_db.create_link(new_rows[idx]["id"], to_id, relation)
            if result:
                created_links.append(result)

    all_memories = mem_db.list_all()

    return {
        "extracted": new_rows,
        "memories": all_memories,
        "links_created": created_links,
    }


@router.get("/list")
def list_memories() -> dict:
    return {"memories": mem_db.list_all()}


@router.delete("/all")
def delete_all_memories() -> dict:
    count = mem_db.delete_all()
    return {"ok": True, "deleted": count}


@router.delete("/{memory_id}")
def delete_memory(memory_id: str) -> dict:
    deleted = mem_db.delete_by_id(memory_id)
    if not deleted:
        raise HTTPException(status_code=404, detail="Memory not found")
    return {"ok": True}


# ---------------------------------------------------------------------------
# Memory link routes
# ---------------------------------------------------------------------------

@router.post("/links")
def create_link(request: CreateLinkRequest) -> dict:
    """Create a directed link between two memories."""
    if request.relation not in mem_db.VALID_RELATIONS:
        raise HTTPException(
            status_code=400,
            detail=f"relation must be one of: {', '.join(sorted(mem_db.VALID_RELATIONS))}",
        )
    link = mem_db.create_link(request.from_id, request.to_id, request.relation)
    if link is None:
        raise HTTPException(status_code=404, detail="One or both memories not found, or invalid relation")
    return link


@router.get("/links")
def list_links() -> dict:
    """Return all memory links."""
    return {"links": mem_db.list_all_links()}


@router.get("/{memory_id}/links")
def get_memory_links(memory_id: str) -> dict:
    """Return all links involving a specific memory."""
    return {"links": mem_db.get_links_for_memory(memory_id)}


@router.delete("/link/{link_id}")
def delete_link(link_id: str) -> dict:
    """Delete a memory link by id."""
    deleted = mem_db.delete_link(link_id)
    if not deleted:
        raise HTTPException(status_code=404, detail="Link not found")
    return {"ok": True}
