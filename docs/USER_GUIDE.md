# Using LocalMind

Installation, model setup, everyday controls and troubleshooting. For a product overview or ways to contribute, return to the [main README](../README.md).

[Install](#install) · [First chat](#get-started) · [Feature limits](#optional-and-experimental-features) · [Your data](#your-data) · [Troubleshooting](#troubleshooting)

## Install

**[Open the latest release](https://github.com/Swarneil1905/LocalMind/releases/latest)** and choose your installer. The published version is **0.8.0**; the source tree includes the **0.9.0 release candidate**. See the [published release notes](releases/v0.8.0.md) and [candidate notes and limits](releases/v0.9.0.md). The release page is the source of truth for available installers.

| Platform | Download |
| --- | --- |
| Windows x64 | `LocalMind_0.8.0_x64-setup.exe` |
| macOS Apple Silicon, macOS 14+ | `LocalMind_0.8.0_aarch64.dmg` |
| macOS Intel, macOS 14+ | `LocalMind_0.8.0_x64.dmg` |
| Linux x64 | `.deb` or `.AppImage` |

The installer includes the Python AI service and Ollama runtime. You do not need Python, Node.js, Rust or Docker to use the app. An existing local Ollama installation can be reused. **Model weights download separately.** Memory use, disk space and speed depend on your model and computer.

Updater bundles are signed with LocalMind's update key. Windows installers are not Authenticode-signed and macOS apps are not Apple-notarized, so operating systems may display a security prompt. Release assets include `SHA256SUMS.txt`.

**Updating:** v0.8.0 shows an update banner when a newer published version is available. The next release adds **Settings → Updates** with a manual check, installed version, release notes, and retry controls. It works independently of the local AI service. Before selecting **Install and restart**, finish active responses and save unsent drafts. If installation fails, use the installer on the release page. Pulling or pushing source code does not update an installed copy.

## Get started

1. **Finish model setup.** Choose your chat model, then explicitly download its weights if needed. Setup verifies that the selected model supports local chat. Compact models use fewer resources but can miss instructions and corrections.
2. **Ask a question.** Choose a response preset from the composer. Follow up in the same chat to keep context.
3. **Add your documents.** In Knowledge, add a folder and assign its project. Enable Knowledge in the matching chat; open citations to inspect source passages.
4. **Review Memory.** Keep useful facts and remove anything you do not want reused.
5. **Use Web when needed.** Enable it from Tools. Online queries leave the device; local explanations do not require search.

| Default model | Purpose | Approximate download |
| --- | --- | --- |
| `qwen2.5:1.5b` | Speed preset and lightweight background work | 1 GB |
| `qwen3:8b` | Balanced preset | 5.2 GB |
| `nomic-embed-text` | Semantic document search | 274 MB |

Once models are installed, local chat and retrieval can work offline. Exact-text Knowledge search remains available when embeddings fail. Local models can still be incorrect, miss context or invent details. Check important claims and cited sources.

### Everyday controls

- **Enter** sends; **Shift + Enter** adds a line break.
- **Tools** toggles Memory, Knowledge and Web. Active context appears above the draft.
- **Ctrl/Cmd + Shift + M** switches between full-window and mini chat while the app is focused.
- **Ctrl/Cmd + Shift + O** starts a new chat.
- Reading older messages keeps your position; **Latest response** returns to the current reply.
- **Response details** shows session-only model identity, timing and omitted context. Copy diagnostics shares metadata without the prompt or answer; it is not a correctness score.

## Optional and experimental features

| Feature | Current boundary |
| --- | --- |
| **Local images** | Disabled by default after SSD-1B failed visual-quality review. Optional CUDA worker, separate runtime and approximately 4.16 GiB model download. No automatic paid fallback. |
| **Runway video** | Your own API key, native credential storage and per-request upload/cost approval. Local $50 monthly reservation limit; unknown submissions are never silently retried. Real provider acceptance remains pending. |
| **Voice** | Windows x64 English pilot: whisper.cpp transcription and installed system voices. Disabled by default pending human microphone/playback checks. Mac/Linux voice is not implemented. |
| **Gmail** | Read-only access with OS-stored credentials and bounded snippet sync. Fresh authorization required; live-account acceptance pending. |
| **MCP and plugins** | An explicitly enabled, owned MCP test server, built-in planner and synthetic repair recipe. No arbitrary third-party code or automatic chat tool execution. |
| **WhatsApp / iMessage** | Unavailable pending provider acceptance. |

Setup and limitations: [Media](media.md) · [Voice](voice.md) · [Connections and tools](connections.md) · [Image research](image-research.md).

The current source build also adds optional OpenAI, Anthropic and Gemini API
connections, screenshot paste and upload, and recovery of interrupted replies.
These additions require a new build; existing 0.8.0 installers do not gain them
automatically. See [local and cloud chat](cloud-chat.md) for setup, privacy,
image limits and verification status.

The source build also includes [a visual workspace](visual-workspace.md):
editable diagrams with saved revisions, Mermaid, charts, local learning widgets
and saved image previews. PDF/text attachments expose their extracted pages.
Optional [image API requests](media.md#image-api-in-the-current-source-build)
require review before sending. Live paid-image acceptance is still pending;
arbitrary generated applications remain disabled.

## Your data

The desktop app talks to an authenticated local service on loopback. LocalMind does not send product analytics. Optional cloud chat keeps a usage and estimated-cost ledger in your local profile.

| Activity | Destination |
| --- | --- |
| Local chat, memory, projects and tasks | Your device; SQLite profile databases |
| Optional cloud chat | Selected provider, after approval of destination, model and included data categories |
| Screenshot drafts | Your profile; sent only when included in a request to a compatible model |
| Document indexing and retrieval | Your device; SQLite and LanceDB |
| Web search | Search provider and retrieved websites; queries may include follow-up context |
| Connected services | The service you authorize; downloaded data remains in your profile |
| Runway video | Reviewed image and prompt go to Runway only after confirmation |
| Model/runtime downloads | Ollama or the documented model/runtime host |
| Update checks | GitHub Releases |

Gmail, Runway and chat-provider credentials use the native OS credential store with no plaintext fallback. Workspace databases are protected by OS account permissions, **not application-level encryption**. People sharing an OS account share its default profile.

| Platform | Default data directory |
| --- | --- |
| Windows | `%APPDATA%\LocalMind` |
| macOS | `~/Library/Application Support/LocalMind` |
| Linux | `$XDG_DATA_HOME/LocalMind`, or `~/.local/share/LocalMind` |

Ollama stores models separately. See [desktop runtime and storage](DESKTOP_RUNTIME.md) before moving or backing up a profile.

## Troubleshooting

| Problem | Next step |
| --- | --- |
| AI service will not start | Use Retry. Close another LocalMind instance using the same profile. |
| Windows setup cannot write `localmind-sidecar.exe` | Close LocalMind and mini chat, then choose Retry. If an older version left a background service running, restart Windows and run setup before opening LocalMind. Do not choose Ignore: that can leave a mixed-version installation. |
| Model unavailable or responses slow | Complete its download or select a smaller model. Check memory and supported thinking settings. |
| Knowledge is incomplete | Inspect indexing status and permissions, then retry. PDFs need selectable text; OCR and automatic file watching are not implemented. |
| Source changed or disappeared | Citations show indexed passages. Reindex to refresh; older citations may no longer resolve afterward. |
| Current information cannot be verified | Enable Web and use a more specific query. Missing dated evidence should be reported, not invented. |
| Media task is paused or unknown | Follow its receipt. Resume only a known provider task; check Runway before replacing an unknown submission. |

Knowledge supports UTF-8 text/code and selectable-text PDFs, up to 10 MiB per file, 500 pages per PDF, 2,000 supported files and 20,000 passages per folder. Hidden and common build folders are skipped.

For [bug reports](https://github.com/Swarneil1905/LocalMind/issues), include version, OS, model, steps and relevant errors. Remove private content and credentials. Use [SECURITY.md](../SECURITY.md) for vulnerabilities.
