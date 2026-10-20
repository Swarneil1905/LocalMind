"""Prepare and summarize manual rubric reviews of saved evaluation runs.

This tool never evaluates answer prose or calls a model. It derives outcomes
only from explicit reviewer findings, while keeping execution gaps visible.
Reviews bind to the exact bytes of the manifest, selected cases, and results.
"""

import argparse
import hashlib
import json
import re
from collections import Counter
from dataclasses import dataclass
from datetime import date
from pathlib import Path


DIMENSIONS = ("correctness_grounding", "instruction_context", "usefulness", "communication")
OPTIONAL_DIMENSION = "state_action"
OUTCOMES = ("pass", "fail", "not_evaluated", "run_error")
REQUIRED_STATUSES = {"met", "partly_met", "unmet", "unobservable"}
FORBIDDEN_STATUSES = {"observed", "not_observed", "unobservable"}
MAX_JSON_BYTES = 32 * 1024 * 1024


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def reject_duplicate_keys(pairs: list) -> dict:
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json(path: Path) -> tuple[dict, str]:
    with path.open("rb") as stream:
        raw = stream.read(MAX_JSON_BYTES + 1)
    require(len(raw) <= MAX_JSON_BYTES, "JSON evidence file exceeds the size limit")
    value = json.loads(raw, object_pairs_hook=reject_duplicate_keys)
    require(isinstance(value, dict), "Expected a JSON object")
    return value, hashlib.sha256(raw).hexdigest()


def valid_sha(value) -> bool:
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


@dataclass
class RunEvidence:
    binding: dict
    manifest: dict
    cases: list[dict]
    results: dict[str, dict | None]
    hashes: dict[str, str | None]


def load_run(run_dir: Path) -> RunEvidence:
    run_dir = Path(run_dir)
    manifest, manifest_sha = load_json(run_dir / "manifest.json")
    selected, cases_sha = load_json(run_dir / "cases.json")
    require(type(manifest.get("schema_version")) is int and manifest["schema_version"] == 1,
            "Unsupported run manifest schema")
    require(type(selected.get("schema_version")) is int and selected["schema_version"] == 1,
            "Unsupported selected-case schema")
    ids = manifest.get("case_ids")
    require(isinstance(ids, list) and bool(ids), "Manifest must select at least one case")
    suite_kind = manifest.get("suite_kind")
    require(suite_kind in (None, "context_development")
            and selected.get("suite_kind") == suite_kind,
            "Supplemental suite identity must match manifest and selected cases")
    context_suite = suite_kind == "context_development"
    identity_pattern = r"CTX-00[1-5]" if context_suite else r"LM-\d{3}"
    require(all(isinstance(identity, str) and re.fullmatch(identity_pattern, identity) for identity in ids),
            "Invalid selected case ID")
    require(len(ids) == len(set(ids)), "Manifest contains duplicate selected IDs")
    require(manifest.get("split") in ("development", "held_out"), "Invalid manifest split")
    require(not context_suite or manifest["split"] == "development",
            "Context supplemental cases are development-only")
    require(valid_sha(manifest.get("suite_sha256")), "Invalid suite SHA256")
    cases = selected.get("cases")
    require(isinstance(cases, list) and all(isinstance(case, dict) for case in cases),
            "Selected cases must be objects")
    require([case.get("id") for case in cases] == ids,
            "cases.json must contain exactly the selected IDs in manifest order")
    for case in cases:
        identity = case["id"]
        require(case.get("split") == manifest["split"], f"{identity}: case split does not match run")
        require(case.get("evaluation_layer") in ("service", "desktop"), f"{identity}: invalid layer")
        require(not context_suite or case["evaluation_layer"] == "service",
                f"{identity}: context supplemental cases require the service layer")
        require(case.get("severity") in ("normal", "critical"), f"{identity}: invalid severity")
        expectation = case.get("expectation")
        require(isinstance(expectation, dict), f"{identity}: missing rubric")
        for kind in ("required", "forbidden"):
            items = expectation.get(kind)
            require(isinstance(items, list) and bool(items)
                    and all(isinstance(item, str) and item.strip() for item in items),
                    f"{identity}: invalid {kind} rubric")
    extra_files = {path.name for path in (run_dir / "results").glob("*.json")} - {f"{identity}.json" for identity in ids}
    require(not extra_files, "Result directory contains cases not selected in this run")
    results, hashes = {}, {}
    for case in cases:
        identity = case["id"]
        path = run_dir / "results" / f"{identity}.json"
        if not path.exists():
            results[identity], hashes[identity] = None, None
            continue
        result, digest = load_json(path)
        for field in ("id", "split", "evaluation_layer", "severity"):
            require(result.get(field) == case[field], f"{identity}: result {field} does not match selected case")
        for field in ("model", "profile"):
            if field in manifest:
                require(result.get(field) == manifest[field], f"{identity}: result {field} does not match manifest")
        require(result.get("status") in ("completed", "run_error", "not_evaluated"), f"{identity}: invalid result status")
        require(type(result.get("truncated")) is bool, f"{identity}: result must state whether output was truncated")
        require(all(key in result for key in ("model", "profile", "answer", "error", "timings", "trace", "effective_request")),
                f"{identity}: result is missing required evidence fields")
        require(isinstance(result["answer"], str) or (result["answer"] is None and result["status"] != "completed"),
                f"{identity}: invalid saved answer")
        results[identity], hashes[identity] = result, digest
    binding = {"manifest_sha256": manifest_sha, "cases_sha256": cases_sha,
               "suite_sha256": manifest["suite_sha256"], "split": manifest["split"], "case_ids": ids}
    return RunEvidence(binding, manifest, cases, results, hashes)


