"""
Gmail connector — OAuth2 "Sign in with Google" (XOAUTH2 over IMAP).

How it works:
  1. User clicks Connect → we check for Google client credentials.
  2. If no client creds: show one-time setup form (client_id + client_secret).
     User creates these once at console.cloud.google.com (Desktop app type).
  3. With client creds: start a local HTTP server on 127.0.0.1:<random port>,
     build the Google OAuth URL, return it as {"type": "oauth_url", "url": ...}.
  4. Frontend opens the URL in the system browser; user signs in.
  5. Google redirects to http://127.0.0.1:<port>/callback?code=...
  6. Local server exchanges code → access + refresh tokens; saves them.
  7. Frontend polls list_connectors; eventually sees "connected".
  8. IMAP auth uses XOAUTH2 (base64 Bearer token string) — no App Password needed.

All stdlib — zero extra pip packages.
"""

import base64
import email
import os
import email.utils
import imaplib
import json
import threading
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from email.header import decode_header
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from typing import Any

from .base import BaseConnector, ConnectorMeta, ConnectorStatus, SyncResult
from ..db.messages import (
    init_db, upsert_contact, upsert_thread, upsert_message,
    get_cursor, set_cursor,
)

# ── Paths ──────────────────────────────────────────────────────────────────────
TOKENS_DIR   = Path.home() / ".localmind" / "tokens"
CLIENT_PATH  = TOKENS_DIR / "gmail_client.json"   # client_id + client_secret
TOKENS_PATH  = TOKENS_DIR / "gmail_oauth.json"    # access + refresh tokens

# ── Google OAuth endpoints ─────────────────────────────────────────────────────
AUTH_URL   = "https://accounts.google.com/o/oauth2/v2/auth"
TOKEN_URL  = "https://oauth2.googleapis.com/token"
SCOPES     = "https://mail.google.com/ email openid"

IMAP_HOST = "imap.gmail.com"
IMAP_PORT = 993


# ── Credential helpers ─────────────────────────────────────────────────────────

def _load_client() -> dict | None:
    """
    Priority:
      1. GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET env vars (set via services/ai/.env)
      2. ~/.localmind/tokens/gmail_client.json (saved from the UI one-time setup form)
    """
    env_id     = os.environ.get("GOOGLE_CLIENT_ID", "").strip()
    env_secret = os.environ.get("GOOGLE_CLIENT_SECRET", "").strip()
    if env_id and env_secret:
        return {"client_id": env_id, "client_secret": env_secret}
    try:
        return json.loads(CLIENT_PATH.read_text()) if CLIENT_PATH.exists() else None
    except Exception:
        return None


def _save_client(client_id: str, client_secret: str) -> None:
    TOKENS_DIR.mkdir(parents=True, exist_ok=True)
    CLIENT_PATH.write_text(json.dumps({"client_id": client_id, "client_secret": client_secret}))


def _load_tokens() -> dict | None:
    try:
        return json.loads(TOKENS_PATH.read_text()) if TOKENS_PATH.exists() else None
    except Exception:
        return None


def _save_tokens(data: dict) -> None:
    TOKENS_DIR.mkdir(parents=True, exist_ok=True)
    TOKENS_PATH.write_text(json.dumps(data))


def _delete_tokens() -> None:
    if TOKENS_PATH.exists():
        TOKENS_PATH.unlink()
    if CLIENT_PATH.exists():
        CLIENT_PATH.unlink()


# ── Token refresh ──────────────────────────────────────────────────────────────

def _refresh_access_token(client: dict, tokens: dict) -> str | None:
    """Exchange refresh_token for a fresh access_token. Returns new token or None."""
    payload = urllib.parse.urlencode({
        "client_id":     client["client_id"],
        "client_secret": client["client_secret"],
        "refresh_token": tokens["refresh_token"],
        "grant_type":    "refresh_token",
    }).encode()
    try:
        req  = urllib.request.Request(TOKEN_URL, data=payload, method="POST")
        resp = urllib.request.urlopen(req, timeout=10)
        data = json.loads(resp.read())
        tokens["access_token"] = data["access_token"]
        tokens["expires_at"]   = time.time() + data.get("expires_in", 3600) - 60
        _save_tokens(tokens)
        return tokens["access_token"]
    except Exception:
        return None


