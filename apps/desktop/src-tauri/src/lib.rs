use std::sync::Mutex;

use localmind_core::sidecar::SidecarHandle;
use tauri::Manager;

/// Tauri managed state holding the Python sidecar handle.
///
/// Wrapped in Mutex<Option<...>> because:
/// - Mutex: Tauri state must be Sync; Child is Send but not Sync
/// - Option: the handle is None until the health check passes
struct SidecarState(Mutex<Option<SidecarHandle>>);

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(SidecarState(Mutex::new(None)))
        .setup(|app| {
            let app_handle = app.handle().clone();

            // Sidecar startup runs in a separate async task so it does not
            // block the Tauri event loop. The UI is visible immediately;
            // the sidecar becomes available within a few seconds.
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
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
