#!/usr/bin/env python3
"""Collect Tauri bundles and assemble a fail-closed, signed updater release.

Collection validates the structure of Tauri's base64-encoded minisign signatures.
Assembly additionally verifies *every* updater cryptographically with minisign
and the configured public key. No private key is read by this module.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from contextlib import contextmanager
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import quote

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
PUBLIC_KEY_FILE = REPOSITORY_ROOT / "apps/desktop/src-tauri/tauri.conf.json"
MAX_ASSET_BYTES = 2 * 1024**3
TARGETS = {
    "aarch64-apple-darwin": "darwin-aarch64",
    "x86_64-apple-darwin": "darwin-x86_64",
    "x86_64-pc-windows-msvc": "windows-x86_64",
    "x86_64-unknown-linux-gnu": "linux-x86_64",
}
PLATFORMS = frozenset(TARGETS.values())
UPDATER_SUFFIXES = (".nsis.zip", ".msi.zip", ".app.tar.gz", ".AppImage.tar.gz")
INSTALLER_SUFFIXES = (".dmg", ".exe", ".msi", ".deb", ".AppImage", ".rpm")
VERSION_RE = re.compile(
    r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\Z"
)


class ArtifactError(ValueError):
    """The input cannot safely be published as a complete release."""


def validate_version(version: str) -> str:
    if len(version) > 128 or not VERSION_RE.fullmatch(version):
        raise ArtifactError("Version must be a semantic version without a v prefix.")
    prerelease = version.split("+", 1)[0].partition("-")[2]
    if any(
        item.isdigit() and len(item) > 1 and item.startswith("0")
        for item in prerelease.split(".")
    ):
        raise ArtifactError(
            "Numeric prerelease identifiers cannot have leading zeroes."
        )
    return version


def validate_repository(repository: str) -> str:
    if not re.fullmatch(
        r"[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9_.-]*", repository
    ):
        raise ArtifactError("Repository must be an owner/name pair.")
    return repository


def validate_filename(name: object) -> str:
    if not isinstance(name, str) or not re.fullmatch(
        r"[A-Za-z0-9][A-Za-z0-9._+-]*", name
    ):
        raise ArtifactError(
            "Artifact filenames must be plain safe filenames, without paths."
        )
    if name in {"latest.json", "SHA256SUMS.txt"} or name.startswith("manifest-"):
        raise ArtifactError("Artifact filename is reserved for release metadata.")
    return name


def asset_kind(name: str) -> str | None:
    if name.endswith(tuple(suffix + ".sig" for suffix in UPDATER_SUFFIXES)):
        return "signature"
    if name.endswith(UPDATER_SUFFIXES):
        return "updater"
    if name.endswith(INSTALLER_SUFFIXES):
        return "installer"
    return None


def check_file(path: Path) -> None:
    if path.is_symlink() or not path.is_file():
        raise ArtifactError(f"Expected a regular artifact file: {path.name}")
    size = path.stat().st_size
    if size == 0:
        raise ArtifactError(f"Artifact is empty: {path.name}")
    if size >= MAX_ASSET_BYTES:
        raise ArtifactError(f"Artifact must be smaller than 2 GiB: {path.name}")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def decode_signature(signature: str) -> bytes:
    """Validate the envelope, not the cryptographic authenticity."""
    try:
        if len(signature) > 16384:
            raise ValueError
        decoded = base64.b64decode(signature, validate=True)
        lines = decoded.decode("ascii").splitlines()
        if (
            len(lines) != 4
            or not lines[0].startswith("untrusted comment:")
            or not lines[2].startswith("trusted comment:")
        ):
            raise ValueError
        packet = base64.b64decode(lines[1], validate=True)
        global_signature = base64.b64decode(lines[3], validate=True)
        if (
            len(packet) != 74
            or packet[:2] not in {b"Ed", b"ED"}
            or len(global_signature) != 64
        ):
            raise ValueError
    except (ValueError, UnicodeError, binascii.Error):
        raise ArtifactError("Invalid Tauri/minisign signature envelope.") from None
    return decoded


def read_signature(path: Path) -> str:
    check_file(path)
    if path.stat().st_size > 16384:
        raise ArtifactError(f"Signature is unexpectedly large: {path.name}")
    try:
        signature = path.read_text(encoding="ascii").strip()
    except UnicodeError:
        raise ArtifactError(f"Signature is not ASCII: {path.name}") from None
    decode_signature(signature)
    return signature


def read_public_key(path: Path) -> str:
    if not path.is_file() or path.stat().st_size > 1024 * 1024:
        raise ArtifactError("The public-key file is missing or unexpectedly large.")
    try:
        text = path.read_text(encoding="utf-8").strip()
        if path.suffix == ".json":
            text = json.loads(text)["plugins"]["updater"]["pubkey"].strip()
        if not text.startswith("untrusted comment:"):
            decoded = base64.b64decode(text, validate=True)
            if len(decoded) == 42:
                if decoded[:2] != b"Ed":
                    raise ValueError
                return text
            text = decoded.decode("ascii")
        lines = text.splitlines()
        if len(lines) != 2 or not lines[0].startswith("untrusted comment:"):
            raise ValueError
        key = base64.b64decode(lines[1], validate=True)
        if len(key) != 42 or key[:2] != b"Ed":
            raise ValueError
        return lines[1]
    except (
        ValueError,
        KeyError,
        TypeError,
        AttributeError,
        UnicodeError,
        binascii.Error,
    ):
        raise ArtifactError("The updater public key is invalid.") from None


def verify_signature(
    path: Path, signature: str, public_key: str, minisign: str = "minisign"
) -> None:
    executable = shutil.which(minisign)
    if executable is None:
        raise ArtifactError(
            "minisign is required to verify release assets; install it before assembly."
        )
    decoded = decode_signature(signature)
    with tempfile.TemporaryDirectory(prefix="localmind-signature-") as temp:
        signature_file = Path(temp) / "updater.minisig"
        signature_file.write_bytes(decoded)
        try:
            result = subprocess.run(
                [
                    executable,
                    "-V",
                    "-q",
                    "-m",
                    str(path.resolve()),
                    "-P",
                    public_key,
                    "-x",
                    str(signature_file),
                ],
                capture_output=True,
                check=False,
                timeout=300,
            )
        except (OSError, subprocess.TimeoutExpired):
            raise ArtifactError(
                f"Could not verify updater signature: {path.name}"
            ) from None
        if result.returncode:
            raise ArtifactError(f"Updater signature verification failed: {path.name}")


def validate_asset_set(platform: str, files: dict[str, Path], version: str) -> str:
    """Require installers, paired updater signatures, and no stale duplicates."""
    by_suffix = {
        suffix: [name for name in files if name.endswith(suffix)]
        for suffix in INSTALLER_SUFFIXES + UPDATER_SUFFIXES
    }
    if platform.startswith("darwin-"):
        required, optional = (".dmg", ".app.tar.gz"), ()
        selected_suffix = ".app.tar.gz"
    elif platform == "linux-x86_64":
        required, optional = (".deb", ".AppImage", ".AppImage.tar.gz"), (".rpm",)
        selected_suffix = ".AppImage.tar.gz"
    elif platform == "windows-x86_64":
        required, optional = (), (".exe", ".msi", ".nsis.zip", ".msi.zip")
        if not by_suffix[".nsis.zip"] and not by_suffix[".msi.zip"]:
            raise ArtifactError(
                "Windows requires a signed NSIS or MSI updater and its installer."
            )
        selected_suffix = ".nsis.zip" if by_suffix[".nsis.zip"] else ".msi.zip"
        for updater_suffix, installer_suffix in (
            (".nsis.zip", ".exe"),
            (".msi.zip", ".msi"),
        ):
            updaters, installers = (
                by_suffix[updater_suffix],
                by_suffix[installer_suffix],
            )
            if bool(updaters) != bool(installers):
                raise ArtifactError(
                    f"Windows {updater_suffix} and {installer_suffix} must both be present."
                )
            if (
                updaters
                and updaters[0][: -len(updater_suffix)] + installer_suffix
                != installers[0]
            ):
                raise ArtifactError("Windows installer and updater names do not match.")
    else:
        raise ArtifactError("Unsupported release platform.")
    allowed = set(required + optional)
    for suffix, names in by_suffix.items():
        if len(names) > 1:
            raise ArtifactError(f"Duplicate {suffix} artifacts for {platform}.")
        if suffix in required and len(names) != 1:
            raise ArtifactError(f"Missing {suffix} artifact for {platform}.")
        if names and suffix not in allowed:
            raise ArtifactError(f"Unexpected {suffix} artifact for {platform}.")
    if (
        platform == "linux-x86_64"
        and by_suffix[".AppImage.tar.gz"][0][: -len(".tar.gz")]
        != by_suffix[".AppImage"][0]
    ):
        raise ArtifactError("Linux AppImage installer and updater names do not match.")
    for name, path in files.items():
        validate_filename(name)
        check_file(path)
        kind = asset_kind(name)
        if kind is None:
            raise ArtifactError(f"Unrecognized artifact: {name}")
        if kind == "signature":
            if name[:-4] not in files or asset_kind(name[:-4]) != "updater":
                raise ArtifactError(f"Orphan signature: {name}")
            read_signature(path)
        elif kind == "updater" and name + ".sig" not in files:
            raise ArtifactError(f"Missing signature for updater: {name}")
        if f"_{version}_" not in name and f"-{version}-" not in name:
            raise ArtifactError(
                f"Artifact filename does not match release version: {name}"
            )
    return by_suffix[selected_suffix][0]


@contextmanager
def output_directory(out: Path):
    """Publish a complete staged output; never replace a nonempty directory."""
    if out.is_symlink():
        raise ArtifactError("Output must not be a symlink.")
    out = out.resolve()
    if out.exists() and (not out.is_dir() or any(out.iterdir())):
        raise ArtifactError("Output must not exist or must be an empty directory.")
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix=".localmind-release-", dir=out.parent
    ) as temporary:
        staging = Path(temporary)
        yield staging
        if out.is_symlink():
            raise ArtifactError(
                "Output changed during release assembly; refusing to overwrite it."
            )
        if out.exists():
            if any(out.iterdir()):
                raise ArtifactError(
                    "Output changed during release assembly; refusing to overwrite it."
                )
            out.rmdir()
        staging.replace(out)


def write_json(path: Path, value: object) -> None:
    path.write_text(
        json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )


def copy_verified(source: Path, destination: Path, expected_hash: str) -> None:
    shutil.copyfile(source, destination)
    if sha256(destination) != expected_hash:
        raise ArtifactError(f"Artifact changed while copying: {source.name}")


def collect(
    target: str, version: str, out: Path, *, repository_root: Path = REPOSITORY_ROOT
) -> dict:
    validate_version(version)
    if target not in TARGETS:
        raise ArtifactError("Unsupported Rust target.")
    platform = TARGETS[target]
    bundle = repository_root / "target" / target / "release/bundle"
    if not bundle.is_dir() or bundle.is_symlink():
        raise ArtifactError(f"Bundle directory is missing for {target}.")
    if out.resolve() == bundle.resolve() or bundle.resolve() in out.resolve().parents:
        raise ArtifactError("Output must be outside the bundle directory.")
    files: dict[str, Path] = {}
    folded: set[str] = set()
    for path in sorted(bundle.rglob("*")):
        # The macOS .app is a directory bundle, not a tree of release assets.
        if any(part.endswith(".app") for part in path.relative_to(bundle).parts[:-1]):
            continue
        if not path.is_file() or asset_kind(path.name) is None:
            continue
        name = path.name
        if platform.startswith("darwin-") and name in {
            "LocalMind.app.tar.gz",
            "LocalMind.app.tar.gz.sig",
        }:
            name = (
                f"LocalMind_{version}_{platform.removeprefix('darwin-')}.app.tar.gz"
                + (".sig" if name.endswith(".sig") else "")
            )
        validate_filename(name)
        if name.casefold() in folded:
            raise ArtifactError(f"Duplicate artifact filename: {name}")
        folded.add(name.casefold())
        files[name] = path
    updater = validate_asset_set(platform, files, version)
    assets = [
        {
            "filename": name,
            "sha256": sha256(path),
            "size": path.stat().st_size,
            "kind": asset_kind(name),
        }
        for name, path in sorted(files.items())
    ]
    hashes = {item["filename"]: item["sha256"] for item in assets}
    descriptor = {
        "schema_version": 1,
        "version": version,
        "target": target,
        "platforms": {
            platform: {
                "filename": updater,
                "signature": read_signature(files[updater + ".sig"]),
                "sha256": hashes[updater],
            }
        },
        "assets": assets,
    }
    with output_directory(out) as staging:
        for name, source in files.items():
            copy_verified(source, staging / name, hashes[name])
        write_json(staging / f"manifest-{platform}.json", descriptor)
    return descriptor


def load_descriptor(path: Path) -> dict:
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 1024 * 1024:
        raise ArtifactError("Invalid platform descriptor file.")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (ValueError, UnicodeError):
        raise ArtifactError(f"Invalid platform descriptor: {path.name}") from None
    if not isinstance(value, dict):
        raise ArtifactError("Platform descriptor must be an object.")
    return value


def assemble(
    input_root: Path,
    out: Path,
    version: str,
    repository: str,
    *,
    public_key_file: Path = PUBLIC_KEY_FILE,
    minisign: str = "minisign",
) -> dict:
    validate_version(version)
    validate_repository(repository)
    if not input_root.is_dir() or input_root.is_symlink():
        raise ArtifactError("Artifact input directory is missing or is a symlink.")
    if (
        out.resolve() == input_root.resolve()
        or input_root.resolve() in out.resolve().parents
    ):
        raise ArtifactError("Output must be outside the artifact input directory.")
    public_key = read_public_key(public_key_file)
    descriptors = sorted(input_root.rglob("manifest-*.json"))
    platforms: dict[str, dict] = {}
    release_files: dict[str, tuple[Path, str]] = {}
    used_paths: set[Path] = set(descriptors)
    folded: set[str] = set()
    for descriptor_path in descriptors:
        descriptor = load_descriptor(descriptor_path)
        target = descriptor.get("target")
        if (
            type(descriptor.get("schema_version")) is not int
            or descriptor.get("schema_version") != 1
            or descriptor.get("version") != version
            or not isinstance(target, str)
            or target not in TARGETS
        ):
            raise ArtifactError(
                "Platform descriptor version, schema, or target does not match the release."
            )
        platform = TARGETS[target]
        entries = descriptor.get("platforms")
        if (
            not isinstance(entries, dict)
            or set(entries) != {platform}
            or not isinstance(entries[platform], dict)
        ):
            raise ArtifactError(
                "Platform descriptor must contain exactly its target platform."
            )
        if platform in platforms:
            raise ArtifactError(f"Duplicate platform descriptor: {platform}")
        assets = descriptor.get("assets")
        if not isinstance(assets, list) or not assets:
            raise ArtifactError("Platform descriptor has no assets.")
        files = {}
        for asset in assets:
            if not isinstance(asset, dict):
                raise ArtifactError("Invalid asset descriptor.")
            name = validate_filename(asset.get("filename"))
            expected_hash = asset.get("sha256")
            if not isinstance(expected_hash, str) or not re.fullmatch(
                r"[0-9a-f]{64}", expected_hash
            ):
                raise ArtifactError(f"Invalid asset checksum: {name}")
            if name.casefold() in folded:
                raise ArtifactError(f"Release filename collision: {name}")
            folded.add(name.casefold())
            path = descriptor_path.parent / name
            check_file(path)
            if (
                type(asset.get("size")) is not int
                or path.stat().st_size != asset.get("size")
                or sha256(path) != expected_hash
            ):
                raise ArtifactError(f"Artifact checksum or size mismatch: {name}")
            if asset.get("kind") != asset_kind(name):
                raise ArtifactError(f"Incorrect artifact kind: {name}")
            files[name] = path
            release_files[name] = (path, expected_hash)
            used_paths.add(path)
        updater = validate_asset_set(platform, files, version)
        entry = entries[platform]
        signature = read_signature(files[updater + ".sig"])
        if (
            entry.get("filename") != updater
            or entry.get("sha256") != release_files[updater][1]
            or entry.get("signature") != signature
        ):
            raise ArtifactError(
                f"Updater descriptor does not match its signed artifact: {platform}"
            )
        for name, path in files.items():
            if asset_kind(name) == "updater":
                verify_signature(
                    path, read_signature(files[name + ".sig"]), public_key, minisign
                )
        platforms[platform] = {
            "signature": signature,
            "url": f"https://github.com/{repository}/releases/download/{quote('v' + version, safe='')}/{quote(updater, safe='')}",
        }
    if set(platforms) != PLATFORMS:
        missing = ", ".join(sorted(PLATFORMS - set(platforms)))
        raise ArtifactError(f"Release is missing required platforms: {missing}")
    for path in input_root.rglob("*"):
        if path.is_symlink():
            raise ArtifactError("Symlinks are not allowed in release artifact inputs.")
        if path.is_file() and path not in used_paths:
            raise ArtifactError(f"Unlisted release input file: {path.name}")
    manifest = {
        "version": version,
        "pub_date": datetime.now(UTC)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z"),
        "platforms": platforms,
    }
    with output_directory(out) as staging:
        for name, (source, expected_hash) in sorted(release_files.items()):
            copy_verified(source, staging / name, expected_hash)
        write_json(staging / "latest.json", manifest)
        checksums = "".join(
            f"{sha256(path)}  {path.name}\n" for path in sorted(staging.iterdir())
        )
        (staging / "SHA256SUMS.txt").write_text(checksums, encoding="utf-8")
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    collection = commands.add_parser(
        "collect", help="Collect one target's Tauri bundle."
    )
    collection.add_argument("--target", required=True, choices=TARGETS)
    collection.add_argument("--version", required=True)
    collection.add_argument("--out", required=True, type=Path)
    assembly = commands.add_parser(
        "assemble", help="Verify and flatten a complete four-platform release."
    )
    assembly.add_argument("--input", required=True, type=Path)
    assembly.add_argument("--out", required=True, type=Path)
    assembly.add_argument("--version", required=True)
    assembly.add_argument("--repository", required=True)
    assembly.add_argument("--public-key-file", type=Path, default=PUBLIC_KEY_FILE)
    assembly.add_argument("--minisign", default="minisign")
    args = parser.parse_args(argv)
    try:
        if args.command == "collect":
            descriptor = collect(args.target, args.version, args.out)
            print(
                f"Collected {len(descriptor['assets'])} assets for {TARGETS[args.target]}."
            )
        else:
            manifest = assemble(
                args.input,
                args.out,
                args.version,
                args.repository,
                public_key_file=args.public_key_file,
                minisign=args.minisign,
            )
            print(
                f"Verified and assembled {len(manifest['platforms'])} platforms for {args.version}."
            )
    except (ArtifactError, OSError) as error:
        print(f"Release validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