def _get_valid_access_token() -> tuple[str | None, str | None]:
    """Returns (access_token, email) or (None, None)."""
    client = _load_client()
    tokens = _load_tokens()
    if not client or not tokens:
        return None, None

    if time.time() < tokens.get("expires_at", 0):
        access_token = tokens["access_token"]
        stored_email = tokens.get("email") or ""
        # Recovery: email was never saved (happens when oauth ran without email scope)
        if not stored_email:
            fetched = _fetch_user_email(access_token)
            if fetched:
                tokens["email"] = fetched
                _save_tokens(tokens)
                stored_email = fetched
        return access_token, stored_email or None

    new_token = _refresh_access_token(client, tokens)
    if not new_token:
        return None, None
    stored_email = tokens.get("email") or ""
    if not stored_email:
        fetched = _fetch_user_email(new_token)
        if fetched:
            tokens["email"] = fetched
            _save_tokens(tokens)
            stored_email = fetched
    return new_token, stored_email or None


# ── XOAUTH2 helper ─────────────────────────────────────────────────────────────

def _xoauth2_string(user_email: str, access_token: str) -> bytes:
    raw = f"user={user_email}\x01auth=Bearer {access_token}\x01\x01"
    return base64.b64encode(raw.encode())


# ── OAuth callback server ──────────────────────────────────────────────────────

class _OAuthCallbackHandler(BaseHTTPRequestHandler):
    """One-shot HTTP handler: captures ?code= and shuts down the server."""

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        params = urllib.parse.parse_qs(parsed.query)
        self.server.auth_code = params.get("code", [None])[0]
        self.server.error     = params.get("error", [None])[0]

        # Send a friendly close-tab page
        body = b"""<!DOCTYPE html>
<html><head><meta charset=utf-8><title>LocalMind</title>
<style>body{font-family:system-ui;display:flex;align-items:center;
justify-content:center;height:100vh;margin:0;background:#0f0f0f;color:#e8e8e8;}
.card{text-align:center;padding:40px;border-radius:16px;background:#1a1a1a;border:1px solid #333}
h2{margin:0 0 8px;font-size:20px}p{color:#999;font-size:14px;margin:0}
.paw{font-size:48px;margin-bottom:16px}</style></head>
<body><div class=card><div class=paw>&#x1F43E;</div>
<h2>You're connected!</h2><p>You can close this tab and return to LocalMind.</p>
</div></body></html>"""
        self.send_response(200)
        self.send_header("Content-Type", "text/html")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        # Signal the server to shut down after this request
        threading.Thread(target=self.server.shutdown, daemon=True).start()

    def log_message(self, *args):
        pass  # suppress access logs


def _exchange_code_for_tokens(client: dict, code: str, redirect_uri: str) -> dict | None:
    payload = urllib.parse.urlencode({
        "code":          code,
        "client_id":     client["client_id"],
        "client_secret": client["client_secret"],
        "redirect_uri":  redirect_uri,
        "grant_type":    "authorization_code",
    }).encode()
    try:
        req  = urllib.request.Request(TOKEN_URL, data=payload, method="POST")
        resp = urllib.request.urlopen(req, timeout=15)
        data = json.loads(resp.read())
        data["expires_at"] = time.time() + data.get("expires_in", 3600) - 60
        return data
    except Exception:
        return None


def _auto_sync() -> None:
    """Run an initial Gmail sync right after OAuth so Buddy has data immediately."""
    import asyncio
    try:
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        connector = GmailConnector()
        loop.run_until_complete(connector.sync())
        loop.close()
    except Exception:
        pass  # non-fatal — user can sync manually


GMAIL_API_BASE = "https://gmail.googleapis.com/gmail/v1/users/me"


