"""
WhatsApp Web connector — Playwright browser bridge.

How it works:
1. First connect: launch Chromium headless=False, open web.whatsapp.com
2. UI shows QR code screen — user scans with phone as normal
3. After scan, session is saved to ~/.localmind/wa_session/ (persistent context)
4. All future syncs: headless=True, session auto-restored, no QR needed
5. Incremental sync: scrape new messages since last cursor timestamp

Privacy: Playwright runs 100% locally. No data leaves the machine.
"""
import asyncio
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .base import BaseConnector, ConnectorMeta, ConnectorStatus, SyncResult
from ..db.messages import (
    init_db, upsert_contact, upsert_thread, upsert_message,
    get_cursor, set_cursor,
)

SESSION_DIR = Path.home() / ".localmind" / "wa_session"
STATE_FILE = Path.home() / ".localmind" / "tokens" / "wa_state.json"


class WhatsAppWebConnector(BaseConnector):
    meta = ConnectorMeta(
        id="whatsapp_web",
        name="WhatsApp",
        description="Read your WhatsApp chats. Buddy learns who you talk to and what about.",
        icon="💬",
        requires_browser=True,
        platform="all",
    )

    async def status(self) -> ConnectorStatus:
        # Connected only after QR scan succeeded (STATE_FILE written by _open_browser).
        # SESSION_DIR gets Playwright profile files immediately on launch — do NOT use it for status.
        return ConnectorStatus.CONNECTED if STATE_FILE.exists() else ConnectorStatus.DISCONNECTED

    @staticmethod
    def _playwright_installed() -> bool:
        """True only if playwright module AND Chromium binary are both present."""
        try:
            import playwright  # noqa: F401
        except ImportError:
            return False
        # Also check that the Chromium browser binary has been installed
        # playwright stores browsers under ~/.cache/ms-playwright/ (Linux/Mac)
        # or %USERPROFILE%\AppData\Local\ms-playwright\ (Windows)
        import sys
        if sys.platform == "win32":
            from pathlib import Path as _P
            ms_pw = _P.home() / "AppData" / "Local" / "ms-playwright"
        else:
            from pathlib import Path as _P
            ms_pw = _P.home() / ".cache" / "ms-playwright"
        if not ms_pw.exists():
            return False
        # Any chromium-* directory means the binary is present
        return any(d.name.startswith("chromium") for d in ms_pw.iterdir() if d.is_dir())

    @staticmethod
    async def _install_playwright() -> bool:
        """Install playwright pip package (if missing) + Chromium browser binary."""
        import sys
        try:
            # Step 1: ensure the pip package is installed
            try:
                import playwright  # noqa: F401
            except ImportError:
                proc = await asyncio.create_subprocess_exec(
                    sys.executable, "-m", "pip", "install", "playwright==1.50.0", "--quiet",
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.PIPE,
                )
                await proc.communicate()
                if proc.returncode != 0:
                    return False

            # Step 2: install Chromium browser binary
            proc2 = await asyncio.create_subprocess_exec(
                sys.executable, "-m", "playwright", "install", "chromium",
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            await proc2.communicate()
            return proc2.returncode == 0
        except Exception:
            return False

    # Real Chrome UA prevents WhatsApp from blocking the Playwright Chromium browser
    _CHROME_UA = (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/125.0.0.0 Safari/537.36"
    )

    async def connect(self, **kwargs) -> dict[str, Any]:
        """
        Open WhatsApp Web for QR scan. Auto-installs playwright if missing.
        """
        if not self._playwright_installed():
            # Install synchronously so the caller can retry immediately after
            asyncio.create_task(self._install_playwright())
            return {
                "type": "installing",
                "message": "Installing WhatsApp bridge (playwright + Chromium ~150 MB). Takes 1–2 minutes — click Connect again when done.",
            }

        # If SESSION_DIR already has content, the browser is either still open or
        # the session was saved — don't launch a second browser instance.
        if SESSION_DIR.exists() and any(SESSION_DIR.iterdir()):
            if STATE_FILE.exists():
                return {"type": "ready", "message": "Already connected."}
            return {
                "type": "browser_opening",
                "message": "WhatsApp Web is still open on your computer. Scan the QR code — the status here updates automatically once connected.",
            }

        SESSION_DIR.mkdir(parents=True, exist_ok=True)
        STATE_FILE.parent.mkdir(parents=True, exist_ok=True)

        async def _open_browser():
            from playwright.async_api import async_playwright
            async with async_playwright() as p:
                browser = await p.chromium.launch_persistent_context(
                    str(SESSION_DIR),
                    headless=False,
                    args=["--no-sandbox", "--disable-blink-features=AutomationControlled"],
                    user_agent=self._CHROME_UA,
                )
                page = browser.pages[0] if browser.pages else await browser.new_page()
                # Hide webdriver flag so WhatsApp doesn't detect automation
                await page.add_init_script(
                    "Object.defineProperty(navigator,'webdriver',{get:()=>undefined})"
                )
                await page.goto("https://web.whatsapp.com")
                # Wait for the chat list — appears after successful QR scan
                # Fallback: also accept #app fully loaded (catches layout changes)
                await page.wait_for_selector(
                    '[data-testid="chat-list"], [aria-label="Chat list"], #pane-side',
                    timeout=120_000,
                )
                # Session saved — mark as connected
                STATE_FILE.write_text(json.dumps({"connected_at": datetime.utcnow().isoformat()}))
                await browser.close()

        # Run in background — don't block the API response
        asyncio.create_task(_open_browser())

        return {
            "type": "browser_opening",
            "message": "WhatsApp Web is opening on your computer. Scan the QR code with your phone — the status here will update automatically once you're connected.",
        }

    async def disconnect(self) -> None:
        import shutil
        import sys
        # Delete STATE_FILE first — this immediately flips status() to DISCONNECTED.
        # SESSION_DIR deletion can fail on Windows if Chromium still has files open.
        if STATE_FILE.exists():
            STATE_FILE.unlink()
        if SESSION_DIR.exists():
            if sys.platform == "win32":
                # ignore_errors=True skips WinError 32 (file in use by another process)
                shutil.rmtree(SESSION_DIR, ignore_errors=True)
            else:
                shutil.rmtree(SESSION_DIR)

    async def sync(self, cursor: str | None = None) -> SyncResult:
        """Scrape messages from all WhatsApp chats since cursor timestamp."""
        init_db()

        if await self.status() != ConnectorStatus.CONNECTED:
            return SyncResult(connector_id="whatsapp_web", items_synced=0, cursor=cursor or "", error="Not connected")

        try:
            from playwright.async_api import async_playwright
        except ImportError:
            return SyncResult(connector_id="whatsapp_web", items_synced=0, cursor=cursor or "", error="playwright not installed")

        stored_cursor = cursor or get_cursor("whatsapp_web")
        items_synced = 0

        try:
            async with async_playwright() as p:
                browser = await p.chromium.launch_persistent_context(
                    str(SESSION_DIR),
                    headless=True,
                    args=["--no-sandbox", "--disable-blink-features=AutomationControlled"],
                    user_agent=self._CHROME_UA,
                )
                page = browser.pages[0] if browser.pages else await browser.new_page()
                await page.add_init_script(
                    "Object.defineProperty(navigator,'webdriver',{get:()=>undefined})"
                )
                await page.goto("https://web.whatsapp.com")

                # Wait for chats to load
                await page.wait_for_selector(
                    '[data-testid="chat-list"], [aria-label="Chat list"], #pane-side',
                    timeout=30_000,
                )
                await asyncio.sleep(2)

                # Get all chat list items (try both known selector variants)
                chat_items = await page.query_selector_all('[data-testid="cell-frame-container"]')
                if not chat_items:
                    chat_items = await page.query_selector_all('[role="listitem"]')

                for chat_item in chat_items[:30]:  # limit to 30 most recent chats per sync
                    try:
                        await chat_item.click()
                        await asyncio.sleep(0.8)

                        # Get chat name — try multiple selectors across WA Web versions
                        header = await page.query_selector(
                            '[data-testid="conversation-header"] span[title], '
                            'header span[title], '
                            '[data-testid="conversation-info-header-chat-title"] span'
                        )
                        chat_name = await header.get_attribute("title") if header else "Unknown"
                        if not chat_name and header:
                            chat_name = await header.inner_text() or "Unknown"

                        # Determine if group
                        group_info = await page.query_selector(
                            '[data-testid="group-info-header-title"], '
                            '[data-testid="conversation-info-header-chat-title"][data-is-group]'
                        )
                        is_group = group_info is not None

                        # Get chat JID from URL or data attributes
                        chat_jid = await page.evaluate("""
                            () => {
                                const chat = window.Store?.Chat?.getActiveChat?.();
                                return chat?.id?._serialized || null;
                            }
                        """) or chat_name

                        thread_id = upsert_thread("whatsapp_web", chat_jid, title=chat_name, is_group=is_group)

                        # Get all visible messages
                        msg_elements = await page.query_selector_all(
                            '[data-testid="msg-container"]'
                        )

                        for msg_el in msg_elements[-50:]:  # last 50 messages per chat
                            try:
                                msg_data = await self._parse_message(page, msg_el, chat_jid, chat_name)
                                if not msg_data:
                                    continue

                                # Skip if older than cursor
                                if stored_cursor and msg_data["sent_at"] < stored_cursor:
                                    continue

                                contact_id = upsert_contact(
                                    "whatsapp_web",
                                    msg_data["sender_id"],
                                    msg_data["sender_name"],
                                )
                                upsert_message(
                                    source="whatsapp_web",
                                    source_id=msg_data["msg_id"],
                                    thread_id=thread_id,
                                    contact_id=contact_id,
                                    direction=msg_data["direction"],
                                    body=msg_data["body"],
                                    sent_at=msg_data["sent_at"],
                                )
                                items_synced += 1
                            except Exception:
                                continue

                    except Exception:
                        continue

                await browser.close()

        except Exception as e:
            return SyncResult(connector_id="whatsapp_web", items_synced=0, cursor=stored_cursor or "", error=str(e))

        new_cursor = datetime.now(timezone.utc).isoformat()
        set_cursor("whatsapp_web", new_cursor, items_synced)
        return SyncResult(connector_id="whatsapp_web", items_synced=items_synced, cursor=new_cursor)

    async def _parse_message(self, page, msg_el, chat_jid: str, chat_name: str) -> dict | None:
        """Extract message data from a DOM element."""
        try:
            # Message text
            text_el = await msg_el.query_selector('[data-testid="msg-text"]')
            body = await text_el.inner_text() if text_el else None

            # Timestamp
            time_el = await msg_el.query_selector("time[datetime]")
            datetime_str = await time_el.get_attribute("datetime") if time_el else None
            sent_at = datetime_str or datetime.utcnow().isoformat()

            # Outbound if it has the "tail-out" class (sent by us)
            class_attr = await msg_el.get_attribute("class") or ""
            direction = "outbound" if "message-out" in class_attr else "inbound"

            # Sender name (for groups)
            sender_el = await msg_el.query_selector('[data-testid="author"]')
            sender_name = await sender_el.inner_text() if sender_el else (
                "Me" if direction == "outbound" else chat_name
            )
            sender_id = f"{sender_name.lower().replace(' ', '_')}@{chat_jid}"

            # Unique ID: hash of chat + sender + timestamp + body snippet
            import hashlib
            msg_id = hashlib.sha1(
                f"{chat_jid}:{sender_id}:{sent_at}:{(body or '')[:40]}".encode()
            ).hexdigest()

            return {
                "msg_id": msg_id,
                "sender_id": sender_id,
                "sender_name": sender_name,
                "body": body,
                "sent_at": sent_at,
                "direction": direction,
            }
        except Exception:
            return None

    async def context_for_buddy(self, query: str) -> str:
        """Return recent WhatsApp messages in structured format for Buddy's context."""
        from ..db.messages import recent_messages
        msgs = recent_messages(source="whatsapp_web", limit=10)
        if not msgs:
            return ""
        lines = [f"The user has {len(msgs)} recent WhatsApp messages. Use ONLY these facts — do not guess:\n"]
        for i, m in enumerate(msgs, 1):
            name = m["display_name"] or "Unknown"
            snippet = (m["body"] or "")[:150].replace("\n", " ")
            direction = m.get("direction", "inbound")
            lines.append(f"WHATSAPP {i}:")
            lines.append(f"  {'To' if direction == 'outbound' else 'From'}: {name}")
            lines.append(f"  Chat: {m['thread_title'] or name}")
            lines.append(f"  Date: {(m['sent_at'] or '')[:10]}")
            if snippet:
                lines.append(f"  Message: {snippet}")
            lines.append("")
        return "\n".join(lines)
