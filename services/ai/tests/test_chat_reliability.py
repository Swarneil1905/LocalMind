# ruff: noqa: E402 -- database imports must follow isolated test data setup
import json
import os
import tempfile
import unittest
from datetime import datetime, timezone
from unittest.mock import AsyncMock, patch

# Imports initialize the configuration DB; isolate it from the user's data.
_data = tempfile.TemporaryDirectory()
os.environ["LOCALMIND_DATA_DIR"] = _data.name

from app.api.routes.chat import ChatRequest, _run_web_search, _stream_ollama
from app.search.fetcher import PageEvidence, extract_article
from app.search.planning import is_recent, plan_query
from app.search.providers import SearchResult
from app.db import conversations


class RetrievalPolicyTests(unittest.TestCase):
    def test_sources_survive_reopening_and_turn_order_is_stable(self):
        chat = conversations.create_conversation("Test sources")
        sources = [{"url": "https://example.com", "published_at": "2026-10-09"}]
        conversations.save_turn(chat["id"], "question", "answer", sources=sources)
        messages = conversations.get_messages(chat["id"])
        self.assertEqual([m["role"] for m in messages], ["user", "assistant"])
        self.assertEqual(messages[1]["sources"], sources)
        conversations.delete_conversation(chat["id"])
    def test_greeting_and_edit_do_not_search(self):
        self.assertIsNone(plan_query("hello", []))
        self.assertIsNone(plan_query("rewrite that more clearly", []))

    def test_followup_uses_relevant_story(self):
        history = [
            {"role": "user", "content": "latest San Antonio news"},
            {"role": "assistant", "content": "Local crime update.\nCollege football: Oklahoma defeated Texas in the Red River Rivalry."},
        ]
        query = plan_query("Can you provide more details about the college football game mentioned?", history)
        self.assertIn("Oklahoma", query)
        self.assertIn("Texas", query)
        self.assertNotIn("Local crime", query)

    def test_unknown_old_and_future_dates_are_not_current(self):
        now = datetime(2026, 10, 10, tzinfo=timezone.utc)
        for date in [None, "not a date", "2023-10-07", "2027-01-01"]:
            self.assertFalse(is_recent(date, now))
        self.assertTrue(is_recent("2026-10-09T12:00:00Z", now))

    def test_article_extraction_ignores_navigation_and_keeps_published_date(self):
        page = extract_article('<meta property="article:published_time" content="2026-10-09"><nav>Celebrity noise</nav><main><h1>Local news</h1><p>Actual story.</p><script>bad()</script></main>')
        self.assertEqual(page.published_at, "2026-10-09")
        self.assertEqual(page.text, "Local news Actual story.")


class ChatStreamTests(unittest.IsolatedAsyncioTestCase):
    async def test_latest_news_rejects_undated_results(self):
        with patch("app.api.routes.chat.search", AsyncMock(return_value=[SearchResult("Old story", "https://example.com/story", "snippet")])), patch("app.api.routes.chat.fetch_evidence", AsyncMock(return_value=PageEvidence("old story"))):
            context, sources = await _run_web_search("latest news", fresh=True)
            self.assertEqual((context, sources), ("", []))

    async def test_search_failure_does_not_call_model_to_invent_news(self):
        request = ChatRequest(message="latest San Antonio news", model="test", web_search_enabled=True, connector_context_enabled=False)
        with patch("app.api.routes.chat._run_web_search", AsyncMock(return_value=("", []))), patch("app.api.routes.chat.httpx.AsyncClient") as client:
            frames = [json.loads(frame.removeprefix("data: ")) async for frame in _stream_ollama(request)]
        client.assert_not_called()
        self.assertEqual(frames[-1]["type"], "done")
        self.assertIn("couldn't verify", next(f["content"] for f in frames if f["type"] == "token"))

    async def test_greeting_skips_search_and_streams_native_reasoning(self):
        class Response:
            def raise_for_status(self): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            async def aiter_lines(self):
                for chunk in [{"message": {"thinking": "reason"}}, {"message": {"content": "Hello"}}, {"done": True}]:
                    yield json.dumps(chunk)
        class Client:
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            def stream(self, *_, **__): return Response()
        with patch("app.api.routes.chat._run_web_search", AsyncMock()) as search, patch("app.api.routes.chat.httpx.AsyncClient", return_value=Client()):
            frames = [json.loads(f.removeprefix("data: ")) async for f in _stream_ollama(ChatRequest(message="hello", model="test", web_search_enabled=True, connector_context_enabled=False))]
        search.assert_not_called()
        self.assertEqual(frames[0], {"type": "status", "phase": "answering"})
        self.assertEqual(frames[1]["thinking"], "reason")
        self.assertEqual(frames[2]["content"], "Hello")
        self.assertEqual(frames[3]["type"], "done")


if __name__ == "__main__":
    unittest.main()
