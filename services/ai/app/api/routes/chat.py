"""
POST /chat/stream

Receives a message and model name, streams tokens from Ollama back to the
Rust caller using Server-Sent Events (text/event-stream).

Request body:
    {
        "message":       "user text",
        "model":         "qwen2.5:7b",
        "system_prompt": "...",          # optional
        "history":       [               # optional, prior turns
            {"role": "user",      "content": "..."},
            {"role": "assistant", "content": "..."}
        ]
    }

Each SSE event is one of:
    data: {"type": "token",  "content": "<chunk>"}
    data: {"type": "done"}
    data: {"type": "error",  "content": "<message>"}
"""

import json
from typing import AsyncIterator

import httpx
from fastapi import APIRouter
from fastapi.responses import StreamingResponse
from pydantic import BaseModel

router = APIRouter(prefix="/chat")

OLLAMA_BASE = "http://127.0.0.1:11434"
# 120 s read timeout: long enough for slow generation, short enough to surface hangs
STREAM_TIMEOUT = httpx.Timeout(connect=5.0, read=120.0, write=10.0, pool=5.0)

DEFAULT_SYSTEM_PROMPT = (
    "You are LocalMind, a private desktop AI assistant. "
    "You help the user with memory, projects, documents, and work tasks. "
    "You are precise, direct, and do not add unnecessary commentary."
)


class ChatRequest(BaseModel):
    message: str
    model: str
    system_prompt: str = DEFAULT_SYSTEM_PROMPT
    history: list[dict] = []


async def _stream_ollama(request: ChatRequest) -> AsyncIterator[str]:
    """Yield SSE-formatted strings from Ollama's streaming chat API."""
    messages = [{"role": "system", "content": request.system_prompt}]
    messages.extend(request.history)
    messages.append({"role": "user", "content": request.message})

    payload = {
        "model": request.model,
        "messages": messages,
        "stream": True,
    }

    try:
        async with httpx.AsyncClient(timeout=STREAM_TIMEOUT) as client:
            async with client.stream(
                "POST",
                f"{OLLAMA_BASE}/api/chat",
                json=payload,
            ) as resp:
                resp.raise_for_status()
                async for line in resp.aiter_lines():
                    if not line:
                        continue
                    try:
                        chunk = json.loads(line)
                    except json.JSONDecodeError:
                        continue

                    content = chunk.get("message", {}).get("content", "")
                    if content:
                        event = json.dumps({"type": "token", "content": content})
                        yield f"data: {event}\n\n"

                    if chunk.get("done"):
                        yield f"data: {json.dumps({'type': 'done'})}\n\n"
                        return

    except httpx.ConnectError:
        error = json.dumps({"type": "error", "content": "Ollama is not running. Start Ollama and try again."})
        yield f"data: {error}\n\n"
    except httpx.HTTPStatusError as exc:
        error = json.dumps({"type": "error", "content": f"Ollama returned {exc.response.status_code}"})
        yield f"data: {error}\n\n"
    except Exception as exc:
        error = json.dumps({"type": "error", "content": str(exc)})
        yield f"data: {error}\n\n"


@router.post("/stream")
async def chat_stream(request: ChatRequest) -> StreamingResponse:
    return StreamingResponse(
        _stream_ollama(request),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "X-Accel-Buffering": "no",
        },
    )
