//! Compact the existing window so chat state and in-flight requests stay intact.
use std::sync::Mutex;
use tauri::{LogicalSize, PhysicalPosition, PhysicalSize, WebviewWindow};

pub struct MiniWindow(pub Mutex<Option<Geometry>>);

pub struct Geometry {
    size: PhysicalSize<u32>,
    position: PhysicalPosition<i32>,
    maximized: bool,
}

#[derive(serde::Serialize)]
pub struct MiniStatus {
    active: bool,
    pinned: bool,
}

fn main_only(window: &WebviewWindow) -> Result<(), String> {
    if window.label() != "main" {
        return Err("Mini chat is only available in the main window.".into());
    }
    Ok(())
}

fn restore(window: &WebviewWindow, geometry: &Geometry) -> tauri::Result<()> {
    window.set_always_on_top(false)?;
    window.unmaximize()?;
    window.set_min_size(Some(LogicalSize::new(768.0, 500.0)))?;
    window.set_size(geometry.size)?;
    // A disconnected monitor must not leave the expanded window unreachable.
    let visible = window.available_monitors()?.iter().any(|monitor| {
        let p = monitor.position();
        let s = monitor.size();
        geometry.position.x >= p.x
            && geometry.position.y >= p.y
            && i64::from(geometry.position.x) + 100 < i64::from(p.x) + i64::from(s.width)
            && i64::from(geometry.position.y) + 100 < i64::from(p.y) + i64::from(s.height)
    });
    if visible {
        window.set_position(geometry.position)?;
    } else {
        window.center()?;
    }
    if geometry.maximized {
        window.maximize()?;
    }
    Ok(())
}

#[tauri::command]
pub fn get_mini_state(
    window: WebviewWindow,
    state: tauri::State<'_, MiniWindow>,
) -> Result<MiniStatus, String> {
    main_only(&window)?;
    let saved = state.0.lock().map_err(|e| e.to_string())?;
    Ok(MiniStatus {
        active: saved.is_some(),
        pinned: window.is_always_on_top().map_err(|e| e.to_string())?,
    })
}

#[tauri::command]
pub fn set_mini_mode(
    window: WebviewWindow,
    state: tauri::State<'_, MiniWindow>,
    compact: bool,
) -> Result<MiniStatus, String> {
    main_only(&window)?;
    let mut saved = state.0.lock().map_err(|e| e.to_string())?;
    if compact && saved.is_none() {
        if window.is_fullscreen().map_err(|e| e.to_string())? {
            return Err("Leave full screen before opening mini chat.".into());
        }
        let geometry = Geometry {
            size: window.inner_size().map_err(|e| e.to_string())?,
            position: window.outer_position().map_err(|e| e.to_string())?,
            maximized: window.is_maximized().map_err(|e| e.to_string())?,
        };
        let result = (|| -> tauri::Result<()> {
            window.unmaximize()?;
            window.set_min_size(Some(LogicalSize::new(340.0, 360.0)))?;
            window.set_size(LogicalSize::new(420.0, 560.0))?;
            Ok(())
        })();
        if let Err(error) = result {
            let _ = restore(&window, &geometry);
            return Err(error.to_string());
        }
        *saved = Some(geometry);
    } else if !compact {
        if let Some(geometry) = saved.as_ref() {
            restore(&window, geometry).map_err(|e| e.to_string())?;
            *saved = None;
        }
    }
    Ok(MiniStatus {
        active: saved.is_some(),
        pinned: window.is_always_on_top().map_err(|e| e.to_string())?,
    })
}

#[tauri::command]
pub fn pin_mini_window(
    window: WebviewWindow,
    state: tauri::State<'_, MiniWindow>,
    pinned: bool,
) -> Result<MiniStatus, String> {
    main_only(&window)?;
    let saved = state.0.lock().map_err(|e| e.to_string())?;
    if saved.is_none() {
        return Err("Open mini chat before pinning it.".into());
    }
    window
        .set_always_on_top(pinned)
        .map_err(|e| e.to_string())?;
    Ok(MiniStatus {
        active: true,
        pinned: window.is_always_on_top().map_err(|e| e.to_string())?,
    })
}

#[tauri::command]
pub fn minimize_mini_window(window: WebviewWindow) -> Result<(), String> {
    main_only(&window)?;
    window.minimize().map_err(|e| e.to_string())
}
