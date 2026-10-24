"""Prepare exact-value repairs on owned copies; applying requires separate review."""
import difflib
import hashlib
import json
import re
import uuid
from contextlib import closing
from datetime import date
from pathlib import Path

from app import project_graph as graph
from app.project_files import MAX_TEXT_BYTES, read_text
from app.storage import data_path
from app.task_actions import TaskArguments

MAX_STAGED_BYTES = 8 * 1024 * 1024
MAX_PROJECT_PREVIEW_BYTES = 16 * 1024 * 1024


def _conn():
    conn = graph._conn()
    conn.executescript('''
        CREATE TABLE IF NOT EXISTS repair_operations (
            id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            source_id TEXT NOT NULL, source_before TEXT NOT NULL, source_after TEXT NOT NULL,
            old_value TEXT NOT NULL, new_value TEXT NOT NULL, staging_path TEXT NOT NULL,
            state TEXT NOT NULL, error TEXT, created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS repair_items (
            id TEXT PRIMARY KEY, operation_id TEXT NOT NULL REFERENCES repair_operations(id) ON DELETE CASCADE,
            link_id TEXT NOT NULL, target_kind TEXT NOT NULL, target_id TEXT NOT NULL,
            reason TEXT NOT NULL, fact TEXT NOT NULL, before_text TEXT NOT NULL, after_text TEXT,
            before_hash TEXT, after_hash TEXT, diff TEXT NOT NULL, validation TEXT NOT NULL,
            checks TEXT NOT NULL, selection TEXT NOT NULL DEFAULT 'pending'
        );
    ''')
    return conn


def list_operations(project_id):
    with closing(_conn()) as conn:
        operations = [dict(row) for row in conn.execute('SELECT * FROM repair_operations WHERE project_id=? ORDER BY created_at DESC', (project_id,))]
        for operation in operations:
            items = [dict(row) for row in conn.execute('SELECT * FROM repair_items WHERE operation_id=? ORDER BY rowid', (operation['id'],))]
            for item in items:
                item['checks'] = json.loads(item['checks'])
            operation['items'] = items
        return operations


def recover_interrupted():
    with closing(_conn()) as conn, conn:
        conn.execute("UPDATE repair_operations SET state='failed', error='Preparation was interrupted. Originals were not changed. Prepare a new preview.' WHERE state='preparing'")


def _markdown_check(text):
    fence = None
    for line in text.splitlines():
        match = re.match(r'^\s{0,3}(`{3,}|~{3,})(.*)$', line)
        if not match:
            continue
        marker, tail = match.groups()
        if fence is None:
            fence = marker
        elif marker[0] == fence[0] and len(marker) >= len(fence) and not tail.strip():
            fence = None
    if fence is not None:
        raise ValueError('The candidate has an unclosed Markdown code fence.')


def _candidate(conn, root, project_id, link, old, new):
    if old not in link['fact']:
        raise ValueError('This dependency quote does not contain the selected old value.')
    if link['target_kind'] == 'file':
        snapshot = read_text(root, link['target_id'])
        if snapshot['sha256'] != link['target_revision']:
            raise ValueError('The dependent file changed after link confirmation. Review it before preparing a repair.')
        raw = snapshot['raw']
        before = raw.decode('utf-8-sig')
        if before.count(old) != 1:
            raise ValueError('Expected exactly one occurrence of the old value. This file needs a manual edit or a narrower value.')
        after = before.replace(old, new, 1)
        encoded = (b'\xef\xbb\xbf' if raw.startswith(b'\xef\xbb\xbf') else b'') + after.encode('utf-8')
        if len(encoded) > MAX_TEXT_BYTES or '\x00' in after:
            raise ValueError('The candidate exceeds the supported text-file limits.')
        if Path(link['target_id']).suffix.lower() in {'.md', '.markdown'}:
            _markdown_check(after)
        return before, after, raw, encoded
    task = graph._task_snapshot(conn, project_id, link['target_id'])
    if task['revision'] != link['target_revision']:
        raise ValueError('The task changed after link confirmation. Review it before preparing a repair.')
    original = json.loads(task['revision'])
    candidate = dict(original)
    occurrences = original['title'].count(old) + int(original['due_at'] == old)
    if occurrences != 1:
        raise ValueError('Expected one exact old value in the task title or due date. No task change was inferred.')
    if original['due_at'] == old:
        candidate['due_at'] = new
    else:
        candidate['title'] = original['title'].replace(old, new, 1)
    TaskArguments(title=candidate['title'], due_at=candidate['due_at'])
    before, after = json.dumps(original, indent=2), json.dumps(candidate, indent=2)
    return before, after, before.encode(), after.encode()


