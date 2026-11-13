"""Cloud chat adapters with one stream contract and no automatic paid retries.

The caller supplies packed, already reviewed messages. The service independently
checks destination, model, data scopes and local budget before opening a socket.
Only fixed official HTTPS endpoints are supported. Provider errors are never
echoed verbatim because they may contain user content or credentials.
"""

import asyncio
import json
from typing import AsyncIterator

import httpx

from app.credentials import CredentialStore, CredentialUnavailable
from app import provider_store as store
from app.provider_store import ProviderError

BASES = {"openai": "https://api.openai.com/v1", "anthropic": "https://api.anthropic.com/v1",
         "gemini": "https://generativelanguage.googleapis.com/v1beta"}
TIMEOUT = httpx.Timeout(connect=10, read=90, write=30, pool=10)
MAX_EVENT = 1_000_000


def _key(provider):
    store.provider_id(provider)
    value = CredentialStore().read("chat-" + provider)
    if not value or not value.get("token"):
        raise ProviderError("credential", "Add an API key in Settings before using this provider.")
    return value["token"]


def set_key(provider, key):
    store.provider_id(provider)
    if not isinstance(key, str) or not key.strip() or len(key) > 1000 or any(c.isspace() for c in key):
        raise ProviderError("credential", "Enter an API key without spaces.")
    store.revoke(provider)
    CredentialStore().write("chat-" + provider, {"token": key})
    return {"connected": True}


def disconnect(provider):
    store.provider_id(provider)
    # Revoke before touching a possibly locked keychain, failing closed even if
    # credential removal needs the user to unlock it and try again.
    store.revoke(provider)
    CredentialStore().delete("chat-" + provider)
    return {"connected": False}


def status():
    result = []
    for provider in store.PROVIDERS:
        value = store.config(provider)
        try:
            value["connected"] = bool(CredentialStore().read("chat-" + provider))
        except CredentialUnavailable:
            value.update(connected=False, credential_error="Unlock your operating system credential store.")
        result.append(value)
    return {**store.policy(), "usage": store.usage(), "providers": result}


def _headers(provider):
    key = _key(provider)
    if provider == "anthropic":
        return {"x-api-key": key, "anthropic-version": "2023-06-01"}
    if provider == "gemini":
        return {"x-goog-api-key": key}
    return {"Authorization": "Bearer " + key}


def _http_error(code):
    if code in (401, 403):
        return ProviderError("auth", "The provider rejected this API key or its access permissions.")
    if code == 429:
        return ProviderError("quota", "The provider rate or credit limit was reached. Check your API account before retrying.")
    if code in (400, 404, 422):
        return ProviderError("unsupported", "The provider rejected this model or request format. Check the selected model and its capabilities.")
    return ProviderError("provider_unavailable", "The provider could not complete this request. Retry explicitly when it is available.")


def _capability(item):
    capabilities = item.get("capabilities") or {}
    image = capabilities.get("image_input")
    if isinstance(image, dict) and type(image.get("supported")) is bool:
        return image["supported"]
    modalities = item.get("input_modalities") or item.get("inputModalities")
    if isinstance(modalities, list):
        return any(str(value).lower() == "image" for value in modalities)
    # A successful model-list response does not establish vision support.
    return None


async def _metadata(provider, model=None):
    store.provider_id(provider)
    store.ensure_remote_allowed()
    path = "/models" + ("/" + store.model_id(model) if model else "")
    params = ({"pageSize": 1000} if provider == "gemini" else {"limit": 1000} if provider == "anthropic" else {}) if model is None else {}
    try:
        async with httpx.AsyncClient(timeout=TIMEOUT, follow_redirects=False) as client:
            response = await client.get(BASES[provider] + path, headers=_headers(provider), params=params)
            if response.status_code != 200:
                raise _http_error(response.status_code)
            if len(response.content) > 5_000_000:
                raise ProviderError("protocol", "The provider returned an oversized model list.")
            body = response.json()
            if not isinstance(body, dict):
                raise ProviderError("protocol", "The provider returned an invalid model response.")
            return body
    except (httpx.HTTPError, json.JSONDecodeError):
        raise ProviderError("network", "Could not connect to the provider. Check your connection and retry.") from None


