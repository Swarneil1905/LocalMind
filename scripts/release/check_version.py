"""Reject a release with mismatched package versions or missing notes."""
import json
import os
from pathlib import Path
import re
import tomllib

ROOT = Path(__file__).resolve().parents[2]


def main():
    config = json.loads((ROOT / "apps/desktop/src-tauri/tauri.conf.json").read_text())
    cargo = tomllib.loads((ROOT / "apps/desktop/src-tauri/Cargo.toml").read_text())
    lock = tomllib.loads((ROOT / "Cargo.lock").read_text())
    version = config["version"]
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise SystemExit("The release version must be a stable semantic version")
    locked = next(p["version"] for p in lock["package"] if p["name"] == "localmind-app")
    if cargo["package"]["version"] != version or locked != version:
        raise SystemExit("Tauri, Cargo.toml and Cargo.lock versions differ")
    ref = os.environ.get("GITHUB_REF", "")
    if ref.startswith("refs/tags/") and ref != f"refs/tags/v{version}":
        raise SystemExit("The pushed tag does not match the application version")
    notes = ROOT / "docs/releases" / f"v{version}.md"
    if not notes.is_file() or not notes.read_text(encoding="utf-8").strip():
        raise SystemExit("Release notes are missing")
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"version={version}\n")
    print(f"Release versions match: {version}")


if __name__ == "__main__":
    main()
