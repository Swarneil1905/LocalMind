"""Build macOS installers, recovering only from a busy temporary DMG mount."""

import argparse
from collections import deque
import json
import plistlib
import re
import subprocess
import time
from pathlib import Path

from build_sidecar import ROOT, validate_target


TARGETS = ("x86_64-apple-darwin", "aarch64-apple-darwin")
BUSY_UNMOUNT = re.compile(r"hdiutil: couldn't (?:eject|unmount)[^\n]*Resource busy")


def run_tauri(command: list[str]) -> tuple[int, str]:
    tail: deque[str] = deque(maxlen=100)
    with subprocess.Popen(
        command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, errors="replace", bufsize=1,
    ) as process:
        assert process.stdout is not None
        for line in process.stdout:
            print(line, end="", flush=True)
            tail.append(line)
        return process.wait(), "".join(tail)


def detach_build_images(bundle: Path, installer_name: str) -> None:
    result = subprocess.run(["hdiutil", "info", "-plist"], capture_output=True, check=True, timeout=30)
    info = plistlib.loads(result.stdout)
    # create-dmg names its writable intermediate rw.<pid>.<installer>. Only
    # images in this target's macos directory may be detached, never /Volumes/*.
    expected_parent = (bundle / "macos").resolve()
    pattern = re.compile(r"rw\.\d+\." + re.escape(installer_name) + r"\Z")
    devices = set()
    for image in info.get("images", []):
        image_path = image.get("image-path")
        if not isinstance(image_path, str):
            continue
        path = Path(image_path).resolve()
        if path.parent != expected_parent or not pattern.fullmatch(path.name):
            continue
        for entity in image.get("system-entities", []):
            device = entity.get("dev-entry", "")
            if re.fullmatch(r"/dev/disk\d+", device):
                devices.add(device)
    for device in sorted(devices):
        print(f"Detaching this build's busy temporary DMG: {device}", flush=True)
        subprocess.run(["hdiutil", "detach", device, "-force"], check=True, timeout=60)


def build_macos(target: str) -> None:
    validate_target(target)
    config = json.loads((ROOT / "apps/desktop/src-tauri/tauri.conf.json").read_text(encoding="utf-8"))
    arch = "x64" if target == "x86_64-apple-darwin" else "aarch64"
    installer_name = f"{config['productName']}_{config['version']}_{arch}.dmg"
    bundle = ROOT / "target" / target / "release/bundle"
    for attempt in range(3):
        action = "build" if attempt == 0 else "bundle"
        command = ["pnpm", "--filter", "desktop", "tauri", action, "--verbose", "--target", target, "--bundles", "app,dmg"]
        status, output = run_tauri(command)
        if status == 0:
            return
        if not BUSY_UNMOUNT.search(output) or attempt == 2:
            raise subprocess.CalledProcessError(status, command)
        detach_build_images(bundle, installer_name)
        print(f"Retrying macOS bundling after busy DMG cleanup ({attempt + 1}/2).", flush=True)
        time.sleep(3)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, choices=TARGETS)
    args = parser.parse_args()
    try:
        build_macos(args.target)
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, f"macOS packaging failed: {error}\n")


if __name__ == "__main__":
    main()
