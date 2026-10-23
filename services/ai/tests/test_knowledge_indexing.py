"""Real SQLite/LanceDB indexing fixtures; no user files or model required."""
import asyncio
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import AsyncMock, patch

import httpx
from fastapi import FastAPI, BackgroundTasks
from app.api.routes import knowledge as routes
from app.db import knowledge as kb, projects
from app.context_retrieval import collect_context
from app.context_budget import pack_context
from app.knowledge_documents import extract_passages


def write_pdf(path):
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    content = b"BT /F1 12 Tf 40 740 Td (PDF-QUARTZ-742 deadline November 12) Tj ET"
    objects.append(b"<< /Length " + str(len(content)).encode() + b" >>\nstream\n" + content + b"\nendstream")
    result, offsets = b"%PDF-1.4\n", [0]
    for number, body in enumerate(objects, 1):
        offsets.append(len(result))
        result += f"{number} 0 obj\n".encode() + body + b"\nendobj\n"
    xref = len(result)
    result += b"xref\n0 6\n0000000000 65535 f \n"
    result += b"".join(f"{offset:010d} 00000 n \n".encode() for offset in offsets[1:])
    result += f"trailer << /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF".encode()
    path.write_bytes(result)


class KnowledgeIndexTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.files = self.root / "files"
        self.files.mkdir()
        self.env = patch.dict(os.environ, {"LOCALMIND_DATA_DIR": str(self.root / "profile")})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.embedding = patch.object(kb, "embed_text", AsyncMock(return_value=[0.01] * 768))
        self.embed = self.embedding.start()
        self.addCleanup(self.embedding.stop)

    async def test_ten_supported_files_have_matching_passages_and_locations(self):
        for extension in ("txt", "md", "py", "js", "ts", "csv", "json", "html", "yaml"):
            (self.files / f"fixture.{extension}").write_text(
                f"Header\nUNIQUE-{extension.upper()}-742 has a deadline of November 12.\n", encoding="utf-8")
        write_pdf(self.files / "fixture.pdf")
        source = await kb.index_path(str(self.files), project_id="atlas")
        self.assertEqual((source["status"], source["file_count"], source["chunk_count"]), ("ready", 10, 10))
        for extension in ("txt", "md", "py", "js", "ts", "csv", "json", "html", "yaml", "pdf"):
            with self.subTest(extension=extension):
                query = "PDF-QUARTZ-742" if extension == "pdf" else f"UNIQUE-{extension.upper()}-742"
                rows = await kb.search(query, project_id="atlas", limit=1)
                self.assertEqual(len(rows), 1)
                self.assertTrue(rows[0]["file_path"].endswith("." + extension))
                detail = kb.passage_detail(rows[0]["id"], "atlas")
                self.assertEqual(detail["file_state"], "current")
                self.assertIn(query, detail["content"])
                self.assertEqual(detail["page"], 1 if extension == "pdf" else None)
                self.assertIsNone(kb.passage_detail(rows[0]["id"], "other"))

    async def test_empty_corrupt_missing_and_embedding_failures_are_truthful(self):
        empty = self.files / "empty.txt"
        empty.write_text("  \n", encoding="utf-8")
        self.assertEqual((await kb.index_path(str(empty)))["status"], "empty")
        corrupt = self.files / "bad.pdf"
        corrupt.write_bytes(b"not a PDF")
        self.assertEqual((await kb.index_path(str(corrupt)))["status"], "error")
        with self.assertRaises(FileNotFoundError):
            kb.create_source(str(self.files / "missing.txt"))
        useful = self.files / "useful.txt"
        useful.write_text("ZX-921 exact identifier\n" * 150, encoding="utf-8")
        self.embed.side_effect = httpx.ConnectError("offline")
        source = await kb.index_path(str(useful))
        self.assertEqual(source["status"], "partial")
        self.assertGreater(source["failed_chunks"], 0)
        self.assertIn("Text search is available", source["error_message"])
        rows = await kb.search("ZX-921")
        self.assertTrue(rows)
        self.assertIn("ZX-921", rows[0]["content"])

    async def test_retry_replaces_old_passages_and_failure_recovers(self):
        file = self.files / "notes.txt"
        file.write_text("OLD-CODE-719")
        source = await kb.index_path(str(file))
        old = (await kb.search("OLD-CODE-719"))[0]
        file.write_text("NEW-CODE-238")
        self.assertEqual(kb.passage_detail(old["id"], None)["file_state"], "changed")
        self.assertTrue(kb.prepare_retry(source["id"]))
        self.assertFalse(kb.prepare_retry(source["id"]))
        self.assertFalse(await kb.search("OLD-CODE-719"))
        result = await kb.index_source(source["id"])
        self.assertEqual(result["status"], "ready")
        self.assertIsNone(kb.passage_detail(old["id"], None))
        self.assertIn("NEW-CODE-238", (await kb.search("NEW-CODE-238"))[0]["content"])
        self.assertTrue(kb.delete_source(source["id"]))
        self.assertFalse(await kb.search("NEW-CODE-238"))

    async def test_vector_store_failure_preserves_searchable_text(self):
        file = self.files / "notes.txt"
        file.write_text("STORAGE-424 retained evidence")
        with patch.object(kb, "_get_lance_table", side_effect=OSError("store unavailable")):
            source = await kb.index_path(str(file))
            self.assertEqual(source["status"], "partial")
            self.assertEqual(source["failed_chunks"], source["chunk_count"])
            rows = await kb.search("STORAGE-424")
            self.assertEqual(len(rows), 1)
            self.assertEqual(kb.passage_detail(rows[0]["id"], None)["file_state"], "current")
            self.assertIsNone(kb.passage_detail("missing", None))

    async def test_missing_file_view_keeps_indexed_snapshot_with_recovery(self):
        file = self.files / "notes.txt"
        file.write_text("Read the saved passage")
        await kb.index_path(str(file))
        row = (await kb.search("saved passage"))[0]
        file.unlink()
        detail = kb.passage_detail(row["id"], None)
        self.assertEqual(detail["file_state"], "missing")
        self.assertIn("Restore", detail["notice"])

    async def test_cross_project_text_and_vector_candidates_stay_excluded(self):
        for scope in ("atlas", "other"):
            file = self.files / f"{scope}.txt"
            file.write_text(f"EXACT-424 {scope} project secret")
            await kb.index_path(str(file), project_id=scope)
        self.embed.side_effect = httpx.ConnectError("offline")
        rows = await kb.search("EXACT-424", project_id="atlas")
        self.assertEqual(len(rows), 1)
        self.assertIn("atlas project", rows[0]["content"])
        self.assertFalse(await kb.search("EXACT-424"))
        self.assertFalse(await kb.search("EXACT-424", project_id="empty"))

    async def test_move_during_embedding_also_filters_cached_lexical_matches(self):
        file = self.files / "notes.txt"
        file.write_text("PRIVATE-424")
        source = await kb.index_path(str(file), project_id="atlas")
        async def move(*args, **kwargs):
            kb.assign_source_project(source["id"], "other")
            return [0.01] * 768
        self.embed.side_effect = move
        self.assertFalse(await kb.search("PRIVATE-424", project_id="atlas"))

    async def test_ten_conflicting_same_name_files_never_cross_project_scope(self):
        for scope in ("atlas", "quartz"):
            folder = self.files / scope
            folder.mkdir()
            for number in range(10):
                (folder / f"notes-{number}.txt").write_text(
                    f"ISOLATION-{number:03d} release codeword {scope}-lantern; "
                    f"launch date {'November 12' if scope == 'atlas' else 'December 8'}, 2026.")
            await kb.index_path(str(folder), project_id=scope)
        for number in range(10):
            with self.subTest(number=number):
                rows = await kb.search(f"ISOLATION-{number:03d}", project_id="atlas")
                self.assertTrue(rows)
                self.assertTrue(all("atlas-lantern" in row["content"] for row in rows))
                self.assertTrue(all("quartz" not in row["file_path"] for row in rows))
                self.assertTrue(rows[0]["file_path"].endswith(f"notes-{number}.txt"))
                self.assertFalse(await kb.search(f"ISOLATION-{number:03d}", project_id="empty"))

    async def test_cancelled_and_restarted_work_can_be_retried(self):
        file = self.files / "notes.txt"
        file.write_text("cancelled work")
        source = kb.create_source(str(file))
        self.embed.side_effect = asyncio.CancelledError()
        with self.assertRaises(asyncio.CancelledError):
            await kb.index_source(source["id"])
        self.assertEqual(kb.get_source(source["id"])["status"], "error")
        self.assertTrue(kb.prepare_retry(source["id"]))
        kb.recover_interrupted_sources()
        self.assertEqual(kb.get_source(source["id"])["status"], "error")

    async def test_source_exists_before_background_task_and_routes_validate_scope(self):
        file = self.files / "notes.txt"
        file.write_text("API-424")
        project = projects.create_project("Atlas")
        background = BackgroundTasks()
        result = await routes.index_path(routes.IndexRequest(path=str(file), project_id=project["id"]), background)
        source = result["source"]
        self.assertEqual(kb.get_source(source["id"])["status"], "indexing")
        await background()
        row = (await kb.search("API-424", project_id=project["id"]))[0]
        app = FastAPI()
        app.include_router(routes.router)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
            self.assertEqual((await client.get(f"/knowledge/passage/{row['id']}")).status_code, 404)
            response = await client.get(f"/knowledge/passage/{row['id']}", params={"project_id": project["id"]})
            self.assertEqual(response.status_code, 200)
            self.assertIn("API-424", response.json()["content"])
            response = await client.post("/knowledge/search", json={"query": "x", "limit": 999})
            self.assertEqual(response.status_code, 422)

    async def test_collector_emits_traceable_citations_and_packer_lists_only_retained_refs(self):
        project = projects.create_project("Atlas")
        file = self.files / "notes.txt"
        file.write_text("LAUNCH-424 is November 12.")
        await kb.index_path(str(file), project_id=project["id"])
        refs = await collect_context("LAUNCH-424", [], project_id=project["id"], knowledge_enabled=True)
        source = next(ref["source"] for ref in refs if "source" in ref)
        self.assertEqual(source["citation"], "K1")
        self.assertEqual(source["location"], "Lines 1-1")
        packed = pack_context("Answer using evidence", "LAUNCH-424", [], refs)
        self.assertIn("[K1]", json.dumps(packed.messages))
        self.assertTrue(packed.report["reference_labels"])

    def test_bounded_unicode_line_offsets(self):
        file = self.files / "unicode.md"
        file.write_text("α\n" * 2000, encoding="utf-8")
        rows = extract_passages(file)
        self.assertGreater(len(rows), 1)
        self.assertEqual(rows[1]["line_start"], 801)
        self.assertEqual(rows[1]["content"], file.read_text(encoding="utf-8")[1600:3400])


if __name__ == "__main__":
    unittest.main()
