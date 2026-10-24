"""Explicit, per-item repair writes with durable recovery receipts.

Windows file handles deny concurrent writes/deletes. POSIX file apply is kept
disabled: advisory locks cannot protect against an unrelated editor's writes.
Task updates and their journal entry commit in one SQLite transaction.
"""
import hashlib
import json
import os
import stat
from contextlib import ExitStack, closing, contextmanager
from pathlib import Path

from app import project_graph as graph, project_repairs as repairs
from app.project_files import MAX_TEXT_BYTES, _windows_final_path, read_text, relative_name


def capabilities():
    enabled = os.environ.get('LOCALMIND_REPAIR_APPLY', '1') == '1'
    return {'tasks': enabled, 'files': enabled and os.name == 'nt',
            'reason': ('Repair apply is disabled by LOCALMIND_REPAIR_APPLY.' if not enabled else
                       'File apply is available on Windows only; Mac/Linux remain preview-only.' if os.name != 'nt' else
                       'Apply one accepted item at a time. Backups and receipts stay in this local profile.')}


def _conn():
    conn = repairs._conn()
    conn.executescript('''CREATE TABLE IF NOT EXISTS repair_writes (
        item_id TEXT PRIMARY KEY REFERENCES repair_items(id) ON DELETE CASCADE,
        project_id TEXT NOT NULL, operation_id TEXT NOT NULL,
        state TEXT NOT NULL, intent TEXT NOT NULL, before_data BLOB NOT NULL,
        after_data BLOB NOT NULL, before_hash TEXT NOT NULL, after_hash TEXT NOT NULL,
        error TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
    );''')
    return conn


def _public(row):
    return {key: value for key, value in dict(row).items() if key not in {'before_data', 'after_data'}}


def receipts(project_id):
    with closing(_conn()) as conn:
        return [_public(row) for row in conn.execute('SELECT * FROM repair_writes WHERE project_id=?', (project_id,))]


def recover_interrupted():
    with closing(_conn()) as conn, conn:
        conn.execute("UPDATE repair_writes SET state='recovery_required', error='Interrupted write. Check recovery before retrying.' WHERE state IN ('writing','restoring')")


def _hash(data):
    return hashlib.sha256(data).hexdigest()


def _item(conn, project_id, operation_id, item_id):
    row = conn.execute('''SELECT i.*, o.source_id, o.source_after, o.staging_path, o.state AS operation_state
        FROM repair_items i JOIN repair_operations o ON o.id=i.operation_id
        WHERE i.id=? AND o.id=? AND o.project_id=?''', (item_id, operation_id, project_id)).fetchone()
    if not row:
        raise ValueError('Repair item not found in this project.')
    if row['operation_state'] != 'ready' or row['validation'] != 'passed' or row['selection'] != 'accepted':
        raise ValueError('Only an accepted, validated preview can be applied.')
    return row


def _allowed(kind):
    flags = capabilities()
    if not flags['tasks' if kind == 'task' else 'files']:
        raise ValueError(flags['reason'])


