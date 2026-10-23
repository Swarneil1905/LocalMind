import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import httpx
from fastapi import FastAPI
from app import project_graph as graph
from app.project_files import read_text, MAX_TEXT_BYTES
from app.db import projects
from app.api.routes import project_graph as routes


class ProjectGraphTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name).resolve()
        self.env = patch.dict(os.environ, {'LOCALMIND_DATA_DIR': str(self.base / 'profile')})
        self.env.start()
        self.root = self.base / 'work'
        self.root.mkdir()
        (self.root / 'source.md').write_text('Launch date: November 18\nOwner: Alex\n', encoding='utf-8')
        (self.root / 'draft.md').write_text('Launch announcement draft', encoding='utf-8')
        self.project = projects.create_project('Launch', str(self.root))
        self.pid = self.project['id']
        self.task = projects.create_task(self.pid, 'Publish announcement', '2026-11-18')

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def source(self):
        graph.approve_root(self.pid)
        return graph.add_source(self.pid, 'source.md')

    def link(self, source, kind='file'):
        return graph.propose_link(self.pid, source['id'], 'Launch date: November 18',
                                  'Announcement must use the agreed launch date', kind,
                                  'draft.md' if kind == 'file' else self.task['id'])

    def test_explicit_approval_and_supported_relative_paths_required(self):
        with self.assertRaises(ValueError):
            graph.add_source(self.pid, 'source.md')
        self.source()
        for path in ('../outside.md', str(self.root / 'source.md'), 'source.md:secret',
                     'nested/../../source.md', './source.md', 'report.pdf', 'folder//source.md'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                graph.add_source(self.pid, path)

    def test_quote_and_targets_must_belong_to_same_project(self):
        source = self.source()
        other = projects.create_project('Other', str(self.root))
        foreign = projects.create_task(other['id'], 'Other task')
        with self.assertRaises(ValueError):
            graph.propose_link(self.pid, source['id'], 'Invented quote', 'reason', 'file', 'draft.md')
        with self.assertRaises(FileNotFoundError):
            graph.propose_link(self.pid, source['id'], 'Owner: Alex', 'reason', 'task', foreign['id'])
        graph.approve_root(other['id'])
        with self.assertRaises(ValueError):
            graph.propose_link(other['id'], source['id'], 'Owner: Alex', 'reason', 'file', 'draft.md')

    def test_confirmed_links_survive_reload_and_only_confirmed_dependents_are_flagged(self):
        source = self.source()
        file_link = self.link(source)
        task_link = self.link(source, 'task')
        rejected = self.link(source)
        for link in (file_link, task_link):
            graph.review_link(self.pid, link['id'], 'confirm')
        graph.review_link(self.pid, rejected['id'], 'reject')
        before_task = projects.list_tasks(self.pid)
        before_draft = (self.root / 'draft.md').read_bytes()
        (self.root / 'source.md').write_text('Launch date: November 25\nOwner: Alex\n', encoding='utf-8')
        result = graph.rescan(self.pid)
        self.assertEqual(result['sources'][0]['state'], 'changed')
        self.assertEqual({link['id'] for link in result['links'] if link['needs_review']}, {file_link['id'], task_link['id']})
        self.assertEqual(projects.list_tasks(self.pid), before_task)
        self.assertEqual((self.root / 'draft.md').read_bytes(), before_draft)
        # New connections read the same stable IDs and original revision.
        loaded = graph.get_graph(self.pid)
        self.assertEqual(loaded, result)
        self.assertEqual(graph.add_source(self.pid, 'source.md')['sha256'], source['sha256'])

    def test_missing_targets_and_sources_have_distinct_recoverable_states(self):
        source = self.source()
        file_link = self.link(source)
        task_link = self.link(source, 'task')
        for link in (file_link, task_link):
            graph.review_link(self.pid, link['id'], 'confirm')
        (self.root / 'draft.md').unlink()
        projects.delete_task(self.pid, self.task['id'])
        result = graph.rescan(self.pid)
        self.assertEqual(result['sources'][0]['state'], 'unchanged')
        self.assertTrue(all(link['target_state'] == 'missing' and link['needs_review'] for link in result['links']))
        (self.root / 'source.md').unlink()
        self.assertEqual(graph.rescan(self.pid)['sources'][0]['state'], 'missing')

    def test_confirmation_rechecks_source_and_target_revisions(self):
        source = self.source()
        link = self.link(source)
        (self.root / 'draft.md').write_text('Concurrent edit', encoding='utf-8')
        with self.assertRaises(ValueError):
            graph.review_link(self.pid, link['id'], 'confirm')
        (self.root / 'source.md').write_text('Updated source', encoding='utf-8')
        with self.assertRaises(ValueError):
            self.link(source, 'task')
        self.assertEqual(graph.get_graph(self.pid)['links'][0]['state'], 'proposed')

    def test_folder_change_cannot_redirect_an_existing_graph(self):
        self.source()
        other = self.base / 'other'
        other.mkdir()
        projects.update_project(self.pid, path=str(other))
        with self.assertRaises(ValueError):
            graph.rescan(self.pid)
        with self.assertRaises(ValueError):
            graph.approve_root(self.pid)

    def test_rescan_reads_only_registered_paths_and_never_other_project(self):
        self.source()
        (self.root / 'unregistered.md').write_text('Not selected', encoding='utf-8')
        other_root = self.base / 'other'
        other_root.mkdir()
        (other_root / 'source.md').write_text('Different fact', encoding='utf-8')
        other = projects.create_project('Other', str(other_root))
        graph.approve_root(other['id'])
        graph.add_source(other['id'], 'source.md')
        previous = graph.get_graph(other['id'])
        with patch('app.project_graph.read_text', wraps=read_text) as read:
            graph.rescan(self.pid)
        self.assertEqual([call.args for call in read.call_args_list], [(self.root, 'source.md')])
        self.assertEqual(graph.get_graph(other['id']), previous)

    def test_symlinks_cannot_escape_approved_folder(self):
        self.source()
        outside = self.base / 'outside.md'
        outside.write_text('Private outside fixture', encoding='utf-8')
        try:
            (self.root / 'linked.md').symlink_to(outside)
        except OSError:
            self.skipTest('Host does not permit symlink creation; this test also runs on Linux CI.')
        with self.assertRaises(ValueError):
            graph.add_source(self.pid, 'linked.md')
        nested = self.root / 'alias'
        nested.symlink_to(self.base, target_is_directory=True)
        with self.assertRaises(ValueError):
            graph.add_source(self.pid, 'alias/outside.md')

    def test_oversized_binary_empty_and_invalid_utf8_are_not_captured(self):
        graph.approve_root(self.pid)
        for content in (b'', b'\x00text', b'\xff', b'x' * (MAX_TEXT_BYTES + 1)):
            (self.root / 'bad.txt').write_bytes(content)
            with self.assertRaises(ValueError):
                graph.add_source(self.pid, 'bad.txt')
        self.assertEqual(graph.get_graph(self.pid)['sources'], [])

    async def test_api_full_review_and_impact_flow_and_schema_rejection(self):
        app = FastAPI()
        app.include_router(routes.router)
        prefix = f'/projects/{self.pid}/graph'
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
            self.assertEqual((await client.post(prefix + '/sources', json={'path': 'source.md'})).status_code, 409)
            self.assertEqual((await client.post(prefix + '/approve')).status_code, 200)
            source = (await client.post(prefix + '/sources', json={'path': 'source.md'})).json()
            payload = {'source_id': source['id'], 'fact': 'Owner: Alex', 'reason': 'Owner reviews draft', 'target_kind': 'file', 'target_id': 'draft.md'}
            self.assertEqual((await client.post(prefix + '/links', json={**payload, 'execute': True})).status_code, 422)
            link = (await client.post(prefix + '/links', json=payload)).json()
            self.assertEqual((await client.post(prefix + '/review', json={'link_id': link['id'], 'decision': 'confirm'})).status_code, 200)
            (self.root / 'source.md').write_text('Owner: Jordan', encoding='utf-8')
            result = (await client.post(prefix + '/rescan')).json()
            self.assertTrue(result['links'][0]['needs_review'])
            self.assertEqual(result['links'][0]['fact'], 'Owner: Alex')