async def list_models(provider):
    body = await _metadata(provider)
    items = body.get("models", []) if provider == "gemini" else body.get("data", [])
    if not isinstance(items, list):
        raise ProviderError("protocol", "The provider returned an invalid model list.")
    models = []
    for item in items[:1000]:
        if not isinstance(item, dict):
            continue
        if provider == "gemini" and "generateContent" not in item.get("supportedGenerationMethods", []):
            continue
        identifier = str(item.get("name", "")).removeprefix("models/") if provider == "gemini" else str(item.get("id", ""))
        try:
            store.model_id(identifier)
        except ProviderError:
            continue
        models.append({"id": identifier, "name": item.get("display_name") or item.get("displayName") or identifier,
                       "vision": _capability(item)})
    return {"models": models, "truncated": bool(body.get("has_more") or body.get("nextPageToken") or len(items) > 1000)}


async def check_model(provider, model):
    body = await _metadata(provider, model)
    returned = str(body.get("name", "")).removeprefix("models/") if provider == "gemini" else body.get("id")
    if returned != model:
        raise ProviderError("model", "The provider did not confirm access to the selected model.")
    return {"ok": True, "model": model, "vision": _capability(body), "generation_tested": False}


def _validate_messages(messages):
    if not isinstance(messages, list) or not messages or len(messages) > 500:
        raise ProviderError("messages", "The conversation is empty or too large.")
    for msg in messages:
        if not isinstance(msg, dict) or msg.get("role") not in ("system", "user", "assistant") or not isinstance(msg.get("content"), str):
            raise ProviderError("messages", "The conversation contains an invalid message.")
        images = msg.get("images", [])
        if not isinstance(images, list) or len(images) > 8 or (images and msg["role"] != "user"):
            raise ProviderError("images", "Images must be attached to a user message, with at most eight per message.")
        for image in images:
            if (not isinstance(image, dict) or image.get("mime_type") not in ("image/png", "image/jpeg", "image/webp")
                    or not isinstance(image.get("data"), str) or len(image["data"]) > 14_000_000):
                raise ProviderError("images", "An image could not be sent. Reattach a supported image.")
    encoded = json.dumps(messages, ensure_ascii=False).encode("utf-8")
    if len(encoded) > 32_000_000:
        raise ProviderError("messages", "The request is too large. Remove some attachments or start a shorter conversation.")
    # Conservative byte bound, including image base64, used only for a local
    # estimate. Actual provider usage settles the reservation when available.
    return len(encoded) + 1024


def preview_estimate(provider, model, messages, max_tokens=2048):
    """Estimate this request locally, without reading a key or reserving money.

    The byte bound deliberately overestimates image inputs. User-configured
    prices can be stale or omit special provider charges, so this is not a
    provider billing guarantee or an account-wide spending ceiling.
    """
    store.provider_id(provider)
    store.model_id(model)
    if type(max_tokens) is not int or not 1 <= max_tokens <= 32768:
        raise ProviderError("tokens", "Choose an output limit between 1 and 32768 tokens.")
    cfg = store.config(provider)
    if model != cfg["model"]:
        raise ProviderError("model", "Save this model's settings before reviewing its cost.")
    input_bound = _validate_messages(messages)
    prices = cfg["input_price_per_million"], cfg["output_price_per_million"]
    known = all(price is not None for price in prices)
    return {"upper_bound_usd": (input_bound * prices[0] + max_tokens * prices[1]) / 1_000_000 if known else None,
            "input_bound": input_bound, "output_limit": max_tokens, "pricing_known": known}


