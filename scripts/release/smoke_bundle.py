"""Extract a built desktop bundle and smoke-test its actual service payload.

The desktop GUI is never launched and no installer is executed. Windows requires
7-Zip with NSIS support; Linux requires dpkg-deb. macOS checks the built .app tree.
This checks packaged services and runtime placement, not GUI or model inference.
"""

import argparse
import json
import os
import plistlib
import re
import shutil
import subprocess
import tempfile
import tomllib
from pathlib import Path

from build_sidecar import ROOT, TARGETS, validate_target
from bundle_ollama import DESTINATION, TARGETS as OLLAMA_TARGETS, VERSION as OLLAMA_VERSION
from bundle_ollama import smoke as smoke_ollama
from smoke_sidecar import smoke as smoke_sidecar


class BundleError(RuntimeError):
    pass


def one_path(paths, description: str) -> Path:
    matches = sorted(paths)
    if len(matches) != 1:
        raise BundleError(f"Expected exactly one {description}, found {len(matches)}: {matches}")
    return matches[0]


def require_file(path: Path, root: Path) -> None:
    if not path.resolve().is_relative_to(root.resolve()):
        raise BundleError(f"Bundle path escapes its payload root: {path}")
    if not path.is_file() or path.stat().st_size == 0:
        raise BundleError(f"Bundle is missing a nonempty required file: {path}")


def versioned_installer(directory: Path, suffix: str, version: str) -> Path:
    # Tauri names installers Product_VERSION_ARCH[_setup].EXT. Version-boundary
    # checks prevent a stale 0.7.0 installer from satisfying a 0.7.0-beta build.
    version_pattern = re.compile(r"_" + re.escape(version) + r"_")
    return one_path(
        (path for path in directory.glob(f"*{suffix}") if version_pattern.search(path.name)),
        f"{suffix} installer for version {version} in {directory}",
    )


def seven_zip() -> str:
    found = shutil.which("7z") or shutil.which("7za")
    if found:
        return found
    for variable in ("ProgramFiles", "ProgramFiles(x86)"):
        program_files = os.environ.get(variable)
        if program_files:
            candidate = Path(program_files) / "7-Zip/7z.exe"
            if candidate.is_file():
                return str(candidate)
    raise BundleError(
        "7-Zip with NSIS extraction support is required to validate the Windows installer. "
        "Install 7-Zip and put 7z.exe on PATH."
    )


def extract_installer(installer: Path, destination: Path, target: str) -> None:
    destination.mkdir(parents=True)
    if target == "x86_64-pc-windows-msvc":
        command = [seven_zip(), "x", "-y", f"-o{destination}", str(installer)]
    else:
        dpkg = shutil.which("dpkg-deb")
        if not dpkg:
            raise BundleError("dpkg-deb is required to extract and validate the Linux installer")
        command = [dpkg, "-x", str(installer), str(destination)]
    options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
    result = subprocess.run(command, capture_output=True, text=True, timeout=300, **options)
    if result.returncode:
        raise BundleError(
            f"Could not extract {installer.name}; installer payload verification cannot continue.\n"
            + (result.stdout + result.stderr)[-8000:]
        )


def find_services(payload: Path, target: str, product_name: str) -> tuple[Path, Path]:
    """Require Tauri's installed layout, not merely a binary somewhere in the tree."""
    if target == "x86_64-pc-windows-msvc":
        sidecar = one_path(payload.rglob("localmind-sidecar.exe"), "packaged Windows sidecar")
        ollama = one_path(payload.rglob("ollama.exe"), "packaged Windows Ollama executable")
        runtime = sidecar.parent / "ollama-runtime"
        if ollama != runtime / "ollama.exe":
            raise BundleError("Windows Ollama runtime must be beside the app/sidecar in ollama-runtime")
    elif target == "x86_64-unknown-linux-gnu":
        sidecar = one_path(payload.rglob("localmind-sidecar"), "packaged Linux sidecar")
        if sidecar != payload / "usr/bin/localmind-sidecar":
            raise BundleError("Linux sidecar must be installed in usr/bin beside the desktop app")
        # Tauri's Debian bundler uses settings.product_name(); codegen also puts
        # config.productName in PackageInfo.name, which resource_dir() resolves.
        # The Cargo/mainBinaryName executable name can differ from productName.
        runtime = payload / "usr/lib" / product_name / "ollama-runtime"
        ollama = one_path(payload.rglob("ollama-runtime/bin/ollama"), "packaged Linux Ollama executable")
        if ollama != runtime / "bin/ollama":
            raise BundleError(f"Linux Ollama resources must be installed in {runtime}")
    else:
        sidecar = payload / "Contents/MacOS/localmind-sidecar"
        runtime = payload / "Contents/Resources/ollama-runtime"
        ollama = runtime / "ollama"
    require_file(sidecar, payload)
    require_file(ollama, payload)
    return sidecar, runtime


