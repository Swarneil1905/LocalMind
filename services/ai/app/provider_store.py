"""Profile-local provider preferences, consent and a transactional usage ledger.

Keys never enter this database. A reservation survives a crash or an ambiguous
network failure so another request cannot spend the same local budget twice.
This ledger cannot enforce the provider account's spending on other devices.
"""

from contextlib import contextmanager
from datetime import datetime, timezone
import json
import math
import re
import sqlite3
from uuid import uuid4

from app.storage import data_path

PROVIDERS = {"openai": "OpenAI", "anthropic": "Anthropic", "gemini": "Google Gemini", "deepseek": "DeepSeek",
             "openrouter": "OpenRouter", "groq": "Groq", "mistral": "Mistral", "xai": "xAI"}
SCOPES = frozenset({"messages", "memory", "knowledge", "connectors", "attachments", "web"})


class ProviderError(ValueError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


def provider_id(value):
    if value not in PROVIDERS:
        raise ProviderError("provider", "Choose a supported provider.")
    return value


def model_id(value):
    if not isinstance(value, str) or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,199}", value):
        raise ProviderError("model", "Enter a valid model identifier.")
    if ".." in value or "/" in value:
        raise ProviderError("model", "Use the model identifier without a URL or models/ prefix.")
    return value


@contextmanager
def database():
    conn = sqlite3.connect(data_path("providers.db"), timeout=10)
    conn.row_factory = sqlite3.Row
    try:
        conn.executescript("""
          CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, value TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS requests (
            id TEXT PRIMARY KEY, provider TEXT NOT NULL, model TEXT NOT NULL,
            month TEXT NOT NULL, reserved REAL NOT NULL, cost REAL,
            state TEXT NOT NULL, created_at TEXT NOT NULL,
            input_price REAL, output_price REAL
          );
        """)
        conn.execute("BEGIN IMMEDIATE")
        yield conn
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    finally:
        conn.close()


def _get(conn, key, default):
    row = conn.execute("SELECT value FROM settings WHERE id=?", (key,)).fetchone()
    return json.loads(row[0]) if row else default


def _put(conn, key, value):
    conn.execute("INSERT OR REPLACE INTO settings VALUES (?,?)", (key, json.dumps(value)))


def _default(provider):
    return {"id": provider, "label": PROVIDERS[provider], "model": "", "vision": False,
            "input_price_per_million": None, "output_price_per_million": None,
            "approved_scopes": [], "consent_id": None, "consent_model": None}


def config(provider):
    provider_id(provider)
    with database() as conn:
        return _get(conn, provider, _default(provider))


def _money(value):
    if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float))
                              or not math.isfinite(value) or value < 0 or value > 1_000_000):
        raise ProviderError("price", "Use a finite, non-negative dollar amount.")
    return value


def _revoke(value):
    value.update(approved_scopes=[], consent_id=None, consent_model=None)
    return value


def configure(provider, changes):
    provider_id(provider)
    allowed = {"model", "vision", "input_price_per_million", "output_price_per_million"}
    if set(changes) - allowed:
        raise ProviderError("config", "Unknown provider setting.")
    if "model" in changes:
        model_id(changes["model"])
    if "vision" in changes and not isinstance(changes["vision"], bool):
        raise ProviderError("config", "Image capability must be true or false.")
    for field in ("input_price_per_million", "output_price_per_million"):
        if field in changes:
            _money(changes[field])
    with database() as conn:
        value = _get(conn, provider, _default(provider))
        if changes.get("model", value["model"]) != value["model"]:
            _revoke(value)
            # Capability and prices belong to a model, not to the provider.
            value.update(vision=False, input_price_per_million=None, output_price_per_million=None)
        value.update(changes)
        _put(conn, provider, value)
        return value


def revoke(provider):
    provider_id(provider)
    with database() as conn:
        value = _revoke(_get(conn, provider, _default(provider)))
        _put(conn, provider, value)
        return value


def approve(provider, scopes):
    provider_id(provider)
    if not isinstance(scopes, list) or any(not isinstance(x, str) for x in scopes) or set(scopes) - SCOPES:
        raise ProviderError("consent", "Unknown data category in cloud approval.")
    with database() as conn:
        value = _get(conn, provider, _default(provider))
        if not value["model"]:
            raise ProviderError("model", "Choose a model before approving cloud access.")
        value.update(approved_scopes=sorted(set(scopes)), consent_id=str(uuid4()), consent_model=value["model"])
        _put(conn, provider, value)
        return value


def policy():
    with database() as conn:
        return _get(conn, "policy", {"local_only": True, "monthly_limit_usd": None})


