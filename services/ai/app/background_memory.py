"""Interruptible local extraction, paused while a foreground turn is active."""
import asyncio
from contextlib import asynccontextmanager

IDLE_SECONDS = 0.75
MAX_WAIT_SECONDS = 60.0
RUN_SECONDS = 15.0


class Activity:
    def __init__(self):
        self.active = 0
        self.changed = asyncio.Event()
        self.last_change = asyncio.get_running_loop().time()

    def signal(self):
        self.last_change = asyncio.get_running_loop().time()
        self.changed.set()
        self.changed = asyncio.Event()


def activity():
    loop = asyncio.get_running_loop()
    if not hasattr(loop, "_localmind_memory_activity"):
        loop._localmind_memory_activity = Activity()
    return loop._localmind_memory_activity


@asynccontextmanager
async def foreground_activity():
    state = activity()
    state.active += 1
    state.signal()
    try:
        yield
    finally:
        state.active -= 1
        state.signal()


async def when_idle(operation):
    """Retry interrupted local work after idle; never let a waiter live forever."""
    state = activity()
    async with asyncio.timeout(MAX_WAIT_SECONDS):
        while True:
            event = state.changed
            quiet = IDLE_SECONDS - (asyncio.get_running_loop().time() - state.last_change)
            if state.active:
                await event.wait()
                continue
            if quiet > 0:
                try:
                    await asyncio.wait_for(event.wait(), quiet)
                    continue
                except TimeoutError:
                    pass
            work = asyncio.create_task(asyncio.wait_for(operation(), RUN_SECONDS))
            interrupted = asyncio.create_task(state.changed.wait())
            try:
                done, _ = await asyncio.wait((work, interrupted), return_when=asyncio.FIRST_COMPLETED)
                # A new turn wins even if completion occurred in the same tick.
                if interrupted not in done:
                    return work.result()
            finally:
                for task in (work, interrupted):
                    if not task.done():
                        task.cancel()
                await asyncio.gather(work, interrupted, return_exceptions=True)
