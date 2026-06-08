use std::sync::Mutex;

use localmind_core::{
    ollama::{OllamaStatus, check as ollama_check},
    sidecar::SidecarHandle,
};
use tauri::Manager;

// ---------------------------------------------------------------------------
// Managed state
// ---------------------------------------------------------------------------

/// Holds the Python sidecar handle after it passes its health check.
struct SidecarState(Mutex<Option<SidecarHandle>>);

/// Cached Ollama status refreshed on startup (and on demand via command).
struct OllamaState(Mutex<Option<OllamaStatus>>);

// ---------------------------------------------------------------------------
// Tauri commands
// ---------------------------------------------------------------------------

/// Return the last known Ollama status. The UI calls this once on mount and
/// whenever it needs a refresh (e.g. after the user installs Ollama).
#[tauri::command]
async fn get_ollama_status(state: tauri::State<'_, OllamaState>) -> Result<OllamaStatus, String> {
    // Re-check live every time the UI asks — the call is fast (3 s timeout)
    let status = ollama_check().await;
    *state.0.lock().map_err(|e| e.to_string())? = Some(status.clone());
    Ok(status)
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

            // Sidecar startup runs in a separate async task so it does not
            // block the Tauri event loop.
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
        .invoke_handler(tauri::generate_handler![get_ollama_status])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