def review_template(run: RunEvidence) -> dict:
    entries = []
    for case in run.cases:
        entry = {
            "id": case["id"], "result_sha256": run.hashes[case["id"]],
            "dimensions": {key: None for key in (*DIMENSIONS, OPTIONAL_DIMENSION)},
            "notes": "",
        }
        for kind in ("required", "forbidden"):
            entry[kind] = [{"index": index, "text": text, "status": None, "evidence": ""}
                           for index, text in enumerate(case["expectation"][kind])]
        entries.append(entry)
    return {"schema_version": 1, "run": run.binding,
            "reviewer": {"name": "", "date": ""}, "cases": entries}


def create_review(run_dir: Path) -> dict:
    """Return an entirely unreviewed template bound to current run evidence."""
    return review_template(load_run(run_dir))


def has_review_content(entry: dict) -> bool:
    return (any(value is not None for value in entry["dimensions"].values())
            or bool(entry["notes"].strip())
            or any(item["status"] is not None or item["evidence"].strip()
                   for kind in ("required", "forbidden") for item in entry[kind]))


def validate_review(run: RunEvidence, review: dict) -> None:
    require(isinstance(review, dict) and set(review) == {"schema_version", "run", "reviewer", "cases"},
            "Invalid review object fields")
    require(type(review["schema_version"]) is int and review["schema_version"] == 1, "Unsupported review schema")
    require(review["run"] == run.binding, "Review run binding does not match manifest/cases/suite hashes or selected IDs")
    reviewer = review["reviewer"]
    require(isinstance(reviewer, dict) and set(reviewer) == {"name", "date"}
            and all(isinstance(value, str) for value in reviewer.values()), "Reviewer needs string name/date fields")
    if reviewer["date"]:
        require(re.fullmatch(r"\d{4}-\d{2}-\d{2}", reviewer["date"]) is not None, "Reviewer date must be YYYY-MM-DD")
        date.fromisoformat(reviewer["date"])
    entries = review["cases"]
    require(isinstance(entries, list) and all(isinstance(entry, dict) for entry in entries), "Review cases must be objects")
    require([entry.get("id") for entry in entries] == run.binding["case_ids"],
            "Review must contain exactly the selected IDs in manifest order")
    any_content = False
    for case, entry in zip(run.cases, entries):
        identity = case["id"]
        require(set(entry) == {"id", "result_sha256", "dimensions", "required", "forbidden", "notes"},
                f"{identity}: invalid review entry fields")
        require(entry["result_sha256"] == run.hashes[identity], f"{identity}: result SHA256 does not match saved evidence")
        dimensions = entry["dimensions"]
        require(isinstance(dimensions, dict) and set(DIMENSIONS) <= set(dimensions)
                and set(dimensions) <= {*DIMENSIONS, OPTIONAL_DIMENSION}, f"{identity}: invalid score dimensions")
        require(all(value is None or (type(value) is int and 0 <= value <= 3) for value in dimensions.values()),
                f"{identity}: scores must be integers 0..3 or null")
        require(isinstance(entry["notes"], str), f"{identity}: notes must be a string")
        for kind, allowed in (("required", REQUIRED_STATUSES), ("forbidden", FORBIDDEN_STATUSES)):
            items = entry[kind]
            original = case["expectation"][kind]
            require(isinstance(items, list) and len(items) == len(original), f"{identity}: {kind} rubric length changed")
            for index, (item, text) in enumerate(zip(items, original)):
                require(isinstance(item, dict) and set(item) == {"index", "text", "status", "evidence"},
                        f"{identity}: invalid {kind} finding")
                require(type(item["index"]) is int and item["index"] == index and item["text"] == text,
                        f"{identity}: {kind} rubric mapping or text changed")
                require(item["status"] is None or (isinstance(item["status"], str) and item["status"] in allowed),
                        f"{identity}: invalid {kind} finding status")
                require(isinstance(item["evidence"], str), f"{identity}: evidence notes must be a string")
                require(item["status"] is None or bool(item["evidence"].strip()),
                        f"{identity}: explicit findings require evidence notes")
        any_content = any_content or has_review_content(entry)
    require(not any_content or (bool(reviewer["name"].strip()) and bool(reviewer["date"])),
            "Review decisions or notes require reviewer name and date")


