"""Verify dependency preflight behavior and, on Linux, real loader resolution."""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from build_linux import (
    BASE_LIBRARY, TAURI_COMMAND, DependencyError, build_environment, build_linux,
    main, preflight, shared_libraries,
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
                    patch("build_linux.subprocess.run", side_effect=run), patch("build_linux.print"):
                build_linux(runtime)
            self.assertEqual([call[0][0] for call in observed], ["ldd", "ldd", "pnpm"])
            self.assertEqual(observed[-1][0], TAURI_COMMAND)
            self.assertTrue(observed[-1][1]["check"])

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
