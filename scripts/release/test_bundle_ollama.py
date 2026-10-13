"""Release runtime integrity regressions. No upstream downloads are performed."""

import hashlib
import io
import json
from pathlib import Path
import stat
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import bundle_ollama as bundle


class RuntimeArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="localmind-runtime-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.destination = self.root / "runtime"
        self.destination.mkdir()

    def zip(self, entries):
        path = self.root / "runtime.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for name, value in entries:
                archive.writestr(name, value)
        return path

    def validate_tar(self, entries):
        data = io.BytesIO()
        with tarfile.open(fileobj=data, mode="w") as archive:
            for name, kind, target in entries:
                entry = tarfile.TarInfo(name)
                entry.type = kind
                entry.linkname = target
                archive.addfile(entry)
        data.seek(0)
        with tarfile.open(fileobj=data, mode="r|") as archive:
            bundle.validate_tar(archive)

    def test_zip_preserves_executable_helper_and_gpu_library_layout(self):
        entries = [
            ("ollama.exe", b"executable"),
            ("lib/ollama/llama-server.exe", b"helper"),
            ("lib/ollama/cuda_v12/ggml-cuda.dll", b"gpu library"),
            ("lib/ollama/THIRD_PARTY_LICENSE", b"license"),
        ]
        (self.destination / ".gitkeep").touch()
        bundle.extract_archive(self.zip(entries), "ollama-windows-amd64.zip", self.destination)
        for path, content in entries:
            self.assertEqual((self.destination / path).read_bytes(), content)

    def test_zip_rejects_path_escape_before_writing_any_file(self):
        for name in ["../escape", "/escape", "C:/escape", "C:escape", "\\\\server\\share\\escape", "safe\\..\\escape", "file:stream"]:
            with self.subTest(name=name):
                archive = self.zip([("valid", b"should not be written"), (name, b"bad")])
                with self.assertRaises(ValueError):
                    bundle.extract_archive(archive, "runtime.zip", self.destination)
                self.assertEqual(list(self.destination.iterdir()), [])

    def test_zip_rejects_case_collisions(self):
        archive = self.zip([("LIB/file.dll", b"first"), ("lib/FILE.dll", b"second")])
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            bundle.extract_archive(archive, "runtime.zip", self.destination)

    def test_zip_rejects_symlinks(self):
        entry = zipfile.ZipInfo("link")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        archive = self.zip([(entry, b"../escape")])
        with self.assertRaisesRegex(ValueError, "Unsupported ZIP"):
            bundle.extract_archive(archive, "runtime.zip", self.destination)

    def test_existing_runtime_cannot_be_overwritten_or_merged(self):
        existing = self.destination / "ollama.exe"
        existing.write_bytes(b"existing")
        archive = self.zip([("ollama.exe", b"replacement")])
        with self.assertRaisesRegex(RuntimeError, "nonempty"):
            bundle.extract_archive(archive, "runtime.zip", self.destination)
        self.assertEqual(existing.read_bytes(), b"existing")

    def test_tar_allows_internal_library_symlinks_and_hardlinks(self):
        self.validate_tar([
            ("./", tarfile.DIRTYPE, ""),
            ("lib/libgpu.so.1", tarfile.REGTYPE, ""),
            ("lib/libgpu.so", tarfile.SYMTYPE, "libgpu.so.1"),
            ("lib/backend/gpu.so", tarfile.SYMTYPE, "../libgpu.so.1"),
            ("lib/libgpu-copy.so", tarfile.LNKTYPE, "lib/libgpu.so.1"),
        ])

    def test_tar_rejects_parent_path_and_external_link_targets(self):
        for entry in [
            ("../escape", tarfile.REGTYPE, ""),
            ("lib/link", tarfile.SYMTYPE, "../../escape"),
            ("lib/link", tarfile.SYMTYPE, "/outside"),
            ("lib/link", tarfile.LNKTYPE, "../outside"),
        ]:
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                self.validate_tar([entry])

    def test_tar_rejects_writing_through_symlink_in_either_member_order(self):
        entries = [("lib", tarfile.SYMTYPE, "other"), ("lib/file", tarfile.REGTYPE, "")]
        for ordered in [entries, list(reversed(entries))]:
            with self.subTest(entries=ordered), self.assertRaisesRegex(ValueError, "traverses a symlink"):
                self.validate_tar(ordered)

    def test_tar_rejects_special_files_and_invalid_hardlinks(self):
        for entry in [("device", tarfile.CHRTYPE, ""), ("link", tarfile.LNKTYPE, "missing")]:
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                self.validate_tar([entry])


class RuntimeDownloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="localmind-runtime-download-test-")
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "download"
        self.payload = b"verified runtime fixture"
        self.asset = {
            "name": "ollama-windows-amd64.zip",
            "size": len(self.payload),
            "digest": "sha256:" + hashlib.sha256(self.payload).hexdigest(),
            "browser_download_url": f"https://github.com/ollama/ollama/releases/download/v{bundle.VERSION}/ollama-windows-amd64.zip",
        }

    def opener(self, body, captured=None):
        class Opener:
            def open(self, request, timeout):
                if captured is not None:
                    captured.append(request)
                return io.BytesIO(body)
        return Opener()

    def test_api_token_is_never_attached_to_download_request(self):
        captured = []
        metadata = json.dumps({"tag_name": f"v{bundle.VERSION}", "draft": False, "prerelease": False, "assets": [self.asset]}).encode()
        with patch.dict(bundle.os.environ, {"GITHUB_TOKEN": "fixture-token"}), patch.object(bundle, "build_opener", side_effect=[self.opener(metadata, captured), self.opener(self.payload, captured)]):
            asset = bundle.release_asset(self.asset["name"])
            bundle.download(asset, self.path)
        self.assertEqual(captured[0].get_header("Authorization"), "Bearer fixture-token")
        self.assertIsNone(captured[1].get_header("Authorization"))
        self.assertEqual(self.path.read_bytes(), self.payload)

    def test_release_requires_digest_published_version_and_official_download_url(self):
        scenarios = [
            {"assets": [{**self.asset, "digest": None}]},
            {"assets": [{**self.asset, "browser_download_url": "https://example.com/runtime.zip"}]},
            {"prerelease": True},
            {"draft": True},
            {"tag_name": "v0.0.0"},
        ]
        for change in scenarios:
            metadata = {"tag_name": f"v{bundle.VERSION}", "draft": False, "prerelease": False, "assets": [self.asset], **change}
            with self.subTest(change=change), patch.object(bundle, "build_opener", return_value=self.opener(json.dumps(metadata).encode())), self.assertRaises(RuntimeError):
                bundle.release_asset(self.asset["name"])

    def test_download_rejects_same_size_corruption_truncation_and_extra_bytes(self):
        for payload in [b"X" * len(self.payload), self.payload[:-1], self.payload + b"extra"]:
            with self.subTest(payload=payload), patch.object(bundle, "build_opener", return_value=self.opener(payload)), self.assertRaisesRegex(RuntimeError, "integrity|exceeded"):
                bundle.download(self.asset, self.path)


if __name__ == "__main__":
    unittest.main()