def _gmail_api_get(access_token: str, path: str, params: dict | None = None) -> dict:
    """GET request to Gmail REST API. Returns parsed JSON dict."""
    url = GMAIL_API_BASE + path
    if params:
        url += "?" + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {access_token}"})
    resp = urllib.request.urlopen(req, timeout=15)
    return json.loads(resp.read())


def _fetch_recent_via_api(access_token: str, limit: int = 15) -> list[dict]:
    """
    Fetch recent messages via Gmail REST API then IMAP fallback.
    Returns list of {date, from, subject, snippet}.
    Raises on total failure so callers can see the actual error.
    """
    import logging as _log
    import urllib.error

    last_error: str = ""

    # ── Attempt 1: Gmail REST API ────────────────────────────────────────────
    try:
        data = _gmail_api_get(access_token, "/messages", {"maxResults": limit})
        message_ids = [m["id"] for m in data.get("messages", [])]
        if message_ids:
            results = []
            for msg_id in message_ids:
                try:
                    msg = _gmail_api_get(
                        access_token,
                        f"/messages/{msg_id}",
                        {"format": "metadata", "metadataHeaders": "Subject,From,Date"},
                    )
                    hdrs = {h["name"]: h["value"] for h in msg.get("payload", {}).get("headers", [])}
                    from_raw = hdrs.get("From", "unknown")
                    sender_name, _ = email.utils.parseaddr(from_raw)
                    results.append({
                        "date":    (hdrs.get("Date") or "")[:16],
                        "from":    sender_name or from_raw,
                        "subject": hdrs.get("Subject", "(no subject)"),
                        "snippet": msg.get("snippet", "")[:120],
                    })
                except Exception:
                    continue
            if results:
                return results
    except urllib.error.HTTPError as exc:
        body = exc.read().decode(errors="replace")
        last_error = f"REST API HTTP {exc.code}: {body[:300]}"
        _log.warning("[gmail] REST API failed (%s): %s", exc.code, body[:200])
    except Exception as exc:
        last_error = f"REST API error: {exc}"
        _log.warning("[gmail] REST API error: %s", exc)

    # ── Attempt 2: IMAP (all mailboxes) ─────────────────────────────────────
    _log.info("[gmail] falling back to IMAP")
    try:
        tokens = _load_tokens()
        user_email = (tokens or {}).get("email", "") if tokens else ""
        if not user_email:
            raise RuntimeError("No email in token — cannot IMAP authenticate")

        mail = imaplib.IMAP4_SSL(IMAP_HOST, IMAP_PORT)
        xoauth2 = _xoauth2_string(user_email, access_token)
        mail.authenticate("XOAUTH2", lambda _: xoauth2)

        # Gmail IMAP folder names with spaces must be double-quoted
        mailboxes = ["INBOX", '"[Gmail]/All Mail"', '"[Gmail]/Sent Mail"']
        nums: list[bytes] = []
        for mbox in mailboxes:
            try:
                ok, _ = mail.select(mbox, readonly=True)
                if ok != "OK":
                    continue
                _, resp = mail.search(None, "ALL")
                found = resp[0].split() if resp[0] else []
                if found:
                    nums = found[-limit:]
                    break
            except Exception:
                continue

        results = []
        for num in reversed(nums):
            try:
                _, data = mail.fetch(num, "(RFC822.HEADER)")
                raw = data[0][1]
                msg = email.message_from_bytes(raw)
                from_raw = msg.get("From", "unknown")
                sender_name, _ = email.utils.parseaddr(from_raw)
                results.append({
                    "date":    (msg.get("Date") or "")[:16],
                    "from":    sender_name or from_raw,
                    "subject": _decode_header_value(msg.get("Subject", "(no subject)")),
                    "snippet": "",
                })
            except Exception:
                continue
        mail.logout()
        if results:
            return results
        last_error = (last_error or "") + " | IMAP: connected but 0 messages in all folders"
    except Exception as exc:
        last_error = (last_error or "") + f" | IMAP error: {exc}"
        _log.warning("[gmail] IMAP fallback failed: %s", exc)

    # Both failed — raise so debug endpoint shows the real error
    raise RuntimeError(last_error or "Both REST API and IMAP returned 0 messages")


