"""Verify dependency preflight behavior and, on Linux, real loader resolution."""

import os
import hashlib
import io
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from build_linux import (
    BASE_LIBRARY, TAURI_COMMAND, DependencyError, build_environment, build_linux,
    main, preflight, prepare_linuxdeploy, shared_libraries,
)


def fixture_elf(root: Path, relative: str) -> Path:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    header = bytearray(64)
    header[:7] = b"\x7fELF\x02\x01\x01"
    header[16:20] = b"\x03\x00\x3e\x00"
    header[20:24] = b"\x01\x00\x00\x00"
    path.write_bytes(header)
    return path.resolve()


class LinuxBuildTests(unittest.TestCase):
    def test_child_environment_preserves_existing_paths_without_empty_segments_or_leaking(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {
            "LD_LIBRARY_PATH": ":/python/lib::/existing/lib:", "NO_STRIP": "false",
        }):
            runtime = Path(temporary)
            before = os.environ.copy()
            environment = build_environment(runtime)
            self.assertEqual(environment["LD_LIBRARY_PATH"],
                             f"{(runtime / 'lib/ollama').resolve()}:/python/lib:/existing/lib")
            self.assertEqual(environment["NO_STRIP"], "true")
            self.assertEqual(environment["LC_ALL"], "C")
            self.assertEqual(environment["LINUXDEPLOY_EXCLUDED_LIBRARIES"], "libcuda.so*")
            self.assertEqual(dict(os.environ), before)
            self.assertNotIn("cuda_v12", environment["LD_LIBRARY_PATH"])

    def test_empty_inherited_library_path_does_not_add_current_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary)
            for inherited in ({}, {"LD_LIBRARY_PATH": "::"}):
                self.assertEqual(build_environment(runtime, inherited)["LD_LIBRARY_PATH"],
                                 str((runtime / "lib/ollama").resolve()))

    def test_nested_cuda_dependency_resolves_to_parent_base_before_pnpm(self):
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary)
            base = fixture_elf(runtime, f"lib/ollama/{BASE_LIBRARY}")
            cuda = fixture_elf(runtime, "lib/ollama/cuda_v12/libggml-cuda.so")
            observed = []

            def run(command, **kwargs):
                observed.append((command, kwargs))
                if command[0] == "ldd":
                    output = ""
                    if command[1] == str(cuda):
                        common = str(base.parent)
                        output = f"{BASE_LIBRARY} => {base} (0x1234)\n" if kwargs["env"]["LD_LIBRARY_PATH"].startswith(common) else f"{BASE_LIBRARY} => not found\n"
                    return subprocess.CompletedProcess(command, 0, output, "")
                return subprocess.CompletedProcess(command, 0)

            with patch("build_linux.shutil.which", return_value="ldd"), \
                    patch("build_linux.subprocess.run", side_effect=run), patch("build_linux.print"), \
                    patch("build_linux.prepare_linuxdeploy") as prepare:
                build_linux(runtime)
            self.assertEqual([call[0][0] for call in observed], ["ldd", "ldd", "pnpm"])
            self.assertEqual(observed[-1][0], TAURI_COMMAND)
            self.assertTrue(observed[-1][1]["check"])
            prepare.assert_called_once()
            self.assertEqual(observed[-1][1]["env"]["XDG_CACHE_HOME"], str(prepare.call_args.args[0]))
            self.assertEqual(observed[-1][1]["env"]["LINUXDEPLOY_EXCLUDED_LIBRARIES"], "libcuda.so*")

    def test_missing_host_driver_is_allowed_only_for_cuda_backends(self):
        for relative, allowed in (
            ("cuda_v12/libggml-cuda.so", True), ("cuda_v13/libggml-cuda.so", True),
            ("libggml-cpu.so", False), ("vulkan/libggml-vulkan.so", False),
        ):
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as temporary:
                runtime = Path(temporary)
                base = fixture_elf(runtime, f"lib/ollama/{BASE_LIBRARY}")
                backend = fixture_elf(runtime, f"lib/ollama/{relative}")

                def run(command, **kwargs):
                    output = ""
                    if command[1] == str(backend):
                        output = f"{BASE_LIBRARY} => {base} (0x1234)\nlibcuda.so.1 => not found\n"
                    return subprocess.CompletedProcess(command, 0, output, "")

                with patch("build_linux.shutil.which", return_value="ldd"), \
                        patch("build_linux.subprocess.run", side_effect=run), patch("build_linux.print"):
                    if allowed:
                        preflight(runtime, build_environment(runtime))
                    else:
                        with self.assertRaisesRegex(DependencyError, "missing dependencies: libcuda.so.1"):
                            preflight(runtime, build_environment(runtime))

    def test_missing_cuda_toolkit_libraries_still_fail_before_build(self):
        for missing in (BASE_LIBRARY, "libcudart.so.12", "libcublas.so.13"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temporary:
                runtime = Path(temporary)
                fixture_elf(runtime, f"lib/ollama/{BASE_LIBRARY}")
                cuda = fixture_elf(runtime, "lib/ollama/cuda_v12/libggml-cuda.so")

                def run(command, **kwargs):
                    output = f"libcuda.so.1 => not found\n{missing} => not found\n" if command[1] == str(cuda) else ""
                    return subprocess.CompletedProcess(command, 0, output, "")

                with patch("build_linux.shutil.which", return_value="ldd"), \
                        patch("build_linux.subprocess.run", side_effect=run), \
                        patch("build_linux.prepare_linuxdeploy") as prepare:
                    with self.assertRaises(DependencyError) as caught:
                        build_linux(runtime)
                    self.assertIn(missing, str(caught.exception))
                    prepare.assert_not_called()

    def test_linuxdeploy_download_is_verified_before_installation(self):
        data = b"test tool bytes"
        with tempfile.TemporaryDirectory() as temporary, \
                patch("build_linux.urlopen", return_value=io.BytesIO(data)), \
                patch("build_linux.LINUXDEPLOY_SHA256", hashlib.sha256(data).hexdigest()), \
                patch("build_linux.print"):
            tool = prepare_linuxdeploy(Path(temporary))
            self.assertEqual(tool.read_bytes(), data)
            self.assertEqual(tool.relative_to(temporary).as_posix(), "tauri/linuxdeploy-x86_64.AppImage")

    def test_linuxdeploy_digest_mismatch_never_installs_tool(self):
        with tempfile.TemporaryDirectory() as temporary, \
                patch("build_linux.urlopen", return_value=io.BytesIO(b"wrong download")):
            with self.assertRaisesRegex(DependencyError, "pinned SHA-256"):
                prepare_linuxdeploy(Path(temporary))
            self.assertFalse(any(path.is_file() for path in Path(temporary).rglob("*")))

    def test_missing_dependencies_and_ldd_errors_are_aggregated_before_pnpm(self):
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary)
            fixture_elf(runtime, f"lib/ollama/{BASE_LIBRARY}")
            cuda = fixture_elf(runtime, "lib/ollama/cuda_v12/libggml-cuda.so")

            def run(command, **kwargs):
                if command[1] == str(cuda):
                    return subprocess.CompletedProcess(command, 0, f"{BASE_LIBRARY} => not found\n", "")
                return subprocess.CompletedProcess(command, 1, "", "loader error")

            with patch("build_linux.shutil.which", return_value="ldd"), \
                    patch("build_linux.subprocess.run", side_effect=run) as calls:
                with self.assertRaises(DependencyError) as caught:
                    build_linux(runtime)
            self.assertIn(str(cuda), str(caught.exception))
            self.assertIn("missing dependencies: libggml-base.so.0", str(caught.exception))
            self.assertIn("ldd exited with status 1", str(caught.exception))
            self.assertIn("loader error", str(caught.exception))
            self.assertEqual(calls.call_count, 2)
            self.assertTrue(all(call.args[0][0] == "ldd" for call in calls.call_args_list))

    def test_base_resolution_from_another_installation_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary)
            fixture_elf(runtime, f"lib/ollama/{BASE_LIBRARY}")
            fixture_elf(runtime, "lib/ollama/cuda_v12/libggml-cuda.so")
            output = f"{BASE_LIBRARY} => /other-installation/{BASE_LIBRARY} (0x1234)\n"
            with patch("build_linux.shutil.which", return_value="ldd"), \
                    patch("build_linux.subprocess.run", return_value=subprocess.CompletedProcess([], 0, output, "")):
                with self.assertRaisesRegex(DependencyError, "must resolve to staged"):
                    preflight(runtime, build_environment(runtime))

    def test_missing_base_is_rejected_before_any_subprocess(self):
        with tempfile.TemporaryDirectory() as temporary, patch("build_linux.subprocess.run") as run:
            with self.assertRaisesRegex(DependencyError, "base library is missing"):
                build_linux(Path(temporary))
            run.assert_not_called()

    def test_cli_preserves_tauri_failure_status(self):
        with patch("build_linux.validate_target"), \
                patch("build_linux.build_linux", side_effect=subprocess.CalledProcessError(42, TAURI_COMMAND)), \
                patch("sys.stderr"):
            with self.assertRaises(SystemExit) as caught:
                main([])
            self.assertEqual(caught.exception.code, 42)


