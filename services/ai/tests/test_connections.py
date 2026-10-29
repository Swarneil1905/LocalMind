from contextlib import redirect_stderr
import io
import json
import os
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock, patch
from urllib.parse import parse_qs, urlsplit

import httpx
from fastapi import FastAPI

from app.credentials import CredentialStore, CredentialUnavailable
from app.connectors.gmail import GmailConnector, SCOPE, MAX_ITEMS
from app.connectors.base import ConnectorStatus
from app.db import messages


class MemoryVault:
    """Synthetic OS boundary fixture, never registered as a production backend."""

    def __init__(self):
        self.values = {}
        self.locked = False

    def get_password(self, service, key):
        if self.locked:
            raise RuntimeError("seeded-secret-must-never-leak")
        return self.values.get((service, key))

    def set_password(self, service, key, value):
        if self.locked:
            raise RuntimeError("seeded-secret-must-never-leak")
        self.values[service, key] = value

    def delete_password(self, service, key):
        self.values.pop((service, key), None)


class FakeGoogle:
    """Paginated deterministic provider with expiry, offline, and revoke controls."""

    now = 1_800_000_000

    def __init__(self):
        self.offline = False
        self.reject = False
        self.revoked = False
        self.calls = []
        self.bad_date = False
        self.large = False

    def __call__(self, request):
        self.calls.append(request)
        if self.offline:
            raise httpx.ConnectError("seeded-secret-must-never-leak", request=request)
        if request.url.path == "/revoke":
            self.revoked = True
            return httpx.Response(200)
        if self.reject or self.revoked:
            return httpx.Response(401, text="seeded-secret-must-never-leak")
        if request.url.path == "/token":
            return httpx.Response(
                200,
                json={
                    "access_token": "fake-access",
                    "refresh_token": "fake-refresh",
                    "expires_in": 3600,
                    "scope": SCOPE,
                },
            )
        if request.url.path.endswith("/messages"):
            if self.large:
                page = int(request.url.params.get("pageToken", "0"))
                return httpx.Response(
                    200,
                    json={
                        "messages": [{"id": str(page * 50 + i)} for i in range(50)],
                        "nextPageToken": str(page + 1),
                    },
                )
            if request.url.params.get("pageToken"):
                return httpx.Response(200, json={"messages": [{"id": "recent2"}]})
            return httpx.Response(
                200,
                json={"messages": [{"id": "recent1"}, {"id": "old1"}], "nextPageToken": "page2"},
            )
        stamp = self.now - (40 * 86400 if request.url.path.endswith("old1") else 100)
        return httpx.Response(
            200,
            json={
                "internalDate": "invalid" if self.bad_date else str(stamp * 1000),
                "snippet": "Synthetic mail sample",
            },
        )


class CredentialTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        patcher = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": self.temp.name})
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_roundtrip_replace_delete_and_profile_isolation(self):
        backend = MemoryVault()
        store = CredentialStore(backend)
        store.write("gmail-token", {"token": "one"})
        store.write("gmail-token", {"token": "two"})
        self.assertEqual(CredentialStore(backend).read("gmail-token"), {"token": "two"})
        with patch.dict(os.environ, {"LOCALMIND_DATA_DIR": self.temp.name + "/other"}):
            self.assertIsNone(CredentialStore(backend).read("gmail-token"))
        store.delete("gmail-token")
        self.assertIsNone(store.read("gmail-token"))
        with self.assertRaises(ValueError):
            store.read("arbitrary-system-password")

    def test_locked_store_has_no_plaintext_fallback_or_secret_in_error(self):
        vault = MemoryVault()
        vault.locked = True
        output = io.StringIO()
        with redirect_stderr(output):
            for operation in (
                lambda: CredentialStore(vault).read("gmail-token"),
                lambda: CredentialStore(vault).write(
                    "gmail-token", {"token": "seeded-secret-must-never-leak"}
                ),
            ):
                with self.assertRaises(CredentialUnavailable) as raised:
                    operation()
                self.assertNotIn("seeded-secret", str(raised.exception))
        self.assertNotIn("seeded-secret", output.getvalue())
        self.assertEqual(os.listdir(self.temp.name), [])

    @unittest.skipUnless(
        sys.platform == "win32" and os.environ.get("LOCALMIND_TEST_NATIVE_CREDENTIALS") == "1",
        "explicit Windows native credential test",
    )
    def test_native_windows_persistence(self):
        store = CredentialStore()
        try:
            store.write("credential-test", {"token": "localmind-synthetic-test-only"})
            self.assertEqual(
                CredentialStore().read("credential-test"),
                {"token": "localmind-synthetic-test-only"},
            )
        finally:
            store.delete("credential-test")
        self.assertIsNone(store.read("credential-test"))


class GmailTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        patcher = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": self.temp.name})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.vault = MemoryVault()
        self.store = CredentialStore(self.vault)
        self.provider = FakeGoogle()
        self.gmail = GmailConnector(
            self.store, httpx.MockTransport(self.provider), lambda: self.provider.now
        )
        self.addAsyncCleanup(self.gmail.close_auth)
        self.store.write(
            "gmail-client", {"client_id": "fake-client", "client_secret": "fake-secret"}
        )
        self.store.write(
            "gmail-token",
            {
                "access_token": "fake-access",
                "refresh_token": "fake-refresh",
                "expires_at": self.provider.now + 60,
            },
        )

    async def test_pagination_range_idempotence_and_no_actions(self):
        result = await self.gmail.sync()
        self.assertEqual(result.items_synced, 2)
        self.assertEqual(len(messages.recent_messages("gmail")), 2)
        await self.gmail.sync()
        self.assertEqual(len(messages.recent_messages("gmail")), 2)
        listing = next(call for call in self.provider.calls if call.url.path.endswith("/messages"))
        self.assertIn("after:", listing.url.params["q"])
        self.assertEqual(self.gmail.capabilities.actions, ())
        with self.assertRaises(PermissionError):
            await self.gmail.action("send_mail", {})

    async def test_total_sync_budget(self):
        self.provider.large = True
        self.assertEqual((await self.gmail.sync()).items_synced, MAX_ITEMS)
        self.assertEqual(len(self.provider.calls), 102)

    async def test_expiry_refresh_offline_recovery_and_revoke(self):
        self.store.write(
            "gmail-token", {"access_token": "old", "refresh_token": "fake-refresh", "expires_at": 1}
        )
        self.assertEqual(await self.gmail.status(), ConnectorStatus.EXPIRED)
        self.provider.offline = True
        self.assertIsNotNone((await self.gmail.sync()).error)
        self.assertEqual(await self.gmail.status(), ConnectorStatus.OFFLINE)
        self.assertNotIn("seeded-secret", self.gmail.detail)
        self.provider.offline = False
        self.assertIsNone((await self.gmail.sync()).error)
        self.assertEqual(self.store.read("gmail-token")["access_token"], "fake-access")
        await self.gmail.disconnect()
        count = len(self.provider.calls)
        self.assertTrue(self.provider.revoked)
        self.assertIsNone(self.store.read("gmail-token"))
        self.assertIsNotNone((await self.gmail.sync()).error)
        self.assertEqual(count, len(self.provider.calls))

    async def test_failed_revoke_disables_sync_across_restart(self):
        self.provider.offline = True
        with self.assertRaises(RuntimeError):
            await self.gmail.disconnect()
        self.assertEqual(await self.gmail.status(), ConnectorStatus.DISABLED)
        restarted = GmailConnector(
            self.store, httpx.MockTransport(self.provider), lambda: self.provider.now
        )
        count = len(self.provider.calls)
        self.assertIsNotNone((await restarted.sync()).error)
        self.assertEqual(count, len(self.provider.calls))
        self.provider.offline = False
        await restarted.disconnect()
        self.assertEqual(await restarted.status(), ConnectorStatus.DISCONNECTED)

    async def test_invalid_date_never_partially_publishes(self):
        self.provider.bad_date = True
        self.assertIsNotNone((await self.gmail.sync()).error)
        messages.init_db()
        self.assertEqual(messages.recent_messages("gmail"), [])

    async def test_locked_storage_never_reports_connected(self):
        self.vault.locked = True
        self.assertEqual(await self.gmail.status(), ConnectorStatus.LOCKED)
        self.assertEqual((await self.gmail.connect())["type"], "error")

    async def test_declined_scope_and_invalid_credentials_do_not_connect(self):
        from app.connectors.gmail import ProviderError
        with self.assertRaises(ProviderError):
            self.gmail._token_record({"access_token": "fake", "scope": "https://mail.google.com/"})
        self.assertEqual((await self.gmail.connect(client_id=123, client_secret="fake"))["type"], "error")

    async def test_atomic_snapshot_rolls_back_on_storage_failure(self):
        from app.connectors.gmail import _save_snapshot
        import sqlite3
        messages.init_db()
        connection = messages.get_conn()
        try:
            with connection:
                connection.execute("CREATE TRIGGER reject_fixture BEFORE INSERT ON messages WHEN NEW.source_id='bad' BEGIN SELECT RAISE(ABORT, 'fixture failure'); END")
        finally:
            connection.close()
        with self.assertRaises(sqlite3.IntegrityError):
            _save_snapshot([("good", self.provider.now, "sample"), ("bad", self.provider.now, "sample")], self.provider.now)
        self.assertEqual(messages.recent_messages("gmail"), [])
        self.assertIsNone(messages.get_cursor("gmail"))
        with patch("app.connectors.gmail._save_snapshot", side_effect=sqlite3.OperationalError("private details")):
            result = await self.gmail.sync()
        self.assertIsNotNone(result.error)
        self.assertNotIn("private details", result.error)
        self.assertEqual(await self.gmail.status(), ConnectorStatus.ERROR)

    async def test_oauth_scope_pkce_state_and_replay(self):
        self.store.delete("gmail-token")
        response = await self.gmail.connect()
        query = parse_qs(urlsplit(response["url"]).query)
        self.assertEqual(query["scope"], [SCOPE])
        self.assertEqual(query["code_challenge_method"], ["S256"])
        self.assertFalse(await self.gmail.handle_oauth_callback("fake-code", "wrong"))
        self.assertFalse(await self.gmail.handle_oauth_callback("fake-code", "wrong-\u2603"))
        self.assertEqual(len(self.provider.calls), 0)
        self.assertTrue(await self.gmail.handle_oauth_callback("fake-code", query["state"][0]))
        self.assertFalse(await self.gmail.handle_oauth_callback("fake-code", query["state"][0]))
        payload = parse_qs(self.provider.calls[0].content.decode())
        self.assertIn("code_verifier", payload)
        self.assertIsNotNone(self.store.read("gmail-token"))

    async def test_no_network_or_secrets_in_diagnostics(self):
        from app.api.routes.connectors import gmail_debug
        from app.connectors.registry import registry

        with patch.object(registry, "get", return_value=self.gmail):
            result = await gmail_debug()
        self.assertEqual(set(result), {"status", "detail", "access"})
        self.assertNotIn("fake-access", json.dumps(result))
        self.assertEqual(self.provider.calls, [])


