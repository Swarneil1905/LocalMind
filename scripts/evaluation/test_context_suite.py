import copy
from pathlib import Path
import unittest

from check_suite import load_suite, validate_suite


class ContextSupplementTests(unittest.TestCase):
    def setUp(self):
        self.suite, self.report = load_suite(
            Path(__file__).resolve().parents[2] / "evaluation" / "context_cases.json",
        )

    def test_five_long_development_cases_have_separate_stable_ids(self):
        self.assertEqual(self.report["cases"], 5)
        self.assertEqual(self.report["splits"], {"development": 5})
        self.assertTrue(all(len(c["messages"]) > 40 for c in self.suite["cases"]))
        self.assertTrue(all(sum(len(m["content"]) for m in c["messages"]) > 16000
                            for c in self.suite["cases"]))

    def test_supplement_cannot_change_baseline_rules_or_use_heldout(self):
        for mutate in (
            lambda s: s.pop("suite_kind"),
            lambda s: s["cases"][0].update(split="held_out"),
            lambda s: s["cases"][0].update(id="LM-001"),
            lambda s: s["cases"][0].update(evaluation_layer="desktop"),
            lambda s: s["cases"].pop(),
        ):
            suite = copy.deepcopy(self.suite)
            mutate(suite)
            with self.assertRaises(ValueError):
                validate_suite(suite)


if __name__ == "__main__":
    unittest.main()
