"""
Background sync engine.

Runs connector.sync() every 15 minutes for all connected connectors.
After each sync, triggers People Profiles extraction.
Uses APScheduler (AsyncIOScheduler) so it lives inside the FastAPI event loop.
"""
import asyncio
import logging

logger = logging.getLogger("localmind.sync")


async def _sync_all_connectors():
    from .connectors.registry import registry
    from .connectors.base import ConnectorStatus
    from .connectors.people_extractor import run_extraction_pipeline

    for connector in registry.all():
        try:
            status = await connector.status()
            if status != ConnectorStatus.CONNECTED:
                continue
            logger.info(f"[Sync] Starting sync for {connector.meta.id}")
            result = await connector.sync()
            if result.error:
                logger.warning(f"[Sync] {connector.meta.id} error: {result.error}")
            else:
                logger.info(f"[Sync] {connector.meta.id} synced {result.items_synced} items")
        except Exception as e:
            logger.error(f"[Sync] {connector.meta.id} exception: {e}")

    # After all connectors sync, refresh people profiles
    try:
        result = await run_extraction_pipeline()
        logger.info(f"[Sync] People profiles updated: {result}")
    except Exception as e:
        logger.error(f"[Sync] Profile extraction failed: {e}")


async def start_background_sync():
    """Called at app startup. Schedules recurring sync every 15 minutes."""
    try:
        from apscheduler.schedulers.asyncio import AsyncIOScheduler

        scheduler = AsyncIOScheduler()
        scheduler.add_job(
            _sync_all_connectors,
            trigger="interval",
            minutes=15,
            id="connector_sync",
            replace_existing=True,
            max_instances=1,          # never run two syncs in parallel
        )
        scheduler.start()
        logger.info("[Sync] Background sync engine started (every 15 min)")

        # Run once immediately on startup
        asyncio.create_task(_sync_all_connectors())

    except ImportError:
        logger.warning("[Sync] apscheduler not installed — background sync disabled. Run: pip install apscheduler")
