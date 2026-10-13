"""Regression cases from the Intel runner's busy DMG detach failure."""

import json
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from build_macos import build_macos, detach_build_images


BUSY = 'hdiutil: couldn\'t eject "disk4" - Resource busy\nfailed to bundle project'


class MacBuildTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        config = self.root / "apps/desktop/src-tauri/tauri.conf.json"
        config.parent.mkdir(parents=True)
        config.write_text(json.dumps({"productName": "LocalMind", "version": "0.7.0"}))
        for name, value in (("ROOT", self.root), ("validate_target", lambda target: None)):
            patcher = patch(f"build_macos.{name}", value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_busy_image_cleanup_retries_bundling_without_recompiling(self):
        for target, arch in (("x86_64-apple-darwin", "x64"), ("aarch64-apple-darwin", "aarch64")):
            with self.subTest(target=target), \
                    patch("build_macos.run_tauri", side_effect=[(1, BUSY), (0, "done")]) as run, \
                    patch("build_macos.detach_build_images") as detach, \
                    patch("build_macos.time.sleep"), patch("build_macos.print"):
                build_macos(target)
                self.assertEqual([call.args[0][4] for call in run.call_args_list], ["build", "bundle"])
                detach.assert_called_once_with(self.root / "target" / target / "release/bundle",
                                               f"LocalMind_0.7.0_{arch}.dmg")

    def test_compile_and_signing_errors_are_not_retried(self):
        for error in ("error: compilation failed", "codesign: invalid identity", "hdiutil: create failed - no space left"):
            with self.subTest(error=error), patch("build_macos.run_tauri", return_value=(1, error)) as run, \
                    patch("build_macos.detach_build_images") as detach:
                with self.assertRaises(subprocess.CalledProcessError):
                    build_macos("x86_64-apple-darwin")
                self.assertEqual(run.call_count, 1)
                detach.assert_not_called()

    def test_busy_retry_is_bounded(self):
        with patch("build_macos.run_tauri", return_value=(1, BUSY)) as run, \
                patch("build_macos.detach_build_images") as detach, \
                patch("build_macos.time.sleep"), patch("build_macos.print"):
            with self.assertRaises(subprocess.CalledProcessError):
                build_macos("x86_64-apple-darwin")
            self.assertEqual(run.call_count, 3)
            self.assertEqual(detach.call_count, 2)

    def test_success_never_queries_or_detaches_images(self):
        with patch("build_macos.run_tauri", return_value=(0, "done")), \
                patch("build_macos.detach_build_images") as detach:
            build_macos("x86_64-apple-darwin")
            detach.assert_not_called()

    def test_detach_is_limited_to_the_exact_target_version_and_whole_device(self):
        bundle = self.root / "target/x86_64-apple-darwin/release/bundle"
        filename = "LocalMind_0.7.0_x64.dmg"
        (bundle / "macos").mkdir(parents=True)
        intermediate = bundle / "macos" / f"rw.22104.{filename}"
        intermediate.write_bytes(b"aborted image")
        unrelated = bundle / "macos/rw.2.LocalMind_0.6.0_x64.dmg"
        unrelated.write_bytes(b"another version")

        def image(path, device):
            return {"image-path": str(path), "system-entities": [{"dev-entry": device}, {"dev-entry": device + "s1"}]}

        images = [
            image(bundle / "macos" / f"rw.22104.{filename}", "/dev/disk4"),
            image(self.root / f"rw.22104.{filename}", "/dev/disk5"),
            image(bundle / "macos/rw.2.LocalMind_0.6.0_x64.dmg", "/dev/disk6"),
            image(bundle / "macos/rw.2.LocalMind_0.7.0_aarch64.dmg", "/dev/disk7"),
            image(bundle / "macos" / filename, "/dev/disk8"),
            image(bundle / "macos" / f"rw.3.{filename}", "/dev/disk9; other-command"),
        ]
        with patch("build_macos.subprocess.run", return_value=subprocess.CompletedProcess([], 0, plistlib.dumps({"images": images}))) as run, \
                patch("build_macos.print"):
            detach_build_images(bundle, filename)
            self.assertEqual([call.args[0] for call in run.call_args_list], [
                ["hdiutil", "info", "-plist"], ["hdiutil", "detach", "/dev/disk4", "-force"],
            ])
            self.assertFalse(intermediate.exists())
            self.assertTrue(unrelated.exists())

    def test_failed_detach_stops_before_another_bundle_attempt(self):
        with patch("build_macos.run_tauri", return_value=(1, BUSY)) as run, \
                patch("build_macos.detach_build_images", side_effect=subprocess.CalledProcessError(1, ["hdiutil"])):
            with self.assertRaises(subprocess.CalledProcessError):
                build_macos("x86_64-apple-darwin")
            self.assertEqual(run.call_count, 1)


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("hdiutil"), "Native macOS hdiutil required")
class NativeMacImageTests(unittest.TestCase):
    def test_detaches_and_removes_only_the_owned_temporary_image(self):
        with tempfile.TemporaryDirectory(prefix="localmind-dmg-test-") as temporary:
            root = Path(temporary)
            bundle = root / "bundle"
            (bundle / "macos").mkdir(parents=True)
            installer = "LocalMind_0.7.0_x64.dmg"
            image = bundle / "macos" / f"rw.123.{installer}"
            mount = root / "mount"
            mount.mkdir()
            subprocess.run(["hdiutil", "create", "-size", "16m", "-fs", "HFS+", "-format", "UDRW",
                            "-volname", "LocalMindFixture", str(image)], check=True, capture_output=True)
            attached = False
            try:
                subprocess.run(["hdiutil", "attach", "-nobrowse", "-noautoopen", "-mountpoint", str(mount),
                                str(image)], check=True, capture_output=True)
                attached = True
                detach_build_images(bundle, installer)
                attached = False
                self.assertFalse(image.exists())
            finally:
                if attached:
                    subprocess.run(["hdiutil", "detach", str(mount), "-force"], capture_output=True, check=False)


if __name__ == "__main__":
    unittest.main()