def _staging(operation_id):
    base = data_path('repair-staging')
    base.mkdir(mode=0o700, exist_ok=True)
    if base.is_symlink() or base.resolve() != base.absolute():
        raise ValueError('The staging directory must be a regular folder inside this profile.')
    path = base / operation_id
    path.mkdir(mode=0o700, exist_ok=False)
    return path


def _write_copy(folder, name, content):
    # Names are operation/item UUIDs, never source paths or model arguments.
    with (folder / name).open('xb') as file:
        file.write(content)


def prepare(project_id, operation_id, source_id, old_value, new_value):
    operation_id = str(uuid.UUID(operation_id))
    old, new = old_value.strip(), new_value.strip()
    if not old or not new or old == new or max(len(old), len(new)) > 240 or any(ord(c) < 32 for c in old + new):
        raise ValueError('Choose two different, nonempty single-line values (up to 240 characters).')
    for value in (old, new):
        if re.fullmatch(r'\d{4}-\d{2}-\d{2}', value):
            date.fromisoformat(value)
    with closing(_conn()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        prior = conn.execute('SELECT * FROM repair_operations WHERE id=?', (operation_id,)).fetchone()
        if prior:
            if (prior['project_id'], prior['source_id'], prior['old_value'], prior['new_value']) != (project_id, source_id, old, new):
                raise ValueError('This preparation ID belongs to a different request.')
            return dict(prior)
        root = graph._root(conn, project_id)
        source = conn.execute('SELECT * FROM graph_sources WHERE id=? AND project_id=?', (source_id, project_id)).fetchone()
        if not source:
            raise ValueError('Source not found in this project.')
        current = read_text(root, source['path'])
        if source['sha256'] == current['sha256'] or old not in source['content'] or old in current['content'] or new not in current['content']:
            raise ValueError('The old value must occur in the captured source and be replaced by the new value in the current source.')
        links = conn.execute("SELECT * FROM graph_links WHERE project_id=? AND source_id=? AND state='confirmed'", (project_id, source_id)).fetchall()
        if not links:
            raise ValueError('Confirm at least one dependency before preparing repairs.')
        if conn.execute('SELECT COUNT(*) FROM repair_operations WHERE project_id=?', (project_id,)).fetchone()[0] >= 50:
            raise ValueError('This project has reached its 50 repair-preview limit.')
        staging = _staging(operation_id)
        conn.execute('INSERT INTO repair_operations VALUES (?,?,?,?,?,?,?,?,?,?,?)',
                     (operation_id, project_id, source_id, source['sha256'], current['sha256'], old, new, str(staging), 'preparing', None, graph._now()))
    # Persist preparation ownership before work begins. A crash leaves an
    # inspectable interrupted record; startup marks it failed. Never touch originals.
    try:
        with closing(_conn()) as conn, conn:
            conn.execute('BEGIN IMMEDIATE')
            _write_copy(staging, 'source-current.txt', current['raw'])
            _write_copy(staging, 'source-captured.txt', source['content'].encode())
            staged_bytes = len(current['raw']) + len(source['content'].encode())
            preview_bytes = conn.execute('''SELECT COALESCE(SUM(length(CAST(i.before_text AS BLOB)) +
                COALESCE(length(CAST(i.after_text AS BLOB)),0) + length(CAST(i.diff AS BLOB))),0)
                FROM repair_items i JOIN repair_operations o ON o.id=i.operation_id WHERE o.project_id=?''', (project_id,)).fetchone()[0]
            for link in links:
                item_id = str(uuid.uuid4())
                before, after, before_hash, after_hash, difference = '', None, None, None, ''
                try:
                    before, after, original, candidate = _candidate(conn, root, project_id, link, old, new)
                    if new not in after or old in after:
                        raise ValueError('The candidate did not replace the old value completely.')
                    before_hash = hashlib.sha256(original).hexdigest()
                    after_hash = hashlib.sha256(candidate).hexdigest()
                    difference = ''.join(difflib.unified_diff(before.splitlines(keepends=True), after.splitlines(keepends=True),
                                                             fromfile=link['target_id'] + ' (original)', tofile=link['target_id'] + ' (proposed)'))
                    record_bytes = len((before + after + difference).encode())
                    if staged_bytes + len(original) + len(candidate) > MAX_STAGED_BYTES or preview_bytes + record_bytes > MAX_PROJECT_PREVIEW_BYTES:
                        raise ValueError('Preview storage limit reached. This candidate was not staged (8 MiB per operation, 16 MiB of preview text per project).')
                    _write_copy(staging, f'{item_id}.before', original)
                    _write_copy(staging, f'{item_id}.after', candidate)
                    staged_bytes += len(original) + len(candidate)
                    preview_bytes += record_bytes
                    checks, validation = ['Source value verified', 'Target revision unchanged', 'Expected replacement verified', 'Supported text/task syntax checked'], 'passed'
                except (ValueError, OSError) as exc:
                    checks, validation = [str(exc)], 'blocked'
                    before, after, difference, before_hash, after_hash = '', None, '', None, None
                conn.execute('INSERT INTO repair_items (id, operation_id, link_id, target_kind, target_id, reason, fact, before_text, after_text, before_hash, after_hash, diff, validation, checks) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
                             (item_id, operation_id, link['id'], link['target_kind'], link['target_id'], link['reason'], link['fact'], before, after, before_hash, after_hash, difference, validation, json.dumps(checks)))
            graph._root(conn, project_id)
            if read_text(root, source['path'])['sha256'] != current['sha256']:
                raise ValueError('The supporting source changed during preparation. Prepare a new preview.')
            conn.execute("UPDATE repair_operations SET state='ready' WHERE id=?", (operation_id,))
    except Exception as exc:
        with closing(_conn()) as conn, conn:
            conn.execute("UPDATE repair_operations SET state='failed', error=? WHERE id=?", (str(exc), operation_id))
        raise
    return next(operation for operation in list_operations(project_id) if operation['id'] == operation_id)


def select_item(project_id, operation_id, item_id, selection):
    if selection not in {'accepted', 'rejected'}:
        raise ValueError('Choose accepted or rejected.')
    from app.repair_apply import _conn as apply_conn
    with closing(apply_conn()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        if conn.execute('SELECT 1 FROM repair_writes WHERE item_id=?', (item_id,)).fetchone():
            raise ValueError('This item has a write receipt. Review recovery instead of changing its selection.')
        row = conn.execute('SELECT i.*, o.state AS operation_state FROM repair_items i JOIN repair_operations o ON o.id=i.operation_id WHERE i.id=? AND o.id=? AND o.project_id=?',
                           (item_id, operation_id, project_id)).fetchone()
        if not row or row['operation_state'] != 'ready':
            raise ValueError('This preview is not ready for review.')
        if selection == 'accepted' and row['validation'] != 'passed':
            raise ValueError('A blocked candidate cannot be accepted.')
        conn.execute('UPDATE repair_items SET selection=? WHERE id=?', (selection, item_id))
        return {'id': item_id, 'selection': selection, 'applied': False}
