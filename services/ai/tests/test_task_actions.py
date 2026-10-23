import asyncio
import json
import os
import tempfile
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import AsyncMock, patch

import httpx
from fastapi import FastAPI
from pydantic import ValidationError
from app import task_actions as actions
from app.db import conversations, projects
from app.api.routes import task_actions as routes
from app.api.routes.chat import ChatRequest, _stream_ollama


class TaskActionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.env = patch.dict(os.environ, {'LOCALMIND_DATA_DIR': self.temp.name})
        self.env.start()
        self.project = projects.create_project('Studio')
        self.chat = conversations.create_conversation('Task trial')
        projects.assign_conversation(self.chat['id'], self.project['id'])
        self.op = str(uuid.uuid4())

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def propose(self, operation=None):
        return actions.store_proposal(operation or self.op, self.chat['id'], self.project['id'],
                                      'Add a task to review the draft.', {'title': 'Review draft', 'due_at': '2026-11-18'})

    def test_untrusted_arguments_never_write_tasks_or_proposals(self):
        for value in ({'title': ''}, {'title': ' '}, {'title': 'a\nb'}, {'title': 12},
                      {'title': 'Read', 'command': 'delete'}, {'title': 'Read', 'project_id': 'other'},
                      {'title': 'Read', 'due_at': 'tomorrow'}, {'title': 'Read', 'due_at': '2026-02-30'}):
            with self.subTest(value=value), self.assertRaises(ValidationError):
                actions.store_proposal(self.op, self.chat['id'], self.project['id'], 'Add task', value)
        self.assertEqual(projects.list_tasks(self.project['id']), [])
        self.assertEqual(actions.list_actions(self.chat['id']), [])

    def test_lost_receipt_retry_and_concurrent_confirm_create_one_task(self):
        self.propose()
        self.assertEqual(projects.list_tasks(self.project['id']), [])
        def confirm(_):
            return actions.review_action(self.op, self.chat['id'], 'confirm')['receipt']
        with ThreadPoolExecutor(max_workers=4) as executor:
            receipts = list(executor.map(confirm, range(4)))
        self.assertTrue(all(receipt == receipts[0] for receipt in receipts))
        self.assertEqual(len(projects.list_tasks(self.project['id'])), 1)
        # Simulate client restart and a lost first response, then task removal.
        self.assertEqual(actions.list_actions(self.chat['id'])[0]['receipt'], receipts[0])
        projects.delete_task(self.project['id'], receipts[0]['task_id'])
        self.assertEqual(confirm(None), receipts[0])
        self.assertEqual(projects.list_tasks(self.project['id']), [])
        self.assertFalse(actions.list_actions(self.chat['id'])[0]['task_exists'])

    def test_rejected_proposal_and_cross_chat_confirmation_do_not_create(self):
        self.propose()
        with self.assertRaises(ValueError):
            actions.review_action(self.op, str(uuid.uuid4()), 'confirm')
        actions.review_action(self.op, self.chat['id'], 'dismiss')
        result = actions.review_action(self.op, self.chat['id'], 'confirm')
        self.assertEqual(result['state'], 'dismissed')
        self.assertEqual(projects.list_all_tasks(), [])

    def test_project_move_blocks_confirmation_but_allows_dismissal(self):
        self.propose()
        other = projects.create_project('Other')
        projects.assign_conversation(self.chat['id'], other['id'])
        with self.assertRaises(ValueError):
            actions.review_action(self.op, self.chat['id'], 'confirm')
        self.assertEqual(actions.review_action(self.op, self.chat['id'], 'dismiss')['state'], 'dismissed')
        self.assertEqual(projects.list_all_tasks(), [])

    def test_operation_id_is_bound_to_original_request(self):
        original = self.propose()
        self.assertEqual(self.propose()['id'], original['id'])
        with self.assertRaises(ValueError):
            actions.store_proposal(self.op, self.chat['id'], self.project['id'], 'Different request', {'title': 'Different'})

    def test_database_failure_rolls_back_task_and_receipt_together(self):
        self.propose()
        conn = projects._get_conn()
        conn.execute("CREATE TRIGGER fail_receipt BEFORE UPDATE ON task_actions BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;")
        conn.commit()
        conn.close()
        import sqlite3
        with self.assertRaises(sqlite3.IntegrityError):
            actions.review_action(self.op, self.chat['id'], 'confirm')
        self.assertEqual(projects.list_all_tasks(), [])
        self.assertEqual(actions.list_actions(self.chat['id'])[0]['state'], 'proposed')

    async def test_explicit_chat_request_prepares_review_without_writing_task_or_searching(self):
        request = ChatRequest(message='Add a task called Review draft.', model='local',
                              project_id=self.project['id'], conversation_id=self.chat['id'],
                              operation_id=self.op, task_actions_enabled=True, web_search_enabled=True)
        response = httpx.Response(200, json={'message': {'content': '{"title":"Review draft","due_at":null}'}},
                                  request=httpx.Request('POST', 'http://localhost/api/chat'))
        with patch('app.task_actions.httpx.AsyncClient') as client, patch('app.api.routes.chat._run_web_search', new_callable=AsyncMock) as search:
            client.return_value.__aenter__.return_value.post = AsyncMock(return_value=response)
            frames = [json.loads(frame.removeprefix('data: ')) async for frame in _stream_ollama(request)]
        search.assert_not_awaited()
        self.assertEqual([frame['type'] for frame in frames], ['status', 'token', 'done'])
        self.assertIn('Review', frames[1]['content'])
        self.assertEqual(projects.list_all_tasks(), [])
        self.assertEqual(actions.list_actions(self.chat['id'])[0]['state'], 'proposed')

    async def test_missing_project_and_model_failure_cannot_claim_success(self):
        request = ChatRequest(message='Add a task called Read.', model='local', task_actions_enabled=True)
        frames = [json.loads(frame.removeprefix('data: ')) async for frame in _stream_ollama(request)]
        self.assertIn('Choose a project', frames[1]['content'])
        request.project_id, request.conversation_id, request.operation_id = self.project['id'], self.chat['id'], self.op
        with patch('app.task_actions.httpx.AsyncClient', side_effect=RuntimeError('offline')):
            frames = [json.loads(frame.removeprefix('data: ')) async for frame in _stream_ollama(request)]
        self.assertEqual(frames[-1]['type'], 'error')
        self.assertEqual(projects.list_all_tasks(), [])

    async def test_api_review_returns_persisted_receipt_and_rejects_unknown_fields(self):
        self.propose()
        app = FastAPI()
        app.include_router(routes.router)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://test') as client:
            invalid = await client.post(f'/task-actions/{self.op}/review', json={'conversation_id': self.chat['id'], 'decision': 'confirm', 'title': 'Injected'})
            self.assertEqual(invalid.status_code, 422)
            result = await client.post(f'/task-actions/{self.op}/review', json={'conversation_id': self.chat['id'], 'decision': 'confirm'})
            self.assertEqual(result.status_code, 200)
            receipt = result.json()['receipt']
            self.assertEqual(receipt['task_id'], projects.list_all_tasks()[0]['id'])

    async def test_cancellation_during_extraction_does_not_create_proposal(self):
        request = ChatRequest(message='Add a task called Read.', model='local', task_actions_enabled=True,
                              project_id=self.project['id'], conversation_id=self.chat['id'], operation_id=self.op)
        entered = asyncio.Event()
        async def blocked(*args, **kwargs):
            entered.set()
            await asyncio.Event().wait()
        with patch('app.task_actions.httpx.AsyncClient') as client:
            client.return_value.__aenter__.return_value.post = blocked
            pending = asyncio.create_task(actions.propose_from_chat(request))
            await entered.wait()
            pending.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await pending
        self.assertEqual(actions.list_actions(self.chat['id']), [])
