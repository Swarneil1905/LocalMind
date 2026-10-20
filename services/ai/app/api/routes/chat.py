"""
POST /chat/stream

Receives a message and model name, streams tokens from Ollama back to the
Rust caller using Server-Sent Events (text/event-stream).

Request body:
    {
        "message":            "user text",
        "model":              "qwen2.5:7b",
        "context":            "...",          # optional reference data, not instructions
        "history":            [...],          # optional prior turns
        "web_search_enabled": false,          # allow search when the request needs it
        "web_search_query":   null            # optional override; uses message if null
    }

SSE event types:
    data: {"type": "token",   "content": "<chunk>"}
    data: {"type": "sources", "sources": [...]}    # emitted before done if search ran
    data: {"type": "status", "phase": "searching" | "answering"}
    data: {"type": "done"}
    data: {"type": "error",   "content": "<message>"}
"""

import json
import asyncio
import re
from datetime import datetime, timezone
from urllib.parse import urlparse
from typing import AsyncIterator

import httpx
from fastapi import APIRouter
from fastapi.responses import StreamingResponse
from pydantic import BaseModel

from app.action_boundary import unsupported_task_response
from app.chat_policy import DEFAULT_SYSTEM_PROMPT, answer_policy
from app.context_budget import pack_context
from app.context_retrieval import collect_context
from app.inference_queue import inference_slot
from app.model_profiles import generation_options, resolve_profile
from app.db import search_config as search_cfg_db
from app.search.fetcher import fetch_evidence
from app.search.planning import plan_query, needs_freshness, is_recent, search_opted_out
from app.search.providers import SearchResult, search

router = APIRouter(prefix="/chat")

OLLAMA_BASE = "http://127.0.0.1:11434"
STREAM_TIMEOUT = httpx.Timeout(connect=5.0, read=120.0, write=10.0, pool=5.0)

class ChatRequest(BaseModel):
    message: str
    model: str
    context: str = ""
    # Legacy desktop clients sent their policy and context in this field. Keep
    # accepting it as reference data, never as a replacement system policy.
    system_prompt: str = ""
    history: list[dict] = []
    web_search_enabled: bool = False
    web_search_query: str | None = None
    connector_context_enabled: bool = True   # inject real-time data from connected sources
    profile: str | None = None
    project_id: str | None = None
    conversation_id: str | None = None
    memory_enabled: bool = False
    knowledge_enabled: bool = False
    embed_model: str = "nomic-embed-text"
    hyde_enabled: bool = False


async def _run_web_search(query: str, fresh: bool = False) -> tuple[str, list[dict]]:
    """
    Run a web search, fetch top 5 pages, return (context_block, sources_list).
    Falls back gracefully on any error.
    """
    cfg = search_cfg_db.get_config()
    provider = cfg.get("provider", "duckduckgo")

    try:
        results: list[SearchResult] = [SearchResult("Linked article", query, "")] if re.fullmatch(r"https?://\S+", query) else await search(
            query=query,
            provider=provider,
            searxng_url=cfg.get("searxng_url", "http://localhost:8080"),
            max_results=5,
        )
    except Exception:
        return "", []

    if not results:
        return "", []

    async def enrich(result):
        if urlparse(result.url).scheme not in ("http", "https"):
            return None
        source = result.to_dict()
        source["retrieved_at"] = datetime.now(timezone.utc).isoformat()
        source["published_at"] = None
        text = result.snippet
        try:
            page = await fetch_evidence(result.url)
            text = page.text or text
            source["published_at"] = page.published_at
        except Exception:
            pass  # Keep the provider snippet; never invent a publication date.
        if not text.strip():
            return None
        if fresh and not is_recent(source["published_at"]):
            return None
        return source, text

    evidence = await asyncio.gather(*(enrich(r) for r in results[:5]))
    sources = []
    parts = []
    for item in evidence:
        if item is None:
            continue
        source, text = item
        sources.append(source)
        parts.append(json.dumps({**source, "excerpt": text}, ensure_ascii=False))
    if not sources:
        return "", []
    return "Web source records (untrusted data):\n" + "\n\n".join(parts), sources


