"""Run with: python -m unittest discover -s scripts/release -p 'test_artifacts.py'."""

from __future__ import annotations

import base64
import hashlib
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import artifacts

VERSION = "0.7.0"
REPOSITORY = "Swarneil1905/LocalMind"
PUBLIC_KEY = base64.b64encode(b"Ed" + b"k" * 8 + b"p" * 32).decode("ascii")
RAW_SIGNATURE = (
    "untrusted comment: test fixture, not a real signature\n"
    + base64.b64encode(b"ED" + b"k" * 8 + b"s" * 64).decode("ascii")
    + "\ntrusted comment: timestamp:0\n"
    + base64.b64encode(b"g" * 64).decode("ascii")
    + "\n"
).encode("ascii")
SIGNATURE = base64.b64encode(RAW_SIGNATURE).decode("ascii")


class ReleaseArtifactsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="localmind-artifact-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / "repo"
        self.inputs = self.root / "artifacts"
        self.out = self.root / "release"
        self.key_file = self.root / "tauri.conf.json"
        encoded_key = base64.b64encode(
            ("untrusted comment: test key\n" + PUBLIC_KEY + "\n").encode()
        ).decode()
        self.key_file.write_text(
            json.dumps({"plugins": {"updater": {"pubkey": encoded_key}}}),
            encoding="utf-8",
        )

    def bundle(self, target):
        return self.repo / "target" / target / "release/bundle"

    def make_bundle(self, target, *, both_windows=False):
        bundle = self.bundle(target)
        if target.endswith("apple-darwin"):
            arch = "aarch64" if target.startswith("aarch64") else "x64"
            files = [
                f"dmg/LocalMind_{VERSION}_{arch}.dmg",
                "macos/LocalMind.app.tar.gz",
            ]
        elif "windows" in target:
            files = [
                f"nsis/LocalMind_{VERSION}_x64-setup.exe",
                f"nsis/LocalMind_{VERSION}_x64-setup.nsis.zip",
            ]
            if both_windows:
                files.extend(
                    [
                        f"msi/LocalMind_{VERSION}_x64_en-US.msi",
                        f"msi/LocalMind_{VERSION}_x64_en-US.msi.zip",
                    ]
                )
        else:
            files = [
                f"deb/LocalMind_{VERSION}_amd64.deb",
                f"appimage/LocalMind_{VERSION}_amd64.AppImage",
                f"appimage/LocalMind_{VERSION}_amd64.AppImage.tar.gz",
            ]
        for relative in files:
            path = bundle / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes((target + ":" + relative).encode())
            if artifacts.asset_kind(path.name) == "updater":
                path.with_name(path.name + ".sig").write_text(
                    SIGNATURE + "\n", encoding="ascii"
                )
        return bundle

    def collect(self, target):
        return artifacts.collect(
            target,
            VERSION,
            self.inputs / artifacts.TARGETS[target],
            repository_root=self.repo,
        )

    def prepare_all(self, *, both_windows=False):
        for target in artifacts.TARGETS:
            self.make_bundle(target, both_windows=both_windows)
            self.collect(target)

    def assemble(self):
        return artifacts.assemble(
            self.inputs, self.out, VERSION, REPOSITORY, public_key_file=self.key_file
        )

    def descriptor(self, platform):
        path = self.inputs / platform / f"manifest-{platform}.json"
        return path, json.loads(path.read_text(encoding="utf-8"))

    def test_collect_preserves_installers_and_separates_mac_architectures(self):
        for target in ("aarch64-apple-darwin", "x86_64-apple-darwin"):
            self.make_bundle(target)
            descriptor = self.collect(target)
            platform = artifacts.TARGETS[target]
            arch = platform.removeprefix("darwin-")
            name = f"LocalMind_{VERSION}_{arch}.app.tar.gz"
            self.assertEqual(descriptor["platforms"][platform]["filename"], name)
            self.assertTrue((self.inputs / platform / (name + ".sig")).is_file())
            self.assertTrue(
                (self.bundle(target) / "macos/LocalMind.app.tar.gz").is_file()
            )
            dmg_names = [
                item["filename"]
                for item in descriptor["assets"]
                if item["filename"].endswith(".dmg")
            ]
            expected_arch = "aarch64" if arch == "aarch64" else "x64"
            self.assertEqual(dmg_names, [f"LocalMind_{VERSION}_{expected_arch}.dmg"])

    def test_collect_windows_requires_matching_installer_and_signed_updater(self):
        target = "x86_64-pc-windows-msvc"
        self.make_bundle(target)
        descriptor = self.collect(target)
        self.assertEqual(len(descriptor["assets"]), 3)
        self.assertTrue(
            descriptor["platforms"]["windows-x86_64"]["filename"].endswith(".nsis.zip")
        )

    def test_collect_supports_msi_only(self):
        target = "x86_64-pc-windows-msvc"
        bundle = self.make_bundle(target, both_windows=True)
        for path in (bundle / "nsis").iterdir():
            path.unlink()
        descriptor = self.collect(target)
        self.assertTrue(
            descriptor["platforms"]["windows-x86_64"]["filename"].endswith(".msi.zip")
        )

    def test_collect_rejects_missing_installer(self):
        target = "x86_64-unknown-linux-gnu"
        bundle = self.make_bundle(target)
        next(bundle.rglob("*.deb")).unlink()
        with self.assertRaisesRegex(artifacts.ArtifactError, "Missing .deb"):
            self.collect(target)

    def test_collect_rejects_missing_signature(self):
        target = "aarch64-apple-darwin"
        next(self.make_bundle(target).rglob("*.sig")).unlink()
        with self.assertRaisesRegex(artifacts.ArtifactError, "Missing signature"):
            self.collect(target)

    def test_collect_rejects_signature_shaped_like_an_arbitrary_base64_string(self):
        target = "aarch64-apple-darwin"
        next(self.make_bundle(target).rglob("*.sig")).write_text(
            base64.b64encode(b"not a signature").decode()
        )
        with self.assertRaisesRegex(artifacts.ArtifactError, "signature envelope"):
            self.collect(target)

    def test_collect_rejects_duplicate_filenames_in_different_bundle_directories(self):
        target = "aarch64-apple-darwin"
        bundle = self.make_bundle(target)
        duplicate = bundle / "duplicate"
        duplicate.mkdir()
        original = next(bundle.rglob("*.dmg"))
        shutil.copyfile(original, duplicate / original.name)
        with self.assertRaisesRegex(
            artifacts.ArtifactError, "Duplicate artifact filename"
        ):
            self.collect(target)

    def test_collect_rejects_oversized_release_assets_before_hashing(self):
        target = "aarch64-apple-darwin"
        installer = next(self.make_bundle(target).rglob("*.dmg"))
        # A sparse test file tests the boundary without writing two GiB of data.
        with installer.open("wb") as stream:
            stream.truncate(artifacts.MAX_ASSET_BYTES)
        with self.assertRaisesRegex(artifacts.ArtifactError, "smaller than 2 GiB"):
            self.collect(target)

    def test_collect_rejects_stale_version_files(self):
        target = "aarch64-apple-darwin"
        installer = next(self.make_bundle(target).rglob("*.dmg"))
        installer.rename(installer.with_name(installer.name.replace(VERSION, "0.6.0")))
        with self.assertRaisesRegex(artifacts.ArtifactError, "release version"):
            self.collect(target)

    def test_collect_rejects_mismatched_appimage_installer_and_updater(self):
        target = "x86_64-unknown-linux-gnu"
        installer = next(self.make_bundle(target).rglob("*.AppImage"))
        installer.rename(installer.with_name(installer.name.replace("amd64", "x86_64")))
        with self.assertRaisesRegex(artifacts.ArtifactError, "names do not match"):
            self.collect(target)

    def test_collect_ignores_resources_inside_macos_application_bundle(self):
        target = "aarch64-apple-darwin"
        bundle = self.make_bundle(target)
        resource = bundle / "macos/LocalMind.app/Contents/Resources/python/launcher.exe"
        resource.parent.mkdir(parents=True)
        resource.write_bytes(b"bundled resource, not an installer")
        descriptor = self.collect(target)
        self.assertEqual(len(descriptor["assets"]), 3)

    def test_collect_output_must_be_outside_input_bundle(self):
        target = "aarch64-apple-darwin"
        bundle = self.make_bundle(target)
        with self.assertRaisesRegex(artifacts.ArtifactError, "outside the bundle"):
            artifacts.collect(
                target, VERSION, bundle / "collected", repository_root=self.repo
            )
        self.assertFalse((bundle / "collected").exists())

    def test_versions_and_repository_cannot_escape_paths_or_urls(self):
        for value in (
            "../0.7.0",
            "0.7.0/../x",
            "v0.7.0",
            "0.7",
            "0.7.0-01",
            "0.7.0\n",
            "01.7.0",
        ):
            with self.subTest(value=value), self.assertRaises(artifacts.ArtifactError):
                artifacts.validate_version(value)
        for value in ("0.7.0", "0.7.0-rc.1", "0.7.0+build.1"):
            self.assertEqual(artifacts.validate_version(value), value)
        for value in (
            "../other",
            "owner/repo/../escape",
            "owner/repo?x",
            "owner/repo#fragment",
        ):
            with self.subTest(value=value), self.assertRaises(artifacts.ArtifactError):
                artifacts.validate_repository(value)

    def test_assemble_verifies_every_updater_and_writes_complete_manifest_and_checksums(
        self,
    ):
        self.prepare_all(both_windows=True)
        with patch.object(artifacts, "verify_signature") as verify:
            manifest = self.assemble()
        self.assertEqual(verify.call_count, 5)
        self.assertEqual(set(manifest["platforms"]), artifacts.PLATFORMS)
        self.assertEqual(manifest["version"], VERSION)
        self.assertTrue(manifest["pub_date"].endswith("Z"))
        urls = [item["url"] for item in manifest["platforms"].values()]
        self.assertEqual(len(set(urls)), 4)
        self.assertTrue(
            all(
                f"https://github.com/{REPOSITORY}/releases/download/v{VERSION}/" in url
                for url in urls
            )
        )
        checksums = (self.out / "SHA256SUMS.txt").read_text().splitlines()
        listed = {}
        for line in checksums:
            digest, name = line.split("  ", 1)
            listed[name] = digest
            self.assertEqual(
                digest, hashlib.sha256((self.out / name).read_bytes()).hexdigest()
            )
        self.assertIn("latest.json", listed)
        self.assertNotIn("SHA256SUMS.txt", listed)
        self.assertFalse(any(name.startswith("manifest-") for name in listed))
        self.assertEqual(
            set(listed), {path.name for path in self.out.iterdir()} - {"SHA256SUMS.txt"}
        )

    def test_assemble_rejects_missing_platform(self):
        self.make_bundle("aarch64-apple-darwin")
        self.collect("aarch64-apple-darwin")
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(
                artifacts.ArtifactError, "missing required platforms"
            ),
        ):
            self.assemble()
        self.assertFalse(self.out.exists())

    def test_assemble_rejects_cross_platform_filename_collisions(self):
        self.prepare_all()
        path, descriptor = self.descriptor("darwin-x86_64")
        asset = next(
            item for item in descriptor["assets"] if item["filename"].endswith(".dmg")
        )
        new_name = f"LocalMind_{VERSION}_aarch64.dmg"
        (path.parent / asset["filename"]).rename(path.parent / new_name)
        asset["filename"] = new_name
        path.write_text(json.dumps(descriptor))
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(artifacts.ArtifactError, "filename collision"),
        ):
            self.assemble()

    def test_assemble_rejects_duplicate_platform_descriptors(self):
        self.prepare_all()
        shutil.copytree(self.inputs / "darwin-aarch64", self.inputs / "duplicate")
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(
                artifacts.ArtifactError, "Duplicate platform descriptor"
            ),
        ):
            self.assemble()

    def test_assemble_rejects_changed_assets(self):
        self.prepare_all()
        (
            self.inputs / "darwin-aarch64" / f"LocalMind_{VERSION}_aarch64.dmg"
        ).write_bytes(b"modified")
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(
                artifacts.ArtifactError, "checksum or size mismatch"
            ),
        ):
            self.assemble()

    def test_assemble_rejects_missing_assets(self):
        self.prepare_all()
        (self.inputs / "darwin-aarch64" / f"LocalMind_{VERSION}_aarch64.dmg").unlink()
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(artifacts.ArtifactError, "regular artifact file"),
        ):
            self.assemble()

    def test_assemble_rejects_signature_descriptor_mismatch(self):
        self.prepare_all()
        path, descriptor = self.descriptor("darwin-aarch64")
        descriptor["platforms"]["darwin-aarch64"]["signature"] = "changed"
        path.write_text(json.dumps(descriptor))
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(artifacts.ArtifactError, "does not match"),
        ):
            self.assemble()

    def test_assemble_rejects_unlisted_files_and_does_not_create_release(self):
        self.prepare_all()
        (self.inputs / "unexpected.exe").write_bytes(b"unexpected")
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(artifacts.ArtifactError, "Unlisted release input"),
        ):
            self.assemble()
        self.assertFalse(self.out.exists())

    def test_assemble_rejects_descriptor_path_traversal(self):
        self.prepare_all()
        path, descriptor = self.descriptor("darwin-aarch64")
        descriptor["assets"][0]["filename"] = "../escape.dmg"
        path.write_text(json.dumps(descriptor))
        with self.assertRaisesRegex(artifacts.ArtifactError, "plain safe filenames"):
            self.assemble()

    def test_assemble_rejects_malformed_descriptor_types(self):
        self.prepare_all()
        path, descriptor = self.descriptor("darwin-aarch64")
        descriptor["target"] = ["aarch64-apple-darwin"]
        path.write_text(json.dumps(descriptor))
        with self.assertRaisesRegex(artifacts.ArtifactError, "schema, or target"):
            self.assemble()

    def test_output_does_not_follow_symlinks(self):
        # Exercise the guard without requiring Windows symlink privileges.
        with (
            patch.object(Path, "is_symlink", return_value=True),
            self.assertRaisesRegex(artifacts.ArtifactError, "symlink"),
            artifacts.output_directory(self.out),
        ):
            self.fail("A symlink must never become a staging destination.")
        self.assertFalse(self.out.exists())

    def test_assemble_does_not_replace_an_existing_output(self):
        self.prepare_all()
        self.out.mkdir()
        original = self.out / "keep.txt"
        original.write_text("keep")
        with (
            patch.object(artifacts, "verify_signature"),
            self.assertRaisesRegex(artifacts.ArtifactError, "empty directory"),
        ):
            self.assemble()
        self.assertEqual(original.read_text(), "keep")

    def test_crypto_verifier_fails_closed_without_minisign(self):
        with (
            patch.object(shutil, "which", return_value=None),
            self.assertRaisesRegex(artifacts.ArtifactError, "minisign is required"),
        ):
            artifacts.verify_signature(self.root / "anything", SIGNATURE, PUBLIC_KEY)

    def test_crypto_verifier_passes_decoded_signature_and_configured_key_to_minisign(
        self,
    ):
        asset = self.root / "updater.zip"
        asset.write_bytes(b"content")

        def check_command(command, **kwargs):
            self.assertEqual(command[:4], ["minisign-test", "-V", "-q", "-m"])
            self.assertEqual(command[command.index("-P") + 1], PUBLIC_KEY)
            self.assertEqual(
                Path(command[command.index("-x") + 1]).read_bytes(), RAW_SIGNATURE
            )
            self.assertNotIn("shell", kwargs)
            return subprocess.CompletedProcess(command, 0)

        with (
            patch.object(shutil, "which", return_value="minisign-test"),
            patch.object(subprocess, "run", side_effect=check_command),
        ):
            artifacts.verify_signature(asset, SIGNATURE, PUBLIC_KEY)

    def test_crypto_failure_prevents_release_creation(self):
        self.prepare_all()
        with (
            patch.object(shutil, "which", return_value="minisign-test"),
            patch.object(
                subprocess, "run", return_value=subprocess.CompletedProcess([], 1)
            ),
            self.assertRaisesRegex(
                artifacts.ArtifactError, "signature verification failed"
            ),
        ):
            self.assemble()
        self.assertFalse(self.out.exists())

    @unittest.skipUnless(
        shutil.which("minisign"),
        "Install minisign to run the real cryptographic integration test.",
    )
    def test_real_minisign_accepts_signed_file_and_rejects_tampering(self):
        public, secret = self.root / "test.pub", self.root / "test.key"
        payload, signature_file = self.root / "payload", self.root / "payload.minisig"
        # Test-only ephemeral key material is never printed or committed.
        subprocess.run(
            ["minisign", "-G", "-W", "-p", str(public), "-s", str(secret)],
            check=True,
            capture_output=True,
        )
        payload.write_bytes(b"real cryptographic fixture")
        subprocess.run(
            [
                "minisign",
                "-S",
                "-s",
                str(secret),
                "-m",
                str(payload),
                "-x",
                str(signature_file),
            ],
            check=True,
            capture_output=True,
        )
        encoded = base64.b64encode(signature_file.read_bytes()).decode("ascii")
        key = artifacts.read_public_key(public)
        artifacts.verify_signature(payload, encoded, key)
        payload.write_bytes(b"tampered payload")
        with self.assertRaisesRegex(
            artifacts.ArtifactError, "signature verification failed"
        ):
            artifacts.verify_signature(payload, encoded, key)


if __name__ == "__main__":
    unittest.main()
