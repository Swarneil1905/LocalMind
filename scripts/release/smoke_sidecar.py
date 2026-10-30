"""Check the frozen release sidecar over HTTP using only a disposable profile.

No Python app imports, mocks, model calls, or connector credentials are used.
The real lifespan and scheduler run; an empty profile and temporary home keep
connectors disconnected (including macOS iMessage) and extraction has no input.
"""

import argparse
import json
import os
import secrets
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from contextlib import contextmanager
from pathlib import Path

from build_sidecar import TARGETS, binary_path, validate_target


class SmokeFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SmokeFailure(message)


def isolated_environment(scratch: Path, profile: Path, port: int, token: str) -> dict[str, str]:
    environment = {
        key: value for key, value in os.environ.items()
        if not key.upper().startswith(("LOCALMIND_", "GOOGLE_", "PYTHON"))
    }
    home = scratch / "home"
    home.mkdir(exist_ok=True)
    extraction = scratch / "tmp"
    extraction.mkdir(exist_ok=True)
    # HOME matters on macOS: iMessage reads its database outside LocalMind's
    # data directory. USERPROFILE serves the same isolation purpose on Windows.
    environment.update({
        "HOME": str(home),
        "USERPROFILE": str(home),
        "APPDATA": str(home / "AppData/Roaming"),
        "LOCALAPPDATA": str(home / "AppData/Local"),
        "XDG_DATA_HOME": str(home / ".local/share"),
        "XDG_CONFIG_HOME": str(home / ".config"),
        "XDG_CACHE_HOME": str(home / ".cache"),
        # Keep onefile extraction under this test too. Windows process-tree
        # termination can prevent the bootloader from removing its _MEI folder.
        "TEMP": str(extraction),
        "TMP": str(extraction),
        "TMPDIR": str(extraction),
        "LOCALMIND_DATA_DIR": str(profile),
        "LOCALMIND_PORT": str(port),
        "LOCALMIND_TOKEN": token,
    })
    return environment


