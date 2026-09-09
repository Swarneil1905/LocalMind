"""Authenticate every local HTTP request, including startup probes."""

from fastapi import Request
from fastapi.responses import JSONResponse

from app.security import authorized


async def verify_bearer_token(request: Request, call_next):
    if not authorized(request.headers.get("Authorization", "")):
        return JSONResponse(status_code=401, content={"detail": "Unauthorized"})
    return await call_next(request)