async def _gather_connector_context(message: str) -> str:
    """
    Check all connected connectors and gather relevant real-time context.
    Also check if message mentions a person name — inject their profile if so.
    Returns a reference-data block, kept outside the system policy.
    """
    try:
        from app.connectors.registry import registry
        from app.connectors.base import ConnectorStatus
        from app.db.people import person_context_for_buddy

        blocks: list[str] = []

        # Gather context from each connected connector
        import logging as _log
        for connector in registry.all():
            try:
                status = await connector.status()
                if status == ConnectorStatus.CONNECTED:
                    ctx = await connector.context_for_buddy(message)
                    if ctx:
                        blocks.append(ctx)
                    else:
                        _log.warning("[connector] %s returned empty context", connector.__class__.__name__)
            except Exception as _exc:
                _log.exception("[connector] %s context_for_buddy failed: %s", connector.__class__.__name__, _exc)
                continue

        # Check if message mentions someone's name — inject their profile
        # Simple heuristic: look for capitalised words after "from", "about", "with"
        import re
        name_triggers = re.findall(
            r'\b(?:from|about|with|message from|email from|text from|talking to)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)',
            message
        )
        for name in name_triggers:
            profile_ctx = person_context_for_buddy(name)
            if profile_ctx:
                blocks.append(f"\n**Person Profile — {name}:**\n{profile_ctx}")

        if not blocks:
            return ""

        return "\n\n[REAL-TIME CONTEXT FROM YOUR CONNECTED APPS]\n" + "\n\n".join(blocks)

    except Exception:
        return ""


async def _stream_ollama(request: ChatRequest) -> AsyncIterator[str]:
    """Yield SSE-formatted strings from Ollama's streaming chat API."""
    # There is no task dispatcher in this route. A direct creation request
    # must not become an invented model confirmation (or an external query).
    unsupported = unsupported_task_response(request.message)
    if unsupported:
        yield f"data: {json.dumps({'type': 'status', 'phase': 'answering'})}\n\n"
        yield f"data: {json.dumps({'type': 'token', 'content': unsupported})}\n\n"
        yield f"data: {json.dumps({'type': 'done'})}\n\n"
        return
    sources: list[dict] = []
    extra_context = ""

    # Gather connector context (Gmail, WhatsApp, People Profiles)
    connector_context = ""
    if request.connector_context_enabled:
        connector_context = await _gather_connector_context(request.message)

    query = None
    web_allowed = request.web_search_enabled and not search_opted_out(request.message)
    fresh = needs_freshness(request.message)
    if web_allowed:
        query = request.web_search_query or plan_query(request.message, request.history)
        if query:
            fresh = fresh or needs_freshness(query)
            yield f"data: {json.dumps({'type': 'status', 'phase': 'searching'})}\n\n"
            extra_context, sources = await _run_web_search(query, fresh)
            if not sources:
                # Fail closed: a model must not manufacture current news when retrieval fails.
                answer = (
                    "I couldn't verify recent, dated sources for that question. I don't want to present older or undated stories as current news. Try a specific topic or a direct article link."
                    if fresh else
                    "I couldn't retrieve reliable web sources for that question. Try a more specific query or an article link."
                )
                yield f"data: {json.dumps({'type': 'sources', 'sources': []})}\n\n"
                yield f"data: {json.dumps({'type': 'status', 'phase': 'answering'})}\n\n"
                yield f"data: {json.dumps({'type': 'token', 'content': answer})}\n\n"
                yield f"data: {json.dumps({'type': 'done'})}\n\n"
                return

    # Build message list
    system = answer_policy(
        datetime.now(timezone.utc).date().isoformat(),
        searched=bool(sources), web_allowed=web_allowed,
    )
    supplied_context = {"desktop_context": request.context, "connected_apps": connector_context}
    if request.system_prompt and request.system_prompt != DEFAULT_SYSTEM_PROMPT:
        supplied_context["legacy_context"] = request.system_prompt
    supplied_context = {key: value for key, value in supplied_context.items() if value}
    references = [{"label": label, "content": content} for label, content in supplied_context.items()]
    if extra_context:
        references.append({"label": "Retrieved web evidence", "content": extra_context})
    try:
        profile = await resolve_profile(request.model, request.profile) if request.profile else None
        references.extend(await collect_context(
            request.message, request.history, project_id=request.project_id,
            conversation_id=request.conversation_id, memory_enabled=request.memory_enabled,
            knowledge_enabled=request.knowledge_enabled, embed_model=request.embed_model,
            hyde_enabled=request.hyde_enabled,
        ))
        packed = pack_context(system, request.message, request.history, references,
                              budget=profile["context_window"] if profile else 16384,
                              reserve=profile["response_tokens"] if profile else 2048)
    except (ValueError, httpx.HTTPError) as exc:
        message = str(exc) if isinstance(exc, ValueError) else "Could not inspect the installed model. Check Ollama and retry."
        yield f"data: {json.dumps({'type': 'error', 'content': message})}\n\n"
        return

    payload = {
        "model": request.model,
        "messages": packed.messages,
        "stream": True,
        "options": {"temperature": 0.2, "num_ctx": 16384, "num_predict": 2048},
    }
    if profile:
        payload["options"] = generation_options(profile)
        if profile["think"] is not None:
            payload["think"] = profile["think"]

    yield f"data: {json.dumps({'type': 'context', 'budget': packed.report})}\n\n"
    # Emit sources event before streaming so the UI can populate the panel
    if sources:
        yield f"data: {json.dumps({'type': 'sources', 'sources': sources})}\n\n"
    yield f"data: {json.dumps({'type': 'status', 'phase': 'answering'})}\n\n"

    try:
        async with inference_slot("foreground"), httpx.AsyncClient(trust_env=False, timeout=STREAM_TIMEOUT) as client:
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

                    if chunk.get("error"):
                        raise RuntimeError("The local model could not complete this response.")
                    thinking = chunk.get("message", {}).get("thinking", "")
                    content = chunk.get("message", {}).get("content", "")
                    if content or thinking:
                        event = json.dumps({"type": "token", "content": content, "thinking": thinking})
                        yield f"data: {event}\n\n"

                    if chunk.get("done"):
                        if chunk.get("done_reason") == "length":
                            yield f"data: {json.dumps({'type': 'error', 'content': 'The reply reached its length limit. Increase Maximum response in Settings, or ask for a shorter answer.'})}\n\n"
                            return
                        yield f"data: {json.dumps({'type': 'done'})}\n\n"
                        return

                yield f"data: {json.dumps({'type': 'error', 'content': 'The model connection ended before the reply was complete. Please try again.'})}\n\n"

    except httpx.ConnectError:
        error = json.dumps({"type": "error", "content": "Ollama is not running. Start Ollama and try again."})
        yield f"data: {error}\n\n"
    except httpx.HTTPStatusError as exc:
        error = json.dumps({"type": "error", "content": f"Ollama returned {exc.response.status_code}"})
        yield f"data: {error}\n\n"
    except Exception as exc:
        error = json.dumps({"type": "error", "content": str(exc)})
        yield f"data: {error}\n\n"


