import argparse
from contextlib import ExitStack
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import AsyncMock, MagicMock, patch

import replay_pool


class PoolReplayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.args = argparse.Namespace(pool=self.root / "pool", out=self.root / "run",
                                       model="local:4b", count=2, timeout_seconds=30,
                                       generation_profile=None)

    def test_invalid_limits_never_query_model(self):
        for changes in ({"count": 0}, {"count": 101}, {"timeout_seconds": float("nan")},
                        {"model": "https://example.invalid"}):
            with self.subTest(changes=changes), patch.object(replay_pool, "check_pool") as check:
                with self.assertRaises(ValueError):
                    replay_pool.run(argparse.Namespace(**{**vars(self.args), **changes}))
                check.assert_not_called()
                self.assertFalse(self.args.out.exists())

    def test_verified_development_sample_runs_unscored_without_reference_answers(self):
        cases = [{"id": f"CP-{index:064x}", "split": "development", "messages": [
            {"role": "user", "content": f"Question {index}"}],
            "web_search": False, "expectation": None} for index in range(2)]
        def sample(pool, out, *, count, split):
            self.assertEqual(split, "development")
            self.assertEqual(count, 2)
            out.write_text(json.dumps({"cases": cases}), encoding="utf-8")
        def execute(case, model, timeout, clock, scratch, generation_profile=None):
            self.assertNotIn("reference_answer", case)
            self.assertIsNone(case["expectation"])
            return {"status": "completed", "answer": "Generated answer", "timings": {}}, False
        with patch.object(replay_pool, "check_pool", return_value={"manifest_sha256": "m", "records_sha256": "r"}), \
                patch.object(replay_pool, "sample_pool", side_effect=sample), \
                patch.object(replay_pool.runner.inventory, "capture", return_value={"ollama": {}}), \
                patch.object(replay_pool.runner, "model_snapshot", return_value={"installed": {"digest": "d"}}), \
                patch.object(replay_pool.runner, "execute_case", side_effect=execute) as run_case:
            self.assertEqual(replay_pool.run(self.args), 0)
            self.assertEqual(run_case.call_count, 2)
        manifest = json.loads((self.args.out / "manifest.json").read_text())
        self.assertEqual(manifest["kind"], "unscored_pool_replay")
        self.assertNotIn("pass_rate", manifest)
        for result in (self.args.out / "results").glob("*.json"):
            self.assertEqual(json.loads(result.read_text())["quality_status"], "unreviewed")

    def test_corrupt_pool_fails_before_output_or_model_request(self):
        with patch.object(replay_pool, "check_pool", side_effect=ValueError("hash mismatch")), \
                patch.object(replay_pool.runner.inventory, "capture") as capture:
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                replay_pool.run(self.args)
            capture.assert_not_called()
            self.assertFalse(self.args.out.exists())

    def test_changed_installation_cannot_use_a_frozen_snapshot(self):
        self.args.generation_profile = self.root / "profile.json"
        def sample(pool, out, **kwargs):
            out.write_text('{"cases":[]}', encoding="utf-8")
        api = MagicMock()
        api.inspect_model = AsyncMock(return_value={"digest": "changed"})
        with patch.object(replay_pool, "check_pool", return_value={"manifest_sha256": "m", "records_sha256": "r"}), \
                patch.object(replay_pool, "sample_pool", side_effect=sample), \
                patch.object(replay_pool.runner.inventory, "capture", return_value={"ollama": {}}), \
                patch.object(replay_pool.runner, "model_snapshot", return_value={"installed": {"digest": "original"}}), \
                patch.object(replay_pool.runner, "profile_api", return_value=api), \
                patch.object(replay_pool.runner, "execute_case") as execute:
            self.assertEqual(replay_pool.run(self.args), 1)
            execute.assert_not_called()
        manifest = json.loads((self.args.out / "manifest.json").read_text())
        self.assertIn("changed", manifest["error"])

    def mocked_preflight(self):
        cases = [{"id": f"CP-{index:064x}", "split": "development",
                  "messages": [{"role": "user", "content": f"Synthetic question {index}"}],
                  "web_search": False, "expectation": None} for index in range(2)]

        def sample(pool, out, **kwargs):
            out.write_text(json.dumps({"cases": cases}), encoding="utf-8")

        stack = ExitStack()
        stack.enter_context(patch.object(
            replay_pool, "check_pool", return_value={"manifest_sha256": "m", "records_sha256": "r"}))
        stack.enter_context(patch.object(replay_pool, "sample_pool", side_effect=sample))
        stack.enter_context(patch.object(
            replay_pool.runner.inventory, "capture", return_value={"ollama": {}}))
        stack.enter_context(patch.object(
            replay_pool.runner, "model_snapshot", return_value={"installed": {"digest": "d"}}))
        return stack

    def read_run(self):
        manifest = json.loads((self.args.out / "manifest.json").read_text())
        results = [json.loads((self.args.out / "results" / f"{case_id}.json").read_text())
                   for case_id in manifest["case_ids"]]
        self.assertEqual(len(results), 2)
        self.assertTrue(all(row["quality_status"] == "unreviewed" for row in results))
        self.assertIn("finished_at_utc", manifest)
        return manifest, results

    def test_preflight_interrupt_finalizes_all_selected_cases_as_not_evaluated(self):
        with self.mocked_preflight(), \
                patch.object(replay_pool.runner.inventory, "capture", side_effect=KeyboardInterrupt), \
                patch.object(replay_pool.runner, "execute_case") as execute:
            self.assertEqual(replay_pool.run(self.args), 130)
            execute.assert_not_called()
        manifest, results = self.read_run()
        self.assertEqual(manifest["state"], "interrupted")
        self.assertIn("interrupted", manifest["error"])
        self.assertEqual([row["status"] for row in results], ["not_evaluated"] * 2)

    def test_between_case_interrupt_preserves_completed_answer_and_unstarted_case(self):
        completed = {"status": "completed", "answer": "Preserve this answer.",
                     "timings": {"total_seconds": 1.25}}
        with self.mocked_preflight(), \
                patch.object(replay_pool.runner, "execute_case", return_value=(completed, False)) as execute, \
                patch("builtins.print", side_effect=KeyboardInterrupt):
            self.assertEqual(replay_pool.run(self.args), 130)
            self.assertEqual(execute.call_count, 1)
        manifest, results = self.read_run()
        self.assertEqual(manifest["state"], "interrupted")
        self.assertEqual(results[0]["status"], "completed")
        self.assertEqual(results[0]["answer"], "Preserve this answer.")
        self.assertEqual(results[0]["timings"]["total_seconds"], 1.25)
        self.assertIsNone(results[0]["error"])
        self.assertEqual(results[1]["status"], "not_evaluated")

    def test_owned_worker_interrupt_preserves_partial_evidence(self):
        partial = {"status": "run_error", "answer": "Partial answer", "truncated": True,
                   "error": "Owned worker stopped.", "timings": {"first_content_seconds": 0.5}}
        with self.mocked_preflight(), \
                patch.object(replay_pool.runner, "execute_case", return_value=(partial, True)) as execute:
            self.assertEqual(replay_pool.run(self.args), 130)
            self.assertEqual(execute.call_count, 1)
        manifest, results = self.read_run()
        self.assertEqual(manifest["state"], "interrupted")
        self.assertEqual(manifest["execution_errors"], 1)
        self.assertEqual(results[0]["answer"], "Partial answer")
        self.assertEqual(results[0]["error"], "Owned worker stopped.")
        self.assertTrue(results[0]["truncated"])
        self.assertEqual(results[1]["status"], "not_evaluated")

    def test_interrupted_initial_seeding_still_records_every_selected_case(self):
        real_write = replay_pool.runner.write_json
        interrupted = False

        def write(path, value):
            nonlocal interrupted
            if path.parent.name == "results" and not interrupted:
                interrupted = True
                raise KeyboardInterrupt
            return real_write(path, value)

        with self.mocked_preflight(), \
                patch.object(replay_pool.runner, "write_json", side_effect=write), \
                patch.object(replay_pool.runner, "execute_case") as execute:
            self.assertEqual(replay_pool.run(self.args), 130)
            execute.assert_not_called()
        manifest, results = self.read_run()
        self.assertEqual(manifest["state"], "interrupted")
        self.assertEqual([row["status"] for row in results], ["not_evaluated"] * 2)


if __name__ == "__main__":
    unittest.main()
