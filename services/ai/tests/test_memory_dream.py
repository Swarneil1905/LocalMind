import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from app.db import memories
from app.memory_dream import dream_pass


class DreamPassTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        env = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": directory.name})
        env.start()
        self.addCleanup(env.stop)

    def contents(self):
        return sorted(row["content"] for row in memories.list_all())

    def test_repeats_and_chat_records_go_and_distinct_facts_stay(self):
        memories.insert_memories(["User prefers dark mode in every app.", "The user prefers dark mode in every app", "User said: how have you been lately?",
                                  "User works with Python every single day."], conversation_id="c1")
        result = dream_pass()
        self.assertEqual(result, {"checked": 4, "removed": 2})
        self.assertEqual(self.contents(), ["User prefers dark mode in every app.", "User works with Python every single day."])

    def test_removed_memories_are_saved_to_a_backup_first(self):
        memories.insert_memories(["User likes green tea every morning.", "user likes green tea every morning"], conversation_id="c1")
        dream_pass()
        backups = list((self.root / "memory-backups").glob("dream-*.json"))
        self.assertEqual(len(backups), 1)
        self.assertIn("repeat of", backups[0].read_text(encoding="utf-8"))

    def test_same_wording_in_different_projects_is_kept(self):
        memories.insert_memories(["User likes green tea every morning."], project_id="p1")
        memories.insert_memories(["User likes green tea every morning."], project_id="p2")
        self.assertEqual(dream_pass()["removed"], 0)

    def test_nothing_to_do_writes_no_backup(self):
        memories.insert_memories(["User likes green tea every morning."])
        dream_pass()
        self.assertFalse((self.root / "memory-backups").exists())


if __name__ == "__main__":
    unittest.main()


class MemoryContextTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        env = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": directory.name})
        env.start()
        self.addCleanup(env.stop)

    async def test_used_memories_travel_with_the_words_that_matched(self):
        from app.context_retrieval import collect_context
        memories.insert_memories(["User is learning Rust for embedded firmware work."])
        refs = await collect_context("explain rust lifetimes", [], memory_enabled=True)
        source = refs[0]["source"]
        self.assertEqual(source["kind"], "memory")
        self.assertEqual(source["memories"][0]["matched"], ["rust"])

    def test_selection_stops_at_the_size_budget(self):
        from app.context_retrieval import MEMORY_BUDGET_CHARS, select_memories
        rows = [{"id": str(i), "content": "rust " + "x" * (MEMORY_BUDGET_CHARS // 3), "project_id": None, "conversation_id": None} for i in range(5)]
        chosen = select_memories("rust", rows, project_id=None, conversation_id=None)
        self.assertEqual(len(chosen), 2)
