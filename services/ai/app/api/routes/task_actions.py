from uuid import UUID
from typing import Literal
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict
from app import task_actions

router = APIRouter(prefix='/task-actions', tags=['task-actions'])


class Review(BaseModel):
    model_config = ConfigDict(extra='forbid')
    conversation_id: UUID
    decision: Literal['confirm', 'dismiss']


@router.get('/conversation/{conversation_id}')
def list_actions(conversation_id: UUID):
    return {'actions': task_actions.list_actions(str(conversation_id))}


@router.post('/{operation_id}/review')
def review(operation_id: UUID, request: Review):
    try:
        return task_actions.review_action(str(operation_id), str(request.conversation_id), request.decision)
    except ValueError as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
