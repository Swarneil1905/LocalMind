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

    def __init__(self, *, exists=False, draft=True, commit=SHA, bad_digest=False, placeholder=False):
        self.calls = []
        self.exists = exists
        self.bad_digest = bad_digest
        self.published = False
        self.placeholder = placeholder
        self.pending_tag = None
        self.release = {
            "id": RELEASE_ID,
            "tag_name": "untagged-abc123" if placeholder else TAG,
            "draft": draft,
            "target_commitish": commit,
            "assets": [],
            "upload_url": f"https://uploads.github.com/{API}/releases/{RELEASE_ID}/assets{{?name,label}}",
        }
        if placeholder:
            self.release.update({
                "name": f"LocalMind {TAG}",
                "author": {"login": "github-actions[bot]"},
                "body": Path(f"docs/releases/{TAG}.md").read_text(encoding="utf-8"),
            })

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
        if args[:4] == ("api", "--method", "POST", f"{API}/releases"):
            if self.exists:
                raise AssertionError("The existing pending-tag draft must be resumed")
            self.exists = True
            assert "draft=true" in args
            assert f"tag_name={TAG}" in args
            assert f"target_commitish={SHA}" in args
            return json.dumps(self.release)
        if "--input" in args:
            path = Path(args[args.index("--input") + 1])
            assert args[:3] == ("api", "--method", "POST")
            assert args[3].startswith(self.release["upload_url"].split("{")[0] + "?name=")
            assert f"Content-Length: {path.stat().st_size}" in args
            assert "Content-Type: application/octet-stream" in args
            digest = "0" * 64 if self.bad_digest else publish.checksum(path)
            self.release["assets"] = [
                asset for asset in self.release["assets"] if asset["name"] != path.name
            ]
            self.release["assets"].append(
                {
                    "id": 1000 + len(self.calls),
                    "name": path.name,
                    "state": "uploaded",
                    "size": path.stat().st_size,
                    "digest": f"sha256:{digest}",
                }
            )
            return json.dumps(self.release["assets"][-1])
        if args == (
            "api",
            f"{API}/releases/{RELEASE_ID}/assets?per_page=100",
            "--paginate",
            "--slurp",
        ):
            return json.dumps([[], self.release["assets"]])
        if args[:3] == ("api", "--method", "DELETE"):
            asset_id = int(args[3].rsplit("/", 1)[1])
            assert any(a["id"] == asset_id for a in self.release["assets"])
            self.release["assets"] = [a for a in self.release["assets"] if a["id"] != asset_id]
            return ""
        if args == (
            "api",
            "--method",
            "PATCH",
            f"{API}/releases/{RELEASE_ID}",
            "-f",
            f"target_commitish={SHA}",
            "-f",
            f"tag_name={TAG}",
        ):
            self.release["target_commitish"] = SHA
            return json.dumps(self.release)
        if args == ("api", "--method", "PATCH", f"{API}/releases/{RELEASE_ID}", "-f", f"tag_name={TAG}"):
            self.pending_tag = TAG
            # Model GitHub continuing to expose the temporary name on a draft.
            return json.dumps(self.release)
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
            if self.placeholder and self.pending_tag:
                self.release["tag_name"] = self.pending_tag
            return json.dumps(self.release)
        raise AssertionError(f"Unexpected GitHub call: {args}")


