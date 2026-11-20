#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use reqwest::{Client, Method};
use serde_json::Value;
use tauri::{Manager, RunEvent, State};

mod manager;

const GM_BASE_URL: &str = "http://127.0.0.1:5180";

#[tauri::command]
async fn gm_request(
    http: State<'_, Client>,
    method: String,
    path: String,
    body: Option<Value>,
) -> Result<Value, String> {
    let parsed_method =
        Method::from_bytes(method.trim().to_ascii_uppercase().as_bytes()).map_err(|e| e.to_string())?;
    let clean_path = if path.starts_with('/') {
        path
    } else {
        format!("/{path}")
    };
    let url = format!("{GM_BASE_URL}{clean_path}");
    let mut req = http.request(parsed_method, &url);
    if let Some(payload) = body {
        req = req.json(&payload);
    }

    let resp = req
        .send()
        .await
        .map_err(|e| format!("No se pudo conectar con Trimūrti ({GM_BASE_URL}): {e}"))?;
    let status = resp.status();
    let text = resp.text().await.unwrap_or_default();
    let parsed: Value = serde_json::from_str(&text).unwrap_or_else(|_| Value::String(text.clone()));

    if !status.is_success() {
        let msg = parsed
            .get("error")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string())
            .unwrap_or_else(|| format!("HTTP {}: {text}", status.as_u16()));
        return Err(msg);
    }
    Ok(parsed)
}

#[tauri::command]
fn manager_status(state: State<'_, manager::ManagerState>) -> manager::Status {
    manager::get_status(&state)
}

#[tauri::command]
fn manager_start(state: State<'_, manager::ManagerState>) -> Result<String, String> {
    manager::start_server(&state)
}

#[tauri::command]
fn manager_stop(state: State<'_, manager::ManagerState>) -> Result<String, String> {
    manager::stop_server(&state)
}

#[tauri::command]
fn manager_launch_game() -> Result<String, String> {
    manager::launch_game()
}

#[tauri::command]
fn manager_log_tail(lines: Option<usize>) -> String {
    manager::read_log_tail(lines.unwrap_or(120))
}

#[tauri::command]
fn manager_save_settings(game_dir: String, workspace_dir: String) -> Result<(), String> {
    let mut s = manager::load_settings();
    s.game_dir = game_dir.trim().to_string();
    if !workspace_dir.trim().is_empty() {
        s.workspace_dir = workspace_dir.trim().to_string();
    }
    manager::save_settings(&s)
}

#[tauri::command]
fn manager_open_folder(target: String) -> Result<(), String> {
    manager::open_folder(&target)
}

fn main() {
    let client = Client::builder()
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .expect("reqwest client");

    let app = tauri::Builder::default()
        .manage(client)
        .manage(manager::ManagerState::new())
        .invoke_handler(tauri::generate_handler![
            gm_request,
            manager_status,
            manager_start,
            manager_stop,
            manager_launch_game,
            manager_log_tail,
            manager_save_settings,
            manager_open_folder,
        ])
        .build(tauri::generate_context!())
        .expect("error building Trimurti GM Tools");

    app.run(|handle, event| {
        if let RunEvent::Exit = event {
            let state = handle.state::<manager::ManagerState>();
            let _ = manager::stop_server(&state);
        }
    });
}
