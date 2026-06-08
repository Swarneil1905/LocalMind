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
Task: extract 0-2 personal facts about the USER from the conversation below.

STRICT rules - violating any rule means return []:
1. Facts must be about the USER only, never about the assistant.
2. Each fact must be a full sentence of at least 6 words.
3. Only extract concrete facts: user's name, job, tools, preferences, projects, skills, or explicit opinions.
4. NEVER extract: single words, generic phrases, greetings, topics the assistant listed, or anything the assistant said.
5. If the user message is a greeting, a status question, or contains no personal facts, return [].
6. Return ONLY a raw JSON array of strings. No markdown, no explanation.

Good examples (only if the user actually stated these):
["User is building a Tauri desktop app named LocalMind in Rust"]
["User prefers Python over Rust for writing quick scripts"]

Bad examples - NEVER return these:
["Memory Management", "project management", "Hello", "as usual", "to assist", "User is asking a question"]

User said: {user_message}
Assistant said: {assistant_message}

JSON:"""


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
        # Extraction failure is non-fatal - just return empty
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
