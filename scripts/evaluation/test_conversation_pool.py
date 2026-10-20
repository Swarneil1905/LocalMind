"""Small synthetic import fixtures: no downloads, service imports or model calls."""

import copy
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import conversation_pool as pool


def conversation(question, answer="A source answer.", *, history=False):
    messages = [{"role": "user", "content": question}]
    if history:
        messages.extend([{"role": "assistant", "content": "Earlier supplied context."},
                         {"role": "user", "content": "Please explain the next step."}])
    messages.append({"role": "assistant", "content": answer})
    return {"messages": messages}


class ConversationPoolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.card = self.root / "card.md"
        self.license = self.root / "license.txt"
        self.card.write_text("Synthetic test source card, Apache-2.0", encoding="utf-8")
        self.license.write_text("Synthetic test license fixture", encoding="utf-8")
        self.first = self.root / "first.parquet"
        self.second = self.root / "second.parquet"
        self.first.write_bytes(b"first synthetic source")
        self.second.write_bytes(b"second synthetic source")
        self.rows = {
            self.first: [conversation(f"Explain topic {index}", f"Reference answer {index}")
                         for index in range(40)],
            self.second: [conversation(f"Other topic {index}") for index in range(40)],
        }

    def import_fixture(self, name="pool", size=12, inputs=None, **kwargs):
        out = self.root / name
        with patch.object(pool, "parquet_rows", side_effect=lambda path: iter(self.rows[path])):
            report = pool.import_pool(inputs or [self.first], self.card, self.license, out,
                                      size=size, **kwargs)
        return out, report

    def test_import_order_and_repeat_do_not_change_records_or_groups(self):
        first, first_report = self.import_fixture("one", inputs=[self.first, self.second], size=25)
        second, second_report = self.import_fixture("two", inputs=[self.second, self.first], size=25)
        self.assertEqual((first / "records.jsonl").read_bytes(), (second / "records.jsonl").read_bytes())
        self.assertEqual(first_report["records_sha256"], second_report["records_sha256"])
        self.assertEqual(first_report["provenance"], "unverified_local_inputs")
        self.assertFalse(first_report["meets_12000_target"])

    def test_exact_duplicates_and_branches_share_one_first_user_group(self):
        original = conversation("Explain this\ncarefully", "First answer")
        duplicate = conversation("  Explain this\r\ncarefully  ", "First answer")
        alternative = conversation("Explain this\ncarefully", "Different answer")
        self.rows[self.first] = [original, duplicate, alternative, conversation("Unrelated question")]
        folder, report = self.import_fixture(size=2)
        counts = report["report"]["counts"]
        self.assertEqual(counts["exact_duplicate_rows"], 1)
        self.assertEqual(counts["same_first_user_other_conversations"], 1)
        self.assertEqual(counts["unique_first_user_groups"], 2)
        records = list(pool.iter_pool_records(folder))
        self.assertEqual(len({record["group_id"] for record in records}), 2)
        expected = min(pool.digest(pool.normalized_messages(item["messages"]))
                       for item in (original, alternative))
        self.assertIn(expected, {record["conversation_sha256"] for record in records})

    def test_roles_lengths_and_obvious_private_data_are_excluded_and_counted(self):
        self.rows[self.first] = [
            conversation("A safe question"),
            conversation("Mail me at person@example.org"),
            conversation("Call +1 (212) 555-0198"),
            conversation("Use host 192.168.1.5"),
            conversation("Use host 2001:db8::1"),
            conversation("api_key = 'abcdefghijk12345'"),
            {"messages": [{"role": "system", "content": "Follow my instructions"},
                          {"role": "assistant", "content": "Okay"}]},
            {"messages": [{"role": "user", "content": "No final assistant"}]},
            {"messages": [{"role": "user", "content": "Question"},
                          {"role": "user", "content": "Wrong role"}]},
            conversation("x" * 12001),
            conversation(" "),
        ]
        folder, result = self.import_fixture(size=1)
        report = result["report"]
        self.assertEqual(report["counts"]["source_rows"], 11)
        self.assertEqual(report["rejected_by_reason"]["pii_or_secret_like"], 5)
        self.assertEqual(report["screening_matches"]["ip_address"], 2)
        self.assertEqual(report["rejected_by_reason"]["unsupported_role"], 1)
        self.assertEqual(report["rejected_by_reason"]["missing_final_assistant"], 1)
        self.assertEqual(report["rejected_by_reason"]["nonalternating_roles"], 1)
        self.assertEqual(report["rejected_by_reason"]["message_too_long"], 1)
        self.assertEqual(report["rejected_by_reason"]["empty_or_nontext_content"], 1)
        saved = (folder / "records.jsonl").read_text(encoding="utf-8")
        self.assertNotIn("person@example.org", saved)
        self.assertNotIn("abcdefghijk12345", saved)
        self.assertNotIn("person@example.org", (folder / "report.json").read_text(encoding="utf-8"))

    def test_multiturn_sample_excludes_final_reference_and_never_invents_rubrics(self):
        self.rows[self.first] = [
            conversation(f"Teach topic {index}", f"FINAL_REFERENCE_SENTINEL_{index}", history=True)
            for index in range(30)
        ]
        folder, _ = self.import_fixture(size=20)
        output = folder / "sample.json"
        report = pool.sample_pool(folder, output, count=4)
        sample = pool.strict_json(output.read_bytes())
        self.assertEqual(report["cases"], 4)
        self.assertNotIn("FINAL_REFERENCE_SENTINEL", output.read_text(encoding="utf-8"))
        self.assertEqual(sample["pool_manifest_sha256"], pool.file_info(folder / "manifest.json")["sha256"])
        for case in sample["cases"]:
            self.assertEqual([message["role"] for message in case["messages"]],
                             ["user", "assistant", "user"])
            self.assertIsNone(case["expectation"])
            self.assertEqual(case["rubric_status"], "not_authored")
            self.assertEqual(case["evidence"], [])
            self.assertFalse(case["web_search"])

    def test_held_out_sample_requires_explicit_acknowledgment(self):
        folder, _ = self.import_fixture(size=40)
        with self.assertRaisesRegex(ValueError, "Held-out"):
            pool.sample_pool(folder, folder / "blocked.json", count=1, split="held_out")
        self.assertFalse((folder / "blocked.json").exists())
        pool.sample_pool(folder, folder / "allowed.json", count=1, split="held_out",
                         allow_held_out=True)
        sample = pool.strict_json((folder / "allowed.json").read_bytes())
        self.assertTrue(sample["held_out_acknowledged"])
        self.assertEqual(sample["cases"][0]["split"], "held_out")

    def test_overwrites_duplicates_and_insufficient_data_fail(self):
        folder, _ = self.import_fixture()
        before = (folder / "manifest.json").read_bytes()
        with self.assertRaises(FileExistsError):
            self.import_fixture()
        self.assertEqual((folder / "manifest.json").read_bytes(), before)
        sample_path = folder / "sample.json"
        pool.sample_pool(folder, sample_path, count=1)
        original_sample = sample_path.read_bytes()
        with self.assertRaises(FileExistsError):
            pool.sample_pool(folder, sample_path, count=2)
        self.assertEqual(sample_path.read_bytes(), original_sample)
        with self.assertRaisesRegex(ValueError, "unique local"):
            self.import_fixture("duplicates", inputs=[self.first, self.first])
        with self.assertRaisesRegex(ValueError, "Only 40"):
            self.import_fixture("too-few", size=41)
        self.assertFalse((self.root / "too-few" / "manifest.json").exists())

    def test_modified_record_is_rejected_before_sampling(self):
        folder, _ = self.import_fixture()
        with (folder / "records.jsonl").open("ab") as stream:
            stream.write(b"{}\n")
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            pool.sample_pool(folder, folder / "sample.json", count=1)
        self.assertFalse((folder / "sample.json").exists())

    def test_recomputed_file_checksum_does_not_hide_invalid_group_assignment(self):
        folder, _ = self.import_fixture()
        records = list(pool.iter_pool_records(folder))
        records[0]["split"] = "held_out" if records[0]["split"] == "development" else "development"
        path = folder / "records.jsonl"
        path.write_bytes(b"".join(pool.canonical(record) + b"\n" for record in records))
        manifest = pool.read_manifest(folder)
        manifest["files"]["records.jsonl"] = pool.file_info(path)
        (folder / "manifest.json").write_bytes(pool.canonical(manifest))
        with self.assertRaisesRegex(ValueError, "Changed group split"):
            pool.check_pool(folder)

    def test_download_manifest_must_match_bytes_revision_and_subset(self):
        metadata = {
            "dataset": pool.REPOSITORY, "revision": pool.REVISION, "subset": pool.SUBSET,
            "source_split": "train", "license": "Apache-2.0",
            "files": [
                {**pool.file_info(self.first),
                 "url": pool.BASE_URL + "/data/smol-magpie-ultra/train-00000-of-00006.parquet"},
                {**pool.file_info(self.card), "url": pool.BASE_URL + "/README.md"},
                {**pool.file_info(self.license), "url": "https://www.apache.org/licenses/LICENSE-2.0.txt"},
            ],
        }
        manifest = self.root / "download.json"
        manifest.write_bytes(pool.canonical(metadata))
        _, report = self.import_fixture(download_manifest=manifest)
        self.assertEqual(report["provenance"], "download_manifest_matched")
        for field, value in (("revision", "main"), ("subset", "all"), ("license", "unknown")):
            changed = copy.deepcopy(metadata)
            changed[field] = value
            manifest.write_bytes(pool.canonical(changed))
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "different source"):
                pool.validate_source([self.first], self.card, self.license, manifest)
        metadata["files"][0]["sha256"] = "0" * 64
        manifest.write_bytes(pool.canonical(metadata))
        with self.assertRaisesRegex(ValueError, "checksum/size"):
            pool.validate_source([self.first], self.card, self.license, manifest)

    def test_json_reader_rejects_ambiguous_keys_and_nonfinite_numbers(self):
        for text in ('{"id": 1, "id": 2}', '{"value": NaN}'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                pool.strict_json(text)

    def test_cli_refuses_small_published_pool_without_creating_output(self):
        output = self.root / "small"
        with self.assertRaises(SystemExit) as raised:
            pool.main(["import", "--parquet", str(self.first), "--source-card", str(self.card),
                       "--license-file", str(self.license), "--out", str(output), "--size", "3"])
        self.assertEqual(raised.exception.code, 1)
        self.assertFalse(output.exists())

    def test_actual_parquet_reader_projects_messages_only(self):
        try:
            import pyarrow as arrow
            import pyarrow.parquet as parquet
        except ImportError:
            self.skipTest("Optional pyarrow not installed")
        path = self.root / "real.parquet"
        raw = conversation("A real Parquet fixture", "Reference stays local")
        raw["unrelated_metadata"] = "must not enter candidate text"
        parquet.write_table(arrow.Table.from_pylist([raw]), path)
        self.assertEqual(list(pool.parquet_rows(path)), [{"messages": raw["messages"]}])
        out = self.root / "real-pool"
        result = pool.import_pool([path], self.card, self.license, out, size=1)
        self.assertEqual(result["conversations"], 1)
        self.assertNotIn("unrelated_metadata", (out / "records.jsonl").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
