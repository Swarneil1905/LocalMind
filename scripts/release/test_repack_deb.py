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
from repack_deb import repack_deb
from test_artifacts import SIGNATURE


class RepackDebTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
