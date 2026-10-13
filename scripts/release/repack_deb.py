"""Recompress Tauri's gzip Debian package with xz without dropping payload files."""

import os
import stat
import subprocess
import tempfile
from pathlib import Path

from artifacts import check_file, read_signature, sha256
from build_sidecar import ROOT


def deduplicate_payload(payload: Path) -> None:
    """Store identical runtime files once, retaining every installed pathname.

    Tauri's Debian tar writer follows resource symlinks, so CUDA library aliases
    become full copies. xz's dictionary cannot deduplicate entire large libraries.
    dpkg-deb preserves hard links, as does the installed filesystem. Limit this
    to immutable application resources, never control files or user configuration.
    """
    groups: dict[tuple[int, int, int, int], list[Path]] = {}
    for directory, subdirs, files in os.walk(payload / "usr/lib", followlinks=False):
        parent = Path(directory)
        subdirs[:] = sorted(name for name in subdirs if not (parent / name).is_symlink())
        if "ollama-runtime" not in parent.relative_to(payload).parts:
            continue
        for name in sorted(files):
            path = parent / name
            metadata = path.lstat()
            if not stat.S_ISREG(metadata.st_mode) or metadata.st_size == 0:
                continue
            key = (metadata.st_size, metadata.st_mode, metadata.st_uid, metadata.st_gid)
            groups.setdefault(key, []).append(path)
    linked = saved = 0
    for candidates in groups.values():
        if len(candidates) < 2:
            continue
        originals: dict[str, Path] = {}
        for path in candidates:
            digest = sha256(path)
            original = originals.setdefault(digest, path)
            if original == path or original.samefile(path):
                continue
            # Build the link before replacing the copy: a link failure must not
            # leave a missing payload file, even inside the temporary tree.
            with tempfile.TemporaryDirectory(prefix=".dedup-", dir=path.parent) as temporary:
                replacement = Path(temporary) / "linked"
                os.link(original, replacement)
                replacement.replace(path)
            linked += 1
            saved += path.stat().st_size
    print(f"Debian runtime deduplication: {linked} aliases, {saved:,} duplicate bytes removed", flush=True)


def repack_deb(installer: Path) -> None:
    installer = installer.resolve()
    original_size = installer.stat().st_size
    # Stage beside the original so replacement is atomic on the same filesystem.
    with tempfile.TemporaryDirectory(prefix="localmind-deb-", dir=installer.parent) as temporary:
        scratch = Path(temporary)
        payload = scratch / "payload"
        packed = scratch / installer.name
        subprocess.run(["dpkg-deb", "--raw-extract", str(installer), str(payload)], check=True)
        deduplicate_payload(payload)
        # Tauri installs every entry as root. Reconstruct control scripts,
        # permissions and symlinks via dpkg, without executing maintainer scripts.
        subprocess.run([
            "dpkg-deb", "--root-owner-group", "--uniform-compression", "-Zxz", "-z9",
            "--build", str(payload), str(packed),
        ], check=True, env=dict(os.environ, DPKG_DEB_THREADS_MAX="2"))
        print(f"Debian compression: {original_size:,} -> {packed.stat().st_size:,} bytes", flush=True)
        check_file(packed)
        subprocess.run(["dpkg-deb", "--info", str(packed)], check=True)
        # Sign the final bytes before promoting either file. The AppImage updater
        # and its signature are not modified by this installer-only operation.
        subprocess.run([
            "pnpm", "--filter", "desktop", "tauri", "signer", "sign", str(packed),
        ], cwd=ROOT, check=True)
        signature = packed.with_name(packed.name + ".sig")
        read_signature(signature)
        packed.replace(installer)
        signature.replace(installer.with_name(installer.name + ".sig"))
