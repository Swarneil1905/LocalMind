"""Checkpoint publication regressions; no Ollama, service import or inference."""

import asyncio
import errno
import json
import os
import sys
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import service_replay as adapter


def windows_error(code: int) -> PermissionError:
    error = PermissionError(errno.EACCES, "PRIVATE_PATH_OR_MESSAGE")
    error.winerror = code
    return error


class CheckpointWriterTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.output = Path(self.temporary.name) / "output.json"
        self.previous = {"status": "run_error", "answer": "partial", "trace": {"checkpoint_final": False}}
        self.latest = {"status": "completed", "answer": "complete answer", "trace": {"checkpoint_final": True}}
        self.output.write_text(json.dumps(self.previous), encoding="utf-8")

    def test_transient_windows_conflicts_retry_same_complete_staging_file(self):
        replace = os.replace
        attempts = []

        def busy_then_publish(source, destination):
            attempts.append(Path(source))
            self.assertEqual(json.loads(Path(source).read_text(encoding="utf-8")), self.latest)
            self.assertEqual(json.loads(self.output.read_text(encoding="utf-8")), self.previous)
            if len(attempts) <= 3:
                raise windows_error((5, 32, 33)[len(attempts) - 1])
            replace(source, destination)

        with patch.object(adapter.sys, "platform", "win32"), \
                patch.object(adapter.os, "replace", side_effect=busy_then_publish), \
                patch.object(adapter.time, "sleep") as sleep:
            adapter.write_checkpoint(self.output, self.latest)
        self.assertEqual(len(attempts), 4)
        self.assertEqual(len(set(attempts)), 1)
        self.assertEqual([call.args[0] for call in sleep.call_args_list], list(adapter.CHECKPOINT_REPLACE_DELAYS[:3]))
        self.assertEqual(json.loads(self.output.read_text(encoding="utf-8")), self.latest)
        self.assertEqual(list(self.output.parent.glob("*.tmp")), [])

    def test_exhausted_retry_raises_and_preserves_old_and_unpublished_evidence(self):
        failure = windows_error(5)
        with patch.object(adapter.sys, "platform", "win32"), \
                patch.object(adapter.os, "replace", side_effect=failure) as replace, \
                patch.object(adapter.time, "sleep") as sleep:
            with self.assertRaises(PermissionError) as caught:
                adapter.write_checkpoint(self.output, self.latest)
        self.assertIs(caught.exception, failure)
        self.assertEqual(replace.call_count, len(adapter.CHECKPOINT_REPLACE_DELAYS) + 1)
        self.assertEqual(sleep.call_count, len(adapter.CHECKPOINT_REPLACE_DELAYS))
        self.assertLess(sum(adapter.CHECKPOINT_REPLACE_DELAYS), 1)
        self.assertEqual(json.loads(self.output.read_text(encoding="utf-8")), self.previous)
        staging = list(self.output.parent.glob("*.tmp"))
        self.assertEqual(len(staging), 1)
        self.assertEqual(json.loads(staging[0].read_text(encoding="utf-8")), self.latest)

    def test_other_errors_and_non_windows_permission_failures_are_not_retried(self):
        failures = (("win32", PermissionError(errno.EACCES, "Denied")),
                    ("win32", OSError(errno.ENOSPC, "Disk full")),
                    ("win32", windows_error(112)), ("linux", windows_error(5)))
        for platform, failure in failures:
            with self.subTest(platform=platform, winerror=getattr(failure, "winerror", None)), \
                    patch.object(adapter.sys, "platform", platform), \
                    patch.object(adapter.os, "replace", side_effect=failure) as replace, \
                    patch.object(adapter.time, "sleep") as sleep:
                with self.assertRaises(OSError) as caught:
                    adapter.write_checkpoint(self.output, self.latest)
                self.assertIs(caught.exception, failure)
                replace.assert_called_once()
                sleep.assert_not_called()
                self.assertEqual(json.loads(self.output.read_text(encoding="utf-8")), self.previous)

    def test_serialization_failure_never_replaces_or_truncates_previous_checkpoint(self):
        with patch.object(adapter.os, "replace") as replace:
            with self.assertRaises(ValueError):
                adapter.write_checkpoint(self.output, {"bad": float("nan")})
        replace.assert_not_called()
        self.assertEqual(json.loads(self.output.read_text(encoding="utf-8")), self.previous)
        self.assertEqual(list(self.output.parent.glob("*.tmp")), [])

    def test_publish_failure_stays_run_error_with_sanitized_codes_when_final_write_recovers(self):
        case = {"messages": [{"role": "user", "content": "Synthetic question"}], "web_search": False}
        chat = SimpleNamespace(ChatRequest=lambda **values: values, plan_query=lambda *_: None)
        failure = windows_error(5)

        def publish(value):
            if not value["trace"]["checkpoint_final"]:
                with patch.object(adapter.sys, "platform", "win32"), \
                        patch.object(adapter.os, "replace", side_effect=failure), \
                        patch.object(adapter.time, "sleep"):
                    adapter.write_checkpoint(self.output, value)
            else:
                adapter.write_checkpoint(self.output, value)

        with patch.object(adapter, "load_service", return_value=(chat, None, None)):
            observed = asyncio.run(adapter.replay(case, "unused", 2, on_update=publish,
                                                  clock="2026-10-19T12:00:00Z"))
        self.assertEqual(observed["status"], "run_error")
        self.assertEqual(observed["error"], "Replay failed: PermissionError (errno=13, winerror=5)")
        self.assertTrue(observed["truncated"])
        self.assertFalse(observed["trace"]["model_called"])
        self.assertTrue(observed["trace"]["checkpoint_final"])
        self.assertEqual(json.loads(self.output.read_text(encoding="utf-8")), observed)
        self.assertNotIn("PRIVATE", json.dumps(observed))

    @unittest.skipUnless(sys.platform == "win32", "Native Windows file-sharing behavior")
    def test_native_open_reader_can_release_lock_during_retry(self):
        replace = os.replace
        conflict = threading.Event()

        def observe_replace(source, destination):
            try:
                replace(source, destination)
            except PermissionError:
                conflict.set()
                raise

        with self.output.open("rb") as reader, ThreadPoolExecutor(max_workers=1) as executor, \
                patch.object(adapter.os, "replace", side_effect=observe_replace):
            writing = executor.submit(adapter.write_checkpoint, self.output, self.latest)
            self.assertTrue(conflict.wait(timeout=2), "Expected an actual Windows delete-sharing conflict")
            self.assertEqual(json.loads(reader.read()), self.previous)
            reader.close()
            writing.result(timeout=3)
        self.assertEqual(json.loads(self.output.read_text(encoding="utf-8")), self.latest)
        self.assertEqual(list(self.output.parent.glob("*.tmp")), [])


if __name__ == "__main__":
    unittest.main()
