//! Sidecar process management for the LocalMind Python AI service.
//!
//! Responsibilities (from spec Section 8):
//! - Select a random available port
//! - Generate a per-session bearer token
//! - Launch the Python process with port and token via environment variables
//! - Poll /health until the sidecar is ready
//! - Kill the sidecar when the handle is dropped

use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::time::Duration;

use rand::Rng;

// ---------------------------------------------------------------------------
// Port selection
// ---------------------------------------------------------------------------

/// Binds to `127.0.0.1:0`, reads the port the OS assigned, then drops the
/// listener. The port is briefly free again before the sidecar claims it.
/// This TOCTOU window is acceptable for a loopback-only service.
pub fn find_available_port() -> std::io::Result<u16> {
    let listener = TcpListener::bind("127.0.0.1:0")?;
    Ok(listener.local_addr()?.port())
}

// ---------------------------------------------------------------------------
// Token generation
// ---------------------------------------------------------------------------

/// Generates 32 cryptographically random bytes and returns them as a
/// 64-character lowercase hex string. Uses `rand::thread_rng` as specified
/// in Section 25 Step 5 of the spec.
pub fn generate_bearer_token() -> String {
    let bytes: [u8; 32] = rand::thread_rng().gen();
    bytes.iter().map(|b| format!("{:02x}", b)).collect()
}

// ---------------------------------------------------------------------------
// Script location
// ---------------------------------------------------------------------------

/// Walks up from `start` up to 10 levels looking for `services/ai/main.py`.
fn find_script_from(start: &std::path::Path) -> Option<PathBuf> {
    let mut dir = start;
    for _ in 0..10 {
        let candidate = dir.join("services").join("ai").join("main.py");
        if candidate.exists() {
            return Some(candidate);
        }
        dir = dir.parent()?;
    }
    None
}

/// Resolves the path to the Python sidecar script.
///
/// Resolution order:
/// 1. `LOCALMIND_SIDECAR_SCRIPT` environment variable (allows dev overrides)
/// 2. Walk up from the current executable (works for `pnpm tauri dev`)
/// 3. Walk up from the current working directory (fallback)
fn resolve_script() -> Option<PathBuf> {
    if let Ok(path) = std::env::var("LOCALMIND_SIDECAR_SCRIPT") {
        let p = PathBuf::from(&path);
        if p.exists() {
            return Some(p);
        }
        eprintln!(
            "[localmind] LOCALMIND_SIDECAR_SCRIPT is set to {path:?} but the file does not exist"
        );
    }

    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            if let Some(found) = find_script_from(parent) {
                return Some(found);
            }
        }
    }

    if let Ok(cwd) = std::env::current_dir() {
        if let Some(found) = find_script_from(&cwd) {
            return Some(found);
        }
    }

    None
}

// ---------------------------------------------------------------------------
// Platform-specific Python executable name
// ---------------------------------------------------------------------------

#[cfg(target_os = "windows")]
fn python_exe() -> &'static str {
    "python"
}

#[cfg(not(target_os = "windows"))]
fn python_exe() -> &'static str {
    "python3"
}

// ---------------------------------------------------------------------------
// SidecarHandle
// ---------------------------------------------------------------------------

/// A handle to the running Python AI service process.
///
/// The sidecar is killed automatically when this value is dropped.
pub struct SidecarHandle {
    child: Child,
    /// Port the sidecar is bound to. Used by the Rust core to route API calls.
    pub port: u16,
    /// Per-session bearer token. Must be included in every request to the sidecar.
    pub token: String,
}

impl SidecarHandle {
    /// Launches the Python AI service as a child process.
    ///
    /// Steps:
    /// 1. Find a random free port
    /// 2. Generate a 32-byte bearer token
    /// 3. Locate `services/ai/main.py`
    /// 4. Spawn `python main.py` with `LOCALMIND_PORT` and `LOCALMIND_TOKEN` set
    pub fn launch() -> Result<Self, SidecarError> {
        let port = find_available_port().map_err(SidecarError::PortBinding)?;
        let token = generate_bearer_token();
        let script = resolve_script().ok_or(SidecarError::ScriptNotFound)?;

        eprintln!(
            "[localmind] launching sidecar: {} {} (port {port})",
            python_exe(),
            script.display()
        );

        // Resolve the platform data directory so Python can persist files there.
        // Windows:  %APPDATA%\LocalMind
        // macOS:    ~/Library/Application Support/LocalMind
        // Linux:    ~/.local/share/LocalMind
        let data_dir = dirs::data_dir()
            .map(|d| d.join("LocalMind"))
            .unwrap_or_else(|| std::path::PathBuf::from("."));

        let child = Command::new(python_exe())
            .arg(&script)
            .env("LOCALMIND_PORT", port.to_string())
            .env("LOCALMIND_TOKEN", &token)
            .env("LOCALMIND_DATA_DIR", data_dir.to_string_lossy().as_ref())
            .stderr(Stdio::inherit())
            .spawn()
            .map_err(SidecarError::Spawn)?;

        Ok(SidecarHandle { child, port, token })
    }

    /// Polls `GET /health` every 500 ms until a 200 response arrives.
    ///
    /// Times out after 30 seconds (60 attempts). The bearer token is included
    /// in every poll request because all routes on the sidecar require it.
    pub async fn wait_until_ready(&self) -> Result<(), SidecarError> {
        let client = reqwest::Client::new();
        let url = format!("http://127.0.0.1:{}/health", self.port);

        for _ in 0..60 {
            tokio::time::sleep(Duration::from_millis(500)).await;

            let result = client
                .get(&url)
                .header("Authorization", format!("Bearer {}", self.token))
                .timeout(Duration::from_secs(2))
                .send()
                .await;

            if let Ok(resp) = result {
                if resp.status().is_success() {
                    return Ok(());
                }
            }
        }

        Err(SidecarError::HealthTimeout)
    }
}

impl Drop for SidecarHandle {
    /// Kills the sidecar child process when the handle is dropped.
    /// This fires when the Tauri app exits and drops its managed state.
    fn drop(&mut self) {
        let _ = self.child.kill();
    }
}

// ---------------------------------------------------------------------------
// Error type
// ---------------------------------------------------------------------------

#[derive(Debug)]
pub enum SidecarError {
    PortBinding(std::io::Error),
    ScriptNotFound,
    Spawn(std::io::Error),
    HealthTimeout,
}

impl std::fmt::Display for SidecarError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SidecarError::PortBinding(e) => write!(f, "failed to find an available port: {e}"),
            SidecarError::ScriptNotFound => write!(
                f,
                "could not locate services/ai/main.py; \
                 set LOCALMIND_SIDECAR_SCRIPT to the absolute path"
            ),
            SidecarError::Spawn(e) => write!(f, "failed to spawn Python sidecar: {e}"),
            SidecarError::HealthTimeout => {
                write!(f, "sidecar health check timed out after 30 seconds")
            }
        }
    }
}

impl std::error::Error for SidecarError {}
