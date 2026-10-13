#!/usr/bin/env python3
"""Stage a verified, complete Ollama runtime and smoke-test its local API.

Uses only the Python standard library. Linux also needs GNU tar and zstd.
Run on the native OS/architecture being released; no model download is performed.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
from urllib.error import URLError
from urllib.request import Request, build_opener, ProxyHandler
import zipfile

VERSION = "0.40.3"
REPOSITORY = "ollama/ollama"
ROOT = Path(__file__).resolve().parents[2]
DESTINATION = ROOT / "apps/desktop/src-tauri/resources/ollama-runtime"
TARGETS = {
    "x86_64-pc-windows-msvc": ("ollama-windows-amd64.zip", "ollama.exe", "lib/ollama/llama-server.exe"),
    "x86_64-unknown-linux-gnu": ("ollama-linux-amd64.tar.zst", "bin/ollama", "lib/ollama/llama-server"),
    "x86_64-apple-darwin": ("ollama-darwin.tgz", "ollama", "llama-server"),
    "aarch64-apple-darwin": ("ollama-darwin.tgz", "ollama", "llama-server"),
}


def safe_name(name: str, *, allow_root: bool = False) -> PurePosixPath:
    """Reject absolute/drive/parent paths on every host, not only the target OS."""
    normalized = name.replace("\\", "/")
    path = PurePosixPath(normalized)
    if "\x00" in name or path.is_absolute() or PureWindowsPath(name).drive or ".." in path.parts:
        raise ValueError(f"Unsafe archive path: {name!r}")
    if any(":" in part for part in path.parts):
        raise ValueError(f"Unsafe archive path: {name!r}")
    if not path.parts and not allow_root:
        raise ValueError("Archive file cannot replace the extraction root")
    return path


def safe_link(member: PurePosixPath, target: str, *, hard: bool) -> PurePosixPath:
    if "\x00" in target or PureWindowsPath(target).drive or target.replace("\\", "/").startswith("/"):
        raise ValueError(f"Unsafe archive link: {member} -> {target!r}")
    parts = [] if hard else list(member.parent.parts)
    for part in target.replace("\\", "/").split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            if not parts:
                raise ValueError(f"Archive link escapes extraction root: {member} -> {target!r}")
            parts.pop()
        elif ":" in part:
            raise ValueError(f"Unsafe archive link: {member} -> {target!r}")
        else:
            parts.append(part)
    return PurePosixPath(*parts)


def validate_tar(archive: tarfile.TarFile) -> None:
    """Validate the entire stream before allowing native tar to write any files."""
    names: set[PurePosixPath] = set()
    symlinks: set[PurePosixPath] = set()
    files: set[PurePosixPath] = set()
    links: list[tuple[PurePosixPath, PurePosixPath, bool]] = []
    for member in archive:
        name = safe_name(member.name, allow_root=member.isdir())
        if not name.parts:
            continue
        if name in names:
            raise ValueError(f"Duplicate archive member: {name}")
        names.add(name)
        if member.issym() or member.islnk():
            links.append((name, safe_link(name, member.linkname, hard=member.islnk()), member.islnk()))
            if member.issym():
                symlinks.add(name)
        elif member.isfile():
            files.add(name)
        elif not member.isdir():
            raise ValueError(f"Unsupported special archive member: {name}")
    for name in names:
        if any(parent in symlinks for parent in name.parents):
            raise ValueError(f"Archive member traverses a symlink: {name}")
    for name, target, hard in links:
        if any(parent in symlinks for parent in target.parents):
            raise ValueError(f"Archive link traverses a directory symlink: {name}")
        if hard and target not in files:
            raise ValueError(f"Hard link target is not a regular archive file: {name}")


def extract_zip(archive: Path, destination: Path) -> None:
    with zipfile.ZipFile(archive) as bundle:
        names: set[str] = set()
        # Preflight every entry before creating anything.
        for member in bundle.infolist():
            name = safe_name(member.filename, allow_root=member.is_dir())
            key = str(name).casefold()  # Windows ZIP is case-insensitive on its destination.
            if key in names:
                raise ValueError(f"Duplicate archive member: {name}")
            names.add(key)
            mode = member.external_attr >> 16
            if stat.S_ISLNK(mode) or (stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR)):
                raise ValueError(f"Unsupported ZIP member: {name}")
        for member in bundle.infolist():
            name = safe_name(member.filename, allow_root=member.is_dir())
            path = destination.joinpath(*name.parts)
            if member.is_dir():
                path.mkdir(parents=True, exist_ok=True)
            else:
                path.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(member) as source, path.open("xb") as target:
                    shutil.copyfileobj(source, target, length=1024 * 1024)


def extract_archive(archive: Path, asset_name: str, destination: Path) -> None:
    prepare_destination(destination)
    if asset_name.endswith(".zip"):
        extract_zip(archive, destination)
    elif asset_name.endswith(".tar.zst"):
        # Stream the preflight through zstd; never materialize another multi-GB tar.
        with subprocess.Popen(["zstd", "-dc", str(archive)], stdout=subprocess.PIPE) as decoder:
            try:
                assert decoder.stdout is not None
                with tarfile.open(fileobj=decoder.stdout, mode="r|") as stream:
                    validate_tar(stream)
                # tarfile stops at the end marker; consume any remaining frame bytes.
                while decoder.stdout.read(1024 * 1024):
                    pass
                if decoder.wait() != 0:
                    raise RuntimeError("zstd could not validate the Ollama archive")
            except BaseException:
                decoder.kill()
                decoder.wait()
                raise
        subprocess.run(["tar", "--zstd", "-xf", str(archive), "-C", str(destination), "--no-same-owner"], check=True)
    elif asset_name.endswith(".tgz"):
        with tarfile.open(archive, "r:gz") as bundle:
            validate_tar(bundle)
        # Native tar preserves the signed Mach-O files, executable modes, and links.
        subprocess.run(["tar", "-xzf", str(archive), "-C", str(destination)], check=True)
    else:
        raise ValueError(f"Unsupported archive format: {asset_name}")
    root = destination.resolve()
    for path in destination.rglob("*"):
        if path.is_symlink() and not path.resolve().is_relative_to(root):
            raise ValueError(f"Extracted symlink escapes runtime: {path}")


def prepare_destination(destination: Path) -> None:
    if destination.is_symlink():
        raise RuntimeError(f"Runtime destination cannot be a symlink: {destination}")
    destination.mkdir(parents=True, exist_ok=True)
    if any(path.name != ".gitkeep" or not path.is_file() or path.is_symlink() for path in destination.iterdir()):
        raise RuntimeError(f"Refusing to mix runtime versions in nonempty directory: {destination}")


def release_asset(name: str) -> dict:
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "LocalMind-release"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = Request(f"https://api.github.com/repos/{REPOSITORY}/releases/tags/v{VERSION}", headers=headers)
    with build_opener().open(request, timeout=60) as response:
        metadata = json.load(response)
    if metadata.get("tag_name") != f"v{VERSION}" or metadata.get("draft") or metadata.get("prerelease"):
        raise RuntimeError("The pinned Ollama release is not a published stable release")
    asset = next((asset for asset in metadata.get("assets", []) if asset["name"] == name), None)
    if not asset:
        raise RuntimeError(f"Ollama v{VERSION} has no asset {name}")
    expected_url = f"https://github.com/{REPOSITORY}/releases/download/v{VERSION}/{name}"
    if asset.get("browser_download_url") != expected_url:
        raise RuntimeError("Unexpected Ollama asset download URL")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", asset.get("digest") or ""):
        raise RuntimeError("GitHub did not provide a SHA-256 digest for the pinned Ollama asset")
    return asset


def download(asset: dict, destination: Path) -> None:
    # Deliberately separate request: never send GITHUB_TOKEN to asset redirects/CDNs.
    request = Request(asset["browser_download_url"], headers={"User-Agent": "LocalMind-release"})
    digest = hashlib.sha256()
    received = 0
    last_progress = time.monotonic()
    with build_opener().open(request, timeout=120) as response, destination.open("wb") as target:
        while chunk := response.read(1024 * 1024):
            target.write(chunk)
            digest.update(chunk)
            received += len(chunk)
            if received > asset["size"]:
                raise RuntimeError("Ollama download exceeded the release asset size")
            if time.monotonic() - last_progress > 20:
                print(f"Downloaded {received // (1024 * 1024)} / {asset['size'] // (1024 * 1024)} MiB", flush=True)
                last_progress = time.monotonic()
    if received != asset["size"] or f"sha256:{digest.hexdigest()}" != asset["digest"]:
        raise RuntimeError("Ollama download failed its size/SHA-256 integrity check")


def stop_process(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    else:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        if os.name != "nt":
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        else:
            process.kill()
        process.wait(timeout=10)


def smoke(executable: Path, timeout: float) -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    with tempfile.TemporaryDirectory(prefix="localmind-ollama-smoke-") as profile:
        env = os.environ.copy()
        (Path(profile) / "models").mkdir()
        (Path(profile) / "tmp").mkdir()
        env.update({"HOME": profile, "USERPROFILE": profile, "OLLAMA_HOST": f"127.0.0.1:{port}", "OLLAMA_MODELS": str(Path(profile) / "models"), "OLLAMA_TMPDIR": str(Path(profile) / "tmp"), "OLLAMA_NO_CLOUD": "1"})
        log_path = Path(profile) / "server.log"
        opener = build_opener(ProxyHandler({}))
        def get_json(endpoint: str) -> dict:
            with opener.open(f"http://127.0.0.1:{port}/api/{endpoint}", timeout=2) as response:
                return json.load(response)
        with log_path.open("wb") as log:
            options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {"start_new_session": True}
            process = subprocess.Popen([str(executable), "serve"], cwd=executable.parent, env=env, stdout=log, stderr=subprocess.STDOUT, **options)
            try:
                deadline = time.monotonic() + timeout
                while time.monotonic() < deadline:
                    if process.poll() is not None:
                        raise RuntimeError(f"Bundled Ollama exited with status {process.returncode}")
                    try:
                        version = get_json("version")
                        tags = get_json("tags")
                    except (URLError, TimeoutError, OSError, ValueError):
                        time.sleep(0.5)
                        continue
                    if version.get("version") != VERSION:
                        raise RuntimeError(f"Bundled Ollama reports unexpected version: {version}")
                    if tags.get("models") != []:
                        raise RuntimeError("Ollama smoke test did not use an empty isolated model store")
                    if process.poll() is not None:
                        raise RuntimeError("Bundled Ollama exited during its health check")
                    print(f"Ollama {VERSION} smoke passed: /api/version and empty /api/tags on an isolated loopback port", flush=True)
                    return
                raise RuntimeError(f"Bundled Ollama did not become ready within {timeout:g} seconds")
            except BaseException:
                log.flush()
                with log_path.open("rb") as diagnostic:
                    diagnostic.seek(max(0, log_path.stat().st_size - 16000))
                    print(diagnostic.read().decode("utf-8", errors="replace"), file=sys.stderr)
                raise
            finally:
                stop_process(process)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", choices=TARGETS, required=True)
    parser.add_argument("--timeout", type=float, default=200, help="Smoke test startup timeout in seconds")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    asset_name, executable_name, helper_name = TARGETS[args.target]
    destination = DESTINATION
    prepare_destination(destination)
    asset = release_asset(asset_name)
    print(f"Staging full Ollama v{VERSION}: {asset_name} ({asset['size']:,} bytes)", flush=True)
    fd, temporary = tempfile.mkstemp(prefix="localmind-ollama-", suffix=".archive", dir=destination.parent)
    os.close(fd)
    archive = Path(temporary)
    try:
        download(asset, archive)
        extract_archive(archive, asset_name, destination)
    finally:
        archive.unlink(missing_ok=True)
    executable = destination / executable_name
    helper = destination / helper_name
    if not executable.is_file() or not helper.is_file() or executable.stat().st_size == 0 or helper.stat().st_size == 0:
        raise RuntimeError("Ollama archive is missing its executable or required llama-server helper")
    if os.name != "nt" and not os.access(executable, os.X_OK):
        raise RuntimeError("Ollama archive did not preserve executable permissions")
    smoke(executable, args.timeout)
    provenance = {"version": VERSION, "target": args.target, "asset": asset_name, "sha256": asset["digest"].removeprefix("sha256:"), "source": asset["browser_download_url"], "executable": executable_name, "smoke": "api/version and api/tags; inference not tested"}
    (destination / "localmind-runtime.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(f"Runtime executable: ollama-runtime/{executable_name}", flush=True)


if __name__ == "__main__":
    main()
