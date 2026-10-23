import hashlib
import os
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

from app import project_graph as graph, project_repairs as repairs
from app.db import projects


class ProjectRepairTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name).resolve()
        self.env = patch.dict(os.environ, {'LOCALMIND_DATA_DIR': str(self.base / 'profile')})
        self.env.start()
        self.root = self.base / 'work'
        self.root.mkdir()
        self.source_file = self.root / 'source.md'
        self.target_file = self.root / 'announcement.md'
        self.source_file.write_text('Launch: 2026-11-18\n', encoding='utf-8')
        self.target_file.write_bytes(b'\xef\xbb\xbf# Announcement\r\nLaunch on 2026-11-18.\r\n')
        self.pid = projects.create_project('Pilot', str(self.root))['id']
        self.task = projects.create_task(self.pid, 'Publish announcement', '2026-11-18')
        graph.approve_root(self.pid)
        self.source = graph.add_source(self.pid, 'source.md')
        self.links = []
        for kind, target in [('file', 'announcement.md'), ('task', self.task['id'])]:
            link = graph.propose_link(self.pid, self.source['id'], 'Launch: 2026-11-18', 'Launch timing', kind, target)
            graph.review_link(self.pid, link['id'], 'confirm')
            self.links.append(link)
        self.source_file.write_text('Launch: 2026-11-25\n', encoding='utf-8')
        self.before_file = self.target_file.read_bytes()
        self.before_tasks = projects.list_tasks(self.pid)

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def prepare(self, operation=None):
        return repairs.prepare(self.pid, operation or str(uuid.uuid4()), self.source['id'], '2026-11-18', '2026-11-25')

    def unchanged(self):
        self.assertEqual(self.target_file.read_bytes(), self.before_file)
        self.assertEqual(projects.list_tasks(self.pid), self.before_tasks)

    def test_copies_validate_and_show_file_and_task_diffs_without_changing_originals(self):
        result = self.prepare()
        self.assertEqual(result['state'], 'ready')
        self.assertEqual(len(result['items']), 2)
        folder = Path(result['staging_path'])
        self.assertTrue(folder.is_relative_to(self.base / 'profile/repair-staging'))
        for item in result['items']:
            self.assertEqual(item['validation'], 'passed')
            self.assertIn('-' , item['diff'])
            self.assertIn('2026-11-25', item['after_text'])
            self.assertNotIn('2026-11-18', item['after_text'])
            before = (folder / f"{item['id']}.before").read_bytes()
            after = (folder / f"{item['id']}.after").read_bytes()
            self.assertEqual(hashlib.sha256(before).hexdigest(), item['before_hash'])
            self.assertEqual(hashlib.sha256(after).hexdigest(), item['after_hash'])
            if item['target_kind'] == 'file':
                self.assertEqual(before, self.before_file)
                self.assertIn(b'\r\n', after)
                self.assertTrue(after.startswith(b'\xef\xbb\xbf'))
        self.unchanged()

    def test_selective_review_and_retry_keep_one_operation_and_no_apply(self):
        op = str(uuid.uuid4())
        result = self.prepare(op)
        self.prepare(op)
        self.assertEqual(len(repairs.list_operations(self.pid)), 1)
        first, second = result['items']
        receipt = repairs.select_item(self.pid, op, first['id'], 'accepted')
        self.assertFalse(receipt['applied'])
        repairs.select_item(self.pid, op, second['id'], 'rejected')
        saved = repairs.list_operations(self.pid)[0]
        self.assertEqual([item['selection'] for item in saved['items']], ['accepted', 'rejected'])
        self.unchanged()

    def test_changed_target_blocks_only_that_candidate_and_cannot_be_accepted(self):
        self.target_file.write_text('A separate concurrent edit', encoding='utf-8')
        changed = self.target_file.read_bytes()
        result = self.prepare()
        file_item = next(item for item in result['items'] if item['target_kind'] == 'file')
        self.assertEqual(file_item['validation'], 'blocked')
        self.assertIsNone(file_item['after_text'])
        with self.assertRaises(ValueError):
            repairs.select_item(self.pid, result['id'], file_item['id'], 'accepted')
        self.assertEqual(self.target_file.read_bytes(), changed)
        self.assertEqual(projects.list_tasks(self.pid), self.before_tasks)

    def test_unverified_values_and_cross_project_requests_cannot_prepare(self):
        other = projects.create_project('Other', str(self.root))
        for pid, old, new in [(self.pid, '2026-11-18', '2026-12-01'), (self.pid, 'unknown', '2026-11-25'),
                              (other['id'], '2026-11-18', '2026-11-25'), (self.pid, '2026-11-18', '2026-02-30')]:
            with self.assertRaises(ValueError):
                repairs.prepare(pid, str(uuid.uuid4()), self.source['id'], old, new)
        self.assertEqual(repairs.list_operations(self.pid), [])
        self.unchanged()

    def test_staging_failure_leaves_inspectable_failure_and_originals_intact(self):
        with patch('app.project_repairs._write_copy', side_effect=OSError('Disk full')):
            with self.assertRaises(OSError):
                self.prepare()
        result = repairs.list_operations(self.pid)[0]
        self.assertEqual(result['state'], 'failed')
        self.assertIn('Disk full', result['error'])
        self.unchanged()

    def test_interrupted_preparation_is_marked_failed_on_restart(self):
        with patch('app.project_repairs._write_copy', side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                self.prepare()
        self.assertEqual(repairs.list_operations(self.pid)[0]['state'], 'preparing')
        repairs.recover_interrupted()
        self.assertEqual(repairs.list_operations(self.pid)[0]['state'], 'failed')
        self.unchanged()

    def test_source_change_during_preparation_invalidates_the_whole_preview(self):
        original = repairs._write_copy
        def change(folder, name, content):
            original(folder, name, content)
            if name == 'source-current.txt':
                self.source_file.write_text('Launch: 2026-12-02\n', encoding='utf-8')
        with patch('app.project_repairs._write_copy', side_effect=change), self.assertRaises(ValueError):
            self.prepare()
        self.assertEqual(repairs.list_operations(self.pid)[0]['state'], 'failed')
        self.unchanged()

    def test_markdown_validation_rejects_unbalanced_fences(self):
        with self.assertRaises(ValueError):
            repairs._markdown_check('```python\nprint(1)\n')
        repairs._markdown_check('~~~python\nprint(1)\n~~~\n')

    def test_preview_budget_blocks_candidates_without_mutating_originals(self):
        with patch('app.project_repairs.MAX_PROJECT_PREVIEW_BYTES', 10):
            result = self.prepare()
        self.assertTrue(all(item['validation'] == 'blocked' for item in result['items']))
        self.assertEqual(len(list(Path(result['staging_path']).glob('*.after'))), 0)
        self.unchanged()

    def test_unsupported_target_explains_block_instead_of_writing_it(self):
        # Simulate a future/legacy graph row with a format this pilot cannot edit.
        conn = graph._conn()
        conn.execute("UPDATE graph_links SET target_id='announcement.pdf' WHERE id=?", (self.links[0]['id'],))
        conn.commit()
        conn.close()
        result = self.prepare()
        item = next(item for item in result['items'] if item['target_kind'] == 'file')
        self.assertEqual(item['validation'], 'blocked')
        self.assertIn('supports UTF-8', item['checks'][0])
        self.unchanged()
