"""Test payload validation failures; these fixtures do not prove binaries run."""

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from smoke_bundle import (
    BundleError, OLLAMA_VERSION, find_services, seven_zip,
    smoke_bundle, verify_runtime, versioned_installer,
)


def fixture(root: Path, name: str, data: bytes = b"fixture") -> Path:
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


class BundlePayloadTests(unittest.TestCase):
    def test_installer_selection_rejects_stale_or_ambiguous_versions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture(root, "LocalMind_0.6.0_x64-setup.exe")
            fixture(root, "LocalMind_0.7.0-beta_x64-setup.exe")
            with self.assertRaises(BundleError):
                versioned_installer(root, ".exe", "0.7.0")
            expected = fixture(root, "LocalMind_0.7.0_x64-setup.exe")
            self.assertEqual(versioned_installer(root, ".exe", "0.7.0"), expected)
            fixture(root, "LocalMind_0.7.0_x64-other.exe")
            with self.assertRaises(BundleError):
                versioned_installer(root, ".exe", "0.7.0")

    def test_windows_requires_resources_beside_app_and_unique_sidecar(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sidecar = fixture(root, "app/localmind-sidecar.exe")
            fixture(root, "wrong/ollama-runtime/ollama.exe")
            with self.assertRaisesRegex(BundleError, "beside"):
                find_services(root, "x86_64-pc-windows-msvc", "localmind-app")
            (root / "wrong/ollama-runtime/ollama.exe").unlink()
            fixture(root, "app/ollama-runtime/ollama.exe")
            self.assertEqual(find_services(root, "x86_64-pc-windows-msvc", "localmind-app")[0], sidecar)
            fixture(root, "duplicate/localmind-sidecar.exe")
            with self.assertRaisesRegex(BundleError, "exactly one"):
                find_services(root, "x86_64-pc-windows-msvc", "localmind-app")

    def test_linux_resource_directory_follows_product_name(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture(root, "usr/bin/localmind-sidecar")
            fixture(root, "usr/lib/LocalMind/ollama-runtime/bin/ollama")
            with self.assertRaisesRegex(BundleError, "resources must"):
                find_services(root, "x86_64-unknown-linux-gnu", "localmind-app")
            self.assertEqual(
                find_services(root, "x86_64-unknown-linux-gnu", "LocalMind")[1],
                root / "usr/lib/LocalMind/ollama-runtime",
            )

    def test_linux_smoke_uses_config_product_name_instead_of_binary_or_cargo_name(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = "x86_64-unknown-linux-gnu"
            fixture(root, "apps/desktop/src-tauri/tauri.conf.json", json.dumps({
                "version": "0.7.0", "productName": "LocalMind",
                "mainBinaryName": "renamed-binary",
            }).encode())
            fixture(root, "apps/desktop/src-tauri/Cargo.toml",
                    b'[package]\nname = "localmind-app"\n')
            fixture(root, f"target/{target}/release/bundle/deb/LocalMind_0.7.0_amd64.deb")
            with patch("smoke_bundle.ROOT", root), \
                    patch("smoke_bundle.validate_target"), \
                    patch("smoke_bundle.extract_installer"), \
                    patch("smoke_bundle.print"), \
                    patch("smoke_bundle.check_payload") as check:
                smoke_bundle(target, 30)
            self.assertEqual(check.call_count, 1)
            self.assertEqual(check.call_args.args[1:], (target, "LocalMind", 30))

    def test_missing_runtime_library_fails_even_when_both_executables_exist(self):
        with tempfile.TemporaryDirectory() as directory:
            staged, runtime = Path(directory) / "staged", Path(directory) / "bundled"
            for root in (staged, runtime):
                fixture(root, "ollama.exe")
                fixture(root, "lib/ollama/llama-server.exe")
                fixture(root, "localmind-runtime.json", json.dumps({
                    "version": OLLAMA_VERSION, "target": "x86_64-pc-windows-msvc",
                }).encode())
            fixture(staged, "lib/ollama/cpu/ggml.dll")
            with self.assertRaisesRegex(BundleError, "ggml.dll"):
                verify_runtime(runtime, staged, "x86_64-pc-windows-msvc")
            fixture(runtime, "lib/ollama/cpu/ggml.dll")
            with patch("smoke_bundle.os.access", return_value=True):
                self.assertEqual(
                    verify_runtime(runtime, staged, "x86_64-pc-windows-msvc"),
                    runtime / "ollama.exe",
                )

    def test_missing_nsis_extractor_has_clear_error(self):
        with patch("smoke_bundle.shutil.which", return_value=None), \
                patch.dict("os.environ", {}, clear=True):
            with self.assertRaisesRegex(BundleError, "7-Zip with NSIS"):
                seven_zip()


if __name__ == "__main__":
    unittest.main()
