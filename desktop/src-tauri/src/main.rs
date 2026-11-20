#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use reqwest::{Client, Method};
use serde_json::Value;
use tauri::{Manager, RunEvent, State};

mod manager;
mod updater;

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
async fn manager_start(app: tauri::AppHandle) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || manager::start_server(&app.state::<manager::ManagerState>()))
        .await
        .map_err(|e| e.to_string())?
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
fn manager_set_auto_update(enabled: bool) -> Result<(), String> {
    let mut s = manager::load_settings();
    s.auto_update = enabled;
    manager::save_settings(&s)
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct UpdateInfo {
    installed: bool,
    local_commit: String,
    build_commit: String,
    remote_commit: String,
    remote_message: String,
    update_available: bool,
    online: bool,
}

/// Compara el commit instalado con el último de main en GitHub.
#[tauri::command]
async fn updater_check() -> Result<UpdateInfo, String> {
    tauri::async_runtime::spawn_blocking(|| {
        let root = manager::server_root(&manager::load_settings());
        let local = updater::source_commit(&root);
        let installed = updater::is_installed(&root);
        let built = updater::build_commit(&root);
        match updater::latest_remote_commit() {
            Some((remote, message)) => UpdateInfo {
                update_available: !installed || remote != local || built != remote,
                installed,
                local_commit: local,
                build_commit: built,
                remote_commit: remote,
                remote_message: message,
                online: true,
            },
            None => UpdateInfo {
                update_available: false,
                installed,
                local_commit: local,
                build_commit: built,
                remote_commit: String::new(),
                remote_message: String::new(),
                online: false,
            },
        }
    })
    .await
    .map_err(|e| e.to_string())
}

/// Descarga y compila la última versión; si el servidor estaba activo lo reinicia.
#[tauri::command]
async fn updater_apply(app: tauri::AppHandle) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<manager::ManagerState>();
        let was_running = manager::is_running(&state);
        if was_running {
            manager::stop_server(&state)?;
        }
        let commit = manager::prepare(&state, true)?;
        let short: String = commit.chars().take(7).collect();
        if was_running {
            manager::start_server(&state)?;
            return Ok(format!("Servidor actualizado a {short} y reiniciado."));
        }
        Ok(format!("Servidor actualizado a {short}."))
    })
    .await
    .map_err(|e| e.to_string())?
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
            manager_set_auto_update,
            updater_check,
            updater_apply,
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
