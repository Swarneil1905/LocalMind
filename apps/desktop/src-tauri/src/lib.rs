use std::sync::Mutex;
use std::collections::HashMap;
use futures_util::future::{AbortHandle, Abortable};

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

struct ChatRequests(Mutex<HashMap<String, AbortHandle>>);

struct SidecarState(Mutex<Option<SidecarHandle>>);
struct RuntimeState(Mutex<RuntimeStatus>);
#[derive(Clone, Serialize)]
struct RuntimeStatus {
    status: String,
    error: Option<String>,
}
struct OllamaState(Mutex<Option<OllamaStatus>>);
/// Holds the child process for a bundled Ollama we started ourselves.
struct OllamaProcess(Mutex<Option<std::process::Child>>);

// ---------------------------------------------------------------------------
// Windows: hide the console window
// ---------------------------------------------------------------------------

/// `CREATE_NO_WINDOW`. Raw `std::process::Command` (unlike Tauri's own
/// `Command.sidecar()` shell-plugin API) does not set this automatically, so
/// without it launching the bundled Ollama binary pops a visible
/// console/terminal window on Windows.
#[cfg(target_os = "windows")]
fn hide_console(cmd: &mut std::process::Command) {
    use std::os::windows::process::CommandExt;
    cmd.creation_flags(0x0800_0000);
}

#[cfg(not(target_os = "windows"))]
fn hide_console(_cmd: &mut std::process::Command) {}

// ---------------------------------------------------------------------------
// Bundled Ollama helpers
// ---------------------------------------------------------------------------

/// Returns the filename of the bundled Ollama binary for the current platform.
fn ollama_binary_name() -> Option<&'static str> {
    if cfg!(all(target_os = "windows", target_arch = "x86_64")) {
        Some("ollama-x86_64-pc-windows-msvc.exe")
    } else if cfg!(all(target_os = "macos", target_arch = "x86_64")) {
        Some("ollama-x86_64-apple-darwin")
    } else if cfg!(all(target_os = "macos", target_arch = "aarch64")) {
        Some("ollama-aarch64-apple-darwin")
    } else if cfg!(all(target_os = "linux", target_arch = "x86_64")) {
        Some("ollama-x86_64-unknown-linux-gnu")
    } else {
        None
    }
}

/// Ensures Ollama is running. If no system Ollama is detected, launches the
/// bundled binary (if present). Waits up to 30 s for it to become ready.
async fn ensure_ollama(app_handle: tauri::AppHandle) {
    let client = reqwest::Client::builder()
        .no_proxy()
        .timeout(std::time::Duration::from_secs(2))
        .build()
        .unwrap_or_else(|_| localmind_core::http::local_client());

    // Already running (system or previously started) — nothing to do.
    if client
        .get("http://localhost:11434/api/tags")
        .send()
        .await
        .map(|response| response.status().is_success()).unwrap_or(false)
    {
        println!("[localmind] Ollama already running");
        return;
    }

    // Locate bundled binary.
    let Some(bin_name) = ollama_binary_name() else {
        println!("[localmind] Unsupported platform for bundled Ollama");
        return;
    };

    let Ok(resource_dir) = app_handle.path().resource_dir() else {
        println!("[localmind] Could not resolve resource dir");
        return;
    };

    let ollama_path = resource_dir.join("binaries").join(bin_name);
    if !ollama_path.exists() {
        // Dev mode — binary not bundled, expect system Ollama.
        println!("[localmind] Bundled Ollama not found (dev mode?), expecting system Ollama");
        return;
    }

    println!("[localmind] Starting bundled Ollama: {:?}", ollama_path);
    let mut ollama_command = std::process::Command::new(&ollama_path);
    ollama_command
        .arg("serve")
        .env("OLLAMA_HOST", "127.0.0.1:11434")
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null());
    hide_console(&mut ollama_command);
    match ollama_command.spawn() {
        Ok(child) => {
            *app_handle.state::<OllamaProcess>().0.lock().unwrap() = Some(child);
            // Wait up to 30 s for the server to become ready.
            for _ in 0..30 {
                tokio::time::sleep(std::time::Duration::from_secs(1)).await;
                if client
                    .get("http://localhost:11434/api/tags")
                    .send()
                    .await
                    .map(|response| response.status().is_success()).unwrap_or(false)
                {
                    println!("[localmind] Bundled Ollama ready");
                    return;
                }
            }
            eprintln!("[localmind] Bundled Ollama did not become ready in 30 s");
        }
        Err(e) => eprintln!("[localmind] Failed to start bundled Ollama: {e}"),
    }
}

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
    #[serde(default)]
    pub sources: Vec<serde_json::Value>,
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
pub struct TaskWithProject {
    pub id: String,
    pub project_id: String,
    pub title: String,
    pub status: String,
    pub due_at: Option<String>,
    pub created_at: String,
    pub updated_at: String,
    pub project_name: Option<String>,
}

