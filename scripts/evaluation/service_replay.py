"""Isolated replay of the real chat service using synthetic retrieval fixtures.

Run one case per worker process. Only the loopback Ollama chat endpoint is
reachable through the instrumented HTTP client. No model thinking is persisted.
"""

import argparse
import asyncio
import copy
import json
import math
import os
import sys
import tempfile
import time
from contextlib import ExitStack
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

import httpx


ROOT = Path(__file__).resolve().parents[2]
OLLAMA_CHAT_URL = "http://127.0.0.1:11434/api/chat"
CHECKPOINT_INTERVAL_SECONDS = 1.0
CHECKPOINT_REPLACE_DELAYS = (0.02, 0.05, 0.1, 0.2, 0.4)
REAL_ASYNC_CLIENT = httpx.AsyncClient
METRIC_FIELDS = (
    "total_duration", "load_duration", "prompt_eval_count", "prompt_eval_duration",
    "eval_count", "eval_duration",
)
_REPLAY_ACTIVE = False


class BlockedNetworkRequest(RuntimeError):
    pass


def utc_clock(value: str) -> datetime:
    if not isinstance(value, str):
        raise ValueError("A fixed UTC clock is required")
    moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if moment.utcoffset() is None:
        raise ValueError("The clock must include a timezone")
    return moment.astimezone(timezone.utc)


def frozen_datetime(moment: datetime):
    class FrozenDatetime(datetime):
        @classmethod
        def now(cls, tz=None):
            return moment.astimezone(tz) if tz else moment.replace(tzinfo=None)

        @classmethod
        def utcnow(cls):
            return moment.replace(tzinfo=None)

    return FrozenDatetime


def load_service():
    # This check must precede imports: service modules may initialize stores.
    profile = os.environ.get("LOCALMIND_DATA_DIR", "")
    if not profile or not Path(profile).is_absolute():
        raise ValueError("Set an absolute isolated LOCALMIND_DATA_DIR before service imports")
    service_path = str(ROOT / "services" / "ai")
    if service_path not in sys.path:
        sys.path.insert(0, service_path)
    from app.api.routes import chat
    from app.search import planning
    from app.search.fetcher import PageEvidence
    return chat, planning, PageEvidence


class ThinkingFilter:
    """Remove legacy split <think> blocks as well as counting their characters."""

    def __init__(self):
        self.pending = ""
        self.inside = False
        self.characters = 0

    def feed(self, text: str) -> str:
        self.pending += text
        answer = []
        while self.pending:
            marker = "</think>" if self.inside else "<think>"
            index = self.pending.find(marker)
            if index >= 0:
                body = self.pending[:index]
                if self.inside:
                    self.characters += len(body)
                else:
                    answer.append(body)
                self.pending = self.pending[index + len(marker):]
                self.inside = not self.inside
                continue
            held = next((n for n in range(len(marker) - 1, 0, -1)
                         if self.pending.endswith(marker[:n])), 0)
            body = self.pending[:-held] if held else self.pending
            if self.inside:
                self.characters += len(body)
            else:
                answer.append(body)
            self.pending = self.pending[-held:] if held else ""
            break
        return "".join(answer)

    def finish(self) -> str:
        pending, self.pending = self.pending, ""
        if self.inside:
            self.characters += len(pending)
            return ""
        return pending


class ObservedStream(httpx.AsyncByteStream):
    def __init__(self, stream, result):
        self.stream = stream
        self.result = result
        self.pending = b""

    def observe(self, line):
        try:
            chunk = json.loads(line)
        except (ValueError, UnicodeError):
            return
        if not isinstance(chunk, dict):
            return
        trace = self.result["trace"]
        if chunk.get("error"):
            trace["ollama_error"] = True
        if chunk.get("done") is True:
            trace["ollama_done"] = True
            reason = chunk.get("done_reason")
            trace["done_reason"] = reason if isinstance(reason, str) else None
            trace["ollama_metrics"] = {
                key: chunk[key] for key in METRIC_FIELDS
                if type(chunk.get(key)) is int and chunk[key] >= 0
            }
            if reason == "length":
                self.result["truncated"] = True

    async def __aiter__(self):
        async for data in self.stream:
            self.pending += data
            while b"\n" in self.pending:
                line, self.pending = self.pending.split(b"\n", 1)
                self.observe(line)
            # Bound temporary raw buffering; it is never written to the report.
            if len(self.pending) > 4 * 1024 * 1024:
                raise RuntimeError("Oversized Ollama stream frame")
            yield data
        if self.pending:
            self.observe(self.pending)
            self.pending = b""

    async def aclose(self):
        self.pending = b""
        await self.stream.aclose()


