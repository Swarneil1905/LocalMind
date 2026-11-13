"""Chat must not wait indefinitely for unrelated or unavailable sources."""
import asyncio
import os
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

from app.connectors import context
from app.connectors.base import BaseConnector, ConnectorStatus
from app.connectors.gmail import GmailConnector, _save_snapshot


def connector(text="reference", *, relevant=True):
    return SimpleNamespace(
        capabilities=SimpleNamespace(read=True),
        context_relevant=Mock(return_value=relevant),
        status=AsyncMock(return_value=ConnectorStatus.CONNECTED),
        context_for_buddy=AsyncMock(return_value=text),
    )


class ConnectorContextTests(unittest.IsolatedAsyncioTestCase):
    async def gather(self, connectors, message="Read my inbox"):
        with patch.object(context.registry, "all", return_value=connectors):
            return await context.gather_context(message)

    async def test_social_turn_never_checks_any_connector(self):
        with patch.object(context.registry, "all") as registry:
            self.assertEqual(await context.gather_context("hello, how are you today?"), "")
        registry.assert_not_called()

    async def test_irrelevant_and_unreadable_sources_do_no_io(self):
        irrelevant, forbidden = connector(relevant=False), connector()
        forbidden.capabilities.read = False
        self.assertEqual(await self.gather([irrelevant, forbidden], "Explain neural networks"), "")
        for source in (irrelevant, forbidden):
            source.status.assert_not_awaited()
            source.context_for_buddy.assert_not_awaited()

    async def test_gmail_relevance_is_checked_before_credential_or_message_access(self):
        gmail = GmailConnector(store=Mock())
        for query in ("Explain backpropagation", "Help me write an email", "How does email work?"):
            with self.subTest(query=query), patch.object(gmail, "status", AsyncMock()) as status:
                self.assertFalse(gmail.context_relevant(query))
                self.assertEqual(await gmail.context_for_buddy(query), "")
                status.assert_not_awaited()
        for query in ("Read my emails", "Summarize unread email", "Anything in Gmail?",
                      "Check my inbox", "Any email from Alex?", "Show received e-mails"):
            self.assertTrue(gmail.context_relevant(query), query)
        self.assertFalse(BaseConnector.context_relevant(None, "anything"))

    async def test_relevant_gmail_request_reads_synced_data_without_provider_calls(self):
        now = 1800000000
        store = Mock()
        store.read.return_value = {"access_token": "synthetic", "expires_at": now + 3600}
        gmail = GmailConnector(store=store, clock=lambda: now)
        with tempfile.TemporaryDirectory() as directory, patch.dict(
            os.environ, {"LOCALMIND_DATA_DIR": directory},
        ), patch.object(gmail, "_http", AsyncMock()) as provider:
            _save_snapshot([("fixture", now - 10, "Meeting at noon")], now)
            result = await self.gather([gmail])
            self.assertIn("Meeting at noon", result)
            self.assertIn("Untrusted Gmail snippets", result)
            self.assertEqual(await self.gather([gmail], "Explain neural networks"), "")
            provider.assert_not_awaited()

    async def test_timeout_cancels_slow_source_and_keeps_fast_reference(self):
        slow, fast = connector(), connector("useful reference")
        cancelled = asyncio.Event()

        async def hang():
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        slow.status.side_effect = hang
        with patch.object(context, "SOURCE_TIMEOUT", .02):
            result = await self.gather([slow, fast])
        self.assertIn("useful reference", result)
        self.assertTrue(cancelled.is_set())
        slow.context_for_buddy.assert_not_awaited()

    async def test_failure_disconnected_and_non_text_sources_do_not_hide_success(self):
        broken, offline, invalid, fast = connector(), connector(), connector(42), connector("kept")
        broken.context_for_buddy.side_effect = RuntimeError("secret token and inbox text")
        offline.status.return_value = ConnectorStatus.EXPIRED
        with self.assertLogs(context.log, level="WARNING") as logs:
            result = await self.gather([broken, offline, invalid, fast])
        self.assertIn("kept", result)
        self.assertNotIn("secret", " ".join(logs.output))
        offline.context_for_buddy.assert_not_awaited()

    async def test_parallelism_and_total_deadline_are_bounded(self):
        active = peak = 0

        async def hang():
            nonlocal active, peak
            active += 1
            peak = max(active, peak)
            try:
                await asyncio.Event().wait()
            finally:
                active -= 1

        sources = [connector() for _ in range(12)]
        for source in sources:
            source.status.side_effect = hang
        with patch.object(context, "TOTAL_TIMEOUT", .03):
            self.assertEqual(await self.gather(sources), "")
        self.assertEqual(peak, context.MAX_CONCURRENT)
        self.assertEqual(active, 0)
        for source in sources[context.MAX_CONNECTORS:]:
            source.context_relevant.assert_not_called()

    async def test_cancelling_chat_cancels_started_connector_work(self):
        started, cancelled = asyncio.Event(), asyncio.Event()
        source = connector()

        async def hang(_):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        source.context_for_buddy.side_effect = hang
        task = asyncio.create_task(self.gather([source]))
        await asyncio.wait_for(started.wait(), 1)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertTrue(cancelled.is_set())

    async def test_reference_order_and_size_do_not_depend_on_completion_order(self):
        slow, fast = connector(), connector("second")
        ready = asyncio.Event()

        async def first(_):
            await ready.wait()
            return "first " + "a" * 10000

        async def second(_):
            ready.set()
            return "second"

        slow.context_for_buddy.side_effect = first
        fast.context_for_buddy.side_effect = second
        result = await self.gather([slow, fast])
        self.assertLess(result.index("first"), result.index("second"))
        self.assertLess(len(result), 4200)


if __name__ == "__main__":
    unittest.main()
