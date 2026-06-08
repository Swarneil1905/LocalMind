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
/// Emits two event types to the frontend window:
///   "chat-token"  — {content: "<chunk>"}   for each token
///   "chat-done"   — {error: null | "msg"}  on finish or error
#[tauri::command]
async fn chat_stream(
    message: String,
    model: String,
    history: Vec<HistoryEntry>,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    // Retrieve port and token from managed state
    let (port, token) = {
        let guard = sidecar_state.0.lock().map_err(|e| e.to_string())?;
        match guard.as_ref() {
            Some(s) => (s.port, s.token.clone()),
            None => return Err("Sidecar not ready yet".into()),
        }
    };

    let url = format!("http://127.0.0.1:{port}/chat/stream");

    // Build JSON body matching ChatRequest in chat.py
    let body = serde_json::json!({
        "message": message,
        "model": model,
        "history": history,
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

        // SSE events are delimited by \n\n
        while let Some(pos) = buffer.find("\n\n") {
            let event_str = buffer[..pos].to_string();
            buffer.drain(..pos + 2);

            // Each event line starts with "data: "
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

    // Stream ended without a "done" event — emit done anyway
    app_handle
        .emit("chat-done", ChatDonePayload { error: None })
        .ok();

    Ok(())
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
        .invoke_handler(tauri::generate_handler![get_ollama_status, chat_stream])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
