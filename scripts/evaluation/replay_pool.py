"""Replay a bounded development sample without treating source answers as truth.

This is a robustness run, not an accuracy score or a training job. Downloaded
reference answers and held-out conversations never enter the selected cases.
"""
import argparse
import asyncio
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import tempfile

from conversation_pool import check_pool, sample_pool
import run_suite as runner


def pending_result(case: dict, model: str) -> dict:
    return {
        "schema_version": 1, "id": case["id"], "model": model,
        "split": "development", "evaluation_layer": "service",
        "severity": case.get("severity", "normal"),
        "status": "not_evaluated", "answer": "", "error": "Case has not run.",
        "truncated": False, "timings": {}, "trace": {}, "effective_request": None,
        "quality_status": "unreviewed", "rubric_status": "not_authored",
    }


def run(args) -> int:
    if type(args.count) is not int or not 1 <= args.count <= 100:
        raise ValueError("Choose 1 to 100 development conversations per run.")
    if not math.isfinite(args.timeout_seconds) or not 1 <= args.timeout_seconds <= 1800:
        raise ValueError("Timeout must be between 1 and 1800 seconds.")
    if not runner.inventory.model_name(args.model) or "://" in args.model:
        raise ValueError("Choose a valid installed local model.")
    checked = check_pool(args.pool)
    # Validate the complete pool before creating any output or contacting Ollama.
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    (out / "results").mkdir()
    sample_pool(args.pool, out / "sample.json", count=args.count, split="development")
    sample = json.loads((out / "sample.json").read_text(encoding="utf-8"))
    clock = datetime.now(timezone.utc).isoformat()
    manifest = {
        "schema_version": 1, "kind": "unscored_pool_replay", "state": "preparing",
        "clock_utc": clock, "model": args.model, "split": "development",
        "pool_manifest_sha256": checked["manifest_sha256"],
        "pool_records_sha256": checked["records_sha256"],
        "sample_sha256": hashlib.sha256((out / "sample.json").read_bytes()).hexdigest(),
        "case_ids": [case["id"] for case in sample["cases"]],
        "source_sha256": runner.source_hashes(), "timeout_seconds": args.timeout_seconds,
        "scope": "Synthetic imported development prompts, isolated Python service; no desktop, live web or connectors.",
        "quality_status": "Unreviewed; source responses are not approved targets. No accuracy or pass rate is computed.",
    }
    runner.write_json(out / "manifest.json", manifest)
    errors = 0
    try:
        for case in sample["cases"]:
            runner.write_json(out / "results" / f"{case['id']}.json",
                              pending_result(case, args.model))
        environment = runner.inventory.capture(args.model, runner.ROOT)
        manifest["model_snapshot"] = runner.model_snapshot(environment["ollama"], args.model)
        runner.write_json(out / "environment.json", environment)
        generation_profile = None
        if args.generation_profile:
            api = runner.profile_api()
            try:
                metadata = asyncio.run(api.inspect_model(args.model))
            except api.httpx.HTTPError as error:
                raise ValueError("Could not inspect the installed model for this profile.") from error
            captured = manifest["model_snapshot"]["installed"]["digest"].removeprefix("sha256:")
            if metadata["digest"].removeprefix("sha256:") != captured:
                raise ValueError("The installed model changed during profile preflight.")
            generation_profile, profile_sha = runner.load_generation_profile(
                args.generation_profile, args.model, getattr(args, "profile", "balanced"), metadata, api=api,
            )
            manifest.update(generation_profile=generation_profile, generation_profile_sha256=profile_sha)
        manifest["state"] = "running"
        runner.write_json(out / "manifest.json", manifest)
        with tempfile.TemporaryDirectory(prefix=".workers-", dir=out) as scratch:
            for case in sample["cases"]:
                # The source's final answer is not present in this sample.
                result, interrupted = runner.execute_case(
                    case, args.model, args.timeout_seconds, clock, Path(scratch),
                    generation_profile=generation_profile,
                )
                result = {**pending_result(case, args.model), "error": None, **result}
                result.update(id=case["id"], split="development", quality_status="unreviewed",
                              rubric_status="not_authored")
                runner.write_json(out / "results" / f"{case['id']}.json", result)
                errors += result["status"] != "completed"
                print(f"{case['id'][:18]}: {result['status']} (unscored)", flush=True)
                if interrupted:
                    manifest.update(state="interrupted", error="Pool replay interrupted.")
                    break
            else:
                manifest["state"] = "completed_with_errors" if errors else "completed"
    except KeyboardInterrupt:
        manifest.update(state="interrupted", error="Pool replay interrupted.")
    except (ValueError, OSError, RuntimeError) as error:
        manifest.update(state="run_error", error=str(error))
        errors += 1
    finally:
        # An interruption can arrive during initial result seeding too. Fill
        # only absent entries; never replace an answer or partial worker result.
        for case in sample["cases"]:
            path = out / "results" / f"{case['id']}.json"
            if not path.exists():
                runner.write_json(path, pending_result(case, args.model))
        manifest["finished_at_utc"] = datetime.now(timezone.utc).isoformat()
        manifest["execution_errors"] = errors
        runner.write_json(out / "manifest.json", manifest)
    return 130 if manifest["state"] == "interrupted" else 0 if manifest["state"] == "completed" else 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pool", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--profile", choices=("speed", "balanced", "boost"), default="balanced")
    parser.add_argument("--count", type=int, default=5)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--generation-profile", type=Path,
                        help="Optional frozen capability profile for the same installed digest")
    args = parser.parse_args(argv)
    try:
        return run(args)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Pool replay failed: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
