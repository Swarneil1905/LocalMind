# LocalMind

**Privacy-first AI assistant - everything runs on your machine.**

[![CI Frontend](https://github.com/Swarneil1905/LocalMind/actions/workflows/ci-frontend.yml/badge.svg)](https://github.com/Swarneil1905/LocalMind/actions/workflows/ci-frontend.yml)
[![CI Python](https://github.com/Swarneil1905/LocalMind/actions/workflows/ci-python.yml/badge.svg)](https://github.com/Swarneil1905/LocalMind/actions/workflows/ci-python.yml)
[![CI Rust](https://github.com/Swarneil1905/LocalMind/actions/workflows/ci-rust.yml/badge.svg)](https://github.com/Swarneil1905/LocalMind/actions/workflows/ci-rust.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

No account. No cloud. No data leaves your computer.

---

## Install

Download the latest release for your platform:

| Platform | Download |
|----------|----------|
| **Windows** (x64) | [LocalMind_0.5.0_x64_en-US.msi](https://github.com/Swarneil1905/LocalMind/releases/tag/v0.5.0) - [.exe installer](https://github.com/Swarneil1905/LocalMind/releases/tag/v0.5.0) |
| **macOS** (Apple Silicon) | [LocalMind_0.5.0_aarch64.dmg](https://github.com/Swarneil1905/LocalMind/releases/tag/v0.5.0) |
| **macOS** (Intel) | [LocalMind_0.5.0_x64.dmg](https://github.com/Swarneil1905/LocalMind/releases/tag/v0.5.0) |
| **Linux** (x64) | [LocalMind_0.5.0_amd64.AppImage](https://github.com/Swarneil1905/LocalMind/releases/tag/v0.5.0) - [.deb](https://github.com/Swarneil1905/LocalMind/releases/tag/v0.5.0) |

> **SHA256 checksums** are attached to every release as `SHA256SUMS.txt`.

### Prerequisites

1. Install [Ollama](https://ollama.com) and pull at least one model:
   ```
   ollama pull qwen2.5:1.5b       # fast/cheap - used for memory extraction
   ollama pull qwen2.5:7b         # balanced - good default for chat
   ollama pull nomic-embed-text   # required for knowledge base search
   ```
2. Launch LocalMind. It detects Ollama automatically at startup.

---

## What is LocalMind?

LocalMind is a desktop AI assistant built on [Tauri](https://tauri.app), [React](https://react.dev), and [Ollama](https://ollama.com). It gives you:

- **Private chat** with local LLMs - no API key, no cloud subscription
- **Persistent memory** - facts about you are extracted from conversations and injected into future chats
- **Knowledge base** - index local files and folders; semantically search them during chat (RAG)
- **HyDE retrieval** - generates a hypothetical answer before searching, improving retrieval quality
- **Hierarchical memory** - linked facts with typed relationships shown in the Memory screen
- **Web search** - optional, opt-in web search via DuckDuckGo or SearXNG
- **Projects and tasks** - organize conversations under projects; track tasks across them
- **Reasoning UI** - collapsible thinking trace for models that emit `<think>` blocks

---

## Architecture

```
+---------------------------------------------------------+
|                   LocalMind Desktop                     |
|                                                         |
|  +------------------+        +---------------------+   |
|  |  React Frontend  |<------>|  Tauri (Rust)       |   |
|  |  (TypeScript)    | events |  IPC / Commands     |   |
|  +------------------+        +----------+----------+   |
|                                         | HTTP          |
|                               +---------v----------+   |
|                               |  Python Sidecar    |   |
|                               |  (FastAPI)         |   |
|                               |                    |   |
|                               |  +-------------+   |   |
|                               |  |   SQLite    |   |   |
|                               |  |  memories   |   |   |
|                               |  |  projects   |   |   |
|                               |  |   convs     |   |   |
|                               |  +-------------+   |   |
|                               |                    |   |
|                               |  +-------------+   |   |
|                               |  |   LanceDB   |   |   |
|                               |  |  (vectors)  |   |   |
|                               |  +-------------+   |   |
|                               +---------+----------+   |
+------------------------------------------+-------------+
                                           | HTTP (local)
                                 +---------v----------+
                                 |  Ollama            |
                                 |  (LLM inference)   |
                                 +--------------------+
```

**Data flow:** The frontend invokes Tauri commands over IPC. Rust forwards requests to the Python sidecar over a localhost-only HTTP connection authenticated with a per-session bearer token. The sidecar calls Ollama for inference and reads/writes to SQLite and LanceDB. Nothing leaves the machine unless web search is explicitly enabled.

---

## Privacy Model

| What | Where it lives | Leaves your machine? |
|------|---------------|----------------------|
| Chat history | SQLite on disk | Never |
| Memory (extracted facts) | SQLite on disk | Never |
| Knowledge base (indexed files) | LanceDB + SQLite on disk | Never |
| LLM inference | Ollama (local process) | Never |
| Web search queries | Optional - DuckDuckGo / SearXNG | Only if enabled |
| Fetched web content | In-memory only, never stored | Outbound only |
| API keys (search providers) | SQLite on disk | Never logged or transmitted |
| Telemetry | None collected | Never |

LocalMind contains no analytics, crash reporting, or usage tracking of any kind.

---

## Comparison

| Feature | LocalMind | ChatGPT | Claude.ai | Open WebUI |
|---------|-----------|---------|-----------|------------|
| Runs 100% locally | Yes | No | No | Yes |
| No account required | Yes | No | No | Yes |
| Persistent memory | Yes | Yes (cloud) | Yes (cloud) | No |
| Local knowledge base (RAG) | Yes | No | No | Yes |
| HyDE retrieval | Yes | No | No | No |
| Hierarchical memory links | Yes | No | No | No |
| Conversation history | Yes | Yes | Yes | Yes |
| Projects and tasks | Yes | No | No | No |
| Reasoning UI (think blocks) | Yes | No | No | Yes |
| Web search (opt-in) | Yes | Yes | Yes | Yes |
| Native desktop app | Yes | No | No | No |
| Open source | Yes | No | No | Yes |

---

## Development

### Requirements

- Rust stable (>= 1.77)
- Node.js 20 + pnpm 9
- Python 3.11
- Ollama

### Setup

```bash
# Clone
git clone https://github.com/Swarneil1905/LocalMind
cd LocalMind

# Frontend deps
cd apps/desktop && pnpm install

# Python deps
cd ../../services/ai && pip install -r requirements.txt

# Run in dev mode (starts Tauri dev server + sidecar)
cd ../../apps/desktop && pnpm tauri dev
```

### CI

Three GitHub Actions workflows run on every push and pull request:

- **ci-frontend.yml** - TypeScript type check + ESLint
- **ci-python.yml** - ruff lint + import smoke test
- **ci-rust.yml** - cargo check + clippy + test

### Release

Push a version tag to trigger the release workflow:

```bash
git tag v0.5.0
git push origin v0.5.0
```

The workflow builds installers for all four platforms, creates a GitHub Release, and attaches `SHA256SUMS.txt`.

---

## Security

See [SECURITY.md](SECURITY.md) for the vulnerability disclosure policy.

---

## License

MIT - see [LICENSE](LICENSE).
