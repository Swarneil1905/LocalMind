"""Ten synthetic repair journeys. No user's workspace or model is touched."""
import argparse
from contextlib import closing
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'services/ai'))
from app import project_graph as graph, project_repairs as repairs, repair_apply as writes  # noqa: E402
from app.db import projects  # noqa: E402

CASES = ['apply_task', 'retry_task', 'stale_task', 'stale_source', 'disabled',
         'restore_task', 'restore_conflict', 'apply_file', 'crash_before_file', 'crash_after_file']


def run_case(name):
    record = {'case': name, 'expected_affected': ['announcement.md', 'Publish announcement']}
    with tempfile.TemporaryDirectory(prefix='localmind-repair-gate-') as directory:
        base = Path(directory).resolve()
        root = base / 'work'
        root.mkdir()
        source, target = root / 'source.md', root / 'announcement.md'
        source.write_text('Launch: 2026-11-18\n', encoding='utf-8')
        before = b'Launch on 2026-11-18.\r\n'
        target.write_bytes(before)
        with patch.dict(os.environ, {'LOCALMIND_DATA_DIR': str(base / 'profile'), 'LOCALMIND_REPAIR_APPLY': '1'}):
            project = projects.create_project('Synthetic launch', str(root))['id']
            task = projects.create_task(project, 'Publish announcement', '2026-11-18')
            graph.approve_root(project)
            captured = graph.add_source(project, 'source.md')
            for kind, destination in [('file', 'announcement.md'), ('task', task['id'])]:
                link = graph.propose_link(project, captured['id'], 'Launch: 2026-11-18', 'Launch schedule', kind, destination)
                graph.review_link(project, link['id'], 'confirm')
            source.write_text('Launch: 2026-11-25\n', encoding='utf-8')
            affected = graph.rescan(project)
            record['observed_affected'] = sorted('Publish announcement' if link['target_kind'] == 'task' else link['target_id']
                                                 for link in affected['links'] if link['needs_review'])
            assert record['observed_affected'] == sorted(record['expected_affected'])
            preview = repairs.prepare(project, str(uuid.uuid4()), captured['id'], '2026-11-18', '2026-11-25')
            kind = 'file' if name.endswith('file') else 'task'
            item = next(item for item in preview['items'] if item['target_kind'] == kind)
            repairs.select_item(project, preview['id'], item['id'], 'accepted')
            arguments = (project, preview['id'], item['id'])
            if kind == 'file' and not writes.capabilities()['files']:
                try:
                    writes.apply_item(*arguments)
                except ValueError:
                    assert target.read_bytes() == before
                    record.update(state='deferred', expected='file apply blocked', observed='blocked; original unchanged')
                    return record
                raise AssertionError('File capability boundary did not block apply')
            if name == 'stale_task':
                with closing(projects._get_conn()) as conn, conn:
                    conn.execute("UPDATE tasks SET title='User edit' WHERE id=?", (task['id'],))
            if name == 'stale_source':
                source.write_text('Launch: 2026-12-01\n', encoding='utf-8')
            if name in {'disabled', 'stale_task', 'stale_source'}:
                with patch.dict(os.environ, {'LOCALMIND_REPAIR_APPLY': '0' if name == 'disabled' else '1'}):
                    try:
                        writes.apply_item(*arguments)
                    except ValueError:
                        assert projects.list_tasks(project)[0]['due_at'] == '2026-11-18'
                        assert target.read_bytes() == before
                        record.update(state='passed', expected='blocked without mutation', observed='blocked without mutation')
                        return record
                    raise AssertionError('Expected a blocked repair')
            if name.startswith('crash_'):
                # A real child-process exit, not an exception that runs cleanup.
                script = '''import os,sys
from app import repair_apply as w
original=w._write
def stop(file,data):
    if sys.argv[4]=='crash_after_file': original(file,data)
    os._exit(71)
w._write=stop
w.apply_item(*sys.argv[1:4])
'''
                env = dict(os.environ, PYTHONPATH=str(REPO / 'services/ai') + os.pathsep + os.environ.get('PYTHONPATH', ''))
                result = subprocess.run([sys.executable, '-c', script, *arguments, name], env=env, timeout=30,
                                        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0, check=False)
                assert result.returncode == 71
                writes.recover_interrupted()
                receipt = writes.recover_item(*arguments)
                expected = 'applied' if name == 'crash_after_file' else 'not_applied'
                assert receipt['state'] == expected
                assert target.read_bytes() == (before.replace(b'2026-11-18', b'2026-11-25') if expected == 'applied' else before)
                record.update(state='passed', expected=expected, observed=receipt['state'])
                return record
            receipt = writes.apply_item(*arguments)
            assert receipt['state'] == 'applied'
            if name == 'retry_task':
                updated = projects.list_tasks(project)
                assert writes.apply_item(*arguments) == receipt
                assert projects.list_tasks(project) == updated
            expected = 'applied'
            if name.startswith('restore'):
                if name == 'restore_conflict':
                    with closing(projects._get_conn()) as conn, conn:
                        conn.execute("UPDATE tasks SET title='Later edit' WHERE id=?", (task['id'],))
                receipt = writes.recover_item(*arguments, restore=True)
                expected = 'conflict' if name == 'restore_conflict' else 'restored'
                assert receipt['state'] == expected
            current = projects.list_tasks(project)[0]
            assert current['due_at'] == ('2026-11-18' if kind == 'file' or expected == 'restored' else '2026-11-25')
            assert target.read_bytes() == (before.replace(b'2026-11-18', b'2026-11-25') if kind == 'file' else before)
            record.update(state='passed', expected=expected, observed=receipt['state'])
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, default=REPO / 'artifacts/evaluation/workspace-repair-gate.json')
    args = parser.parse_args()
    results = []
    for name in CASES:
        try:
            results.append(run_case(name))
        except Exception as exc:
            results.append({'case': name, 'state': 'failed', 'error': repr(exc)})
    failed = any(item['state'] == 'failed' for item in results)
    report = {'platform': sys.platform, 'capabilities': writes.capabilities(), 'passed': not failed, 'fixtures': results,
              'disposition': 'Windows files and local tasks enabled; Mac/Linux file apply deferred; runtime kill switch enforced.',
              'scope': 'Synthetic repair journey only. Run the full service suite for knowledge isolation and chat task creation.'}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'passed': not failed, 'fixtures': len(results), 'deferred': sum(item['state'] == 'deferred' for item in results), 'report': str(args.out)}))
    raise SystemExit(1 if failed else 0)


if __name__ == '__main__':
    main()
