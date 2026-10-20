"""Import, verify and sample local SmolTalk conversation candidates; never run a model."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
from pathlib import Path
import re
import shutil
import sqlite3
import tempfile
import unicodedata


REPOSITORY = "HuggingFaceTB/smoltalk"
REVISION = "5feaf2fd3ffca7c237fc38d1861bc30365d48ffa"
SUBSET = "smol-magpie-ultra"
POOL_VERSION = "smoltalk-magpie-v1"
DEFAULT_SEED = "localmind-smoltalk-v1"
MINIMUM_POOL_SIZE = 12000
DEFAULT_LIMITS = {"conversation_chars": 24000, "message_chars": 12000, "messages": 32}
BASE_URL = f"https://huggingface.co/datasets/{REPOSITORY}/resolve/{REVISION}"
HEX64 = re.compile(r"[0-9a-f]{64}")
EMAIL = re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.IGNORECASE)
PHONE = re.compile(r"(?<!\w)\+?\d[\d ()-]{7,}\d(?!\w)")
IPV4 = re.compile(r"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])")
IPV6 = re.compile(r"(?<!\w)[0-9a-fA-F]*:[0-9a-fA-F:]+(?!\w)")
SECRET = re.compile(
    r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|"
    r"\b(?:sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[A-Z0-9]{16})\b|"
    r"\b(?:api[_ -]?key|access[_ -]?token|password|secret)\s*[:=]\s*"
    r"""["']?[A-Za-z0-9+/=_-]{8,}""",
    re.IGNORECASE,
)
SCREENING_VERSION = "obvious-contact-ip-secret-v1"


def canonical(value) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"),
                      allow_nan=False).encode("utf-8")


def digest(value) -> str:
    return hashlib.sha256(canonical(value)).hexdigest()


def file_info(path: Path) -> dict:
    hasher = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            hasher.update(chunk)
            size += len(chunk)
    return {"file": path.name, "bytes": size, "sha256": hasher.hexdigest()}


def strict_json(raw: str | bytes):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON key")
            result[key] = value
        return result

    def invalid_constant(_):
        raise ValueError("Non-finite JSON number")

    return json.loads(raw, object_pairs_hook=pairs, parse_constant=invalid_constant)


def write_new_json(path: Path, value) -> None:
    with path.open("xb") as stream:
        stream.write(canonical(value) + b"\n")


def normalized_text(text: str) -> str:
    # Preserve whitespace inside prose/code. Only line endings, Unicode form
    # and outer whitespace affect exact-dedup normalization.
    return unicodedata.normalize("NFC", text.replace("\r\n", "\n").replace("\r", "\n")).strip()


def normalized_messages(messages: list[dict]) -> list[dict]:
    return [{"role": item["role"], "content": normalized_text(item["content"])}
            for item in messages]


def screening_reasons(text: str) -> list[str]:
    reasons = []
    if EMAIL.search(text):
        reasons.append("email")
    if any(10 <= sum(char.isdigit() for char in match.group()) <= 15
           for match in PHONE.finditer(text)):
        reasons.append("phone_like")
    for pattern in (IPV4, IPV6):
        found = False
        for match in pattern.finditer(text):
            try:
                ipaddress.ip_address(match.group())
                found = True
                break
            except ValueError:
                continue
        if found:
            reasons.append("ip_address")
            break
    if SECRET.search(text):
        reasons.append("secret_like")
    return reasons


def validate_messages(value, limits: dict) -> str | None:
    if not isinstance(value, list) or not value:
        return "missing_conversation"
    if len(value) > limits["messages"]:
        return "too_many_messages"
    if len(value) % 2:
        return "missing_final_assistant"
    characters = 0
    for index, message in enumerate(value):
        if not isinstance(message, dict):
            return "invalid_message"
        if message.get("role") not in {"user", "assistant"}:
            return "unsupported_role"
        if message["role"] != ("user" if index % 2 == 0 else "assistant"):
            return "nonalternating_roles"
        content = message.get("content")
        if not isinstance(content, str) or not content.strip():
            return "empty_or_nontext_content"
        if len(content) > limits["message_chars"]:
            return "message_too_long"
        characters += len(content)
    if characters > limits["conversation_chars"]:
        return "conversation_too_long"
    return None