#[derive(Debug, Deserialize, Serialize, Clone)]
pub struct SearchConfig {
    pub provider: String,
    pub privacy_mode: String,
    pub enabled: bool,
    #[serde(default)]
    pub searxng_url: Option<String>,
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
    request_id: String,
    thinking: String,
    content: String,
}

#[derive(Clone, Serialize)]
struct ChatDonePayload {
    request_id: String,
    error: Option<String>,
}

#[derive(Clone, Serialize)]
struct ChatSourcesPayload {
    request_id: String,
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
        None => Err("Local AI is still starting. Please wait a moment and try again.".into()),
    }
}

/// A failed deletion must reject the IPC call before any success event or
/// optimistic UI removal. Shared by the memory/knowledge/project/chat commands.
async fn delete_from_sidecar(client: &reqwest::Client, url: &str, token: &str) -> Result<(), String> {
    client.delete(url)
        .bearer_auth(token)
        .send().await
        .and_then(|response| response.error_for_status())
        .map_err(|error| error.to_string())?;
    Ok(())
}

/// Lets the frontend check sidecar readiness on mount (in case it became ready
/// before the UI subscribed) instead of only relying on the "sidecar-ready" event.
#[tauri::command]
fn get_sidecar_status(state: tauri::State<'_, SidecarState>) -> bool {
    state.0.lock().map(|mut g| {
        g.as_mut().map(|sidecar| sidecar.check_running().is_ok()).unwrap_or(false)
    }).unwrap_or(false)
}

/// Unlike transient events, this snapshot preserves a startup failure for a
/// frontend that subscribes late or reloads after the failure.
#[tauri::command]
fn get_runtime_status(state: tauri::State<'_, RuntimeState>) -> Result<RuntimeStatus, String> {
    state.0.lock().map(|status| status.clone()).map_err(|e| e.to_string())
}

fn runtime_status(app: &tauri::AppHandle, status: &str, error: Option<String>) {
    if let Ok(mut state) = app.state::<RuntimeState>().0.lock() {
        *state = RuntimeStatus { status: status.into(), error };
    }
}

async fn start_sidecar(app: &tauri::AppHandle) -> Result<(), String> {
    // Tauri external binaries are installed beside the app executable.
    let bundled = std::env::current_exe().ok().and_then(|exe| {
        exe.parent().map(|directory| {
            #[cfg(target_os = "windows")]
            let name = "localmind-sidecar.exe";
            #[cfg(not(target_os = "windows"))]
            let name = "localmind-sidecar";
            directory.join(name)
        })
    });
    let result = if let Some(path) = bundled.filter(|path| {
        path.metadata().map(|metadata| metadata.len() > 0).unwrap_or(false)
    }) {
        SidecarHandle::launch_binary(path)
    } else {
        SidecarHandle::launch()
    };
    let mut sidecar = result.map_err(|error| error.to_string())?;
    sidecar.wait_until_ready().await.map_err(|error| error.to_string())?;
    *app.state::<SidecarState>().0.lock().map_err(|e| e.to_string())? = Some(sidecar);
    runtime_status(app, "ready", None);
    app.emit("sidecar-ready", ()).ok();
    Ok(())
}

