"""Local response preferences and user-selected feedback."""
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from app import answer_feedback, response_preferences
from app.db import memories

router = APIRouter(prefix="/quality")


class StrictBody(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Preferences(StrictBody):
    length: str
    depth: str
    tone: str


class Feedback(StrictBody):
    conversation_id: str
    content: str = Field(min_length=1, max_length=1_000_000)
    category: str
    correction: str = Field(default="", max_length=4000)


class Identifier(StrictBody):
    id: str


class Selection(StrictBody):
    ids: list[str] = Field(min_length=1, max_length=200)


class MemoryEdit(Identifier):
    content: str = Field(min_length=1, max_length=2000)
    expected_content: str = Field(min_length=1, max_length=1_000_000)


@router.get("/preferences")
def get_preferences():
    return response_preferences.read()


@router.post("/preferences")
def save_preferences(request: Preferences):
    try:
        return response_preferences.save(request.model_dump())
    except ValueError as exc:
        raise HTTPException(422, str(exc)) from None


@router.get("/feedback")
def list_feedback():
    return {"records": answer_feedback.list_feedback(), "counts": answer_feedback.category_counts()}


@router.post("/feedback")
def save_feedback(request: Feedback):
    try:
        return answer_feedback.record(**request.model_dump())
    except ValueError as exc:
        raise HTTPException(422, str(exc)) from None


@router.post("/feedback/delete")
def delete_feedback(request: Identifier):
    answer_feedback.delete(request.id)
    return {"ok": True}


@router.post("/feedback/export")
def export_feedback(request: Selection):
    try:
        return answer_feedback.export_selected(request.ids)
    except ValueError as exc:
        raise HTTPException(422, str(exc)) from None


@router.post("/memory/edit")
def edit_memory(request: MemoryEdit):
    try:
        return memories.edit(request.id, request.content, request.expected_content)
    except ValueError as exc:
        raise HTTPException(409, str(exc)) from None
