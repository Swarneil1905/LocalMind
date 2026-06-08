// Spec reference: Section 8 (Rust Responsibilities — Ollama health check)
// Phase 1: detect Ollama, list models, detect GPU.

use serde::{Deserialize, Serialize};
use std::time::Duration;

const OLLAMA_BASE: &str = "http://127.0.0.1:11434";
const TIMEOUT_SECS: u64 = 3;

// ---------------------------------------------------------------------------
// Public types (serialised to the UI as JSON via Tauri commands)
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OllamaStatus {
    /// true if Ollama responded to /api/tags within the timeout
    pub running: bool,
    /// Ollama version string, or None if not reachable
    pub version: Option<String>,
    /// Models currently available in Ollama
    pub models: Vec<ModelInfo>,
    /// GPU information (None on CPU-only machines)
    pub gpu: Option<GpuInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelInfo {
    pub name: String,
    /// Size in bytes
    pub size: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuInfo {
    pub name: String,
    /// Total VRAM in MiB
    pub vram_total_mib: u64,
    /// Free VRAM in MiB at the time of the check
    pub vram_free_mib: u64,
}

// ---------------------------------------------------------------------------
// Ollama API response shapes (only the fields we need)
// ---------------------------------------------------------------------------

#[derive(Deserialize)]
struct TagsResponse {
    models: Vec<OllamaModel>,
}

#[derive(Deserialize)]
struct OllamaModel {
    name: String,
    size: u64,
}

#[derive(Deserialize)]
struct VersionResponse {
    version: String,
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Query Ollama and GPU state. Never panics — returns a status struct with
/// `running = false` if Ollama is not reachable.
pub async fn check() -> OllamaStatus {
    let client = match reqwest::Client::builder()
        .timeout(Duration::from_secs(TIMEOUT_SECS))
        .build()
    {
        Ok(c) => c,
        Err(_) => {
            return OllamaStatus {
                running: false,
                version: None,
                models: vec![],
                gpu: detect_gpu().await,
            }
        }
    };

    // /api/tags is the primary liveness check — it also gives us the model list
    let tags_resp = client
        .get(format!("{OLLAMA_BASE}/api/tags"))
        .send()
        .await;

    let (running, models) = match tags_resp {
        Ok(resp) if resp.status().is_success() => {
            let models = resp
                .json::<TagsResponse>()
                .await
                .map(|t| {
                    t.models
                        .into_iter()
                        .map(|m| ModelInfo { name: m.name, size: m.size })
                        .collect()
                })
                .unwrap_or_default();
            (true, models)
        }
        _ => (false, vec![]),
    };

    // /api/version is best-effort — failure does not change the running flag
    let version_str = if running {
        client
            .get(format!("{OLLAMA_BASE}/api/version"))
            .send()
            .await
            .ok()
            .and_then(|r| {
                // status check without consuming the body yet
                if r.status().is_success() { Some(r) } else { None }
            })
            // .and_then cannot be async, so we flatten with a separate match
    } else {
        None
    };

    // Await the version body outside the closure
    let version = match version_str {
        Some(resp) => resp.json::<VersionResponse>().await.ok().map(|v| v.version),
        None => None,
    };

    let gpu = detect_gpu().await;

    OllamaStatus { running, version, models, gpu }
}

// ---------------------------------------------------------------------------
// GPU detection — Windows/Linux via nvidia-smi
// ---------------------------------------------------------------------------

async fn detect_gpu() -> Option<GpuInfo> {
    detect_nvidia().await
    // macOS Metal detection would be added here in a later phase
}

async fn detect_nvidia() -> Option<GpuInfo> {
    // Single nvidia-smi call: name, total VRAM (MiB), free VRAM (MiB)
    let output = tokio::process::Command::new("nvidia-smi")
        .args([
            "--query-gpu=name,memory.total,memory.free",
            "--format=csv,noheader,nounits",
        ])
        .output()
        .await
        .ok()?;

    if !output.status.success() {
        return None;
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    // Each GPU is one line; take the first one
    let line = stdout.lines().next()?;
    let parts: Vec<&str> = line.split(',').map(str::trim).collect();

    if parts.len() < 3 {
        return None;
    }

    Some(GpuInfo {
        name: parts[0].to_string(),
        vram_total_mib: parts[1].parse().ok()?,
        vram_free_mib: parts[2].parse().ok()?,
    })
}
