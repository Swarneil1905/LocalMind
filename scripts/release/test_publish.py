"""Publication regressions; all GitHub calls are mocked and no release is created."""

from __future__ import annotations

import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

import publish

VERSION = "0.7.0"
TAG = f"v{VERSION}"
REPOSITORY = "Swarneil1905/LocalMind"
API = f"repos/{REPOSITORY}"
SHA = "a" * 40
RELEASE_ID = 123


class FakeGitHub:
    """Model a pending-tag draft: tag lookup must not be used to find it."""

    def __init__(self, *, exists=False, draft=True, commit=SHA, bad_digest=False):
        self.calls = []
        self.exists = exists
        self.bad_digest = bad_digest
        self.published = False
        self.release = {
            "id": RELEASE_ID,
            "tag_name": TAG,
            "draft": draft,
            "target_commitish": commit,
            "assets": [],
        }

    def __call__(self, *args, **kwargs):
        self.calls.append(args)
        if args == ("api", f"{API}/git/ref/tags/{TAG}"):
            # Draft release exists, but its tag will only be created at publish.
            assert kwargs.get("allow_missing")
            return None
        if args == ("api", f"{API}/releases?per_page=100", "--paginate", "--slurp"):
            # Include two pages so discovery cannot accidentally use only page 1.
            unrelated = {"id": 1, "tag_name": "v0.6.0", "draft": False}
            return json.dumps([[unrelated], [self.release] if self.exists else []])
        if args[:2] == ("release", "create"):
            if self.exists:
                raise AssertionError("The existing pending-tag draft must be resumed")
            self.exists = True
            return "https://github.com/example/release/draft\n"
        if args[:2] == ("release", "upload"):
            path = Path(args[3])
            digest = "0" * 64 if self.bad_digest else publish.checksum(path)
            self.release["assets"] = [
                asset for asset in self.release["assets"] if asset["name"] != path.name
            ]
            self.release["assets"].append(
                {
                    "name": path.name,
                    "state": "uploaded",
                    "size": path.stat().st_size,
                    "digest": f"sha256:{digest}",
                }
            )
            return ""
        if args == ("api", f"{API}/releases/{RELEASE_ID}"):
            return json.dumps(self.release)
        if args == (
            "api",
            "--method",
            "PATCH",
            f"{API}/releases/{RELEASE_ID}",
            "-F",
            "draft=false",
            "-f",
            "make_latest=true",
        ):
            self.published = True
            self.release["draft"] = False
            return json.dumps(self.release)
        raise AssertionError(f"Unexpected GitHub call: {args}")


class PublishTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="localmind-publish-test-")
        self.addCleanup(self.temporary.cleanup)
        self.assets = Path(self.temporary.name)
        (self.assets / "LocalMind_0.7.0_x64-setup.exe").write_bytes(b"test installer")
        (self.assets / "latest.json").write_text(json.dumps({"version": VERSION}))
        sums = "".join(
            f"{publish.checksum(path)}  {path.name}\n"
            for path in sorted(self.assets.iterdir())
        )
        (self.assets / "SHA256SUMS.txt").write_text(sums, encoding="utf-8")

    def run_publication(self, github):
        with (
            patch.object(publish, "gh", side_effect=github),
            patch.dict(
                os.environ, {"GITHUB_REPOSITORY": REPOSITORY, "GITHUB_SHA": SHA}
            ),
            redirect_stdout(io.StringIO()),
        ):
            publish.main(["--version", VERSION, "--assets", str(self.assets)])

    def test_new_pending_tag_draft_is_verified_by_id_before_publication(self):
        github = FakeGitHub()
        self.run_publication(github)
        self.assertTrue(github.published)
        self.assertEqual(
            sum(call[:2] == ("release", "create") for call in github.calls), 1
        )
        self.assertIn(("api", f"{API}/releases/{RELEASE_ID}"), github.calls)
        self.assertFalse(
            any("/releases/tags/" in arg for call in github.calls for arg in call)
        )
        verified_index = github.calls.index(("api", f"{API}/releases/{RELEASE_ID}"))
        published_index = next(
            i for i, call in enumerate(github.calls) if "PATCH" in call
        )
        self.assertLess(verified_index, published_index)

    def test_resume_existing_draft_found_on_later_page_without_recreating_it(self):
        github = FakeGitHub(exists=True)
        self.run_publication(github)
        self.assertTrue(github.published)
        self.assertFalse(
            any(call[:2] == ("release", "create") for call in github.calls)
        )
        self.assertEqual(len(github.release["assets"]), 3)

    def test_wrong_github_upload_digest_keeps_release_as_draft(self):
        github = FakeGitHub(exists=True, bad_digest=True)
        with self.assertRaisesRegex(SystemExit, "upload checksum mismatch"):
            self.run_publication(github)
        self.assertFalse(github.published)
        self.assertTrue(github.release["draft"])
        self.assertFalse(any("PATCH" in call for call in github.calls))

    def test_public_release_is_never_uploaded_to_or_replaced(self):
        github = FakeGitHub(exists=True, draft=False)
        with self.assertRaisesRegex(SystemExit, "already public"):
            self.run_publication(github)
        self.assertFalse(github.published)
        self.assertFalse(
            any(call[0] == "release" or "PATCH" in call for call in github.calls)
        )

    def test_draft_for_another_commit_is_not_resumed(self):
        github = FakeGitHub(exists=True, commit="b" * 40)
        with self.assertRaisesRegex(SystemExit, "another commit"):
            self.run_publication(github)
        self.assertFalse(
            any(call[0] == "release" or "PATCH" in call for call in github.calls)
        )

    def test_duplicate_pending_tag_drafts_are_rejected(self):
        pages = [[{"tag_name": TAG}], [{"tag_name": TAG}]]
        with (
            patch.object(publish, "gh", return_value=json.dumps(pages)),
            self.assertRaisesRegex(SystemExit, "Multiple releases"),
        ):
            publish.find_release(API, TAG)

    def test_release_listing_failure_is_not_treated_as_missing_release(self):
        github = FakeGitHub()

        def failing_listing(*args, **kwargs):
            if "--paginate" in args:
                raise RuntimeError("GitHub API permission denied")
            return github(*args, **kwargs)

        with self.assertRaisesRegex(RuntimeError, "permission denied"):
            self.run_publication(failing_listing)
        self.assertFalse(github.exists)
        self.assertFalse(github.published)


if __name__ == "__main__":
    unittest.main()
