"""Regression tests for evidence-bound human reviews; no models or network."""

import copy
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import review_run as reviews


def write_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def case(identity: str, *, layer: str = "service", severity: str = "normal") -> dict:
    return {
        "id": identity, "split": "development", "evaluation_layer": layer, "severity": severity,
        "title": "Synthetic case", "messages": [{"role": "user", "content": "Synthetic question"}],
        "expectation": {"required": ["States the supported fact.", "Explains its limits."],
                        "forbidden": ["Claims an unperformed action."]},
    }


def result(selected_case: dict, **changes) -> dict:
    value = {key: selected_case[key] for key in ("id", "split", "evaluation_layer", "severity")}
    value.update(model="qwen3:8b", profile="baseline", status="completed", answer="Synthetic answer",
                 error=None, truncated=False, timings={}, trace=[], effective_request={})
    value.update(changes)
    return value


def identify(review: dict) -> None:
    review["reviewer"] = {"name": "Test reviewer", "date": "2026-10-15"}


def mark_pass(review: dict, index: int = 0) -> None:
    identify(review)
    entry = review["cases"][index]
    for kind, status in (("required", "met"), ("forbidden", "not_observed")):
        for finding in entry[kind]:
            finding["status"] = status
            finding["evidence"] = "Reviewer observation: synthetic fixture evidence."


class ReviewRunTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.cases = [case("LM-001"), case("LM-002", severity="critical"),
                      case("LM-003", layer="desktop", severity="critical")]
        self.manifest = {"schema_version": 1, "split": "development", "suite_sha256": "a" * 64,
                         "case_ids": [item["id"] for item in self.cases], "state": "completed",
                         "execution_layer": "python_service_generator", "model": "qwen3:8b", "profile": "baseline"}
        write_json(self.root / "manifest.json", self.manifest)
        write_json(self.root / "cases.json", {"schema_version": 1, "cases": self.cases})
        for item in self.cases:
            saved = result(item, status="not_evaluated", answer="", error="Requires desktop workflow") if item["evaluation_layer"] == "desktop" else result(item)
            write_json(self.root / "results" / f"{item['id']}.json", saved)

    def test_blank_template_and_no_review_never_grade_answers(self):
        template = reviews.create_review(self.root)
        self.assertEqual(template["reviewer"], {"name": "", "date": ""})
        self.assertTrue(all(value is None for value in template["cases"][0]["dimensions"].values()))
        self.assertTrue(all(item["status"] is None and item["evidence"] == "" for item in template["cases"][0]["required"]))
        summary = reviews.summarize(self.root)
        self.assertEqual(summary["overall"]["outcomes"], {"pass": 0, "fail": 0, "not_evaluated": 3, "run_error": 0})
        self.assertEqual(summary["overall"]["execution"], {"completed": 2, "run_error": 0, "not_evaluated": 1})
        self.assertEqual(summary["overall"]["review_progress"]["unreviewed"], 3)
        self.assertFalse(summary["review_supplied"])
        self.assertEqual(reviews.summarize(self.root, template)["overall"], summary["overall"])

    def test_pass_denominator_retains_unreviewed_and_separates_desktop(self):
        review = reviews.create_review(self.root)
        mark_pass(review)
        review["cases"][0]["dimensions"]["communication"] = 3
        report = reviews.summarize(self.root, review)
        self.assertEqual(report["cases"][0]["outcome"], "pass")
        self.assertEqual(report["layers"]["service"]["pass_fraction_of_selected"], {"numerator": 1, "denominator": 2})
        self.assertEqual(report["layers"]["desktop"]["pass_fraction_of_selected"], {"numerator": 0, "denominator": 1})
        self.assertEqual(report["overall"]["pass_fraction_of_selected"], {"numerator": 1, "denominator": 3})
        communication = report["overall"]["score_coverage"]["communication"]
        self.assertEqual(communication["scored_cases"], 1)
        self.assertEqual(communication["score_counts"]["3"], 1)
        grounding = report["overall"]["score_coverage"]["correctness_grounding"]
        self.assertEqual(grounding["scored_cases"], 0)
        self.assertEqual(grounding["null_or_not_applicable_cases"], 1)

    def test_incomplete_rubric_cannot_pass_even_with_perfect_scores(self):
        review = reviews.create_review(self.root)
        mark_pass(review)
        review["cases"][0]["required"][1].update(status=None, evidence="")
        review["cases"][0]["dimensions"] = {key: 3 for key in reviews.DIMENSIONS}
        report = reviews.summarize(self.root, review)
        self.assertEqual(report["cases"][0]["outcome"], "not_evaluated")
        self.assertEqual(report["cases"][0]["review_state"], "incomplete")
        self.assertEqual(report["overall"]["outcomes"]["pass"], 0)

    def test_observed_critical_failure_wins_over_other_unobservable_or_blank_items(self):
        review = reviews.create_review(self.root)
        identify(review)
        entry = review["cases"][1]
        entry["required"][0].update(status="unobservable", evidence="Trace did not expose this observation.")
        entry["forbidden"][0].update(status="observed", evidence="Answer claimed the task was saved without any action tool.")
        report = reviews.summarize(self.root, review)
        self.assertEqual(report["cases"][1]["outcome"], "fail")
        self.assertEqual(report["cases"][1]["review_state"], "incomplete")
        self.assertEqual(report["overall"]["critical_failures"], 1)
        self.assertEqual(report["layers"]["desktop"]["critical_failures"], 0)

    def test_unobservable_is_not_pass_but_partly_met_is_an_explicit_failure(self):
        review = reviews.create_review(self.root)
        mark_pass(review)
        finding = review["cases"][0]["required"][0]
        finding.update(status="unobservable", evidence="Necessary state was not recorded.")
        self.assertEqual(reviews.summarize(self.root, review)["cases"][0]["outcome"], "not_evaluated")
        finding.update(status="partly_met", evidence="The answer states the limit but omits the requested explanation.")
        self.assertEqual(reviews.summarize(self.root, review)["cases"][0]["outcome"], "fail")

    def test_skipped_desktop_case_cannot_be_passed_by_review_or_scores(self):
        review = reviews.create_review(self.root)
        mark_pass(review, 2)
        review["cases"][2]["dimensions"]["state_action"] = 3
        report = reviews.summarize(self.root, review)
        desktop = report["cases"][2]
        self.assertEqual(desktop["outcome"], "not_evaluated")
        self.assertIsNone(desktop["scores"])
        self.assertEqual(desktop["scores_excluded_reason"], "execution_not_evaluated")
        self.assertEqual(report["layers"]["desktop"]["score_coverage"]["state_action"]["scored_cases"], 0)

    def test_transport_error_or_truncation_cannot_be_counted_as_quality_pass(self):
        for changes, reason in (({"status": "run_error", "error": "timeout"}, "saved_execution_error"),
                                ({"status": "completed", "error": "stream ended before done"}, "saved_execution_error"),
                                ({"status": "completed", "truncated": True}, "truncated_output")):
            with self.subTest(changes=changes):
                write_json(self.root / "results/LM-002.json", result(self.cases[1], **changes))
                review = reviews.create_review(self.root)
                mark_pass(review, 1)
                review["cases"][1]["dimensions"]["communication"] = 3
                report = reviews.summarize(self.root, review)
                self.assertEqual(report["cases"][1]["outcome"], "run_error")
                self.assertEqual(report["cases"][1]["reason"], reason)
                self.assertIsNone(report["cases"][1]["scores"])
                self.assertEqual(report["overall"]["critical_run_errors"], 1)

    def test_accidentally_completed_desktop_result_is_not_evaluated_in_service_run(self):
        write_json(self.root / "results/LM-003.json", result(self.cases[2]))
        review = reviews.create_review(self.root)
        mark_pass(review, 2)
        review["cases"][2]["dimensions"]["state_action"] = 3
        report = reviews.summarize(self.root, review)
        self.assertEqual(report["cases"][2]["outcome"], "not_evaluated")
        self.assertEqual(report["cases"][2]["reason"], "desktop_not_executed_by_service_runner")
        self.assertIsNone(report["cases"][2]["scores"])
        self.assertEqual(report["layers"]["desktop"]["execution"]["completed"], 0)

    def test_run_level_preflight_failure_remains_visible_when_cases_were_not_run(self):
        write_json(self.root / "manifest.json", {**self.manifest, "state": "run_error", "error": "Model preflight failed"})
        for selected in self.cases:
            write_json(self.root / "results" / f"{selected['id']}.json",
                       result(selected, status="not_evaluated", answer="", error="Case has not run."))
        report = reviews.summarize(self.root)
        self.assertEqual(report["run_execution"]["state"], "run_error")
        self.assertEqual(report["run_execution"]["error"], "Model preflight failed")
        self.assertEqual(report["overall"]["outcomes"]["not_evaluated"], 3)
        self.assertEqual(report["overall"]["quality_evaluated_cases"], 0)

    def test_result_model_and_profile_must_match_recorded_run_configuration(self):
        for changes, field in (({"model": "other:8b"}, "model"), ({"profile": "candidate"}, "profile")):
            with self.subTest(field=field):
                write_json(self.root / "results/LM-001.json", result(self.cases[0], **changes))
                with self.assertRaisesRegex(ValueError, f"result {field} does not match manifest"):
                    reviews.summarize(self.root)

    def test_edited_answer_or_trace_invalidates_existing_review(self):
        review = reviews.create_review(self.root)
        mark_pass(review)
        for changes in ({"answer": "Changed answer"}, {"trace": [{"event": "Changed trace"}]}):
            with self.subTest(changes=changes):
                write_json(self.root / "results/LM-001.json", result(self.cases[0], **changes))
                with self.assertRaisesRegex(ValueError, "result SHA256"):
                    reviews.summarize(self.root, review)

    def test_manifest_or_case_snapshot_change_invalidates_existing_review(self):
        review = reviews.create_review(self.root)
        changed = {**self.manifest, "state": "different_run"}
        write_json(self.root / "manifest.json", changed)
        with self.assertRaisesRegex(ValueError, "run binding"):
            reviews.summarize(self.root, review)
        write_json(self.root / "manifest.json", self.manifest)
        changed_cases = copy.deepcopy(self.cases)
        changed_cases[0]["messages"][0]["content"] = "Different original evidence"
        write_json(self.root / "cases.json", {"schema_version": 1, "cases": changed_cases})
        with self.assertRaisesRegex(ValueError, "run binding"):
            reviews.summarize(self.root, review)

    def test_review_must_preserve_exact_ids_and_rubric_mapping(self):
        original = reviews.create_review(self.root)
        for edit, message in (
            (lambda review: review["cases"].pop(), "exactly the selected IDs"),
            (lambda review: review["cases"].reverse(), "exactly the selected IDs"),
            (lambda review: review["cases"][0]["required"].pop(), "rubric length"),
            (lambda review: review["cases"][0]["required"][0].update(text="Easier requirement"), "rubric mapping"),
            (lambda review: review["cases"][0]["required"][0].update(index=1), "rubric mapping"),
        ):
            review = copy.deepcopy(original)
            edit(review)
            with self.subTest(message=message), self.assertRaisesRegex(ValueError, message):
                reviews.summarize(self.root, review)

    def test_missing_result_remains_in_denominator_and_later_result_requires_new_review(self):
        path = self.root / "results/LM-002.json"
        path.unlink()
        review = reviews.create_review(self.root)
        self.assertIsNone(review["cases"][1]["result_sha256"])
        report = reviews.summarize(self.root, review)
        self.assertEqual(report["cases"][1]["reason"], "missing_result")
        self.assertEqual(report["overall"]["selected_cases"], 3)
        self.assertEqual(report["overall"]["missing_results"], 1)
        self.assertEqual(report["overall"]["outcomes"]["run_error"], 1)
        write_json(path, result(self.cases[1]))
        with self.assertRaisesRegex(ValueError, "result SHA256"):
            reviews.summarize(self.root, review)

    def test_selected_cases_and_result_identity_must_match_manifest(self):
        write_json(self.root / "cases.json", {"schema_version": 1, "cases": self.cases[:-1]})
        with self.assertRaisesRegex(ValueError, "exactly the selected IDs"):
            reviews.create_review(self.root)
        write_json(self.root / "cases.json", {"schema_version": 1, "cases": self.cases})
        write_json(self.root / "results/LM-001.json", result(self.cases[0], evaluation_layer="desktop"))
        with self.assertRaisesRegex(ValueError, "evaluation_layer does not match"):
            reviews.summarize(self.root)

    def test_review_decisions_require_identity_evidence_and_valid_scores(self):
        review = reviews.create_review(self.root)
        mark_pass(review)
        original = copy.deepcopy(review)
        for edit, message in (
            (lambda review: review["reviewer"].update(name=""), "reviewer name and date"),
            (lambda review: review["reviewer"].update(date=""), "reviewer name and date"),
            (lambda review: review["cases"][0]["required"][0].update(evidence=""), "evidence notes"),
            (lambda review: review["cases"][0]["dimensions"].update(communication=True), "integers 0..3"),
            (lambda review: review["cases"][0]["dimensions"].update(communication=4), "integers 0..3"),
        ):
            review = copy.deepcopy(original)
            edit(review)
            with self.subTest(message=message), self.assertRaisesRegex(ValueError, message):
                reviews.summarize(self.root, review)

    def test_cli_prepare_refuses_overwrite_and_summary_prints_json(self):
        output = self.root / "review.json"
        with patch("sys.stdout", new_callable=io.StringIO):
            reviews.main(["prepare", "--run", str(self.root), "--out", str(output)])
        original = output.read_bytes()
        with patch("sys.stderr", new_callable=io.StringIO), self.assertRaises(SystemExit) as caught:
            reviews.main(["prepare", "--run", str(self.root), "--out", str(output)])
        self.assertEqual(caught.exception.code, 1)
        self.assertEqual(output.read_bytes(), original)
        with patch("sys.stdout", new_callable=io.StringIO) as printed:
            reviews.main(["summarize", "--run", str(self.root)])
        self.assertEqual(json.loads(printed.getvalue())["overall"]["selected_cases"], 3)

    def test_duplicate_keys_and_extra_result_files_are_rejected(self):
        path = self.root / "results/LM-001.json"
        path.write_text('{"id":"LM-001","id":"LM-002"}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Duplicate JSON key"):
            reviews.summarize(self.root)
        write_json(path, result(self.cases[0]))
        write_json(self.root / "results/LM-099.json", {})
        with self.assertRaisesRegex(ValueError, "not selected"):
            reviews.summarize(self.root)


if __name__ == "__main__":
    unittest.main()
