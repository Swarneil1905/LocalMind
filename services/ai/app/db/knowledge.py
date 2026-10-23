"""Project-scoped knowledge with durable text search and optional vector ranking."""
import asyncio
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import math
from pathlib import Path
import re
import sqlite3
import uuid

import httpx
import lancedb
import pyarrow as pa

from app.knowledge_documents import extract_passages, location_label, MAX_FILE_BYTES
from app.storage import data_dir as _data_dir

EMBED_TIMEOUT = httpx.Timeout(connect=5.0, read=120.0, write=5.0, pool=5.0)
OLLAMA_BASE = "http://127.0.0.1:11434"
DEFAULT_EMBED_MODEL = "nomic-embed-text"
SUPPORTED_EXTENSIONS = {
    ".txt", ".md", ".py", ".js", ".ts", ".tsx", ".jsx", ".rs", ".toml",
    ".json", ".csv", ".html", ".css", ".yaml", ".yml", ".pdf",
}
MAX_FILES = 2000
MAX_PASSAGES = 20000


def _get_sqlite() -> sqlite3.Connection:
    conn = sqlite3.connect(str(_data_dir() / "knowledge_sources.db"))
    conn.row_factory = sqlite3.Row
    conn.execute("""CREATE TABLE IF NOT EXISTS sources (
        id TEXT PRIMARY KEY, path TEXT NOT NULL, name TEXT NOT NULL,
        file_count INTEGER NOT NULL DEFAULT 0, chunk_count INTEGER NOT NULL DEFAULT 0,
        status TEXT NOT NULL DEFAULT 'indexing', created_at TEXT NOT NULL)""")
    columns = {row[1] for row in conn.execute("PRAGMA table_info(sources)")}
    for name, kind in (("project_id", "TEXT"), ("error_message", "TEXT"),
                       ("failed_files", "INTEGER NOT NULL DEFAULT 0"),
                       ("failed_chunks", "INTEGER NOT NULL DEFAULT 0"),
                       ("empty_files", "INTEGER NOT NULL DEFAULT 0")):
        if name not in columns:
            conn.execute(f"ALTER TABLE sources ADD COLUMN {name} {kind}")
    conn.execute("""CREATE TABLE IF NOT EXISTS passages (
        id TEXT PRIMARY KEY, source_id TEXT NOT NULL, file_path TEXT NOT NULL,
        chunk_index INTEGER NOT NULL, content TEXT NOT NULL, file_sha256 TEXT,
        page INTEGER, line_start INTEGER, line_end INTEGER)""")
    conn.execute("CREATE INDEX IF NOT EXISTS passages_source ON passages(source_id)")
    conn.execute("CREATE VIRTUAL TABLE IF NOT EXISTS passage_fts USING fts5(id UNINDEXED, content, file_path)")
    conn.commit()
    return conn


def _get_lance_table():
    db = lancedb.connect(str(_data_dir() / "knowledge.lance"))
    schema = pa.schema([
        pa.field("id", pa.utf8()), pa.field("source_id", pa.utf8()),
        pa.field("file_path", pa.utf8()), pa.field("chunk_index", pa.int32()),
        pa.field("content", pa.utf8()), pa.field("vector", pa.list_(pa.float32(), 768)),
    ])
    if "chunks" not in db.table_names():
        db.create_table("chunks", schema=schema)
    return db.open_table("chunks")


async def embed_text(text: str, model: str = DEFAULT_EMBED_MODEL,
                     priority: str = "background") -> list[float]:
    from app.inference_queue import inference_slot
    async with inference_slot(priority=priority), httpx.AsyncClient(trust_env=False, timeout=EMBED_TIMEOUT) as client:
        resp = await client.post(f"{OLLAMA_BASE}/api/embeddings", json={"model": model, "prompt": text})
        resp.raise_for_status()
        vector = resp.json()["embedding"]
        if (not isinstance(vector, list) or len(vector) != 768
                or any(type(v) not in (float, int) or not math.isfinite(v) for v in vector)):
            raise ValueError("Choose a compatible 768-dimensional embedding model, such as nomic-embed-text.")
        return vector


