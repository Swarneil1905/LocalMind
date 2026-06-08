use std::sync::Mutex;

use futures_util::StreamExt;
use localmind_core::{
    ollama::{check as ollama_check, OllamaStatus},
    sidecar::SidecarHandle,
};
use serde::{Deserialize, Serialize};
use tauri::{Emitter, Manager};

// ---------------------------------------------------------------------------
// Managed state
// ---------------------------------------------------------------------------

struct SidecarState(Mutex<Option<SidecarHandle>>);
struct OllamaState(Mutex<Option<OllamaStatus>>);

// ---------------------------------------------------------------------------
// Shared types
// ---------------------------------------------------------------------------

/// A single turn in the conversation history sent from the UI.
#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct HistoryEntry {
    pub role: String,
    pub content: String,
}

/// A single memory row returned from the Python sidecar.
#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct MemoryEntry {
    pub id: String,
    pub content: String,
    pub created_at: String,
}

/// Payload emitted for every token chunk while streaming.
#[derive(Clone, Serialize)]
struct ChatTokenPayload {
    content: String,
}

/// Payload emitted when generation finishes or an error occurs.
#[derive(Clone, Serialize)]
struct ChatDonePayload {
    error: Option<String>,
}

/// Payload emitted when the memory list changes.
#[derive(Clone, Serialize)]
struct MemoriesUpdatedPayload {
    memories: Vec<MemoryEntry>,
}

// ---------------------------------------------------------------------------
// Helper - build a sidecar URL
// ---------------------------------------------------------------------------

fn sidecar_url(state: &SidecarState, path: &str) -> Result<(String, String), String> {
    let guard = state.0.lock().map_err(|e| e.to_string())?;
    match guard.as_ref() {
        Some(s) => Ok((format!("http://127.0.0.1:{}{path}", s.port), s.token.clone())),
        None => Err("Sidecar not ready yet".into()),
    }
}

// ---------------------------------------------------------------------------
// Tauri commands
// ---------------------------------------------------------------------------

/// Return the current Ollama status (performs a live check).
#[tauri::command]
async fn get_ollama_status(
    state: tauri::State<'_, OllamaState>,
) -> Result<OllamaStatus, String> {
    let status = ollama_check().await;
    *state.0.lock().map_err(|e| e.to_string())? = Some(status.clone());
    Ok(status)
}

/// Stream a chat turn to Ollama through the Python sidecar.
///
/// If `memory_enabled` is true, fetches all stored memories first and
/// prepends them to the system prompt.
///
/// Emits two event types to the frontend window:
///   "chat-token"  - {content: "<chunk>"}   for each token
///   "chat-done"   - {error: null | "msg"}  on finish or error
#[tauri::command]
async fn chat_stream(
    message: String,
    model: String,
    history: Vec<HistoryEntry>,
    memory_enabled: bool,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/chat/stream")?;

    // Build system prompt, injecting memories when enabled
    let system_prompt = if memory_enabled {
        let (list_url, list_token) = sidecar_url(&sidecar_state, "/memory/list")?;
        let client = reqwest::Client::new();
        let memories: Vec<MemoryEntry> = match client
            .get(&list_url)
            .header("Authorization", format!("Bearer {list_token}"))
            .send()
            .await
        {
            Ok(resp) if resp.status().is_success() => {
                resp.json::<serde_json::Value>()
                    .await
                    .ok()
                    .and_then(|v| serde_json::from_value(v["memories"].clone()).ok())
                    .unwrap_or_default()
            }
            _ => vec![],
        };

        build_system_prompt(&memories)
    } else {
        default_system_prompt()
    };

    let body = serde_json::json!({
        "message": message,
        "model": model,
        "history": history,
        "system_prompt": system_prompt,
    });

    let client = reqwest::Client::new();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;

    if !resp.status().is_success() {
        let status = resp.status().as_u16();
        return Err(format!("Sidecar returned HTTP {status}"));
    }

    // Parse the SSE stream line by line
    let mut stream = resp.bytes_stream();
    let mut buffer = String::new();

    while let Some(chunk) = stream.next().await {
        let bytes = chunk.map_err(|e| e.to_string())?;
        let text = String::from_utf8_lossy(&bytes);
        buffer.push_str(&text);

        while let Some(pos) = buffer.find("\n\n") {
            let event_str = buffer[..pos].to_string();
            buffer.drain(..pos + 2);

            for line in event_str.lines() {
                let Some(json_str) = line.strip_prefix("data: ") else {
                    continue;
                };

                let Ok(value) = serde_json::from_str::<serde_json::Value>(json_str) else {
                    continue;
                };

                match value.get("type").and_then(|t| t.as_str()) {
                    Some("token") => {
                        let content = value
                            .get("content")
                            .and_then(|c| c.as_str())
                            .unwrap_or("")
                            .to_string();
                        app_handle
                            .emit("chat-token", ChatTokenPayload { content })
                            .ok();
                    }
                    Some("done") => {
                        app_handle
                            .emit("chat-done", ChatDonePayload { error: None })
                            .ok();
                        return Ok(());
                    }
                    Some("error") => {
                        let msg = value
                            .get("content")
                            .and_then(|c| c.as_str())
                            .unwrap_or("Unknown error")
                            .to_string();
                        app_handle
                            .emit("chat-done", ChatDonePayload { error: Some(msg) })
                            .ok();
                        return Ok(());
                    }
                    _ => {}
                }
            }
        }
    }

    app_handle
        .emit("chat-done", ChatDonePayload { error: None })
        .ok();

    Ok(())
}

