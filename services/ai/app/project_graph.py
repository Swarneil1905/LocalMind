"""User-reviewed dependencies and conservative revision-based impact checks."""
import json
import uuid
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path

from app.db import projects
from app.project_files import read_text, relative_name

MAX_RECORDS = 200


def _now():
    return datetime.now(timezone.utc).isoformat()


def _conn():
    conn = projects._get_conn()
    conn.executescript('''
        CREATE TABLE IF NOT EXISTS graph_roots (
            project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
            path TEXT NOT NULL, device TEXT NOT NULL, inode TEXT NOT NULL, approved_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS graph_sources (
            id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            path TEXT NOT NULL, sha256 TEXT NOT NULL, content TEXT NOT NULL,
            state TEXT NOT NULL DEFAULT 'unchanged', current_sha256 TEXT, notice TEXT,
            checked_at TEXT NOT NULL, UNIQUE(project_id, path)
        );
        CREATE TABLE IF NOT EXISTS graph_links (
            id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            source_id TEXT NOT NULL REFERENCES graph_sources(id) ON DELETE CASCADE,
            fact TEXT NOT NULL, reason TEXT NOT NULL, target_kind TEXT NOT NULL,
            target_id TEXT NOT NULL, target_revision TEXT NOT NULL, target_preview TEXT NOT NULL,
            state TEXT NOT NULL DEFAULT 'proposed', target_state TEXT NOT NULL DEFAULT 'unchanged',
            target_notice TEXT, checked_at TEXT NOT NULL, created_at TEXT NOT NULL
        );
    ''')
    return conn


def _project(conn, project_id):
    row = conn.execute('SELECT * FROM projects WHERE id=?', (project_id,)).fetchone()
    if not row:
        raise ValueError('Project not found.')
    return dict(row)