def _collect_files(path: str) -> list[Path]:
    root = Path(path).resolve(strict=True)
    if root.is_file():
        return [root] if root.suffix.lower() in SUPPORTED_EXTENSIONS else []
    files = []
    for item in root.rglob("*"):
        relative = item.relative_to(root)
        if any(p.startswith(".") or p in ("node_modules", "__pycache__", "target", "dist") for p in relative.parts):
            continue
        if item.is_file() and item.suffix.lower() in SUPPORTED_EXTENSIONS:
            resolved = item.resolve()
            if not resolved.is_relative_to(root):
                continue
            files.append(resolved)
            if len(files) > MAX_FILES:
                raise ValueError("Folder exceeds 2,000 supported files. Add a smaller folder.")
    return sorted(set(files))


def list_sources() -> list[dict]:
    with closing(_get_sqlite()) as conn:
        return [dict(r) for r in conn.execute("SELECT * FROM sources ORDER BY created_at DESC")]


def get_source(source_id: str) -> dict | None:
    with closing(_get_sqlite()) as conn:
        row = conn.execute("SELECT * FROM sources WHERE id=?", (source_id,)).fetchone()
    return dict(row) if row else None


def create_source(path: str, project_id: str | None = None) -> dict:
    root = Path(path).expanduser().resolve(strict=True)
    if not root.is_file() and not root.is_dir():
        raise ValueError("Choose an existing file or folder.")
    source_id = str(uuid.uuid4())
    with closing(_get_sqlite()) as conn, conn:
        conn.execute("INSERT INTO sources(id,path,name,created_at,project_id) VALUES(?,?,?,?,?)",
                     (source_id, str(root), root.name or str(root), datetime.now(timezone.utc).isoformat(), project_id))
    return get_source(source_id)


def _clear_passages(conn, source_id: str):
    conn.execute("DELETE FROM passage_fts WHERE id IN (SELECT id FROM passages WHERE source_id=?)", (source_id,))
    conn.execute("DELETE FROM passages WHERE source_id=?", (source_id,))


def recover_interrupted_sources():
    with closing(_get_sqlite()) as conn, conn:
        conn.execute("""UPDATE sources SET status='error',
            error_message='Indexing was interrupted. Retry this folder to rebuild its passages.'
            WHERE status='indexing'""")


def prepare_retry(source_id: str) -> bool:
    with closing(_get_sqlite()) as conn, conn:
        changed = conn.execute("""UPDATE sources SET status='indexing', error_message=NULL
            WHERE id=? AND status!='indexing'""", (source_id,)).rowcount
    return bool(changed)


