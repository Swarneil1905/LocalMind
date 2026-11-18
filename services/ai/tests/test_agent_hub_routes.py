import os
import tempfile
import unittest
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.api.middleware.auth import verify_bearer_token
from app.api.routes.agent_hub import router
from app.db import projects


class AgentHubRouteTests(unittest.TestCase):
    def setUp(self):
        profile = tempfile.TemporaryDirectory()
        self.addCleanup(profile.cleanup)
        env = patch.dict(os.environ, LOCALMIND_DATA_DIR=profile.name, LOCALMIND_TOKEN="hub-token")
        env.start()
        self.addCleanup(env.stop)
        app = FastAPI()
        app.middleware("http")(verify_bearer_token)
        app.include_router(router)
        self.client = TestClient(app, headers={"Authorization": "Bearer hub-token"})
        self.addCleanup(self.client.close)
        self.pid = projects.create_project("Demo")["id"]
        self.a, self.b, self.c = [projects.create_task(self.pid, t)["id"] for t in ("Plan", "Build", "Ship")]
        self.url = f"/projects/{self.pid}/hub"

    def add(self, task, requires, revision, project=None):
        url = f"/projects/{project}/hub" if project else self.url
        return self.client.post(f"{url}/dependencies", json={
            "task_id": task, "requires_id": requires, "expected_revision": revision})

    def test_requires_token(self):
        self.assertEqual(self.client.get(self.url, headers={"Authorization": "Bearer wrong"}).status_code, 401)

    def test_add_then_snapshot(self):
        response = self.add(self.b, self.a, 0)
        self.assertEqual((response.status_code, response.json()), (200, {"revision": 1}))
        self.assertEqual(self.client.get(self.url).json(), {
            "revision": 1, "dependencies": [{"task_id": self.b, "requires_id": self.a}]})

    def test_stale_revision_and_cycle_conflict(self):
        self.add(self.b, self.a, 0)
        self.assertEqual(self.add(self.c, self.a, 0).status_code, 409)
        self.assertEqual(self.add(self.a, self.b, 1).status_code, 409)
        self.assertEqual(len(self.client.get(self.url).json()["dependencies"]), 1)

    def test_cross_project_rejected(self):
        other = projects.create_project("Separate")["id"]
        foreign = projects.create_task(other, "Private")["id"]
        self.assertEqual(self.add(self.a, foreign, 0).status_code, 404)
        # Tasks from this project cannot be edited through another project's route either.
        self.assertEqual(self.add(self.b, self.a, 0, project=other).status_code, 404)
        self.assertEqual(self.client.get(self.url).json(), {"revision": 0, "dependencies": []})
        self.assertEqual(self.client.get(f"/projects/{other}/hub").json()["dependencies"], [])


if __name__ == "__main__":
    unittest.main()
