"""
FastAPI application instance for the LocalMind AI service.

All routes require a valid bearer token passed in the Authorization header.
The token is set at sidecar startup via the LOCALMIND_TOKEN environment variable
and is generated fresh by the Rust core on every application launch.
"""

from contextlib import asynccontextmanager
from fastapi import FastAPI
from app.api.middleware.auth import verify_bearer_token

from app.api.routes import (
    chat,
    connectors,
    conversations,
    health,
    knowledge,
    memory,
    models,
    projects,
    search_config,
    task_actions,
    project_graph,
    voice,
)
from app.connectors.registry import setup_registry
from app.db.messages import init_db as init_messages_db
from app.db.people import init_db as init_people_db
from app.security import configured_token
from app.storage import migrate_legacy_connectors, profile_lock


@asynccontextmanager
async def lifespan(app: FastAPI):
    configured_token()
    with profile_lock():
        migrate_legacy_connectors()
        from app.voice import cleanup_audio
        cleanup_audio()
        init_messages_db()
        init_people_db()
        knowledge.kb.recover_interrupted_sources()
        from app.project_repairs import recover_interrupted
        recover_interrupted()
        from app.repair_apply import recover_interrupted as recover_writes
        recover_writes()
        setup_registry()
        from app.sync_engine import start_background_sync, stop_background_sync
        await start_background_sync()
        try:
            yield
        finally:
            from app.voice import jobs
            await jobs().shutdown()
            await stop_background_sync()


app = FastAPI(
    title="LocalMind AI Service",
    version="0.2.0",
    docs_url=None,
    redoc_url=None,
    lifespan=lifespan,
)


app.middleware("http")(verify_bearer_token)


app.include_router(health.router)
app.include_router(chat.router)
app.include_router(models.router)
app.include_router(memory.router)
app.include_router(knowledge.router)
app.include_router(conversations.router)
app.include_router(projects.router)
app.include_router(search_config.router)
app.include_router(connectors.router)
app.include_router(task_actions.router)
app.include_router(project_graph.router)
app.include_router(voice.router)
