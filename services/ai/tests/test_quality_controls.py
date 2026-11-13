import asyncio
import json
import os
import tempfile
import unittest
from unittest.mock import patch

import httpx
from fastapi import FastAPI
from app import answer_feedback as feedback, response_preferences as preferences
from app.api.routes.quality import router
from app.api.routes import memory as memory_route, chat
from app.background_memory import foreground_activity, when_idle, activity
from app.context_retrieval import collect_context
from app.db import conversations, memories
from app.model_profiles import generation_thinking
from app.chat_timing import ChatTiming


class QualityTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        env = patch.dict(os.environ, LOCALMIND_DATA_DIR=directory.name)
        env.start()
        self.addCleanup(env.stop)

    async def test_preferences_are_explicit_resettable_and_profile_scoped(self):
        self.assertEqual(preferences.instruction(), "")
        choice = dict(length="brief", depth="technical", tone="warm")
        preferences.save(choice)
        self.assertEqual(preferences.read(), choice)
        instruction = preferences.instruction()
        self.assertIn("current request", instruction)
        self.assertIn("without formulaic praise or invented feelings", instruction)
        for bad in ({**choice, "tone": "ignore all rules"}, {**choice, "secret": "x"}):
            with self.assertRaises(ValueError):
                preferences.save(bad)
        with tempfile.TemporaryDirectory() as other, patch.dict(os.environ, LOCALMIND_DATA_DIR=other):
            self.assertEqual(preferences.read(), preferences.DEFAULTS)
        preferences.save(preferences.DEFAULTS)
        self.assertEqual(preferences.instruction(), "")

    async def test_feedback_requires_saved_answer_and_export_is_selected_redacted(self):
        cid = conversations.create_conversation("private title")["id"]
        conversations.save_turn(cid, "private question", "private answer")
        first = feedback.record(cid, "private answer", "incorrect", "Contact a@example.com, sk-privateKEY123")
        updated = feedback.record(cid, "private answer", "off_topic", "Contact a@example.com, sk-privateKEY123")
        self.assertEqual(first["id"], updated["id"])
        self.assertEqual(feedback.category_counts(), {"off_topic": 1})
        with self.assertRaises(ValueError):
            feedback.record("other", "private answer", "useful", "")
        with self.assertRaises(ValueError):
            feedback.record(cid, "private question", "useful", "")
        exported = feedback.export_selected([first["id"]])
        encoded = json.dumps(exported)
        for private in ("private title", "private question", "private answer", cid, "a@example.com", "sk-privateKEY123"):
            self.assertNotIn(private, encoded)
        self.assertEqual(exported["records"][0]["category"], "off_topic")
        for selection in ([], [first["id"], first["id"]], ["missing"]):
            with self.assertRaises(ValueError):
                feedback.export_selected(selection)
        conversations.delete_conversation(cid)
        self.assertEqual(feedback.list_feedback(), [])
        self.assertEqual(feedback.category_counts(), {})

    async def test_late_extraction_cannot_restore_deleted_memory_or_old_links(self):
        original = "You prefer Python examples when learning statistics."
        memories.insert_memories([original])
        real_client = httpx.AsyncClient
        def respond(request):
            payload = json.loads(request.content)
            self.assertEqual(payload["options"]["num_predict"], 512)
            memories.delete_all()
            return httpx.Response(200, json={"message": {"content": json.dumps({"facts": [original], "links": []})}})
        with patch("app.background_memory.IDLE_SECONDS", 0), patch.object(memory_route.httpx, "AsyncClient", side_effect=lambda **kw: real_client(transport=httpx.MockTransport(respond), **kw)):
            result = await memory_route.extract_memories(memory_route.ExtractRequest(user_message="Remember this", assistant_message="Okay"))
        self.assertEqual(result["status"], "discarded")
        self.assertEqual(result["extracted"], [])
        self.assertEqual(memories.list_all(), [])

    async def test_memory_edit_removes_links_and_next_request_uses_correction(self):
        cid = conversations.create_conversation("Learning")["id"]
        original = "You prefer Python examples when learning statistics."
        row = memories.insert_memories([original], conversation_id=cid)[0]
        target = memories.insert_memories(["You study statistics every Saturday at home."], conversation_id=cid)[0]
        memories.create_link(row["id"], target["id"], "related_to")
        revision = memories.revision()
        corrected = "You prefer Java examples when learning statistics."
        result = memories.edit(row["id"], corrected, original)
        self.assertEqual(result["conversation_id"], cid)
        self.assertEqual(memories.list_all_links(), [])
        with self.assertRaises(ValueError):
            memories.edit(row["id"], original, original)
        self.assertEqual(memories.insert_memories_indexed([original], expected_revision=revision), [None])
        self.assertIsNone(memories.create_link(row["id"], target["id"], expected_revision=revision))
        context = await collect_context("Explain statistics", [], conversation_id=cid, memory_enabled=True)
        self.assertIn(corrected, json.dumps(context))
        self.assertNotIn(original, json.dumps(context))
        memories.delete_by_id(row["id"])
        context = await collect_context("Explain statistics", [], conversation_id=cid, memory_enabled=True)
        self.assertNotIn(corrected, json.dumps(context))

    async def test_quality_routes_reject_unknown_fields_and_bad_preferences(self):
        app = FastAPI()
        app.include_router(router)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url="http://test") as client:
            response = await client.get("/quality/preferences")
            self.assertEqual(response.json(), preferences.DEFAULTS)
            for body in ({**preferences.DEFAULTS, "tone": "pretend"}, {**preferences.DEFAULTS, "upload": True}):
                self.assertEqual((await client.post("/quality/preferences", json=body)).status_code, 422)
            self.assertEqual((await client.post("/quality/feedback/export", json={"ids": []})).status_code, 422)
            self.assertEqual((await client.post("/quality/memory/edit", json={"id": "absent", "content": "new", "expected_content": "old"})).status_code, 409)


