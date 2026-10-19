"""Replay a frozen conversation split against an installed local Ollama model.

Run from the repository root. Requires the AI service's Python dependencies.
Each case gets a fresh process/profile; live search and connectors are disabled.
"""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

import capture_environment as inventory
from check_suite import DEFAULT_SUITE, load_suite

ROOT = Path(__file__).resolve().parents[2]
WORKER = Path(__file__).with_name("service_replay.py")


def write_json(path: Path, value: dict) -> None:
    """Publish one complete checkpoint; readers never see half-written JSON."""
    staging = path.with_suffix(path.suffix + ".tmp")
    staging.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n",
                       encoding="utf-8")
    staging.replace(path)


def select_cases(suite: dict, split: str, case_ids: list[str] | None,
                 allow_held_out: bool = False) -> list[dict]:
    if split not in ("development", "held_out"):
        raise ValueError("Choose development or held_out")
    if split == "held_out" and not allow_held_out:
        raise ValueError("Held-out execution requires --allow-held-out; never use its answers for tuning")
    if case_ids is not None and (not case_ids or len(set(case_ids)) != len(case_ids)):
        raise ValueError("Case IDs must be nonempty and unique")
    eligible = [case for case in suite["cases"] if case["split"] == split]
    if case_ids is not None:
        unknown = set(case_ids) - {case["id"] for case in eligible}
        if unknown:
            raise ValueError(f"Cases outside selected split or unknown: {', '.join(sorted(unknown))}")
        eligible = [case for case in eligible if case["id"] in case_ids]
    # Stable corpus order, independent of command-line argument order.
    return sorted(eligible, key=lambda case: case["id"])


def select_model(profile: str, override: str | None, root: Path = ROOT) -> str:
    if override is not None:
        if not inventory.model_name(override):
            raise ValueError("Invalid Ollama model name")
        return override
    mapping = inventory.source_defaults(root)["model_mapping"]
    if mapping.get("status") != "ok" or profile not in mapping.get("values", {}):
        raise ValueError("Cannot read the UI model mapping; specify --model explicitly")
    return mapping["values"][profile]


def model_snapshot(ollama: dict, model: str) -> dict:
    installed = ollama.get("installed_models", {}).get("models", [])
    active = ollama.get("active_models", {}).get("models", [])
    aliases = {model, model + ":latest"} if ":" not in model.rsplit("/", 1)[-1] else {model}
    matches = [item for item in installed if item.get("name") in aliases or item.get("model") in aliases]
    if len(matches) != 1 or not matches[0].get("digest") or not matches[0].get("size", 0):
        raise ValueError(f"{model} is not an identifiable installed model; install it separately before evaluating")
    if ollama.get("selected_model_metadata", {}).get("status") != "ok":
        raise ValueError("Ollama could not describe the selected model")
    selected = matches[0]
    metadata = ollama["selected_model_metadata"]
    # Cloud aliases can have a normal-looking name, size and digest. A request
    # to localhost is not itself proof that the daemon performs local inference.
    if selected.get("is_remote") is True or metadata.get("is_remote") is True:
        raise ValueError("Remote/cloud Ollama models are not allowed in local evaluation")
    if selected.get("is_remote") is not False or metadata.get("is_remote") is not False:
        raise ValueError("Cannot establish whether the selected Ollama model is local")
    loaded = next((item for item in active if item.get("digest") == selected["digest"]), None)
    return {"installed": selected, "loaded_before_run": loaded,
            "loaded_state_known": ollama.get("active_models", {}).get("status") == "ok",
            "metadata": metadata}


def source_hashes(root: Path = ROOT) -> dict:
    """Identify dirty source too, rather than labeling it solely with Git HEAD."""
    files = sorted((root / "services/ai/app").rglob("*.py"))
    files += sorted((root / "scripts/evaluation").glob("*.py"))
    return {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in files}


def base_result(case: dict, model: str, profile: str) -> dict:
    return {"schema_version": 1, "id": case["id"], "split": case["split"],
            "evaluation_layer": case["evaluation_layer"], "severity": case["severity"],
            "model": model, "profile": profile, "status": "not_evaluated", "answer": "",
            "error": None, "truncated": False, "timings": {}, "trace": {},
            "effective_request": None}


def read_checkpoint(path: Path) -> dict:
    try:
        result = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(result, dict) and isinstance(result.get("answer"), str):
            return result
    except (OSError, ValueError):
        pass
    return {"answer": "", "timings": {}, "trace": {}, "effective_request": None}


