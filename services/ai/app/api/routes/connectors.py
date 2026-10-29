"""
Connector API routes.

POST   /connectors/{id}/connect     → initiate auth (returns oauth_url / qr / ready)
GET    /connectors/{id}/status      → current ConnectorStatus
POST   /connectors/{id}/sync        → trigger manual sync
DELETE /connectors/{id}             → disconnect
GET    /connectors                  → list all connectors with status
GET    /connectors/{id}/people      → list people profiles from this source
POST   /connectors/gmail/oauth-callback  → handle OAuth redirect
POST   /connectors/extract-profiles → run People Profiles extraction pipeline
"""
from fastapi import APIRouter, HTTPException, BackgroundTasks
from pydantic import BaseModel

from ...connectors.registry import registry
from ...connectors.base import ConnectorStatus
from ...db.people import all_people, person_context_for_buddy

router = APIRouter(prefix="/connectors", tags=["connectors"])


# ── List all connectors ───────────────────────────────────────────────────────

@router.get("")
async def list_connectors():
    return await registry.status_all()


# ── Status ────────────────────────────────────────────────────────────────────

@router.get("/{connector_id}/status")
async def get_status(connector_id: str):
    connector = registry.get(connector_id)
    if not connector:
        raise HTTPException(404, f"Connector '{connector_id}' not found")
    status = await connector.status()
    return {"id": connector_id, "status": status.value}


# ── Connect ───────────────────────────────────────────────────────────────────

class ConnectBody(BaseModel):
    credentials: dict = {}   # arbitrary key-value pairs (email, password, etc.)


@router.post("/{connector_id}/connect")
async def connect(connector_id: str, body: ConnectBody = ConnectBody()):
    connector = registry.get(connector_id)
    if not connector:
        raise HTTPException(404, f"Connector '{connector_id}' not found")
    result = await connector.connect(**body.credentials)
    return result


# ── Sync ──────────────────────────────────────────────────────────────────────

@router.post("/{connector_id}/sync")
async def sync(connector_id: str, background_tasks: BackgroundTasks):
    connector = registry.get(connector_id)
    if not connector:
        raise HTTPException(404, f"Connector '{connector_id}' not found")

    status = await connector.status()
    if status not in (ConnectorStatus.CONNECTED, ConnectorStatus.EXPIRED, ConnectorStatus.OFFLINE):
        raise HTTPException(400, f"Connector '{connector_id}' is not connected (status: {status.value})")

    # Run sync in background so the API returns immediately
    async def _run():
        result = await connector.sync()
        # After sync, trigger People Profiles extraction
        from ...connectors.people_extractor import extract_profiles_from_source
        if not result.error and connector_id != "gmail":
            await extract_profiles_from_source(connector_id)
        return result

    background_tasks.add_task(_run)
    return {"message": f"Sync started for {connector_id}", "connector_id": connector_id}


# ── Disconnect ────────────────────────────────────────────────────────────────

@router.delete("/{connector_id}")
async def disconnect(connector_id: str):
    connector = registry.get(connector_id)
    if not connector:
        raise HTTPException(404, f"Connector '{connector_id}' not found")
    try:
        await connector.disconnect()
    except RuntimeError:
        raise HTTPException(400, "Disconnect is incomplete. Retry when online with the credential store unlocked.") from None
    return {"message": f"{connector_id} disconnected"}


# ── Gmail OAuth callback (local redirect server) ──────────────────────────────

class OAuthCallbackBody(BaseModel):
    code: str
    state: str | None = None


@router.post("/gmail/oauth-callback")
async def gmail_oauth_callback(body: OAuthCallbackBody):
    gmail = registry.get("gmail")
    if not gmail:
        raise HTTPException(404, "Gmail connector not registered")
    success = await gmail.handle_oauth_callback(body.code, body.state)
    if success:
        return {"message": "Gmail connected successfully"}
    raise HTTPException(500, "Failed to exchange OAuth code")


# ── People Profiles ───────────────────────────────────────────────────────────

@router.get("/people")
async def list_people(limit: int = 50):
    """List all extracted people profiles."""
    people = all_people(limit=limit)
    return [dict(p) for p in people]


@router.get("/people/{name}/context")
async def person_context(name: str):
    """Get Buddy-ready context string for a person by name."""
    context = person_context_for_buddy(name)
    if not context:
        return {"context": None, "message": f"No profile found for '{name}'"}
    return {"context": context}


@router.post("/extract-profiles")
async def extract_profiles(background_tasks: BackgroundTasks):
    """Trigger People Profiles extraction across all sources."""
    from ...connectors.people_extractor import run_extraction_pipeline
    background_tasks.add_task(run_extraction_pipeline)
    return {"message": "Profile extraction started in background"}


# Diagnostics deliberately contain no token, email, message, path or provider error body.
@router.get("/gmail/debug")
async def gmail_debug():
    gmail = registry.get("gmail")
    if not gmail:
        raise HTTPException(404, "Gmail connector not registered")
    return {"status": (await gmail.status()).value, "detail": gmail.detail, "access": "read-only"}
