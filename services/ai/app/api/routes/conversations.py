"""
REST routes for conversation persistence - Phase 3.5.
"""

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from app.db import conversations as db

router = APIRouter(prefix="/conversations", tags=["conversations"])


# ---------------------------------------------------------------------------
# Request models
# ---------------------------------------------------------------------------

class CreateConversationRequest(BaseModel):
    title: str


class SaveTurnRequest(BaseModel):
    user_content: str
    assistant_content: str
    assistant_thinking: str | None = None


class RenameRequest(BaseModel):
    title: str


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------

@router.post("")
def create_conversation(req: CreateConversationRequest) -> dict:
    return db.create_conversation(req.title)


@router.get("")
def list_conversations() -> dict:
    return {"conversations": db.list_conversations()}


@router.get("/{conversation_id}/messages")
def get_messages(conversation_id: str) -> dict:
    return {"messages": db.get_messages(conversation_id)}


@router.post("/{conversation_id}/turn")
def save_turn(conversation_id: str, req: SaveTurnRequest) -> dict:
    db.save_turn(
        conversation_id,
        req.user_content,
        req.assistant_content,
        req.assistant_thinking,
    )
    return {"ok": True}


@router.delete("/{conversation_id}")
def delete_conversation(conversation_id: str) -> dict:
    if not db.delete_conversation(conversation_id):
        raise HTTPException(status_code=404, detail="Conversation not found")
    return {"ok": True}


@router.patch("/{conversation_id}")
def rename_conversation(conversation_id: str, req: RenameRequest) -> dict:
    if not db.rename_conversation(conversation_id, req.title):
        raise HTTPException(status_code=404, detail="Conversation not found")
    return {"ok": True}
