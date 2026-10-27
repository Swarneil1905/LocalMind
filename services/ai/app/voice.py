"""Optional, CPU-only local speech. Audio is never sent to a network service."""

import asyncio
import base64
from collections import OrderedDict
import hashlib
import io
import json
import logging
import os
from pathlib import Path
import platform
import struct
import subprocess
import sys
import tempfile
import time
import wave
from weakref import WeakKeyDictionary
import zipfile

import httpx

from app.storage import data_path

WHISPER_BUILD = "b5454"
RUNTIME_URL = (
    f"https://github.com/ggml-org/whisper.cpp/releases/download/{WHISPER_BUILD}/whisper-bin-x64.zip"
)
RUNTIME_SHA = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb"
MODELS = {
    "base.en": (147964211, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"),
    "small.en": (487614201, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"),
}
MAX_AUDIO = 960_044  # 30 seconds, mono 16 kHz PCM16 plus a canonical WAV header.


class VoiceError(ValueError):
    pass


def root():
    path = data_path("voice")
    path.mkdir(parents=True, exist_ok=True)
    return path


def supported():
    return sys.platform == "win32" and platform.machine().lower() in {"amd64", "x86_64"}


def runtime_path():
    return root() / "whisper" / "whisper-cli.exe"


def model_path(name="base.en"):
    if name not in MODELS:
        raise VoiceError("Unknown speech model.")
    return root() / f"ggml-{name}.bin"


def cleanup_audio():
    """Remove only known files in marked, profile-owned interrupted captures."""
    folder = root() / "audio"
    folder.mkdir(exist_ok=True)
    resolved = folder.resolve()
    for child in folder.glob("capture-*"):
        if child.is_symlink() or not child.is_dir() or child.resolve().parent != resolved:
            continue
        try:
            marker = child / ".localmind-audio"
            if not marker.is_file() or marker.is_symlink():
                continue
            with marker.open("rb") as source:
                if source.read(3) != b"v1":
                    continue
            for name in ("recording.wav", "transcript.json", ".localmind-audio"):
                (child / name).unlink(missing_ok=True)
            child.rmdir()
        except OSError:
            pass  # Leave locked or unknown files alone; do not block text chat startup.
    return folder


def powershell():
    return str(
        Path(os.environ.get("SystemRoot", r"C:\Windows"))
        / "System32/WindowsPowerShell/v1.0/powershell.exe"
    )


# Input is JSON on stdin, never interpolated into command source. Stock voices
# remain part of Windows; LocalMind neither redistributes nor clones them.
SPEECH_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Speech
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
  if ($request.operation -eq 'voices') {
    $voices = @($synth.GetInstalledVoices() | Where-Object Enabled | ForEach-Object {
      @{ id = $_.VoiceInfo.Name; name = $_.VoiceInfo.Name; language = $_.VoiceInfo.Culture.Name }
    })
    ConvertTo-Json -InputObject $voices -Compress
  } else {
    $synth.SelectVoice($request.voice)
    $stream = New-Object System.IO.MemoryStream
    try {
      $synth.SetOutputToWaveStream($stream)
      $synth.Speak([string]$request.text)
      $synth.SetOutputToNull()
      [Console]::Write([Convert]::ToBase64String($stream.ToArray()))
    } finally { $stream.Dispose() }
  }
} finally { $synth.Dispose() }
"""


async def process(args, *, input_bytes=None, timeout=90):
    kwargs = {"creationflags": subprocess.CREATE_NO_WINDOW} if sys.platform == "win32" else {}
    spawning = asyncio.create_task(
        asyncio.create_subprocess_exec(
            *args,
            stdin=asyncio.subprocess.PIPE
            if input_bytes is not None
            else asyncio.subprocess.DEVNULL,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            **kwargs,
        )
    )
    try:
        child = await asyncio.shield(spawning)
    except asyncio.CancelledError:
        child = await spawning
        if child.returncode is None:
            child.kill()
        await child.wait()
        raise
    try:
        stdout, _stderr = await asyncio.wait_for(child.communicate(input_bytes), timeout)
        if child.returncode:
            raise VoiceError(
                f"The local speech engine exited with code {child.returncode}. Check its installation and try again."
            )
        return stdout
    except asyncio.TimeoutError:
        raise VoiceError("The local speech engine timed out. Try a shorter recording.") from None
    finally:
        if child.returncode is None:
            child.kill()
            await child.wait()


async def sapi(operation, **values):
    if not supported():
        raise VoiceError("Local speech output currently requires Windows x64 stock voices.")
    encoded = base64.b64encode(SPEECH_SCRIPT.encode("utf-16-le")).decode()
    return await process(
        [
            powershell(),
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-WindowStyle",
            "Hidden",
            "-EncodedCommand",
            encoded,
        ],
        input_bytes=json.dumps({"operation": operation, **values}).encode(),
        timeout=30,
    )


def decode_audio(encoded):
    if len(encoded) > 1_280_060:
        raise VoiceError("Recordings must be at most 30 seconds.")
    try:
        data = base64.b64decode(encoded, validate=True)
        if len(data) > MAX_AUDIO:
            raise VoiceError("Recordings must be at most 30 seconds.")
        with wave.open(io.BytesIO(data), "rb") as audio:
            if (
                audio.getnchannels(),
                audio.getsampwidth(),
                audio.getframerate(),
                audio.getcomptype(),
            ) != (1, 2, 16000, "NONE"):
                raise VoiceError("Use mono 16 kHz PCM16 WAV audio.")
            count = audio.getnframes()
            if not 1600 <= count <= 480_000:
                raise VoiceError("Record between 0.1 and 30 seconds.")
            frames = audio.readframes(count)
            if len(frames) != count * 2:
                raise VoiceError("The recording is incomplete.")
    except (ValueError, wave.Error, EOFError) as error:
        raise VoiceError(
            str(error)
            if isinstance(error, VoiceError)
            else "The recording is not valid PCM WAV audio."
        ) from None
    samples = struct.unpack(f"<{len(frames) // 2}h", frames)
    rms = (sum(value * value for value in samples) / len(samples)) ** 0.5 / 32768
    # Reject silence before Whisper, which can hallucinate text from empty audio.
    if rms < 0.002:
        raise VoiceError("No speech was detected. Move closer to your microphone and try again.")
    return data


async def transcribe(encoded, model="base.en"):
    data = decode_audio(encoded)
    executable, weights = runtime_path(), model_path(model)
    if not executable.is_file() or not weights.is_file():
        raise VoiceError("Install local transcription in Voice settings first.")
    # Only files belonging to this request are removed, including on cancellation.
    audio_root = root() / "audio"
    audio_root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="capture-", dir=audio_root) as directory:
        (Path(directory) / ".localmind-audio").write_text("v1")
        audio = Path(directory) / "recording.wav"
        audio.write_bytes(data)
        output = Path(directory) / "transcript"
        await process(
            [
                str(executable),
                "-m",
                str(weights),
                "-f",
                str(audio),
                "-l",
                "en",
                "-t",
                str(min(4, os.cpu_count() or 2)),
                "-ng",
                "-nt",
                "-oj",
                "-of",
                str(output),
            ],
            timeout=120,
        )
        try:
            result = json.loads(output.with_suffix(".json").read_text(encoding="utf-8"))
            text = " ".join(item["text"].strip() for item in result["transcription"]).strip()
        except (OSError, ValueError, KeyError, TypeError):
            raise VoiceError("The transcription engine returned an unreadable result.") from None
        if not text or text.lower() in {"[blank_audio]", "[silence]", "[music]", "(silence)"}:
            raise VoiceError("No speech was detected. Your draft has not changed.")
        return text


class VoiceJobs:
    def __init__(self):
        self.slot = asyncio.Semaphore(1)
        self.tasks = {}
        self.cancelled = OrderedDict()
        self.install_task = None
        self.install_state = {"phase": "idle", "downloaded": 0, "total": 0, "error": None}
        self.voices = None

    async def run(self, turn_id, work):
        if turn_id in self.cancelled:
            raise VoiceError("This voice turn was cancelled. Start a new recording.")
        if turn_id in self.tasks or len(self.tasks) >= 8:
            raise VoiceError("Speech is busy. Stop the current turn or try again shortly.")

        async def queued():
            async with self.slot:
                return await work()

        task = asyncio.create_task(queued())
        self.tasks[turn_id] = task
        try:
            return await task
        finally:
            self.tasks.pop(turn_id, None)

    def cancel(self, turn_id):
        self.cancelled[turn_id] = time.monotonic()
        while len(self.cancelled) > 512:
            self.cancelled.popitem(last=False)
        task = self.tasks.get(turn_id)
        if task:
            task.cancel()

    async def stock_voices(self):
        if not supported():
            return []
        if self.voices is None:
            self.voices = json.loads((await sapi("voices")).decode("utf-8-sig"))
        return self.voices

    async def status(self):
        try:
            voices, speech_error = await self.stock_voices(), None
        except (VoiceError, OSError, ValueError) as error:
            logging.getLogger(__name__).warning("Stock voice discovery failed: %s", error)
            voices, speech_error = (
                [],
                "Windows speech voices are unavailable. Text chat still works.",
            )
        return {
            "supported": supported(),
            "stt_ready": runtime_path().is_file() and model_path().is_file(),
            "engine": f"whisper.cpp {WHISPER_BUILD} / base.en / CPU",
            "voices": voices,
            "speech_error": speech_error,
            "install": dict(self.install_state),
        }

    async def shutdown(self):
        tasks = list(self.tasks.values()) + ([self.install_task] if self.install_task else [])
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)


_jobs = WeakKeyDictionary()


def jobs():
    loop = asyncio.get_running_loop()
    if loop not in _jobs:
        _jobs[loop] = VoiceJobs()
    return _jobs[loop]


async def download(url, destination, expected_size, expected_hash, progress=None):
    staging = destination.with_suffix(destination.suffix + ".part")
    digest, size = hashlib.sha256(), 0
    try:
        async with httpx.AsyncClient(follow_redirects=True, timeout=60) as client:
            async with client.stream("GET", url) as response:
                response.raise_for_status()
                with staging.open("wb") as output:
                    async for chunk in response.aiter_bytes(256 * 1024):
                        size += len(chunk)
                        if size > expected_size:
                            raise VoiceError("The speech download exceeded its expected size.")
                        output.write(chunk)
                        digest.update(chunk)
                        if progress:
                            progress(len(chunk))
        if size != expected_size or digest.hexdigest() != expected_hash:
            raise VoiceError("The speech download failed checksum verification. Please retry.")
        staging.replace(destination)
    finally:
        staging.unlink(missing_ok=True)


async def install(model="base.en", progress=None):
    if not supported():
        raise VoiceError("Automatic speech setup currently supports Windows x64.")
    if model not in MODELS:
        raise VoiceError("Unknown speech model.")
    if not runtime_path().is_file():
        archive = root() / "whisper.zip"
        await download(RUNTIME_URL, archive, 8928640, RUNTIME_SHA, progress)
        try:
            with zipfile.ZipFile(archive) as bundle:
                # Flatten only the CLI and its DLLs from a checksum-pinned official archive.
                with tempfile.TemporaryDirectory(prefix=".whisper-", dir=root()) as staging:
                    output = Path(staging) / "whisper"
                    output.mkdir()
                    for entry in bundle.infolist():
                        name = Path(entry.filename).name
                        if name == "whisper-cli.exe" or name.lower().endswith(".dll"):
                            (output / name).write_bytes(bundle.read(entry))
                    if not (output / "whisper-cli.exe").is_file():
                        raise VoiceError("The runtime archive is missing whisper-cli.")
                    output.rename(root() / "whisper")
            if not runtime_path().is_file():
                raise VoiceError("The speech runtime archive did not contain whisper-cli.")
        finally:
            archive.unlink(missing_ok=True)
    size, digest = MODELS[model]
    if not model_path(model).is_file():
        await download(
            f"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin",
            model_path(model),
            size,
            digest,
            progress,
        )


def start_install():
    state = jobs()
    if state.install_task and not state.install_task.done():
        return

    async def work():
        state.install_state = {
            "phase": "downloading",
            "downloaded": 0,
            "total": 156892851,
            "error": None,
        }

        def progress(size):
            state.install_state["downloaded"] += size

        try:
            await install(progress=progress)
            state.install_state["phase"] = "ready"
        except asyncio.CancelledError:
            state.install_state["phase"] = "idle"
            raise
        except (OSError, httpx.HTTPError, VoiceError, zipfile.BadZipFile):
            state.install_state.update(
                phase="error", error="Speech download failed. Check your connection and retry."
            )

    state.install_task = asyncio.create_task(work())
