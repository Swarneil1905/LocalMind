"""
Health check route.

The Rust core polls this endpoint after launching the sidecar to confirm
it is ready to accept requests. The bearer token is required on this route
because the Rust core includes it in every health check request.
"""

from fastapi import APIRouter
from fastapi.responses import JSONResponse

router = APIRouter()


@router.get("/health")
async def health() -> JSONResponse:
    return JSONResponse(content={"status": "ok"})
