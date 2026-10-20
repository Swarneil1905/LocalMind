"""
Model listing and status routes.

GET /models/list   - returns all models currently available in Ollama
GET /models/status - returns whether Ollama is reachable and what version
"""

import httpx
from fastapi import APIRouter, HTTPException
from fastapi.responses import JSONResponse
from pydantic import BaseModel
from app.model_profiles import resolve_profile, restore_profile, update_profile

router = APIRouter(prefix="/models")

OLLAMA_BASE = "http://127.0.0.1:11434"
TIMEOUT = 3.0


@router.get("/list")
async def list_models() -> JSONResponse:
    try:
        async with httpx.AsyncClient(trust_env=False, timeout=TIMEOUT) as client:
            resp = await client.get(f"{OLLAMA_BASE}/api/tags")
            resp.raise_for_status()
            data = resp.json()
            models = [
                {"name": m["name"], "size": m.get("size", 0), "digest": m.get("digest")}
                for m in data.get("models", [])
            ]
            return JSONResponse(content={"models": models})
    except Exception:
        return JSONResponse(content={"models": []})


@router.get("/status")
async def models_status() -> JSONResponse:
    try:
        async with httpx.AsyncClient(trust_env=False, timeout=TIMEOUT) as client:
            resp = await client.get(f"{OLLAMA_BASE}/api/version")
            resp.raise_for_status()
            version = resp.json().get("version", "unknown")
            return JSONResponse(content={"running": True, "version": version})
    except Exception:
        return JSONResponse(content={"running": False, "version": None})


class ProfileRequest(BaseModel):
    model: str
    preset: str = "balanced"
    changes: dict | None = None


@router.post("/profile")
async def model_profile(request: ProfileRequest) -> dict:
    try:
        if request.changes is not None:
            return await update_profile(request.model, request.preset, request.changes)
        return await resolve_profile(request.model, request.preset)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=503, detail="Could not inspect the installed model in Ollama.") from exc


@router.post("/profile/restore")
async def restore_model_profile(request: ProfileRequest) -> dict:
    try:
        return await restore_profile(request.model, request.preset)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=503, detail="Could not inspect the installed model in Ollama.") from exc
