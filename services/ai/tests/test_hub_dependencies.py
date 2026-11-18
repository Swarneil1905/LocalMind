import json
import os
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from app.db import hub_dependencies as graph
from app.db import projects


class HubDependenciesTests(unittest.TestCase):
    def setUp(self):
        profile = self.enter_context(tempfile.TemporaryDirectory())
        self.enter_context(patch.dict(os.environ, LOCALMIND_DATA_DIR=profile))
        self.pid = projects.create_project("Demo")["id"]
        self.a, self.b, self.c = [projects.create_task(self.pid, label)["id"]
                                  for label in ("Plan", "Build", "Review")]

    def enter_context(self, cm):
        self.addCleanup(cm.__exit__, None, None, None)
        return cm.__enter__()

    def test_reload_cycle_and_task_deletion(self):
        graph.require(self.pid, self.b, self.a, 0)
        graph.require(self.pid, self.c, self.b, 1)
        with self.assertRaisesRegex(ValueError, "cycle"):
            graph.require(self.pid, self.a, self.c, 2)
        self.assertEqual(len(graph.snapshot(self.pid)["dependencies"]), 2)
        projects.delete_task(self.pid, self.b)
        state = graph.snapshot(self.pid)
        self.assertEqual(state["dependencies"], [])
        self.assertGreater(state["revision"], 2)

    def test_fresh_service_process_reads_committed_graph(self):
        graph.require(self.pid, self.b, self.a, 0)
        script = ("import json,sys; from app.db.hub_dependencies import snapshot; "
                  "print(json.dumps(snapshot(sys.argv[1])))")
        output = subprocess.check_output([sys.executable, "-c", script, self.pid], timeout=15)
        self.assertEqual(json.loads(output), {"revision": 1, "dependencies": [
            {"task_id": self.b, "requires_id": self.a}]})

    def test_concurrent_edits_have_one_winner(self):
        def edit(task):
            try:
                return graph.require(self.pid, task, self.a, 0)
            except graph.RevisionConflict:
                return 0

        with ThreadPoolExecutor(2) as pool:
            self.assertCountEqual(pool.map(edit, (self.b, self.c)), [1, 0])
        self.assertEqual(len(graph.snapshot(self.pid)["dependencies"]), 1)

    def test_other_project_cannot_supply_prerequisite(self):
        other = projects.create_project("Separate")["id"]
        foreign = projects.create_task(other, "Private")["id"]
        with self.assertRaises(KeyError):
            graph.require(self.pid, self.a, foreign, 0)
        self.assertEqual(graph.snapshot(self.pid), {"revision": 0, "dependencies": []})
