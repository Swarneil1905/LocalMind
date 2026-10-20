"""Task-write intent checks run without a model, database, or network."""

import unittest

from app.action_boundary import unsupported_task_response


class TaskActionBoundaryTests(unittest.TestCase):
    def test_explicit_task_writes_return_honest_manual_steps(self):
        requests = (
            "Save that as a task called 'Practice one boosting step' in my Learning project.",
            "Please save this as a task.",
            "Can you please add that to my task list?",
            "Would you create a task called 'Read one chapter' in my Study project?",
            "I would like you to create one task in Learning Lab called 'Calculate one update'.",
            "Add a task to call the dentist.",
            "Save the task.",
            "Please add task 'Buy milk'.",
            "Create a task in the Work project.",
            "Create a task in project Work.",
            "Create a new task on my task board.",
            "Add this exercise as a task in my Study project.",
            "Add a task to practice recursion in my Study project.",
            "  PLEASE SAVE THAT AS A TASK.  ",
        )
        for message in requests:
            with self.subTest(message=message):
                answer = unsupported_task_response(message)
                self.assertIsNotNone(answer)
                self.assertIn("haven’t created or saved a task", answer)
                self.assertIn("**Tasks**", answer)
                self.assertIn("**Add task**", answer)
                self.assertIn("If the project does not exist", answer)

    def test_response_does_not_invent_or_echo_title_or_project(self):
        answer = unsupported_task_response(
            "Save that as a task called 'Secret title 173' in my Private project."
        )
        self.assertNotIn("Secret title", answer)
        self.assertNotIn("Private", answer)
        self.assertNotIn("Learning", answer)
        self.assertNotIn("http", answer)

    def test_quoted_task_titles_do_not_trigger_content_or_code_exclusions(self):
        for title in (
            "'Draft a Python example'", '"Create code using asyncio"',
            "‘Do not save passwords’", "“Write a task description”",
            "`Review a code snippet`",
        ):
            with self.subTest(title=title):
                self.assertIsNotNone(unsupported_task_response(f"Add a task called {title}."))

    def test_drafts_explanations_and_quoted_transformations_stay_with_chat(self):
        requests = (
            "Draft a task description for reading one chapter.",
            "Explain how to add a task in this app.",
            "Can you explain how to save that as a task?",
            "Rewrite 'Save that as a task' more politely.",
            "Translate: Add a task called Buy milk.",
            "'Save that as a task called Buy milk.'",
            '"Create a task in the Work project."',
            "```\nSave that as a task.\n```",
            "> Save that as a task.",
            "Create a task description for reading one chapter.",
            "Create a task template called Weekly review.",
            "Add a task example to this tutorial.",
            "Save this as a task description.",
            "Create a task called Read a chapter as a draft.",
            "Create a task called Read a chapter, but do not save it.",
            "Add a task called Buy milk as an example.",
            "Add a task called Buy milk, but don't actually add it.",
        )
        for message in requests:
            with self.subTest(message=message):
                self.assertIsNone(unsupported_task_response(message))

    def test_learning_and_code_tasks_stay_with_chat(self):
        requests = (
            "Make this a learning task and ask me one question.",
            "Create a task for learning XGBoost.",
            "Create a task for me to practice recursion.",
            "Create a learning task about boosting.",
            "Add a task to practice recursion.",
            "Create a task that teaches me how recursion works.",
            "Create code that adds a task to a task list.",
            "Add task creation to this Python function.",
            "Add a task using asyncio.create_task().",
            "Create a task named 'worker' in Python.",
            "Add a task to the event loop.",
            "Save this code as task.py.",
            "Save this example as a task template.",
        )
        for message in requests:
            with self.subTest(message=message):
                self.assertIsNone(unsupported_task_response(message))

    def test_other_requests_are_not_classified_as_task_writes(self):
        for message in ("", "What tasks do I have?", "Save a file called task.txt.",
                        "Add two numbers.", "Create a project called Study.",
                        "I previously asked you to save that as a task.",
                        "Do not create a task."):
            with self.subTest(message=message):
                self.assertIsNone(unsupported_task_response(message))


if __name__ == "__main__":
    unittest.main()