class RestrictedTransport(httpx.AsyncBaseTransport):
    def __init__(self, inner, result):
        self.inner = inner
        self.result = result

    async def handle_async_request(self, request):
        if request.method != "POST" or str(request.url) != OLLAMA_CHAT_URL:
            self.result["trace"]["blocked_network_requests"] += 1
            raise BlockedNetworkRequest("Only the fixed loopback Ollama chat endpoint is allowed")
        payload = json.loads(await request.aread())
        self.result["effective_request"] = payload
        self.result["trace"]["model_called"] = True
        self.result["trace"]["prompt_chars"] = sum(
            len(message["content"]) for message in payload.get("messages", [])
            if isinstance(message.get("content"), str)
        )
        response = await self.inner.handle_async_request(request)
        return httpx.Response(
            response.status_code, headers=response.headers,
            stream=ObservedStream(response.stream, self.result), extensions=response.extensions,
        )

    async def aclose(self):
        await self.inner.aclose()


def initial_result(clock):
    return {
        "status": "run_error", "answer": "", "error": "Replay is still running",
        # In checkpoints, truncated means incomplete, not just a token limit.
        "truncated": True,
        "timings": {"first_event_seconds": None, "first_thinking_seconds": None,
                    "first_content_seconds": None, "total_seconds": 0.0},
        "trace": {
            "clock_utc": clock.isoformat(), "planned_query": None,
            "search_queries": [], "fetch_calls": [], "accepted_fixture_ids": [],
            "rejected_fixture_ids": [], "unused_fixture_ids": [],
            "model_called": False, "service_done": False, "ollama_done": False,
            "ollama_error": False, "done_reason": None, "ollama_metrics": {},
            "thinking_characters": 0, "content_characters": 0,
            "prompt_chars": None,
            "blocked_network_requests": 0,
            "fixture_mode": "fixed search results and extracted page text; no live retrieval",
            "memory_mode": "none",
            "checkpoint_final": False,
        },
        "effective_request": None,
    }


