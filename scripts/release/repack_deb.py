"""Recompress Tauri's gzip Debian package with xz without dropping payload files."""

import os
import subprocess
import tempfile
from pathlib import Path

from artifacts import check_file
from build_sidecar import ROOT


def repack_deb(installer: Path) -> None:
    installer = installer.resolve()
    original_size = installer.stat().st_size
    # Stage beside the original so replacement is atomic on the same filesystem.
    with tempfile.TemporaryDirectory(prefix="localmind-deb-", dir=installer.parent) as temporary:
        scratch = Path(temporary)
        payload = scratch / "payload"
        packed = scratch / installer.name
        subprocess.run(["dpkg-deb", "--raw-extract", str(installer), str(payload)], check=True)
        # Tauri installs every entry as root. Reconstruct control scripts,
        # permissions and symlinks via dpkg, without executing maintainer scripts.
        subprocess.run([
            "dpkg-deb", "--root-owner-group", "--uniform-compression", "-Zxz", "-z9",
            "--build", str(payload), str(packed),
        ], check=True, env=dict(os.environ, DPKG_DEB_THREADS_MAX="2"))
        check_file(packed)
        subprocess.run(["dpkg-deb", "--info", str(packed)], check=True)
        print(f"Debian compression: {original_size:,} -> {packed.stat().st_size:,} bytes", flush=True)
        # Sign the final bytes before promoting either file. The AppImage updater
        # and its signature are not modified by this installer-only operation.
        subprocess.run([
            "pnpm", "--filter", "desktop", "tauri", "signer", "sign", str(packed),
        ], cwd=ROOT, check=True)
        signature = packed.with_name(packed.name + ".sig")
        check_file(signature)
        packed.replace(installer)
        signature.replace(installer.with_name(installer.name + ".sig"))
