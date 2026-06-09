"""
REST routes for web search configuration - Phase 5.
"""

from fastapi import APIRouter
from pydantic import BaseModel

from app.db import search_config as db

router = APIRouter(prefix="/search-config", tags=["search"])


class UpdateSearchConfigRequest(BaseModel):
    provider: str | None = None
    privacy_mode: str | None = None
    enabled: bool | None = None


@router.get("")
def get_search_config() -> dict:
    return db.get_config()


@router.patch("")
def update_search_config(req: UpdateSearchConfigRequest) -> dict:
    return db.update_config(
        provider=req.provider,
        privacy_mode=req.privacy_mode,
        enabled=req.enabled,
    )