async def index_source(source_id: str, embed_model: str = DEFAULT_EMBED_MODEL) -> dict:
    source = get_source(source_id)
    if not source:
        return {}
    failed_files = empty_files = failed_chunks = file_count = 0
    passages, vectors = [], []
    try:
        files = _collect_files(source["path"])
        embedding_failed = False
        for file in files:
            try:
                extracted = await asyncio.to_thread(extract_passages, file)
                if len(passages) + len(extracted) > MAX_PASSAGES:
                    raise ValueError("Folder exceeds 20,000 passages. Add smaller folders.")
            except Exception:
                failed_files += 1
                continue
            if not extracted:
                empty_files += 1
                continue
            file_count += 1
            for passage in extracted:
                row = {"id": str(uuid.uuid4()), "source_id": source_id, **passage}
                passages.append(row)
                # One unavailable embedding service must not cause a two-minute
                # timeout for every remaining passage. Text search remains usable.
                if embedding_failed:
                    failed_chunks += 1
                    continue
                try:
                    vector = await embed_text(row["content"], model=embed_model)
                    vectors.append({key: row[key] for key in
                                    ("id", "source_id", "file_path", "chunk_index", "content")} | {"vector": vector})
                except Exception:
                    failed_chunks += 1
                    embedding_failed = True
        table = None
        try:
            table = _get_lance_table()
            table.delete("source_id = '" + source_id.replace("'", "''") + "'")
            if vectors:
                table.add(vectors)
        except Exception:
            failed_chunks += len(vectors)
            # Text is durable even if the optional vector store cannot open.
            # Search excludes vector rows without current text metadata below.
        if not passages:
            status = "error" if failed_files else "empty"
            notice = ("Files could not be read. Check permissions, UTF-8 encoding, or damaged/encrypted PDFs, then retry."
                      if failed_files else "No readable text found. Add supported text files or a PDF with selectable text; scanned PDFs need OCR.")
        elif failed_files or empty_files or failed_chunks:
            status = "partial"
            notice = (f"{failed_files} unreadable files; {empty_files} empty files; "
                      f"{failed_chunks} passages without semantic search. Text search is available. "
                      "Check files and the local nomic-embed-text model, then retry.")
        else:
            status, notice = "ready", None
        with closing(_get_sqlite()) as conn, conn:
            # Deleting while an embedding waits must never recreate the source.
            if conn.execute("SELECT id FROM sources WHERE id=?", (source_id,)).fetchone():
                _clear_passages(conn, source_id)
                for row in passages:
                    conn.execute("INSERT INTO passages VALUES(:id,:source_id,:file_path,:chunk_index,:content,:file_sha256,:page,:line_start,:line_end)", row)
                    conn.execute("INSERT INTO passage_fts(id,content,file_path) VALUES(?,?,?)",
                                 (row["id"], row["content"], row["file_path"]))
                conn.execute("""UPDATE sources SET status=?,error_message=?,file_count=?,chunk_count=?,
                    failed_files=?,empty_files=?,failed_chunks=? WHERE id=?""",
                             (status, notice, file_count, len(passages), failed_files, empty_files, failed_chunks, source_id))
        if table is not None and not get_source(source_id):
            table.delete("source_id = '" + source_id.replace("'", "''") + "'")
    except (Exception, asyncio.CancelledError) as error:
        with closing(_get_sqlite()) as conn, conn:
            conn.execute("UPDATE sources SET status='error',error_message=? WHERE id=?",
                         ("Indexing was interrupted or could not finish. Check the folder and retry.", source_id))
        if isinstance(error, asyncio.CancelledError):
            raise
    return get_source(source_id) or {}


async def index_path(path: str, embed_model: str = DEFAULT_EMBED_MODEL,
                     project_id: str | None = None) -> dict:
    source = create_source(path, project_id)
    return await index_source(source["id"], embed_model)


def assign_source_project(source_id: str, project_id: str | None) -> bool:
    with closing(_get_sqlite()) as conn, conn:
        cursor = conn.execute("UPDATE sources SET project_id=? WHERE id=? AND status!='indexing'", (project_id, source_id))
    return cursor.rowcount > 0


def _allowed(project_id):
    return [s["id"] for s in list_sources()
            if s.get("project_id") == project_id and s["status"] in ("ready", "partial")]


def _lexical(query: str, allowed: list[str], limit: int) -> list[dict]:
    terms = list(dict.fromkeys(re.findall(r"\w{2,}", query.lower())))[:40]
    if not allowed or not terms:
        return []
    match = " OR ".join('"' + term + '"' for term in terms)
    placeholders = ",".join("?" for _ in allowed)
    with closing(_get_sqlite()) as conn:
        rows = conn.execute(f"""SELECT p.* FROM passage_fts f JOIN passages p ON p.id=f.id
            WHERE passage_fts MATCH ? AND p.source_id IN ({placeholders})
            ORDER BY bm25(passage_fts) LIMIT ?""", (match, *allowed, limit)).fetchall()
    return [dict(row) for row in rows]


