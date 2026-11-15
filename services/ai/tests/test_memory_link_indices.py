import json
import os
import tempfile
import unittest
from unittest.mock import patch

import httpx

from app.api.routes import memory
from app.db import memories


class MemoryLinkIndexTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        env = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": directory.name})
        env.start()
        self.addCleanup(env.stop)
        self.target = memories.insert_memories([
            "You use Python to analyze your research data."
        ])[0]

    async def extract(self, facts, links):
        real_client = httpx.AsyncClient

        def respond(request):
            return httpx.Response(200, json={"message": {"content": json.dumps({
                "facts": facts, "links": links,
            })}})

        with patch.object(memory.httpx, "AsyncClient", side_effect=lambda **kw:
                          real_client(transport=httpx.MockTransport(respond), **kw)):
            return await memory.extract_memories(memory.ExtractRequest(
                user_message="I prefer Python examples when learning new concepts.",
                assistant_message="Understood.",
            ))

    def link(self, index, relation="elaborates"):
        return {"from_new": index, "to_id": self.target["id"], "relation": relation}

    async def test_skipped_first_fact_never_steals_the_second_facts_link(self):
        retained = "You prefer Python examples when learning new concepts."
        for skipped in ["Too short", self.target["content"].upper(), "", None, False, 0,
                        {"text": "You prefer detailed explanations of mathematical ideas."}]:
            with self.subTest(skipped=skipped):
                for row in memories.list_all():
                    if row["id"] != self.target["id"]:
                        memories.delete_by_id(row["id"])
                result = await self.extract([skipped, retained], [
                    self.link(0, "contradicts"), self.link(1, "elaborates"),
                ])
                self.assertEqual([row["content"] for row in result["extracted"]], [retained])
                self.assertEqual(len(result["links_created"]), 1)
                link = result["links_created"][0]
                self.assertEqual(link["from_id"], result["extracted"][0]["id"])
                self.assertEqual(link["relation"], "elaborates")
                self.assertEqual(len(memories.list_all_links()), 1)
                memories.delete_by_id(result["extracted"][0]["id"])

    async def test_duplicate_within_batch_does_not_shift_later_links(self):
        first = "You prefer Python examples when learning new concepts."
        last = "You prefer Python notebooks when learning new concepts."
        result = await self.extract([first, first.upper(), last], [
            self.link(0), self.link(1, "contradicts"), self.link(2, "part_of"),
        ])
        self.assertEqual([row["content"] for row in result["extracted"]], [first, last])
        self.assertEqual([(row["from_id"], row["relation"]) for row in result["links_created"]], [
            (result["extracted"][0]["id"], "elaborates"),
            (result["extracted"][1]["id"], "part_of"),
        ])

    async def test_invalid_indices_and_targets_never_create_links(self):
        result = await self.extract([
            "You prefer Python examples when learning new concepts.",
        ], [self.link(index) for index in [-1, 1, 999, True, False, "0", 0.0, None]] + [
            {"from_new": 0, "to_id": "missing", "relation": "elaborates"},
            self.link(0, "unknown_relation"),
        ])
        self.assertEqual(len(result["extracted"]), 1)
        self.assertEqual(result["links_created"], [])
        self.assertEqual(memories.list_all_links(), [])

    async def test_all_skipped_facts_create_neither_memories_nor_links(self):
        result = await self.extract(["Too short", self.target["content"]], [
            self.link(0), self.link(1),
        ])
        self.assertEqual(result["extracted"], [])
        self.assertEqual(result["links_created"], [])
        self.assertEqual(len(result["memories"]), 1)


class MemoryGateTests(unittest.TestCase):
    def test_assistant_claims_and_topic_echoes_are_rejected(self):
        asked = "whats happening at Nvidia? how are the AI markets?"
        for fact in ("Nvidia's stock price has surged due to strong demand for AI chips.",
                     "The AI market is showing strong growth with rising investments.",
                     "User is interested in Nvidia stock price and AI markets."):
            with self.subTest(fact=fact):
                self.assertFalse(memory._grounded_in_user_message(fact, asked))

    def test_facts_the_user_stated_are_kept(self):
        for fact, said in (("User is a software engineer who works with Python.", "I'm a software engineer and I mostly write Python"),
                           ("User prefers dark mode in every app.", "Remember that I like dark mode in every app"),
                           ("The user's name is Swarneil Pradhan.", "my name is Swarneil Pradhan")):
            with self.subTest(fact=fact):
                self.assertTrue(memory._grounded_in_user_message(fact, said))


class ExtractParsingTests(unittest.TestCase):
    def test_invalid_collection_shapes_are_not_treated_as_fact_arrays(self):
        for facts in [None, True, 42, "You use Python for your daily research work.", {"a": "b"}]:
            with self.subTest(facts=facts):
                self.assertEqual(memory._parse_extract_response(json.dumps({
                    "facts": facts, "links": [],
                })), ([], []))

    def test_malformed_links_do_not_discard_valid_facts(self):
        facts = ["You use Python for your daily research work."]
        for links in [None, True, 42, "invalid", {"from_new": 0}]:
            with self.subTest(links=links):
                self.assertEqual(memory._parse_extract_response(json.dumps({
                    "facts": facts, "links": links,
                })), (facts, []))

    def test_legacy_array_and_unparseable_response_remain_supported(self):
        facts = ["You use Python for your daily research work."]
        self.assertEqual(memory._parse_extract_response(json.dumps(facts)), (facts, []))
        self.assertEqual(memory._parse_extract_response("not JSON"), ([], []))


if __name__ == "__main__":
    unittest.main()