def request_body(provider, model, messages, max_tokens):
    """Translate canonical text and resolved image bytes without changing roles."""
    if provider == "openai":
        rows = []
        for msg in messages:
            if not msg.get("images"):
                rows.append({"role": msg["role"], "content": msg["content"]})
                continue
            parts = [{"type": "input_text", "text": msg["content"]}]
            parts.extend({"type": "input_image", "image_url": f"data:{img['mime_type']};base64,{img['data']}"} for img in msg.get("images", []))
            rows.append({"role": msg["role"], "content": parts})
        return "/responses", {"model": model, "input": rows, "max_output_tokens": max_tokens, "stream": True, "store": False}
    system = "\n\n".join(msg["content"] for msg in messages if msg["role"] == "system")
    if provider == "anthropic":
        rows = []
        for msg in messages:
            if msg["role"] == "system":
                continue
            parts = [{"type": "text", "text": msg["content"]}] if msg["content"] else []
            parts.extend({"type": "image", "source": {"type": "base64", "media_type": img["mime_type"], "data": img["data"]}} for img in msg.get("images", []))
            rows.append({"role": msg["role"], "content": parts})
        return "/messages", {"model": model, "system": system, "messages": rows, "max_tokens": max_tokens, "stream": True}
    rows = []
    for msg in messages:
        if msg["role"] == "system":
            continue
        parts = [{"text": msg["content"]}] if msg["content"] else []
        parts.extend({"inlineData": {"mimeType": img["mime_type"], "data": img["data"]}} for img in msg.get("images", []))
        rows.append({"role": "model" if msg["role"] == "assistant" else "user", "parts": parts})
    return f"/models/{model}:streamGenerateContent?alt=sse", {
        "systemInstruction": {"parts": [{"text": system}]}, "contents": rows,
        "generationConfig": {"maxOutputTokens": max_tokens},
    }


async def _events(response):
    """SSE framing handles comments, CRLF and multi-line data fields."""
    data = []
    size = 0
    async for line in response.aiter_lines():
        if len(line) > MAX_EVENT:
            raise ProviderError("protocol", "The provider returned an oversized stream event.")
        if not line:
            if data:
                yield "\n".join(data)
                data, size = [], 0
        elif line.startswith("data:"):
            part = line[5:].lstrip(" ")
            size += len(part)
            if size > MAX_EVENT:
                raise ProviderError("protocol", "The provider returned an oversized stream event.")
            data.append(part)
    if data:
        yield "\n".join(data)


def _tokens(value):
    return value if type(value) is int and value >= 0 else None


