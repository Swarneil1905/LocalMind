import json
import os
import tempfile
import unittest
from unittest.mock import patch

import httpx
from fastapi import HTTPException

from app.api.routes import memory
from app.db import conversations, memories, projects
from app.inference_queue import current_queue


class MemoryScopeTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.env = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": self.directory.name})
        self.env.start()
        self.addCleanup(self.env.stop)

    async def test_extract_uses_scope_and_rejects_cross_project_links(self):
        atlas, other = projects.create_project("Atlas"), projects.create_project("Other")
        conversation = conversations.create_conversation("Atlas study")
        projects.assign_conversation(conversation["id"], atlas["id"])
        own = memories.insert_memories(["You use Python examples in your Atlas project."],
                                       project_id=atlas["id"])[0]
        foreign = memories.insert_memories(["Secret customer data belongs to the Other project."],
                                           project_id=other["id"])[0]
        payloads = []
        real_client = httpx.AsyncClient
        def respond(request):
            payloads.append(json.loads(request.content))
            answer = {"facts": ["You prefer short Python examples before mathematical equations."],
                      "links": [{"from_new": 0, "to_id": foreign["id"], "relation": "related_to"},
                                {"from_new": 0, "to_id": own["id"], "relation": "elaborates"}]}
            return httpx.Response(200, json={"message": {"content": json.dumps(answer)}})
        with patch.object(memory.httpx, "AsyncClient",
                          side_effect=lambda **kw: real_client(transport=httpx.MockTransport(respond), **kw)):
            await memory.extract_memories(memory.ExtractRequest(
                user_message="I prefer short Python examples before equations.", assistant_message="Understood.",
                conversation_id=conversation["id"], project_id=atlas["id"]))
        self.assertIn(own["content"], payloads[0]["messages"][0]["content"])
        self.assertNotIn(foreign["content"], payloads[0]["messages"][0]["content"])
        self.assertEqual({link["to_id"] for link in memories.list_all_links()}, {own["id"]})
        created = next(row for row in memories.list_all() if row["id"] not in (own["id"], foreign["id"]))
        self.assertEqual(created["project_id"], atlas["id"])
        self.assertEqual(created["conversation_id"], conversation["id"])
        self.assertFalse(current_queue().active)

    async def test_unknown_or_changed_conversation_never_calls_model(self):
        with patch.object(memory.httpx, "AsyncClient") as client:
            with self.assertRaises(HTTPException) as error:
                await memory.extract_memories(memory.ExtractRequest(
                    user_message="A personal fact", assistant_message="Okay", conversation_id="missing"))
        self.assertEqual(error.exception.status_code, 409)
        client.assert_not_called()


if __name__ == "__main__":
    unittest.main()