fn sidecar_failed(app: &tauri::AppHandle, error: String) {
    eprintln!("[localmind] {error}");
    runtime_status(app, "failed", Some(error.clone()));
    app.emit("sidecar-failed", error).ok();
}

#[tauri::command]
async fn restart_sidecar(app: tauri::AppHandle) -> Result<(), String> {
    {
        let state = app.state::<RuntimeState>();
        let mut status = state.0.lock().map_err(|e| e.to_string())?;
        if status.status != "failed" {
            return Err("Local AI is already running or starting.".into());
        }
        *status = RuntimeStatus { status: "starting".into(), error: None };
    }
    // No restart of a healthy runtime: it would interrupt active work.
    *app.state::<SidecarState>().0.lock().map_err(|e| e.to_string())? = None;
    if let Err(error) = start_sidecar(&app).await {
        sidecar_failed(&app, error.clone());
        return Err(error);
    }
    Ok(())
}

async fn supervise_sidecar(app: tauri::AppHandle) {
    loop {
        tokio::time::sleep(std::time::Duration::from_secs(2)).await;
        let failure = {
            let state = app.state::<SidecarState>();
            let Ok(mut sidecar) = state.0.lock() else { return; };
            let error = sidecar.as_mut().and_then(|s| s.check_running().err());
            if error.is_some() { sidecar.take(); }
            error
        };
        if let Some(error) = failure {
            if let Ok(mut requests) = app.state::<ChatRequests>().0.lock() {
                for (request_id, request) in requests.drain() {
                    request.abort();
                    app.emit("chat-done", ChatDonePayload {
                        request_id,
                        error: Some("Local AI stopped while answering. Restart it and try again.".into()),
                    }).ok();
                }
            }
            sidecar_failed(&app, error.to_string());
        }
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

fn take_sse_frame(buffer: &mut Vec<u8>) -> Option<String> {
    let end = buffer.windows(2).position(|w| w == b"\n\n")?;
    let frame = String::from_utf8_lossy(&buffer[..end]).into_owned();
    buffer.drain(..end + 2);
    Some(frame)
}

fn take_json_line(buffer: &mut Vec<u8>) -> Option<Vec<u8>> {
    let end = buffer.iter().position(|byte| *byte == b'\n')?;
    Some(buffer.drain(..=end).collect())
}

fn parse_pull_progress(line: &[u8]) -> Result<serde_json::Value, String> {
    let value: serde_json::Value = serde_json::from_slice(line)
        .map_err(|error| format!("Could not read model download progress: {error}"))?;
    if let Some(error) = value["error"].as_str() {
        return Err(error.to_string());
    }
    Ok(value)
}

#[cfg(test)]
mod stream_tests {
    use super::{parse_pull_progress, take_json_line, take_sse_frame};

    #[test]
    fn mutations_reject_http_failures_before_reporting_success() {
        use std::io::{Read, Write};
        let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap();
        for (status, succeeds) in [("204 No Content", true), ("404 Not Found", false), ("500 Internal Server Error", false)] {
            let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
            let url = format!("http://{}/memory/test-id", listener.local_addr().unwrap());
            let server = std::thread::spawn(move || {
                let (mut stream, _) = listener.accept().unwrap();
                stream.set_read_timeout(Some(std::time::Duration::from_secs(3))).unwrap();
                let mut request = Vec::new();
                let mut buffer = [0u8; 1024];
                while !request.windows(4).any(|bytes| bytes == b"\r\n\r\n") {
                    let size = stream.read(&mut buffer).unwrap();
                    assert!(size > 0);
                    request.extend_from_slice(&buffer[..size]);
                }
                write!(stream, "HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").unwrap();
                String::from_utf8(request).unwrap()
            });
            let client = localmind_core::http::local_client();
            let result = runtime.block_on(super::delete_from_sidecar(&client, &url, "test-launch-token"));
            assert_eq!(result.is_ok(), succeeds, "unexpected mutation result for {status}");
            let request = server.join().unwrap();
            assert!(request.starts_with("DELETE /memory/test-id HTTP/1.1"));
            assert!(request.to_ascii_lowercase().contains("authorization: bearer test-launch-token"));
        }
    }

    #[test]
    fn model_download_preserves_split_json_chunks() {
        let wire = "{\"status\":\"pulling 模型\"}\n{\"status\":\"success\"}\n";
        let mut buffer = Vec::new();
        let mut statuses = Vec::new();
        for byte in wire.as_bytes() {
            buffer.push(*byte);
            while let Some(line) = take_json_line(&mut buffer) {
                statuses.push(parse_pull_progress(&line).unwrap()["status"].as_str().unwrap().to_owned());
            }
        }
        assert_eq!(statuses, ["pulling 模型", "success"]);
    }

    #[test]
    fn model_download_errors_are_not_reported_as_success() {
        assert_eq!(parse_pull_progress(br#"{"error":"model not found"}"#).unwrap_err(), "model not found");
    }

    #[test]
    fn preserves_unicode_split_across_network_chunks() {
        let wire = "data: {\"content\":\"你好 👋\"}\n\n";
        let mut buffer = Vec::new();
        let mut frames = Vec::new();
        for byte in wire.as_bytes() {
            buffer.push(*byte);
            if let Some(frame) = take_sse_frame(&mut buffer) { frames.push(frame); }
        }
        assert_eq!(frames, vec![wire.trim_end()]);
        assert!(buffer.is_empty());
    }

    #[test]
    fn retains_partial_next_event() {
        let mut buffer = b"data: first\n\ndata: sec".to_vec();
        assert_eq!(take_sse_frame(&mut buffer).as_deref(), Some("data: first"));
        assert_eq!(take_sse_frame(&mut buffer), None);
        buffer.extend_from_slice(b"ond\n\n");
        assert_eq!(take_sse_frame(&mut buffer).as_deref(), Some("data: second"));
    }
}

#[tauri::command]
fn cancel_chat(request_id: String, requests: tauri::State<'_, ChatRequests>) -> Result<(), String> {
    if let Some(handle) = requests.0.lock().map_err(|e| e.to_string())?.remove(&request_id) {
        handle.abort();
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
#[tauri::command]
async fn chat_stream(
    request_id: String,
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
    requests: tauri::State<'_, ChatRequests>,
) -> Result<(), String> {
    let (handle, registration) = AbortHandle::new_pair();
    {
        let mut active = requests.0.lock().map_err(|e| e.to_string())?;
        for (_, previous) in active.drain() { previous.abort(); }
        active.insert(request_id.clone(), handle);
    }
    let result = Abortable::new(chat_stream_inner(
        request_id.clone(), message, model, history, memory_enabled, knowledge_enabled,
        embed_model, hyde_enabled, web_search_enabled, app_handle, sidecar_state,
    ), registration).await;
    requests.0.lock().map_err(|e| e.to_string())?.remove(&request_id);
    result.unwrap_or(Ok(()))
}

#[allow(clippy::too_many_arguments)]
async fn chat_stream_inner(
    request_id: String,
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
    let client = reqwest::Client::builder()
        .no_proxy()
        .connect_timeout(std::time::Duration::from_secs(5))
        .timeout(std::time::Duration::from_secs(300))
        .build().map_err(|e| e.to_string())?;

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
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;

    if !resp.status().is_success() {
        let status = resp.status().as_u16();
        return Err(format!("Sidecar returned HTTP {status}"));
    }

    let mut stream = resp.bytes_stream();
    let mut buffer: Vec<u8> = Vec::new();

    while let Some(chunk) = stream.next().await {
        let bytes = chunk.map_err(|e| e.to_string())?;
        buffer.extend_from_slice(&bytes);

        while let Some(event_str) = take_sse_frame(&mut buffer) {
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
                        app_handle.emit("chat-token", ChatTokenPayload { request_id: request_id.clone(), content, thinking: value.get("thinking").and_then(|v| v.as_str()).unwrap_or("").to_string() }).ok();
                    }
                    Some("sources") => {
                        let sources: Vec<serde_json::Value> = value
                            .get("sources")
                            .and_then(|s| s.as_array())
                            .cloned()
                            .unwrap_or_default();
                        app_handle.emit("chat-sources", ChatSourcesPayload { request_id: request_id.clone(), sources }).ok();
                    }
                    Some("done") => {
                        app_handle.emit("chat-done", ChatDonePayload { request_id: request_id.clone(), error: None }).ok();
                        return Ok(());
                    }
                    Some("error") => {
                        let msg = value
                            .get("content")
                            .and_then(|c| c.as_str())
                            .unwrap_or("Unknown error")
                            .to_string();
                        app_handle
                            .emit("chat-done", ChatDonePayload { request_id: request_id.clone(), error: Some(msg) })
                            .ok();
                        return Ok(());
                    }
                    _ => {}
                }
            }
        }
    }

    Err("The response ended unexpectedly. Please try again.".into())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/memory/list")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let memories: Vec<MemoryEntry> =
        serde_json::from_value(data["memories"].clone()).unwrap_or_default();
    app_handle.emit("memories-updated", MemoriesUpdatedPayload { memories }).ok();
    Ok(())
}

#[tauri::command]
async fn delete_all_memories(
    app_handle: tauri::AppHandle,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, "/memory/all")?;
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
    // Emit empty memories list so the UI clears immediately
    app_handle.emit("memories-updated", MemoriesUpdatedPayload { memories: vec![] }).ok();
    Ok(())
}

#[tauri::command]
async fn list_memory_links(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<MemoryLink>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/memory/links")?;
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
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
    let client = localmind_core::http::local_client();
    client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;

    let sidecar_port_token: Option<(u16, String)> = {
        let guard = sidecar_state.0.lock().map_err(|e| e.to_string())?;
        guard.as_ref().map(|s| (s.port, s.token.clone()))
    };
    let app_clone = app_handle.clone();
    if let Some((port, list_token)) = sidecar_port_token {
        let list_url = format!("http://127.0.0.1:{port}/knowledge/sources");
        tauri::async_runtime::spawn(async move {
            let client = localmind_core::http::local_client();
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/knowledge/sources")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    sources: Option<Vec<serde_json::Value>>,
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
        "sources": sources.unwrap_or_default(),
    });
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/conversations")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
    let (list_url, list_token) = sidecar_url(&sidecar_state, "/projects")?;
    let resp = client
        .get(&list_url)
        .header("Authorization", format!("Bearer {list_token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    Ok(serde_json::from_value(data["tasks"].clone()).unwrap_or_default())
}

#[tauri::command]
async fn get_followup_questions(
    last_user: String,
    last_assistant: String,
    model: Option<String>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<String>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/chat/followups")?;
    let body = serde_json::json!({
        "last_user": last_user,
        "last_assistant": last_assistant,
        "model": model.unwrap_or_else(|| "qwen2.5:1.5b".to_string()),
    });
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    let data: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    Ok(serde_json::from_value(data["questions"].clone()).unwrap_or_default())
}

#[tauri::command]
async fn list_all_tasks(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<Vec<TaskWithProject>, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/projects/tasks/all")?;
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
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
    let client = localmind_core::http::local_client();
    client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    let client = localmind_core::http::local_client();
    let resp = client
        .patch(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
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
    "You are LocalMind, a private local AI assistant. \
     Be concise and direct. No unnecessary filler, no \"Certainly!\", no \"Of course!\". \
     When real-time context from Gmail or other apps is provided, use ONLY those facts. \
     Never guess, hallucinate, or fill in details not explicitly stated in the context. \
     If you don't know something from the provided context, say so plainly. \
     Always address the user as 'you' — never use any name or refer to them in third person."
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
    format!(
        "{base}\n\nBackground facts you remember (use as context, NOT to analyze — always respond directly to the user as 'you'):\n{facts}"
    )
}

// ---------------------------------------------------------------------------
// Model management commands
// ---------------------------------------------------------------------------

/// Returns the names of all locally pulled Ollama models.
#[tauri::command]
async fn list_ollama_models() -> Result<Vec<String>, String> {
    let client = reqwest::Client::builder()
        .no_proxy()
        .timeout(std::time::Duration::from_secs(5))
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client
        .get("http://localhost:11434/api/tags")
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;

    let json: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let models = json["models"]
        .as_array()
        .unwrap_or(&vec![])
        .iter()
        .filter_map(|m| m["name"].as_str().map(String::from))
        .collect();

    Ok(models)
}

/// Pulls an Ollama model, emitting `pull-progress` events as it downloads.
/// Event payload: `{ model, status, percent, completed, total }`.
#[tauri::command]
async fn pull_ollama_model(app: tauri::AppHandle, name: String) -> Result<(), String> {
    let client = reqwest::Client::builder()
        .no_proxy()
        .timeout(std::time::Duration::from_secs(3600))
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client
        .post("http://localhost:11434/api/pull")
        .json(&serde_json::json!({ "name": name, "stream": true }))
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;

    let mut stream = resp.bytes_stream();
    let mut buffer = Vec::new();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| e.to_string())?;
        buffer.extend_from_slice(&chunk);
        while let Some(line) = take_json_line(&mut buffer) {
            if line.iter().all(u8::is_ascii_whitespace) {
                continue;
            }
            {
                let val = parse_pull_progress(&line)?;
                let status = val["status"].as_str().unwrap_or("").to_string();
                let completed = val["completed"].as_u64().unwrap_or(0);
                let total = val["total"].as_u64().unwrap_or(1);
                let percent = completed.saturating_mul(100).checked_div(total).unwrap_or(0).min(100);

                let _ = app.emit(
                    "pull-progress",
                    serde_json::json!({
                        "model": &name,
                        "status": &status,
                        "percent": percent,
                        "completed": completed,
                        "total": total,
                    }),
                );

                if status == "success" {
                    return Ok(());
                }
            }
        }
    }
    // Some HTTP implementations omit the trailing newline on the final record.
    if !buffer.is_empty() && parse_pull_progress(&buffer)?["status"] == "success" {
        return Ok(());
    }
    Err("The model download ended before it completed. Please retry.".into())
}

// ---------------------------------------------------------------------------
// Auto-updater commands
// ---------------------------------------------------------------------------

#[tauri::command]
async fn check_for_updates(app: tauri::AppHandle) -> Result<Option<serde_json::Value>, String> {
    use tauri_plugin_updater::UpdaterExt;
    let updater = app.updater().map_err(|e| e.to_string())?;
    match updater.check().await {
        Ok(Some(update)) => Ok(Some(serde_json::json!({
            "version": update.version,
            "currentVersion": update.current_version,
            "body": update.body,
        }))),
        Ok(None) => Ok(None),
        Err(e) => Err(e.to_string()),
    }
}

#[tauri::command]
async fn install_update(app: tauri::AppHandle) -> Result<(), String> {
    use tauri_plugin_updater::UpdaterExt;
    let updater = app.updater().map_err(|e| e.to_string())?;
    if let Some(update) = updater.check().await.map_err(|e| e.to_string())? {
        update
            .download_and_install(|_downloaded, _total| {}, || {})
            .await
            .map_err(|e| e.to_string())?;
        app.restart();
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Connector commands
// ---------------------------------------------------------------------------

#[tauri::command]
async fn list_connectors(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<serde_json::Value, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/connectors")?;
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    resp.json::<serde_json::Value>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn connect_connector(
    connector_id: String,
    credentials: Option<serde_json::Value>,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<serde_json::Value, String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/connectors/{connector_id}/connect"))?;
    let body = serde_json::json!({ "credentials": credentials.unwrap_or(serde_json::json!({})) });
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .json(&body)
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    resp.json::<serde_json::Value>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn disconnect_connector(
    connector_id: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<(), String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/connectors/{connector_id}"))?;
    let client = localmind_core::http::local_client();
    delete_from_sidecar(&client, &url, &token).await?;
    Ok(())
}

#[tauri::command]
async fn sync_connector(
    connector_id: String,
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<serde_json::Value, String> {
    let (url, token) = sidecar_url(&sidecar_state, &format!("/connectors/{connector_id}/sync"))?;
    let client = localmind_core::http::local_client();
    let resp = client
        .post(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    resp.json::<serde_json::Value>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn list_people(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<serde_json::Value, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/connectors/people")?;
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    resp.json::<serde_json::Value>().await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn gmail_debug(
    sidecar_state: tauri::State<'_, SidecarState>,
) -> Result<serde_json::Value, String> {
    let (url, token) = sidecar_url(&sidecar_state, "/connectors/gmail/debug")?;
    let client = localmind_core::http::local_client();
    let resp = client
        .get(&url)
        .header("Authorization", format!("Bearer {token}"))
        .send()
        .await
        .and_then(|response| response.error_for_status())
        .map_err(|e| e.to_string())?;
    resp.json::<serde_json::Value>().await.map_err(|e| e.to_string())
}

// ---------------------------------------------------------------------------
// App entry point
// ---------------------------------------------------------------------------

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .manage(ChatRequests(Mutex::new(HashMap::new())))
        .manage(SidecarState(Mutex::new(None)))
        .manage(RuntimeState(Mutex::new(RuntimeStatus { status: "starting".into(), error: None })))
        .manage(OllamaState(Mutex::new(None)))
        .manage(OllamaProcess(Mutex::new(None)))
        .setup(|app| {
            let app_handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                // 1. Ensure Ollama is running (bundled or system).
                ensure_ollama(app_handle.clone()).await;

                // Startup and retry share one launch path. Keep failures queryable.
                if let Err(error) = start_sidecar(&app_handle).await {
                    sidecar_failed(&app_handle, error);
                }
                tauri::async_runtime::spawn(supervise_sidecar(app_handle.clone()));

                // 3. Record Ollama status for the settings page.
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
        .on_window_event(|window, event| {
            // Stop owned processes when the last app window closes.
            if let tauri::WindowEvent::Destroyed = event {
                let handle = window.app_handle().clone();
                if handle.webview_windows().keys().any(|label| label != window.label()) {
                    return;
                }
                let sidecar = {
                    let state = handle.state::<SidecarState>();
                    state.0.lock().ok().and_then(|mut guard| guard.take())
                };
                drop(sidecar);
                // Extract the child process while the State/MutexGuard are still
                // alive, then drop them before touching the child — this satisfies
                // the borrow checker (guard must be dropped before State, which
                // must be dropped before AppHandle).
                let child: Option<std::process::Child> = {
                    let state: tauri::State<'_, OllamaProcess> = handle.state();
                    state.0.lock().ok().and_then(|mut g| g.take())
                };
                if let Some(mut c) = child {
                    let _ = c.kill();
                    let _ = c.wait();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            get_ollama_status,
            get_sidecar_status,
            get_runtime_status,
            restart_sidecar,
            chat_stream,
            cancel_chat,
            extract_memories,
            list_memories,
            delete_memory,
            delete_all_memories,
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
            list_all_tasks,
            update_task,
            delete_task,
            assign_conversation_to_project,
            get_project_conversations,
            get_search_config,
            update_search_config,
            list_memory_links,
            create_memory_link,
            delete_memory_link,
            get_followup_questions,
            list_ollama_models,
            pull_ollama_model,
            check_for_updates,
            install_update,
            list_connectors,
            connect_connector,
            disconnect_connector,
            sync_connector,
            list_people,
            gmail_debug,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