class VoiceGateTests(unittest.IsolatedAsyncioTestCase):
    async def test_disabled_voice_has_no_execution_path(self):
        from app.api.routes.voice import router

        app = FastAPI()
        app.include_router(router)
        with (
            patch.dict(os.environ, {"LOCALMIND_VOICE_PILOT": "0"}),
            patch("app.voice.sapi", AsyncMock()) as synth,
            patch("app.voice.transcribe", AsyncMock()) as stt,
        ):
            async with httpx.AsyncClient(
                transport=httpx.ASGITransport(app=app), base_url="http://test"
            ) as client:
                state = (await client.get("/voice/status")).json()
                self.assertFalse(state["stt_ready"])
                self.assertIn("disabled_reason", state)
                self.assertEqual((await client.post("/voice/install")).status_code, 403)
                turn = "5d031cb8-3687-4cf5-9d3c-05e6dbba7704"
                self.assertEqual(
                    (
                        await client.post(
                            "/voice/transcribe", json={"turn_id": turn, "audio": "a" * 44}
                        )
                    ).status_code,
                    403,
                )
                self.assertEqual(
                    (
                        await client.post(
                            "/voice/speak",
                            json={"turn_id": turn, "text": "hello", "voice": "stock"},
                        )
                    ).status_code,
                    403,
                )
        synth.assert_not_called()
        stt.assert_not_called()
