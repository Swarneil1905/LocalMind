"""Exercise real local STT -> chat -> TTS with synthetic clips, without a microphone.

Use an isolated LOCALMIND_DATA_DIR containing the installed speech runtime/models.
Outputs are local evidence, not human microphone or speaker acceptance results.
"""

import argparse
import asyncio
import base64
import io
import json
import os
import re
import sys
import time
import uuid
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "services/ai"))


async def run(args):
    if not os.environ.get("LOCALMIND_DATA_DIR"):
        raise SystemExit(
            "Set LOCALMIND_DATA_DIR to an isolated evaluation profile first."
        )
    from app import voice
    from app.api.routes.chat import ChatRequest, _stream_ollama
    from app.db import conversations

    args.out.mkdir(parents=True, exist_ok=True)
    speaker = (await voice.jobs().stock_voices())[0]["id"]
    cases = json.loads((ROOT / "evaluation/voice/utterances.json").read_text())[:10]
    conversation = conversations.create_conversation(
        "Synthetic local voice verification"
    )
    report = {
        "synthetic": True,
        "microphone_tested": False,
        "speaker_playback_tested": False,
        "model": args.model,
        "conversation_id": conversation["id"],
        "turns": [],
    }
    history = []
    for case in cases:
        turn_id = str(uuid.uuid4())
        started = time.perf_counter()
        encoded = base64.b64encode(
            (args.clips / (case["id"] + ".wav")).read_bytes()
        ).decode()
        transcript = await voice.jobs().run(
            turn_id, lambda encoded=encoded: voice.transcribe(encoded)
        )
        stt_seconds = time.perf_counter() - started
        answer, done, audio_task = "", False, None

        async def speak(text, turn_id=turn_id, started=started):
            raw = await voice.jobs().run(
                turn_id, lambda: voice.sapi("speak", voice=speaker, text=text)
            )
            with wave.open(io.BytesIO(base64.b64decode(raw)), "rb") as audio:
                assert audio.getnframes() > 0
            return time.perf_counter() - started

        try:
            request = ChatRequest(
                message=transcript,
                model=args.model,
                profile="speed",
                history=history,
                conversation_id=conversation["id"],
                connector_context_enabled=False,
                operation_id=turn_id,
            )
            async for event in _stream_ollama(request):
                record = json.loads(event.removeprefix("data: ").strip())
                if record["type"] == "error":
                    raise RuntimeError(record["content"])
                if record["type"] == "token":
                    answer += record.get("content", "")
                    # This probe measures first sentence synthesis, not the UI formatter.
                    sentence = re.match(r"^([^`$]{12,400}?[.!?])\s", answer)
                    if audio_task is None and sentence:
                        audio_task = asyncio.create_task(speak(sentence[1]))
                if record["type"] == "done":
                    done = True
            if not done or not answer.strip():
                raise RuntimeError("The local model did not finish a text reply.")
            if audio_task is None:
                audio_task = asyncio.create_task(speak(answer[:400]))
            first_audio = await audio_task
            conversations.save_turn(conversation["id"], transcript, answer)
            history.extend(
                [
                    {"role": "user", "content": transcript},
                    {"role": "assistant", "content": answer},
                ]
            )
            stored = conversations.get_messages(conversation["id"])
            assert len(stored) == len(history)
            report["turns"].append(
                {
                    "case": case["id"],
                    "turn_id": turn_id,
                    "transcript": transcript,
                    "answer": answer,
                    "stt_seconds": round(stt_seconds, 3),
                    "first_wav_seconds": round(first_audio, 3),
                    "total_seconds": round(time.perf_counter() - started, 3),
                    "stored_messages": len(stored),
                }
            )
            (args.out / "journey.json").write_text(
                json.dumps(report, indent=2), encoding="utf-8"
            )
            print(
                case["id"], "passed", report["turns"][-1]["total_seconds"], flush=True
            )
        finally:
            if audio_task and not audio_task.done():
                audio_task.cancel()
                await asyncio.gather(audio_task, return_exceptions=True)
    assert not list((voice.root() / "audio").glob("capture-*"))
    print(
        "10 turns completed; 20 text messages persisted; temporary captures removed.",
        flush=True,
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="qwen2.5:1.5b")
    parser.add_argument(
        "--clips", type=Path, default=ROOT / "artifacts/evaluation/voice/benchmark"
    )
    parser.add_argument(
        "--out", type=Path, default=ROOT / "artifacts/evaluation/voice/journey"
    )
    asyncio.run(run(parser.parse_args()))