def split_for(group_id: str, seed: str) -> str:
    # Domain separation keeps sample ranking independent of split membership.
    bucket = int(digest(["split-v1", seed, group_id])[:16], 16) % 100
    return "held_out" if bucket < 10 else "development"


def sample_rank(identity: str, seed: str) -> str:
    return digest(["sample-v1", seed, identity])


def validate_source(parquets: list[Path], card: Path, license_file: Path,
                    download_manifest: Path | None) -> dict:
    if not parquets or len({path.resolve() for path in parquets}) != len(parquets):
        raise ValueError("Supply unique local Parquet inputs")
    inputs = sorted((file_info(path) for path in parquets), key=lambda item: item["sha256"])
    if len({item["sha256"] for item in inputs}) != len(inputs):
        raise ValueError("Duplicate source-file content")
    documents = {"card": file_info(card), "license": file_info(license_file)}
    if any(item["bytes"] == 0 or item["bytes"] > 1024 * 1024 for item in documents.values()):
        raise ValueError("Source card/license must be nonempty and at most 1 MiB")
    provenance = {
        "status": "unverified_local_inputs", "download_manifest_sha256": None,
        "note": "Repository/revision are declared; local bytes alone do not establish upstream origin.",
    }
    if download_manifest is not None:
        if download_manifest.stat().st_size > 1024 * 1024:
            raise ValueError("Download manifest is too large")
        downloaded = strict_json(download_manifest.read_bytes())
        expected = {"dataset": REPOSITORY, "revision": REVISION, "subset": SUBSET,
                    "source_split": "train", "license": "Apache-2.0"}
        if not isinstance(downloaded, dict) or any(downloaded.get(k) != v for k, v in expected.items()):
            raise ValueError("Download manifest is for a different source/revision/license")
        source_files = downloaded.get("files")
        if not isinstance(source_files, list) or not all(isinstance(item, dict) for item in source_files):
            raise ValueError("Invalid download file manifest")
        for info in [*inputs, *documents.values()]:
            matches = [item for item in source_files if all(item.get(k) == v for k, v in info.items())]
            if len(matches) != 1:
                raise ValueError("Input checksum/size does not match the download manifest")
            url = matches[0].get("url", "")
            if info in inputs:
                if not re.fullmatch(
                    re.escape(BASE_URL + "/data/" + SUBSET + "/") + r"train-\d+-of-\d+\.parquet",
                    url,
                ):
                    raise ValueError("Only the pinned smol-magpie-ultra train files are supported")
            elif info == documents["card"] and url != BASE_URL + "/README.md":
                raise ValueError("Source card is not from the pinned revision")
            elif info == documents["license"] and url != "https://www.apache.org/licenses/LICENSE-2.0.txt":
                raise ValueError("Unexpected source license URL")
            info["url"] = url
        provenance = {
            "status": "download_manifest_matched",
            "download_manifest_sha256": file_info(download_manifest)["sha256"],
            "note": "Input bytes match the supplied pinned downloader record; see its source checks.",
        }
    return {
        "repository": REPOSITORY, "revision": REVISION, "subset": SUBSET,
        "source_split": "train", "license": "Apache-2.0",
        "license_scope": "Only the new synthetic smol-magpie-ultra subset; not the whole mixture.",
        "inputs": inputs, "documents": documents, "provenance": provenance,
        "sampling_scope": "Only supplied train shards; not a representative full-dataset sample.",
    }


def parquet_rows(path: Path):
    try:
        import pyarrow.parquet as parquet
    except ImportError as exc:
        raise ValueError("Importing Parquet requires optional pyarrow; checking/sampling do not") from exc
    source = parquet.ParquetFile(path)
    if "messages" not in source.schema_arrow.names:
        raise ValueError("Parquet is missing the messages column")
    for batch in source.iter_batches(batch_size=512, columns=["messages"]):
        yield from batch.to_pylist()