def verify_runtime(runtime: Path, staged: Path, target: str) -> Path:
    """Require every staged runtime file, including all CPU/GPU shared libraries."""
    _, executable_name, helper_name = OLLAMA_TARGETS[target]
    require_file(staged / executable_name, staged)
    require_file(staged / helper_name, staged)
    require_file(runtime / helper_name, runtime)
    files = [path for path in staged.rglob("*") if path.is_file() and path.name != ".gitkeep"]
    for source in files:
        relative = source.relative_to(staged)
        bundled = runtime / relative
        # macOS codesigning can change Mach-O bytes and sizes. Compare retained
        # paths/nonempty files here; the executable is actually launched below.
        require_file(bundled, runtime)
        if "apple-darwin" not in target and source.stat().st_size != bundled.stat().st_size:
            raise BundleError(f"Packaged Ollama runtime file changed size: {relative}")
    provenance_path = runtime / "localmind-runtime.json"
    require_file(provenance_path, runtime)
    provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
    if provenance.get("target") != target or provenance.get("version") != OLLAMA_VERSION:
        raise BundleError("Packaged Ollama provenance does not match this release target/version")
    executable = runtime / executable_name
    require_file(executable, runtime)
    if os.name != "nt" and not os.access(executable, os.X_OK):
        raise BundleError(f"Packaged Ollama lost executable permissions: {executable}")
    print(f"Verified {len(files)} packaged Ollama runtime files, including shared libraries.", flush=True)
    return executable


def check_payload(payload: Path, target: str, product_name: str, timeout: float) -> None:
    sidecar, runtime = find_services(payload, target, product_name)
    ollama = verify_runtime(runtime, DESTINATION, target)
    print(f"Testing packaged sidecar: {sidecar}", flush=True)
    smoke_sidecar(sidecar, timeout)
    print(f"Testing packaged Ollama: {ollama}", flush=True)
    smoke_ollama(ollama, timeout)


def smoke_bundle(target: str, timeout: float) -> None:
    validate_target(target)
    config = json.loads((ROOT / "apps/desktop/src-tauri/tauri.conf.json").read_text(encoding="utf-8"))
    cargo = tomllib.loads((ROOT / "apps/desktop/src-tauri/Cargo.toml").read_text(encoding="utf-8"))
    version = config["version"]
    product_name = config.get("productName") or cargo["package"]["name"]
    bundle = ROOT / "target" / target / "release/bundle"
    if "apple-darwin" in target:
        app = bundle / "macos" / f"{product_name}.app"
        info_path = app / "Contents/Info.plist"
        require_file(info_path, app)
        with info_path.open("rb") as stream:
            info = plistlib.load(stream)
        if info.get("CFBundleShortVersionString") != version:
            raise BundleError(f"macOS app version does not match {version}")
        check_payload(app, target, product_name, timeout)
    else:
        windows = target == "x86_64-pc-windows-msvc"
        directory, suffix = ("nsis", ".exe") if windows else ("deb", ".deb")
        installer = versioned_installer(bundle / directory, suffix, version)
        require_file(installer, bundle)
        with tempfile.TemporaryDirectory(prefix="localmind-bundle-smoke-") as temporary:
            payload = Path(temporary) / "payload"
            print(f"Extracting installer for service smoke: {installer.name}", flush=True)
            extract_installer(installer, payload, target)
            check_payload(payload, target, product_name, timeout)
    print("Built desktop bundle passed service placement, runtime completeness and startup checks.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, choices=TARGETS)
    parser.add_argument("--timeout", type=float, default=200)
    arguments = parser.parse_args()
    if arguments.timeout <= 0:
        parser.error("--timeout must be positive")
    try:
        smoke_bundle(arguments.target, arguments.timeout)
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Desktop bundle smoke failed: {error}\n")


if __name__ == "__main__":
    main()
