import asyncio
import base64
import io
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock, patch
from uuid import uuid4
import wave

import httpx
from fastapi import FastAPI

from app import voice
from app.api.routes.voice import router
from app.api.middleware.auth import verify_bearer_token


def recording(value=1000, rate=16000, count=3200):
    target = io.BytesIO()
    with wave.open(target, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(rate)
        audio.writeframes(struct.pack(f"<{count}h", *([value] * count)))
    return base64.b64encode(target.getvalue()).decode()


class AudioValidationTests(unittest.TestCase):
    def test_valid_audio_and_silence(self):
        self.assertTrue(voice.decode_audio(recording()).startswith(b"RIFF"))
        with self.assertRaisesRegex(voice.VoiceError, "No speech"):
            voice.decode_audio(recording(0))

    def test_malformed_truncated_oversized_and_wrong_rate(self):
        values = [
            "not base64",
            base64.b64encode(b"RIFFjunk").decode(),
            "A" * 1_280_061,
            recording(rate=48000),
            recording(count=480001),
        ]
        raw = base64.b64decode(recording())
        values.append(base64.b64encode(raw[:-2]).decode())
        for value in values:
            with self.subTest(value=value[:30]), self.assertRaises(voice.VoiceError):
                voice.decode_audio(value)

    def test_recovery_only_deletes_marked_owned_audio(self):
        with (
            tempfile.TemporaryDirectory() as directory,
            patch.dict(os.environ, {"LOCALMIND_DATA_DIR": directory}),
        ):
            folder = voice.cleanup_audio()
            owned = folder / "capture-owned"
            owned.mkdir()
            (owned / ".localmind-audio").write_text("v1")
            (owned / "recording.wav").write_bytes(b"private")
            other = folder / "capture-other"
            other.mkdir()
            (other / "recording.wav").write_bytes(b"keep")
            voice.cleanup_audio()
            self.assertFalse(owned.exists())
            self.assertEqual((other / "recording.wav").read_bytes(), b"keep")

    def test_unreadable_or_locked_capture_does_not_block_startup(self):
        with (
            tempfile.TemporaryDirectory() as directory,
            patch.dict(os.environ, {"LOCALMIND_DATA_DIR": directory}),
        ):
            folder = voice.cleanup_audio()
            broken = folder / "capture-broken"
            broken.mkdir()
            (broken / ".localmind-audio").write_bytes(b"\xff\xff")
            voice.cleanup_audio()
            self.assertTrue(broken.exists())
            (broken / ".localmind-audio").write_bytes(b"v1")
            with patch.object(Path, "unlink", side_effect=PermissionError("locked")):
                voice.cleanup_audio()
            self.assertTrue((broken / ".localmind-audio").exists())


class VoiceRuntimeTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancel_kills_child_and_releases_waiters(self):
        started = asyncio.Event()

        async def wait():
            started.set()
            return await voice.process([sys.executable, "-c", "import time; time.sleep(30)"])

        jobs = voice.VoiceJobs()
        task = asyncio.create_task(jobs.run("first", wait))
        await started.wait()
        jobs.cancel("first")
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(await jobs.run("second", AsyncMock(return_value="ready")), "ready")
        with self.assertRaisesRegex(voice.VoiceError, "cancelled"):
            await jobs.run("first", AsyncMock())

    async def test_only_one_speech_job_runs_and_cancelled_waiter_never_starts(self):
        jobs = voice.VoiceJobs()
        entered, release = asyncio.Event(), asyncio.Event()

        async def first():
            entered.set()
            await release.wait()
            return "one"

        owner = asyncio.create_task(jobs.run("one", first))
        await entered.wait()
        work = AsyncMock(return_value="two")
        waiter = asyncio.create_task(jobs.run("two", work))
        await asyncio.sleep(0)
        jobs.cancel("two")
        release.set()
        self.assertEqual(await owner, "one")
        with self.assertRaises(asyncio.CancelledError):
            await waiter
        work.assert_not_called()

    async def test_cancel_before_request_blocks_late_work(self):
        jobs = voice.VoiceJobs()
        jobs.cancel("late")
        work = AsyncMock()
        with self.assertRaises(voice.VoiceError):
            await jobs.run("late", work)
        work.assert_not_called()

    async def test_transcription_uses_cpu_and_removes_private_audio(self):
        with (
            tempfile.TemporaryDirectory() as directory,
            patch.dict(os.environ, {"LOCALMIND_DATA_DIR": directory}),
        ):
            voice.runtime_path().parent.mkdir()
            voice.runtime_path().touch()
            voice.model_path().touch()
            observed = []

            async def engine(args, **kwargs):
                observed.extend(args)
                output = Path(args[args.index("-of") + 1]).with_suffix(".json")
                output.write_text(json.dumps({"transcription": [{"text": " Test words. "}]}))
                return b""

            with patch("app.voice.process", side_effect=engine):
                self.assertEqual(await voice.transcribe(recording()), "Test words.")
            self.assertIn("-ng", observed)
            self.assertEqual(list((voice.root() / "audio").iterdir()), [])
            with patch("app.voice.process", side_effect=asyncio.CancelledError):
                with self.assertRaises(asyncio.CancelledError):
                    await voice.transcribe(recording())
            self.assertEqual(list((voice.root() / "audio").iterdir()), [])

    async def test_hidden_windows_process_and_no_shell(self):
        child = AsyncMock()
        child.returncode = 0
        child.communicate.return_value = (b"result", b"")
        with (
            patch("app.voice.asyncio.create_subprocess_exec", return_value=child) as spawn,
            patch("app.voice.sys.platform", "win32"),
            patch("app.voice.subprocess.CREATE_NO_WINDOW", 0x08000000, create=True),
        ):
            await voice.process(["engine", "literal argument"])
        self.assertEqual(spawn.call_args.kwargs["creationflags"], 0x08000000)
        self.assertNotIn("shell", spawn.call_args.kwargs)

    async def test_timeout_kills_engine(self):
        with self.assertRaisesRegex(voice.VoiceError, "timed out"):
            await voice.process([sys.executable, "-c", "import time; time.sleep(30)"], timeout=0.1)

    async def test_bad_download_checksum_never_publishes_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "weights.bin"
            transport = httpx.MockTransport(lambda request: httpx.Response(200, content=b"bad"))
            client = httpx.AsyncClient(transport=transport)
            with (
                patch("app.voice.httpx.AsyncClient", return_value=client),
                self.assertRaisesRegex(voice.VoiceError, "checksum"),
            ):
                await voice.download("https://example.test/model", path, 3, "0" * 64)
            self.assertFalse(path.exists())
            self.assertFalse(path.with_suffix(".bin.part").exists())

    async def test_routes_require_auth_validate_ids_and_preserve_turn_identity(self):
        app = FastAPI()
        app.middleware("http")(verify_bearer_token)
        app.include_router(router)
        turn = str(uuid4())
        with patch.dict(os.environ, {"LOCALMIND_TOKEN": "test-token", "LOCALMIND_VOICE_PILOT": "1"}):
            async with httpx.AsyncClient(
                transport=httpx.ASGITransport(app=app), base_url="http://test"
            ) as client:
                self.assertEqual((await client.get("/voice/status")).status_code, 401)
                client.headers["Authorization"] = "Bearer test-token"
                self.assertEqual(
                    (
                        await client.post(
                            "/voice/transcribe", json={"turn_id": "bad", "audio": recording()}
                        )
                    ).status_code,
                    422,
                )
                with patch("app.voice.transcribe", AsyncMock(return_value="hello")) as engine:
                    response = await client.post(
                        "/voice/transcribe", json={"turn_id": turn, "audio": recording()}
                    )
                    self.assertEqual(response.json(), {"turn_id": turn, "text": "hello"})
                    await client.post("/voice/cancel", json={"turn_id": turn})
                    self.assertEqual(
                        (
                            await client.post(
                                "/voice/transcribe", json={"turn_id": turn, "audio": recording()}
                            )
                        ).status_code,
                        400,
                    )
                    self.assertEqual(engine.await_count, 1)

    async def test_unknown_stock_voice_never_runs_synthesis(self):
        from app.api.routes.voice import Speech, speak
        from fastapi import HTTPException

        with (
            patch.dict(os.environ, {"LOCALMIND_VOICE_PILOT": "1"}),
            patch.object(voice.jobs(), "stock_voices", AsyncMock(return_value=[{"id": "stock"}])),
            patch("app.voice.sapi", AsyncMock()) as synth,
        ):
            with self.assertRaises(HTTPException):
                await speak(Speech(turn_id=uuid4(), text="hello", voice="missing"))
            synth.assert_not_called()
