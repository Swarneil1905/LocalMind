# Local voice pilot

This feature is in the source tree after v0.7.1. The v0.7.1 installers do not include it. The initial supported configuration is Windows x64, English input, CPU transcription, and installed Windows stock voices. Text chat remains available on every supported desktop platform.

## Use voice

1. Open **Voice** in the sidebar. Install local transcription once. Setup downloads about 157 MB and shows progress; a failed or interrupted download can be retried.
2. Open a conversation and expand **Voice** beneath the composer. Select an input. Device names may become available only after granting microphone access yourself.
3. Optionally select a Windows voice and enable **Speak replies to voice messages**. This setting is off by default.
4. Choose **Start recording**, or hold **Hold to talk**. The microphone opens only after an explicit action. **Finish recording** ends capture; capture also ends at 30 seconds.
5. Edit **Review transcript**, then choose **Add to message**. This appends to existing typed text. Review the combined message and send normally. **Discard recording** preserves the typed draft.
6. **Stop voice**, Escape, or **Interrupt and record** stops the current voice turn. Partial answer text remains in chat. Starting another recording also stops an active text generation.

Keyboard: Ctrl+Shift+V on Windows starts/finishes recording; Escape cancels. The hold button supports Space and Enter. Fields and controls have visible focus indicators, labels, and live status/error announcements.

## Processing and privacy

```text
Explicit capture -> 16 kHz mono PCM WAV -> authenticated local Rust bridge
  -> Python speech queue -> whisper.cpp CPU -> editable transcript
  -> normal chat pipeline / Ollama -> sentence queue -> Windows speech -> playback
```

One UUID identifies a recording, transcription, generation request, and its speech segments. Cancellation invalidates the turn before stopping playback and processes. Late results cannot enter a newer turn. A separate bounded speech queue admits one CPU speech job at a time; whisper.cpp uses `-ng` (no GPU), so it does not compete for the existing foreground/background GPU queue. At most four CPU threads are used. Individual engine processes have timeouts and are killed/reaped on cancellation. Windows child processes start without console windows.

Microphone capture uses an AudioWorklet in the desktop webview. Capture is bounded to 30 seconds, converted to mono PCM16 at 16 kHz, and validated again by the service. A low-energy check rejects silent clips before Whisper. It is not a general speech-activity detector: background noise can still produce an incorrect transcript, so review remains mandatory.

Raw audio is not uploaded or saved in conversation history. The local profile's `voice/audio/capture-*` directory holds request-owned input and transcription files only while inference runs. Normal completion, error, and cancellation remove them. On restart, cleanup removes only recognized files in marked capture directories; it never recursively removes unknown files. An abrupt system failure can leave these files until the next launch. Playback audio exists in memory and its object URL is revoked on stop.

Only microphone ID, stock voice name, and the spoken-reply preference persist in webview storage. Window blur, navigation, page hiding, device change, and component disposal stop active voice work. A resume watchdog also expires a turn after a JavaScript scheduling gap over five seconds, covering sleep/resume when a blur event is absent; a severely stalled app can trigger this safeguard too. Physical sleep/wake behavior still needs the hardware checks below. A missing saved input is reported rather than silently substituting another device. Restart never opens a microphone automatically.

**Sent text uses ordinary chat permissions.** If web search or a connector is enabled, derived text queries can leave the device just as with a typed message. Speech setup itself needs access to GitHub and Hugging Face; audio processing works offline after setup.

## Engine choice and licenses