def execution_state(result: dict | None) -> tuple[str, str | None]:
    if result is None:
        return "run_error", "missing_result"
    if result["truncated"]:
        return "run_error", "truncated_output"
    if result["status"] == "not_evaluated":
        return "not_evaluated", "execution_not_evaluated"
    if result["status"] == "run_error" or result["error"]:
        return "run_error", "saved_execution_error"
    return "completed", None


def review_outcome(entry: dict) -> tuple[str, str, str]:
    """Derive an outcome from human findings; dimensions never set pass/fail."""
    if not has_review_content(entry):
        return "not_evaluated", "unreviewed", "unreviewed"
    complete = all(item["status"] is not None for kind in ("required", "forbidden") for item in entry[kind])
    progress = "complete" if complete else "incomplete"
    if (any(item["status"] in ("partly_met", "unmet") for item in entry["required"])
            or any(item["status"] == "observed" for item in entry["forbidden"])):
        return "fail", "explicit_rubric_failure", progress
    if not complete:
        return "not_evaluated", "incomplete_review", progress
    if any(item["status"] == "unobservable" for kind in ("required", "forbidden") for item in entry[kind]):
        return "not_evaluated", "unobservable_requirement_or_failure_condition", progress
    return "pass", "all_required_met_and_forbidden_not_observed", progress


def aggregate(cases: list[dict]) -> dict:
    outcomes = Counter(case["outcome"] for case in cases)
    execution = Counter(case["execution_status"] for case in cases)
    review_progress = Counter(case["review_state"] for case in cases)
    score_eligible = [case for case in cases if case["scores"] is not None]
    coverage = {}
    for dimension in (*DIMENSIONS, OPTIONAL_DIMENSION):
        values = [case["scores"].get(dimension) for case in score_eligible]
        scored = [value for value in values if value is not None]
        coverage[dimension] = {
            "eligible_reviewed_cases": len(values), "scored_cases": len(scored),
            "null_or_not_applicable_cases": len(values) - len(scored),
            "score_counts": {str(score): scored.count(score) for score in range(4)},
        }
    return {
        "selected_cases": len(cases),
        "outcomes": {key: outcomes[key] for key in OUTCOMES},
        "execution": {key: execution[key] for key in ("completed", "run_error", "not_evaluated")},
        "missing_results": sum(case["reason"] == "missing_result" for case in cases),
        "review_progress": {key: review_progress[key] for key in ("unreviewed", "incomplete", "complete")},
        "quality_evaluated_cases": outcomes["pass"] + outcomes["fail"],
        "pass_fraction_of_selected": {"numerator": outcomes["pass"], "denominator": len(cases)},
        "critical_cases": sum(case["severity"] == "critical" for case in cases),
        "critical_failures": sum(case["severity"] == "critical" and case["outcome"] == "fail" for case in cases),
        "critical_run_errors": sum(case["severity"] == "critical" and case["outcome"] == "run_error" for case in cases),
        "score_coverage": coverage,
    }


