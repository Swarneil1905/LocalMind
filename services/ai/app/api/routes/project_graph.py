from typing import Literal
from uuid import UUID
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from app import project_graph as graph
from app import project_repairs as repairs
from app import repair_apply

router = APIRouter(prefix='/projects/{project_id}/graph', tags=['project-dependencies'])


class StrictRequest(BaseModel):
    model_config = ConfigDict(extra='forbid', strict=True)


class SourceRequest(StrictRequest):
    path: str = Field(min_length=1, max_length=1000)


class LinkRequest(StrictRequest):
    source_id: str = Field(min_length=1, max_length=36)
    fact: str = Field(min_length=1, max_length=2000)
    reason: str = Field(min_length=1, max_length=1000)
    target_kind: Literal['file', 'task']
    target_id: str = Field(min_length=1, max_length=1000)


class ReviewRequest(StrictRequest):
    link_id: str = Field(min_length=1, max_length=36)
    decision: Literal['confirm', 'reject']


class RepairRequest(StrictRequest):
    operation_id: str = Field(min_length=36, max_length=36)
    source_id: str = Field(min_length=1, max_length=36)
    old_value: str = Field(min_length=1, max_length=240)
    new_value: str = Field(min_length=1, max_length=240)


class SelectionRequest(StrictRequest):
    operation_id: str = Field(min_length=36, max_length=36)
    item_id: str = Field(min_length=36, max_length=36)
    selection: Literal['accepted', 'rejected']


class WriteRequest(StrictRequest):
    operation_id: str = Field(min_length=36, max_length=36)
    item_id: str = Field(min_length=36, max_length=36)
    confirm: Literal[True]


def call(function, *args, **kwargs):
    try:
        return function(*args, **kwargs)
    except (ValueError, OSError) as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@router.get('')
def get(project_id: UUID):
    return call(graph.get_graph, str(project_id))


@router.post('/approve')
def approve(project_id: UUID):
    return call(graph.approve_root, str(project_id))


@router.post('/sources')
def add(project_id: UUID, request: SourceRequest):
    return call(graph.add_source, str(project_id), request.path)


@router.post('/links')
def propose(project_id: UUID, request: LinkRequest):
    return call(graph.propose_link, str(project_id), **request.model_dump())


@router.post('/review')
def review(project_id: UUID, request: ReviewRequest):
    return call(graph.review_link, str(project_id), **request.model_dump())


@router.post('/rescan')
def rescan(project_id: UUID):
    return call(graph.rescan, str(project_id))


@router.post('/prepare')
def prepare(project_id: UUID, request: RepairRequest):
    return call(repairs.prepare, str(project_id), **request.model_dump())


@router.post('/selection')
def selection(project_id: UUID, request: SelectionRequest):
    return call(repairs.select_item, str(project_id), **request.model_dump())


@router.post('/apply')
def apply(project_id: UUID, request: WriteRequest):
    return call(repair_apply.apply_item, str(project_id), request.operation_id, request.item_id)


@router.post('/recover')
def recover(project_id: UUID, request: WriteRequest):
    return call(repair_apply.recover_item, str(project_id), request.operation_id, request.item_id)


@router.post('/restore')
def restore(project_id: UUID, request: WriteRequest):
    return call(repair_apply.recover_item, str(project_id), request.operation_id, request.item_id, restore=True)
