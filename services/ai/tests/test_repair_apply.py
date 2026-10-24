import asyncio
import json
import os
import sqlite3
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from unittest.mock import patch

import httpx
from fastapi import FastAPI
import test_project_repairs as fixtures
from app import repair_apply as writes, project_repairs as repairs
from app.db import projects
from app.api.routes import project_graph as routes


class RepairApplyTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.ProjectRepairTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        self.env = patch.dict(os.environ, {'LOCALMIND_REPAIR_APPLY': '1'})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.operation = self.fixture.prepare()
        self.items = {item['target_kind']: item for item in self.operation['items']}
        for item in self.items.values():
            repairs.select_item(self.fixture.pid, self.operation['id'], item['id'], 'accepted')

    def apply(self, kind='task'):
        return writes.apply_item(self.fixture.pid, self.operation['id'], self.items[kind]['id'])

    def recover(self, kind='task', restore=False):
        return writes.recover_item(self.fixture.pid, self.operation['id'], self.items[kind]['id'], restore=restore)

    def test_task_apply_replay_and_restore_are_transactional(self):
        receipt = self.apply()
        self.assertEqual(receipt['state'], 'applied')
        after = projects.list_tasks(self.fixture.pid)
        self.assertEqual(after[0]['due_at'], '2026-11-25')
        self.assertEqual(self.apply(), receipt)
        self.assertEqual(projects.list_tasks(self.fixture.pid), after)
        self.assertEqual(self.recover(restore=True)['state'], 'restored')
        self.assertEqual(projects.list_tasks(self.fixture.pid)[0]['due_at'], '2026-11-18')
        self.assertEqual(self.apply()['state'], 'restored')
        with self.assertRaises(ValueError):
            repairs.select_item(self.fixture.pid, self.operation['id'], self.items['task']['id'], 'rejected')

    def test_concurrent_confirmation_updates_task_once(self):
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: self.apply(), range(2)))
        self.assertEqual(results[0], results[1])
        self.assertEqual(len(writes.receipts(self.fixture.pid)), 1)

    def test_stale_source_blocks_apply_without_journal_or_task_change(self):
        self.fixture.source_file.write_text('Launch: 2026-12-01', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Supporting source changed'):
            self.apply()
        self.fixture.unchanged()
        self.assertEqual(writes.receipts(self.fixture.pid), [])

    def test_stale_task_blocks_apply_and_restore_preserves_later_edit(self):
        with closing(sqlite3.connect(projects._db_path())) as conn, conn:
            conn.execute("UPDATE tasks SET title='Edited by user'")
        with self.assertRaisesRegex(ValueError, 'Task changed'):
            self.apply()
        self.assertEqual(writes.receipts(self.fixture.pid), [])

    def test_restore_does_not_overwrite_newer_task_changes(self):
        self.apply()
        with closing(sqlite3.connect(projects._db_path())) as conn, conn:
            conn.execute("UPDATE tasks SET title='Later edit'")
        self.assertEqual(self.recover(restore=True)['state'], 'conflict')
        self.assertEqual(projects.list_tasks(self.fixture.pid)[0]['title'], 'Later edit')

    def test_unaccepted_cross_project_and_disabled_requests_do_not_write(self):
        item = self.items['task']
        repairs.select_item(self.fixture.pid, self.operation['id'], item['id'], 'rejected')
        with self.assertRaises(ValueError):
            self.apply()
        repairs.select_item(self.fixture.pid, self.operation['id'], item['id'], 'accepted')
        other = projects.create_project('Other', str(self.fixture.root))
        with self.assertRaises(ValueError):
            writes.apply_item(other['id'], self.operation['id'], item['id'])
        with patch.dict(os.environ, {'LOCALMIND_REPAIR_APPLY': '0'}):
            with self.assertRaisesRegex(ValueError, 'disabled'):
                self.apply()
        self.fixture.unchanged()

    def test_api_requires_explicit_confirmation_and_honors_disabled_gate(self):
        async def run():
            app = FastAPI()
            app.include_router(routes.router)
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://test') as client:
                path = f'/projects/{self.fixture.pid}/graph/apply'
                payload = {'operation_id': self.operation['id'], 'item_id': self.items['task']['id']}
                self.assertEqual((await client.post(path, json=payload)).status_code, 422)
                payload['confirm'] = False
                self.assertEqual((await client.post(path, json=payload)).status_code, 422)
                payload['confirm'] = True
                with patch.dict(os.environ, {'LOCALMIND_REPAIR_APPLY': '0'}):
                    self.assertEqual((await client.post(path, json=payload)).status_code, 409)
                self.assertEqual((await client.post(path, json=payload)).status_code, 200)
        asyncio.run(run())

    def test_task_db_failure_rolls_back_update_and_receipt(self):
        with closing(sqlite3.connect(projects._db_path())) as conn, conn:
            conn.execute("CREATE TRIGGER reject_task BEFORE UPDATE ON tasks BEGIN SELECT RAISE(ABORT, 'fixture failure'); END")
        with self.assertRaises(sqlite3.IntegrityError):
            self.apply()
        self.fixture.unchanged()
        self.assertEqual(writes.receipts(self.fixture.pid), [])

    def test_tampered_staged_bytes_are_rejected(self):
        staged = self.fixture.base / 'profile/repair-staging' / self.operation['id'] / (self.items['task']['id'] + '.after')
        staged.write_text(json.dumps({'title': 'Injected task'}), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Staged copy changed'):
            self.apply()
        self.fixture.unchanged()

    @unittest.skipUnless(os.name == 'nt', 'Windows exclusive handles; other systems are preview-only')
    def test_file_apply_preserves_bom_newlines_and_can_restore(self):
        self.assertEqual(self.apply('file')['state'], 'applied')
        self.assertEqual(self.fixture.target_file.read_bytes(), self.fixture.before_file.replace(b'2026-11-18', b'2026-11-25'))
        self.assertEqual(self.apply('file')['state'], 'applied')
        self.assertEqual(self.recover('file', restore=True)['state'], 'restored')
        self.fixture.unchanged()

    @unittest.skipUnless(os.name == 'nt', 'Windows exclusive handles')
    def test_file_and_source_handles_deny_competing_writes(self):
        original = writes._write
        def compete(file, data):
            for path in (self.fixture.target_file, self.fixture.source_file):
                with self.assertRaises(OSError):
                    with path.open('wb') as other:
                        other.write(b'concurrent edit')
            original(file, data)
        with patch.object(writes, '_write', side_effect=compete):
            self.apply('file')

    @unittest.skipUnless(os.name == 'nt', 'Windows exclusive handles')
    def test_stale_file_and_hardlink_are_rejected(self):
        self.fixture.target_file.write_text('Later user edit', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'File changed'):
            self.apply('file')
        self.fixture.target_file.write_bytes(self.fixture.before_file)
        os.link(self.fixture.target_file, self.fixture.root / 'other.md')
        with self.assertRaisesRegex(ValueError, 'no links'):
            self.apply('file')
        self.fixture.unchanged()

    @unittest.skipUnless(os.name == 'nt', 'Windows exclusive handles')
    def test_interruption_before_after_and_during_write_is_reported_honestly(self):
        for moment, expected in [('before', 'not_applied'), ('after', 'applied'), ('partial', 'conflict')]:
            with self.subTest(moment=moment):
                self.fixture.target_file.write_bytes(self.fixture.before_file)
                operation = self.fixture.prepare()
                item = next(item for item in operation['items'] if item['target_kind'] == 'file')
                repairs.select_item(self.fixture.pid, operation['id'], item['id'], 'accepted')
                original = writes._write
                def interrupt(file, data):
                    if moment == 'after':
                        original(file, data)
                    elif moment == 'partial':
                        file.seek(0)
                        file.write(b'Partial fixture')
                        file.flush()
                        os.fsync(file.fileno())
                    raise KeyboardInterrupt('simulated process interruption')
                with patch.object(writes, '_write', side_effect=interrupt), self.assertRaises(KeyboardInterrupt):
                    writes.apply_item(self.fixture.pid, operation['id'], item['id'])
                writes.recover_interrupted()
                receipt = writes.recover_item(self.fixture.pid, operation['id'], item['id'])
                self.assertEqual(receipt['state'], expected)
                # Persisted full backup always survives, even when current bytes are ambiguous.
                with closing(writes._conn()) as conn:
                    saved = conn.execute('SELECT before_data FROM repair_writes WHERE item_id=?', (item['id'],)).fetchone()[0]
                    self.assertEqual(saved, self.fixture.before_file)

    def test_non_windows_file_gate_is_explicit(self):
        with patch.object(writes.os, 'name', 'posix'):
            self.assertFalse(writes.capabilities()['files'])
            with self.assertRaisesRegex(ValueError, 'Windows only'):
                writes._allowed('file')

    @unittest.skipUnless(os.name == 'nt', 'Windows exclusive handles')
    def test_write_failure_keeps_recovery_receipt_and_restore_interruption_is_reconciled(self):
        with patch.object(writes, '_write', side_effect=OSError('Disk full')), self.assertRaises(OSError):
            self.apply('file')
        self.assertEqual(writes.receipts(self.fixture.pid)[0]['state'], 'recovery_required')
        self.assertEqual(self.recover('file')['state'], 'not_applied')
        self.fixture.unchanged()
        # A different accepted preview can be applied after the failed attempt.
        operation = self.fixture.prepare()
        item = next(item for item in operation['items'] if item['target_kind'] == 'file')
        repairs.select_item(self.fixture.pid, operation['id'], item['id'], 'accepted')
        args = (self.fixture.pid, operation['id'], item['id'])
        writes.apply_item(*args)
        original = writes._write
        def stop(file, data):
            original(file, data)
            raise KeyboardInterrupt('restore interrupted after write')
        with patch.object(writes, '_write', side_effect=stop), self.assertRaises(KeyboardInterrupt):
            writes.recover_item(*args, restore=True)
        writes.recover_interrupted()
        self.assertEqual(writes.recover_item(*args)['state'], 'restored')
        self.fixture.unchanged()
