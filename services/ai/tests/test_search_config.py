"""SearxNG settings must survive persistence and reach the selected provider."""

import os
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.api.routes.chat import _run_web_search
from app.api.routes.search_config import router
from app.db import search_config


class SearchConfigTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.profile = Path(self.temporary.name)
        environment = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": str(self.profile)})
        environment.start()
        self.addCleanup(environment.stop)
        app = FastAPI()
        app.include_router(router)
        self.client = TestClient(app)
        self.addCleanup(self.client.close)

    def test_old_database_migrates_without_losing_legacy_fields(self):
        connection = sqlite3.connect(self.profile / "search_config.db")
        try:
            connection.execute("CREATE TABLE search_config (id INTEGER PRIMARY KEY, provider TEXT, privacy_mode TEXT, enabled INTEGER)")
            connection.execute("INSERT INTO search_config VALUES (1, 'searxng', 'strict', 1)")
            connection.commit()
        finally:
            connection.close()
        self.assertEqual(search_config.get_config(), {
            "provider": "searxng", "privacy_mode": "strict", "enabled": True,
            "searxng_url": "http://localhost:8080",
        })

    def test_url_roundtrip_and_partial_updates_preserve_configuration(self):
        self.assertEqual(self.client.get("/search-config").json()["searxng_url"], "http://localhost:8080")
        response = self.client.patch("/search-config", json={
            "provider": "searxng", "searxng_url": "https://search.example.com/instance/",
        })
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(response.json()["searxng_url"], "https://search.example.com/instance")
        self.client.patch("/search-config", json={"privacy_mode": "standard", "enabled": False})
        self.assertEqual(search_config.get_config()["searxng_url"], "https://search.example.com/instance")

    def test_invalid_url_is_rejected_without_overwriting_saved_value(self):
        for value in ("file:///tmp/search", "ftp://example.com", "not-a-url", "http://", "https://user:secret@example.com", "https://example.com?api_key=secret", "https://example.com/#fragment"):
            with self.subTest(url=value):
                response = self.client.patch("/search-config", json={"searxng_url": value})
                self.assertEqual(response.status_code, 422)
                self.assertEqual(search_config.get_config()["searxng_url"], "http://localhost:8080")
        response = self.client.patch("/search-config", json={"searxng_url": "http://127.0.0.1:8181"})
        self.assertEqual(response.status_code, 200, response.text)

    async def test_persisted_url_reaches_searxng_provider_from_chat(self):
        search_config.update_config(provider="searxng", searxng_url="https://search.example.com/instance")
        with patch("app.search.providers.search_searxng", new=AsyncMock(return_value=[])) as provider:
            self.assertEqual(await _run_web_search("project update"), ("", []))
        provider.assert_awaited_once_with("project update", "https://search.example.com/instance", 5)


if __name__ == "__main__":
    unittest.main()