def import_pool(parquets: list[Path], card: Path, license_file: Path, out: Path, *,
                size: int = MINIMUM_POOL_SIZE, seed: str = DEFAULT_SEED,
                download_manifest: Path | None = None, limits: dict | None = None) -> dict:
    """Create one immutable import. An incomplete output has no manifest and cannot validate."""
    limits = dict(DEFAULT_LIMITS if limits is None else limits)
    if type(size) is not int or size < 1 or not isinstance(seed, str) or not seed.strip():
        raise ValueError("Sample size and seed must be nonempty")
    if set(limits) != set(DEFAULT_LIMITS) or any(type(value) is not int or value < 1 for value in limits.values()):
        raise ValueError("Invalid conversation limits")
    source = validate_source(parquets, card, license_file, download_manifest)
    out.mkdir(parents=True, exist_ok=False)
    shutil.copyfile(card, out / "SOURCE-README.md")
    shutil.copyfile(license_file, out / "LICENSE-2.0.txt")
    if download_manifest is not None:
        shutil.copyfile(download_manifest, out / "download.json")
    counts, rejected, pii = Counter(), Counter(), Counter()
    source_by_hash = {item["sha256"]: item for item in source["inputs"]}
    paths = sorted(((file_info(path)["sha256"], path) for path in parquets), key=lambda item: item[0])
    with tempfile.TemporaryDirectory(prefix=".import-", dir=out) as scratch:
        connection = sqlite3.connect(Path(scratch) / "candidates.db")
        try:
            connection.execute("CREATE TABLE hashes (value TEXT PRIMARY KEY)")
            connection.execute(
                "CREATE TABLE candidates (group_id TEXT PRIMARY KEY, content_hash TEXT, "
                "origin TEXT, rank TEXT, payload TEXT)"
            )
            for source_hash, path in paths:
                for row_index, row in enumerate(parquet_rows(path)):
                    counts["source_rows"] += 1
                    messages = row.get("messages") if isinstance(row, dict) else None
                    invalid = validate_messages(messages, limits)
                    if invalid:
                        rejected[invalid] += 1
                        continue
                    findings = screening_reasons("\n".join(item["content"] for item in messages))
                    if findings:
                        rejected["pii_or_secret_like"] += 1
                        pii.update(findings)
                        continue
                    counts["eligible_rows_before_dedup"] += 1
                    normalized = normalized_messages(messages)
                    content_hash = digest(normalized)
                    group_id = "CG-" + digest(normalized[0]["content"])
                    identity = "CP-" + content_hash
                    inserted = connection.execute(
                        "INSERT OR IGNORE INTO hashes VALUES (?)", (content_hash,)
                    ).rowcount
                    counts["exact_duplicate_rows"] += int(not inserted)
                    origin = f"{source_hash}:{row_index:012d}"
                    record = {
                        "id": identity, "group_id": group_id, "split": split_for(group_id, seed),
                        "conversation_sha256": content_hash,
                        "input_sha256": digest(normalized[:-1]),
                        "messages": [{"role": item["role"], "content": item["content"]}
                                     for item in messages[:-1]],
                        "reference_answer": {"role": "assistant", "content": messages[-1]["content"],
                                             "status": "unverified_source_response"},
                        "source": {"file_sha256": source_hash, "row_index": row_index},
                    }
                    connection.execute(
                        "INSERT INTO candidates VALUES (?, ?, ?, ?, ?) "
                        "ON CONFLICT(group_id) DO UPDATE SET content_hash=excluded.content_hash, "
                        "origin=excluded.origin, rank=excluded.rank, payload=excluded.payload "
                        "WHERE excluded.content_hash < candidates.content_hash OR "
                        "(excluded.content_hash = candidates.content_hash AND excluded.origin < candidates.origin)",
                        (group_id, content_hash, origin, sample_rank(group_id, seed),
                         canonical(record).decode("utf-8")),
                    )
                # Confirm inputs were not edited while this import was reading them.
                if file_info(path)["sha256"] != source_hash or source_hash not in source_by_hash:
                    raise ValueError("A source file changed during import")
                connection.commit()
            unique = connection.execute("SELECT count(*) FROM candidates").fetchone()[0]
            counts["unique_first_user_groups"] = unique
            counts["same_first_user_other_conversations"] = (
                counts["eligible_rows_before_dedup"] - counts["exact_duplicate_rows"] - unique
            )
            if unique < size:
                raise ValueError(f"Only {unique} screened unique conversations available; {size} requested")
            selected = [strict_json(row[0]) for row in connection.execute(
                "SELECT payload FROM candidates ORDER BY rank, group_id LIMIT ?", (size,)
            )]
        finally:
            connection.close()
    selected.sort(key=lambda record: record["id"])
    with (out / "records.jsonl").open("xb") as stream:
        for record in selected:
            stream.write(canonical(record) + b"\n")
    counts["selected_conversations"] = len(selected)
    report = {
        "counts": dict(counts), "rejected_by_reason": dict(sorted(rejected.items())),
        "screening_matches": dict(sorted(pii.items())),
        "splits": dict(Counter(record["split"] for record in selected)),
        "screening_limit": "Heuristics miss some personal data and reject some harmless examples; not anonymization.",
        "dedup_limit": "Exact normalized conversations and first-user grouping; semantic paraphrases are not detected.",
    }
    write_new_json(out / "report.json", report)
    files = {name: file_info(out / name) for name in
             ("records.jsonl", "report.json", "SOURCE-README.md", "LICENSE-2.0.txt")}
    if download_manifest is not None:
        files["download.json"] = file_info(out / "download.json")
    manifest = {
        "schema_version": 1, "kind": "conversation_pool", "pool_version": POOL_VERSION,
        "created_at_utc": datetime.now(timezone.utc).isoformat(),
        "purpose": "Evaluation candidates; no human-approved rubrics or training performed.",
        "source": source, "seed": seed, "requested_size": size, "limits": limits,
        "screening_version": SCREENING_VERSION,
        "dedup_version": "nfc-newline-outerstrip-first-user-v1",
        "split_version": "group-sha256-90-development-10-held-out-v1",
        "importer_sha256": file_info(Path(__file__))["sha256"],
        "files": files,
    }
    write_new_json(out / "manifest.json", manifest)
    return check_pool(out)


