"""Routing regressions without a provider, model, or user profile."""

import unittest
from datetime import datetime, timezone
from unittest.mock import patch

from app.search.planning import needs_freshness, plan_query, search_opted_out


class SearchPlanningTests(unittest.TestCase):
    def test_ordinary_teaching_drafting_and_calculation_stay_local(self):
        messages = [
            "Walk me through XGBoost using a bicycle rental example.",
            "Explain photosynthesis with an analogy.",
            "Teach me why database indexes speed up reads.",
            "How does binary search work?",
            "Now explain gradient descent with one numerical example.",
            "Current predictions are [12, 15]. Add [-1, 2] and explain the result.",
            "Draft a short reply to Mira about our report.",
            "Make this a learning exercise. Ask one question and wait.",
            "Can you please rewrite that more clearly?",
            "Translate this sentence into French: Today's news is surprising.",
            "Summarize this paragraph: The latest release was delayed yesterday.",
            "Rewrite this sentence: Read the article at https://example.invalid/story for the latest news.",
            "Explain electric current in a circuit.",
            "Explain how to use references in Rust.",
            "Explain how to cite sources in an essay.",
            "Teach me one exercise today.",
        ]
        for message in messages:
            with self.subTest(message=message):
                self.assertIsNone(plan_query(message, []))

    def test_explicit_opt_out_overrides_freshness_links_and_source_words(self):
        messages = [
            "Explain that idea. You do not need to search for this.",
            "Don't search the web; explain gradient descent.",
            "Don’t browse. What might today's news mean, in general?",
            "No need to search for the latest release. Use what you know.",
            "Answer without web access, and say if the current price is unknown.",
            "Please skip searching and help me draft a note.",
            "Do not use the internet. Explain the link format https://example.invalid/a.",
            "Use only the provided text: the latest update mentions a new library.",
            "Answer offline and explain this source citation.",
            "Don't look it up. Tell me if the current rate is unknown.",
            "No searching; use the information already here.",
            "Answer without browsing, even if you don't know today's score.",
            "No need for a web search. Explain binary trees.",
        ]
        for message in messages:
            with self.subTest(message=message):
                self.assertTrue(search_opted_out(message))
                self.assertIsNone(plan_query(message, []))
        self.assertFalse(search_opted_out("Do not guess. Search the web for a dated source."))

    def test_explicit_sources_and_current_information_request_retrieval(self):
        messages = [
            "Search the web for the original gradient boosting paper.",
            "Can you look up the official SQLite documentation?",
            "Explain XGBoost and cite the original paper.",
            "Find sources for the claim about the bridge opening.",
            "Using the supplied sources, what changed at the library?",
            "Summarize the policy from the supplied page in one sentence.",
            "Please summarize today's headlines.",
            "What is the latest Python release?",
            "What is the current weather in Seattle?",
            "What happened this week at the harbor?",
            "I need this week’s transit news.",
        ]
        for message in messages:
            with self.subTest(message=message):
                self.assertIsNotNone(plan_query(message, []))

    def test_one_requested_link_is_fetched_directly(self):
        url = "https://example.invalid/article"
        for message in (url, f"Summarize {url}.", f"Read this page: {url}", f"Summarize this: {url}"):
            with self.subTest(message=message):
                self.assertEqual(plan_query(message, []), url)

    def test_news_followup_selects_story_but_teaching_does_not_inherit_search(self):
        history = [
            {"role": "user", "content": "What is the latest city news?"},
            {"role": "assistant", "content": "A road reopened.\nCollege football: Oklahoma played Texas in the Red River Rivalry."},
        ]
        query = plan_query("Can you provide more details about the college football game mentioned?", history)
        self.assertIn("Oklahoma", query)
        self.assertIn("Texas", query)
        self.assertNotIn("A road reopened", query)
        self.assertTrue(needs_freshness(query))
        date_query = plan_query("When did that happen?", history)
        self.assertIsNotNone(date_query)
        self.assertFalse(needs_freshness(date_query))
        for message in ("Explain that concept with an analogy.", "Now teach me how football scoring works.",
                        "Make that a learning exercise.", "Please rewrite that as two short sentences."):
            with self.subTest(message=message):
                self.assertIsNone(plan_query(message, history))
        history += [{"role": "user", "content": "Now help me understand binary search."},
                    {"role": "assistant", "content": "It halves a sorted search interval."}]
        self.assertIsNone(plan_query("Tell me more about it.", history))

    def test_teaching_followup_keeps_history_for_model_without_searching(self):
        history = [{"role": "user", "content": "Why do boosted trees make successive corrections?"},
                   {"role": "assistant", "content": "The next tree adds a correction to the current prediction."}]
        self.assertIsNone(plan_query("Explain it more deeply with one small example.", history))
        self.assertIsNone(plan_query("Give me more details about that.", history))
        history[-1]["content"] += " Optional reading: https://example.invalid/trees."
        self.assertIsNone(plan_query("Give me more details about that.", history))

    def test_drafting_schedule_and_shared_updates_are_local_but_external_inputs_are_not(self):
        for message in ("Write a study plan for today.", "Make a checklist for tonight.",
                        "Please create a revision plan for this week.",
                        "Draft a summary of the latest updates I shared.",
                        "Write a reply using the recent details we discussed."):
            with self.subTest(message=message):
                self.assertFalse(needs_freshness(message))
                self.assertIsNone(plan_query(message, []))
        for message in ("Make a checklist for tonight using today's weather forecast.",
                        "Draft a summary of this week's transit news.",
                        "Write a briefing from the latest official release notes.",
                        "Draft a summary using the policy from the supplied page.",
                        "Draft an email about today's headlines and the latest updates I shared."):
            with self.subTest(message=message):
                self.assertIsNotNone(plan_query(message, []))

    def test_historical_date_followups_retain_sources_through_referential_turns(self):
        history = [
            {"role": "user", "content": "What is the latest city news?"},
            {"role": "assistant", "content": "College football: Oklahoma beat Texas in the Red River Rivalry."},
        ]
        query = plan_query("What year was the football game?", history)
        self.assertIsNotNone(query)
        self.assertIn("oklahoma", query.lower())
        self.assertIn("texas", query.lower())
        self.assertFalse(needs_freshness(query))
        self.assertEqual(plan_query("When did Oklahoma beat Texas?", history),
                         "When did Oklahoma beat Texas?")
        history += [
            {"role": "user", "content": "Can you provide more details about the college football game mentioned?"},
            {"role": "assistant", "content": "Oklahoma beat Texas by a narrow margin in that football game."},
        ]
        date_query = plan_query("When did that game happen?", history)
        self.assertIsNotNone(date_query)
        self.assertFalse(needs_freshness(date_query))
        self.assertTrue(needs_freshness(plan_query("Tell me more details about that game.", history)))
        history += [{"role": "user", "content": "Write a study plan for today."},
                    {"role": "assistant", "content": "Start with your notes and one exercise."}]
        self.assertIsNone(plan_query("Tell me more details about that.", history))

    def test_freshness_distinguishes_calendar_requests_from_conversation_words(self):
        for message in ("today's transit news", "this week's ferry alerts", "this week’s library news",
                        "What happened this week?", "current exchange rates", "the price right now",
                        "What is happening now in town?", "the latest PostgreSQL release"):
            with self.subTest(message=message):
                self.assertTrue(needs_freshness(message))
        for message in ("Now explain binary search.", "Now explain weather forecasting.",
                        "Explain electric current.", "Use current predictions in this calculation.",
                        "Explain your latest example.", "Teach me trees today.",
                        "Rewrite this: Today's headlines are surprising."):
            with self.subTest(message=message):
                self.assertFalse(needs_freshness(message))

    def test_game_date_query_keeps_named_subject_and_rank_abbreviation(self):
        history = [
            {"role": "user", "content": "What is the latest city news?"},
            {"role": "assistant", "content": "A road reopened. College football: Oklahoma defeated No. 1 Texas in the Red River game."},
        ]
        details = "Can you provide more details about the college football game mentioned?"
        query = plan_query(details, history)
        self.assertIn("Oklahoma defeated No. 1 Texas", query)
        self.assertNotIn("road reopened", query)
        history += [
            {"role": "user", "content": details},
            {"role": "assistant", "content": "Oklahoma beat Texas 34-30 in the Red River game, according to the supplied article."},
        ]
        date_query = plan_query("When did that game happen?", history)
        self.assertIn("oklahoma beat texas", date_query.lower())
        self.assertIn("red river", date_query.lower())
        self.assertFalse(needs_freshness(date_query))

    def test_fresh_query_uses_clock_but_direct_link_stays_a_url(self):
        class FixedDateTime(datetime):
            @classmethod
            def now(cls, tz=None):
                return cls(2026, 10, 19, 12, tzinfo=timezone.utc)

        with patch("app.search.planning.datetime", FixedDateTime):
            query = plan_query("What is this week's transit news?", [])
            self.assertTrue(query.startswith("2026-10-19 "))
            self.assertEqual(plan_query("Check today's notice at https://example.invalid/notice", []),
                             "https://example.invalid/notice")


if __name__ == "__main__":
    unittest.main()
