import os
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import AsyncMock, patch

from app.context_retrieval import collect_context, resolve_project, select_memories
from app.db import conversations, knowledge, memories, projects


class RelevantMemoryTests(unittest.TestCase):
    def test_relevance_and_scope_exclude_unrelated_projects_and_conversations(self):
        rows = [
            {"content": "You prefer learning through small worked examples."},
            {"content": "You enjoy gardening on quiet Sunday afternoons."},
            {"content": "Atlas examples must use the secret launch data.", "project_id": "other"},
            {"content": "You prefer Python examples for this discussion.", "conversation_id": "other-chat"},
            {"content": "Atlas examples should use synthetic Python data.", "project_id": "atlas"},
        ]
        selected = select_memories("Explain an Atlas decision tree example", rows,
                                   project_id="atlas", conversation_id="chat")
        self.assertEqual({r["content"] for r in selected}, {rows[0]["content"], rows[4]["content"]})


class LocalContextTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.environment = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": self.directory.name})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.addCleanup(self.directory.cleanup)

    async def test_conversation_scope_is_authoritative_and_mismatch_fails(self):
        a, b = projects.create_project("Atlas"), projects.create_project("Other")
        chat = conversations.create_conversation("Atlas plan")
        projects.assign_conversation(chat["id"], a["id"])
        self.assertEqual(resolve_project(None, chat["id"]), a["id"])
        with self.assertRaisesRegex(ValueError, "project changed"):
            resolve_project(b["id"], chat["id"])
        with self.assertRaisesRegex(ValueError, "no longer exists"):
            resolve_project(None, "missing")

    async def test_collector_passes_exact_project_and_no_other_tasks(self):
        a, b = projects.create_project("Atlas"), projects.create_project("Other")
        projects.create_task(a["id"], "Atlas synthetic launch")
        projects.create_task(b["id"], "Other confidential task")
        chat = conversations.create_conversation("Atlas")
        projects.assign_conversation(chat["id"], a["id"])
        with patch.object(knowledge, "search", AsyncMock(return_value=[])) as search:
            refs = await collect_context("Explain the launch", [], conversation_id=chat["id"],
                                         knowledge_enabled=True)
        self.assertEqual(search.call_args.kwargs["project_id"], a["id"])
        self.assertIn("Atlas synthetic launch", refs[0]["content"])
        self.assertNotIn("Other confidential", str(refs))

    async def test_memory_migration_preserves_legacy_records_and_scopes_new_facts(self):
        db = Path(self.directory.name) / "memories.db"
        with closing(sqlite3.connect(db)) as connection, connection:
            connection.execute("CREATE TABLE memories(id TEXT PRIMARY KEY, content TEXT NOT NULL, created_at TEXT NOT NULL)")
            connection.execute("INSERT INTO memories VALUES('old', 'You prefer small worked examples before equations.', '2020')")
        legacy = memories.list_all()[0]
        self.assertIsNone(legacy["project_id"])
        rows = memories.insert_memories(["You prefer Python examples for this project."],
                                        project_id="atlas", conversation_id="chat")
        self.assertEqual(rows[0]["project_id"], "atlas")
        self.assertEqual(rows[0]["conversation_id"], "chat")
        self.assertEqual(projects.get_project_memories("atlas"), rows)
        self.assertEqual(projects.get_project_memories("other"), [])

    async def test_legacy_knowledge_migrates_without_becoming_project_evidence(self):
        db = Path(self.directory.name) / "knowledge_sources.db"
        with closing(sqlite3.connect(db)) as connection, connection:
            connection.execute("CREATE TABLE sources(id TEXT PRIMARY KEY,path TEXT,name TEXT,file_count INTEGER,chunk_count INTEGER,status TEXT,created_at TEXT)")
            connection.execute("INSERT INTO sources VALUES('old','fixture','legacy',1,1,'ready','2020')")
        self.assertIsNone(knowledge.list_sources()[0]["project_id"])
        with patch.object(knowledge, "embed_text", AsyncMock()) as embed:
            self.assertEqual(await knowledge.search("secret", project_id="atlas"), [])
        embed.assert_not_called()
        self.assertTrue(knowledge.assign_source_project("old", "atlas"))
        self.assertEqual(knowledge.list_sources()[0]["project_id"], "atlas")

    async def test_actual_vector_query_prefilters_project_before_ranking(self):
        conn = knowledge._get_sqlite()
        for source_id, project_id in [("a", "atlas"), ("b", "other"), ("u", None)]:
            conn.execute("INSERT INTO sources(id,path,name,status,created_at,project_id) VALUES(?,?,?,'ready','2020',?)",
                         (source_id, source_id, source_id, project_id))
        conn.commit()
        conn.close()
        table = knowledge._get_lance_table()
        table.add([{"id": key, "source_id": key, "file_path": key, "chunk_index": 0,
                    "content": key + " evidence", "vector": [0.0] * 768} for key in ["a", "b", "u"]])
        with patch.object(knowledge, "embed_text", AsyncMock(return_value=[0.0] * 768)):
            self.assertEqual([r["source_id"] for r in await knowledge.search("evidence", project_id="atlas")], ["a"])
            self.assertEqual([r["source_id"] for r in await knowledge.search("evidence")], ["u"])

    async def test_source_moved_while_embedding_waits_is_not_returned_to_old_project(self):
        conn = knowledge._get_sqlite()
        conn.execute("INSERT INTO sources(id,path,name,status,created_at,project_id) VALUES('a','a','a','ready','2020','atlas')")
        conn.commit()
        conn.close()
        table = knowledge._get_lance_table()
        table.add([{"id": "a", "source_id": "a", "file_path": "a", "chunk_index": 0,
                    "content": "Moved evidence", "vector": [0.0] * 768}])

        async def move_while_waiting(*args, **kwargs):
            knowledge.assign_source_project("a", "other")
            return [0.0] * 768

        with patch.object(knowledge, "embed_text", side_effect=move_while_waiting):
            self.assertEqual(await knowledge.search("evidence", project_id="atlas"), [])

    async def test_collector_rejects_scope_changes_during_awaited_retrieval(self):
        a, b = projects.create_project("Atlas"), projects.create_project("Other")
        chat = conversations.create_conversation("Atlas")
        for mutation in ("move", "delete"):
            with self.subTest(mutation=mutation):
                projects.assign_conversation(chat["id"], a["id"])

                async def change_scope(*args, **kwargs):
                    if mutation == "move":
                        projects.assign_conversation(chat["id"], b["id"])
                    else:
                        projects.delete_project(a["id"])
                    return []

                with patch.object(knowledge, "search", side_effect=change_scope):
                    with self.assertRaises(ValueError):
                        await collect_context(
                            "Explain this project's launch", [], conversation_id=chat["id"],
                            knowledge_enabled=True,
                        )


if __name__ == "__main__":
    unittest.main()