def read_manifest(pool: Path) -> dict:
    path = pool / "manifest.json"
    if path.stat().st_size > 1024 * 1024:
        raise ValueError("Pool manifest is too large")
    manifest = strict_json(path.read_bytes())
    if not isinstance(manifest, dict) or manifest.get("schema_version") != 1 or manifest.get("kind") != "conversation_pool":
        raise ValueError("Unsupported conversation pool schema")
    if manifest.get("pool_version") != POOL_VERSION:
        raise ValueError("Unsupported conversation pool version")
    return manifest


def iter_pool_records(pool: Path):
    """Stream records after check_pool has verified the pool."""
    with (pool / "records.jsonl").open("rb") as stream:
        while raw := stream.readline(2 * 1024 * 1024 + 1):
            if len(raw) > 2 * 1024 * 1024:
                raise ValueError("Oversized pool record")
            yield strict_json(raw)


def check_pool(pool: Path) -> dict:
    manifest = read_manifest(pool)
    expected_files = {"records.jsonl", "report.json", "SOURCE-README.md", "LICENSE-2.0.txt"}
    files = manifest.get("files")
    if not isinstance(files, dict) or set(files) not in (expected_files, expected_files | {"download.json"}):
        raise ValueError("Invalid pool file manifest")
    for name, expected in files.items():
        if file_info(pool / name) != expected:
            raise ValueError(f"Pool file checksum mismatch: {name}")
    source = manifest.get("source", {})
    if not isinstance(source, dict) or any(source.get(key) != value for key, value in (
        ("repository", REPOSITORY), ("revision", REVISION), ("subset", SUBSET),
        ("source_split", "train"), ("license", "Apache-2.0"),
    )):
        raise ValueError("Invalid pool source identity")
    limits = manifest.get("limits", {})
    if set(limits) != set(DEFAULT_LIMITS) or any(type(v) is not int or v < 1 for v in limits.values()):
        raise ValueError("Invalid pool limits")
    seed = manifest.get("seed")
    if not isinstance(seed, str) or not seed:
        raise ValueError("Invalid pool seed")
    if manifest.get("screening_version") != SCREENING_VERSION or manifest.get("dedup_version") != "nfc-newline-outerstrip-first-user-v1" or manifest.get("split_version") != "group-sha256-90-development-10-held-out-v1":
        raise ValueError("Unsupported screening/dedup/split algorithm")
    provenance = source.get("provenance", {})
    if not isinstance(provenance, dict) or provenance.get("status") not in {"unverified_local_inputs", "download_manifest_matched"}:
        raise ValueError("Invalid source provenance status")
    if provenance["status"] == "download_manifest_matched":
        if "download.json" not in files or provenance.get("download_manifest_sha256") != files["download.json"]["sha256"]:
            raise ValueError("Missing or mismatched download provenance")
    source_hashes = {item.get("sha256") for item in source.get("inputs", []) if isinstance(item, dict)}
    if not source_hashes or any(not isinstance(item, str) or not HEX64.fullmatch(item) for item in source_hashes):
        raise ValueError("Invalid source hashes")
    ids, groups, splits = set(), set(), Counter()
    previous = ""
    for record in iter_pool_records(pool):
        if not isinstance(record, dict):
            raise ValueError("Invalid pool record")
        if set(record) != {"id", "group_id", "split", "conversation_sha256", "input_sha256",
                           "messages", "reference_answer", "source"}:
            raise ValueError("Unexpected pool record fields")
        messages = record.get("messages")
        reference = record.get("reference_answer")
        if not isinstance(messages, list) or not isinstance(reference, dict) or reference.get("status") != "unverified_source_response":
            raise ValueError("Missing conversation or unverified reference marker")
        full = [*messages, reference]
        if validate_messages(full, limits):
            raise ValueError("Invalid conversation structure/length")
        if any(set(item) != {"role", "content"} for item in messages) or set(reference) != {"role", "content", "status"}:
            raise ValueError("Unexpected conversation/reference fields")
        if screening_reasons("\n".join(item["content"] for item in full)):
            raise ValueError("Pool contains a screening match")
        normalized = normalized_messages(full)
        content_hash = digest(normalized)
        identity, group = "CP-" + content_hash, "CG-" + digest(normalized[0]["content"])
        if record.get("id") != identity or record.get("conversation_sha256") != content_hash:
            raise ValueError("Conversation identity/hash mismatch")
        if record.get("input_sha256") != digest(normalized[:-1]) or record.get("group_id") != group:
            raise ValueError("Input/group hash mismatch")
        if identity in ids or group in groups or identity <= previous:
            raise ValueError("Duplicate or unsorted conversations/groups")
        if record.get("split") != split_for(group, seed):
            raise ValueError("Changed group split")
        origin = record.get("source", {})
        if not isinstance(origin, dict) or origin.get("file_sha256") not in source_hashes or type(origin.get("row_index")) is not int or origin["row_index"] < 0:
            raise ValueError("Invalid source row provenance")
        ids.add(identity)
        groups.add(group)
        splits[record["split"]] += 1
        previous = identity
    if type(manifest.get("requested_size")) is not int or len(ids) != manifest["requested_size"]:
        raise ValueError("Pool size does not match manifest")
    report = strict_json((pool / "report.json").read_bytes())
    if not isinstance(report, dict) or report.get("counts", {}).get("selected_conversations") != len(ids) or report.get("splits") != dict(splits):
        raise ValueError("Pool report counts do not match records")
    return {"status": "valid", "pool_version": POOL_VERSION, "conversations": len(ids),
            "meets_12000_target": len(ids) >= MINIMUM_POOL_SIZE, "splits": dict(splits),
            "provenance": source["provenance"]["status"],
            "manifest_sha256": file_info(pool / "manifest.json")["sha256"],
            "records_sha256": files["records.jsonl"]["sha256"], "report": report}


