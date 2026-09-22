"""Exercise the real app/lifespan in a clean process and disposable profile.

Database modules and connector paths can already be imported by other tests.
The subprocess keeps this first-install contract independent of test order and
the developer's profile. No real model, connector, or external HTTP is used.
"""

import os
import secrets
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


def _run_contract() -> None:
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    from unittest.mock import AsyncMock, patch
    from fastapi.testclient import TestClient
    from app.main import app
    from app import sync_engine
    from app.storage import data_dir

    profile = Path(os.environ["LOCALMIND_DATA_DIR"])
    assert data_dir() == profile
    token = os.environ["LOCALMIND_TOKEN"]
    headers = {"Authorization": f"Bearer {token}"}
    sources = [{
        "title": "Example source",
        "url": "https://example.com/article",
        "published_at": "2026-10-09",
    }]

    # Keep the real scheduler and lifespan, replacing only work which could
    # contact connected services. Fail if any tested route attempts live HTTP.
    with patch("app.sync_engine._sync_all_connectors", new=AsyncMock()), \
            patch("httpx.AsyncClient.send", side_effect=AssertionError("Unexpected live HTTP")) as network:
        with TestClient(app) as client:
            assert sync_engine._scheduler is not None and sync_engine._scheduler.running
            for route in ("/health", "/conversations", "/projects", "/openapi.json"):
                assert client.get(route).status_code == 401, route
            assert client.get("/health", headers={"Authorization": "Bearer another-launch"}).status_code == 401
            client.headers.update(headers)
            assert client.get("/health").json() == {"status": "ok"}

            created = client.post("/conversations", json={"title": "First desktop chat"})
            assert created.status_code == 200, created.text
            conversation_id = created.json()["id"]
            saved = client.post(f"/conversations/{conversation_id}/turn", json={
                "user_content": "What does this article say?",
                "assistant_content": "A sourced answer.",
                "assistant_thinking": "Internal reasoning",
                "sources": sources,
            })
            assert saved.status_code == 200, saved.text
            messages = client.get(f"/conversations/{conversation_id}/messages").json()["messages"]
            assert [message["role"] for message in messages] == ["user", "assistant"]
            assert messages[1]["sources"] == sources
            assert messages[1]["thinking"] == "Internal reasoning"

            project = client.post("/projects", json={"name": "First desktop project"})
            assert project.status_code == 200, project.text
            project_id = project.json()["id"]
            association = client.post("/projects/assign-conversation", json={
                "conversation_id": conversation_id, "project_id": project_id,
            })
            assert association.status_code == 200, association.text
            associated = client.get(f"/projects/{project_id}/conversations").json()["conversations"]
            assert [item["id"] for item in associated] == [conversation_id]

            task = client.post(f"/projects/{project_id}/tasks", json={"title": "Review sources"})
            assert task.status_code == 200, task.text
            task_id = task.json()["id"]
            task_url = f"/projects/{project_id}/tasks/{task_id}"
            assert client.patch(task_url, json={"status": "not-a-status"}).status_code == 422
            assert client.patch(task_url, json={"status": "done"}).status_code == 200
            task_rows = client.get(f"/projects/{project_id}/tasks").json()["tasks"]
            assert len(task_rows) == 1 and task_rows[0]["status"] == "done"
            all_tasks = client.get("/projects/tasks/all")
            assert all_tasks.status_code == 200, all_tasks.text
            assert all_tasks.json()["tasks"][0]["project_name"] == "First desktop project"
            assert client.delete(task_url).status_code == 200
            assert client.delete(task_url).status_code == 404
            assert client.patch(task_url, json={"status": "open"}).status_code == 404
            assert client.post("/projects/missing-project/tasks", json={"title": "Missing"}).status_code == 404

        assert sync_engine._scheduler is None
        assert sync_engine._initial_sync is None
        # A second real lifespan verifies lock release and persistence across
        # restarts, with startup initialization occurring again in this profile.
        with TestClient(app, headers=headers) as reopened:
            response = reopened.get("/health")
            assert response.status_code == 200
            messages = reopened.get(f"/conversations/{conversation_id}/messages").json()["messages"]
            assert messages[1]["sources"] == sources
            assert reopened.get(f"/projects/{project_id}/tasks").json() == {"tasks": []}
            assert reopened.delete(f"/conversations/{conversation_id}").status_code == 200
            assert reopened.delete(f"/conversations/{conversation_id}").status_code == 404
            assert reopened.delete(f"/projects/{project_id}").status_code == 200
        network.assert_not_called()

    assert sync_engine._scheduler is None
    assert sync_engine._initial_sync is None
    assert (profile / "messages.db").is_file()
    assert (profile / "people.db").is_file()
    assert not (profile / ".legacy-migration-v1").exists(), "Custom profile imported personal legacy data"
    print("Real app startup, authentication, persistence, projects, tasks, and shutdown passed.")


class AppContractTests(unittest.TestCase):
    def test_real_app_first_install_and_restart_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            environment = os.environ.copy()
            environment.update({
                "LOCALMIND_DATA_DIR": str(Path(temporary) / "isolated-profile"),
                "LOCALMIND_TOKEN": secrets.token_hex(32),
                "PYTHONDONTWRITEBYTECODE": "1",
            })
            result = subprocess.run(
                [sys.executable, str(Path(__file__).resolve()), "--run-contract"],
                cwd=Path(__file__).resolve().parents[1],
                env=environment,
                capture_output=True,
                text=True,
                timeout=60,
                check=False,
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    if "--run-contract" in sys.argv:
        _run_contract()
    else:
        unittest.main()
