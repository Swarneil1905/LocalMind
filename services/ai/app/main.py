"""
FastAPI application instance for the LocalMind AI service.

All routes require a valid bearer token passed in the Authorization header.
The token is set at sidecar startup via the LOCALMIND_TOKEN environment variable
and is generated fresh by the Rust core on every application launch.
"""

import os

from contextlib import asynccontextmanager
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

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
)
from app.connectors.registry import setup_registry
from app.db.messages import init_db as init_messages_db
from app.db.people import init_db as init_people_db


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: initialise DBs, register connectors, start background sync
    init_messages_db()
    init_people_db()
    setup_registry()
    from app.sync_engine import start_background_sync
    await start_background_sync()
    yield
    # Shutdown: nothing to clean up for now


app = FastAPI(
    title="LocalMind AI Service",
    version="0.2.0",
    docs_url=None,
    redoc_url=None,
    lifespan=lifespan,
)


@app.middleware("http")
async def verify_bearer_token(request: Request, call_next):  # type: ignore[no-untyped-def]
    # Health check is unauthenticated so Tauri can poll it
    if request.url.path == "/health":
        return await call_next(request)
    token = os.environ.get("LOCALMIND_TOKEN", "")
    auth_header = request.headers.get("Authorization", "")
    if auth_header != f"Bearer {token}":
        return JSONResponse(status_code=401, content={"detail": "Unauthorized"})
    return await call_next(request)


app.include_router(health.router)
app.include_router(chat.router)
app.include_router(models.router)
app.include_router(memory.router)
app.include_router(knowledge.router)
app.include_router(conversations.router)
app.include_router(projects.router)
app.include_router(search_config.router)
app.include_router(connectors.router)
