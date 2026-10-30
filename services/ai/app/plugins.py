"""Owned declarative plugins. Manifests select fixed handlers, never imported code."""
from contextlib import closing
import json
import sqlite3
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

from app.storage import data_path
from app.task_actions import TaskArguments


class Strict(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class PlanInput(Strict):
    tasks: list[TaskArguments] = Field(min_length=1, max_length=12)


class Tool(Strict):
    name: str
    permission: str
    input_schema: dict


class Manifest(Strict):
    id: str = Field(pattern=r"^[a-z][a-z0-9-]{2,40}$")
    version: str = Field(pattern=r"^\d+\.\d+\.\d+$")
    entry_point: str
    permissions: list[str] = Field(min_length=1, max_length=8)
    tools: list[Tool] = Field(min_length=1, max_length=8)


PLANNER = {
    "id": "local-planner", "version": "1.0.0", "entry_point": "builtin.task_planner",
    "permissions": ["task.preview"],
    "tools": [{"name": "plan_tasks", "permission": "task.preview", "input_schema": PlanInput.model_json_schema()}],
}


def validate_manifest(raw):
    manifest = Manifest.model_validate(raw)
    # Match the owned implementation, including schemas and permission set.
    if manifest.model_dump() != PLANNER:
        raise ValueError("Only the shipped planner manifest and its declared preview permission are supported.")
    return manifest


def _conn():
    conn = sqlite3.connect(data_path("plugins.db"))
    conn.row_factory = sqlite3.Row
    conn.execute("CREATE TABLE IF NOT EXISTS plugins (id TEXT PRIMARY KEY, manifest TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 0, error TEXT NOT NULL DEFAULT '')")
    return conn


def register(raw=PLANNER):
    manifest = validate_manifest(raw)  # No database mutation before validation.
    serialized = json.dumps(manifest.model_dump(), sort_keys=True)
    with closing(_conn()) as conn, conn:
        prior = conn.execute("SELECT manifest FROM plugins WHERE id=?", (manifest.id,)).fetchone()
        if prior and prior["manifest"] != serialized:
            conn.execute("UPDATE plugins SET manifest=?, enabled=0, error='' WHERE id=?", (serialized, manifest.id))
        else:
            conn.execute("INSERT OR IGNORE INTO plugins(id,manifest) VALUES (?,?)", (manifest.id, serialized))


def status():
    register()
    with closing(_conn()) as conn:
        row = conn.execute("SELECT * FROM plugins WHERE id='local-planner'").fetchone()
    return {"manifest": PLANNER, "enabled": bool(row["enabled"]), "status": "error" if row["error"] else "enabled" if row["enabled"] else "disabled",
            "error": row["error"], "location": "local", "evidence": "Verified with synthetic inputs: permission checks, disable and failure recovery. Task previews only.", "tools": PLANNER["tools"] if row["enabled"] else []}


def set_enabled(enabled: bool):
    if type(enabled) is not bool:
        raise ValueError("Choose enabled or disabled.")
    register()
    with closing(_conn()) as conn, conn:
        conn.execute("UPDATE plugins SET enabled=?, error='' WHERE id='local-planner'", (int(enabled),))
    return status()


def _plan(arguments):
    tasks = PlanInput.model_validate(arguments)
    return {"tasks": [task.model_dump() for task in tasks.tasks], "saved": False,
            "message": "Preview only. Use the reviewed task flow to save a task to a project."}


def call(name: Literal["plan_tasks"], arguments):
    register()
    # The transaction serializes enable/disable against execution. A disabled
    # plugin cannot start a queued request. This bounded handler creates no jobs.
    with closing(_conn()) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute("SELECT enabled FROM plugins WHERE id='local-planner'").fetchone()
        if not row["enabled"]:
            raise ValueError("The local planner is disabled.")
        if name != "plan_tasks":
            raise ValueError("Tool is outside the plugin's declared permissions.")
        PlanInput.model_validate(arguments)
        try:
            result = _plan(arguments)
            if not isinstance(result, dict) or result.get("saved") is not False:
                raise RuntimeError()
        except Exception:
            conn.execute("UPDATE plugins SET enabled=0, error='Planner failed. Enable it again to retry.' WHERE id='local-planner'")
            conn.commit()
            raise ValueError("Planner failed and was disabled. No tasks were saved.") from None
    return result