async def replay(case: dict, model: str, timeout_seconds: float, on_update=None, *,
                 clock: str | None = None, transport_factory=None,
                 generation_profile: dict | None = None) -> dict:
    """Replay one service case; callers must isolate the profile before import.

    transport_factory is a no-network unit-test seam. Module patching makes this
    intentionally sequential: the CLI runs exactly one case per fresh process.
    """
    global _REPLAY_ACTIVE
    if _REPLAY_ACTIVE:
        raise RuntimeError("Concurrent replays in one process are unsupported")
    if not isinstance(timeout_seconds, (int, float)) or not math.isfinite(timeout_seconds) or timeout_seconds <= 0:
        raise ValueError("timeout_seconds must be positive and finite")
    if case.get("evaluation_layer", "service") != "service":
        raise ValueError("Desktop cases cannot be evaluated by the service adapter")
    moment = utc_clock(case.get("as_of") or clock or datetime.now(timezone.utc).isoformat())
    messages = case["messages"]
    if not messages or messages[-1].get("role") != "user":
        raise ValueError("A replay must end with the user's request")
    result = initial_result(moment)
    trace = result["trace"]
    evidence = case.get("evidence", [])
    by_url = {source["url"]: source for source in evidence}
    if len(by_url) != len(evidence):
        raise ValueError("Fixture URLs must be unique within a case")
    all_ids = [source["id"] for source in evidence]
    fetched_ids = set()
    accepted_ids = set()
    thinking_filter = ThinkingFilter()
    native_thinking_characters = 0
    started = time.monotonic()

    def checkpoint(final=False):
        trace["accepted_fixture_ids"] = [identity for identity in all_ids if identity in accepted_ids]
        trace["rejected_fixture_ids"] = [identity for identity in all_ids if identity in fetched_ids - accepted_ids]
        trace["unused_fixture_ids"] = [identity for identity in all_ids if identity not in fetched_ids]
        trace["thinking_characters"] = native_thinking_characters + thinking_filter.characters
        trace["content_characters"] = len(result["answer"])
        trace["checkpoint_final"] = final
        result["timings"]["total_seconds"] = round(time.monotonic() - started, 6)
        if on_update:
            on_update(copy.deepcopy(result))

    async def heartbeat():
        while True:
            await asyncio.sleep(CHECKPOINT_INTERVAL_SECONDS)
            checkpoint()

    _REPLAY_ACTIVE = True
    ticker = None
    try:
        chat, planning, page_type = load_service()
        if generation_profile is not None:
            from app.model_profiles import validate_profile
            generation_profile = validate_profile(generation_profile, generation_profile)
            if generation_profile["model"] != model:
                raise ValueError("The generation profile belongs to a different model")
            trace["generation_profile"] = generation_profile
            trace["profile_mode"] = "frozen capability snapshot; no live profile or user settings"
        request_messages = [{"role": item["role"], "content": item["content"]} for item in messages]
        context = ""
        if case.get("memory_context"):
            context = "Synthetic approved memory fixture (background data):\n" + case["memory_context"]
            trace["memory_mode"] = "direct service context injection; response-only, no memory retrieval or persistence"
        request = chat.ChatRequest(
            message=request_messages[-1]["content"], model=model,
            history=request_messages[:-1], context=context,
            web_search_enabled=case["web_search"], connector_context_enabled=False,
            profile=generation_profile["preset"] if generation_profile else None,
        )
        original_plan = chat.plan_query

        def planned_query(message, history):
            query = original_plan(message, history)
            trace["planned_query"] = query
            return query

        async def fixture_search(*, query, **kwargs):
            trace["search_queries"].append(query)
            return [chat.SearchResult(item["title"], item["url"], item["text"])
                    for item in evidence[:kwargs.get("max_results", 5)]]

        async def fixture_fetch(url):
            source = by_url.get(url)
            trace["fetch_calls"].append({"url": url, "fixture_id": source["id"] if source else None})
            if source is None:
                raise ValueError("No fixture exists for this URL; live fetching is disabled")
            fetched_ids.add(source["id"])
            return page_type(source["text"], source.get("published_at"))

        def restricted_client(*args, **kwargs):
            # A fresh transport owns its sockets per client. Even if production
            # starts requesting proxies/redirects, this evaluation cannot use them.
            if args:
                raise ValueError("Unexpected positional HTTP client arguments")
            for key in ("proxy", "proxies", "mounts", "transport", "trust_env", "follow_redirects"):
                kwargs.pop(key, None)
            inner = transport_factory() if transport_factory else httpx.AsyncHTTPTransport(trust_env=False)
            return REAL_ASYNC_CLIENT(
                **kwargs, trust_env=False, follow_redirects=False,
                transport=RestrictedTransport(inner, result),
            )

        async def consume():
            nonlocal native_thinking_characters
            async for frame in chat._stream_ollama(request):
                for line in frame.splitlines():
                    if not line.startswith("data: "):
                        continue
                    event = json.loads(line[6:])
                    now = round(time.monotonic() - started, 6)
                    if result["timings"]["first_event_seconds"] is None:
                        result["timings"]["first_event_seconds"] = now
                    kind = event.get("type")
                    if kind == "token":
                        before_thinking = native_thinking_characters + thinking_filter.characters
                        native_thinking_characters += len(event.get("thinking") or "")
                        content = thinking_filter.feed(event.get("content") or "")
                        if (native_thinking_characters + thinking_filter.characters > before_thinking
                                and result["timings"]["first_thinking_seconds"] is None):
                            result["timings"]["first_thinking_seconds"] = now
                        result["answer"] += content
                        if content.strip() and result["timings"]["first_content_seconds"] is None:
                            result["timings"]["first_content_seconds"] = now
                    elif kind == "context":
                        trace["context_budget"] = event.get("budget", {})
                    elif kind == "sources":
                        accepted_ids.update(by_url[source["url"]]["id"]
                                            for source in event.get("sources", []) if source.get("url") in by_url)
                    elif kind == "done":
                        trace["service_done"] = True
                    elif kind == "error":
                        result["error"] = str(event.get("content") or "Service error")[:512]
                        trace["service_error"] = True
                    checkpoint()

        checkpoint()
        with ExitStack() as stack:
            if generation_profile:
                async def frozen_profile(requested_model, requested_preset):
                    if requested_model != model or requested_preset != generation_profile["preset"]:
                        raise ValueError("Profile selection changed during replay")
                    return generation_profile
                stack.enter_context(patch.object(chat, "resolve_profile", frozen_profile))
            for owner, name, replacement in (
                (chat, "search", fixture_search), (chat, "fetch_evidence", fixture_fetch),
                (chat, "plan_query", planned_query), (chat, "datetime", frozen_datetime(moment)),
                (planning, "datetime", frozen_datetime(moment)),
                (chat.httpx, "AsyncClient", restricted_client),
            ):
                stack.enter_context(patch.object(owner, name, replacement))
            ticker = asyncio.create_task(heartbeat())
            await asyncio.wait_for(consume(), timeout_seconds)
        if trace.get("service_error") or trace["ollama_error"]:
            result["error"] = result["error"] if trace.get("service_error") else "Ollama reported an error"
        elif trace["done_reason"] == "length":
            result["error"] = "Generation reached its output length limit"
        elif not trace["service_done"] or (trace["model_called"] and not trace["ollama_done"]):
            result["error"] = "The response ended without a completion marker"
        elif thinking_filter.inside:
            result["error"] = "The response ended inside an unclosed thinking block"
            trace["unclosed_thinking"] = True
        elif not (result["answer"] + thinking_filter.pending).strip():
            result["error"] = "The service returned an empty answer"
        else:
            result.update(status="completed", error=None, truncated=False)
    except asyncio.TimeoutError:
        result["error"] = "Replay exceeded the case timeout"
        trace["timed_out"] = True
    except asyncio.CancelledError:
        result["error"] = "Replay was cancelled"
        trace["cancelled"] = True
    except Exception as error:
        # Do not persist exception messages that may include file paths or data.
        result["error"] = f"Replay failed: {type(error).__name__}"
        if isinstance(error, OSError):
            codes = [f"{name}={getattr(error, name)}" for name in ("errno", "winerror")
                     if type(getattr(error, name, None)) is int]
            if codes:
                result["error"] += " (" + ", ".join(codes) + ")"
    finally:
        if ticker:
            ticker.cancel()
            await asyncio.gather(ticker, return_exceptions=True)
        _REPLAY_ACTIVE = False
        tail = thinking_filter.finish()
        result["answer"] += tail
        if tail.strip() and result["timings"]["first_content_seconds"] is None:
            result["timings"]["first_content_seconds"] = round(time.monotonic() - started, 6)
        checkpoint(final=True)
    return result


