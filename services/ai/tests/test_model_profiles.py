"""Profile validation, installation identity and rollback without real models."""
import os
import json
import tempfile
import unittest
from contextlib import closing
from unittest.mock import AsyncMock, patch

import httpx

from app import model_profiles as profiles


def metadata(**changes):
    return {"model": "fixture:4b", "digest": "a" * 64,
            "capabilities": ["completion", "thinking"], "context_limit": 32768,
            "thinking_values": [False, True], **changes}


class ProfileValidationTests(unittest.TestCase):
    def test_limits_and_capabilities_are_enforced(self):
        info = metadata()
        default = profiles.default_profile(info, "balanced")
        bad = [
            {"context_window": 65536}, {"context_window": True}, {"response_tokens": 16000},
            {"response_tokens": 0}, {"temperature": float("nan")}, {"temperature": True},
            {"top_p": 0}, {"top_k": 101}, {"think": 1}, {"think": "high"},
            {"digest": "b" * 64}, {"preset": "unknown"}, {"schema_version": 9},
        ]
        for changes in bad:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                profiles.validate_profile({**default, **changes}, info)
        for value in (False, True, None):
            self.assertEqual(profiles.validate_profile({**default, "think": value}, info)["think"], value)
        with self.assertRaises(ValueError):
            profiles.validate_profile({**default, "think": False}, metadata(thinking_values=[True]))

    def test_named_thinking_levels_do_not_accept_booleans(self):
        info = metadata(thinking_values=["low", "medium", "high"])
        profile = profiles.default_profile(info, "boost")
        self.assertEqual(profiles.validate_profile({**profile, "think": "low"}, info)["think"], "low")
        with self.assertRaises(ValueError):
            profiles.validate_profile({**profile, "think": True}, info)

    def test_generation_options_include_explicit_response_reserve(self):
        value = profiles.default_profile(metadata(), "balanced")
        self.assertEqual(profiles.generation_options(value),
                         {"num_ctx": 16384, "num_predict": 2048, "temperature": .2,
                          "top_p": .9, "top_k": 40})

    def test_saved_profile_must_leave_room_for_policy_and_a_short_request(self):
        info = metadata()
        profile = profiles.default_profile(info, "balanced")
        # These fit the model and the old 1024-token input minimum, but not
        # the service's conservative estimate of its mandatory policy alone.
        for context, reserve in ((2048, 512), (4096, 2048), (8192, 6144)):
            with self.subTest(context=context, reserve=reserve), self.assertRaises(ValueError):
                profiles.validate_profile(
                    {**profile, "context_window": context, "response_tokens": reserve}, info,
                )


class ProfilePersistenceTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.env = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": self.temporary.name})
        self.env.start()
        self.inspect = patch.object(profiles, "inspect_model", AsyncMock(return_value=metadata()))
        self.mock_inspect = self.inspect.start()

    def tearDown(self):
        self.inspect.stop()
        self.env.stop()
        self.temporary.cleanup()

    async def test_save_reload_restore_and_installation_separation(self):
        initial = await profiles.resolve_profile("fixture:4b", "balanced")
        updated = await profiles.update_profile("fixture:4b", "balanced",
                                               {"think": False, "context_window": 8192,
                                                "temperature": 0.65, "response_tokens": 1024,
                                                "top_p": 0.85, "top_k": 25})
        self.assertIs(updated["think"], False)
        self.assertEqual(profiles.generation_options(updated),
                         {"num_ctx": 8192, "num_predict": 1024, "temperature": 0.65,
                          "top_p": 0.85, "top_k": 25})
        self.assertEqual(await profiles.resolve_profile("fixture:4b", "balanced"), updated)
        restored = await profiles.restore_profile("fixture:4b", "balanced")
        self.assertEqual(restored["context_window"], initial["context_window"])
        self.assertIsNone(restored["think"])
        with tempfile.TemporaryDirectory() as other, patch.dict(os.environ, {"LOCALMIND_DATA_DIR": other}):
            self.assertIsNone(profiles.read_profile("fixture:4b", "balanced"))
        self.assertIsNotNone(profiles.read_profile("fixture:4b", "balanced"))

    def history(self):
        with closing(profiles._database()) as connection:
            rows = connection.execute("SELECT body FROM profile_history ORDER BY id").fetchall()
        return [json.loads(row[0]) for row in rows]

    async def test_same_digest_capability_change_recovers_and_preserves_old_settings(self):
        previous = await profiles.update_profile("fixture:4b", "balanced", {"think": False})
        self.mock_inspect.return_value = metadata(thinking_values=["low", "high"])
        recovered = await profiles.resolve_profile("fixture:4b", "balanced")
        self.assertIsNone(recovered["think"])
        self.assertEqual(recovered["thinking_values"], ["low", "high"])
        self.assertIn("Defaults have been restored", recovered["settings_notice"])
        self.assertIn(previous, self.history())
        self.assertEqual(await profiles.resolve_profile("fixture:4b", "balanced"), recovered)

        # A failed correction must neither overwrite the recovered safe values
        # nor erase the old profile kept for inspection.
        with self.assertRaisesRegex(ValueError, "thinking setting"):
            await profiles.update_profile("fixture:4b", "balanced", {"think": False})
        self.assertEqual(profiles.read_profile("fixture:4b", "balanced"), recovered)
        updated = await profiles.update_profile("fixture:4b", "balanced", {"think": "low"})
        self.assertEqual(updated["think"], "low")
        self.assertNotIn("settings_notice", updated)
        self.assertIn(previous, self.history())
        restored = await profiles.restore_profile("fixture:4b", "balanced")
        self.assertIsNone(restored["think"])
        self.assertNotIn("settings_notice", restored)

    async def test_update_recovers_stale_profile_before_applying_valid_correction(self):
        previous = await profiles.update_profile("fixture:4b", "balanced", {"think": False})
        self.mock_inspect.return_value = metadata(thinking_values=[True])
        updated = await profiles.update_profile("fixture:4b", "balanced",
                                               {"think": None, "temperature": 0.4})
        self.assertIsNone(updated["think"])
        self.assertEqual(updated["temperature"], 0.4)
        self.assertNotIn("settings_notice", updated)
        self.assertIn(previous, self.history())
        self.assertEqual(profiles.read_profile("fixture:4b", "balanced"), updated)

    async def test_raised_policy_minimum_recovers_without_accepting_unsafe_update(self):
        with patch.object(profiles, "minimum_input_budget", return_value=1024):
            previous = await profiles.update_profile("fixture:4b", "balanced",
                                                    {"context_window": 4096, "response_tokens": 2048})
        with patch.object(profiles, "minimum_input_budget", return_value=3072):
            # Update is also a recovery entry point, even if proposed values
            # cannot satisfy the new policy minimum.
            with self.assertRaisesRegex(ValueError, "leave 3072"):
                await profiles.update_profile("fixture:4b", "balanced",
                                              {"context_window": 4096, "response_tokens": 2048})
            recovered = await profiles.resolve_profile("fixture:4b", "balanced")
            self.assertEqual(recovered["context_window"], 16384)
            self.assertIn("settings_notice", recovered)
            self.assertIn(previous, self.history())
            self.assertEqual(profiles.read_profile("fixture:4b", "balanced"), recovered)

    async def test_profile_routes_save_real_values_read_back_and_reject_metadata_changes(self):
        from fastapi import FastAPI
        from app.api.routes.models import router

        app = FastAPI()
        app.include_router(router)
        identity = {"model": "fixture:4b", "preset": "balanced"}
        changes = {"context_window": 8192, "response_tokens": 1024,
                   "think": False, "temperature": 0.7}
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),
                                     base_url="http://testserver") as client:
            saved = await client.post("/models/profile", json={**identity, "changes": changes})
            self.assertEqual(saved.status_code, 200)
            for key, value in changes.items():
                self.assertEqual(saved.json()[key], value)
            loaded = await client.post("/models/profile", json=identity)
            self.assertEqual(loaded.json(), saved.json())
            for forged in ({"digest": "b" * 64}, {"context_limit": 999999},
                           {"capabilities": ["completion"]}, {"thinking_values": ["high"]},
                           {"settings_notice": "Forged recovery"}):
                rejected = await client.post("/models/profile", json={**identity, "changes": forged})
                self.assertEqual(rejected.status_code, 422)
            self.assertEqual((await client.post("/models/profile", json=identity)).json(), saved.json())

    async def test_new_digest_revalidates_defaults_and_rejects_old_rollback(self):
        await profiles.update_profile("fixture:4b", "balanced", {"think": False})
        self.mock_inspect.return_value = metadata(digest="b" * 64, thinking_values=["low"])
        current = await profiles.resolve_profile("fixture:4b", "balanced")
        self.assertEqual(current["digest"], "b" * 64)
        self.assertIsNone(current["think"])
        with self.assertRaisesRegex(ValueError, "installed model changed"):
            await profiles.restore_profile("fixture:4b", "balanced")

    async def test_invalid_update_cannot_change_stored_profile(self):
        initial = await profiles.resolve_profile("fixture:4b", "balanced")
        for changes in ({"digest": "b" * 64}, {"context_window": 999999}, {}, {"think": "high"}):
            with self.assertRaises(ValueError):
                await profiles.update_profile("fixture:4b", "balanced", changes)
            self.assertEqual(profiles.read_profile("fixture:4b", "balanced"), initial)
        with self.assertRaisesRegex(ValueError, "No previous"):
            await profiles.restore_profile("fixture:4b", "balanced")


class ProfileInspectionTests(unittest.IsolatedAsyncioTestCase):
    async def inspect(self, *, shown=None, tag=None):
        real_client = httpx.AsyncClient
        def respond(request):
            value = {"models": [tag or {"name": "fixture:4b", "digest": "a" * 64}]}
            if request.url.path == "/api/show":
                value = shown or {"capabilities": ["completion", "thinking"],
                                  "model_info": {"qwen.context_length": 32768},
                                  "thinking": {"values": [False, True]}}
            return httpx.Response(200, json=value)
        with patch.object(profiles.httpx, "AsyncClient",
                          side_effect=lambda **kw: real_client(transport=httpx.MockTransport(respond), **kw)):
            return await profiles.inspect_model("fixture:4b")

    async def test_metadata_drives_controls_without_guessing(self):
        self.assertEqual((await self.inspect())["thinking_values"], [False, True])
        no_controls = await self.inspect(shown={"capabilities": ["completion", "thinking"],
                                               "model_info": {"qwen.context_length": 262144}})
        self.assertEqual(no_controls["thinking_values"], [])
        self.assertEqual(no_controls["context_limit"], 65536)

    async def test_remote_embeddings_and_missing_capacity_are_rejected(self):
        for shown in ({"capabilities": ["embedding"]},
                      {"capabilities": ["completion"]},
                      {"remote_host": "https://example.invalid", "capabilities": ["completion"]}):
            with self.subTest(shown=shown), self.assertRaises(ValueError):
                await self.inspect(shown=shown)
        with self.assertRaises(ValueError):
            await self.inspect(tag={"name": "fixture:4b", "digest": "a" * 64, "remote_model": "cloud"})


if __name__ == "__main__":
    unittest.main()
