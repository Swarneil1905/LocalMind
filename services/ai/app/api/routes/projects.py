"""
REST routes for projects and tasks - Phase 4.
"""

import httpx
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from app.db import projects as db

router = APIRouter(prefix="/projects", tags=["projects"])

# ---------------------------------------------------------------------------
# Request models
# ---------------------------------------------------------------------------

class CreateProjectRequest(BaseModel):
    name: str
    path: str | None = None


class UpdateProjectRequest(BaseModel):
    name: str | None = None
    path: str | None = None
    summary: str | None = None


class CreateTaskRequest(BaseModel):
    title: str
    due_at: str | None = None


class UpdateTaskRequest(BaseModel):
    title: str | None = None
    status: str | None = None
    due_at: str | None = None


class AssignConversationRequest(BaseModel):
    conversation_id: str
    project_id: str | None = None


class GenerateSummaryRequest(BaseModel):
    speed_model: str = "qwen2.5:1.5b"


# ---------------------------------------------------------------------------
# Project routes
# ---------------------------------------------------------------------------

@router.post("")
def create_project(req: CreateProjectRequest) -> dict:
    return db.create_project(req.name, req.path)


@router.get("")
def list_projects() -> dict:
    return {"projects": db.list_projects()}


@router.get("/{project_id}")
def get_project(project_id: str) -> dict:
    project = db.get_project(project_id)
    if not project:
        raise HTTPException(status_code=404, detail="Project not found")
    return project


@router.patch("/{project_id}")
def update_project(project_id: str, req: UpdateProjectRequest) -> dict:
    if not db.update_project(project_id, req.name, req.path, req.summary):
        raise HTTPException(status_code=404, detail="Project not found")
    project = db.get_project(project_id)
    return project or {}


@router.delete("/{project_id}")
def delete_project(project_id: str) -> dict:
    if not db.delete_project(project_id):
        raise HTTPException(status_code=404, detail="Project not found")
    return {"ok": True}


# ---------------------------------------------------------------------------
# Task routes
# ---------------------------------------------------------------------------

@router.post("/{project_id}/tasks")
def create_task(project_id: str, req: CreateTaskRequest) -> dict:
    if not db.get_project(project_id):
        raise HTTPException(status_code=404, detail="Project not found")
    return db.create_task(project_id, req.title, req.due_at)


@router.get("/tasks/all")
def list_all_tasks() -> dict:
    """Return all tasks across all projects (includes project_name field)."""
    return {"tasks": db.list_all_tasks()}


@router.get("/{project_id}/tasks")
def list_tasks(project_id: str) -> dict:
    return {"tasks": db.list_tasks(project_id)}


@router.patch("/{project_id}/tasks/{task_id}")
def update_task(project_id: str, task_id: str, req: UpdateTaskRequest) -> dict:
    try:
        updated = db.update_task(project_id, task_id, req.title, req.status, req.due_at)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    if not updated:
        raise HTTPException(status_code=404, detail="Task not found")
    return {"ok": True}


@router.delete("/{project_id}/tasks/{task_id}")
def delete_task(project_id: str, task_id: str) -> dict:
    if not db.delete_task(project_id, task_id):
        raise HTTPException(status_code=404, detail="Task not found")
    return {"ok": True}


# ---------------------------------------------------------------------------
# Association routes
# ---------------------------------------------------------------------------

@router.post("/assign-conversation")
def assign_conversation(req: AssignConversationRequest) -> dict:
    db.assign_conversation(req.conversation_id, req.project_id)
    return {"ok": True}


@router.get("/{project_id}/conversations")
def get_conversations(project_id: str) -> dict:
    return {"conversations": db.get_project_conversations(project_id)}


@router.get("/{project_id}/memories")
def get_memories(project_id: str) -> dict:
    return {"memories": db.get_project_memories(project_id)}


# ---------------------------------------------------------------------------
# Summary generation
# ---------------------------------------------------------------------------

OLLAMA_BASE = "http://127.0.0.1:11434"
SUMMARY_TIMEOUT = httpx.Timeout(connect=5.0, read=120.0, write=5.0, pool=5.0)

SUMMARY_PROMPT = """\
You are summarizing a software project for a local AI assistant.
Based on the following project information, write a concise 2-3 sentence summary.
Focus on what the project is, its current status, and the main open tasks.

Project name: {name}
Path: {path}
Open tasks: {tasks}

Write the summary in plain prose. No bullet points, no headers."""


@router.post("/{project_id}/summary")
async def generate_summary(project_id: str, req: GenerateSummaryRequest) -> dict:
    project = db.get_project(project_id)
    if not project:
        raise HTTPException(status_code=404, detail="Project not found")

    tasks = db.list_tasks(project_id)
    open_tasks = [t["title"] for t in tasks if t["status"] in ("open", "in_progress")]
    task_text = ", ".join(open_tasks) if open_tasks else "none"

    prompt = SUMMARY_PROMPT.format(
        name=project["name"],
        path=project["path"] or "not set",
        tasks=task_text,
    )

    try:
        async with httpx.AsyncClient(timeout=SUMMARY_TIMEOUT) as client:
            resp = await client.post(
                f"{OLLAMA_BASE}/api/generate",
                json={"model": req.speed_model, "prompt": prompt, "stream": False},
            )
            resp.raise_for_status()
            summary = resp.json().get("response", "").strip()
    except Exception as exc:
        raise HTTPException(status_code=502, detail=f"Ollama error: {exc}") from exc

    db.update_project(project_id, summary=summary)
    return {"summary": summary}
