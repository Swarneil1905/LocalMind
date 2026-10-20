"""Fetch a pinned public conversation shard for local evaluation preparation.

No dataset code is executed. The shard checksum comes from its pinned Hub LFS
metadata. Imported conversations and experiment outputs belong under artifacts/.
"""

import argparse
import hashlib
import json
from pathlib import Path
import urllib.request

REVISION = "5feaf2fd3ffca7c237fc38d1861bc30365d48ffa"
REPOSITORY = "HuggingFaceTB/smoltalk"
SHARD = "data/smol-magpie-ultra/train-00000-of-00006.parquet"
SHARD_BYTES = 232402904
SHARD_SHA256 = "512cb90bf3c8a6772e97c43977d10177b2726118a107eaf8de4797a604e09df8"


def download(url: str, destination: Path, limit: int, expected_hash: str | None = None,
             expected_size: int | None = None) -> dict:
    digest = hashlib.sha256()
    size = 0
    # Exclusive creation keeps previous evidence intact, including failed attempts.
    with destination.open("xb") as output:
        with urllib.request.urlopen(url, timeout=60) as response:
            while chunk := response.read(1024 * 1024):
                size += len(chunk)
                if size > limit:
                    raise ValueError(f"Download exceeded the limit for {destination.name}")
                digest.update(chunk)
                output.write(chunk)
    actual_hash = digest.hexdigest()
    if expected_size is not None and size != expected_size:
        raise ValueError("Downloaded shard size does not match the pinned metadata")
    if expected_hash is not None and actual_hash != expected_hash:
        raise ValueError("Downloaded shard checksum does not match the pinned metadata")
    return {"file": destination.name, "url": url, "bytes": size, "sha256": actual_hash}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    base = f"https://huggingface.co/datasets/{REPOSITORY}/resolve/{REVISION}"
    files = [
        download(f"{base}/README.md", args.out / "SOURCE-README.md", 1024 * 1024),
        download("https://www.apache.org/licenses/LICENSE-2.0.txt",
                 args.out / "LICENSE-2.0.txt", 1024 * 1024),
        download(f"{base}/{SHARD}", args.out / Path(SHARD).name, SHARD_BYTES,
                 SHARD_SHA256, SHARD_BYTES),
    ]
    manifest = {
        "dataset": REPOSITORY, "revision": REVISION, "subset": "smol-magpie-ultra",
        "source_split": "train", "license": "Apache-2.0",
        "license_scope": "Only this newly generated subset, not the full SmolTalk mixture.",
        "source_kind": "synthetic", "files": files,
        "sampling_scope": "One of six source train shards; not a representative full-dataset sample.",
    }
    (args.out / "download.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "downloaded_and_verified", "bytes": sum(f["bytes"] for f in files)}))


if __name__ == "__main__":
    main()
