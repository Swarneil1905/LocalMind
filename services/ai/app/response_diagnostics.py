"""Prompt-free response metadata. Timings are measured, not quality scores."""
import math


def response_diagnostics(model, profile, budget, *, elapsed, first_token, terminal):
    def count(key):
        value = terminal.get(key)
        return value if type(value) is int and 0 <= value <= 10**9 else None

    duration = terminal.get("eval_duration")
    tokens = count("eval_count")
    rate = (round(tokens * 1e9 / duration, 2)
            if tokens is not None and type(duration) is int and 0 < duration <= 10**18 else None)
    def seconds(value):
        return round(value, 3) if isinstance(value, (int, float)) and math.isfinite(value) and value >= 0 else None
    def provider_duration(key):
        value = terminal.get(key)
        return seconds(value / 1e9) if type(value) is int and 0 <= value <= 10**18 else None
    return {
        "model": model, "digest": profile.get("digest") if profile else None,
        "preset": profile.get("preset") if profile else None,
        "context_window": budget["context_tokens"],
        "response_tokens": budget["response_reserve_tokens"],
        "history_dropped": budget["history_dropped"],
        "references_dropped": budget["references_dropped"],
        "references_shortened": budget["references_shortened"],
        "total_seconds": seconds(elapsed), "first_token_seconds": seconds(first_token),
        "output_tokens": tokens, "input_tokens": count("prompt_eval_count"),
        "tokens_per_second": rate,
        "model_load_seconds": provider_duration("load_duration"),
        "prompt_eval_seconds": provider_duration("prompt_eval_duration"),
        "stop_reason": terminal.get("done_reason") if terminal.get("done_reason") in ("stop", "length") else "unknown",
    }
