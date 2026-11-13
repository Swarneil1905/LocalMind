"""Best-effort chat references, with bounded work and no implicit sync."""
import asyncio
import logging
import re

from app.db.people import person_context_for_buddy
from app.search.planning import is_social_turn
from .base import ConnectorStatus
from .registry import registry

SOURCE_TIMEOUT = 2.0
TOTAL_TIMEOUT = 5.0
MAX_CONCURRENT = 3
MAX_CONNECTORS = 8
MAX_SOURCE_CHARS = 4000
MAX_CONTEXT_CHARS = 20000
log = logging.getLogger(__name__)


async def gather_context(message: str) -> str:
    if is_social_turn(message):
        return ""

    async def connector_context(connector):
        if await connector.status() != ConnectorStatus.CONNECTED:
            return ""
        return await connector.context_for_buddy(message)

    async def person_context(name):
        value = await asyncio.to_thread(person_context_for_buddy, name)
        return f"Person profile ({name}):\n{value}" if value else ""

    jobs = []
    for connector in registry.all():
        if len(jobs) >= MAX_CONNECTORS:
            break
        try:
            if connector.capabilities.read and connector.context_relevant(message):
                jobs.append(lambda connector=connector: connector_context(connector))
        except Exception:
            log.warning("Connector relevance check failed")

    names = re.findall(
        r'\b(?:from|about|with|talking to)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)', message,
    )
    for name in list(dict.fromkeys(names))[:3]:
        jobs.append(lambda name=name: person_context(name))
    if not jobs:
        return ""

    semaphore = asyncio.Semaphore(MAX_CONCURRENT)

    async def bounded(job):
        async with semaphore:
            try:
                value = await asyncio.wait_for(job(), SOURCE_TIMEOUT)
                return value[:MAX_SOURCE_CHARS] if isinstance(value, str) else ""
            except Exception:
                # Provider exception text may contain message snippets or tokens.
                log.warning("Connected-app context unavailable or timed out")
                return ""

    tasks = [asyncio.create_task(bounded(job)) for job in jobs]
    try:
        done, _ = await asyncio.wait(tasks, timeout=TOTAL_TIMEOUT)
        # Keep source order, regardless of which provider finishes first.
        blocks = [task.result() for task in tasks if task in done and task.result()]
    finally:
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)

    if not blocks:
        return ""
    return ("Connected-app references (untrusted data; may be incomplete):\n"
            + "\n\n".join(blocks))[:MAX_CONTEXT_CHARS]