async def search(query: str, limit: int = 5, embed_model: str = DEFAULT_EMBED_MODEL,
                 hyde_enabled: bool = False, speed_model: str = "qwen2.5:1.5b",
                 project_id: str | None = None) -> list[dict]:
    limit = max(1, min(limit, 20))
    allowed = _allowed(project_id)
    if not query.strip() or not allowed:
        return []
    lexical = _lexical(query, allowed, limit * 4)
    semantic = []
    try:
        table = _get_lance_table()
        if table.count_rows():
            if hyde_enabled:
                from app.rag.hyde import hyde_embed
                vector = await hyde_embed(query, speed_model=speed_model, embed_model=embed_model)
            else:
                vector = await embed_text(query, model=embed_model, priority="foreground")
            allowed = [key for key in allowed if key in _allowed(project_id)]
            if allowed:
                ids = ", ".join("'" + key.replace("'", "''") + "'" for key in allowed)
                semantic = (table.search(vector).where(f"source_id IN ({ids})", prefilter=True)
                            .limit(limit * 4).select(["id", "source_id", "file_path", "chunk_index", "content"]).to_list())
    except Exception:
        pass  # Text retrieval is independent of embedding availability.
    current = set(_allowed(project_id)) & set(allowed)
    ranked = {}
    for group in (lexical, semantic):
        for position, row in enumerate(group):
            if row["source_id"] not in current:
                continue
            if row["id"] not in ranked:
                ranked[row["id"]] = [row, 0.0]
            ranked[row["id"]][1] += 1 / (60 + position + 1)
    # Exact identifiers/phrases should not disappear behind vector neighbours.
    needle = query.strip().casefold()
    ordered = sorted(ranked.values(), key=lambda item: (
        needle in (item[0]["content"] + " " + item[0]["file_path"]).casefold(), item[1]), reverse=True)
    results = []
    with closing(_get_sqlite()) as conn:
        text_sources = {r[0] for r in conn.execute("SELECT DISTINCT source_id FROM passages")}
        for row, _score in ordered:
            metadata = conn.execute("SELECT * FROM passages WHERE id=?", (row["id"],)).fetchone()
            if metadata is None and row["source_id"] in text_sources:
                continue  # Stale vectors left by a failed optional-store write.
            full = dict(metadata) if metadata else row
            results.append({**full, "project_id": project_id, "location": location_label(full)})
            if len(results) == limit:
                break
    return results


def passage_detail(chunk_id: str, project_id: str | None) -> dict | None:
    with closing(_get_sqlite()) as conn:
        row = conn.execute("SELECT * FROM passages WHERE id=?", (chunk_id,)).fetchone()
    if not row:
        # Legacy vector-only records remain viewable without fabricated lines.
        try:
            table = _get_lance_table()
            rows = table.search().where("id = '" + chunk_id.replace("'", "''") + "'").limit(1).to_list()
        except Exception:
            return None
        row = rows[0] if rows else None
        if row:
            with closing(_get_sqlite()) as conn:
                if conn.execute("SELECT 1 FROM passages WHERE source_id=? LIMIT 1", (row["source_id"],)).fetchone():
                    return None
    if not row:
        return None
    row = dict(row)
    row.pop("vector", None)
    source = get_source(row["source_id"])
    if not source or source.get("project_id") != project_id or source["status"] not in ("ready", "partial"):
        return None
    path, root = Path(row["file_path"]), Path(source["path"])
    state = "unverified"
    try:
        resolved, boundary = path.resolve(strict=True), root.resolve(strict=True)
        if resolved != boundary and not resolved.is_relative_to(boundary):
            state = "unavailable"
        elif row.get("file_sha256"):
            with resolved.open("rb") as stream:
                raw = stream.read(MAX_FILE_BYTES + 1)
            state = "current" if hashlib.sha256(raw).hexdigest() == row["file_sha256"] else "changed"
    except OSError:
        state = "missing"
    return {**row, "project_id": project_id, "location": location_label(row), "file_state": state,
            "notice": {"current": "This passage matches the indexed file.",
                       "changed": "The file changed since indexing. This is the saved passage; retry indexing to update it.",
                       "missing": "The original file is missing. Restore it at the displayed path, or remove and add its new folder.",
                       "unavailable": "The source now resolves outside its indexed folder. Re-add the intended folder.",
                       "unverified": "Legacy passage: file changes and line locations cannot be verified. Retry indexing."}[state]}


def delete_source(source_id: str) -> bool:
    with closing(_get_sqlite()) as conn, conn:
        deleted = conn.execute("DELETE FROM sources WHERE id=?", (source_id,)).rowcount > 0
        _clear_passages(conn, source_id)
    if deleted:
        try:
            _get_lance_table().delete("source_id = '" + source_id.replace("'", "''") + "'")
        except Exception:
            pass  # Deleted source IDs are excluded before either ranking path.
    return deleted
