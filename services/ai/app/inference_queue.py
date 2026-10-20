"""One inference slot per sidecar, with foreground priority and cancellation.

A waiting chat goes before queued background work. An already running job is
not forcibly interrupted. HTTP callers must hold the slot until their response
or stream closes; cancelling a waiter or owner always releases its capacity.
"""

import asyncio
from contextlib import asynccontextmanager
import heapq
import itertools
from typing import Literal
from weakref import WeakKeyDictionary


class InferenceQueue:
    def __init__(self, limit: int = 64):
        self.limit = limit
        self._waiting: list[tuple[int, int, asyncio.Future]] = []
        self._sequence = itertools.count()
        self._owner: asyncio.Future | None = None

    @property
    def pending(self) -> int:
        return sum(not future.done() for _, _, future in self._waiting)

    @property
    def active(self) -> bool:
        return self._owner is not None

    def _advance(self) -> None:
        if self._owner is not None:
            return
        while self._waiting:
            _, _, future = heapq.heappop(self._waiting)
            if not future.done():
                self._owner = future
                future.set_result(None)
                return

    @asynccontextmanager
    async def slot(self, priority: Literal["foreground", "background"] = "background"):
        if priority not in ("foreground", "background"):
            raise ValueError("Unknown inference priority")
        if self.pending >= self.limit:
            raise RuntimeError("The local model queue is full. Please retry shortly.")
        ticket = asyncio.get_running_loop().create_future()
        heapq.heappush(self._waiting, (0 if priority == "foreground" else 1,
                                      next(self._sequence), ticket))
        self._advance()
        try:
            await ticket
            yield
        finally:
            if self._owner is ticket:
                self._owner = None
            ticket.cancel()
            self._waiting = [entry for entry in self._waiting if entry[2] is not ticket]
            heapq.heapify(self._waiting)
            self._advance()


# Production has one loop. Tests can create several without sharing loop-bound
# futures or stale ownership; profile data never enters the scheduler.
_queues: WeakKeyDictionary = WeakKeyDictionary()


def current_queue() -> InferenceQueue:
    loop = asyncio.get_running_loop()
    if loop not in _queues:
        _queues[loop] = InferenceQueue()
    return _queues[loop]


@asynccontextmanager
async def inference_slot(priority: Literal["foreground", "background"] = "background"):
    async with current_queue().slot(priority):
        yield