class PublishTests(unittest.TestCase):
    def test_publication_preflight_needs_no_assets_and_never_writes(self):
        github = FakeGitHub()
        with patch.object(publish, "gh", side_effect=github), patch.dict(
            os.environ, {"GITHUB_REPOSITORY": REPOSITORY, "GITHUB_SHA": SHA}
        ), redirect_stdout(io.StringIO()):
            publish.main(["--version", VERSION, "--preflight"])
        self.assertFalse(any("--method" in c for c in github.calls))

    def test_preflight_stops_when_existing_tag_points_to_older_code(self):
        with patch.object(publish, "gh", return_value=json.dumps(
            {"object": {"type": "commit", "sha": "b" * 40}}
        )) as call, patch.dict(
            os.environ, {"GITHUB_REPOSITORY": REPOSITORY, "GITHUB_SHA": SHA}
        ), self.assertRaisesRegex(SystemExit, "tag does not match"):
            publish.main(["--version", VERSION, "--preflight"])
        self.assertEqual(call.call_count, 1)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="localmind-publish-test-")
        self.addCleanup(self.temporary.cleanup)
        self.assets = Path(self.temporary.name)
        (self.assets / "LocalMind_0.7.0_x64-setup.exe").write_bytes(b"test installer")
        (self.assets / "latest.json").write_text(json.dumps({"version": VERSION}))
        sums = "".join(
            f"{publish.checksum(path)}  {path.name}\n" for path in sorted(self.assets.iterdir())
        )
        (self.assets / "SHA256SUMS.txt").write_text(sums, encoding="utf-8")

    def run_publication(self, github):
        with (
            patch.object(publish, "gh", side_effect=github),
            patch.dict(os.environ, {"GITHUB_REPOSITORY": REPOSITORY, "GITHUB_SHA": SHA}),
            redirect_stdout(io.StringIO()),
        ):
            publish.main(["--version", VERSION, "--assets", str(self.assets)])

    def test_new_pending_tag_draft_is_verified_by_id_before_publication(self):
        github = FakeGitHub()
        self.run_publication(github)
        self.assertTrue(github.published)
        self.assertEqual(
            sum(
                call[:4] == ("api", "--method", "POST", f"{API}/releases") for call in github.calls
            ),
            1,
        )
        self.assertIn(("api", f"{API}/releases/{RELEASE_ID}"), github.calls)
        self.assertFalse(any("/releases/tags/" in arg for call in github.calls for arg in call))
        verified_index = github.calls.index(("api", f"{API}/releases/{RELEASE_ID}"))
        published_index = next(i for i, call in enumerate(github.calls) if "PATCH" in call)
        self.assertLess(verified_index, published_index)

    def test_temporary_draft_tag_is_bound_before_publication_and_resumes_by_id(self):
        for exists in (False, True):
            with self.subTest(existing=exists):
                github = FakeGitHub(exists=exists, placeholder=True)
                self.run_publication(github)
                self.assertTrue(github.published)
                self.assertEqual(github.release["tag_name"], TAG)
                self.assertEqual(sum(c[:4] == ("api", "--method", "POST", f"{API}/releases")
                                     for c in github.calls), 0 if exists else 1)

    def test_unrecognized_placeholder_draft_is_not_adopted_or_duplicated(self):
        for update in ({"body": "someone else's notes"}, {"author": {"login": "someone"}}):
            with self.subTest(update=update):
                github = FakeGitHub(exists=True, placeholder=True)
                github.release.update(update)
                with self.assertRaisesRegex(SystemExit, "unrecognized draft"):
                    self.run_publication(github)
                self.assertFalse(any("--method" in c for c in github.calls))

    def test_resume_existing_draft_found_on_later_page_without_recreating_it(self):
        github = FakeGitHub(exists=True)
        self.run_publication(github)
        self.assertTrue(github.published)
        self.assertFalse(
            any(call[:4] == ("api", "--method", "POST", f"{API}/releases") for call in github.calls)
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
        self.assertFalse(any("--method" in call for call in github.calls))

    def test_draft_for_another_commit_is_not_resumed(self):
        github = FakeGitHub(exists=True, commit="b" * 40)
        with self.assertRaisesRegex(SystemExit, "another commit"):
            self.run_publication(github)
        self.assertFalse(any("--method" in call for call in github.calls))

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

    def test_creation_response_is_used_when_listings_never_show_new_draft(self):
        github = FakeGitHub()

        def stale_listing(*args, **kwargs):
            if args == ("api", f"{API}/releases?per_page=100", "--paginate", "--slurp"):
                github.calls.append(args)
                return "[[]]"
            return github(*args, **kwargs)

        self.run_publication(stale_listing)
        self.assertTrue(github.published)
        self.assertEqual(sum(f"{API}/releases?per_page=100" in c for c in github.calls), 1)
        self.assertFalse(any(c[0] == "release" for c in github.calls))

    def test_lost_create_response_is_not_retried(self):
        github = FakeGitHub()

        def lost_response(*args, **kwargs):
            result = github(*args, **kwargs)
            if args[:4] == ("api", "--method", "POST", f"{API}/releases"):
                raise publish.GitHubError("connection closed")
            return result

        with self.assertRaisesRegex(RuntimeError, "may have succeeded"):
            self.run_publication(lost_response)
        self.assertTrue(github.exists)
        self.assertFalse(github.published)
        self.assertEqual(sum("POST" in c for c in github.calls), 1)
        self.run_publication(github)
        self.assertTrue(github.published)

    def test_lost_upload_response_resumes_without_uploading_verified_file_again(self):
        github = FakeGitHub()

        def lost_response(*args, **kwargs):
            result = github(*args, **kwargs)
            if "--input" in args:
                raise publish.GitHubError("connection closed")
            return result

        with self.assertRaisesRegex(RuntimeError, "connection closed"):
            self.run_publication(lost_response)
        self.assertFalse(github.published)
        self.run_publication(github)
        self.assertTrue(github.published)
        self.assertEqual(sum("--input" in c for c in github.calls), 3)

    def test_starter_and_wrong_digest_assets_are_replaced_before_retry(self):
        for state, digest in [("starter", None), ("uploaded", "sha256:wrong")]:
            with self.subTest(state=state):
                github = FakeGitHub(exists=True)
                path = next(p for p in self.assets.iterdir() if p.suffix == ".exe")
                github.release["assets"] = [
                    {
                        "id": 55,
                        "name": path.name,
                        "state": state,
                        "size": path.stat().st_size,
                        "digest": digest,
                    }
                ]
                self.run_publication(github)
                self.assertTrue(github.published)
                self.assertIn(
                    ("api", "--method", "DELETE", f"{API}/releases/assets/55"), github.calls
                )

    def test_unexpected_assets_are_not_deleted(self):
        github = FakeGitHub(exists=True)
        github.release["assets"] = [{"id": 55, "name": "unrelated.txt"}]
        with self.assertRaisesRegex(SystemExit, "unexpected assets"):
            self.run_publication(github)
        self.assertFalse(any("--method" in c for c in github.calls))

    def old_workflow_draft(self):
        github = FakeGitHub(exists=True, commit="b" * 40)
        github.release.update(
            {
                "author": {"login": "github-actions[bot]"},
                "name": f"LocalMind {TAG}",
                "body": Path(f"docs/releases/{TAG}.md").read_text(encoding="utf-8"),
            }
        )
        return github

    def test_empty_old_workflow_draft_can_be_reused_after_fix(self):
        github = self.old_workflow_draft()
        self.run_publication(github)
        self.assertTrue(github.published)
        self.assertEqual(github.release["target_commitish"], SHA)
        self.assertFalse(
            any(c[:4] == ("api", "--method", "POST", f"{API}/releases") for c in github.calls)
        )

    def test_old_draft_with_assets_or_custom_metadata_is_not_retargeted(self):
        for update in [
            {"assets": [{"id": 55, "name": "old.exe"}]},
            {"author": {"login": "a-person"}},
            {"body": "custom notes"},
            {"name": "custom title"},
            {"draft": False},
        ]:
            with self.subTest(update=update):
                github = self.old_workflow_draft()
                github.release.update(update)
                with self.assertRaises(SystemExit):
                    self.run_publication(github)
                self.assertFalse(any("--method" in c for c in github.calls))

    def test_invalid_creation_identity_or_upload_url_stops_before_upload(self):
        for update in [
            {"id": True},
            {"tag_name": "v9.9.9"},
            {"upload_url": "https://elsewhere.example/upload"},
        ]:
            with self.subTest(update=update):
                github = FakeGitHub()
                github.release.update(update)
                with self.assertRaises(RuntimeError):
                    self.run_publication(github)
                self.assertFalse(any("--input" in c for c in github.calls))

    def test_tag_moved_during_upload_prevents_publication(self):
        github = FakeGitHub()
        checks = 0

        def changed_tag(*args, **kwargs):
            nonlocal checks
            if args == ("api", f"{API}/git/ref/tags/{TAG}"):
                checks += 1
                return json.dumps(
                    {"object": {"type": "commit", "sha": SHA if checks == 1 else "b" * 40}}
                )
            return github(*args, **kwargs)

        with self.assertRaisesRegex(SystemExit, "tag does not match"):
            self.run_publication(changed_tag)
        self.assertFalse(github.published)

    def test_corruption_discovered_in_final_asset_listing_prevents_publication(self):
        github = FakeGitHub()

        def corrupted(*args, **kwargs):
            if "/assets?per_page=100" in args[1] and len(github.release["assets"]) == 3:
                github.release["assets"][0]["digest"] = "sha256:wrong"
            return github(*args, **kwargs)

        with self.assertRaisesRegex(SystemExit, "upload checksum mismatch"):
            self.run_publication(corrupted)
        self.assertFalse(github.published)


class ReadApiTests(unittest.TestCase):
    def test_temporary_read_failure_is_retried_with_a_bound(self):
        for status in (404, 502):
            with self.subTest(status=status), patch.object(publish.time, "sleep"):
                with patch.object(
                    publish,
                    "gh",
                    side_effect=[publish.GitHubError(f"not ready (HTTP {status})"), '{"id":123}'],
                ) as call:
                    self.assertEqual(publish.read_api("example"), {"id": 123})
                    self.assertEqual(call.call_count, 2)
                with patch.object(
                    publish, "gh", side_effect=publish.GitHubError(f"failed (HTTP {status})")
                ) as call:
                    with self.assertRaises(publish.GitHubError):
                        publish.read_api("example")
                    self.assertEqual(call.call_count, 3)

    def test_permission_errors_are_not_retried(self):
        for status in (401, 403):
            with (
                self.subTest(status=status),
                patch.object(
                    publish, "gh", side_effect=publish.GitHubError(f"denied (HTTP {status})")
                ) as call,
            ):
                with self.assertRaises(publish.GitHubError):
                    publish.read_api("example")
                self.assertEqual(call.call_count, 1)

    def test_only_http_404_is_treated_as_missing(self):
        import subprocess

        for message, missing in [
            ("gh: Not Found (HTTP 404)", True),
            ("proxy port 404 unreachable", False),
        ]:
            with (
                self.subTest(message=message),
                patch.object(
                    publish.subprocess,
                    "run",
                    return_value=subprocess.CompletedProcess([], 1, "", message),
                ),
            ):
                if missing:
                    self.assertIsNone(publish.gh("api", "example", allow_missing=True))
                else:
                    with self.assertRaises(publish.GitHubError):
                        publish.gh("api", "example", allow_missing=True)


if __name__ == "__main__":
    unittest.main()
