"""Validated, per-installation model settings bound to an installed digest."""

from contextlib import closing
from datetime import datetime, timezone
import json
import math
import re
import sqlite3

import httpx

from app.storage import data_path
from app.chat_policy import answer_policy
from app.context_budget import estimate_tokens

PRESETS = ("speed", "balanced", "boost")
EDITABLE = {"context_window", "response_tokens", "temperature", "top_p", "top_k", "think"}
MODEL_METADATA = ("model", "digest", "capabilities", "context_limit", "thinking_values")
OLLAMA_BASE = "http://127.0.0.1:11434"


def minimum_input_budget() -> int:
    """Reserve the longest policy plus a short user turn, rounded to 256."""
    costs = [
        estimate_tokens([{"role": "system", "content": answer_policy(
            "2000-01-01", searched=searched, web_allowed=allowed)},
            {"role": "user", "content": ""}])
        for searched, allowed in ((True, True), (False, True), (False, False))
    ]
    return math.ceil((max(costs) + 512) / 256) * 256


def _name(model: str) -> str:
    if not isinstance(model, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/-]{0,180}", model):
        raise ValueError("Choose a valid installed model name.")
    if ".." in model or model.startswith("/") or "://" in model:
        raise ValueError("Choose a valid installed model name.")
    return model


def _database():
    connection = sqlite3.connect(data_path("model_profiles.db"))
    connection.execute("CREATE TABLE IF NOT EXISTS profiles (model TEXT, preset TEXT, body TEXT NOT NULL, "
                       "PRIMARY KEY (model, preset))")
    connection.execute("CREATE TABLE IF NOT EXISTS profile_history (id INTEGER PRIMARY KEY AUTOINCREMENT, "
                       "model TEXT, preset TEXT, body TEXT NOT NULL)")
    connection.commit()
    return connection


def read_profile(model: str, preset: str) -> dict | None:
    with closing(_database()) as connection:
        row = connection.execute("SELECT body FROM profiles WHERE model=? AND preset=?",
                                 (model, preset)).fetchone()
        return json.loads(row[0]) if row else None


def _persist(profile: dict) -> dict:
    with closing(_database()) as connection, connection:
        current = connection.execute("SELECT body FROM profiles WHERE model=? AND preset=?",
                                     (profile["model"], profile["preset"])).fetchone()
        body = json.dumps(profile, sort_keys=True, allow_nan=False)
        if current and json.loads(current[0]) == profile:
            return profile
        if current:
            connection.execute("INSERT INTO profile_history(model,preset,body) VALUES(?,?,?)",
                               (profile["model"], profile["preset"], current[0]))
        connection.execute("INSERT OR REPLACE INTO profiles(model,preset,body) VALUES(?,?,?)",
                           (profile["model"], profile["preset"], body))
        connection.execute("DELETE FROM profile_history WHERE model=? AND preset=? AND id NOT IN "
                           "(SELECT id FROM profile_history WHERE model=? AND preset=? ORDER BY id DESC LIMIT 10)",
                           (profile["model"], profile["preset"], profile["model"], profile["preset"]))
    return profile


async def inspect_model(model: str) -> dict:
    model = _name(model)
    async with httpx.AsyncClient(trust_env=False, timeout=10.0) as client:
        tags = await client.get(f"{OLLAMA_BASE}/api/tags")
        tags.raise_for_status()
        aliases = {model, model + ":latest"} if ":" not in model.rsplit("/", 1)[-1] else {model}
        installed = [row for row in tags.json().get("models", [])
                     if row.get("name") in aliases or row.get("model") in aliases]
        if len(installed) != 1 or not re.fullmatch(r"(?:sha256:)?[0-9a-f]{64}", installed[0].get("digest", "")):
            raise ValueError("This model is not installed with an identifiable digest.")
        response = await client.post(f"{OLLAMA_BASE}/api/show", json={"model": model})
        response.raise_for_status()
        shown = response.json()
    for value in (installed[0], shown):
        if any(value.get(key) for key in ("remote_model", "remote_host")):
            raise ValueError("Choose a local model for this profile.")
    capabilities = shown.get("capabilities", [])
    if not isinstance(capabilities, list) or "completion" not in capabilities:
        raise ValueError("This installed model does not advertise text completion.")
    context_lengths = [value for key, value in shown.get("model_info", {}).items()
                       if key.endswith(".context_length") and type(value) is int and value > 0]
    if not context_lengths:
        raise ValueError("Ollama did not report the model's context capacity.")
    thinking = shown.get("thinking", {})
    values = thinking.get("values", []) if isinstance(thinking, dict) else []
    if not isinstance(values, list) or any(type(v) not in (str, bool) for v in values):
        values = []
    # Older Ollama versions list thinking capability without controls. Do not
    # guess boolean support for models that only accept named reasoning levels.
    return {"model": model, "digest": installed[0]["digest"], "capabilities": capabilities,
            "context_limit": min(max(context_lengths), 65536), "thinking_values": values,
            "minimum_input_budget": minimum_input_budget()}


