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

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct HistoryEntry {
    pub role: String,
    pub content: String,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct MemoryEntry {
    pub id: String,
    pub content: String,
    pub created_at: String,
}

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

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct KnowledgeChunk {
    pub id: String,
    pub source_id: String,
    pub file_path: String,
    pub chunk_index: i64,
    pub content: String,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct Conversation {
    pub id: String,
    pub title: String,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct ConversationMessage {
    pub id: String,
    pub conversation_id: String,
    pub role: String,
    pub content: String,
    pub thinking: Option<String>,
    pub created_at: String,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct Project {
    pub id: String,
    pub name: String,
    pub path: Option<String>,
    pub summary: Option<String>,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct Task {
    pub id: String,
    pub project_id: String,
    pub title: String,
    pub status: String,
    pub due_at: Option<String>,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct SearchConfig {
    pub provider: String,
    pub privacy_mode: String,
    pub enabled: bool,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct MemoryLink {
    pub id: String,
    pub from_id: String,
    pub to_id: String,
    pub relation: String,
    pub created_at: String,
    pub from_content: String,
    pub to_content: String,
}

// ---------------------------------------------------------------------------
// Event payloads
// ---------------------------------------------------------------------------

#[derive(Clone, Serialize)]
struct ChatTokenPayload {
    content: String,
}

#[derive(Clone, Serialize)]
struct ChatDonePayload {
    error: Option<String>,
}

#[derive(Clone, Serialize)]
struct ChatSourcesPayload {
    sources: Vec<serde_json::Value>,
}

#[derive(Clone, Serialize)]
struct MemoriesUpdatedPayload {
    memories: Vec<MemoryEntry>,
}

#[derive(Clone, Serialize)]
struct KnowledgeUpdatedPayload {
    sources: Vec<KnowledgeSource>,
}

#[derive(Clone, Serialize)]
struct ConversationsUpdatedPayload {
    conversations: Vec<Conversation>,
}

#[derive(Clone, Serialize)]
struct ProjectsUpdatedPayload {
    projects: Vec<Project>,
}

// ---------------------------------------------------------------------------
// Helper
// ---------------------------------------------------------------------------

fn sidecar_url(state: &SidecarState, path: &str) -> Result<(String, String), String> {
    let guard = state.0.lock().map_err(|e| e.to_string())?;
    match guard.as_ref() {
        Some(s) => Ok((format!("http://127.0.0.1:{}{path}", s.port), s.token.clone())),
        None => Err("Sidecar not ready yet".into()),
    }
}

// ---------------------------------------------------------------------------
// Ollama command
// ---------------------------------------------------------------------------

#[tauri::command]
async fn get_ollama_status(
    state: tauri::State<'_, OllamaState>,
) -> Result<OllamaStatus, String> {
    let status = ollama_check().await;
    *state.0.lock().map_err(|e| e.to_string())? = Some(status.clone());
    Ok(status)
}

// ---------------------------------------------------------------------------
// Chat command
// ---------------------------------------------------------------------------

#[allow(clippy::too_many_arguments)]
#[tauri::command]
async fn chat_stream(
    message: String,
    model: String,
    history: Vec<HistoryEntry>,
    memory_enabled: bool,
    knowledge_enabled: bool,
    embed_model: String,
    hyde_enabled: bool,
    web_search_enabled: bool,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/chat/stream")?;
    let client = reqwest::Client::new();

    let mut system_prompt = if memory_enabled {
        let (list_url, list_token) = sidecar_url(&sidecar_state, "/memory/list")?;
        let memories: Vec<MemoryEntry> = match client
            .get(&list_url)
            .header("Authorization", format!("Bearer {list_token}"))
            .send()
            .await
        {
            Ok(resp) if resp.status().is_success() => resp
                .json::<serde_json::Value>()
                .await
                .ok()
                .and_then(|v| serde_json::from_value(v["memories"].clone()).ok())
                .unwrap_or_default(),
            _ => vec![],
        };

        // 1-hop link expansion: fetch all links and append linked facts not already listed
        let (links_url, links_token) = sidecar_url(&sidecar_state, "/memory/links")?;
        let links: Vec<MemoryLink> = match client
            .get(&links_url)
            .header("Authorization", format!("Bearer {links_token}"))
            .send()
            .await
        {
            Ok(resp) if resp.status().is_success() => resp
                .json::<serde_json::Value>()
                .await
                .ok()
                .and_then(|v| serde_json::from_value(v["links"].clone()).ok())
                .unwrap_or_default(),
            _ => vec![],
        };

        let mut prompt = build_system_prompt(&memories);
        if !links.is_empty() {
            let link_lines: Vec<String> = links
                .iter()
                .map(|l| format!("- \"{}\" {} \"{}\"", l.from_content, l.relation, l.to_content))
                .collect();
            prompt = format!(
                "{prompt}\n\nMemory connections:\n{}",
                link_lines.join("\n")
            );
        }
        prompt
    } else {
        default_system_prompt()
    };

    if knowledge_enabled {
        let (search_url, search_token) = sidecar_url(&sidecar_state, "/knowledge/search")?;
        let search_body = serde_json::json!({
            "query": message,
            "limit": 5,
            "embed_model": embed_model,
            "hyde_enabled": hyde_enabled,
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
                            "{system_prompt}\n\nRelevant context from the user knowledge base:\n\n{context}"
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
        "web_search_enabled": web_search_enabled,
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
                        app_handle.emit("chat-token", ChatTokenPayload { content }).ok();
                    }
                    Some("sources") => {
                        let sources: Vec<serde_json::Value> = value
                            .get("sources")
                            .and_then(|s| s.as_array())
                            .cloned()
                            .unwrap_or_default();
                        app_handle.emit("chat-sources", ChatSourcesPayload { sources }).ok();
                    }
                    Some("done") => {
                        app_handle.emit("chat-done", ChatDonePayload { error: None }).ok();
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

    app_handle.emit("chat-done", ChatDonePayload { error: None }).ok();
    Ok(())
}

// ---------------------------------------------------------------------------
// Memory commands
// ---------------------------------------------------------------------------

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
        return Ok(());
    }
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let memories: Vec<MemoryEntry> =
        serde_json::from_value(data["memories"].clone()).unwrap_or_default();
    app_handle.emit("memories-updated", MemoriesUpdatedPayload { memories }).ok();
    Ok(())
}

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
    Ok(serde_json::from_value(data["memories"].clone()).unwrap_or_default())
}

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
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/memory/list")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let memories: Vec<MemoryEntry> =
        serde_json::from_value(data["memories"].clone()).unwrap_or_default();
    app_handle.emit("memories-updated", MemoriesUpdatedPayload { memories }).ok();
    Ok(())
}

#[tauri::command]
async fn list_memory_links(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<MemoryLink>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/memory/links")?;
    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    Ok(serde_json::from_value(data["links"].clone()).unwrap_or_default())
}

#[tauri::command]
async fn create_memory_link(
    from_id: String,
    to_id: String,
    relation: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<MemoryLink, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/memory/links")?;
    let body = serde_json::json!({ "from_id": from_id, "to_id": to_id, "relation": relation });
    let client = reqwest::Client::new();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    resp.json::<MemoryLink>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn delete_memory_link(
    link_id: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/memory/link/{link_id}"))?;
    let client = reqwest::Client::new();
    client
        .delete(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    Ok(())
}

// ---------------------------------------------------------------------------
// Knowledge commands
// ---------------------------------------------------------------------------

#[tauri::command]
async fn pick_folder(app_handle: tauri::AppHandle) -> Result<Option<String>, String> {
    let (tx, rx) = tokio::sync::oneshot::channel::<Option<String>>();
    app_handle.dialog().file().pick_folder(move |path| {
        let result = path.map(|p| p.to_string());
        tx.send(result).ok();
    });
    rx.await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn index_knowledge(
    path: String,
    embed_model: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/knowledge/index")?;
    let body = serde_json::json!({ "path": path, "embed_model": embed_model });
    let client = reqwest::Client::new();
    client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;

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
                    break;
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
                app_clone
                    .emit("knowledge-updated", KnowledgeUpdatedPayload { sources: sources.clone() })
                    .ok();
                if sources.iter().all(|s| s.status != "indexing") {
                    break;
                }
            }
        });
    }
    Ok(())
}

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
    Ok(serde_json::from_value(data["sources"].clone()).unwrap_or_default())
}

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
    app_handle.emit("knowledge-updated", KnowledgeUpdatedPayload { sources }).ok();
    Ok(())
}

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
    Ok(serde_json::from_value(data["results"].clone()).unwrap_or_default())
}

// ---------------------------------------------------------------------------
// Conversation commands
// ---------------------------------------------------------------------------

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
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    resp.json::<Conversation>().await.map_err(|e| e.to_string())
}

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
    Ok(serde_json::from_value(data["conversations"].clone()).unwrap_or_default())
}

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
    Ok(serde_json::from_value(data["messages"].clone()).unwrap_or_default())
}

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
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    Ok(())
}

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
        .emit("conversations-updated", ConversationsUpdatedPayload { conversations })
        .ok();
    Ok(())
}

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
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Project commands
// ---------------------------------------------------------------------------