@contextmanager
def locked_file(root, relative, writable=False):
    """Keep parent directories stable and deny other write/delete handles."""
    if os.name != 'nt':
        raise ValueError('File apply requires Windows exclusive file handles.')
    import ctypes
    import msvcrt
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                       wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    close = kernel.CloseHandle
    close.argtypes = [wintypes.HANDLE]
    close.restype = wintypes.BOOL
    name = relative_name(relative)
    candidate = root.joinpath(*name.split('/'))
    if candidate.resolve(strict=True) != candidate:
        raise ValueError('Linked paths cannot be modified.')
    handles, fd = [], None
    try:
        # Hold all ancestors against renaming while the file is open.
        for directory in reversed(candidate.parents):
            handle = create(str(directory), 0x80000000, 3, None, 3, 0x02200000, None)
            if handle == wintypes.HANDLE(-1).value:
                raise ctypes.WinError(ctypes.get_last_error())
            handles.append(handle)
        handle = create(str(candidate), 0x80000000 | (0x40000000 if writable else 0), 1, None, 3, 0x00200000, None)
        if handle == wintypes.HANDLE(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            fd = msvcrt.open_osfhandle(handle, (os.O_RDWR if writable else os.O_RDONLY) | os.O_BINARY)
        except BaseException:
            close(handle)
            raise
        info = os.fstat(fd)
        if (_windows_final_path(fd) != candidate or candidate.resolve(strict=True) != candidate
                or not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > MAX_TEXT_BYTES
                or getattr(info, 'st_file_attributes', 0) & 0x400):
            raise ValueError('Choose an unchanged regular file with no links, inside the approved folder.')
        with os.fdopen(fd, 'r+b' if writable else 'rb', closefd=False) as file:
            yield file
    finally:
        if fd is not None:
            os.close(fd)
        for handle in reversed(handles):
            close(handle)


def _bytes(file):
    file.seek(0)
    value = file.read(MAX_TEXT_BYTES + 1)
    if len(value) > MAX_TEXT_BYTES:
        raise ValueError('File exceeds the repair size limit.')
    return value


def _write(file, data):
    file.seek(0)
    file.write(data)
    file.truncate()
    file.flush()
    os.fsync(file.fileno())
    if _bytes(file) != data:
        raise OSError('Written file failed verification; check recovery.')


def _staged(item):
    # Preview bytes are not trusted merely because they are in the profile.
    folder = Path(item['staging_path'])
    from app.storage import data_path
    expected = data_path('repair-staging') / item['operation_id']
    if folder != expected or folder.resolve(strict=True) != expected.absolute():
        raise ValueError('Staging directory changed; prepare a new preview.')
    result = []
    for suffix, digest in [('before', item['before_hash']), ('after', item['after_hash'])]:
        path = folder / f"{item['id']}.{suffix}"
        if path.resolve(strict=True) != path or not path.is_file() or path.stat().st_size > MAX_TEXT_BYTES:
            raise ValueError('Invalid staged copy.')
        with path.open('rb') as file:
            data = file.read(MAX_TEXT_BYTES + 1)
        if _hash(data) != digest:
            raise ValueError('Staged copy changed; prepare a new preview.')
        result.append(data)
    return result


def _source(conn, root, item, stack):
    source = conn.execute('SELECT path FROM graph_sources WHERE id=?', (item['source_id'],)).fetchone()
    if not source:
        raise ValueError('Supporting source no longer exists.')
    if os.name == 'nt':
        data = _bytes(stack.enter_context(locked_file(root, source['path'])))
        digest = _hash(data)
    else:
        digest = read_text(root, source['path'])['sha256']
    if digest != item['source_after']:
        raise ValueError('Supporting source changed after preview. Prepare a new preview.')


def _journal(conn, project_id, item, before, after):
    now = graph._now()
    conn.execute('INSERT INTO repair_writes VALUES (?,?,?,?,?,?,?,?,?,?,?,?)',
                 (item['id'], project_id, item['operation_id'], 'writing', 'apply', before, after,
                  _hash(before), _hash(after), None, now, now))


def _mark(conn, item_id, state, error=None):
    conn.execute('UPDATE repair_writes SET state=?, error=?, updated_at=? WHERE item_id=?',
                 (state, error, graph._now(), item_id))


def _task_bytes(conn, project_id, target_id):
    revision = json.loads(graph._task_snapshot(conn, project_id, target_id)['revision'])
    return json.dumps(revision, indent=2).encode()


def _update_task(conn, project_id, target_id, data):
    task = json.loads(data)
    conn.execute('UPDATE tasks SET title=?, due_at=?, status=?, updated_at=? WHERE id=? AND project_id=?',
                 (task['title'], task['due_at'], task['status'], graph._now(), target_id, project_id))


def apply_item(project_id, operation_id, item_id):
    with ExitStack() as stack, closing(_conn()) as conn:
        conn.execute('BEGIN IMMEDIATE')
        item = _item(conn, project_id, operation_id, item_id)
        _allowed(item['target_kind'])
        prior = conn.execute('SELECT * FROM repair_writes WHERE item_id=?', (item_id,)).fetchone()
        if prior:
            return _public(prior)  # Even a restored item never silently reapplies.
        root = graph._root(conn, project_id)
        _source(conn, root, item, stack)
        before, after = _staged(item)
        if item['target_kind'] == 'task':
            if _task_bytes(conn, project_id, item['target_id']) != before:
                raise ValueError('Task changed after preview. Nothing was applied.')
            _journal(conn, project_id, item, before, after)
            _update_task(conn, project_id, item['target_id'], after)
            _mark(conn, item_id, 'applied')
            conn.commit()
        else:
            file = stack.enter_context(locked_file(root, item['target_id'], writable=True))
            if _bytes(file) != before:
                raise ValueError('File changed after preview. Nothing was applied.')
            graph._root(conn, project_id)
            _journal(conn, project_id, item, before, after)
            conn.commit()  # Full recoverable backup is durable before any file write.
            try:
                conn.execute('BEGIN IMMEDIATE')
                graph._root(conn, project_id)
                _write(file, after)
                _mark(conn, item_id, 'applied')
                conn.commit()
            except BaseException as exc:
                conn.rollback()
                if isinstance(exc, Exception):
                    _mark(conn, item_id, 'recovery_required', str(exc))
                    conn.commit()
                # Abrupt exits leave 'writing'; startup marks it for recovery.
                raise
        return _public(conn.execute('SELECT * FROM repair_writes WHERE item_id=?', (item_id,)).fetchone())


def recover_item(project_id, operation_id, item_id, restore=False):
    with ExitStack() as stack, closing(_conn()) as conn:
        conn.execute('BEGIN IMMEDIATE')
        item = _item(conn, project_id, operation_id, item_id)
        row = conn.execute('SELECT * FROM repair_writes WHERE item_id=? AND project_id=?', (item_id, project_id)).fetchone()
        if not row:
            raise ValueError('No saved write receipt exists for this item.')
        if restore:
            _allowed(item['target_kind'])
        root = graph._root(conn, project_id)
        file = None
        if item['target_kind'] == 'file':
            file = stack.enter_context(locked_file(root, item['target_id'], writable=restore))
            current = _bytes(file)
        else:
            current = _task_bytes(conn, project_id, item['target_id'])
        if _hash(row['before_data']) != row['before_hash'] or _hash(row['after_data']) != row['after_hash']:
            raise ValueError('Recovery backup integrity check failed.')
        if current == row['before_data']:
            _mark(conn, item_id, 'restored' if restore or row['intent'] == 'restore' else 'not_applied')
        elif current != row['after_data']:
            _mark(conn, item_id, 'conflict', 'Current content matches neither saved revision. No overwrite performed; recover manually from the saved before/after preview.')
        elif not restore:
            _mark(conn, item_id, 'applied')
        elif item['target_kind'] == 'task':
            _update_task(conn, project_id, item['target_id'], row['before_data'])
            conn.execute("UPDATE repair_writes SET intent='restore' WHERE item_id=?", (item_id,))
            _mark(conn, item_id, 'restored')
        else:
            conn.execute("UPDATE repair_writes SET state='restoring', intent='restore', error=NULL WHERE item_id=?", (item_id,))
            conn.commit()
            conn.execute('BEGIN IMMEDIATE')
            _write(file, row['before_data'])
            _mark(conn, item_id, 'restored')
        conn.commit()
        return _public(conn.execute('SELECT * FROM repair_writes WHERE item_id=?', (item_id,)).fetchone())
