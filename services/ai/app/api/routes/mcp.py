from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field

from app.mcp_client import owned_mcp, MCPError

router = APIRouter(prefix="/mcp", tags=["mcp"])


class Approval(BaseModel):
    model_config = ConfigDict(extra="forbid")
    approval: str = Field(min_length=64, max_length=64)


class Call(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str = Field(max_length=64)
    arguments: dict


@router.get("/preview")
async def preview():
    return owned_mcp().preview()


@router.post("/enable")
async def enable(body: Approval):
    try:
        return await owned_mcp().enable(body.approval)
    except (MCPError, OSError):
        raise HTTPException(
            400, "Could not enable the owned MCP server. Review its configuration and retry."
        ) from None


@router.post("/stop")
async def stop():
    try:
        return await owned_mcp().stop()
    except MCPError as error:
        raise HTTPException(400, str(error)) from None


@router.post("/call")
async def call(body: Call):
    try:
        return await owned_mcp().call(body.name, body.arguments)
    except MCPError as error:
        raise HTTPException(400, str(error)) from None