The installer pins [whisper.cpp b5454](https://github.com/ggml-org/whisper.cpp/releases/tag/b5454) and checks exact SHA-256 and byte length before publishing downloads. Files are staged before installation. Models and runtime binaries are not committed to this repository.

| Component | Download size | SHA-256 |
| --- | ---: | --- |
| `whisper-bin-x64.zip`, b5454 | 8,928,640 bytes | `6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb` |
| `ggml-base.en.bin` | 147,964,211 bytes | `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002` |
| `ggml-small.en.bin`, benchmark only | 487,614,201 bytes | `c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d` |

whisper.cpp is distributed under its [MIT license](https://github.com/ggml-org/whisper.cpp/blob/d1be6fde11ac6e0407606b4e42fe72d34add8037/LICENSE). Whisper code and weights are [MIT licensed by OpenAI](https://github.com/openai/whisper/blob/main/LICENSE); converted weights come from [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp). Preserve applicable notices if redistributing these components.

The TTS fallback uses [Windows System.Speech](https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer). It invokes voices already licensed and installed with the user's Windows environment. LocalMind does not redistribute voice packs or clone voices. Voice availability and sound quality depend on that installation. Kokoro has not been benchmarked or shipped in this pilot.

On the development machine, both base.en and small.en achieved 2.35% word error rate on ten quiet synthetic clips. Base's median transcription time was 1.06 seconds, versus 3.64 seconds for small; measured peak child-process RSS was approximately 308 MB versus 774 MB. Base was selected for lower latency and memory with no accuracy advantage for small on this set. Windows stock TTS produced a complete short WAV in a median 0.41 seconds. These are device-specific synthetic results, not accuracy or latency guarantees for human speech.

## Reproduce the measurements

Use an isolated profile, not your everyday chat profile. Install the service's development dependencies and optional `psutil` for process CPU/RSS sampling. Set `LOCALMIND_DATA_DIR` to that profile and `PYTHONPATH` to `services/ai`.

```powershell
$env:LOCALMIND_DATA_DIR = "$PWD/artifacts/evaluation/voice/profile"
$env:PYTHONPATH = "$PWD/services/ai"
python -c "import asyncio; from app.voice import install; asyncio.run(install()); asyncio.run(install('small.en'))"
python scripts/evaluation/voice_benchmark.py
python scripts/evaluation/voice_journey.py --model qwen2.5:1.5b
```

The second model is optional and downloads about 488 MB. The journey requires the named model to be installed in local Ollama. The benchmark creates 20 synthetic clips with fixed reference text, two installed voices, names, pauses, deterministic noise, and silence. It reports word error rate, wall time, CPU time/load, peak child-process RAM, first invocation, and subsequent OS-cache invocations. Every clip launches a new CLI process and reloads weights; there is no resident-engine warm-start claim. Number formatting affects word error rate.

The journey runs ten real STT/chat/TTS turns, validates the synthesized WAV, and verifies 20 ordinary text messages in the isolated conversation database. It measures time until the first WAV is ready, not speaker playback latency. It does not record from a microphone or play sound. Frontend regression tests separately exercise sentence ordering, code/math omission, turn identity, cancellation races, permission denial, draft preservation, keyboard controls, and playback failure.

Generated audio, run reports, and daily notes remain under ignored `artifacts/`; reusable scripts and synthetic reference cases are tracked.

## Human acceptance before promoting the pilot

- [ ] Test actual microphone permission grant, denial, retry, and named/default input on the packaged Windows app.
- [ ] Record at least 20 natural utterances across multiple speakers/accents and realistic room noise. Review recognition errors and compare against synthetic results.
- [ ] Check stock voice pronunciation and volume, first audible latency, and whether code/math omission sounds understandable.
- [ ] Complete ten spoken exchanges, correcting at least one draft and interrupting at least two replies. Reopen the conversation and check history.
- [ ] Unplug the selected microphone while recording; reconnect and explicitly select it again. Confirm recovery without draft loss.
- [ ] Sleep/wake while recording and while speaking, then close/reopen the app. Confirm the OS microphone indicator clears and capture resumes only after an explicit action.
- [ ] Navigate with only the keyboard and a screen reader; inspect focus, status announcements, disabled controls, and narrow-window scrolling.

These human hardware checks remain pending. Automated lifecycle tests and synthetic pipeline measurements do not replace them. Hands-free turn detection, simultaneous listening/speaking, Mac/Linux speech adapters, multilingual model selection, and neural TTS are separate future work.