def write_checkpoint(output: Path, result: dict):
    """Publish complete JSON, tolerating brief Windows reader/AV rename locks.

    A failed replacement leaves both the previous checkpoint and the complete
    staging file intact. It still raises; an unpublished final result must not
    be mistaken for successful evaluation. Retrying never rewrites the staging
    file or opens the destination for truncation.
    """
    encoded = json.dumps(result, ensure_ascii=False, allow_nan=False, indent=2) + "\n"
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", dir=output.parent,
                                         prefix=output.name + ".", suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(encoded)
    except BaseException:
        if temporary:
            temporary.unlink(missing_ok=True)
        raise
    for attempt in range(len(CHECKPOINT_REPLACE_DELAYS) + 1):
        try:
            os.replace(temporary, output)
            return
        except OSError as error:
            # MoveFileExW may report ACCESS_DENIED (5), not just SHARING/LOCK
            # VIOLATION (32/33), when an ordinary reader denies delete sharing.
            # Only replacement is retried. Other platforms/errors fail at once;
            # genuine ACL denial also propagates after this bounded backoff.
            if (sys.platform != "win32" or getattr(error, "winerror", None) not in (5, 32, 33)
                    or attempt == len(CHECKPOINT_REPLACE_DELAYS)):
                raise
            time.sleep(CHECKPOINT_REPLACE_DELAYS[attempt])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    document = json.loads(args.input.read_text(encoding="utf-8"))
    profile = Path(document["profile_dir"])
    if not profile.is_absolute():
        raise ValueError("The isolated profile path must be absolute")
    if args.output.exists():
        raise FileExistsError("Worker output already exists")
    # The parent supplies a new child path within its per-case TemporaryDirectory.
    # Refuse all preexisting profiles, including empty directories or symlinks.
    profile.mkdir(parents=False, exist_ok=False)
    os.environ["LOCALMIND_DATA_DIR"] = str(profile.resolve())
    result = asyncio.run(replay(
        document["case"], document["model"], document["timeout_seconds"],
        on_update=lambda value: write_checkpoint(args.output, value), clock=document["clock"],
        generation_profile=document.get("generation_profile"),
    ))
    return 0 if result["status"] == "completed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
