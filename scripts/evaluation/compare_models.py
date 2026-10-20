"""Compare installed local models on frozen development cases and cold/warm probes.

This records execution and timings, never a human quality approval or a default
model change. Source changes during the comparison invalidate the remaining run.
"""

import argparse
import asyncio
from collections import Counter
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import statistics
import sys
import tempfile
import time
from urllib.request import build_opener, ProxyHandler, Request

import capture_environment as inventory
from check_suite import DEFAULT_SUITE, load_suite
import run_suite as runner


MODELS = ("qwen3:4b", "qwen3.5:4b", "qwen3:8b")
CASE_IDS = ("LM-001", "LM-003", "LM-007", "LM-010", "LM-016", "LM-018", "LM-019")
PROBE_COUNT = 3
PROBE = {
    "id": "PROBE-001", "split": "development", "evaluation_layer": "service",
    "severity": "normal", "web_search": False, "evidence": [],
    "messages": [{"role": "user", "content":
                  "In two short sentences, explain how a to-do list differs from a calendar."}],
}
REPLAY_FILES = {
    "run_suite.py", "service_replay.py", "capture_environment.py", "check_suite.py",
    "compare_models.py",
}


def profile_api():
    service = str(runner.ROOT / "services" / "ai")
    if service not in sys.path:
        sys.path.insert(0, service)
    # Import defines storage helpers but never opens a profile. Only inspect,
    # default_profile and validate_profile are used; never resolve/update.
    from app import model_profiles
    return model_profiles


def matched_profile(metadata: dict, api=None) -> dict:
    api = api or profile_api()
    disable_thinking = any(type(value) is bool and value is False
                           for value in metadata["thinking_values"])
    proposed = {
        **api.default_profile(metadata, "balanced"),
        "context_window": 8192, "response_tokens": 1024, "temperature": 0.2,
        "top_p": 0.9, "top_k": 40, "think": False if disable_thinking else None,
    }
    return api.validate_profile(proposed, metadata)


def comparison_sources() -> dict:
    return {
        name: digest for name, digest in runner.source_hashes().items()
        if name.startswith("services/ai/app/")
        or (name.startswith("scripts/evaluation/") and Path(name).name in REPLAY_FILES)
    }


def require_unchanged(expected: dict) -> None:
    if comparison_sources() != expected:
        raise ValueError("Production/replay source changed during comparison; use a new run directory")


def process_snapshot() -> dict:
    try:
        result = inventory.models_metadata(inventory.request_metadata("ps"), active=True)
        for model in result["models"]:
            if "size" in model and "size_vram" in model:
                model["estimated_non_vram_bytes"] = max(0, model["size"] - model["size_vram"])
        result["memory_note"] = "Ollama allocation estimates, not measured process RAM or peak GPU usage."
        return result
    except (OSError, ValueError, TypeError) as exc:
        return {"status": "unavailable", "reason": type(exc).__name__, "models": []}


def selected_loaded(snapshot: dict, metadata: dict, context: int | None = None) -> bool:
    if snapshot.get("status") != "ok":
        return False
    return any(
        row.get("digest") == metadata["digest"]
        and (context is None or row.get("context_length") == context)
        for row in snapshot.get("models", [])
    )


def unload_model(model: str) -> dict:
    """Unload only the selected candidate; no prompt or inference request."""
    if model not in MODELS:
        raise ValueError("Only comparison candidates may be unloaded")
    payload = json.dumps({"model": model, "keep_alive": 0, "stream": False}).encode("utf-8")
    request = Request("http://127.0.0.1:11434/api/generate", data=payload,
                      headers={"Content-Type": "application/json"})
    opener = build_opener(ProxyHandler({}), inventory.NoRedirect())
    with opener.open(request, timeout=15) as response:
        raw = response.read(65537)
    if len(raw) > 65536:
        raise ValueError("Oversized unload response")
    result = json.loads(raw)
    if not isinstance(result, dict) or result.get("done") is not True or result.get("error"):
        raise ValueError("Ollama did not confirm model unload")
    return {"status": "requested", "model": model}