def _fetch_user_email(access_token: str) -> str | None:
    """Call Google userinfo to get the authenticated email address."""
    import logging as _log
    try:
        req = urllib.request.Request(
            "https://www.googleapis.com/oauth2/v2/userinfo",
            headers={"Authorization": f"Bearer {access_token}"},
        )
        resp = urllib.request.urlopen(req, timeout=10)
        data = json.loads(resp.read())
        email_addr = data.get("email")
        if not email_addr:
            _log.warning("[gmail] userinfo returned no email field: %s", list(data.keys()))
        return email_addr
    except Exception as exc:
        _log.warning("[gmail] _fetch_user_email failed: %s", exc)
        return None


def _run_oauth_flow(client: dict) -> None:
    """
    Background thread:
    1. Starts local HTTP server
    2. Waits up to 5 minutes for the callback
    3. Exchanges code → tokens → saves
    """
    server = HTTPServer(("127.0.0.1", 0), _OAuthCallbackHandler)
    server.auth_code = None
    server.error = None
    port = server.server_address[1]

    redirect_uri = f"http://127.0.0.1:{port}/callback"

    # Build the auth URL and write to a temp file so status() can read it
    params = {
        "client_id":     client["client_id"],
        "redirect_uri":  redirect_uri,
        "response_type": "code",
        "scope":         SCOPES,
        "access_type":   "offline",
        "prompt":        "consent",
    }
    auth_url = AUTH_URL + "?" + urllib.parse.urlencode(params)

    # Write pending state to disk so status() returns CONNECTING
    TOKENS_DIR.mkdir(parents=True, exist_ok=True)
    pending_path = TOKENS_DIR / "gmail_oauth_pending.json"
    pending_path.write_text(json.dumps({
        "auth_url":    auth_url,
        "redirect_uri": redirect_uri,
        "port":        port,
    }))

    # Wait for Google's redirect (5 minute timeout)
    server.timeout = 1
    deadline = time.time() + 300
    while time.time() < deadline and server.auth_code is None and server.error is None:
        server.handle_request()

    # Clean up pending file
    if pending_path.exists():
        pending_path.unlink()

    if server.auth_code:
        tokens = _exchange_code_for_tokens(client, server.auth_code, redirect_uri)
        if tokens:
            email_addr = _fetch_user_email(tokens["access_token"])
            tokens["email"] = email_addr or ""
            _save_tokens(tokens)
            # Auto-trigger initial sync so Buddy has emails immediately
            threading.Thread(target=_auto_sync, daemon=True).start()


# ── Email parsing helpers ──────────────────────────────────────────────────────

def _decode_header_value(value: str | None) -> str:
    if not value:
        return ""
    parts = decode_header(value)
    decoded = []
    for part, charset in parts:
        if isinstance(part, bytes):
            decoded.append(part.decode(charset or "utf-8", errors="replace"))
        else:
            decoded.append(part)
    return " ".join(decoded)


def _get_text_body(msg) -> str | None:
    if msg.is_multipart():
        for part in msg.walk():
            if part.get_content_type() == "text/plain":
                payload = part.get_payload(decode=True)
                if payload:
                    return payload.decode(part.get_content_charset() or "utf-8", errors="replace")
    else:
        if msg.get_content_type() == "text/plain":
            payload = msg.get_payload(decode=True)
            if payload:
                return payload.decode(msg.get_content_charset() or "utf-8", errors="replace")
    return None


# ── Connector ──────────────────────────────────────────────────────────────────