class Client:
    def __init__(self, port: int, token: str):
        self.base_url = f"http://127.0.0.1:{port}"
        self.token = token
        # CI/user proxy settings must never receive this loopback bearer token.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(self, method: str, path: str, payload=None, *, expected=200, token=True, timeout=5):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = f"Bearer {self.token if token is True else token}"
        request = urllib.request.Request(
            self.base_url + path,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers=headers,
            method=method,
        )
        try:
            response = self.opener.open(request, timeout=timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read().decode("utf-8")
            require(response.status == expected,
                    f"{method} {path}: expected HTTP {expected}, got {response.status}: {body}")
            return json.loads(body) if body else None


def stop_process(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return
    if os.name == "nt":
        # A onefile executable has a bootloader parent and an application child.
        # Windows terminate() only kills the parent; kill this owned process tree.
        # This also works on CI without a console to deliver CTRL_BREAK_EVENT to.
        result = subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            capture_output=True, text=True, timeout=20,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        if result.returncode and process.poll() is None:
            raise SmokeFailure(f"Could not stop sidecar process tree: {result.stderr}")
    else:
        os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=20)
    except subprocess.TimeoutExpired:
        if os.name != "nt":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
        process.wait(timeout=10)
        raise SmokeFailure("Sidecar did not stop within 20 seconds")


@contextmanager
def running_sidecar(binary: Path, scratch: Path, profile: Path, timeout: float):
    # Reserve a random port until immediately before launching the executable.
    # A collision is a smoke failure, never a connection to an unauthenticated
    # unrelated service: readiness requires this launch's unique bearer token.
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    token = secrets.token_hex(32)
    environment = isolated_environment(scratch, profile, port, token)
    client = Client(port, token)
    log_path = scratch / f"sidecar-{port}.log"
    options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {
        "start_new_session": True,
    }
    with log_path.open("wb") as log:
        process = subprocess.Popen(
            [str(binary)], cwd=scratch, env=environment,
            stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, **options,
        )
        try:
            deadline = time.monotonic() + timeout
            while True:
                require(process.poll() is None,
                        f"Frozen sidecar exited during startup ({process.returncode})")
                try:
                    response = client.request("GET", "/health")
                    require(response == {"status": "ok"}, "Unexpected health response")
                    break
                except (urllib.error.URLError, TimeoutError, ConnectionError):
                    require(time.monotonic() < deadline, "Frozen sidecar readiness timed out")
                    time.sleep(0.25)
            yield client
            require(process.poll() is None, "Sidecar exited before shutdown")
        finally:
            stop_process(process)
    output = log_path.read_text(errors="replace")
    require("apscheduler not installed" not in output,
            "The packaged scheduler could not import its dependencies")
    require("[Sync] Profile extraction failed" not in output,
            "The packaged empty-profile startup job failed")
    # Display captured diagnostics on failure in smoke(), but never dump tokens.


def smoke(binary: Path, timeout: float) -> None:
    require(binary.is_file() and binary.stat().st_size > 0,
            f"Frozen sidecar executable is missing or empty: {binary}")
    with tempfile.TemporaryDirectory(prefix="localmind-sidecar-smoke-") as temporary:
        scratch = Path(temporary)
        profile = scratch / "profile"
        sources = [{
            "title": "Packaged runtime source",
            "url": "https://example.com/release-smoke",
            "published_at": "2026-01-01",
        }]
        try:
            with running_sidecar(binary, scratch, profile, timeout) as client:
                client.request("GET", "/health", expected=401, token=False)
                client.request("GET", "/health", expected=401, token="different-launch")
                connectors = client.request("GET", "/connectors")
                require(bool(connectors) and all(row["status"] == "disconnected" or
                        (row["id"] in ("whatsapp_web", "imessage") and row["status"] == "disabled" and row["available"] is False) or
                        (os.name != "nt" and row["id"] == "gmail" and row["status"] == "locked") for row in connectors),
                        "A connector unexpectedly has access to an existing session")

                extensions = client.request("GET", "/extensions/status")
                require(not extensions["planner"]["enabled"] and not extensions["images"]["available"], "Extension unexpectedly enabled")
                client.request("POST", "/extensions/planner-state", {"enabled": True})
                plan = client.request("POST", "/extensions/planner-call", {"name": "plan_tasks", "arguments": {"tasks": [{"title": "Synthetic release check"}]}})
                require(plan["saved"] is False, "Planner unexpectedly saved a task")
                client.request("POST", "/extensions/planner-state", {"enabled": False})
                client.request("POST", "/extensions/planner-call", {"name": "plan_tasks", "arguments": {"tasks": [{"title": "Blocked"}]}}, expected=400)
                package = client.request("GET", "/extensions/recipe-export")["package"]
                receipt = client.request("POST", "/extensions/recipe-import", {"package": package})
                require(receipt["synthetic"] and all(receipt["checks"].values()), "Recipe checks failed")
                client.request("POST", "/extensions/recipe-import", {"package": "{}"}, expected=400)

                voice = client.request("GET", "/voice/status")
                require(not voice["stt_ready"] and bool(voice.get("disabled_reason")),
                        "Voice beta gate unexpectedly enabled")
                preview = client.request("GET", "/mcp/preview")
                require(not preview["enabled"], "MCP unexpectedly enabled before approval")
                client.request("POST", "/mcp/call", {"name": "read_fixture", "arguments": {"path": "sample.txt"}}, expected=400)
                enabled = client.request("POST", "/mcp/enable", {"approval": preview["approval"]}, timeout=45)
                require(enabled["enabled"], "Owned MCP server failed to initialize in the frozen runtime")
                try:
                    result = client.request("POST", "/mcp/call", {"name": "read_fixture", "arguments": {"path": "sample.txt"}})
                    require(result.get("untrusted") is True and "No user files" in result.get("text", ""),
                            "Unexpected owned tool result")
                    client.request("POST", "/mcp/call", {"name": "read_fixture", "arguments": {"path": "../secret"}}, expected=400)
                finally:
                    require(not client.request("POST", "/mcp/stop", {}, timeout=25)["enabled"], "Owned MCP failed to stop")

                conversation = client.request("POST", "/conversations", {"title": "Release smoke"})
                conversation_id = conversation["id"]
                client.request("POST", f"/conversations/{conversation_id}/turn", {
                    "user_content": "A saved question",
                    "assistant_content": "A saved answer",
                    "assistant_thinking": "Saved reasoning",
                    "sources": sources,
                })
                messages_url = f"/conversations/{conversation_id}/messages"
                messages = client.request("GET", messages_url)["messages"]
                require([row["role"] for row in messages] == ["user", "assistant"],
                        "Conversation roles were not persisted")
                require(messages[1]["sources"] == sources, "Source metadata did not roundtrip")

                project = client.request("POST", "/projects", {"name": "Release project"})
                project_url = f"/projects/{project['id']}"
                client.request("PATCH", project_url, {"name": "Verified project"})
                require(client.request("GET", project_url)["name"] == "Verified project",
                        "Project rename did not persist")
                client.request("POST", "/projects/assign-conversation", {
                    "conversation_id": conversation_id, "project_id": project["id"],
                })
                assigned = client.request("GET", project_url + "/conversations")["conversations"]
                require([row["id"] for row in assigned] == [conversation_id],
                        "First-launch conversation assignment failed")

                task = client.request("POST", project_url + "/tasks", {"title": "Verify bundle"})
                task_url = project_url + f"/tasks/{task['id']}"
                client.request("PATCH", task_url, {"status": "invalid"}, expected=422)
                client.request("PATCH", task_url, {"status": "done"})
                tasks = client.request("GET", project_url + "/tasks")["tasks"]
                require(len(tasks) == 1 and tasks[0]["status"] == "done",
                        "Task update did not persist")
                client.request("POST", "/projects/missing/tasks", {"title": "Missing"}, expected=404)

            # Restart the actual executable: verify persistence and lock release,
            # rather than just reading from the first process's memory.
            with running_sidecar(binary, scratch, profile, timeout) as client:
                require(not client.request("GET", "/mcp/preview")["enabled"], "MCP approval survived restart")
                extensions = client.request("GET", "/extensions/status")
                require(not extensions["planner"]["enabled"], "Disabled planner became enabled after restart")
                require(extensions["recipe"]["registered"], "Synthetic recipe registration did not persist")
                messages = client.request("GET", messages_url)["messages"]
                require(messages[1]["sources"] == sources, "Sources were lost after restart")
                require(messages[1]["thinking"] == "Saved reasoning", "Reasoning was lost")
                tasks = client.request("GET", project_url + "/tasks")["tasks"]
                require(len(tasks) == 1 and tasks[0]["id"] == task["id"]
                        and tasks[0]["status"] == "done", "Task was lost after restart")
                client.request("DELETE", task_url)
                client.request("DELETE", task_url, expected=404)
                client.request("PATCH", task_url, {"status": "open"}, expected=404)
                client.request("DELETE", f"/conversations/{conversation_id}")
                client.request("DELETE", f"/conversations/{conversation_id}", expected=404)
                client.request("DELETE", project_url)
                client.request("GET", project_url, expected=404)

            require(not (profile / ".legacy-migration-v1").exists(),
                    "Smoke profile unexpectedly migrated personal data")
            require((profile / "messages.db").is_file(), "Startup did not create the profile")
        except Exception:
            for log_path in sorted(scratch.glob("sidecar-*.log")):
                print(f"\n--- {log_path.name} ---\n{log_path.read_text(errors='replace')[-16000:]}")
            raise
    print("Frozen sidecar passed: startup, auth, sources, projects, tasks, restart and shutdown.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, choices=TARGETS)
    parser.add_argument("--binary", type=Path,
                        help="Verify a sidecar extracted from an installer/app instead of the build output")
    parser.add_argument("--startup-timeout", type=float, default=90)
    arguments = parser.parse_args()
    if arguments.startup_timeout <= 0:
        parser.error("--startup-timeout must be positive")
    try:
        validate_target(arguments.target)
        binary = arguments.binary.resolve() if arguments.binary else binary_path(arguments.target)
        smoke(binary, arguments.startup_timeout)
    except (OSError, RuntimeError, subprocess.SubprocessError, ValueError) as error:
        parser.exit(1, f"Sidecar smoke failed: {error}\n")


if __name__ == "__main__":
    main()
