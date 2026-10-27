import unittest

from voice_benchmark import word_errors


class WordErrorTests(unittest.TestCase):
    def test_insertions_deletions_substitutions(self):
        self.assertEqual(word_errors("one two three", "one four"), (2, 3))
        self.assertEqual(word_errors("", "hallucinated speech"), (2, 0))
        self.assertEqual(word_errors("Hello, WORLD!", "hello world"), (0, 2))