#[tauri::command]
async fn create_project(
    name: String,
    path: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Project, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/projects")?;
    let body = serde_json::json!({ "name": name, "path": path });
    let client = reqwest::Client::new();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    resp.json::<Project>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn list_projects(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<Project>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/projects")?;
    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    Ok(serde_json::from_value(data["projects"].clone()).unwrap_or_default())
}

#[tauri::command]
async fn update_project(
    project_id: String,
    name: Option<String>,
    path: Option<String>,
    summary: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Project, String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/projects/{project_id}"))?;
    let body = serde_json::json!({ "name": name, "path": path, "summary": summary });
    let client = reqwest::Client::new();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    resp.json::<Project>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn delete_project(
    project_id: String,
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/projects/{project_id}"))?;
    let client = reqwest::Client::new();
    client
        .delete(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/projects")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let projects: Vec<Project> =
        serde_json::from_value(data["projects"].clone()).unwrap_or_default();
    app_handle.emit("projects-updated", ProjectsUpdatedPayload { projects }).ok();
    Ok(())
}

#[tauri::command]
async fn generate_project_summary(
    project_id: String,
    speed_model: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<String, String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/projects/{project_id}/summary"))?;
    let body = serde_json::json!({ "speed_model": speed_model });
    let client = reqwest::Client::new();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    Ok(data["summary"].as_str().unwrap_or("").to_string())
}

#[tauri::command]
async fn create_task(
    project_id: String,
    title: String,
    due_at: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Task, String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/projects/{project_id}/tasks"))?;
    let body = serde_json::json!({ "title": title, "due_at": due_at });
    let client = reqwest::Client::new();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    resp.json::<Task>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn list_tasks(
    project_id: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<Task>, String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/projects/{project_id}/tasks"))?;
    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    Ok(serde_json::from_value(data["tasks"].clone()).unwrap_or_default())
}

#[tauri::command]
async fn update_task(
    project_id: String,
    task_id: String,
    title: Option<String>,
    status: Option<String>,
    due_at: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(
        &sidecar_state,
        &format!("/projects/{project_id}/tasks/{task_id}"),
    )?;
    let body = serde_json::json!({ "title": title, "status": status, "due_at": due_at });
    let client = reqwest::Client::new();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    Ok(())
}

#[tauri::command]
async fn delete_task(
    project_id: String,
    task_id: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(
        &sidecar_state,
        &format!("/projects/{project_id}/tasks/{task_id}"),
    )?;
    let client = reqwest::Client::new();
    client
        .delete(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
async fn assign_conversation_to_project(
    conversation_id: String,
    project_id: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/projects/assign-conversation")?;
    let body = serde_json::json!({
        "conversation_id": conversation_id,
        "project_id": project_id,
    });
    let client = reqwest::Client::new();
    client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
async fn get_project_conversations(
    project_id: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<Conversation>, String> {
    let (url, token) = sidecar_url(
        &sidecar_state,
        &format!("/projects/{project_id}/conversations"),
    )?;
    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    Ok(serde_json::from_value(data["conversations"].clone()).unwrap_or_default())
}

// ---------------------------------------------------------------------------
// Search config commands
// ---------------------------------------------------------------------------

#[tauri::command]
async fn get_search_config(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<SearchConfig, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/search-config")?;
    let client = reqwest::Client::new();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    resp.json::<SearchConfig>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn update_search_config(
    provider: String,
    privacy_mode: String,
    enabled: bool,
    api_key: Option<String>,
    searxng_url: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<SearchConfig, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/search-config")?;
    let body = serde_json::json!({
        "provider": provider,
        "privacy_mode": privacy_mode,
        "enabled": enabled,
        "api_key": api_key,
        "searxng_url": searxng_url,
    });
    let client = reqwest::Client::new();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !resp.status().is_success() {
        return Err(format!("Sidecar returned HTTP {}", resp.status().as_u16()));
    }
    resp.json::<SearchConfig>().await.map_err(|e| e.to_string())
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
            create_project,
            list_projects,
            update_project,
            delete_project,
            generate_project_summary,
            create_task,
            list_tasks,
            update_task,
            delete_task,
            assign_conversation_to_project,
            get_project_conversations,
            get_search_config,
            update_search_config,
            list_memory_links,
            create_memory_link,
            delete_memory_link,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
