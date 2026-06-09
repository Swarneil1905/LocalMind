use std::sync::Mutex;

use futures_util::StreamExt;
use localmind_core::{
    ollama::{check as ollama_check, OllamaStatus},
    sidecar::SidecarHandle,
};
use serde::{Deserialize, Serialize};
use tauri::{Emitter, Manager};
use tauri_plugin_dialog::DialogExt;

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

/// A knowledge source (indexed folder or file).
#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct KnowledgeSource {
    pub id: String,
    pub path: String,
    pub name: String,
    pub file_count: i64,
    pub chunk_count: i64,
    pub status: String,
    pub created_at: String,
}

/// A single retrieved chunk from the knowledge base.
#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct KnowledgeChunk {
    pub id: String,
    pub source_id: String,
    pub file_path: String,
    pub chunk_index: i64,
    pub content: String,
}

/// A persisted conversation (sidebar entry).
#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct Conversation {
    pub id: String,
    pub title: String,
    pub created_at: String,
    pub updated_at: String,
}

/// A single message row from a persisted conversation.
#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct ConversationMessage {
    pub id: String,
    pub conversation_id: String,
    pub role: String,
    pub content: String,
    pub thinking: Option<String>,
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

/// Payload emitted when knowledge sources change.
#[derive(Clone, Serialize)]
struct KnowledgeUpdatedPayload {
    sources: Vec<KnowledgeSource>,
}