@unittest.skipUnless(sys.platform == "linux" and shutil.which("cc") and shutil.which("ldd"),
                     "Native Linux cc and ldd are required for a real loader test")
class NativeLinuxLoaderTests(unittest.TestCase):
    @unittest.skipUnless(os.environ.get("LOCALMIND_TEST_LINUXDEPLOY") == "1",
                         "Set LOCALMIND_TEST_LINUXDEPLOY=1 to download and test the pinned packaging tool")
    def test_real_linuxdeploy_packages_cuda_backend_without_host_driver(self):
        with tempfile.TemporaryDirectory(prefix="localmind-driver-fixture-") as temporary:
            scratch = Path(temporary)
            payload = scratch / "Fixture.AppDir"
            runtime = payload / "usr/lib/LocalMind/ollama-runtime"
            common = runtime / "lib/ollama"
            nested = common / "cuda_v12"
            nested.mkdir(parents=True)
            source = scratch / "fixture.c"
            driver = scratch / "libcuda.so.1"
            source.write_text("int fixture_driver(void) { return 0; }\n")
            subprocess.run(["cc", "-shared", "-fPIC", str(source), "-Wl,-soname,libcuda.so.1", "-o", str(driver)], check=True, capture_output=True)
            source.write_text('#include <stdio.h>\nint fixture_base(void) { return puts("fixture"); }\n')
            subprocess.run(["cc", "-shared", "-fPIC", str(source), f"-Wl,-soname,{BASE_LIBRARY}", "-o", str(common / BASE_LIBRARY)], check=True, capture_output=True)
            source.write_text("extern int fixture_base(void);\nextern int fixture_driver(void);\nint fixture_cuda(void) { return fixture_base() + fixture_driver(); }\n")
            subprocess.run(["cc", "-shared", "-fPIC", str(source), f"-L{common}", f"-L{scratch}", f"-l:{BASE_LIBRARY}", "-l:libcuda.so.1", "-o", str(nested / "libggml-cuda.so")], check=True, capture_output=True)
            # The link-only driver is outside the AppDir and is removed before
            # dependency scanning, reproducing the hosted runner's missing GPU.
            driver.unlink()
            executable = payload / "usr/bin/fixture"
            executable.parent.mkdir(parents=True)
            source.write_text("int main(void) { return 0; }\n")
            subprocess.run(["cc", str(source), "-o", str(executable)], check=True, capture_output=True)
            desktop = scratch / "fixture.desktop"
            desktop.write_text("[Desktop Entry]\nType=Application\nName=Fixture\nExec=fixture\nIcon=fixture\nCategories=Utility;\n")
            icon = scratch / "fixture.svg"
            icon.write_text('<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64"/></svg>')
            environment = build_environment(runtime)
            environment["APPIMAGE_EXTRACT_AND_RUN"] = "1"
            # This CI runner must actually reproduce the absent-driver case.
            observed = subprocess.run(["ldd", str(nested / "libggml-cuda.so")], env=environment, capture_output=True, text=True, check=True)
            self.assertRegex(observed.stdout, r"libcuda\.so\.1\s*=>\s*not found")
            preflight(runtime, environment)
            tool = prepare_linuxdeploy(scratch / "cache")
            command = [str(tool), "--appdir", str(payload), "--desktop-file", str(desktop), "--icon-file", str(icon)]
            baseline = subprocess.run(command, env=dict(environment, LINUXDEPLOY_EXCLUDED_LIBRARIES=""), capture_output=True, text=True, timeout=120)
            self.assertNotEqual(baseline.returncode, 0)
            self.assertIn("Could not find dependency: libcuda.so.1", baseline.stdout + baseline.stderr)
            result = subprocess.run(command, env=environment, capture_output=True, text=True, timeout=120)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertTrue((nested / "libggml-cuda.so").is_file())
            self.assertTrue((common / BASE_LIBRARY).is_file())
            self.assertEqual(list(payload.rglob("libcuda.so*")), [])

    def test_nested_library_needs_common_search_path_and_aliases_are_checked_once(self):
        with tempfile.TemporaryDirectory(prefix="localmind-ldd-fixture-") as temporary:
            runtime = Path(temporary)
            common = runtime / "lib/ollama"
            nested = common / "cuda_v12"
            nested.mkdir(parents=True)
            base_source, cuda_source = runtime / "base.c", runtime / "cuda.c"
            # Keep a real libc dependency so ldd consistently treats this as a
            # dynamic library instead of reporting a dependency-free fixture
            # as statically linked. The fixture functions are never executed.
            base_source.write_text('#include <stdio.h>\nint localmind_fixture_base(void) { return puts("fixture"); }\n')
            cuda_source.write_text("extern int localmind_fixture_base(void);\nint fixture_cuda(void) { return localmind_fixture_base(); }\n")
            base = common / BASE_LIBRARY
            cuda = nested / "libggml-cuda.so"
            subprocess.run(["cc", "-shared", "-fPIC", str(base_source), f"-Wl,-soname,{BASE_LIBRARY}", "-o", str(base)], check=True, capture_output=True)
            subprocess.run(["cc", "-shared", "-fPIC", str(cuda_source), f"-L{common}", f"-l:{BASE_LIBRARY}", "-o", str(cuda)], check=True, capture_output=True)
            (common / "libggml-base-alias.so").symlink_to(base.name)
            # Use an empty path for the baseline to avoid inherited staging libs.
            baseline = dict(os.environ, LD_LIBRARY_PATH="", LC_ALL="C")
            observed = subprocess.run(["ldd", str(cuda)], env=baseline, capture_output=True, text=True, check=True)
            self.assertRegex(observed.stdout, r"libggml-base\.so\.0\s*=>\s*not found")
            before = os.environ.copy()
            environment = build_environment(runtime, baseline)
            with patch("build_linux.print"):
                preflight(runtime, environment)
            self.assertEqual(dict(os.environ), before)
            _, libraries = shared_libraries(runtime)
            self.assertEqual(set(libraries), {base.resolve(), cuda.resolve()})


if __name__ == "__main__":
    unittest.main()
