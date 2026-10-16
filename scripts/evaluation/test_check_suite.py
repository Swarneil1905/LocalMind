"""Protect the split and fixture boundary before a model runner uses the corpus."""

import copy
import unittest

from check_suite import load_suite, validate_suite


class SuiteContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source, cls.report = load_suite()

    def setUp(self):
        self.suite = copy.deepcopy(self.source)

    def test_checked_in_corpus_has_both_execution_layers(self):
        self.assertEqual(self.report["cases"], 30)
        self.assertGreater(self.report["layers"]["service"], 0)
        self.assertGreater(self.report["layers"]["desktop"], 0)

    def test_duplicate_ids_cannot_overwrite_case_results(self):
        self.suite["cases"][1]["id"] = self.suite["cases"][0]["id"]
        with self.assertRaisesRegex(ValueError, "duplicate case ID"):
            validate_suite(self.suite)

    def test_accidental_holdout_relabel_is_rejected(self):
        case = next(c for c in self.suite["cases"] if c["split"] == "held_out")
        case["split"] = "development"
        with self.assertRaisesRegex(ValueError, "20 development"):
            validate_suite(self.suite)

    def test_stored_answer_cannot_be_used_as_the_user_prompt(self):
        self.suite["cases"][0]["messages"][-1]["role"] = "assistant"
        with self.assertRaisesRegex(ValueError, "expected user"):
            validate_suite(self.suite)

    def test_swapping_splits_cannot_expose_a_holdout_for_tuning(self):
        self.suite["cases"][0]["split"] = "held_out"
        self.suite["cases"][-1]["split"] = "development"
        with self.assertRaisesRegex(ValueError, "frozen split"):
            validate_suite(self.suite)

    def test_external_evidence_url_is_not_a_fixture(self):
        case = next(c for c in self.suite["cases"] if c.get("evidence"))
        case["evidence"][0]["url"] = "https://news.example.com/private-article"
        with self.assertRaisesRegex(ValueError, "synthetic .invalid"):
            validate_suite(self.suite)

    def test_fixed_clock_cannot_be_dropped_from_evidence_case(self):
        case = next(c for c in self.suite["cases"] if c.get("evidence"))
        case.pop("as_of")
        with self.assertRaisesRegex(ValueError, "fixed as_of"):
            validate_suite(self.suite)

    def test_empty_rubric_cannot_be_reported_as_an_evaluation(self):
        self.suite["cases"][0]["expectation"]["required"] = []
        with self.assertRaisesRegex(ValueError, "empty required"):
            validate_suite(self.suite)

    def test_null_clock_does_not_enable_time_dependent_fixtures(self):
        case = next(c for c in self.suite["cases"] if c.get("evidence"))
        case["as_of"] = None
        with self.assertRaisesRegex(ValueError, "fixed as_of"):
            validate_suite(self.suite)


if __name__ == "__main__":
    unittest.main()
