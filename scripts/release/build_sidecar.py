"""Freeze the AI service with the native Python for the requested Rust target.

Run after installing services/ai/requirements.txt and PyInstaller. Python and its
native dependencies must match the target; renaming a binary is not cross compilation.
"""

import argparse
import os
import platform
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
TARGETS = {
    "x86_64-pc-windows-msvc": ("Windows", "x86_64"),
    "x86_64-apple-darwin": ("Darwin", "x86_64"),
    "aarch64-apple-darwin": ("Darwin", "aarch64"),
    "x86_64-unknown-linux-gnu": ("Linux", "x86_64"),
}


def validate_target(target: str) -> None:
    machine = platform.machine().lower()
    machine = {"amd64": "x86_64", "arm64": "aarch64"}.get(machine, machine)
    actual = (platform.system(), machine)
    expected = TARGETS[target]
    if actual != expected or struct.calcsize("P") != 8:
        raise RuntimeError(
            f"Target {target} requires native 64-bit {expected[0]}/{expected[1]} Python; "
            f"this interpreter is {actual[0]}/{actual[1]} ({struct.calcsize('P') * 8}-bit). "
            "Select a matching runner and Python architecture."
        )


def binary_path(target: str) -> Path:
    extension = ".exe" if TARGETS[target][0] == "Windows" else ""
    return ROOT / "apps/desktop/src-tauri/binaries" / f"localmind-sidecar-{target}{extension}"


def build(target: str) -> Path:
    validate_target(target)
    service = ROOT / "services/ai"
    with tempfile.TemporaryDirectory(prefix="localmind-freeze-") as temporary:
        scratch = Path(temporary)
        environment = os.environ.copy()
        # Collection hooks can import app modules. Keep their database paths out
        # of the builder's real profile, without bundling this temporary profile.
        environment["LOCALMIND_DATA_DIR"] = str(scratch / "build-profile")
        command = [
            sys.executable, "-m", "PyInstaller",
            "--noconfirm", "--clean", "--onefile", "--noupx",
            "--name", "localmind-sidecar",
            "--distpath", str(scratch / "dist"),
            "--workpath", str(scratch / "work"),
            "--specpath", str(scratch),
            "--paths", str(service),
            "--collect-submodules", "app",
            # Uvicorn selects its loop/protocol/lifespan implementations by name.
            "--collect-submodules", "uvicorn",
            # APScheduler resolves the interval trigger through entry points.
            "--collect-submodules", "apscheduler",
            "--copy-metadata", "APScheduler",
            str(service / "main.py"),
        ]
        print(f"Building {target} with {sys.executable}", flush=True)
        subprocess.run(command, cwd=service, env=environment, check=True)
        suffix = ".exe" if TARGETS[target][0] == "Windows" else ""
        output = scratch / "dist" / f"localmind-sidecar{suffix}"
        if not output.is_file() or output.stat().st_size == 0:
            raise RuntimeError(f"PyInstaller did not produce a usable executable: {output}")
        destination = binary_path(target)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(output, destination)
        if os.name != "nt":
            destination.chmod(destination.stat().st_mode | 0o111)
        print(f"Sidecar ready: {destination} ({destination.stat().st_size:,} bytes)")
        return destination


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, choices=TARGETS)
    arguments = parser.parse_args()
    try:
        build(arguments.target)
    except (RuntimeError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Sidecar build failed: {error}\n")


if __name__ == "__main__":
    main()
