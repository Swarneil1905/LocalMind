"""Show one real Claude Code assignment and the subsequent Codex decision."""
import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
ASSIGNMENT = (
    "Implement a read-only Task prerequisites section for the selected project on "
    "LocalMind's Projects page. Use Tauri invoke('agent_hub_snapshot', { projectId }) "
    "returning {revision, dependencies:[{task_id,requires_id}]}; Codex owns that "
    "Rust command. Resolve IDs to current task titles, handle loading, empty, service "
    "unavailable, and project switching without displaying stale data. Only edit "
    "React component files and focused UI tests. No mock data in the product, "
    "no package changes, no pushes. Report edited files and limitations. "
    "Codex will independently run tests and review your changes."
)


def main():
    run = Path(sys.argv[1]).resolve()
    assignment = Path(sys.argv[2]).read_text(encoding="utf-8") if len(sys.argv) > 2 else ASSIGNMENT
    run.mkdir(exist_ok=False)
    exe = shutil.which("claude") or str(max(
        (Path(os.environ["APPDATA"]) / "Claude/claude-code").rglob("claude.exe"),
        key=lambda path: path.stat().st_mtime))

    def show(actor, message):
        line = f"{datetime.datetime.now():%H:%M:%S}  {actor}  {message}"
        print(line, flush=True)
        with (run / "activity.txt").open("a", encoding="utf-8") as out:
            print(line, file=out)

    show("CODEX → CLAUDE", assignment)
    show("CODEX", "I own the integration and independent checks.")
    process = subprocess.Popen(
        [exe, "-p", assignment, "--model", "sonnet", "--verbose", "--output-format", "stream-json",
         "--allowedTools", "Read,Glob,Grep,Edit,Write,Skill", "--max-turns", "12"],
        cwd=ROOT, stdout=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    for line in process.stdout:
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if event.get("type") == "assistant":
            for part in event.get("message", {}).get("content", []):
                if part.get("type") == "tool_use":
                    show("CLAUDE", f"{part.get('name')}: {part.get('input', {}).get('file_path', '')}")
                elif part.get("type") == "text":
                    show("CLAUDE", part["text"])
        elif event.get("type") == "result":
            show("CLAUDE → CODEX", event.get("result") or event.get("subtype", "Ended"))
    show("SESSION", f"Claude exited ({process.wait()}). Waiting for Codex's review.")
    while not (run / "review.txt").is_file():
        time.sleep(1)
    show("CODEX REVIEW", (run / "review.txt").read_text(encoding="utf-8-sig"))
    input("Review complete. Press Enter to close this window.")


if __name__ == "__main__":
    main()
