"""Regression coverage for isolated desktop profiles and the local IPC boundary."""

import os
import sqlite3
import tempfile
import unittest
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch

from app.security import authorized, configured_token
from app.storage import data_dir, default_data_dir, migrate_legacy_connectors, profile_lock


class LocalTransportTests(unittest.IsolatedAsyncioTestCase):
    async def test_ollama_calls_bypass_environment_proxy(self):
        from app.api.routes.models import models_status

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'{"version":"test-local-ollama"}')

            def log_message(self, *_):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            proxy_environment = {
                "HTTP_PROXY": "http://127.0.0.1:1",
                "HTTPS_PROXY": "http://127.0.0.1:1",
                "ALL_PROXY": "http://127.0.0.1:1",
                "NO_PROXY": "",
            }
            with patch.dict(os.environ, proxy_environment), patch("app.api.routes.models.OLLAMA_BASE", f"http://127.0.0.1:{server.server_port}"):
                response = await models_status()
            self.assertEqual(json.loads(response.body), {"running": True, "version": "test-local-ollama"})
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


class LocalAuthenticationTests(unittest.TestCase):
    def test_health_and_data_routes_both_require_authentication(self):
        from fastapi import FastAPI
        from fastapi.testclient import TestClient
        from app.api.middleware.auth import verify_bearer_token
        from app.api.routes.health import router
        app = FastAPI()
        app.middleware("http")(verify_bearer_token)
        app.include_router(router)
        with patch.dict(os.environ, {"LOCALMIND_TOKEN": "launch-secret"}), TestClient(app) as client:
            for path in ("/health", "/conversations", "/openapi.json"):
                self.assertEqual(client.get(path).status_code, 401)
            response = client.get("/health", headers={"Authorization": "Bearer launch-secret"})
            self.assertEqual(response.status_code, 200)
            self.assertEqual(response.json(), {"status": "ok"})

    def test_unconfigured_service_cannot_authenticate_even_empty_bearer(self):
        for token in ("", "   "):
            with patch.dict(os.environ, {"LOCALMIND_TOKEN": token}):
                self.assertFalse(authorized(f"Bearer {token}"))
                with self.assertRaises(RuntimeError):
                    configured_token()

    def test_only_this_launch_token_authenticates(self):
        with patch.dict(os.environ, {"LOCALMIND_TOKEN": "per-session-secret"}):
            for header in ("", "Bearer", "Bearer old-secret", "bearer per-session-secret", "Bearer café"):
                self.assertFalse(authorized(header))
            self.assertTrue(authorized("Bearer per-session-secret"))


class DesktopProfileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.environment = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": str(self.root / "profile-a")})
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def test_profile_paths_must_be_absolute(self):
        with patch.dict(os.environ, {"LOCALMIND_DATA_DIR": "shared-relative-folder"}):
            with self.assertRaisesRegex(RuntimeError, "absolute"):
                data_dir()

    def test_standalone_service_uses_os_user_data_not_shared_temp(self):
        with patch.dict(os.environ, {"LOCALMIND_DATA_DIR": "", "APPDATA": str(self.root)}), patch("app.storage.sys.platform", "win32"):
            self.assertEqual(data_dir(), self.root / "LocalMind")
        with patch.dict(os.environ, {"APPDATA": ""}), patch("app.storage.sys.platform", "win32"):
            with self.assertRaises(RuntimeError):
                default_data_dir()

    def test_separate_profiles_do_not_share_chats_connectors_or_settings(self):
        from app.db import conversations, messages, people, search_config
        first = conversations.create_conversation("Private to A")
        messages.init_db()
        people.init_db()
        search_config.update_config(enabled=True)
        with patch.dict(os.environ, {"LOCALMIND_DATA_DIR": str(self.root / "profile-b")}):
            self.assertEqual(conversations.list_conversations(), [])
            self.assertFalse(search_config.get_config()["enabled"])
            messages.init_db()
            people.init_db()
            for module, filename in ((messages, "messages.db"), (people, "people.db")):
                conn = module.get_conn()
                try:
                    actual = Path(conn.execute("PRAGMA database_list").fetchone()[2])
                    self.assertEqual(actual, self.root / "profile-b" / filename)
                finally:
                    conn.close()
        self.assertEqual(conversations.list_conversations()[0]["id"], first["id"])
        self.assertTrue(search_config.get_config()["enabled"])

    def test_profile_has_only_one_runtime_and_lock_is_released(self):
        with profile_lock():
            with self.assertRaisesRegex(RuntimeError, "already using"):
                with profile_lock():
                    self.fail("Second runtime acquired the same profile")
        with profile_lock():
            pass

    def test_first_install_can_associate_chats_and_projects_without_restart(self):
        from app.db import conversations, projects
        project = projects.create_project("First project")
        self.assertEqual(projects.get_project_conversations(project["id"]), [])
        conversation = conversations.create_conversation("First chat")
        self.assertTrue(projects.assign_conversation(conversation["id"], project["id"]))
        self.assertEqual(projects.get_project_conversations(project["id"])[0]["id"], conversation["id"])
        self.assertTrue(projects.assign_conversation(conversation["id"], None))
        self.assertEqual(projects.get_project_conversations(project["id"]), [])

    def test_legacy_migration_preserves_wal_data_and_never_overwrites(self):
        legacy = self.root / ".localmind"
        legacy.mkdir()
        source = sqlite3.connect(legacy / "messages.db")
        try:
            source.execute("PRAGMA journal_mode=WAL")
            source.execute("CREATE TABLE retained (content TEXT)")
            source.execute("INSERT INTO retained VALUES ('old message')")
            source.commit()
            profile = data_dir()
            with patch("app.storage.Path.home", return_value=self.root), patch("app.storage.default_data_dir", return_value=profile):
                migrate_legacy_connectors()
                target = sqlite3.connect(profile / "messages.db")
                try:
                    self.assertEqual(target.execute("SELECT content FROM retained").fetchone()[0], "old message")
                    target.execute("UPDATE retained SET content = 'new message'")
                    target.commit()
                    migrate_legacy_connectors()
                    self.assertEqual(target.execute("SELECT content FROM retained").fetchone()[0], "new message")
                finally:
                    target.close()
            self.assertTrue((legacy / "messages.db").exists())
        finally:
            source.close()

    def test_explicit_custom_profile_never_imports_personal_data(self):
        legacy = self.root / ".localmind" / "tokens"
        legacy.mkdir(parents=True)
        (legacy / "gmail_oauth.json").write_text('{"access_token": "private"}')
        with patch("app.storage.Path.home", return_value=self.root), patch("app.storage.default_data_dir", return_value=self.root / "default-profile"):
            migrate_legacy_connectors()
        self.assertFalse((data_dir() / "tokens").exists())

    def test_completed_migration_does_not_restore_deleted_credentials(self):
        legacy = self.root / ".localmind" / "tokens"
        legacy.mkdir(parents=True)
        (legacy / "gmail_oauth.json").write_text('{"access_token": "private"}')
        profile = data_dir()
        with patch("app.storage.Path.home", return_value=self.root), patch("app.storage.default_data_dir", return_value=profile):
            migrate_legacy_connectors()
            migrated = profile / "tokens" / "gmail_oauth.json"
            self.assertTrue(migrated.exists())
            migrated.unlink()
            migrated.parent.rmdir()
            migrate_legacy_connectors()
            self.assertFalse(migrated.exists())

    def test_custom_profile_works_without_platform_default_directory(self):
        with patch("app.storage.default_data_dir", side_effect=RuntimeError("missing APPDATA")):
            migrate_legacy_connectors()
            self.assertEqual(data_dir(), self.root / "profile-a")


if __name__ == "__main__":
    unittest.main()
