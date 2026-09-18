"""
REST routes for web search configuration - Phase 5.
"""

from fastapi import APIRouter
from pydantic import BaseModel, HttpUrl, field_validator

from app.db import search_config as db

router = APIRouter(prefix="/search-config", tags=["search"])


class UpdateSearchConfigRequest(BaseModel):
    provider: str | None = None
    privacy_mode: str | None = None
    enabled: bool | None = None
    searxng_url: HttpUrl | None = None

    @field_validator("searxng_url")
    @classmethod
    def validate_base_url(cls, value: HttpUrl | None) -> HttpUrl | None:
        if value is not None and (value.username or value.password or value.query or value.fragment):
            raise ValueError("SearxNG URL must not include credentials, query parameters, or a fragment.")
        return value


@router.get("")
def get_search_config() -> dict:
    return db.get_config()


@router.patch("")
def update_search_config(req: UpdateSearchConfigRequest) -> dict:
    return db.update_config(
        provider=req.provider,
        privacy_mode=req.privacy_mode,
        enabled=req.enabled,
        searxng_url=str(req.searxng_url).rstrip("/") if req.searxng_url is not None else None,
    )