def confirm_unloaded(metadata: dict) -> dict:
    unload_model(metadata["model"])
    for attempt in range(10):
        snapshot = process_snapshot()
        if snapshot.get("status") != "ok":
            raise ValueError("Cannot verify cold state because Ollama /ps is unavailable")
        if not selected_loaded(snapshot, metadata):
            return snapshot
        if attempt < 9:
            time.sleep(0.2)
    raise ValueError("The selected model remained loaded after its unload request")


def sanitized_error(exc: Exception) -> str:
    return f"{type(exc).__name__}: {exc}" if isinstance(exc, ValueError) else type(exc).__name__


def probe_error(reason: str) -> dict:
    return {"status": "run_error", "answer": "", "error": reason, "truncated": True,
            "timings": {}, "trace": {}, "effective_request": None}


def execute_observed(case: dict, metadata: dict, profile: dict, timeout: float,
                     clock: str, scratch: Path, sources: dict) -> tuple[dict, bool]:
    require_unchanged(sources)
    before = process_snapshot()
    started = time.monotonic()
    result, interrupted = runner.execute_case(
        case, metadata["model"], timeout, clock, scratch, generation_profile=profile
    )
    result["observation"] = {
        "before": before, "after": process_snapshot(),
        "worker_wall_seconds": time.monotonic() - started,
    }
    # Preserve the result even when source drift prevents it being compared.
    result["source_unchanged"] = comparison_sources() == sources
    return result, interrupted


def metric_summary(results: list[dict]) -> dict:
    successful = [row for row in results if row.get("status") == "completed"
                  and not row.get("truncated") and row.get("source_unchanged") is True]
    metrics = {}
    for name, get_value in (
        ("first_content_seconds", lambda row: row.get("timings", {}).get("first_content_seconds")),
        ("total_seconds", lambda row: row.get("timings", {}).get("total_seconds")),
        ("ollama_load_seconds", lambda row: (
            row.get("trace", {}).get("ollama_metrics", {}).get("load_duration", 0) / 1e9
            if "load_duration" in row.get("trace", {}).get("ollama_metrics", {}) else None)),
    ):
        values = [get_value(row) for row in successful]
        values = [value for value in values if type(value) in (int, float) and math.isfinite(value)]
        metrics[name] = {
            "n": len(values), "median": statistics.median(values) if values else None,
            "min": min(values) if values else None, "max": max(values) if values else None,
        }
    return {
        "requested": len(results), "execution": dict(Counter(row["status"] for row in results)),
        "usable_measurements": len(successful), "metrics": metrics,
    }


def summarize(out: Path, manifest: dict) -> dict:
    conditions = {}
    for model in manifest["models"]:
        directory = out / model.replace(":", "-")
        probes = {
            mode: [json.loads(path.read_bytes()) for path in sorted((directory / "probes").glob(f"{mode}-*.json"))]
            for mode in ("cold", "warm")
        }
        quality = [json.loads(path.read_bytes()) for path in sorted((directory / "quality/results").glob("*.json"))]
        conditions[model] = {
            "cold": metric_summary(probes["cold"]), "warm": metric_summary(probes["warm"]),
            "quality_execution": dict(Counter(row["status"] for row in quality)),
            "quality_selected": len(CASE_IDS), "quality_judgment": "not_human_reviewed",
        }
    return {
        "schema_version": 1, "state": manifest["state"], "error": manifest.get("error"),
        "conditions": conditions,
        "default_decision": "Retain the existing default; no automated quality approval or settings changes.",
        "limitations": [
            "Seven development cases once per model; no held-out evaluation or statistical quality estimate.",
            "Three cold and three warm probes per model; process cold is not OS disk-cache cold.",
            "Thinking is disabled only when the installed model explicitly advertises boolean false support.",
            "Matched generation limits do not equal matched generated lengths or identical architecture.",
            "Ollama /ps estimates allocations; it is not process-RAM or peak-VRAM instrumentation.",
            "Service generator only; no native UI, IPC, real account content, or persisted tool actions.",
        ],
    }


