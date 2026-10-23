"""
Knowledge indexing and search routes - Phase 3.

POST /knowledge/index
    Kick off background indexing of a folder or file. Returns immediately.
    The source record appears in GET /knowledge/sources with status="indexing"
    while work is in progress, then transitions to ready, partial, empty or error.

GET  /knowledge/sources
    Return all indexed sources.

POST /knowledge/search
    Project-scoped text and semantic search over indexed passages.

DELETE /knowledge/source/{source_id}
    Remove a source and all its chunks.
"""

from fastapi import APIRouter, BackgroundTasks, HTTPException
from pydantic import BaseModel, Field

from app.db import knowledge as kb
from app.db import projects

router = APIRouter(prefix="/knowledge")


class IndexRequest(BaseModel):
    path: str
    embed_model: str = "nomic-embed-text"
    project_id: str | None = None


class SearchRequest(BaseModel):
    query: str = Field(min_length=1, max_length=4000)
    limit: int = Field(default=5, ge=1, le=20)
    embed_model: str = "nomic-embed-text"
    hyde_enabled: bool = False
    speed_model: str = "qwen2.5:1.5b"
    project_id: str | None = None


def _validate_project(project_id: str | None) -> None:
    if project_id is not None and not projects.get_project(project_id):
        raise HTTPException(status_code=404, detail="Project not found")


class SourceProjectRequest(BaseModel):
    project_id: str | None = None


@router.patch("/source/{source_id}/project")
def assign_source_project(source_id: str, request: SourceProjectRequest) -> dict:
    _validate_project(request.project_id)
    if not kb.assign_source_project(source_id, request.project_id):
        raise HTTPException(status_code=404, detail="Source not found")
    return {"sources": kb.list_sources()}


@router.post("/index")
async def index_path(request: IndexRequest, background_tasks: BackgroundTasks) -> dict:
    """
    Start indexing a folder or file in the background.
    kb.create_source creates an 'indexing' source record synchronously before
    doing any embedding, so the source will appear in /sources immediately.
    """
    _validate_project(request.project_id)
    try:
        source = kb.create_source(request.path, request.project_id)
    except (OSError, ValueError) as error:
        raise HTTPException(status_code=422, detail="Choose an existing readable file or folder.") from error
    background_tasks.add_task(kb.index_source, source["id"], request.embed_model)
    return {"ok": True, "source": source}


class RetryRequest(BaseModel):
    embed_model: str = "nomic-embed-text"


@router.post("/source/{source_id}/retry")
async def retry_source(source_id: str, request: RetryRequest, background_tasks: BackgroundTasks) -> dict:
    source = kb.get_source(source_id)
    if not source:
        raise HTTPException(status_code=404, detail="Source not found")
    _validate_project(source.get("project_id"))
    if not kb.prepare_retry(source_id):
        raise HTTPException(status_code=409, detail="This folder is already indexing")
    background_tasks.add_task(kb.index_source, source_id, request.embed_model)
    return {"ok": True, "source": kb.get_source(source_id)}


@router.get("/passage/{chunk_id}")
def get_passage(chunk_id: str, project_id: str | None = None) -> dict:
    _validate_project(project_id)
    passage = kb.passage_detail(chunk_id, project_id)
    if not passage:
        raise HTTPException(status_code=404, detail="This passage was removed, reindexed, or moved to another project. Search again.")
    return passage


@router.get("/sources")
def get_sources() -> dict:
    """Return all indexed sources with status."""
    return {"sources": kb.list_sources()}


@router.post("/search")
async def search_knowledge(request: SearchRequest) -> dict:
    """Semantic search over indexed chunks."""
    _validate_project(request.project_id)
    results = await kb.search(
        request.query,
        limit=request.limit,
        embed_model=request.embed_model,
        hyde_enabled=request.hyde_enabled,
        speed_model=request.speed_model,
        project_id=request.project_id,
    )
    return {"results": results}


@router.delete("/source/{source_id}")
def delete_source(source_id: str) -> dict:
    deleted = kb.delete_source(source_id)
    if not deleted:
        raise HTTPException(status_code=404, detail="Source not found")
    return {"ok": True}