/// Payload emitted when the conversation list changes.
#[derive(Clone, Serialize)]
struct ConversationsUpdatedPayload {
    conversations: Vec<Conversation>,
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
/// If `knowledge_enabled` is true and indexed sources exist, runs a
/// semantic search on the user message and injects relevant chunks.
///
/// Emits two event types to the frontend window:
///   "chat-token"  - {content: "<chunk>"}   for each token
///   "chat-done"   - {error: null | "msg"}  on finish or error
#[allow(clippy::too_many_arguments)]
#[tauri::command]
async fn chat_stream(
    message: String,
    model: String,
    history: Vec<HistoryEntry>,
    memory_enabled: bool,
    knowledge_enabled: bool,
    embed_model: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/chat/stream")?;
    let client = reqwest::Client::new();

    // Build system prompt, injecting memories when enabled
    let mut system_prompt = if memory_enabled {
        let (list_url, list_token) = sidecar_url(&sidecar_state, "/memory/list")?;
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

    // Inject knowledge context when enabled
    if knowledge_enabled {
        let (search_url, search_token) = sidecar_url(&sidecar_state, "/knowledge/search")?;
        let search_body = serde_json::json!({
            "query": message,
            "limit": 5,
            "embed_model": embed_model,
        });
        if let Ok(resp) = client
            .post(&search_url)
            .header("Authorization", format!("Bearer {search_token}"))
            .json(&search_body)
            .send()
            .await
        {
            if resp.status().is_success() {
                if let Ok(data) = resp.json::<serde_json::Value>().await {
                    let chunks: Vec<KnowledgeChunk> =
                        serde_json::from_value(data["results"].clone()).unwrap_or_default();
                    if !chunks.is_empty() {
                        let context = chunks
                            .iter()
                            .map(|c| format!("[{}]\n{}", c.file_path, c.content))
                            .collect::<Vec<_>>()
                            .join("\n\n---\n\n");
                        system_prompt = format!(
                            "{system_prompt}\n\nRelevant context from the user's knowledge base:\n\n{context}"
                        );
                    }
                }
            }
        }
    }

    let body = serde_json::json!({
        "message": message,
        "model": model,
        "history": history,
        "system_prompt": system_prompt,
    });

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
// Knowledge commands
// ---------------------------------------------------------------------------

/// Open a native folder picker dialog and return the chosen path.
/// Returns None if the user cancels.
#[tauri::command]
async fn pick_folder(app_handle: tauri::AppHandle) -> Result<Option<String>, String> {
    // tauri-plugin-dialog uses a one-shot channel pattern for async results
    let (tx, rx) = tokio::sync::oneshot::channel::<Option<String>>();

    app_handle
        .dialog()
        .file()
        .pick_folder(move |path| {
            // FilePath implements Display; use to_string() for conversion
            let result = path.map(|p| p.to_string());
            tx.send(result).ok();
        });

    rx.await.map_err(|e| e.to_string())
}

/// Trigger background indexing of a folder or file.
/// Emits "knowledge-updated" when indexing completes.
#[tauri::command]
async fn index_knowledge(
    path: String,
    embed_model: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/knowledge/index")?;

    let body = serde_json::json!({
        "path": path,
        "embed_model": embed_model,
    });

    let client = reqwest::Client::new();
    client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;

    // Poll until the source transitions from "indexing" to "ready"/"error"
    // (background task on Python side), then emit the updated list.
    // Extract port and token as owned Strings before dropping the lock -
    // SidecarHandle is not Clone so we cannot clone the guard directly.
    let sidecar_port_token: Option<(u16, String)> = {
        let guard = sidecar_state.0.lock().map_err(|e| e.to_string())?;
        guard.as_ref().map(|s| (s.port, s.token.clone()))
    };
    let app_clone = app_handle.clone();

    if let Some((port, list_token)) = sidecar_port_token {
        let list_url = format!("http://127.0.0.1:{port}/knowledge/sources");

        tauri::async_runtime::spawn(async move {
            let client = reqwest::Client::new();
            let mut attempts = 0u32;
            loop {
                tokio::time::sleep(std::time::Duration::from_secs(3)).await;
                attempts += 1;
                if attempts > 200 {
                    break; // give up after 10 minutes
                }

                let Ok(resp) = client
                    .get(&list_url)
                    .header("Authorization", format!("Bearer {list_token}"))
                    .send()
                    .await
                else {
                    continue;
                };

                let Ok(data) = resp.json::<serde_json::Value>().await else {
                    continue;
                };

                let sources: Vec<KnowledgeSource> =
                    serde_json::from_value(data["sources"].clone()).unwrap_or_default();

                // Emit on every poll so the UI shows live progress
                app_clone
                    .emit("knowledge-updated", KnowledgeUpdatedPayload { sources: sources.clone() })
                    .ok();

                // Stop polling once no source is still indexing
                if sources.iter().all(|s| s.status != "indexing") {
                    break;
                }
            }
        });
    }

    Ok(())
}

/// Return all indexed knowledge sources.
#[tauri::command]
async fn list_knowledge_sources(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<KnowledgeSource>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/knowledge/sources")?;

    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let sources: Vec<KnowledgeSource> =
        serde_json::from_value(data["sources"].clone()).unwrap_or_default();

    Ok(sources)
}

/// Delete a knowledge source and all its chunks. Emits "knowledge-updated".
#[tauri::command]
async fn delete_knowledge_source(
    source_id: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/knowledge/source/{source_id}"))?;

    let client = reqwest::Client::new();
    client
        .delete(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    // Fetch updated sources and emit
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/knowledge/sources")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let sources: Vec<KnowledgeSource> =
        serde_json::from_value(data["sources"].clone()).unwrap_or_default();

    app_handle
        .emit("knowledge-updated", KnowledgeUpdatedPayload { sources })
        .ok();

    Ok(())
}

/// Search the knowledge base and return the top matching chunks.
/// Used by the KnowledgePage search bar.
#[tauri::command]
async fn search_knowledge(
    query: String,
    embed_model: String,
    limit: Option<usize>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<KnowledgeChunk>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/knowledge/search")?;

    let body = serde_json::json!({
        "query": query,
        "limit": limit.unwrap_or(5),
        "embed_model": embed_model,
    });

    let client = reqwest::Client::new();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let chunks: Vec<KnowledgeChunk> =
        serde_json::from_value(data["results"].clone()).unwrap_or_default();

    Ok(chunks)
}

// ---------------------------------------------------------------------------
// Conversation commands
// ---------------------------------------------------------------------------

/// Create a new conversation and return it.
#[tauri::command]
async fn create_conversation(
    title: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Conversation, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/conversations")?;

    let body = serde_json::json!({ "title": title });

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

    let conv: Conversation = resp.json().await.map_err(|e| e.to_string())?;
    Ok(conv)
}

/// Return all conversations, newest first.
#[tauri::command]
async fn list_conversations(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<Conversation>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/conversations")?;

    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let conversations: Vec<Conversation> =
        serde_json::from_value(data["conversations"].clone()).unwrap_or_default();

    Ok(conversations)
}

/// Return all messages for a conversation, oldest first.
#[tauri::command]
async fn get_conversation_messages(
    conversation_id: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<ConversationMessage>, String> {
    let (url, token) = sidecar_url(
        &sidecar_state,
        &format!("/conversations/{conversation_id}/messages"),
    )?;

    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let messages: Vec<ConversationMessage> =
        serde_json::from_value(data["messages"].clone()).unwrap_or_default();

    Ok(messages)
}

/// Persist a user + assistant turn to a conversation.
/// Called after the assistant reply is complete.
#[tauri::command]
async fn save_conversation_turn(
    conversation_id: String,
    user_content: String,
    assistant_content: String,
    assistant_thinking: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(
        &sidecar_state,
        &format!("/conversations/{conversation_id}/turn"),
    )?;

    let body = serde_json::json!({
        "user_content": user_content,
        "assistant_content": assistant_content,
        "assistant_thinking": assistant_thinking,
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

    Ok(())
}

/// Delete a conversation and all its messages.
/// Emits "conversations-updated" with the remaining list.
#[tauri::command]
async fn delete_conversation(
    conversation_id: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(
        &sidecar_state,
        &format!("/conversations/{conversation_id}"),
    )?;

    let client = reqwest::Client::new();
    client
        .delete(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    // Fetch updated list and notify UI
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/conversations")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;

    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let conversations: Vec<Conversation> =
        serde_json::from_value(data["conversations"].clone()).unwrap_or_default();

    app_handle
        .emit(
            "conversations-updated",
            ConversationsUpdatedPayload { conversations },
        )
        .ok();

    Ok(())
}

/// Rename a conversation.
#[tauri::command]
async fn rename_conversation(
    conversation_id: String,
    title: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(
        &sidecar_state,
        &format!("/conversations/{conversation_id}"),
    )?;

    let body = serde_json::json!({ "title": title });

    let client = reqwest::Client::new();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;

    if !resp.status().is_success() {
        let status = resp.status().as_u16();
        return Err(format!("Sidecar returned HTTP {status}"));
    }

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
        .plugin(tauri_plugin_dialog::init())
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
            pick_folder,
            index_knowledge,
            list_knowledge_sources,
            search_knowledge,
            delete_knowledge_source,
            create_conversation,
            list_conversations,
            get_conversation_messages,
            save_conversation_turn,
            delete_conversation,
            rename_conversation,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
