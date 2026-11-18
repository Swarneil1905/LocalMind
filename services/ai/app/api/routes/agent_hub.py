from uuid import UUID
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from app.db import hub_dependencies as hub

router = APIRouter(prefix='/projects/{project_id}/hub', tags=['agent-hub'])


class RequireRequest(BaseModel):
    model_config = ConfigDict(extra='forbid', strict=True)
    task_id: str = Field(min_length=1, max_length=36)
    requires_id: str = Field(min_length=1, max_length=36)
    expected_revision: int = Field(ge=0)


def call(function, *args):
    try:
        return function(*args)
    except KeyError as exc:
        raise HTTPException(status_code=404, detail=exc.args[0]) from exc
    except ValueError as exc:  # includes RevisionConflict: caller must reload
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@router.get('')
def snapshot(project_id: UUID):
    return call(hub.snapshot, str(project_id))


@router.post('/dependencies')
def require(project_id: UUID, request: RequireRequest):
    return {'revision': call(hub.require, str(project_id), request.task_id,
                             request.requires_id, request.expected_revision)}
