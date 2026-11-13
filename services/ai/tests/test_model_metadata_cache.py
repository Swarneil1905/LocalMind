"""Metadata reuse must not conceal removal, replacement or invalid controls."""
import unittest
import os
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

import httpx

from app import model_profiles as profiles


class MetadataCacheTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        profiles.clear_metadata_cache()
        self.addCleanup(profiles.clear_metadata_cache)
        self.now = 100.0
        self.tag = {"name": "fixture:4b", "digest": "a" * 64}
        self.shown = {"capabilities": ["completion", "thinking"],
                      "model_info": {"fixture.context_length": 32768},
                      "thinking": {"values": [False, True]}}
        self.calls = []
        self.fail = None
        real_client = httpx.AsyncClient

        def respond(request):
            path = request.url.path
            self.calls.append(path)
            if path == self.fail:
                return httpx.Response(503)
            return httpx.Response(200, json=(
                {"models": [self.tag] if self.tag else []} if path == "/api/tags" else self.shown
            ))

        client = patch.object(profiles.httpx, "AsyncClient", side_effect=lambda **kw:
                              real_client(transport=httpx.MockTransport(respond), **kw))
        client.start()
        self.addCleanup(client.stop)
        clock = patch.object(profiles, "time", SimpleNamespace(monotonic=lambda: self.now))
        clock.start()
        self.addCleanup(clock.stop)

    async def inspect(self, **kwargs):
        return await profiles.inspect_model("fixture:4b", **kwargs)

    async def test_warm_metadata_reuses_show_but_checks_installed_digest_every_time(self):
        first = await self.inspect()
        second = await self.inspect()
        self.assertEqual(first, second)
        self.assertEqual(self.calls, ["/api/tags", "/api/show", "/api/tags"])
        first["capabilities"].append("vision")
        first["thinking_values"].clear()
        self.assertNotIn("vision", (await self.inspect())["capabilities"])
        self.assertEqual((await self.inspect())["thinking_values"], [False, True])

    async def test_expiry_refreshes_capabilities_even_for_the_same_digest(self):
        await self.inspect()
        self.shown["thinking"] = {"values": ["low", "high"]}
        self.now += profiles.METADATA_TTL_SECONDS
        self.assertEqual((await self.inspect())["thinking_values"], ["low", "high"])
        self.assertEqual(self.calls.count("/api/show"), 2)

    async def test_replacement_invalidates_immediately(self):
        await self.inspect()
        self.tag["digest"] = "b" * 64
        self.shown["model_info"] = {"fixture.context_length": 8192}
        value = await self.inspect()
        self.assertEqual(value["digest"], "b" * 64)
        self.assertEqual(value["context_limit"], 8192)
        self.assertEqual(self.calls.count("/api/show"), 2)

    async def test_removed_reinstalled_or_remote_model_cannot_reuse_old_metadata(self):
        await self.inspect()
        saved_tag = self.tag
        self.tag = None
        with self.assertRaisesRegex(ValueError, "not installed"):
            await self.inspect()
        self.tag = saved_tag
        await self.inspect()
        self.assertEqual(self.calls.count("/api/show"), 2)
        self.tag["remote_host"] = "https://example.invalid"
        with self.assertRaisesRegex(ValueError, "local model"):
            await self.inspect()
        self.assertNotIn("fixture:4b", profiles._metadata_cache)

    async def test_network_failures_never_fall_back_to_cached_metadata(self):
        await self.inspect()
        self.fail = "/api/tags"
        with self.assertRaises(httpx.HTTPStatusError):
            await self.inspect()
        self.assertNotIn("fixture:4b", profiles._metadata_cache)
        self.fail = None
        await self.inspect()
        self.fail = "/api/show"
        with self.assertRaises(httpx.HTTPStatusError):
            await self.inspect(force_refresh=True)
        self.assertNotIn("fixture:4b", profiles._metadata_cache)

    async def test_invalid_metadata_is_not_cached_and_valid_retry_works(self):
        self.shown["model_info"] = {}
        with self.assertRaisesRegex(ValueError, "context capacity"):
            await self.inspect()
        self.assertFalse(profiles._metadata_cache)
        self.shown["model_info"] = {"fixture.context_length": 4096}
        self.assertEqual((await self.inspect())["context_limit"], 4096)

    async def test_explicit_refresh_checks_changed_thinking_controls(self):
        profile = profiles.default_profile(await self.inspect(), "balanced")
        self.shown["thinking"] = {"values": ["low", "high"]}
        fresh = await self.inspect(force_refresh=True)
        with self.assertRaisesRegex(ValueError, "thinking setting"):
            profiles.validate_profile({**profile, "think": False}, fresh)

    async def test_saving_and_restoring_settings_bypass_warm_metadata(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(
            os.environ, {"LOCALMIND_DATA_DIR": directory},
        ):
            await profiles.update_profile("fixture:4b", "balanced", {"think": False})
            await profiles.update_profile("fixture:4b", "balanced", {"think": True})
            self.shown["thinking"] = {"values": ["low", "high"]}
            with self.assertRaisesRegex(ValueError, "thinking setting"):
                await profiles.restore_profile("fixture:4b", "balanced")
            with self.assertRaisesRegex(ValueError, "thinking setting"):
                await profiles.update_profile("fixture:4b", "balanced", {"think": False})
            self.assertIsNone(profiles.read_profile("fixture:4b", "balanced")["think"])

    async def test_cache_has_a_fixed_size_and_evicts_least_recently_used(self):
        with patch.object(profiles, "METADATA_CACHE_LIMIT", 2):
            for name in ("one", "two", "one", "three"):
                self.tag["name"] = name
                await profiles.inspect_model(name)
        self.assertEqual(list(profiles._metadata_cache), ["one", "three"])

    async def test_context_policy_budget_is_recomputed_on_a_cache_hit(self):
        await self.inspect()
        with patch.object(profiles, "minimum_input_budget", return_value=7777):
            self.assertEqual((await self.inspect())["minimum_input_budget"], 7777)


if __name__ == "__main__":
    unittest.main()
