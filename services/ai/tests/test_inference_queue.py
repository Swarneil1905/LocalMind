import asyncio
import unittest

from app.inference_queue import InferenceQueue


class InferenceQueueTests(unittest.IsolatedAsyncioTestCase):
    async def test_waiting_chat_precedes_two_background_jobs(self):
        queue, order = InferenceQueue(), []
        async def work(name, priority):
            async with queue.slot(priority):
                order.append(name)
                await asyncio.sleep(0)
        async with queue.slot():
            background = [asyncio.create_task(work(name, "background")) for name in ("memory", "followups")]
            await asyncio.sleep(0)
            chat = asyncio.create_task(work("chat", "foreground"))
            await asyncio.sleep(0)
            self.assertEqual(queue.pending, 3)
        await asyncio.gather(*background, chat)
        self.assertEqual(order, ["chat", "memory", "followups"])
        self.assertFalse(queue.active)
        self.assertEqual(queue.pending, 0)

    async def test_cancel_waiter_cannot_cancel_active_or_next_request(self):
        queue, entered = InferenceQueue(), []
        async def work(name):
            async with queue.slot("foreground"):
                entered.append(name)
        async with queue.slot():
            cancelled = asyncio.create_task(work("cancelled"))
            next_request = asyncio.create_task(work("next"))
            await asyncio.sleep(0)
            cancelled.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await cancelled
            self.assertTrue(queue.active)
            self.assertEqual(queue.pending, 1)
        await next_request
        self.assertEqual(entered, ["next"])

    async def test_cancel_owner_closes_its_resource_before_next_job(self):
        queue, events, started = InferenceQueue(), [], asyncio.Event()
        async def owner():
            async with queue.slot("foreground"):
                try:
                    started.set()
                    await asyncio.Future()
                finally:
                    events.append("stream_closed")
        async def next_request():
            async with queue.slot("foreground"):
                events.append("next_started")
        first = asyncio.create_task(owner())
        await started.wait()
        second = asyncio.create_task(next_request())
        await asyncio.sleep(0)
        first.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await first
        await second
        self.assertEqual(events, ["stream_closed", "next_started"])
        self.assertFalse(queue.active)

    async def test_exception_and_cancel_immediately_after_grant_release_slot(self):
        queue = InferenceQueue()
        with self.assertRaises(RuntimeError):
            async with queue.slot():
                raise RuntimeError("fixture")
        async def waiter():
            async with queue.slot():
                self.fail("Cancelled task should not enter")
        async with queue.slot():
            task = asyncio.create_task(waiter())
            await asyncio.sleep(0)
        task.cancel()  # Ticket was granted but waiter has not resumed.
        with self.assertRaises(asyncio.CancelledError):
            await task
        async with queue.slot("foreground"):
            self.assertTrue(queue.active)
        self.assertFalse(queue.active)
        self.assertEqual(queue.pending, 0)

    async def test_queue_limit_is_bounded_and_cancelled_waiters_leave_capacity(self):
        queue = InferenceQueue(limit=1)
        async def waiter():
            async with queue.slot():
                pass
        async with queue.slot():
            task = asyncio.create_task(waiter())
            await asyncio.sleep(0)
            with self.assertRaisesRegex(RuntimeError, "queue is full"):
                async with queue.slot():
                    pass
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            self.assertEqual(queue.pending, 0)


if __name__ == "__main__":
    unittest.main()