def summarize(run_dir: Path, review: dict | None = None) -> dict:
    """Summarize all selected cases, validating review/evidence binding first."""
    run = load_run(run_dir)
    supplied = review is not None
    review = review if supplied else review_template(run)
    validate_review(run, review)
    cases = []
    for case, entry in zip(run.cases, review["cases"]):
        identity = case["id"]
        execution, execution_reason = execution_state(run.results[identity])
        if (execution == "completed" and case["evaluation_layer"] == "desktop"
                and run.manifest.get("execution_layer") in ("python_service_generator", "service")):
            execution, execution_reason = "not_evaluated", "desktop_not_executed_by_service_runner"
        outcome, reason, progress = review_outcome(entry)
        if execution != "completed":
            outcome, reason = execution, execution_reason
        # Scores about an absent, skipped, errored, or truncated answer are not
        # quality observations. Keep the exclusion visible for each case.
        eligible = execution == "completed" and has_review_content(entry)
        cases.append({
            "id": identity, "evaluation_layer": case["evaluation_layer"], "severity": case["severity"],
            "result_sha256": run.hashes[identity], "execution_status": execution,
            "outcome": outcome, "reason": reason, "review_state": progress,
            "scores": {key: entry["dimensions"].get(key) for key in (*DIMENSIONS, OPTIONAL_DIMENSION)} if eligible else None,
            "scores_excluded_reason": None if eligible else (execution_reason or "unreviewed"),
            "findings": {key: entry[key] for key in ("required", "forbidden", "notes")} if has_review_content(entry) else None,
        })
    return {
        "schema_version": 1, "run": run.binding,
        "run_execution": {
            "state": run.manifest.get("state", "not_recorded"),
            "error": run.manifest.get("error"),
            "execution_layer": run.manifest.get("execution_layer", "not_recorded"),
            "scope": run.manifest.get("scope"),
        },
        "assessment_method": "Manual rubric findings only; no automatic answer grading or score averages.",
        "review_supplied": supplied, "reviewer": review["reviewer"],
        "overall": aggregate(cases),
        "layers": {layer: aggregate([case for case in cases if case["evaluation_layer"] == layer])
                   for layer in ("service", "desktop")},
        "cases": cases,
    }


def write_new_json(path: Path, value: dict) -> None:
    encoded = json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n"
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(encoded)


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare = commands.add_parser("prepare", help="Write a blank review; never overwrite an existing file")
    prepare.add_argument("--run", required=True, type=Path)
    prepare.add_argument("--out", required=True, type=Path)
    report = commands.add_parser("summarize", help="Report execution gaps and explicit review findings")
    report.add_argument("--run", required=True, type=Path)
    report.add_argument("--review", type=Path)
    report.add_argument("--out", type=Path)
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "prepare":
            write_new_json(arguments.out, create_review(arguments.run))
            print(json.dumps({"status": "review_template_created", "reviewed_cases": 0}))
        else:
            review = load_json(arguments.review)[0] if arguments.review else None
            summary = summarize(arguments.run, review)
            if arguments.out:
                write_new_json(arguments.out, summary)
            else:
                print(json.dumps(summary, ensure_ascii=False, allow_nan=False))
    except (OSError, ValueError, TypeError) as error:
        parser.exit(1, f"Evaluation review failed: {error}\n")


if __name__ == "__main__":
    main()
