"""Execution boundaries, split guards and durable results without a running model."""

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch, MagicMock

import run_suite as runner
from check_suite import DEFAULT_SUITE, load_suite


class RunTests(unittest.TestCase):
    def setUp(self):
        self.suite, _ = load_suite()
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.args = argparse.Namespace(suite=DEFAULT_SUITE, split="development", cases=["LM-007"],
                                       allow_held_out=False, profile="balanced", model="qwen3:8b",
                                       timeout_seconds=30, out=self.root / "run")
        self.environment = {"ollama": {
            "installed_models": {"status": "ok", "models": [{"name": "qwen3:8b", "size": 5000, "digest": "abc", "is_remote": False}]},
            "active_models": {"status": "ok", "models": []},
            "selected_model_metadata": {"status": "ok", "is_remote": False, "details": {"quantization_level": "Q4_K_M"}},
        }}

    def test_default_split_and_explicit_holdout_guard(self):
        cases = runner.select_cases(self.suite, "development", None)
        self.assertEqual([c["id"] for c in cases], [f"LM-{i:03d}" for i in range(1, 21)])
        for split, ids, allow in (("held_out", None, False), ("development", ["LM-021"], True),
                                  ("held_out", ["LM-001"], True), ("development", ["LM-007", "LM-007"], False)):
            with self.subTest(split=split, ids=ids), self.assertRaises(ValueError):
                runner.select_cases(self.suite, split, ids, allow)
        held = runner.select_cases(self.suite, "held_out", None, True)
        self.assertEqual(len(held), 10)

    def test_order_does_not_depend_on_cli_ids(self):
        cases = runner.select_cases(self.suite, "development", ["LM-016", "LM-006", "LM-007"])
        self.assertEqual([c["id"] for c in cases], ["LM-006", "LM-007", "LM-016"])

    def test_invalid_split_or_timeout_never_creates_run_or_contacts_model(self):
        for changes in ({"cases": ["LM-021"]}, {"timeout_seconds": float("nan")},
                        {"timeout_seconds": 0}, {"timeout_seconds": float("inf")}):
            args = argparse.Namespace(**{**vars(self.args), **changes})
            with self.subTest(changes=changes), patch.object(runner.inventory, "capture") as capture:
                with self.assertRaises(ValueError):
                    runner.run(args)
                capture.assert_not_called()
                self.assertFalse(args.out.exists())

    def test_existing_run_is_never_overwritten(self):
        self.args.out.mkdir()
        (self.args.out / "manifest.json").write_text("original", encoding="utf-8")
        with patch.object(runner.inventory, "capture") as capture:
            with self.assertRaises(FileExistsError):
                runner.run(self.args)
            capture.assert_not_called()
        self.assertEqual((self.args.out / "manifest.json").read_text(), "original")

    def test_missing_model_is_recorded_and_never_downloaded_or_replayed(self):
        self.environment["ollama"]["installed_models"]["models"] = []
        with patch.object(runner.inventory, "capture", return_value=self.environment), \
                patch.object(runner, "execute_case") as execute:
            self.assertEqual(runner.run(self.args), 1)
            execute.assert_not_called()
        manifest = json.loads((self.args.out / "manifest.json").read_text())
        self.assertEqual(manifest["state"], "run_error")
        self.assertIn("installed model", manifest["error"])

    def test_renamed_cloud_alias_is_rejected_before_inference(self):
        for field in ("installed_models", "selected_model_metadata"):
            with self.subTest(field=field):
                self.args.out = self.root / field
                remote = self.environment["ollama"][field]
                remote = remote["models"][0] if field == "installed_models" else remote
                remote["is_remote"] = True
                with patch.object(runner.inventory, "capture", return_value=self.environment), \
                        patch.object(runner, "execute_case") as execute:
                    self.assertEqual(runner.run(self.args), 1)
                    execute.assert_not_called()
                manifest = json.loads((self.args.out / "manifest.json").read_text())
                self.assertIn("Remote/cloud", manifest["error"])
                remote["is_remote"] = False

    def test_unknown_remote_status_is_not_treated_as_local(self):
        del self.environment["ollama"]["selected_model_metadata"]["is_remote"]
        with self.assertRaisesRegex(ValueError, "whether.*local"):
            runner.model_snapshot(self.environment["ollama"], "qwen3:8b")

    def test_full_results_are_saved_but_desktop_never_replayed(self):
        self.args.cases = ["LM-017", "LM-007"]
        response = {"status": "completed", "answer": "Full\nanswer", "timings": {"total_seconds": 2},
                    "effective_request": {"model": "qwen3:8b", "options": {"num_ctx": 16384}}}
        with patch.object(runner.inventory, "capture", return_value=self.environment), \
                patch.object(runner, "execute_case", return_value=(response, False)) as execute:
            self.assertEqual(runner.run(self.args), 0)
            self.assertEqual(execute.call_count, 1)
        manifest = json.loads((self.args.out / "manifest.json").read_text())
        self.assertEqual(manifest["case_ids"], ["LM-007", "LM-017"])
        self.assertEqual(manifest["state"], "completed")
        self.assertTrue(manifest["source_sha256"])
        result = json.loads((self.args.out / "results/LM-007.json").read_text())
        self.assertEqual(result["answer"], "Full\nanswer")
        self.assertEqual(result["profile"], "balanced")
        desktop = json.loads((self.args.out / "results/LM-017.json").read_text())
        self.assertEqual(desktop["status"], "not_evaluated")
        selected = json.loads((self.args.out / "cases.json").read_text())
        self.assertEqual(len(selected["cases"]), 2)
        self.assertNotIn("LM-021", (self.args.out / "cases.json").read_text())

    def test_interruption_keeps_partial_case_and_unrun_cases_visible(self):
        self.args.cases = ["LM-007", "LM-016"]
        with patch.object(runner.inventory, "capture", return_value=self.environment), \
                patch.object(runner, "execute_case", return_value=({"status": "run_error", "answer": "partial", "truncated": True}, True)) as execute:
            self.assertEqual(runner.run(self.args), 130)
            self.assertEqual(execute.call_count, 1)
        self.assertEqual(json.loads((self.args.out / "manifest.json").read_text())["state"], "interrupted")
        self.assertEqual(json.loads((self.args.out / "results/LM-007.json").read_text())["answer"], "partial")
        self.assertEqual(json.loads((self.args.out / "results/LM-016.json").read_text())["status"], "not_evaluated")

    def test_validated_context_suite_identity_survives_evidence_export(self):
        self.args.suite = DEFAULT_SUITE.with_name("context_cases.json")
        self.args.cases = ["CTX-003"]
        response = {"status": "completed", "answer": "Synthetic context answer"}
        with patch.object(runner.inventory, "capture", return_value=self.environment), \
                patch.object(runner, "execute_case", return_value=(response, False)):
            self.assertEqual(runner.run(self.args), 0)
        for filename in ("manifest.json", "cases.json"):
            exported = json.loads((self.args.out / filename).read_text())
            self.assertEqual(exported["suite_kind"], "context_development")
        manifest = json.loads((self.args.out / "manifest.json").read_text())
        self.assertEqual(manifest["case_ids"], ["CTX-003"])
        self.assertEqual(manifest["split"], "development")

    def test_hard_deadline_kills_owned_worker_and_recovers_checkpoint(self):
        case = self.suite["cases"][6]
        process = MagicMock()
        process.wait.side_effect = [subprocess.TimeoutExpired("worker", 2), 0]

        def spawn(command, **kwargs):
            request = json.loads(Path(command[command.index("--input") + 1]).read_text())
            self.assertTrue(Path(request["profile_dir"]).is_absolute())
            self.assertFalse(Path(request["profile_dir"]).exists())
            self.assertNotIn("LOCALMIND_TOKEN", kwargs["env"])
            self.assertEqual(kwargs["stdin"], subprocess.DEVNULL)
            self.assertEqual(kwargs["env"]["LOCALMIND_DATA_DIR"], request["profile_dir"])
            output = Path(command[command.index("--output") + 1])
            runner.write_json(output, {"answer": "partial reply", "status": "run_error"})
            return process

        with patch.object(runner.subprocess, "Popen", side_effect=spawn):
            result, interrupted = runner.execute_case(case, "qwen3:8b", 1, "2026-10-15T12:00:00Z", self.root)
        process.kill.assert_called_once_with()
        self.assertFalse(interrupted)
        self.assertEqual(result["answer"], "partial reply")
        self.assertEqual(result["status"], "run_error")
        self.assertTrue(result["truncated"])
        self.assertEqual(list(self.root.iterdir()), [])

    def test_model_selection_uses_ui_mapping_and_records_override_separately(self):
        with patch.object(runner.inventory, "source_defaults", return_value={"model_mapping": {"status": "ok", "values": {"speed": "small:1b"}}}):
            self.assertEqual(runner.select_model("speed", None), "small:1b")
            self.assertEqual(runner.select_model("speed", "other:2b"), "other:2b")
            with self.assertRaises(ValueError):
                runner.select_model("balanced", None)

    def test_worker_error_exit_preserves_specific_reason_and_partial_answer(self):
        process = MagicMock()
        process.wait.return_value = 1

        def spawn(command, **kwargs):
            output = Path(command[command.index("--output") + 1])
            runner.write_json(output, {"answer": "partial", "status": "run_error",
                                      "error": "Replay exceeded the case timeout", "truncated": True,
                                      "trace": {"checkpoint_final": True}})
            return process

        with patch.object(runner.subprocess, "Popen", side_effect=spawn):
            result, _ = runner.execute_case(self.suite["cases"][6], "qwen3:8b", 1,
                                             "2026-10-15T12:00:00Z", self.root)
        self.assertEqual(result["error"], "Replay exceeded the case timeout")
        self.assertEqual(result["answer"], "partial")
        process.kill.assert_not_called()


if __name__ == "__main__":
    unittest.main()
