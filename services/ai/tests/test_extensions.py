import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import httpx
from fastapi import FastAPI

from app import plugins, recipes
from app.connectors.unavailable import UnavailableConnector
from app.api.routes.extensions import router
from app.api.middleware.auth import verify_bearer_token


class ExtensionTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.root = Path(folder.name)
        self.addCleanup(folder.cleanup)
        patcher = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": str(self.root)})
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_manifest_registry_enable_disable_and_declared_scope(self):
        self.assertEqual(plugins.status()["tools"], [])
        with self.assertRaises(ValueError):
            plugins.call("plan_tasks", {"tasks": [{"title": "Example"}]})
        plugins.set_enabled(True)
        self.assertEqual(len(plugins.status()["tools"]), 1)
        result = plugins.call("plan_tasks", {"tasks": [{"title": "Review proposal", "due_at": "2030-05-01"}]})
        self.assertFalse(result["saved"])
        self.assertEqual(result["tasks"][0]["title"], "Review proposal")
        for name, args in [("write_file", {}), ("plan_tasks", {"tasks": [{"title": "Example", "path": "../secret"}]}), ("plan_tasks", {"tasks": [{"title": "Example"}]*13})]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                plugins.call(name, args)
        plugins.set_enabled(False)
        self.assertEqual(plugins.status()["tools"], [])
        with self.assertRaises(ValueError):
            plugins.call("plan_tasks", {"tasks": [{"title": "Example"}]})
        self.assertFalse((self.root / "projects.db").exists())

    def test_bad_manifests_never_partially_register(self):
        for field, value in [("version", "1.0"), ("entry_point", "os.system"), ("permissions", ["file.write"]), ("tools", []), ("unexpected", "execute")]:
            manifest = copy.deepcopy(plugins.PLANNER)
            manifest[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                plugins.register(manifest)
            self.assertFalse((self.root / "plugins.db").exists())
        missing = copy.deepcopy(plugins.PLANNER)
        del missing["entry_point"]
        with self.assertRaises(ValueError):
            plugins.register(missing)
        self.assertFalse((self.root / "plugins.db").exists())

    def test_manifest_change_revokes_enablement(self):
        plugins.set_enabled(True)
        connection = plugins._conn()
        with connection:
            connection.execute("UPDATE plugins SET manifest='{}'")
        connection.close()
        self.assertFalse(plugins.status()["enabled"])

    def test_plugin_failure_disables_and_recovers_without_jobs(self):
        plugins.set_enabled(True)
        with patch("app.plugins._plan", side_effect=RuntimeError("private-value")):
            with self.assertRaisesRegex(ValueError, "disabled") as raised:
                plugins.call("plan_tasks", {"tasks": [{"title": "Example"}]})
        self.assertNotIn("private-value", str(raised.exception))
        self.assertFalse(plugins.status()["enabled"])
        plugins.set_enabled(True)
        self.assertFalse(plugins.call("plan_tasks", {"tasks": [{"title": "Retry"}]})["saved"])

    def test_recipe_round_trip_shared_repair_semantics_and_no_private_export(self):
        (self.root / "private-chat.txt").write_text("seeded-secret-token private conversation", encoding="utf-8")
        exported = recipes.export_recipe()
        self.assertNotIn("seeded-secret", exported)
        self.assertNotIn(str(self.root), exported)
        result = recipes.import_recipe(exported)
        self.assertEqual(len(result["checks"]), 4)
        self.assertTrue(all(result["checks"].values()))
        self.assertTrue(recipes.status()["registered"])
        self.assertEqual((self.root / "private-chat.txt").read_text(), "seeded-secret-token private conversation")
        self.assertEqual(result["permissions"], ["synthetic.copy.read", "synthetic.copy.write"])

    def test_corrupt_import_preserves_registration_and_allows_recovery(self):
        valid = recipes.export_recipe()
        recipes.import_recipe(valid)
        for field, value in [("version", "2.0.0"), ("format_version", True), ("files", {"../../private.txt": "secret"}), ("checks", []), ("permissions", ["file.write"]), ("entry_point", "subprocess.run")]:
            package = copy.deepcopy(recipes.RECIPE)
            package[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                recipes.import_recipe(json.dumps(package))
            self.assertTrue(recipes.status()["registered"])
        for invalid in ("{", "[]", "x" * 17000, '{"id":"one","id":"two"}', "[" * 1200):
            with self.assertRaises(ValueError):
                recipes.import_recipe(invalid)
        self.assertTrue(all(recipes.import_recipe(valid)["checks"].values()))

    def test_failed_synthetic_check_does_not_register(self):
        with patch("app.recipes.preview_text", return_value="incorrect output"):
            with self.assertRaises(ValueError):
                recipes.import_recipe(recipes.export_recipe())
        self.assertFalse(recipes.status()["registered"])


class ExtensionBoundaryTests(unittest.IsolatedAsyncioTestCase):
    async def test_catalog_registers_disabled_provider_adapters(self):
        from app.connectors.registry import registry, setup_registry
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, {"LOCALMIND_DATA_DIR": folder}), \
                patch.object(registry, "_connectors", {}), patch("sys.platform", "darwin"):
            setup_registry()
            for name in ("whatsapp_web", "imessage"):
                self.assertIsInstance(registry.get(name), UnavailableConnector)
            rows = await registry.status_all()
            self.assertEqual({row["id"] for row in rows}, {"gmail", "whatsapp_web", "imessage"})
            for row in rows:
                self.assertTrue(row["evidence"])
                self.assertIn(row["location"], ("local", "cloud"))
                if row["id"] != "gmail":
                    self.assertFalse(row["available"])
                    self.assertEqual(row["status"], "disabled")

    async def test_disabled_connectors_never_reach_provider_or_network(self):
        for connector_id in ("whatsapp_web", "imessage"):
            connector = UnavailableConnector(connector_id, connector_id)
            self.assertEqual((await connector.status()).value, "disabled")
            self.assertEqual((await connector.connect())["type"], "error")
            self.assertIsNotNone((await connector.sync()).error)
            self.assertEqual(await connector.context_for_buddy("secret"), "")
            with self.assertRaises(PermissionError):
                await connector.action("send", {})

    async def test_auth_and_disabled_api_contract(self):
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, {"LOCALMIND_DATA_DIR": folder, "LOCALMIND_TOKEN": "fixture"}):
            app = FastAPI()
            app.middleware("http")(verify_bearer_token)
            app.include_router(router)
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
                self.assertEqual((await client.get("/extensions/status")).status_code, 401)
                client.headers["Authorization"] = "Bearer fixture"
                data = (await client.get("/extensions/status")).json()
                self.assertFalse(data["images"]["available"])
                self.assertFalse(data["planner"]["enabled"])
                self.assertEqual((await client.post("/extensions/planner-state", json={"enabled": "yes"})).status_code, 422)
                self.assertEqual((await client.post("/extensions/planner-call", json={"name": "plan_tasks", "arguments": {"tasks": []}})).status_code, 400)
                await client.post("/extensions/planner-state", json={"enabled": True})
                result = await client.post("/extensions/planner-call", json={"name": "plan_tasks", "arguments": {"tasks": [{"title": "seeded-private-token", "extra": "bad"}]}})
                self.assertEqual(result.status_code, 400)
                self.assertNotIn("seeded-private-token", result.text)
                self.assertEqual((await client.post("/extensions/images/generate", json={})).status_code, 404)
