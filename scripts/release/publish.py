"""Upload a complete draft, verify its assets, then make it the latest release."""

import argparse
import hashlib
import json
import os
import re
import subprocess
from pathlib import Path


def gh(*args, allow_missing=False):
    result = subprocess.run(["gh", *args], capture_output=True, text=True, check=False)
    if result.returncode:
        if allow_missing and "404" in result.stderr:
            return None
        raise RuntimeError(result.stderr.strip())
    return result.stdout


def checksum(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def find_release(api, tag):
    # The tag endpoint only discovers published releases. Authenticated release
    # listings also include drafts whose tag does not exist yet.
    pages = json.loads(
        gh("api", f"{api}/releases?per_page=100", "--paginate", "--slurp")
    )
    if not isinstance(pages, list) or any(not isinstance(page, list) for page in pages):
        raise RuntimeError("GitHub returned an invalid release listing")
    matches = [
        release for page in pages for release in page if release.get("tag_name") == tag
    ]
    if len(matches) > 1:
        raise SystemExit(
            "Multiple releases have this tag; refusing an ambiguous publication"
        )
    return matches[0] if matches else None


def require_matching_draft(release, sha):
    if not release["draft"]:
        raise SystemExit("This release is already public; refusing to replace it")
    if release["target_commitish"] != sha:
        raise SystemExit("The existing draft belongs to another commit")
    if type(release.get("id")) is not int or release["id"] <= 0:
        raise RuntimeError("GitHub returned an invalid release ID")


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--assets", type=Path, required=True)
    args = parser.parse_args(argv)
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        raise SystemExit("Invalid release version")
    repo, sha = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_SHA"]
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", repo) or not re.fullmatch(
        r"[0-9a-f]{40}", sha
    ):
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
    for path in files:
        if path.name in sums and checksum(path) != sums[path.name]:
            raise SystemExit(f"Checksum mismatch: {path.name}")

    # Never move a pre-existing tag to make a release appear to contain new code.
    ref = gh("api", f"{api}/git/ref/tags/{tag}", allow_missing=True)
    if ref:
        obj = json.loads(ref)["object"]
        while obj["type"] == "tag":
            obj = json.loads(gh("api", f"{api}/git/tags/{obj['sha']}"))["object"]
        if obj["type"] != "commit" or obj["sha"] != sha:
            raise SystemExit("The existing tag does not match the tested commit")

    release = find_release(api, tag)
    if release is None:
        gh(
            "release",
            "create",
            tag,
            "--repo",
            repo,
            "--target",
            sha,
            "--draft",
            "--title",
            f"LocalMind {tag}",
            "--notes-file",
            f"docs/releases/{tag}.md",
        )
        release = find_release(api, tag)
        if release is None:
            raise RuntimeError(
                "The created draft could not be found; retry the same tested commit"
            )
    require_matching_draft(release, sha)
    release_id = release["id"]

    for path in files:
        gh("release", "upload", tag, str(path), "--repo", repo, "--clobber")
        print(f"Uploaded {path.name}", flush=True)
    release = json.loads(gh("api", f"{api}/releases/{release_id}"))
    require_matching_draft(release, sha)
    uploaded = {asset["name"]: asset for asset in release["assets"]}
    if len(uploaded) != len(release["assets"]) or set(uploaded) != {
        p.name for p in files
    }:
        raise SystemExit("The draft asset list differs from the verified build")
    for path in files:
        asset = uploaded[path.name]
        if asset["state"] != "uploaded" or asset["size"] != path.stat().st_size:
            raise SystemExit(f"Incomplete upload: {path.name}")
        if asset.get("digest") != f"sha256:{checksum(path)}":
            raise SystemExit(f"GitHub upload checksum mismatch: {path.name}")
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
    print(f"Published https://github.com/{repo}/releases/tag/{tag}")


if __name__ == "__main__":
    main()
