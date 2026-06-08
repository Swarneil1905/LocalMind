"""
Memory extraction and management routes.

POST /memory/extract
    Call the speed model (1B-2B) to extract key facts from the last exchange,
    persist them, and return the updated full list.

GET  /memory/list
    Return all stored memories, newest first.

DELETE /memory/{id}
    Delete a single memory by id.
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
Read the following conversation exchange and extract 0 to 3 facts about the user that are worth remembering for future conversations.
Only extract facts that are personal, preferential, or highly specific to the user.
Return ONLY a valid JSON array of short strings (max 20 words each). Nothing else.
If nothing is worth remembering, return [].

Examples of good facts:
- "User is building a Tauri desktop app called LocalMind"
- "User prefers Python over Rust for quick scripts"
- "User's GPU is an NVIDIA RTX 4050 with 6 GB VRAM"

Examples of bad facts (too generic, skip these):
- "User asked about medieval history"
- "User greeted the assistant"

User: {user_message}
Assistant: {assistant_message}

JSON array:"""


class ExtractRequest(BaseModel):
    user_message: str
    assistant_message: str
    speed_model: str = "qwen2.5:1.5b"


def _parse_json_array(text: str) -> list[str]:
    """
    Try to extract a JSON array from model output.
    The model sometimes wraps the array in markdown code fences or adds prose.
    """
    text = text.strip()

    # Direct parse first
    try:
        result = json.loads(text)
        if isinstance(result, list):
            return [str(s) for s in result if s]
    except json.JSONDecodeError:
        pass

    # Find the first [...] block
    match = re.search(r"\[.*?\]", text, re.DOTALL)
    if match:
        try:
            result = json.loads(match.group())
            if isinstance(result, list):
                return [str(s) for s in result if s]
        except json.JSONDecodeError:
            pass

    return []


@router.post("/extract")
async def extract_memories(request: ExtractRequest) -> dict:
    prompt = EXTRACT_PROMPT.format(
        user_message=request.user_message,
        assistant_message=request.assistant_message,
    )

    payload = {
        "model": request.speed_model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
    }

    extracted: list[str] = []
    try:
        async with httpx.AsyncClient(timeout=EXTRACT_TIMEOUT) as client:
            resp = await client.post(f"{OLLAMA_BASE}/api/chat", json=payload)
            resp.raise_for_status()
            data = resp.json()
            raw = data.get("message", {}).get("content", "")
            extracted = _parse_json_array(raw)
    except Exception:
        # Extraction failure is non-fatal — just return empty
        pass

    new_rows = mem_db.insert_memories(extracted)
    all_memories = mem_db.list_all()

    return {
        "extracted": new_rows,
        "memories": all_memories,
    }


@router.get("/list")
def list_memories() -> dict:
    return {"memories": mem_db.list_all()}


@router.delete("/{memory_id}")
def delete_memory(memory_id: str) -> dict:
    deleted = mem_db.delete_by_id(memory_id)
    if not deleted:
        raise HTTPException(status_code=404, detail="Memory not found")
    return {"ok": True}
