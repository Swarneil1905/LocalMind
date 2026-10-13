"""Guard release architecture selection and isolation before invoking PyInstaller."""

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

from build_sidecar import build, validate_target  # noqa: E402
from smoke_sidecar import isolated_environment  # noqa: E402


class NativeSidecarBuildTests(unittest.TestCase):
    def test_cross_architecture_build_fails_before_running_pyinstaller(self):
        with patch("build_sidecar.platform.system", return_value="Darwin"), \
                patch("build_sidecar.platform.machine", return_value="arm64"), \
                patch("build_sidecar.subprocess.run") as run:
            with self.assertRaisesRegex(RuntimeError, "matching runner"):
                build("x86_64-apple-darwin")
            run.assert_not_called()

    def test_matching_machine_aliases_are_accepted(self):
        for system, machine, target in (
            ("Darwin", "arm64", "aarch64-apple-darwin"),
            ("Windows", "AMD64", "x86_64-pc-windows-msvc"),
            ("Linux", "x86_64", "x86_64-unknown-linux-gnu"),
        ):
            with self.subTest(target=target), \
                    patch("build_sidecar.platform.system", return_value=system), \
                    patch("build_sidecar.platform.machine", return_value=machine), \
                    patch("build_sidecar.struct.calcsize", return_value=8):
                validate_target(target)

    def test_32_bit_python_is_rejected_even_on_64_bit_os(self):
        with patch("build_sidecar.platform.system", return_value="Windows"), \
                patch("build_sidecar.platform.machine", return_value="AMD64"), \
                patch("build_sidecar.struct.calcsize", return_value=4):
            with self.assertRaisesRegex(RuntimeError, "32-bit"):
                validate_target("x86_64-pc-windows-msvc")


class FrozenSmokeIsolationTests(unittest.TestCase):
    def test_real_profile_credentials_and_python_path_are_not_inherited(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {
            "HOME": "/real-home", "USERPROFILE": "/real-home", "APPDATA": "/real-data",
            "LOCALMIND_DATA_DIR": "/real-profile", "LOCALMIND_TOKEN": "real-token",
            "GOOGLE_CLIENT_ID": "real-client", "GOOGLE_CLIENT_SECRET": "real-secret",
            "PYTHONPATH": "/source-tree", "PATH": "/system-bin",
        }, clear=True):
            scratch = Path(directory)
            isolated = isolated_environment(scratch, scratch / "profile", 12345, "fresh-token")
            self.assertEqual(isolated["LOCALMIND_DATA_DIR"], str(scratch / "profile"))
            self.assertEqual(isolated["LOCALMIND_TOKEN"], "fresh-token")
            self.assertEqual(isolated["LOCALMIND_PORT"], "12345")
            self.assertEqual(isolated["HOME"], str(scratch / "home"))
            self.assertEqual(isolated["USERPROFILE"], str(scratch / "home"))
            self.assertTrue(Path(isolated["APPDATA"]).is_relative_to(scratch))
            self.assertTrue(Path(isolated["TEMP"]).is_relative_to(scratch))
            self.assertTrue(Path(isolated["TMPDIR"]).is_relative_to(scratch))
            self.assertNotIn("GOOGLE_CLIENT_ID", isolated)
            self.assertNotIn("GOOGLE_CLIENT_SECRET", isolated)
            self.assertNotIn("PYTHONPATH", isolated)
            self.assertEqual(isolated["PATH"], "/system-bin")
            self.assertEqual(os.environ["LOCALMIND_TOKEN"], "real-token")


if __name__ == "__main__":
    unittest.main()
