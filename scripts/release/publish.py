"""Upload a complete draft, verify its assets, then make it the latest release."""

import argparse
import hashlib
import json
import os
import re
import subprocess
import time
from pathlib import Path
from urllib.parse import urlencode


class GitHubError(RuntimeError):
    def __init__(self, message):
        super().__init__(message)
        match = re.search(r"\(HTTP (\d{3})\)", message)
        self.status = int(match[1]) if match else None


def gh(*args, allow_missing=False):
    result = subprocess.run(["gh", *args], capture_output=True, text=True, check=False)
    if result.returncode:
        error = GitHubError(result.stderr.strip())
        if allow_missing and error.status == 404:
            return None
        raise error
    return result.stdout


def checksum(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_api(endpoint, *, paginated=False):
    # Retry reads only. Replaying a POST after a lost response can create duplicates.
    for attempt in range(3):
        try:
            options = ("--paginate", "--slurp") if paginated else ()
            return json.loads(gh("api", endpoint, *options))
        except GitHubError as error:
            if error.status not in {404, 500, 502, 503, 504} or attempt == 2:
                raise
            time.sleep(2**attempt)


def list_api(endpoint):
    pages = read_api(f"{endpoint}?per_page=100", paginated=True)
    if not isinstance(pages, list) or any(not isinstance(page, list) for page in pages):
        raise RuntimeError("GitHub returned an invalid listing")
    return [item for page in pages for item in page]


def find_release(api, tag):
    matches = [r for r in list_api(f"{api}/releases") if r.get("tag_name") == tag]
    if len(matches) > 1:
        raise SystemExit("Multiple releases have this tag; refusing an ambiguous publication")
    return matches[0] if matches else None


def require_identity(release, tag, release_id=None):
    if type(release.get("id")) is not int or release["id"] <= 0:
        raise RuntimeError("GitHub returned an invalid release ID")
    if release.get("tag_name") != tag or (release_id is not None and release["id"] != release_id):
        raise RuntimeError("GitHub returned a different release")


def require_matching_draft(release, sha, tag, release_id=None):
    require_identity(release, tag, release_id)
    if release.get("draft") is not True:
        raise SystemExit("This release is already public; refusing to replace it")
    if release.get("target_commitish") != sha:
        raise SystemExit(
            f"Draft {release['id']} belongs to another commit; inspect it in GitHub "
            "Releases and remove the obsolete draft before retrying. Do not delete a public release."
        )


def asset_map(api, release_id):
    assets = list_api(f"{api}/releases/{release_id}/assets")
    result = {asset["name"]: asset for asset in assets}
    if len(result) != len(assets):
        raise SystemExit("Duplicate draft asset names")
    return result


def asset_matches(asset, path, digest):
    return (
        asset.get("state") == "uploaded"
        and asset.get("size") == path.stat().st_size
        and asset.get("digest") == f"sha256:{digest}"
    )


def prepare_draft(api, tag, sha, notes_path):
    notes = notes_path.read_text(encoding="utf-8").strip()
    release = find_release(api, tag)
    if release is None:
        # Keep the actual creation response. A listing immediately after creation
        # need not discover the draft, and tag-based uploads repeat that lookup.
        try:
            release = json.loads(
                gh(
                    "api",
                    "--method",
                    "POST",
                    f"{api}/releases",
                    "-f",
                    f"tag_name={tag}",
                    "-f",
                    f"target_commitish={sha}",
                    "-f",
                    f"name=LocalMind {tag}",
                    "-F",
                    "draft=true",
                    "-F",
                    f"body=@{notes_path}",
                )
            )
        except (GitHubError, json.JSONDecodeError) as error:
            raise RuntimeError(
                "Draft creation did not return a usable response. It may have succeeded; "
                "inspect GitHub Releases before rerunning. No second draft was created by this run."
            ) from error
    else:
        require_identity(release, tag)
        release_id = release["id"]
        release = read_api(f"{api}/releases/{release_id}")
        require_identity(release, tag, release_id)
        # The old publisher could leave an empty bot draft before uploading anything.
        # Only that recognizable empty draft may be reused after a code fix.
        if (
            release.get("draft") is True
            and release.get("target_commitish") != sha
            and re.fullmatch(r"[0-9a-f]{40}", release.get("target_commitish", ""))
            and release.get("author", {}).get("login") == "github-actions[bot]"
            and release.get("name") == f"LocalMind {tag}"
            and (release.get("body") or "").strip() == notes
            and not asset_map(api, release_id)
        ):
            release = json.loads(
                gh(
                    "api",
                    "--method",
                    "PATCH",
                    f"{api}/releases/{release_id}",
                    "-f",
                    f"target_commitish={sha}",
                )
            )
            require_identity(release, tag, release_id)
            print(f"Reusing empty workflow draft {release_id} for tested commit {sha}", flush=True)
    require_matching_draft(release, sha, tag)
    print(f"Using draft {release['id']} for {tag} at {sha}", flush=True)
    return release


def check_tag(api, tag, sha):
    # Never move a pre-existing tag to make a release appear to contain new code.
    ref = gh("api", f"{api}/git/ref/tags/{tag}", allow_missing=True)
    if ref:
        obj = json.loads(ref)["object"]
        while obj["type"] == "tag":
            obj = read_api(f"{api}/git/tags/{obj['sha']}")["object"]
        if obj["type"] != "commit" or obj["sha"] != sha:
            raise SystemExit("The existing tag does not match the tested commit")


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--assets", type=Path, required=True)
    args = parser.parse_args(argv)
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        raise SystemExit("Invalid release version")
    repo, sha = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_SHA"]
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", repo) or not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise SystemExit("Invalid repository or commit")
    tag = f"v{args.version}"
    api = f"repos/{repo}"
    files = sorted(args.assets.iterdir())
    if not files or any(not p.is_file() or p.is_symlink() for p in files):
        raise SystemExit("Release assets must be regular files")
    if not {"latest.json", "SHA256SUMS.txt"}.issubset({p.name for p in files}):
        raise SystemExit("Updater manifest or checksums are missing")
    sums = {}
    for line in (args.assets / "SHA256SUMS.txt").read_text().splitlines():
        digest, name = line.split("  ", 1)
        if name in sums:
            raise SystemExit("Duplicate checksum entry")
        sums[name] = digest
    if set(sums) != {p.name for p in files if p.name != "SHA256SUMS.txt"}:
        raise SystemExit("Checksum file does not cover exactly the release files")
    digests = {path.name: checksum(path) for path in files}
    for path in files:
        if path.name in sums and digests[path.name] != sums[path.name]:
            raise SystemExit(f"Checksum mismatch: {path.name}")

    check_tag(api, tag, sha)
    release = prepare_draft(api, tag, sha, Path(f"docs/releases/{tag}.md"))
    release_id = release["id"]
    endpoint = f"{api}/releases/{release_id}"
    upload_url = release.get("upload_url", "").split("{", 1)[0]
    if upload_url != f"https://uploads.github.com/{endpoint}/assets":
        raise RuntimeError("GitHub returned an unexpected upload URL")
    uploaded = asset_map(api, release_id)
    if set(uploaded) - {p.name for p in files}:
        raise SystemExit("The draft contains unexpected assets; inspect it before retrying")

    for path in files:
        asset = uploaded.get(path.name)
        if asset and asset_matches(asset, path, digests[path.name]):
            print(f"Already verified {path.name}", flush=True)
            continue
        require_matching_draft(read_api(endpoint), sha, tag, release_id)
        if asset:
            asset_id = asset.get("id")
            if type(asset_id) is not int or asset_id <= 0:
                raise RuntimeError("GitHub returned an invalid asset ID")
            gh("api", "--method", "DELETE", f"{api}/releases/assets/{asset_id}")
        # --input streams the file; never load multi-GB installers into memory.
        # A failed/lost upload response leaves a resumable draft. Do not replay POST.
        response = json.loads(
            gh(
                "api",
                "--method",
                "POST",
                f"{upload_url}?{urlencode({'name': path.name})}",
                "-H",
                "Content-Type: application/octet-stream",
                "-H",
                f"Content-Length: {path.stat().st_size}",
                "--input",
                str(path),
            )
        )
        if response.get("name") != path.name or not asset_matches(
            response, path, digests[path.name]
        ):
            raise SystemExit(f"GitHub upload checksum mismatch or incomplete upload: {path.name}")
        print(f"Uploaded {path.name}", flush=True)
    require_matching_draft(read_api(endpoint), sha, tag, release_id)
    uploaded = asset_map(api, release_id)
    if set(uploaded) != {p.name for p in files}:
        raise SystemExit("The draft asset list differs from the verified build")
    for path in files:
        asset = uploaded[path.name]
        if asset["state"] != "uploaded" or asset["size"] != path.stat().st_size:
            raise SystemExit(f"Incomplete upload: {path.name}")
        if asset.get("digest") != f"sha256:{digests[path.name]}":
            raise SystemExit(f"GitHub upload checksum mismatch: {path.name}")
    check_tag(api, tag, sha)
    result = json.loads(
        gh(
            "api",
            "--method",
            "PATCH",
            f"{api}/releases/{release_id}",
            "-F",
            "draft=false",
            "-f",
            "make_latest=true",
        )
    )
    require_identity(result, tag, release_id)
    if result.get("draft") is not False:
        raise RuntimeError("GitHub did not confirm publication; inspect the existing release")
    print(f"Published https://github.com/{repo}/releases/tag/{tag}")


if __name__ == "__main__":
    main()