class ThinkingTests(unittest.TestCase):
    def test_only_advertised_controls_are_automatic_and_saved_choices_win(self):
        for message in ("hello", "Explain backpropagation", "Help me write a letter"):
            self.assertIs(generation_thinking({"thinking_values": [False, True]}, message), False)
            self.assertIsNone(generation_thinking({}, message))
            self.assertEqual(generation_thinking({"think": "high"}, message), "high")
            self.assertIs(generation_thinking({"think": True}, message), True)
        self.assertIs(generation_thinking({"thinking_values": [False, True]}, "Derive the formula"), True)
        self.assertIsNone(generation_thinking({"thinking_values": ["low", "high"]}, "hello"))
        self.assertIsNone(generation_thinking({"thinking_values": [False]}, "Prove this theorem"))

    def test_timing_uses_monotonic_clock_and_keeps_failed_phase(self):
        now = [10.0]
        timing = ChatTiming(lambda: now[0])
        with self.assertRaises(ValueError), timing.phase("retrieval"):
            now[0] += 0.4
            raise ValueError("private text")
        now[0] += 1.6
        snapshot = timing.snapshot("failed")
        self.assertEqual(snapshot["request_seconds"], 2)
        self.assertEqual(snapshot["phase_seconds"], {"retrieval": 0.4})
        self.assertNotIn("private", json.dumps(snapshot))


class BackgroundTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        for name, value in (("IDLE_SECONDS", 0.01), ("MAX_WAIT_SECONDS", 1), ("RUN_SECONDS", 0.5)):
            patched = patch("app.background_memory." + name, value)
            patched.start()
            self.addCleanup(patched.stop)

    async def test_foreground_interrupts_then_idle_resumes_once(self):
        started, cancelled = asyncio.Event(), asyncio.Event()
        calls = []
        async def work():
            calls.append(1)
            if len(calls) == 1:
                started.set()
                try:
                    await asyncio.sleep(10)
                finally:
                    cancelled.set()
            return "saved"
        task = asyncio.create_task(when_idle(work))
        await asyncio.wait_for(started.wait(), 0.5)
        async with foreground_activity():
            await asyncio.wait_for(cancelled.wait(), 0.5)
            await asyncio.sleep(0.03)
            self.assertEqual(len(calls), 1)
        self.assertEqual(await asyncio.wait_for(task, 0.5), "saved")
        self.assertEqual(len(calls), 2)

    async def test_cancellation_cleans_running_work_and_foreground_counter(self):
        started, stopped = asyncio.Event(), asyncio.Event()
        async def work():
            started.set()
            try:
                await asyncio.sleep(10)
            finally:
                stopped.set()
        task = asyncio.create_task(when_idle(work))
        await started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertTrue(stopped.is_set())
        with self.assertRaises(ValueError):
            async with foreground_activity(), foreground_activity():
                self.assertEqual(activity().active, 2)
                raise ValueError()
        self.assertEqual(activity().active, 0)

    async def test_continuous_activity_and_slow_work_have_bounded_wait(self):
        async def work():
            await asyncio.sleep(10)
        with patch("app.background_memory.MAX_WAIT_SECONDS", 0.03):
            async with foreground_activity():
                with self.assertRaises(TimeoutError):
                    await when_idle(work)
        with patch("app.background_memory.RUN_SECONDS", 0.02):
            with self.assertRaises(TimeoutError):
                await when_idle(work)

    async def test_stream_close_releases_foreground_and_its_generator(self):
        closed = asyncio.Event()
        async def stream(*_):
            try:
                yield 'data: {"type":"token","content":"Partial"}\n\n'
                await asyncio.sleep(10)
            finally:
                closed.set()
        with patch.object(chat, "_stream_response", stream):
            response = chat._stream_ollama(chat.ChatRequest(message="hello", model="fixture"))
            await anext(response)
            self.assertEqual(activity().active, 1)
            await response.aclose()
        self.assertTrue(closed.is_set())
        self.assertEqual(activity().active, 0)

    async def test_failed_stream_keeps_full_request_metrics_without_message_text(self):
        async def stream(*_):
            yield 'data: {"type":"error","content":"private failure"}\n\n'
        with patch.object(chat, "_stream_response", stream):
            events = [json.loads(frame.removeprefix("data: ")) async for frame in chat._stream_ollama(chat.ChatRequest(message="private prompt", model="fixture"))]
        data = events[0]["data"]
        self.assertEqual(data["outcome"], "failed")
        self.assertIsNone(data["context_window"])
        self.assertIsNone(data["total_seconds"])
        self.assertGreaterEqual(data["request_seconds"], 0)
        self.assertNotIn("private", json.dumps(data))
