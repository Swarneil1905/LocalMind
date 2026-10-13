# LocalMind desktop development

The desktop app combines React, TypeScript, and Vite with Tauri 2. Rust launches the Python AI service and forwards desktop commands to its authenticated loopback API. Ollama runs model inference locally.

For installation and product features, see the [main README](../../README.md). Run the commands below from the repository root unless a step says otherwise.

## Prerequisites

- Node.js 22 and pnpm 11.0.9.
- Python 3.11 and Rust stable.
- [Tauri's native prerequisites](https://v2.tauri.app/start/prerequisites/) for your OS, including Windows C++ build tools/WebView2, macOS command-line tools, or Linux WebKit development libraries.
- Ollama installed and running locally on port 11434. Development expects a system Ollama; release builds stage a bundled runtime separately.

Docker is not required.

## Set up the workspace

```sh
git clone https://github.com/Swarneil1905/LocalMind.git
cd LocalMind
pnpm install --frozen-lockfile
```

Create and activate a Python 3.11 environment. Keep it active in the terminal that launches Tauri so the AI service uses the installed dependencies.

**Windows PowerShell:**

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r services/ai/requirements.txt
python -m pip install ruff==0.15.16
```

**macOS / Linux:**

```sh
python3.11 -m venv .venv
source .venv/bin/activate
python -m pip install -r services/ai/requirements.txt
python -m pip install ruff==0.15.16
```

Tauri's external-binary configuration requires a target-named sidecar file even for compilation. For source development, create an empty placeholder only if the file is missing. The launcher falls back to `services/ai/main.py` when the adjacent bundled binary is empty or absent.

**Windows PowerShell:**

```powershell
$rustTarget = ((rustc -vV | Select-String '^host:').Line -split '\s+')[1]
$sidecarPath = "apps/desktop/src-tauri/binaries/localmind-sidecar-$rustTarget.exe"
New-Item -ItemType Directory -Force apps/desktop/src-tauri/binaries | Out-Null
if (-not (Test-Path -LiteralPath $sidecarPath)) {
    New-Item -ItemType File -Path $sidecarPath | Out-Null
}
```

**macOS / Linux:**

```sh
rust_target=$(rustc -vV | sed -n 's/^host: //p')
sidecar_path="apps/desktop/src-tauri/binaries/localmind-sidecar-$rust_target"
mkdir -p apps/desktop/src-tauri/binaries
if [ ! -e "$sidecar_path" ]; then
    touch "$sidecar_path"
fi
```

These placeholders are for development only. Native release builds replace them with a real, tested Python executable. If you previously staged a nonempty frozen sidecar, the launcher will prefer it; rebuild it after Python changes, or use an empty development placeholder in a clean checkout to run Python from source.

## Run the app

Confirm Ollama is running with `ollama list`. If it is not running, start `ollama serve` in a separate terminal or open the installed Ollama app.

From the repository root, with the Python environment active:

```sh
pnpm --filter desktop tauri dev
```

This starts Vite, the native desktop window, and the local AI service. Use the model setup screen to download models, or pull the defaults through Ollama:

```sh
ollama pull qwen2.5:1.5b
ollama pull qwen3:8b
ollama pull nomic-embed-text
```

`pnpm --filter desktop dev` starts Vite alone. A browser tab does not provide Tauri commands or start the AI service, so it cannot exercise the complete app.

Development uses your normal LocalMind profile unless you set `LOCALMIND_DATA_DIR` to an absolute path before launching. Use a separate profile for experiments; two running services cannot share a profile. See [profile storage](../../docs/DESKTOP_RUNTIME.md#profile-storage) for details.

## Validate changes

Frontend checks, from the repository root:

```sh
pnpm --filter desktop typecheck
pnpm --filter desktop lint
pnpm --filter desktop test
pnpm --filter desktop build
```

Python service checks, with the environment active:

```sh
cd services/ai
python -m ruff check .
python -m unittest discover -s tests -v
python -c "import app.main; import app.api.routes.health"
cd ../..
```

Rust and release tooling checks, from the repository root:

```sh
cargo check --workspace
cargo clippy --workspace -- -D warnings
cargo test --workspace
python -m ruff check --config services/ai/pyproject.toml scripts/release
python -m unittest discover -s scripts/release -p "test_*.py" -v
python scripts/release/check_version.py
```

Native packaging tests may skip on other operating systems or when their tools are unavailable. GitHub Actions runs platform-specific checks on the appropriate runners. Passing local unit tests does not replace testing a packaged installer.

## Build a release

Follow [Releasing LocalMind](../../docs/RELEASING.md). The release workflow freezes the Python service, stages the complete Ollama runtime, builds installers on matching native runners, and validates the packaged services and updater signatures. A plain frontend build does not produce a distributable installer.