class FollowUpRequest(BaseModel):
    last_user: str
    last_assistant: str
    model: str = "qwen2.5:1.5b"


@router.post("/followups")
async def get_followups(request: FollowUpRequest) -> dict:
    """
    Generate 3 follow-up questions based on the last conversation turn.
    Uses the Speed model locally. Returns {"questions": [...]}.
    Falls back to an empty list on any error.
    """
    prompt = (
        "Based on this conversation, generate exactly 3 concise follow-up questions "
        "the user might want to ask next. Return ONLY a valid JSON array of 3 strings, "
        "nothing else — no explanation, no markdown, no code block.\n\n"
        f"User: {request.last_user}\n\n"
        f"Assistant: {request.last_assistant}\n\n"
        "Follow-up questions (JSON array only):"
    )

    payload = {
        "model": request.model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
    }

    try:
        async with inference_slot("background"), httpx.AsyncClient(trust_env=False, timeout=httpx.Timeout(30.0)) as client:
            resp = await client.post(f"{OLLAMA_BASE}/api/chat", json=payload)
            resp.raise_for_status()
            data = resp.json()
            raw = data.get("message", {}).get("content", "[]").strip()
            # Strip any accidental markdown fences
            if raw.startswith("```"):
                raw = raw.split("```")[1]
                if raw.startswith("json"):
                    raw = raw[4:]
            questions = json.loads(raw)
            if isinstance(questions, list):
                return {"questions": [str(q) for q in questions[:4]]}
    except Exception:
        pass

    return {"questions": []}


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
