"""Frozen profile replay remains bound to the actual installed model."""

import argparse
import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

import run_suite as runner
from check_suite import DEFAULT_SUITE


class GenerationProfileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.path = self.root / "profile.json"
        self.api = runner.profile_api()
        self.metadata = {
            "model": "fixture:8b", "digest": "a" * 64,
            "capabilities": ["completion", "thinking"], "context_limit": 32768,
            "thinking_values": [False, True],
        }
        self.profile = {
            **self.api.default_profile(self.metadata, "balanced"),
            "context_window": 8192, "response_tokens": 1024, "think": False,
        }
        self.path.write_text(json.dumps(self.profile), encoding="utf-8")
        self.args = argparse.Namespace(
            suite=DEFAULT_SUITE.with_name("context_cases.json"), split="development",
            cases=["CTX-001"], allow_held_out=False, profile="balanced",
            model="fixture:8b", generation_profile=self.path, timeout_seconds=30,
            out=self.root / "run",
        )
        self.environment = {"ollama": {
            "installed_models": {"status": "ok", "models": [
                {"name": "fixture:8b", "size": 5000, "digest": "a" * 64, "is_remote": False},
            ]},
            "active_models": {"status": "ok", "models": []},
            "selected_model_metadata": {"status": "ok", "is_remote": False},
        }}

    def test_invalid_profile_cannot_override_actual_capabilities_or_identity(self):
        for changes in (
            {"model": "other:8b"}, {"preset": "speed"}, {"digest": "b" * 64},
            {"think": "high"}, {"context_window": 65536}, {"response_tokens": 0},
        ):
            with self.subTest(changes=changes):
                self.path.write_text(json.dumps({**self.profile, **changes}), encoding="utf-8")
                with self.assertRaises(ValueError):
                    runner.load_generation_profile(
                        self.path, "fixture:8b", "balanced", self.metadata, self.api,
                    )

    def test_profile_file_has_a_bounded_size_and_requires_an_object(self):
        for body in (" " * 65537, "[]"):
            self.path.write_text(body, encoding="utf-8")
            with self.assertRaises(ValueError):
                runner.load_generation_profile(
                    self.path, "fixture:8b", "balanced", self.metadata, self.api,
                )

    def test_frozen_profile_is_recorded_and_passed_to_worker_without_saving_user_settings(self):
        response = {"status": "completed", "answer": "Synthetic"}
        with patch.object(runner.inventory, "capture", return_value=self.environment), \
                patch.object(self.api, "inspect_model", AsyncMock(return_value=self.metadata)), \
                patch.object(self.api, "_persist") as persist, \
                patch.object(runner, "execute_case", return_value=(response, False)) as execute:
            self.assertEqual(runner.run(self.args), 0)
            persist.assert_not_called()
        manifest = json.loads((self.args.out / "manifest.json").read_text())
        self.assertEqual(manifest["generation_profile"], self.profile)
        self.assertEqual(len(manifest["generation_profile_sha256"]), 64)
        self.assertEqual(execute.call_args.kwargs["generation_profile"], self.profile)

    def test_digest_drift_before_or_during_preflight_stops_before_inference(self):
        for change in ("saved_profile", "installation"):
            with self.subTest(change=change):
                self.args.out = self.root / change
                profile, metadata = copy.deepcopy(self.profile), copy.deepcopy(self.metadata)
                if change == "saved_profile":
                    profile["digest"] = "b" * 64
                else:
                    metadata["digest"] = "b" * 64
                self.path.write_text(json.dumps(profile), encoding="utf-8")
                with patch.object(runner.inventory, "capture", return_value=self.environment), \
                        patch.object(self.api, "inspect_model", AsyncMock(return_value=metadata)), \
                        patch.object(runner, "execute_case") as execute:
                    self.assertEqual(runner.run(self.args), 1)
                    execute.assert_not_called()
                manifest = json.loads((self.args.out / "manifest.json").read_text())
                self.assertEqual(manifest["state"], "run_error")


if __name__ == "__main__":
    unittest.main()