def approve_root(project_id):
    with closing(_conn()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        project = _project(conn, project_id)
        if not project['path'] or not Path(project['path']).is_absolute():
            raise ValueError('Set an absolute working folder in the project Overview first.')
        path = Path(project['path']).resolve(strict=True)
        if not path.is_dir():
            raise ValueError('The working folder must be an existing directory.')
        previous = conn.execute('SELECT * FROM graph_roots WHERE project_id=?', (project_id,)).fetchone()
        info = path.stat()
        if previous:
            if (previous['path'], previous['device'], previous['inode']) != (str(path), str(info.st_dev), str(info.st_ino)):
                raise ValueError('This graph belongs to a different folder revision. Restore that folder or use a new project.')
            return dict(previous)
        conn.execute('INSERT INTO graph_roots VALUES (?,?,?,?,?)',
                     (project_id, str(path), str(info.st_dev), str(info.st_ino), _now()))
        return dict(conn.execute('SELECT * FROM graph_roots WHERE project_id=?', (project_id,)).fetchone())


def _root(conn, project_id):
    project = _project(conn, project_id)
    row = conn.execute('SELECT * FROM graph_roots WHERE project_id=?', (project_id,)).fetchone()
    if not row:
        raise ValueError('Approve the working folder before tracking files.')
    path = Path(row['path'])
    if not project['path'] or Path(project['path']).resolve(strict=True) != path or path.resolve(strict=True) != path:
        raise ValueError('The working folder changed. Restore the approved folder before scanning.')
    info = path.stat()
    if (str(info.st_dev), str(info.st_ino)) != (row['device'], row['inode']):
        raise ValueError('The approved folder was replaced. Use a new project for the new folder.')
    return path


def add_source(project_id, path):
    path = relative_name(path)
    with closing(_conn()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        root = _root(conn, project_id)
        existing = conn.execute('SELECT * FROM graph_sources WHERE project_id=? AND path=?', (project_id, path)).fetchone()
        if existing:
            return dict(existing)  # Never silently reset a baseline used by confirmed links.
        if conn.execute('SELECT COUNT(*) FROM graph_sources WHERE project_id=?', (project_id,)).fetchone()[0] >= MAX_RECORDS:
            raise ValueError('This project has reached its 200 tracked-file limit.')
        snapshot = read_text(root, path)
        sid = str(uuid.uuid4())
        conn.execute('INSERT INTO graph_sources (id, project_id, path, sha256, content, current_sha256, checked_at) VALUES (?,?,?,?,?,?,?)',
                     (sid, project_id, path, snapshot['sha256'], snapshot['content'], snapshot['sha256'], _now()))
        return dict(conn.execute('SELECT * FROM graph_sources WHERE id=?', (sid,)).fetchone())


def _task_snapshot(conn, project_id, task_id):
    row = conn.execute('SELECT * FROM tasks WHERE id=? AND project_id=?', (task_id, project_id)).fetchone()
    if not row:
        raise FileNotFoundError('The dependent task no longer exists in this project.')
    task = dict(row)
    revision = json.dumps({key: task[key] for key in ('title', 'status', 'due_at')}, sort_keys=True)
    return {'revision': revision, 'preview': f"{task['title']} · {task['status']} · {task['due_at'] or 'No due date'}"}


def _target(conn, root, project_id, kind, target):
    if kind == 'task':
        return _task_snapshot(conn, project_id, target)
    if kind != 'file':
        raise ValueError('Choose a text file or a local project task.')
    snapshot = read_text(root, target)
    return {'revision': snapshot['sha256'], 'preview': snapshot['content'][:2000]}


def propose_link(project_id, source_id, fact, reason, target_kind, target_id):
    fact, reason = fact.strip(), reason.strip()
    if not fact or len(fact) > 2000 or not reason or len(reason) > 1000:
        raise ValueError('Provide a source quote (up to 2000 characters) and a dependency reason (up to 1000).')
    if target_kind == 'file':
        target_id = relative_name(target_id)
    with closing(_conn()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        root = _root(conn, project_id)
        source = conn.execute('SELECT * FROM graph_sources WHERE id=? AND project_id=?', (source_id, project_id)).fetchone()
        if not source or fact not in source['content']:
            raise ValueError('Select an exact quote from a tracked source in this project.')
        if read_text(root, source['path'])['sha256'] != source['sha256']:
            raise ValueError('This source changed since it was captured. Review its impacts before adding dependencies.')
        if target_kind == 'file' and target_id == source['path']:
            raise ValueError('Choose a dependent file different from the source.')
        target = _target(conn, root, project_id, target_kind, target_id)
        if conn.execute('SELECT COUNT(*) FROM graph_links WHERE project_id=?', (project_id,)).fetchone()[0] >= MAX_RECORDS:
            raise ValueError('This project has reached its 200 dependency limit.')
        lid, now = str(uuid.uuid4()), _now()
        conn.execute('INSERT INTO graph_links (id, project_id, source_id, fact, reason, target_kind, target_id, target_revision, target_preview, checked_at, created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?)',
                     (lid, project_id, source_id, fact, reason, target_kind, target_id, target['revision'], target['preview'], now, now))
        return dict(conn.execute('SELECT * FROM graph_links WHERE id=?', (lid,)).fetchone())


def review_link(project_id, link_id, decision):
    if decision not in {'confirm', 'reject'}:
        raise ValueError('Choose confirm or reject.')
    with closing(_conn()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        row = conn.execute('SELECT * FROM graph_links WHERE id=? AND project_id=?', (link_id, project_id)).fetchone()
        if not row:
            raise ValueError('Dependency not found in this project.')
        if row['state'] != 'proposed':
            return dict(row)
        if decision == 'confirm':
            root = _root(conn, project_id)
            source = conn.execute('SELECT * FROM graph_sources WHERE id=? AND project_id=?', (row['source_id'], project_id)).fetchone()
            if read_text(root, source['path'])['sha256'] != source['sha256']:
                raise ValueError('The source changed after this proposal. It cannot be confirmed.')
            if _target(conn, root, project_id, row['target_kind'], row['target_id'])['revision'] != row['target_revision']:
                raise ValueError('The dependent item changed after this proposal. Reject it and prepare a new link.')
        conn.execute('UPDATE graph_links SET state=? WHERE id=?', ('confirmed' if decision == 'confirm' else 'rejected', link_id))
        return dict(conn.execute('SELECT * FROM graph_links WHERE id=?', (link_id,)).fetchone())


def _observe(read, revision):
    try:
        current = read()
        value = current.get('revision', current.get('sha256'))
        return ('unchanged' if value == revision else 'changed', value, None)
    except FileNotFoundError:
        return 'missing', None, 'The registered file or task is missing. Restore it or review its dependencies.'
    except (OSError, ValueError) as exc:
        return 'unavailable', None, str(exc)


def rescan(project_id):
    with closing(_conn()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        root = _root(conn, project_id)
        now = _now()
        # No directory walk: only explicitly captured sources and reviewed
        # dependency targets are read, within bounded per-project limits.
        for source in conn.execute('SELECT * FROM graph_sources WHERE project_id=?', (project_id,)).fetchall():
            state, current, notice = _observe(lambda: read_text(root, source['path']), source['sha256'])
            conn.execute('UPDATE graph_sources SET state=?, current_sha256=?, notice=?, checked_at=? WHERE id=?',
                         (state, current, notice, now, source['id']))
        for link in conn.execute("SELECT * FROM graph_links WHERE project_id=? AND state!='rejected'", (project_id,)).fetchall():
            state, _, notice = _observe(lambda: _target(conn, root, project_id, link['target_kind'], link['target_id']), link['target_revision'])
            conn.execute('UPDATE graph_links SET target_state=?, target_notice=?, checked_at=? WHERE id=?', (state, notice, now, link['id']))
    return get_graph(project_id)


def get_graph(project_id):
    with closing(_conn()) as conn:
        _project(conn, project_id)
        root = conn.execute('SELECT * FROM graph_roots WHERE project_id=?', (project_id,)).fetchone()
        sources = [dict(row) for row in conn.execute('SELECT * FROM graph_sources WHERE project_id=? ORDER BY path', (project_id,))]
        links = [dict(row) for row in conn.execute('SELECT * FROM graph_links WHERE project_id=? ORDER BY created_at', (project_id,))]
        by_id = {source['id']: source for source in sources}
        for link in links:
            source = by_id[link['source_id']]
            link['source_path'] = source['path']
            link['source_state'] = source['state']
            link['needs_review'] = link['state'] == 'confirmed' and (source['state'] != 'unchanged' or link['target_state'] != 'unchanged')
            link['impact_reason'] = ('The supporting source revision changed or cannot be checked. Review the saved quote and dependent item.'
                                     if source['state'] != 'unchanged' else 'The dependent item changed or is missing. Review the saved dependency.') if link['needs_review'] else None
        from app.project_repairs import list_operations
        from app.repair_apply import capabilities, receipts
        return {'root': dict(root) if root else None, 'sources': sources, 'links': links,
                'tasks': projects.list_tasks(project_id), 'repairs': list_operations(project_id),
                'apply_capabilities': capabilities(), 'write_receipts': receipts(project_id)}