def execute_case(case: dict, model: str, timeout_seconds: float, clock: str,
                 scratch: Path) -> tuple[dict, bool]:
    """Return result and interruption flag. Kill only this evaluator's child."""
    with tempfile.TemporaryDirectory(prefix=f"{case['id']}-", dir=scratch) as temporary:
        work = Path(temporary)
        request, response = work / "input.json", work / "output.json"
        profile = work / "profile"  # Worker creates it; never reuse an existing profile.
        write_json(request, {"case": case, "model": model, "timeout_seconds": timeout_seconds,
                             "clock": clock, "profile_dir": str(profile.resolve())})
        environment = os.environ.copy()
        environment["LOCALMIND_DATA_DIR"] = str(profile.resolve())
        # Prevent inherited desktop credentials from entering this process.
        environment.pop("LOCALMIND_TOKEN", None)
        command = [sys.executable, str(WORKER), "--input", str(request), "--output", str(response)]
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        interrupted, failure, code = False, None, None
        with (work / "worker.log").open("wb") as log:
            process = subprocess.Popen(command, cwd=ROOT, env=environment, stdin=subprocess.DEVNULL,
                                       stdout=log, stderr=log, creationflags=flags)
            try:
                code = process.wait(timeout=timeout_seconds + 15)
            except subprocess.TimeoutExpired:
                failure = "Evaluation worker exceeded its hard deadline and was stopped."
                process.kill()
                process.wait()
            except KeyboardInterrupt:
                interrupted = True
                failure = "Evaluation interrupted; the owned worker was stopped."
                process.kill()
                process.wait()
        result = read_checkpoint(response)
        if code and not (code == 1 and result.get("status") == "run_error"
                         and result.get("trace", {}).get("checkpoint_final") is True):
            failure = f"Evaluation worker exited with code {code}. Check the AI service dependencies."
        if failure:
            result.update(status="run_error", error=failure, truncated=True)
        elif result.get("status") not in ("completed", "run_error"):
            result.update(status="run_error", error="Evaluation worker produced no valid final result.",
                          truncated=True)
        return result, interrupted


def run(args: argparse.Namespace) -> int:
    suite, suite_report = load_suite(args.suite)
    cases = select_cases(suite, args.split, args.cases, args.allow_held_out)
    if not math.isfinite(args.timeout_seconds) or not 1 <= args.timeout_seconds <= 1800:
        raise ValueError("Per-case timeout must be between 1 and 1800 seconds")
    model = select_model(args.profile, args.model)
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)  # No overwrite/resume of experiment evidence.
    (out / "results").mkdir()
    started = datetime.now(timezone.utc).isoformat()
    manifest = {"schema_version": 1, "state": "preparing", "started_at_utc": started,
                "clock_utc": started, "split": args.split, "case_ids": [case["id"] for case in cases],
                "suite_sha256": suite_report["sha256"], "profile": args.profile, "model": model,
                "model_override": args.model is not None, "timeout_seconds": args.timeout_seconds,
                "execution_layer": "python_service_generator",
                "scope": "Synthetic fixtures; no desktop UI, auth transport, live web, connectors or real user profile.",
                "held_out_acknowledged": args.allow_held_out if args.split == "held_out" else False,
                "source_sha256": source_hashes()}
    write_json(out / "cases.json", {"schema_version": 1, "cases": cases})
    write_json(out / "manifest.json", manifest)
    for case in cases:
        result = base_result(case, model, args.profile)
        result["error"] = "Desktop state must be evaluated in the app." if case["evaluation_layer"] == "desktop" else "Case has not run."
        write_json(out / "results" / f"{case['id']}.json", result)
    interrupted, errors = False, 0
    try:
        environment = inventory.capture(model, ROOT)
        write_json(out / "environment.json", environment)
        manifest["model_snapshot"] = model_snapshot(environment["ollama"], model)
        manifest["state"] = "running"
        write_json(out / "manifest.json", manifest)
        # All temporary profile state stays within the user-selected run directory.
        with tempfile.TemporaryDirectory(prefix=".workers-", dir=out) as scratch:
            for index, case in enumerate(cases, 1):
                if case["evaluation_layer"] == "desktop":
                    print(f"[{index}/{len(cases)}] {case['id']}: not evaluated (desktop state)", flush=True)
                    continue
                print(f"[{index}/{len(cases)}] {case['id']}: running {model}", flush=True)
                result, interrupted = execute_case(case, model, args.timeout_seconds, started, Path(scratch))
                result = {**base_result(case, model, args.profile), **result}
                write_json(out / "results" / f"{case['id']}.json", result)
                errors += result["status"] == "run_error"
                print(f"  {result['status']}; {len(result['answer'])} answer characters", flush=True)
                if interrupted:
                    break
        manifest["state"] = "interrupted" if interrupted else "completed_with_errors" if errors else "completed"
    except KeyboardInterrupt:
        manifest["state"] = "interrupted"
        interrupted = True
    except (OSError, ValueError) as exc:
        manifest.update(state="run_error", error=str(exc))
        errors += 1
        print(f"Run stopped: {exc}", file=sys.stderr)
    finally:
        manifest["finished_at_utc"] = datetime.now(timezone.utc).isoformat()
        write_json(out / "manifest.json", manifest)
    print(f"Results: {out}")
    print("Answers are unreviewed. Use review_run.py to prepare the human review.")
    return 130 if interrupted else 1 if errors else 0


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", type=Path, default=DEFAULT_SUITE)
    parser.add_argument("--split", choices=("development", "held_out"), default="development")
    parser.add_argument("--allow-held-out", action="store_true", help="Acknowledge evaluation-only use, never prompt tuning")
    parser.add_argument("--cases", nargs="+", help="Optional subset of case IDs, executed in fixed corpus order")
    parser.add_argument("--profile", choices=("speed", "balanced", "boost"), default="balanced")
    parser.add_argument("--model", help="Explicit installed local model override; recorded as a separate condition")
    parser.add_argument("--timeout-seconds", type=float, default=120, help="Overall deadline for each service replay")
    parser.add_argument("--out", type=Path, required=True, help="New experiment directory; must not exist")
    args = parser.parse_args(argv)
    try:
        code = run(args)
    except (OSError, ValueError, TypeError) as exc:
        parser.exit(1, f"Evaluation could not start: {exc}\n")
    raise SystemExit(code)


if __name__ == "__main__":
    main()