async def stream_chat(provider: str, model: str, messages: list[dict], *, scopes: list[str],
                      request_id: str, consent_id: str | None, max_tokens: int = 2048) -> AsyncIterator[dict]:
    reserved = False
    call_started = False
    received_usage = False
    input_tokens = output_tokens = None
    answer_chars = 0
    try:
        store.provider_id(provider)
        store.model_id(model)
        if type(max_tokens) is not int or not 1 <= max_tokens <= 32768:
            raise ProviderError("tokens", "Choose an output limit between 1 and 32768 tokens.")
        input_bound = _validate_messages(messages)
        actual_scopes = set(scopes) | {"messages"}
        if any(msg.get("images") for msg in messages):
            actual_scopes.add("attachments")
        headers = _headers(provider)
        estimate = store.reserve(provider, model, actual_scopes, consent_id, request_id, input_bound, max_tokens)
        reserved = True
        yield {"type": "estimate", **estimate}
        # The consumer can pause after the estimate. Recheck a revocation made
        # during that pause before the request leaves this device.
        store.ensure_remote_allowed()
        current = store.config(provider)
        if current["consent_id"] != consent_id or current["consent_model"] != model:
            store.settle(request_id, rejected=True)
            raise ProviderError("consent_required", "Cloud approval changed before sending. Review this request again.")
        path, body = request_body(provider, model, messages, max_tokens)
        completed = False
        async with httpx.AsyncClient(timeout=TIMEOUT, follow_redirects=False) as client:
            call_started = True
            async with client.stream("POST", BASES[provider] + path, headers=headers, json=body) as response:
                if response.status_code != 200:
                    # Definite authentication/validation/rate rejection did not
                    # start a generation. 5xx and connection failures are ambiguous.
                    if response.status_code in (400, 401, 403, 404, 413, 422, 429):
                        store.settle(request_id, rejected=True)
                    raise _http_error(response.status_code)
                async for raw in _events(response):
                    if raw == "[DONE]":
                        continue
                    event = json.loads(raw)
                    if not isinstance(event, dict):
                        raise ProviderError("protocol", "The provider sent an invalid stream event.")
                    kind = event.get("type")
                    if event.get("error") or kind in ("error", "response.failed"):
                        raise ProviderError("generation", "The provider stopped this response. Your partial answer is preserved; retry explicitly.")
                    if provider == "openai":
                        if kind in ("response.output_text.delta", "response.refusal.delta"):
                            answer_chars += len(event.get("delta", "").strip())
                            yield {"type": "token", "content": event.get("delta", "")}
                        if kind in ("response.completed", "response.incomplete"):
                            usage = event.get("response", {}).get("usage") or {}
                            input_tokens, output_tokens = _tokens(usage.get("input_tokens")), _tokens(usage.get("output_tokens"))
                            received_usage = input_tokens is not None and output_tokens is not None
                            if kind == "response.incomplete":
                                raise ProviderError("incomplete", "The provider stopped before finishing. Your partial answer is preserved.")
                            completed = True
                    elif provider == "anthropic":
                        if kind == "message_start":
                            usage = event.get("message", {}).get("usage") or {}
                            input_tokens = _tokens(usage.get("input_tokens"))
                        elif kind == "content_block_delta":
                            delta = event.get("delta", {})
                            if delta.get("type") == "text_delta":
                                answer_chars += len(delta.get("text", "").strip())
                                yield {"type": "token", "content": delta.get("text", "")}
                        elif kind == "message_delta":
                            output_tokens = _tokens((event.get("usage") or {}).get("output_tokens"))
                            received_usage = input_tokens is not None and output_tokens is not None
                            stop = event.get("delta", {}).get("stop_reason")
                            if stop == "refusal":
                                raise ProviderError("blocked", "The provider declined this request.")
                            if stop not in (None, "end_turn", "stop_sequence"):
                                raise ProviderError("incomplete", "The provider stopped before finishing. Your partial answer is preserved.")
                        elif kind == "message_stop":
                            completed = True
                    else:
                        if event.get("promptFeedback", {}).get("blockReason"):
                            raise ProviderError("blocked", "The provider declined this request.")
                        usage = event.get("usageMetadata") or {}
                        if usage:
                            input_tokens = _tokens(usage.get("promptTokenCount"))
                            output_tokens = _tokens(usage.get("candidatesTokenCount"))
                            thoughts = _tokens(usage.get("thoughtsTokenCount", 0))
                            output_tokens = output_tokens + thoughts if output_tokens is not None and thoughts is not None else None
                            received_usage = input_tokens is not None and output_tokens is not None
                        candidates = event.get("candidates", [])
                        for candidate in candidates[:1]:
                            for part in candidate.get("content", {}).get("parts", []):
                                if "text" in part and not part.get("thought"):
                                    answer_chars += len(part["text"].strip())
                                    yield {"type": "token", "content": part["text"]}
                            stop = candidate.get("finishReason")
                            if stop and stop != "STOP":
                                raise ProviderError("incomplete", "The provider stopped before finishing. Your partial answer is preserved.")
                            completed = completed or stop == "STOP"
        if not completed:
            raise ProviderError("interrupted", "The connection ended before the response finished. Retry explicitly; the provider may already have charged for it.")
        if not answer_chars:
            raise ProviderError("empty", "The provider finished without an answer. Retry explicitly or choose a different model.")
        cost = store.settle(request_id, input_tokens, output_tokens)
        yield {"type": "usage", "input_tokens": input_tokens, "output_tokens": output_tokens,
               "cost_usd": cost, "cost_is_estimate": True}
        yield {"type": "done"}
    except asyncio.CancelledError:
        raise
    except CredentialUnavailable:
        yield {"type": "error", "code": "credential_store", "content": "Unlock your operating system credential store and retry. No plaintext key fallback is used."}
    except ProviderError as exc:
        if reserved and received_usage:
            cost = store.settle(request_id, input_tokens, output_tokens)
            yield {"type": "usage", "input_tokens": input_tokens, "output_tokens": output_tokens,
                   "cost_usd": cost, "cost_is_estimate": True}
        yield {"type": "error", "code": exc.code, "content": str(exc)}
    except (httpx.HTTPError, json.JSONDecodeError, TypeError, KeyError, AttributeError):
        yield {"type": "error", "code": "connection", "content": "The provider connection failed. Your partial answer is preserved. Retry explicitly; the previous request may already have been charged."}
    finally:
        if reserved:
            # On cancellation or malformed streams retain the reservation. Usage
            # from a final incomplete event can still settle an actual charge.
            store.settle(request_id, input_tokens if received_usage else None,
                         output_tokens if received_usage else None, rejected=not call_started)
