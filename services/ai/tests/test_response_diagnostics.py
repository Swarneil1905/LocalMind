# ruff: noqa: E402 -- isolate eager database imports from the real user profile
import json
import os
import tempfile
import unittest
from unittest.mock import patch
_data = tempfile.TemporaryDirectory()
os.environ["LOCALMIND_DATA_DIR"] = _data.name
from app.response_diagnostics import response_diagnostics
from app.context_budget import pack_context
from app.api.routes.chat import _stream_ollama, ChatRequest


class DiagnosticsTests(unittest.TestCase):
    def test_provider_load_and_prompt_timings_convert_nanoseconds_and_reject_invalid_values(self):
        budget = pack_context("system", "hello", [], []).report
        result = response_diagnostics("fixture", None, budget, elapsed=3, first_token=2,
            terminal={"load_duration": 1_500_000_000, "prompt_eval_duration": 200_000_000})
        self.assertEqual(result["model_load_seconds"], 1.5)
        self.assertEqual(result["prompt_eval_seconds"], 0.2)
        for invalid in (True, -1, "100", 10**20):
            result = response_diagnostics("fixture", None, budget, elapsed=3, first_token=None,
                terminal={"load_duration": invalid, "prompt_eval_duration": invalid})
            self.assertIsNone(result["model_load_seconds"])
            self.assertIsNone(result["prompt_eval_seconds"])

    def test_metadata_does_not_include_prompts_answers_or_source_labels(self):
        packed = pack_context("private system", "private question", [], [{"label": "secret file", "content": "secret text"}])
        result = response_diagnostics("fixture", {"digest": "d", "preset": "speed"}, packed.report,
            elapsed=2.3, first_token=0.5, terminal={"eval_count": 20, "eval_duration": 1_000_000_000, "prompt_eval_count": 40, "done_reason": "stop", "answer": "private answer"})
        self.assertEqual(result["tokens_per_second"], 20)
        self.assertEqual(result["first_token_seconds"], 0.5)
        self.assertNotIn("private", json.dumps(result))
        self.assertNotIn("secret", json.dumps(result))

    def test_absent_or_invalid_measurements_stay_unknown(self):
        budget = pack_context("system", "hi", [], []).report
        for terminal in ({}, {"eval_count": True, "eval_duration": 0}, {"eval_count": -1, "prompt_eval_count": "4"}):
            result = response_diagnostics("fixture", None, budget, elapsed=float("nan"), first_token=None, terminal=terminal)
            self.assertIsNone(result["tokens_per_second"])
            self.assertIsNone(result["total_seconds"])
            self.assertIsNone(result["first_token_seconds"])
            self.assertIsNone(result["output_tokens"])
            json.dumps(result, allow_nan=False)


class StreamDiagnosticsTests(unittest.IsolatedAsyncioTestCase):
    async def frames(self, chunks):
        class Response:
            def raise_for_status(self): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            async def aiter_lines(self):
                for chunk in chunks:
                    yield json.dumps(chunk)
        class Client:
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            def stream(self, *_, **__): return Response()
        with patch("app.api.routes.chat.httpx.AsyncClient", return_value=Client()):
            return [json.loads(frame.removeprefix("data: ")) async for frame in _stream_ollama(
                ChatRequest(model="fixture", message="hello", connector_context_enabled=False))]

    async def test_terminal_diagnostics_precede_done(self):
        frames = await self.frames([{"message": {"content": "Hello"}}, {"done": True, "done_reason": "stop", "eval_count": 2}])
        self.assertEqual([frame["type"] for frame in frames][-2:], ["diagnostics", "done"])
        self.assertEqual(frames[-2]["data"]["output_tokens"], 2)
        self.assertIsNotNone(frames[-2]["data"]["first_token_seconds"])

    async def test_empty_or_reasoning_only_completion_is_not_success(self):
        for chunks in ([{"done": True}], [{"message": {"thinking": "reason"}}, {"done": True}]):
            frames = await self.frames(chunks)
            self.assertEqual(frames[-1]["type"], "error")
            self.assertIn("no answer", frames[-1]["content"])

    async def test_truncation_keeps_metrics_and_reports_incomplete_answer(self):
        frames = await self.frames([{"message": {"content": "Partial"}}, {"done": True, "done_reason": "length", "eval_count": 32}])
        self.assertEqual(frames[-1]["type"], "error")
        self.assertEqual(frames[-2]["data"]["stop_reason"], "length")
        self.assertEqual(frames[-2]["data"]["output_tokens"], 32)