class GmailConnector(BaseConnector):
    meta = ConnectorMeta(
        id="gmail",
        name="Gmail",
        description="Read emails, search your inbox, let Buddy draft replies.",
        icon="📧",
        requires_browser=False,
        platform="all",
    )

    def _imap_login(self) -> imaplib.IMAP4_SSL | None:
        """Returns an authenticated IMAP connection, or None."""
        access_token, user_email = _get_valid_access_token()
        if not access_token or not user_email:
            return None
        try:
            mail = imaplib.IMAP4_SSL(IMAP_HOST, IMAP_PORT)
            xoauth2 = _xoauth2_string(user_email, access_token)
            mail.authenticate("XOAUTH2", lambda _: xoauth2)
            return mail
        except Exception:
            return None

    async def status(self) -> ConnectorStatus:
        # OAuth flow in progress?
        pending = TOKENS_DIR / "gmail_oauth_pending.json"
        if pending.exists():
            return ConnectorStatus.CONNECTING

        # Check token validity without a live IMAP round-trip (faster + more reliable)
        client = _load_client()
        tokens = _load_tokens()
        if not client or not tokens:
            return ConnectorStatus.DISCONNECTED

        # If token is still fresh, trust it
        if time.time() < tokens.get("expires_at", 0):
            return ConnectorStatus.CONNECTED

        # Try to refresh
        new_token = _refresh_access_token(client, tokens)
        return ConnectorStatus.CONNECTED if new_token else ConnectorStatus.DISCONNECTED

    async def connect(self, client_id: str = "", client_secret: str = "", **kwargs) -> dict[str, Any]:
        """
        Step 1 (no client creds stored): return credential_required to collect client_id/secret.
        Step 2 (client creds present): start OAuth flow, return oauth_url for the frontend to open.
        """
        # If new client creds were just submitted, save them
        if client_id and client_secret:
            _save_client(client_id.strip(), client_secret.strip())

        client = _load_client()

        if not client:
            return {
                "type": "credential_required",
                "message": "One-time Google app setup required.",
                "fields": [
                    {
                        "key": "client_id",
                        "label": "Google Client ID",
                        "type": "text",
                        "placeholder": "123456789-abc.apps.googleusercontent.com",
                        "help": "Step 1: Go to console.cloud.google.com → APIs & Services → Credentials → Create OAuth client → Desktop app. Copy the Client ID here.",
                    },
                    {
                        "key": "client_secret",
                        "label": "Google Client Secret",
                        "type": "password",
                        "placeholder": "GOCSPX-...",
                        "help": "Step 2: Copy the Client Secret from the same screen. You only need to do this once.",
                    },
                ],
            }

        # Already have valid tokens?
        access_token, _ = _get_valid_access_token()
        if access_token:
            return {"type": "ready"}

        # Start OAuth flow in background thread
        pending = TOKENS_DIR / "gmail_oauth_pending.json"
        if not pending.exists():
            thread = threading.Thread(target=_run_oauth_flow, args=(client,), daemon=True)
            thread.start()
            # Give the thread a moment to write the pending file
            time.sleep(0.4)

        # Read the auth URL from the pending file
        try:
            pending_data = json.loads(pending.read_text())
            auth_url = pending_data["auth_url"]
        except Exception:
            return {"type": "error", "message": "Failed to start OAuth flow. Try again."}

        return {"type": "oauth_url", "url": auth_url}

    async def disconnect(self) -> None:
        _delete_tokens()

    async def sync(self, cursor: str | None = None) -> SyncResult:
        init_db()
        access_token, user_email = _get_valid_access_token()
        if not access_token or not user_email:
            return SyncResult(connector_id="gmail", items_synced=0, cursor=cursor or "", error="Not authenticated")

        stored_cursor = cursor or get_cursor("gmail")
        try:
            import asyncio
            items = await asyncio.get_event_loop().run_in_executor(
                None, self._sync_blocking, access_token, user_email, stored_cursor
            )
        except Exception as e:
            return SyncResult(connector_id="gmail", items_synced=0, cursor=stored_cursor or "", error=str(e))

        new_cursor = datetime.now(timezone.utc).strftime("%d-%b-%Y")
        set_cursor("gmail", new_cursor, items)
        return SyncResult(connector_id="gmail", items_synced=items, cursor=new_cursor)

    def _sync_blocking(self, access_token: str, user_email: str, stored_cursor: str | None) -> int:
        mail = imaplib.IMAP4_SSL(IMAP_HOST, IMAP_PORT)
        xoauth2 = _xoauth2_string(user_email, access_token)
        mail.authenticate("XOAUTH2", lambda _: xoauth2)

        if stored_cursor:
            search_criteria = f'(SINCE "{stored_cursor}")'
        else:
            from datetime import timedelta
            week_ago = (datetime.now() - timedelta(days=30)).strftime("%d-%b-%Y")
            search_criteria = f'(SINCE "{week_ago}")'

        # Try INBOX first; if empty fall back to All Mail (handles archived-inbox users)
        msg_list: list[bytes] = []
        for mailbox in ["INBOX", "[Gmail]/All Mail"]:
            try:
                status, _ = mail.select(mailbox, readonly=True)
                if status != "OK":
                    continue
                _, msg_nums = mail.search(None, search_criteria)
                found = msg_nums[0].split() if msg_nums[0] else []
                if found:
                    msg_list = found
                    break
            except Exception:
                continue

        items_synced = 0
        for num in msg_list[-100:]:
            try:
                _, data = mail.fetch(num, "(RFC822)")
                raw = data[0][1]
                msg = email.message_from_bytes(raw)

                subject       = _decode_header_value(msg.get("Subject", "(no subject)"))
                from_raw      = msg.get("From", "unknown")
                date_str      = msg.get("Date", "")
                msg_id        = msg.get("Message-ID", str(num))
                thread_id_hdr = msg.get("Thread-Index") or msg.get("References") or msg_id

                sender_name, sender_email_addr = email.utils.parseaddr(from_raw)
                sender_name = sender_name or sender_email_addr

                try:
                    sent_at = email.utils.parsedate_to_datetime(date_str).isoformat()
                except Exception:
                    sent_at = datetime.utcnow().isoformat()

                body      = _get_text_body(msg)
                direction = "outbound" if sender_email_addr.lower() == user_email.lower() else "inbound"

                contact_id   = upsert_contact("gmail", sender_email_addr, sender_name or None)
                thread_db_id = upsert_thread("gmail", thread_id_hdr, title=subject)
                upsert_message(
                    source="gmail", source_id=msg_id, thread_id=thread_db_id,
                    contact_id=contact_id, direction=direction,
                    body=body, sent_at=sent_at, is_read=False,
                )
                items_synced += 1
            except Exception:
                continue

        mail.logout()
        return items_synced
    async def context_for_buddy(self, query: str) -> str:
        """
        Return recent Gmail context for Buddy.
        1. Tries local DB (populated by sync)
        2. Falls back to live Gmail REST API fetch (no IMAP quirks)
        """
        import asyncio
        import logging as _log
        from ..db.messages import recent_messages

        def _format_emails(emails: list[dict], source: str) -> str:
            lines = [f"The user has {len(emails)} recent emails. Use ONLY these facts to answer — do not guess:\n"]
            for i, m in enumerate(emails, 1):
                lines.append(f"EMAIL {i}:")
                lines.append(f"  From: {m.get('from') or m.get('display_name') or m.get('contact_id') or 'Unknown'}")
                lines.append(f"  Subject: {m.get('subject') or m.get('thread_title') or '(no subject)'}")
                lines.append(f"  Date: {(m.get('date') or m.get('sent_at') or '')[:10]}")
                body = (m.get("snippet") or m.get("body") or "")[:150].replace("\n", " ")
                if body:
                    lines.append(f"  Preview: {body}")
                lines.append("")
            return "\n".join(lines)

        msgs = recent_messages(source="gmail", limit=15)
        if msgs:
            return _format_emails(msgs, "db")

        # DB empty — live fetch via Gmail REST API
        access_token, _ = _get_valid_access_token()
        if not access_token:
            _log.warning("[gmail] context_for_buddy: no valid access token")
            return ""

        try:
            live = await asyncio.get_event_loop().run_in_executor(
                None, _fetch_recent_via_api, access_token
            )
            return _format_emails(live, "api")
        except Exception as exc:
            _log.warning("[gmail] context_for_buddy failed: %s", exc)
            return ""

    def _fetch_recent_live(self, access_token: str, user_email: str, limit: int = 10) -> list[dict]:
        """Legacy IMAP fetch — kept for the debug endpoint only."""
        return _fetch_recent_via_api(access_token, limit)
