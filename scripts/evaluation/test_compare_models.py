"""Benchmark controls and failure reporting with fake inference and local metadata."""

import copy
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, MagicMock, patch

import compare_models as compare


def metadata(model="qwen3:4b", thinking=None):
    return {"model": model, "digest": "a" * 64, "capabilities": ["completion", "thinking"],
            "context_limit": 32768, "thinking_values": [False, True] if thinking is None else thinking}


def fake_api():
    def validate(profile, observed):
        if profile["context_window"] > observed["context_limit"]:
            raise ValueError("Insufficient context")
        return profile
    return SimpleNamespace(
        inspect_model=AsyncMock(side_effect=lambda model: metadata(model)),
        default_profile=lambda observed, preset: {
            **observed, "schema_version": 1, "preset": preset, "saved_at": "fixture",
        },
        validate_profile=validate,
    )


def completed():
    return {
        "status": "completed", "answer": "Fixture answer", "error": None, "truncated": False,
        "timings": {"first_content_seconds": 0.25, "total_seconds": 0.5},
        "trace": {"model_called": True, "ollama_metrics": {"load_duration": 100000000}},
        "effective_request": {"messages": [{"role": "user", "content": "Fixture"}]},
    }


class ModelComparisonTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.out = Path(self.temporary.name) / "comparison"
        self.api = fake_api()
        self.active = {
            "status": "ok", "models": [{"model": "qwen3:4b", "digest": "a" * 64,
                                      "context_length": 8192, "size": 500, "size_vram": 400}],
        }
        self.environment = {"ollama": {
            "installed_models": {"status": "ok", "models": [
                {"name": "qwen3:4b", "digest": "a" * 64, "size": 500, "is_remote": False}]},
            "active_models": self.active,
            "selected_model_metadata": {"status": "ok", "is_remote": False},
        }}

    def patches(self):
        patches = [
            patch.object(compare, "profile_api", return_value=self.api),
            patch.object(compare, "comparison_sources", return_value={"service": "fixed"}),
            patch.object(compare.inventory, "capture", return_value=self.environment),
            patch.object(compare, "process_snapshot", return_value=self.active),
            patch.object(compare, "confirm_unloaded", return_value={"status": "ok", "models": []}),
        ]
        for context in patches:
            context.start()
            self.addCleanup(context.stop)

    def test_matched_limits_and_type_sensitive_thinking_support(self):
        for values, expected in (([False, True], False), ([True], None), (["low"], None),
                                 ([0, True], None), ([], None)):
            with self.subTest(values=values):
                profile = compare.matched_profile(metadata(thinking=values), self.api)
                self.assertIs(profile["think"], expected)
                self.assertEqual(
                    {key: profile[key] for key in ("context_window", "response_tokens", "temperature", "top_p", "top_k")},
                    {"context_window": 8192, "response_tokens": 1024, "temperature": 0.2, "top_p": 0.9, "top_k": 40},
                )
        with self.assertRaisesRegex(ValueError, "Insufficient context"):
            compare.matched_profile({**metadata(), "context_limit": 4096}, self.api)

    def test_unload_targets_only_candidate_and_never_sends_prompt_or_uses_proxy(self):
        opener = MagicMock()
        opener.open.return_value.__enter__.return_value.read.return_value = b'{"done":true}'
        with patch.object(compare, "build_opener", return_value=opener) as build:
            compare.unload_model("qwen3:4b")
        request = opener.open.call_args.args[0]
        self.assertEqual(request.full_url, "http://127.0.0.1:11434/api/generate")
        self.assertEqual(json.loads(request.data), {"model": "qwen3:4b", "keep_alive": 0, "stream": False})
        self.assertEqual(build.call_args.args[0].proxies, {})
        with self.assertRaises(ValueError):
            compare.unload_model("someone-elses-model")

    def test_cold_requires_verified_unloaded_state_and_warm_matching_context(self):
        with patch.object(compare, "unload_model") as unload, \
                patch.object(compare, "process_snapshot", return_value={"status": "unavailable"}):
            with self.assertRaisesRegex(ValueError, "Cannot verify cold"):
                compare.confirm_unloaded(metadata())
            unload.assert_called_once_with("qwen3:4b")
        self.assertTrue(compare.selected_loaded(self.active, metadata(), 8192))
        self.assertFalse(compare.selected_loaded(self.active, metadata(), 16384))
        self.assertFalse(compare.selected_loaded({"status": "unavailable"}, metadata()))

    def test_full_condition_runs_same_cases_and_profiles_without_changing_defaults(self):
        self.patches()
        with patch.object(compare.runner, "execute_case", side_effect=lambda *a, **k: (completed(), False)) as execute:
            self.assertEqual(compare.run(self.out, models=("qwen3:4b",)), 0)
        self.assertEqual(execute.call_count, 6 + len(compare.CASE_IDS))
        quality_ids = [call.args[0]["id"] for call in execute.call_args_list[6:]]
        self.assertEqual(quality_ids, list(compare.CASE_IDS))
        for call in execute.call_args_list:
            self.assertEqual(call.kwargs["generation_profile"]["context_window"], 8192)
            self.assertIs(call.kwargs["generation_profile"]["think"], False)
        manifest = json.loads((self.out / "manifest.json").read_bytes())
        self.assertFalse(manifest["default_changed"])
        self.assertEqual(manifest["human_review"], "pending")
        rollback = json.loads((self.out / "rollback.json").read_bytes())
        self.assertEqual(rollback["model"], "qwen3:8b")
        self.assertEqual(rollback["context_window"], 16384)
        self.assertIsNone(rollback["think"])
        summary = json.loads((self.out / "summary.json").read_bytes())
        self.assertEqual(summary["conditions"]["qwen3:4b"]["warm"]["usable_measurements"], 3)
        self.assertEqual(summary["conditions"]["qwen3:4b"]["quality_judgment"], "not_human_reviewed")

    def test_interruption_preserves_all_unrun_denominators(self):
        self.patches()
        with patch.object(compare.runner, "execute_case", return_value=(
            compare.probe_error("Interrupted"), True,
        )):
            self.assertEqual(compare.run(self.out, models=("qwen3:4b",)), 130)
        summary = json.loads((self.out / "summary.json").read_bytes())
        condition = summary["conditions"]["qwen3:4b"]
        self.assertEqual(summary["state"], "interrupted")
        self.assertEqual(condition["cold"]["execution"], {"run_error": 1, "not_evaluated": 2})
        self.assertEqual(condition["warm"]["execution"], {"not_evaluated": 3})
        self.assertEqual(condition["quality_execution"], {"not_evaluated": 7})

    def test_source_drift_preserves_answer_but_stops_remaining_trials(self):
        self.patches()
        with patch.object(compare, "comparison_sources", side_effect=[
            {"service": "fixed"}, {"service": "fixed"}, {"service": "fixed"},
            {"service": "changed"}, {"service": "changed"}, {"service": "changed"},
        ]), patch.object(compare.runner, "execute_case", return_value=(completed(), False)) as execute:
            self.assertEqual(compare.run(self.out, models=("qwen3:4b",)), 1)
        self.assertEqual(execute.call_count, 1)
        result = json.loads((self.out / "qwen3-4b/probes/cold-1.json").read_bytes())
        self.assertEqual(result["answer"], "Fixture answer")
        self.assertFalse(result["source_unchanged"])
        summary = json.loads((self.out / "summary.json").read_bytes())
        self.assertEqual(summary["conditions"]["qwen3:4b"]["cold"]["usable_measurements"], 0)

    def test_failed_and_truncated_measurements_do_not_improve_latency_average(self):
        success = {**completed(), "source_unchanged": True}
        failed = {**completed(), "status": "run_error", "source_unchanged": True}
        truncated = {**completed(), "truncated": True, "source_unchanged": True}
        missing = {**completed(), "source_unchanged": True, "timings": {}, "trace": {}}
        summary = compare.metric_summary([success, failed, truncated, missing])
        self.assertEqual(summary["requested"], 4)
        self.assertEqual(summary["execution"], {"completed": 3, "run_error": 1})
        self.assertEqual(summary["metrics"]["first_content_seconds"]["n"], 1)
        self.assertEqual(summary["metrics"]["ollama_load_seconds"]["median"], 0.1)

    def test_changed_model_digest_aborts_before_inference(self):
        self.patches()
        self.environment = copy.deepcopy(self.environment)
        self.environment["ollama"]["installed_models"]["models"][0]["digest"] = "b" * 64
        with patch.object(compare.inventory, "capture", return_value=self.environment), \
                patch.object(compare.runner, "execute_case") as execute:
            self.assertEqual(compare.run(self.out, models=("qwen3:4b",)), 1)
        execute.assert_not_called()
        manifest = json.loads((self.out / "manifest.json").read_bytes())
        self.assertIn("digest changed", manifest["error"])

    def test_existing_outputs_invalid_timeouts_and_models_never_start(self):
        self.out.mkdir()
        sentinel = self.out / "sentinel"
        sentinel.write_text("keep", encoding="utf-8")
        with patch.object(compare, "profile_api") as api:
            with self.assertRaises(FileExistsError):
                compare.run(self.out)
            with self.assertRaises(ValueError):
                compare.run(self.out, timeout=float("nan"))
            with self.assertRaises(ValueError):
                compare.run(self.out, models=("remote:cloud",))
            api.assert_not_called()
        self.assertEqual(sentinel.read_text(encoding="utf-8"), "keep")


if __name__ == "__main__":
    unittest.main()