/// Extract memories from the last exchange and emit "memories-updated".
/// Called by the UI after each assistant reply when memory is enabled.
#[tauri::command]
async fn extract_memories(
    user_message: String,
    assistant_message: String,
    speed_model: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/memory/extract")?;

    let body = serde_json::json!({
        "user_message": user_message,
        "assistant_message": assistant_message,
        "speed_model": speed_model,
    });

    let client = reqwest::Client::new();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;

    if !resp.status().is_success() {
        // Extraction failure is non-fatal - swallow quietly
        return Ok(());
    }

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let memories: Vec<MemoryEntry> = serde_json::from_value(data["memories"].clone())
        .unwrap_or_default();

    app_handle
        .emit("memories-updated", MemoriesUpdatedPayload { memories })
        .ok();

    Ok(())
}

/// Return all stored memories.
#[tauri::command]
async fn list_memories(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<MemoryEntry>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/memory/list")?;

    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let memories: Vec<MemoryEntry> = serde_json::from_value(data["memories"].clone())
        .unwrap_or_default();

    Ok(memories)
}

/// Delete a single memory by id, then emit "memories-updated" with the new list.
#[tauri::command]
async fn delete_memory(
    memory_id: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/memory/{memory_id}"))?;

    let client = reqwest::Client::new();
    client
        .delete(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    // Fetch updated list and notify UI
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/memory/list")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let memories: Vec<MemoryEntry> = serde_json::from_value(data["memories"].clone())
        .unwrap_or_default();

    app_handle
        .emit("memories-updated", MemoriesUpdatedPayload { memories })
        .ok();

    Ok(())
}

// ---------------------------------------------------------------------------
// System prompt helpers
// ---------------------------------------------------------------------------

fn default_system_prompt() -> String {
    "You are LocalMind, a private desktop AI assistant. \
     You help the user with memory, projects, documents, and work tasks. \
     You are precise, direct, and do not add unnecessary commentary."
        .to_string()
}

fn build_system_prompt(memories: &[MemoryEntry]) -> String {
    let base = default_system_prompt();
    if memories.is_empty() {
        return base;
    }
    let facts = memories
        .iter()
        .map(|m| format!("- {}", m.content))
        .collect::<Vec<_>>()
        .join("\n");
    format!("{base}\n\nThings you remember about the user:\n{facts}")
}

// ---------------------------------------------------------------------------
// App entry point
// ---------------------------------------------------------------------------

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(SidecarState(Mutex::new(None)))
        .manage(OllamaState(Mutex::new(None)))
        .setup(|app| {
            let app_handle = app.handle().clone();

            tauri::async_runtime::spawn(async move {
                let sidecar = match SidecarHandle::launch() {
                    Ok(s) => s,
                    Err(e) => {
                        eprintln!("[localmind] sidecar launch failed: {e}");
                        return;
                    }
                };

                match sidecar.wait_until_ready().await {
                    Ok(()) => {
                        println!("[localmind] sidecar ready on port {}", sidecar.port);
                        let state = app_handle.state::<SidecarState>();
                        *state.0.lock().unwrap() = Some(sidecar);
                    }
                    Err(e) => {
                        eprintln!("[localmind] sidecar health check failed: {e}");
                    }
                }

                // Run Ollama detection after the sidecar is up
                let status = ollama_check().await;
                println!(
                    "[localmind] Ollama running={} models={} gpu={:?}",
                    status.running,
                    status.models.len(),
                    status.gpu.as_ref().map(|g| &g.name)
                );
                let ollama_state = app_handle.state::<OllamaState>();
                *ollama_state.0.lock().unwrap() = Some(status);
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_ollama_status,
            chat_stream,
            extract_memories,
            list_memories,
            delete_memory,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
