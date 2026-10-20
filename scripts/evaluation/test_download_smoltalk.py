import hashlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import download_smoltalk as downloader


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.target = Path(self.directory.name) / "shard"

    def test_records_verified_bytes_hash_and_source_url(self):
        content = b"small fixture"
        expected = hashlib.sha256(content).hexdigest()
        with patch.object(downloader.urllib.request, "urlopen", return_value=io.BytesIO(content)):
            result = downloader.download("https://example.invalid/pinned", self.target,
                                         len(content), expected, len(content))
        self.assertEqual(result["sha256"], expected)
        self.assertEqual(result["bytes"], len(content))
        self.assertEqual(self.target.read_bytes(), content)

    def test_wrong_size_hash_or_limit_cannot_succeed(self):
        for limit, digest, size in ((2, None, None), (100, "a" * 64, 3), (100, None, 4)):
            with self.subTest(limit=limit, size=size):
                self.target.unlink(missing_ok=True)
                with patch.object(downloader.urllib.request, "urlopen", return_value=io.BytesIO(b"abc")):
                    with self.assertRaises(ValueError):
                        downloader.download("https://example.invalid/pinned", self.target, limit, digest, size)

    def test_existing_evidence_is_not_overwritten_or_downloaded_again(self):
        self.target.write_bytes(b"preserve")
        with patch.object(downloader.urllib.request, "urlopen") as request:
            with self.assertRaises(FileExistsError):
                downloader.download("https://example.invalid/pinned", self.target, 100)
            request.assert_not_called()
        self.assertEqual(self.target.read_bytes(), b"preserve")


if __name__ == "__main__":
    unittest.main()