def default_profile(metadata: dict, preset: str) -> dict:
    if preset not in PRESETS:
        raise ValueError("Choose Speed, Balanced or Boost.")
    context = min(16384, metadata["context_limit"])
    input_budget = minimum_input_budget()
    if context < input_budget + 128:
        raise ValueError("This model's context is too small for LocalMind's instructions and a response.")
    return {**metadata, "schema_version": 1, "preset": preset, "context_window": context,
            "minimum_input_budget": input_budget,
            "response_tokens": min(2048, context // 4, context - input_budget), "temperature": 0.2,
            "top_p": 0.9, "top_k": 40, "think": None,
            "saved_at": datetime.now(timezone.utc).isoformat()}


def validate_profile(profile: dict, metadata: dict) -> dict:
    if profile.get("preset") not in PRESETS or profile.get("schema_version") != 1:
        raise ValueError("Unsupported model profile.")
    if profile.get("model") != metadata["model"] or profile.get("digest") != metadata["digest"]:
        raise ValueError("The installed model changed. Review its new profile before restoring old settings.")
    context, reserve = profile.get("context_window"), profile.get("response_tokens")
    input_budget = minimum_input_budget()
    if type(context) is not int or not input_budget + 128 <= context <= metadata["context_limit"]:
        raise ValueError("Context must fit within the installed model's supported capacity.")
    if type(reserve) is not int or not 128 <= reserve <= min(8192, context - input_budget):
        raise ValueError(f"Reserve at least 128 response tokens and leave {input_budget} tokens for instructions and your message.")
    for field, low, high in (("temperature", 0, 2), ("top_p", 0.01, 1)):
        value = profile.get(field)
        if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
            raise ValueError(f"Unsupported {field} value.")
    if type(profile.get("top_k")) is not int or not 1 <= profile["top_k"] <= 100:
        raise ValueError("Top-k must be between 1 and 100.")
    think = profile.get("think")
    if think is not None and not any(type(think) is type(value) and think == value
                                     for value in metadata["thinking_values"]):
        raise ValueError("This thinking setting is not supported by the installed model.")
    # Callers may hold a full validated profile as the metadata snapshot.
    # Only installation facts are authoritative here; copying its generation
    # settings would silently undo the user's otherwise valid changes.
    return {**profile, **{key: metadata[key] for key in MODEL_METADATA},
            "minimum_input_budget": input_budget}


async def resolve_profile(model: str, preset: str) -> dict:
    if preset not in PRESETS:
        raise ValueError("Unknown model preset.")
    metadata = await inspect_model(model)
    saved = read_profile(model, preset)
    if saved and saved.get("digest") == metadata["digest"]:
        try:
            return validate_profile(saved, metadata)
        except ValueError:
            recovered = validate_profile(default_profile(metadata, preset), metadata)
            recovered["settings_notice"] = (
                "Saved response settings no longer fit the model's capabilities or "
                "LocalMind's context requirements. Defaults have been restored; "
                "your previous settings remain in history."
            )
            return _persist(recovered)
    return _persist(validate_profile(default_profile(metadata, preset), metadata))


async def update_profile(model: str, preset: str, changes: dict) -> dict:
    if not isinstance(changes, dict) or not changes or set(changes) - EDITABLE:
        raise ValueError("Only supported generation settings can be changed.")
    current = await resolve_profile(model, preset)
    updated = validate_profile({**current, **changes}, current)
    updated["saved_at"] = datetime.now(timezone.utc).isoformat()
    updated.pop("settings_notice", None)
    return _persist(updated)


async def restore_profile(model: str, preset: str) -> dict:
    if preset not in PRESETS:
        raise ValueError("Unknown model preset.")
    metadata = await inspect_model(model)
    with closing(_database()) as connection:
        row = connection.execute("SELECT body FROM profile_history WHERE model=? AND preset=? "
                                 "ORDER BY id DESC LIMIT 1", (model, preset)).fetchone()
    if not row:
        raise ValueError("No previous profile is available.")
    restored = validate_profile(json.loads(row[0]), metadata)
    restored["saved_at"] = datetime.now(timezone.utc).isoformat()
    restored.pop("settings_notice", None)
    return _persist(restored)


def generation_options(profile: dict) -> dict:
    return {"num_ctx": profile["context_window"], "num_predict": profile["response_tokens"],
            "temperature": profile["temperature"], "top_p": profile["top_p"], "top_k": profile["top_k"]}
