"""Reproducible local speech benchmark. Synthetic audio is not human acceptance.

Install psutil for CPU/RSS measurements. All generated audio and results belong
in ignored artifacts/. Runtime/models must already be installed in the profile.
"""

import argparse
import asyncio
import base64
import hashlib
import io
import json
import math
import random
import re
import statistics
import struct
import sys
import time
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "services/ai"))


def words(text):
    return re.findall(r"[a-z0-9]+", text.lower())


def word_errors(reference, hypothesis):
    expected, observed = words(reference), words(hypothesis)
    row = list(range(len(observed) + 1))
    for index, word in enumerate(expected, 1):
        current = [index]
        for column, other in enumerate(observed, 1):
            current.append(
                min(current[-1] + 1, row[column] + 1, row[column - 1] + (word != other))
            )
        row = current
    return row[-1], len(expected)


def fixture_wave(data, noise=False):
    with wave.open(io.BytesIO(data), "rb") as source:
        assert source.getnchannels() == 1 and source.getsampwidth() == 2
        rate = source.getframerate()
        samples = struct.unpack(
            f"<{source.getnframes()}h", source.readframes(source.getnframes())
        )
    count = int(len(samples) * 16000 / rate)
    values = []
    rng = random.Random(20261026)
    for index in range(count):
        start, stop = (
            int(index * rate / 16000),
            max(int(index * rate / 16000) + 1, int((index + 1) * rate / 16000)),
        )
        value = sum(samples[start:stop]) / max(1, stop - start)
        values.append(
            max(-32768, min(32767, round(value + (rng.gauss(0, 450) if noise else 0))))
        )
    return wav(values)


def wav(values):
    output = io.BytesIO()
    with wave.open(output, "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(16000)
        target.writeframes(struct.pack(f"<{len(values)}h", *values))
    return output.getvalue()


async def measure(work):
    import psutil

    parent = psutil.Process()
    running, peak, cpu = True, 0, {}

    async def sample():
        nonlocal peak
        while running:
            processes = parent.children(recursive=True)
            rss = 0
            for process in processes:
                try:
                    rss += process.memory_info().rss
                    times = process.cpu_times()
                    cpu[process.pid] = times.user + times.system
                except psutil.Error:
                    pass
            peak = max(peak, rss)
            await asyncio.sleep(0.025)

    observer = asyncio.create_task(sample())
    started = time.perf_counter()
    try:
        value = await work()
    finally:
        elapsed = time.perf_counter() - started
        running = False
        await observer
    return value, {
        "wall_seconds": round(elapsed, 3),
        "peak_child_rss_mb": round(peak / 1024**2, 2),
        "child_cpu_seconds": round(sum(cpu.values()), 3),
        "child_cpu_percent": round(sum(cpu.values()) / elapsed * 100, 1),
    }


async def main(args):
    from app import voice

    cases = json.loads((ROOT / "evaluation/voice/utterances.json").read_text())
    args.out.mkdir(parents=True, exist_ok=True)
    speakers = await voice.jobs().stock_voices()
    if not speakers:
        raise SystemExit(
            "This synthetic benchmark requires installed Windows stock voices."
        )
    tts, clips = [], []
    for index, case in enumerate(cases):
        path = args.out / (case["id"] + ".wav")
        if not path.exists():
            if case["text"]:
                speaker = speakers[index % len(speakers)]["id"]

                async def synth(speaker=speaker, case=case):
                    return await voice.sapi("speak", voice=speaker, text=case["text"])

                encoded, metrics = await measure(synth)
                data = fixture_wave(base64.b64decode(encoded), case["group"] == "noise")
                tts.append({"case": case["id"], "voice": speaker, **metrics})
            else:
                data = wav([0] * 32000)
            path.write_bytes(data)
        clips.append(
            {
                **case,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "path": str(path),
            }
        )
    report = {
        "synthetic": True,
        "human_microphone_tested": False,
        "runtime": voice.WHISPER_BUILD,
        "cpu_threads": 4,
        "gpu": False,
        "tts_engine": "Windows System.Speech stock voices",
        "tts": tts,
        "cache_note": "Every clip starts a new CLI process and reloads weights. First invocation and subsequent OS-cache runs are labeled; no resident warm-engine claim.",
        "cases": clips,
        "models": {},
    }
    for model in args.models:
        rows = []
        for index, case in enumerate(clips):
            encoded = base64.b64encode(Path(case["path"]).read_bytes()).decode()

            async def recognize(encoded=encoded, model=model):
                try:
                    return {
                        "text": await voice.transcribe(encoded, model),
                        "error": None,
                    }
                except voice.VoiceError as error:
                    return {"text": "", "error": str(error)}

            result, metrics = await measure(recognize)
            errors, count = word_errors(case["text"], result["text"])
            rows.append(
                {
                    "case": case["id"],
                    "group": case["group"],
                    **result,
                    **metrics,
                    "word_errors": errors,
                    "reference_words": count,
                    "invocation": "first" if index == 0 else "subsequent",
                }
            )
            print(model, case["id"], metrics["wall_seconds"], result, flush=True)
            report["models"][model] = {"rows": rows}
            (args.out / "report.json").write_text(json.dumps(report, indent=2))
        groups = {}
        for group in {row["group"] for row in rows}:
            subset = [row for row in rows if row["group"] == group]
            total = sum(row["reference_words"] for row in subset)
            groups[group] = {
                "wer": sum(row["word_errors"] for row in subset) / total
                if total
                else None,
                "p50_seconds": statistics.median(row["wall_seconds"] for row in subset),
            }
        times = sorted(row["wall_seconds"] for row in rows if row["group"] != "silence")
        report["models"][model].update(
            groups=groups, p95_seconds=times[math.ceil(len(times) * 0.95) - 1]
        )
        (args.out / "report.json").write_text(json.dumps(report, indent=2))
    print(
        json.dumps(
            {
                model: {key: value for key, value in result.items() if key != "rows"}
                for model, result in report["models"].items()
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--out", type=Path, default=ROOT / "artifacts/evaluation/voice/benchmark"
    )
    parser.add_argument(
        "--models",
        nargs="+",
        choices=["base.en", "small.en"],
        default=["base.en", "small.en"],
    )
    asyncio.run(main(parser.parse_args()))
