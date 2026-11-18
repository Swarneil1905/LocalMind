"""Project-scoped prerequisites for Agent Hub, in the existing projects database."""
from contextlib import closing

from app.db import projects


def _open():
    conn = projects._get_conn()
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS hub_graph_versions (
            project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
            revision INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS hub_dependencies (
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            requires_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            PRIMARY KEY (task_id, requires_id),
            CHECK (task_id <> requires_id)
        );
        CREATE TRIGGER IF NOT EXISTS hub_edge_removed AFTER DELETE ON hub_dependencies
        BEGIN
            UPDATE hub_graph_versions SET revision = revision + 1
            WHERE project_id IN (
                SELECT project_id FROM tasks WHERE id IN (OLD.task_id, OLD.requires_id)
            );
        END;
    """)
    return conn


def _revision(conn, project_id):
    if not conn.execute("SELECT 1 FROM projects WHERE id=?", (project_id,)).fetchone():
        raise KeyError("Unknown project")
    row = conn.execute("SELECT revision FROM hub_graph_versions WHERE project_id=?",
                       (project_id,)).fetchone()
    return row[0] if row else 0


def snapshot(project_id):
    with closing(_open()) as conn, conn:
        conn.execute("BEGIN")
        revision = _revision(conn, project_id)
        rows = conn.execute("""
            SELECT e.task_id, e.requires_id FROM hub_dependencies e
            JOIN tasks t ON t.id=e.task_id WHERE t.project_id=?
            ORDER BY e.task_id, e.requires_id
        """, (project_id,))
        return {"revision": revision, "dependencies": [dict(row) for row in rows]}


class RevisionConflict(ValueError):
    """Someone else changed the graph after the caller read it."""


def require(project_id, task_id, requires_id, expected_revision):
    """Add a prerequisite atomically; competing editors must reload."""
    with closing(_open()) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        current = _revision(conn, project_id)
        if current != expected_revision:
            raise RevisionConflict("Reload the project graph before editing")
        if task_id == requires_id:
            raise ValueError("A task cannot depend on itself")
        count = conn.execute("SELECT count(*) FROM tasks WHERE project_id=? AND id IN (?,?)",
                             (project_id, task_id, requires_id)).fetchone()[0]
        if count != 2:
            raise KeyError("Both tasks must belong to the active project")
        if conn.execute("""
            WITH RECURSIVE upstream(id) AS (
                SELECT ? UNION SELECT e.requires_id FROM hub_dependencies e
                JOIN upstream u ON e.task_id=u.id
            ) SELECT 1 FROM upstream WHERE id=?
        """, (requires_id, task_id)).fetchone():
            raise ValueError("This prerequisite would create a cycle")
        added = conn.execute("INSERT OR IGNORE INTO hub_dependencies VALUES (?,?)",
                             (task_id, requires_id)).rowcount
        if added:
            conn.execute("INSERT INTO hub_graph_versions VALUES (?,?) ON CONFLICT(project_id) "
                         "DO UPDATE SET revision=excluded.revision", (project_id, current + 1))
        return current + added
