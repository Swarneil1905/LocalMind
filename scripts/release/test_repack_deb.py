"""Verify Debian repacking preserves payloads and only promotes signed output."""

import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from artifacts import ArtifactError
from repack_deb import deduplicate_payload, repack_deb
from test_artifacts import SIGNATURE


class RepackDebTests(unittest.TestCase):
    def test_runtime_aliases_share_storage_without_changing_contents(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload = Path(temporary)
            runtime = payload / "usr/lib/LocalMind/ollama-runtime/lib/ollama"
            runtime.mkdir(parents=True)
            data = os.urandom(128 * 1024)
            originals = {}
            for directory in ("cuda_v12", "cuda_v13"):
                folder = runtime / directory
                folder.mkdir()
                for name in ("libcublas.so", "libcublas.so.12", "libcublas.so.12.8"):
                    path = folder / name
                    path.write_bytes(data)
                    originals[path] = data
            different = runtime / "different.so"
            different.write_bytes(b"x" * len(data))
            originals[different] = different.read_bytes()
            # Identical contents outside the runtime must remain independent.
            other = payload / "usr/lib/another-app.conf"
            other.write_bytes(data)
            control = payload / "DEBIAN/control"
            control.parent.mkdir()
            control.write_bytes(data)
            with patch("repack_deb.print"):
                deduplicate_payload(payload)
                deduplicate_payload(payload)  # Already linked files remain valid.
            first = next(iter(originals))
            for path, content in originals.items():
                self.assertEqual(path.read_bytes(), content)
                self.assertEqual(first.samefile(path), path != different)
            self.assertFalse(first.samefile(other))
            self.assertFalse(first.samefile(control))

    def test_failed_link_leaves_payload_copy_intact(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload = Path(temporary)
            runtime = payload / "usr/lib/LocalMind/ollama-runtime"
            runtime.mkdir(parents=True)
            for name in ("a", "b"):
                (runtime / name).write_bytes(b"same data")
            with patch("repack_deb.os.link", side_effect=OSError("cannot create link")):
                with self.assertRaisesRegex(OSError, "cannot create link"):
                    deduplicate_payload(payload)
            for name in ("a", "b"):
                self.assertEqual((runtime / name).read_bytes(), b"same data")
            self.assertEqual(sorted(path.name for path in runtime.iterdir()), ["a", "b"])

    def test_recompressed_bytes_are_signed_before_replacing_original(self):
        with tempfile.TemporaryDirectory() as temporary:
            installer = Path(temporary) / "LocalMind_0.7.0_amd64.deb"
            installer.write_bytes(b"original")
            observed = []

            def run(command, **kwargs):
                observed.append(command)
                if "--build" in command:
                    Path(command[-1]).write_bytes(b"compressed")
                if "signer" in command:
                    self.assertEqual(installer.read_bytes(), b"original")
                    packed = Path(command[-1])
                    self.assertEqual(packed.read_bytes(), b"compressed")
                    packed.with_name(packed.name + ".sig").write_text(SIGNATURE, encoding="ascii")
                return subprocess.CompletedProcess(command, 0)

            with patch("repack_deb.subprocess.run", side_effect=run), patch("repack_deb.print"):
                repack_deb(installer)
            self.assertEqual(installer.read_bytes(), b"compressed")
            self.assertEqual(installer.with_name(installer.name + ".sig").read_text(), SIGNATURE)
            self.assertIn("-Zxz", observed[1])
            self.assertIn("--root-owner-group", observed[1])

    def test_failed_or_oversized_repack_never_replaces_original(self):
        for failure in ("compression", "size", "signing", "invalid-signature"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                installer = Path(temporary) / "fixture.deb"
                installer.write_bytes(b"original")
                signature = installer.with_name(installer.name + ".sig")
                signature.write_text("original signature")

                def run(command, **kwargs):
                    if "--build" in command:
                        if failure == "compression":
                            raise subprocess.CalledProcessError(1, command)
                        Path(command[-1]).write_bytes(b"larger than the fixture limit" if failure == "size" else b"packed")
                    if "signer" in command:
                        if failure == "signing":
                            raise subprocess.CalledProcessError(1, command)
                        packed = Path(command[-1])
                        packed.with_name(packed.name + ".sig").write_text("invalid")
                    return subprocess.CompletedProcess(command, 0)

                with patch("repack_deb.subprocess.run", side_effect=run), \
                        patch("artifacts.MAX_ASSET_BYTES", 20), patch("repack_deb.print"):
                    with self.assertRaises((ArtifactError, subprocess.CalledProcessError)):
                        repack_deb(installer)
                self.assertEqual(installer.read_bytes(), b"original")
                self.assertEqual(signature.read_text(), "original signature")


@unittest.skipUnless(sys.platform == "linux" and shutil.which("dpkg-deb"), "Native Linux dpkg-deb required")
class NativeDebTests(unittest.TestCase):
    def test_real_repack_preserves_files_links_permissions_and_control_scripts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload = root / "original"
            control = payload / "DEBIAN"
            control.mkdir(parents=True)
            (control / "control").write_text("Package: localmind-fixture\nVersion: 0.7.0\nArchitecture: amd64\nMaintainer: Fixture <fixture@example.invalid>\nDescription: packaging regression fixture\n")
            (control / "postinst").write_text("#!/bin/sh\nexit 0\n")
            (control / "postinst").chmod(0o755)
            binary = payload / "usr/bin/localmind-fixture"
            binary.parent.mkdir(parents=True)
            binary.write_bytes(b"#!/bin/sh\nexit 0\n" + b"# preserved payload\n" * 10000)
            binary.chmod(0o755)
            alias = binary.with_name("fixture-alias")
            alias.symlink_to(binary.name)
            runtime = payload / "usr/lib/LocalMind/ollama-runtime/lib/ollama/cuda_v12"
            runtime.mkdir(parents=True)
            library = runtime / "libfixture.so.12.8"
            library.write_bytes(os.urandom(128 * 1024))
            library.chmod(0o755)
            # Reproduce Tauri's expanded symlink aliases in a real Debian package.
            copied_alias = runtime / "libfixture.so.12"
            shutil.copy2(library, copied_alias)
            different_mode = runtime / "different-mode.so"
            shutil.copy2(library, different_mode)
            different_mode.chmod(0o644)
            retained_link = runtime / "libfixture.so"
            retained_link.symlink_to(copied_alias.name)
            outside = root / "outside"
            outside.mkdir()
            (outside / "a").write_bytes(b"outside data")
            (outside / "b").write_bytes(b"outside data")
            (runtime / "outside-link").symlink_to(outside, target_is_directory=True)
            with patch("repack_deb.print"):
                deduplicate_payload(payload)
            self.assertFalse((outside / "a").samefile(outside / "b"))
            (runtime / "outside-link").unlink()
            # Reset the alias to a full copy so repack_deb must deduplicate it.
            copied_alias.unlink()
            shutil.copy2(library, copied_alias)
            expected_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
            installer = root / "fixture.deb"
            subprocess.run(["dpkg-deb", "--root-owner-group", "-Zgzip", "--build", str(payload), str(installer)], check=True, capture_output=True)
            real_run = subprocess.run

            def run(command, **kwargs):
                if command[0] == "pnpm":
                    packed = Path(command[-1])
                    packed.with_name(packed.name + ".sig").write_text(SIGNATURE, encoding="ascii")
                    return subprocess.CompletedProcess(command, 0)
                return real_run(command, **kwargs)

            with patch("repack_deb.subprocess.run", side_effect=run), patch("repack_deb.print"):
                repack_deb(installer)
            self.assertIn(b"data.tar.xz", installer.read_bytes()[:4096])
            extracted = root / "extracted"
            subprocess.run(["dpkg-deb", "--raw-extract", str(installer), str(extracted)], check=True)
            restored = extracted / "usr/bin/localmind-fixture"
            self.assertEqual(hashlib.sha256(restored.read_bytes()).hexdigest(), expected_hash)
            self.assertTrue(os.access(restored, os.X_OK))
            self.assertEqual(os.readlink(extracted / "usr/bin/fixture-alias"), binary.name)
            self.assertEqual((extracted / "DEBIAN/control").read_bytes(), (control / "control").read_bytes())
            self.assertEqual((extracted / "DEBIAN/postinst").read_bytes(), (control / "postinst").read_bytes())
            self.assertTrue(os.access(extracted / "DEBIAN/postinst", os.X_OK))
            restored_library = extracted / library.relative_to(payload)
            restored_alias = extracted / copied_alias.relative_to(payload)
            self.assertEqual(restored_library.read_bytes(), library.read_bytes())
            self.assertTrue(restored_library.samefile(restored_alias))
            self.assertFalse(restored_library.samefile(extracted / different_mode.relative_to(payload)))
            self.assertEqual(os.readlink(extracted / retained_link.relative_to(payload)), copied_alias.name)


if __name__ == "__main__":
    unittest.main()