def sample_pool(pool: Path, out: Path, *, count: int = 100, split: str = "development",
                allow_held_out: bool = False) -> dict:
    validated = check_pool(pool)
    if split not in {"development", "held_out"} or (split == "held_out" and not allow_held_out):
        raise ValueError("Held-out sampling requires explicit --allow-held-out")
    if type(count) is not int or count < 1:
        raise ValueError("Sample count must be positive")
    manifest = read_manifest(pool)
    eligible = [row for row in iter_pool_records(pool) if row["split"] == split]
    if len(eligible) < count:
        raise ValueError("Not enough conversations in the selected split")
    selected = sorted(eligible, key=lambda row: (sample_rank(row["id"], manifest["seed"]), row["id"]))[:count]
    cases = [{
        "id": row["id"], "split": row["split"], "evaluation_layer": "service",
        "severity": "normal", "category": "imported_conversation", "title": "Imported conversation",
        "messages": row["messages"], "web_search": False, "evidence": [],
        "expectation": None, "rubric_status": "not_authored", "reference_record_id": row["id"],
    } for row in sorted(selected, key=lambda row: row["id"])]
    sample = {
        "schema_version": 1, "kind": "conversation_pool_sample", "pool_version": POOL_VERSION,
        "pool_manifest_sha256": validated["manifest_sha256"],
        "pool_records_sha256": validated["records_sha256"], "split": split,
        "held_out_acknowledged": split == "held_out" and allow_held_out,
        "reference_policy": "Source final answers remain in the pool; never sent as input or treated as approved targets.",
        "cases": cases,
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    write_new_json(out, sample)
    return {"status": "sampled", "cases": len(cases), "split": split,
            "sample_sha256": file_info(out)["sha256"], "rubrics": "not_authored"}


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    importer = commands.add_parser("import")
    importer.add_argument("--parquet", type=Path, action="append", required=True)
    importer.add_argument("--source-card", type=Path, required=True)
    importer.add_argument("--license-file", type=Path, required=True)
    importer.add_argument("--download-manifest", type=Path)
    importer.add_argument("--out", type=Path, required=True)
    importer.add_argument("--size", type=int, default=MINIMUM_POOL_SIZE)
    importer.add_argument("--seed", default=DEFAULT_SEED)
    checker = commands.add_parser("check")
    checker.add_argument("--pool", type=Path, required=True)
    sampler = commands.add_parser("sample")
    sampler.add_argument("--pool", type=Path, required=True)
    sampler.add_argument("--out", type=Path, required=True)
    sampler.add_argument("--count", type=int, default=100)
    sampler.add_argument("--split", choices=("development", "held_out"), default="development")
    sampler.add_argument("--allow-held-out", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.command == "import":
            if args.size < MINIMUM_POOL_SIZE:
                raise ValueError("A published pool must contain at least 12000 unique screened conversations")
            report = import_pool(args.parquet, args.source_card, args.license_file, args.out,
                                 size=args.size, seed=args.seed, download_manifest=args.download_manifest)
        elif args.command == "check":
            report = check_pool(args.pool)
            if not report["meets_12000_target"]:
                raise ValueError("Pool is below the 12000-conversation target")
        else:
            report = sample_pool(args.pool, args.out, count=args.count, split=args.split,
                                 allow_held_out=args.allow_held_out)
    except (OSError, ValueError, TypeError, sqlite3.Error) as exc:
        parser.exit(1, f"Conversation pool failed: {exc}\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
