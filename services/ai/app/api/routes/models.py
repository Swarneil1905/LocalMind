"""
Model listing and status routes.

GET /models/list   — returns all models currently available in Ollama
GET /models/status — returns whether Ollama is reachable and what version
"""

import httpx
from fastapi import APIRouter
from fastapi.responses import JSONResponse

router = APIRouter(prefix="/models")

OLLAMA_BASE = "http://127.0.0.1:11434"
TIMEOUT = 3.0


@router.get("/list")
async def list_models() -> JSONResponse:
    try:
        async with httpx.AsyncClient(timeout=TIMEOUT) as client:
            resp = await client.get(f"{OLLAMA_BASE}/api/tags")
            resp.raise_for_status()
            data = resp.json()
            models = [
                {"name": m["name"], "size": m.get("size", 0)}
                for m in data.get("models", [])
            ]
            return JSONResponse(content={"models": models})
    except Exception:
        return JSONResponse(content={"models": []})


@router.get("/status")
async def models_status() -> JSONResponse:
    try:
        async with httpx.AsyncClient(timeout=TIMEOUT) as client:
            resp = await client.get(f"{OLLAMA_BASE}/api/version")
            resp.raise_for_status()
            version = resp.json().get("version", "unknown")
            return JSONResponse(content={"running": True, "version": version})
    except Exception:
        return JSONResponse(content={"running": False, "version": None})