def run(out: Path, *, timeout: float = 180.0, models: tuple[str, ...] = MODELS) -> int:
    if not models or len(set(models)) != len(models) or set(models) - set(MODELS):
        raise ValueError("Choose unique installed comparison candidates")
    if not math.isfinite(timeout) or not 1 <= timeout <= 1800:
        raise ValueError("Per-trial timeout must be between 1 and 1800 seconds")
    suite, suite_report = load_suite(DEFAULT_SUITE)
    cases = runner.select_cases(suite, "development", list(CASE_IDS))
    out.mkdir(parents=True, exist_ok=False)
    started = datetime.now(timezone.utc).isoformat()
    sources = comparison_sources()
    manifest = {
        "schema_version": 1, "kind": "installed_model_comparison", "state": "preparing",
        "started_at_utc": started, "clock_utc": started, "models": list(models),
        "case_ids": list(CASE_IDS), "split": "development", "suite_sha256": suite_report["sha256"],
        "source_sha256": sources, "probe_repeats": PROBE_COUNT, "timeout_seconds": timeout,
        "default_changed": False, "human_review": "pending",
    }
    runner.write_json(out / "manifest.json", manifest)
    quality_manifests = {}
    for model in models:
        directory = out / model.replace(":", "-")
        (directory / "probes").mkdir(parents=True)
        (directory / "quality/results").mkdir(parents=True)
        quality = {
            "schema_version": 1, "state": "preparing", "started_at_utc": started,
            "clock_utc": started, "split": "development", "case_ids": list(CASE_IDS),
            "suite_sha256": suite_report["sha256"], "model": model, "profile": "balanced",
            "model_override": True, "execution_layer": "python_service_generator",
            "source_sha256": sources, "scope": "Frozen development fixtures; no real user profile.",
        }
        quality_manifests[model] = quality
        runner.write_json(directory / "quality/manifest.json", quality)
        runner.write_json(directory / "quality/cases.json", {"schema_version": 1, "cases": cases})
        for case in cases:
            pending = runner.base_result(case, model, "balanced")
            pending["error"] = "Comparison case has not run."
            runner.write_json(directory / f"quality/results/{case['id']}.json", pending)
        for mode in ("cold", "warm"):
            for repeat in range(1, PROBE_COUNT + 1):
                pending = {**probe_error("Probe has not run."), "status": "not_evaluated",
                           "mode": mode, "repeat": repeat, "model": model}
                runner.write_json(directory / f"probes/{mode}-{repeat}.json", pending)
    errors, interrupted = 0, False
    try:
        api = profile_api()
        metadata = {model: asyncio.run(api.inspect_model(model))
                    for model in dict.fromkeys([*models, "qwen3:8b"])}
        profiles = {model: matched_profile(metadata[model], api) for model in models}
        rollback = {
            "model": "qwen3:8b", "digest": metadata["qwen3:8b"]["digest"],
            "mode": "legacy service request without a saved generation profile",
            "context_window": 16384, "response_tokens": 2048, "temperature": 0.2,
            "think": None, "top_p": "model default", "top_k": "model default",
            "status": "recorded rollback configuration; not a matched benchmark condition",
        }
        runner.write_json(out / "rollback.json", rollback)
        manifest.update(state="running", metadata=metadata, generation_profiles=profiles)
        runner.write_json(out / "manifest.json", manifest)
        with tempfile.TemporaryDirectory(prefix=".workers-", dir=out) as temporary:
            scratch = Path(temporary)
            for model in models:
                print(f"{model}: metadata captured; beginning cold/warm probes", flush=True)
                directory = out / model.replace(":", "-")
                environment = inventory.capture(model, runner.ROOT)
                runner.write_json(directory / "environment.json", environment)
                snapshot = runner.model_snapshot(environment["ollama"], model)
                if snapshot["installed"]["digest"] != metadata[model]["digest"]:
                    raise ValueError("Installed model digest changed before this condition")
                runner.write_json(directory / "profile.json", profiles[model])
                condition_error = False
                for mode in ("cold", "warm"):
                    for repeat in range(1, PROBE_COUNT + 1):
                        require_unchanged(sources)
                        try:
                            state = confirm_unloaded(metadata[model]) if mode == "cold" else process_snapshot()
                            if mode == "warm" and not selected_loaded(state, metadata[model], 8192):
                                raise ValueError("Warm model/context was not loaded; this is not a warm measurement")
                            result, interrupted = execute_observed(
                                PROBE, metadata[model], profiles[model], timeout, started, scratch, sources
                            )
                        except (OSError, ValueError) as exc:
                            result = probe_error(sanitized_error(exc))
                        result.update(mode=mode, repeat=repeat, model=model)
                        runner.write_json(directory / f"probes/{mode}-{repeat}.json", result)
                        errors += result["status"] != "completed"
                        print(f"  {mode} {repeat}: {result['status']}; "
                              f"first content {result.get('timings', {}).get('first_content_seconds')}", flush=True)
                        if interrupted:
                            raise KeyboardInterrupt
                        require_unchanged(sources)
                quality_manifests[model]["state"] = "running"
                runner.write_json(directory / "quality/manifest.json", quality_manifests[model])
                for case in cases:
                    print(f"{model}: {case['id']}", flush=True)
                    result, interrupted = execute_observed(
                        case, metadata[model], profiles[model], timeout, started, scratch, sources
                    )
                    result = {**runner.base_result(case, model, "balanced"), **result}
                    runner.write_json(directory / f"quality/results/{case['id']}.json", result)
                    errors += result["status"] != "completed"
                    condition_error |= result["status"] != "completed"
                    if interrupted:
                        raise KeyboardInterrupt
                    require_unchanged(sources)
                quality_manifests[model].update(
                    state="completed_with_errors" if condition_error else "completed",
                    finished_at_utc=datetime.now(timezone.utc).isoformat(),
                    generation_profile=profiles[model],
                )
                runner.write_json(directory / "quality/manifest.json", quality_manifests[model])
                # Release this condition's model before the next candidate.
                confirm_unloaded(metadata[model])
                if asyncio.run(api.inspect_model(model))["digest"] != metadata[model]["digest"]:
                    raise ValueError("Installed model digest changed during this condition")
        manifest["state"] = "completed_with_errors" if errors else "completed"
    except KeyboardInterrupt:
        interrupted = True
        manifest.update(state="interrupted", error="Comparison interrupted; remaining trials were not evaluated.")
    except Exception as exc:
        manifest.update(state="run_error", error=sanitized_error(exc))
        errors += 1
    finally:
        manifest["finished_at_utc"] = datetime.now(timezone.utc).isoformat()
        manifest["source_unchanged"] = comparison_sources() == sources
        runner.write_json(out / "manifest.json", manifest)
        for model, quality in quality_manifests.items():
            if quality["state"] in {"preparing", "running"}:
                quality.update(state=manifest["state"], error=manifest.get("error"),
                               finished_at_utc=manifest["finished_at_utc"])
                runner.write_json(out / model.replace(":", "-") / "quality/manifest.json", quality)
        runner.write_json(out / "summary.json", summarize(out, manifest))
    print(json.dumps({"state": manifest["state"], "errors": errors,
                      "default_changed": False, "human_review": "pending"}), flush=True)
    return 130 if interrupted else 1 if errors else 0


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=180)
    parser.add_argument("--models", choices=MODELS, nargs="+", default=list(MODELS))
    args = parser.parse_args(argv)
    try:
        code = run(args.out, timeout=args.timeout_seconds, models=tuple(args.models))
    except (OSError, ValueError) as exc:
        parser.exit(1, f"Comparison could not start: {sanitized_error(exc)}\n")
    raise SystemExit(code)


if __name__ == "__main__":
    main()
