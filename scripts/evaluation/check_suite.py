"""Validate the fixed conversation corpus without contacting a model or network."""

import argparse
import hashlib
import json
from collections import Counter
from datetime import datetime
from pathlib import Path
from urllib.parse import urlsplit

DEFAULT_SUITE = Path(__file__).resolve().parents[2] / "evaluation" / "cases.json"


def validate_suite(suite: dict) -> dict:
    if not isinstance(suite, dict) or suite.get("schema_version") != 1:
        raise ValueError("Expected a version 1 suite object")
    context_suite = suite.get("suite_kind") == "context_development"
    if suite.get("suite_kind") not in (None, "context_development"):
        raise ValueError("Unknown supplemental suite kind")
    cases = suite.get("cases")
    expected_count = 5 if context_suite else 30
    if not isinstance(cases, list) or len(cases) != expected_count:
        raise ValueError(f"The {'context supplement' if context_suite else 'baseline'} must contain exactly {expected_count} cases")
    seen = set()
    splits, layers, categories = Counter(), Counter(), Counter()
    for case in cases:
        if not isinstance(case, dict):
            raise ValueError("Every case must be an object")
        identity = case.get("id")
        prefix = "CTX-" if context_suite else "LM-"
        if not isinstance(identity, str) or not identity.startswith(prefix) or identity in seen:
            raise ValueError(f"Missing or duplicate case ID: {identity}")
        seen.add(identity)
        for field in ("title", "category"):
            if not isinstance(case.get(field), str) or not case[field].strip():
                raise ValueError(f"{identity}: missing {field}")
        if case.get("split") not in ("development", "held_out"):
            raise ValueError(f"{identity}: invalid split")
        if case.get("evaluation_layer") not in ("service", "desktop"):
            raise ValueError(f"{identity}: invalid evaluation layer")
        if case.get("severity") not in ("critical", "normal"):
            raise ValueError(f"{identity}: invalid severity")
        if type(case.get("web_search")) is not bool:
            raise ValueError(f"{identity}: web_search must be boolean")
        messages = case.get("messages")
        if not isinstance(messages, list) or not messages or len(messages) % 2 != 1:
            raise ValueError(f"{identity}: conversation must end with a user message")
        for index, message in enumerate(messages):
            if not isinstance(message, dict):
                raise ValueError(f"{identity}: invalid message")
            role = "user" if index % 2 == 0 else "assistant"
            if message.get("role") != role or not isinstance(message.get("content"), str):
                raise ValueError(f"{identity}: expected {role} message at index {index}")
            if not message["content"].strip():
                raise ValueError(f"{identity}: empty message")
        expectation = case.get("expectation")
        if not isinstance(expectation, dict):
            raise ValueError(f"{identity}: missing acceptance rubric")
        for field in ("required", "forbidden"):
            items = expectation.get(field)
            if not isinstance(items, list) or not items:
                raise ValueError(f"{identity}: empty {field} rubric")
            if any(not isinstance(item, str) or not item.strip() for item in items):
                raise ValueError(f"{identity}: invalid {field} rubric")
        evidence = case.get("evidence", [])
        if not isinstance(evidence, list):
            raise ValueError(f"{identity}: evidence must be a list")
        evidence_ids = set()
        for source in evidence:
            if not isinstance(source, dict):
                raise ValueError(f"{identity}: invalid evidence")
            for field in ("id", "title", "url", "text"):
                if not isinstance(source.get(field), str) or not source[field].strip():
                    raise ValueError(f"{identity}: evidence missing {field}")
            if source["id"] in evidence_ids:
                raise ValueError(f"{identity}: duplicate evidence ID")
            evidence_ids.add(source["id"])
            url = urlsplit(source["url"])
            if url.scheme != "https" or not (url.hostname or "").endswith(".invalid"):
                raise ValueError(f"{identity}: evidence must use a synthetic .invalid URL")
        if evidence and not isinstance(case.get("as_of"), str):
            raise ValueError(f"{identity}: dated evidence needs a fixed as_of clock")
        for date in [case.get("as_of"), *(e.get("published_at") for e in evidence)]:
            if date is None:
                continue
            if not isinstance(date, str):
                raise ValueError(f"{identity}: date must be an ISO timestamp")
            parsed = datetime.fromisoformat(date.replace("Z", "+00:00"))
            if parsed.utcoffset() is None:
                raise ValueError(f"{identity}: timestamps must include a timezone")
        if "memory_context" in case and not isinstance(case["memory_context"], str):
            raise ValueError(f"{identity}: memory_context must be synthetic text")
        splits[case["split"]] += 1
        layers[case["evaluation_layer"]] += 1
        categories[case["category"]] += 1
    if context_suite:
        if splits != {"development": 5} or layers != {"service": 5}:
            raise ValueError("Context supplement contains only five development service cases")
        if seen != {f"CTX-{index:03d}" for index in range(1, 6)}:
            raise ValueError("Keep context supplement IDs CTX-001 through CTX-005")
        return {"cases": len(cases), "splits": dict(splits), "layers": dict(layers),
                "categories": dict(categories), "suite_kind": "context_development"}
    if splits != {"development": 20, "held_out": 10}:
        raise ValueError("Keep the fixed 20 development / 10 held-out split")
    if seen != {f"LM-{index:03d}" for index in range(1, 31)}:
        raise ValueError("Keep the stable case IDs LM-001 through LM-030")
    for case in cases:
        expected_split = "development" if int(case["id"][3:]) <= 20 else "held_out"
        if case["split"] != expected_split:
            raise ValueError(f"{case['id']}: changed the frozen split assignment")
    return {"cases": len(cases), "splits": dict(splits), "layers": dict(layers),
            "categories": dict(categories)}


def load_suite(path: Path = DEFAULT_SUITE) -> tuple[dict, dict]:
    raw = path.read_bytes()
    suite = json.loads(raw)
    report = validate_suite(suite)
    report["sha256"] = hashlib.sha256(raw).hexdigest()
    return suite, report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", type=Path, default=DEFAULT_SUITE)
    args = parser.parse_args()
    try:
        _, report = load_suite(args.suite)
    except (OSError, ValueError, TypeError) as exc:
        parser.exit(1, f"Conversation suite validation failed: {exc}\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
