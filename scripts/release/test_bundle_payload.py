"""Test payload validation failures; these fixtures do not prove binaries run."""

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from smoke_bundle import (
    BundleError, OLLAMA_VERSION, check_payload, extract_appimage, find_services, seven_zip,
    smoke_bundle, verify_runtime, versioned_installer,
)


def fixture(root: Path, name: str, data: bytes = b"fixture") -> Path:
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def elf_fixture() -> bytes:
    header = bytearray(64)
    header[:7] = b"\x7fELF\x02\x01\x01"
    header[16:20] = b"\x03\x00\x3e\x00"
    header[20:24] = b"\x01\x00\x00\x00"
    return bytes(header) + b"fixture only, not executable"


class BundlePayloadTests(unittest.TestCase):
    def test_linux_payload_rejects_packaged_nvidia_driver(self):
        with tempfile.TemporaryDirectory() as directory:
            payload = Path(directory)
            fixture(payload, "usr/lib/libcuda.so.617.42")
            with patch("smoke_bundle.smoke_ollama") as smoke:
                with self.assertRaisesRegex(BundleError, "must use the host NVIDIA driver"):
                    check_payload(payload, "x86_64-unknown-linux-gnu", "LocalMind", 5)
                smoke.assert_not_called()

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
            fixture(root, f"target/{target}/release/bundle/appimage/LocalMind_0.7.0_amd64.AppImage")
            with patch("smoke_bundle.ROOT", root), \
                    patch("smoke_bundle.validate_target"), \
                    patch("smoke_bundle.extract_installer"), \
                    patch("smoke_bundle.extract_appimage", return_value=root / "AppDir"), \
                    patch("smoke_bundle.print"), \
                    patch("smoke_bundle.check_payload") as check:
                smoke_bundle(target, 30)
            self.assertEqual(check.call_count, 2)
            for call in check.call_args_list:
                self.assertEqual(call.args[1:], (target, "LocalMind", 30))
            self.assertEqual(check.call_args_list[0].kwargs, {"appimage": False})
            self.assertEqual(check.call_args_list[1].kwargs, {"appimage": True})
            self.assertEqual(check.call_args_list[1].args[0], root / "AppDir")

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

    def test_appimage_selection_rejects_missing_stale_and_ambiguous_images(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture(root, "LocalMind_0.6.0_amd64.AppImage")
            fixture(root, "LocalMind_0.7.0-beta_amd64.AppImage")
            with self.assertRaises(BundleError):
                versioned_installer(root, ".AppImage", "0.7.0")
            expected = fixture(root, "LocalMind_0.7.0_amd64.AppImage")
            self.assertEqual(versioned_installer(root, ".AppImage", "0.7.0"), expected)
            fixture(root, "LocalMind_0.7.0_duplicate.AppImage")
            with self.assertRaises(BundleError):
                versioned_installer(root, ".AppImage", "0.7.0")

    def test_appimage_extracts_only_in_owned_directory_without_extract_and_run(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = fixture(root, "LocalMind_0.7.0_amd64.AppImage")
            destination = root / "extracted"

            def extracted(command, **kwargs):
                (kwargs["cwd"] / "squashfs-root").mkdir()
                return subprocess.CompletedProcess(command, 0, "", "")

            with patch.dict(os.environ, {"APPIMAGE_EXTRACT_AND_RUN": "1"}), \
                    patch("smoke_bundle.subprocess.run", side_effect=extracted) as run:
                self.assertEqual(extract_appimage(image, destination), destination / "squashfs-root")
                self.assertEqual(os.environ["APPIMAGE_EXTRACT_AND_RUN"], "1")
            self.assertEqual(run.call_args.args[0], [str(image.resolve()), "--appimage-extract"])
            self.assertEqual(run.call_args.kwargs["cwd"], destination)
            self.assertNotIn("APPIMAGE_EXTRACT_AND_RUN", run.call_args.kwargs["env"])

    def test_corrupt_appimage_extraction_and_missing_appdir_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = fixture(root, "LocalMind_0.7.0_amd64.AppImage")
            with patch("smoke_bundle.subprocess.run", return_value=subprocess.CompletedProcess(
                [], 1, "", "invalid squashfs image",
            )):
                with self.assertRaisesRegex(BundleError, "invalid squashfs image"):
                    extract_appimage(image, root / "corrupt")
            with patch("smoke_bundle.subprocess.run", return_value=subprocess.CompletedProcess([], 0, "", "")):
                with self.assertRaisesRegex(BundleError, "squashfs-root"):
                    extract_appimage(image, root / "missing")

    def test_missing_appimage_blocks_all_payload_smokes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = "x86_64-unknown-linux-gnu"
            fixture(root, "apps/desktop/src-tauri/tauri.conf.json", json.dumps({
                "version": "0.7.0", "productName": "LocalMind",
            }).encode())
            fixture(root, "apps/desktop/src-tauri/Cargo.toml", b'[package]\nname="localmind-app"\n')
            fixture(root, f"target/{target}/release/bundle/deb/LocalMind_0.7.0_amd64.deb")
            with patch("smoke_bundle.ROOT", root), patch("smoke_bundle.validate_target"), \
                    patch("smoke_bundle.check_payload") as check:
                with self.assertRaisesRegex(BundleError, "AppImage"):
                    smoke_bundle(target, 30)
            check.assert_not_called()

    def test_appimage_elf_size_exception_does_not_relax_deb_or_data_checks(self):
        with tempfile.TemporaryDirectory() as directory:
            staged, runtime = Path(directory) / "staged", Path(directory) / "AppDir/runtime"
            target = "x86_64-unknown-linux-gnu"
            for root in (staged, runtime):
                fixture(root, "bin/ollama", elf_fixture())
                fixture(root, "lib/ollama/llama-server", elf_fixture())
                fixture(root, "lib/ollama/libggml.so", elf_fixture())
                fixture(root, "LICENSE", b"original")
                fixture(root, "localmind-runtime.json", json.dumps({
                    "version": OLLAMA_VERSION, "target": target,
                }).encode())
            fixture(runtime, "lib/ollama/libggml.so", elf_fixture() + b"rewritten rpath")
            with patch("smoke_bundle.os.access", return_value=True), patch("smoke_bundle.print"):
                with self.assertRaisesRegex(BundleError, "changed size"):
                    verify_runtime(runtime, staged, target)
                self.assertEqual(verify_runtime(runtime, staged, target, appimage=True), runtime / "bin/ollama")
                fixture(runtime, "LICENSE", b"truncated")
                with self.assertRaisesRegex(BundleError, "LICENSE"):
                    verify_runtime(runtime, staged, target, appimage=True)
                fixture(runtime, "LICENSE", b"original")
                fixture(runtime, "lib/ollama/libggml.so", b"\x7fELF")
                with self.assertRaisesRegex(BundleError, "changed size"):
                    verify_runtime(runtime, staged, target, appimage=True)

    def test_appimage_missing_payload_and_sidecar_startup_failure_cannot_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            payload = Path(directory)
            target = "x86_64-unknown-linux-gnu"
            with patch("smoke_bundle.smoke_sidecar") as sidecar, \
                    patch("smoke_bundle.smoke_ollama") as ollama:
                with self.assertRaises(BundleError):
                    check_payload(payload, target, "LocalMind", 30, appimage=True)
                sidecar.assert_not_called()
                ollama.assert_not_called()
            binary = payload / "usr/bin/localmind-sidecar"
            runtime = payload / "usr/lib/LocalMind/ollama-runtime"
            with patch("smoke_bundle.find_services", return_value=(binary, runtime)), \
                    patch("smoke_bundle.verify_runtime", return_value=runtime / "bin/ollama"), \
                    patch("smoke_bundle.print"), \
                    patch("smoke_bundle.smoke_sidecar", side_effect=RuntimeError("corrupt sidecar")) as sidecar, \
                    patch("smoke_bundle.smoke_ollama") as ollama:
                with self.assertRaisesRegex(RuntimeError, "corrupt sidecar"):
                    check_payload(payload, target, "LocalMind", 30, appimage=True)
                sidecar.assert_called_once_with(binary, 30)
                ollama.assert_not_called()


if __name__ == "__main__":
    unittest.main()
