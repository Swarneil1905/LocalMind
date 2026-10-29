import asyncio
import os
from uuid import UUID

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field

from app import voice

router = APIRouter(prefix="/voice", tags=["voice"])


class Turn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    turn_id: UUID


class Recording(Turn):
    audio: str = Field(min_length=44, max_length=1_280_060)


class Speech(Turn):
    text: str = Field(min_length=1, max_length=600)
    voice: str = Field(min_length=1, max_length=150)


async def execute(turn, work):
    if os.environ.get("LOCALMIND_VOICE_PILOT") != "1":
        raise HTTPException(403, "Voice is disabled in this beta pending human microphone and playback checks.")
    try:
        return await voice.jobs().run(str(turn), work)
    except asyncio.CancelledError:
        raise HTTPException(409, "Voice turn cancelled.") from None
    except (voice.VoiceError, OSError) as error:
        raise HTTPException(400, str(error)) from None


@router.get("/status")
async def status():
    if os.environ.get("LOCALMIND_VOICE_PILOT") != "1":
        return {"supported": False, "stt_ready": False, "engine": "Voice beta gate", "voices": [],
                "disabled_reason": "Voice is disabled in this beta. Human microphone, playback and ten multi-turn session checks are pending. Text chat remains available.",
                "install": {"phase": "disabled", "downloaded": 0, "total": 0}}
    return await voice.jobs().status()


@router.post("/install")
async def install():
    if os.environ.get("LOCALMIND_VOICE_PILOT") != "1":
        raise HTTPException(403, "Voice installation is disabled in this beta.")
    if not voice.supported():
        raise HTTPException(400, "Local voice setup currently supports Windows x64.")
    voice.start_install()
    return {"started": True}


@router.post("/cancel")
async def cancel(request: Turn):
    voice.jobs().cancel(str(request.turn_id))
    return {"cancelled": True}


@router.post("/transcribe")
async def transcribe(request: Recording):
    async def work():
        return {"turn_id": str(request.turn_id), "text": await voice.transcribe(request.audio)}

    return await execute(request.turn_id, work)


@router.post("/speak")
async def speak(request: Speech):
    async def work():
        voices = await voice.jobs().stock_voices()
        if request.voice not in {item["id"] for item in voices}:
            raise voice.VoiceError("The selected voice is unavailable. Choose an installed voice.")
        audio = (await voice.sapi("speak", text=request.text, voice=request.voice)).decode().strip()
        return {"turn_id": str(request.turn_id), "audio": audio}

    return await execute(request.turn_id, work)