def configure_policy(changes):
    if set(changes) - {"local_only", "monthly_limit_usd"}:
        raise ProviderError("config", "Unknown cloud policy setting.")
    if "local_only" in changes and not isinstance(changes["local_only"], bool):
        raise ProviderError("config", "Local-only must be true or false.")
    if "monthly_limit_usd" in changes:
        _money(changes["monthly_limit_usd"])
    with database() as conn:
        value = _get(conn, "policy", {"local_only": True, "monthly_limit_usd": None})
        value.update(changes)
        _put(conn, "policy", value)
        # Returning to local-only also revokes old grants, rather than silently
        # reviving them if the user later reconnects a cloud provider.
        if value["local_only"]:
            for provider in PROVIDERS:
                _put(conn, provider, _revoke(_get(conn, provider, _default(provider))))
        return value


def ensure_remote_allowed():
    if policy()["local_only"]:
        raise ProviderError("local_only", "Local-only mode is on. Enable cloud access in Settings before connecting.")


def _month():
    return datetime.now(timezone.utc).strftime("%Y-%m")


def _usage(conn):
    rows = conn.execute("SELECT * FROM requests WHERE month=?", (_month(),)).fetchall()
    return {"month": _month(),
            "spent_usd": sum(row["cost"] or 0 for row in rows if row["state"] == "completed"),
            "reserved_usd": sum(row["reserved"] for row in rows if row["state"] in ("pending", "uncertain")),
            "unknown_requests": sum(row["state"] in ("pending", "uncertain") for row in rows),
            "account_wide_spend": None}


def usage():
    with database() as conn:
        return _usage(conn)


def reserve(provider, model, scopes, consent_id, request_id, input_bound, max_tokens):
    provider_id(provider)
    model_id(model)
    if not isinstance(request_id, str) or not re.fullmatch(r"[a-zA-Z0-9_-]{1,128}", request_id):
        raise ProviderError("request", "A unique request identifier is required.")
    if not set(scopes) <= SCOPES:
        raise ProviderError("consent", "Unknown data category in this request.")
    with database() as conn:
        pol = _get(conn, "policy", {"local_only": True, "monthly_limit_usd": None})
        if pol["local_only"]:
            raise ProviderError("local_only", "Local-only mode blocks cloud requests.")
        cfg = _get(conn, provider, _default(provider))
        if (not consent_id or consent_id != cfg["consent_id"] or model != cfg["consent_model"]
                or not set(scopes) <= set(cfg["approved_scopes"])):
            raise ProviderError("consent_required", "Review the data sent to this provider before continuing.")
        if "attachments" in scopes and not cfg["vision"]:
            raise ProviderError("vision", "This model is not configured for image input. Choose a compatible model.")
        prices = (cfg["input_price_per_million"], cfg["output_price_per_million"])
        known = all(price is not None for price in prices)
        if pol["monthly_limit_usd"] is not None and not known:
            raise ProviderError("unknown_price", "Set this model's input and output prices before using a local budget.")
        amount = (input_bound * prices[0] + max_tokens * prices[1]) / 1_000_000 if known else 0
        current = _usage(conn)
        if pol["monthly_limit_usd"] is not None:
            # Unknown requests made before enabling a limit cannot be treated as free.
            unknown = conn.execute("SELECT 1 FROM requests WHERE month=? AND state IN ('pending','uncertain') AND input_price IS NULL", (_month(),)).fetchone()
            if unknown or current["spent_usd"] + current["reserved_usd"] + amount > pol["monthly_limit_usd"]:
                raise ProviderError("budget", "The local monthly limit cannot cover this request. Review usage in Settings.")
        try:
            conn.execute("INSERT INTO requests VALUES (?,?,?,?,?,?,?,?,?,?)",
                         (request_id, provider, model, _month(), amount, None, "pending",
                          datetime.now(timezone.utc).isoformat(), prices[0], prices[1]))
        except sqlite3.IntegrityError:
            raise ProviderError("duplicate", "This request was already submitted. Retry explicitly with a new request.") from None
        return {"request_id": request_id, "estimated_max_cost_usd": amount if known else None}


def settle(request_id, input_tokens=None, output_tokens=None, *, rejected=False):
    with database() as conn:
        row = conn.execute("SELECT * FROM requests WHERE id=?", (request_id,)).fetchone()
        if not row or row["state"] not in ("pending", "uncertain"):
            return None
        measured = all(type(x) is int and x >= 0 for x in (input_tokens, output_tokens))
        known = row["input_price"] is not None and row["output_price"] is not None
        if rejected:
            cost, state = 0, "rejected"
        elif measured and known:
            cost = (input_tokens * row["input_price"] + output_tokens * row["output_price"]) / 1_000_000
            state = "completed"
        else:
            cost, state = None, "uncertain"
        conn.execute("UPDATE requests SET cost=?,state=? WHERE id=?", (cost, state, request_id))
        return cost
