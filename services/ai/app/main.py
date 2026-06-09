"""
FastAPI application instance for the LocalMind AI service.

All routes require a valid bearer token passed in the Authorization header.
The token is set at sidecar startup via the LOCALMIND_TOKEN environment variable
and is generated fresh by the Rust core on every application launch.
"""

import os

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from app.api.routes import (
    chat,
    conversations,
    health,
    knowledge,
    memory,
    models,
    projects,
    search_config,
)

app = FastAPI(title="LocalMind AI Service", version="0.1.0", docs_url=None, redoc_url=None)


@app.middleware("http")
async def verify_bearer_token(request: Request, call_next):  # type: ignore[no-untyped-def]
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
