use serde::{Deserialize, Serialize};
use std::fs;
use std::net::{SocketAddr, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::Duration;

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

const HOSTS_PATH: &str = r"C:\Windows\System32\drivers\etc\hosts";
const START_MARKER: &str = "# >>> TRIMURTI-PS-START";
const END_MARKER: &str = "# <<< TRIMURTI-PS-END";

const DOMAINS: &[&str] = &[
    "open.ys4fun.com",
    "prod-api-activity.ys4fun.com",
    "ta.ys4fun.com",
    "account.ys4fun.com",
    "m.bbs.ys4fun.com",
    "bbs.ys4fun.com",
    "skzy.ys4fun.com",
    "webstatic.ys4fun.com",
    "ys4fun-prod-pub.ys4fun.com",
    "version.ys4fun.com",
    "version-skzy.ys4fun.com",
    "prod-version.ys4fun.com",
    "sdk.ys4fun.com",
    "sdk-api.ys4fun.com",
    "api-sdk.ys4fun.com",
    "pass.ys4fun.com",
    "notice.ys4fun.com",
    "notice-skzy.ys4fun.com",
    "cdn.ys4fun.com",
    "cdn-skzy.ys4fun.com",
    "skzy-cdn.ys4fun.com",
    "gateway.ys4fun.com",
    "gateway-skzy.ys4fun.com",
    "login.ys4fun.com",
    "login-skzy.ys4fun.com",
    "api.ys4fun.com",
];

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub game_dir: String,
    pub workspace_dir: String,
    #[serde(default = "default_true")]
    pub auto_update: bool,
}

fn default_true() -> bool {
    true
}

impl Default for Settings {
    fn default() -> Self {
        let default_game = r"C:\Program Files\AetherGazerLauncher\AetherGazer";
        let appdata_server = default_appdata_server_dir();
        let _ = fs::create_dir_all(&appdata_server);
        Self {
            game_dir: if Path::new(default_game).exists() {
                default_game.to_string()
            } else {
                String::new()
            },
            workspace_dir: appdata_server.to_string_lossy().to_string(),
            auto_update: true,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub app_version: String,
    pub settings: Settings,
    pub server_running: bool,
    pub hosts_active: bool,
    pub cert_ready: bool,
    pub game_found: bool,
    pub busy_ports: Vec<u16>,
    pub server_mode: String,
    pub log_path: String,
    pub server_dir: String,
    pub installed: bool,
    pub built: bool,
    pub source_commit: String,
    pub build_commit: String,
    pub dotnet_ready: bool,
    /// Paso en curso de la preparación (descarga, .NET, compilación); vacío si no hay ninguno.
    pub stage: String,
}

pub struct ManagerState {
    pub child: Mutex<Option<Child>>,
    pub stage: Mutex<String>,
    /// Evita dos preparaciones/actualizaciones a la vez.
    pub busy: Mutex<bool>,
}

impl ManagerState {
    pub fn new() -> Self {
        Self {
            child: Mutex::new(None),
            stage: Mutex::new(String::new()),
            busy: Mutex::new(false),
        }
    }

    pub fn set_stage(&self, msg: &str) {
        if let Ok(mut g) = self.stage.lock() {
            *g = msg.to_string();
        }
    }
}

fn default_appdata_server_dir() -> PathBuf {
    let base = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("TrimurtiGMTools").join("server")
}

fn config_path() -> PathBuf {
    let base = std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("TrimurtiGMTools").join("settings.json")
}

pub fn load_settings() -> Settings {
    let path = config_path();
    let mut settings = fs::read_to_string(&path)
        .ok()
        .and_then(|raw| serde_json::from_str::<Settings>(&raw).ok())
        .unwrap_or_default();
    if settings.workspace_dir.trim().is_empty() {
        let appdata_server = default_appdata_server_dir();
        let _ = fs::create_dir_all(&appdata_server);
        settings.workspace_dir = appdata_server.to_string_lossy().to_string();
    }
    if settings.game_dir.trim().is_empty() {
        let default_game = r"C:\Program Files\AetherGazerLauncher\AetherGazer";
        if Path::new(default_game).exists() {
            settings.game_dir = default_game.to_string();
        }
    }
    settings
}

pub fn save_settings(settings: &Settings) -> Result<(), String> {
    let path = config_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("No se pudo crear carpeta de ajustes: {e}"))?;
    }
    let json = serde_json::to_string_pretty(settings).map_err(|e| e.to_string())?;
    fs::write(&path, json).map_err(|e| format!("No se pudo guardar settings.json: {e}"))
}

fn port_open(port: u16) -> bool {
    let addr: SocketAddr = ([127, 0, 0, 1], port).into();
    TcpStream::connect_timeout(&addr, Duration::from_millis(120)).is_ok()
}

fn check_hosts_active() -> bool {
    fs::read_to_string(HOSTS_PATH)
        .map(|content| content.lines().any(|l| l.trim() == START_MARKER))
        .unwrap_or(false)
}

fn hosts_without_trimurti() -> Result<Vec<String>, String> {
    if !Path::new(HOSTS_PATH).exists() {
        return Ok(Vec::new());
    }
    let raw = fs::read_to_string(HOSTS_PATH)
        .map_err(|e| format!("No se pudo leer {HOSTS_PATH}: {e}"))?;
    let mut out = Vec::new();
    let mut skip = false;
    for line in raw.lines() {
        let trimmed = line.trim();
        if trimmed == START_MARKER {
            skip = true;
            continue;
        }
        if trimmed == END_MARKER {
            skip = false;
            continue;
        }
        if !skip {
            out.push(line.to_string());
        }
    }
    Ok(out)
}

fn flush_dns() {
    let mut cmd = Command::new("ipconfig");
    cmd.arg("/flushdns").stdout(Stdio::null()).stderr(Stdio::null());
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);
    let _ = cmd.status();
}

pub fn add_trimurti_hosts() -> Result<(), String> {
    let mut lines = hosts_without_trimurti()?;
    lines.push(String::new());
    lines.push(START_MARKER.to_string());
    for d in DOMAINS {
        lines.push(format!("127.0.0.1 {d}"));
    }
    lines.push(END_MARKER.to_string());
    let joined = lines.join("\r\n") + "\r\n";
    fs::write(HOSTS_PATH, joined).map_err(|e| {
        format!("No se pudo escribir {HOSTS_PATH} (ejecuta la app como Administrador): {e}")
    })?;
    flush_dns();
    Ok(())
}

pub fn remove_trimurti_hosts() -> Result<(), String> {
    if !Path::new(HOSTS_PATH).exists() {
        return Ok(());
    }
    let lines = hosts_without_trimurti()?;
    let joined = lines.join("\r\n") + "\r\n";
    fs::write(HOSTS_PATH, joined).map_err(|e| format!("No se pudo restaurar {HOSTS_PATH}: {e}"))?;
    flush_dns();
    Ok(())
}

fn server_data_dir(ws: &Path) -> PathBuf {
    ws.join("data")
}

#[allow(dead_code)]
fn legacy_server_data_dir(ws: &Path) -> PathBuf {
    let src_data = ws.join("src").join("Trimūrti.Server").join("Data");
    if ws.join("src").join("Trimūrti.Server").exists() {
        return src_data;
    }
    ws.join("Data")
}

fn ensure_local_cert(ws: &Path) -> Result<PathBuf, String> {
    let data_dir = server_data_dir(ws);
    fs::create_dir_all(&data_dir).map_err(|e| format!("No se pudo crear carpeta Data: {e}"))?;
    let pfx_path = data_dir.join("trimurti-local.pfx");

    let pfx_str = pfx_path.to_string_lossy().replace('\'', "''");
    let script = format!(
        r#"$ErrorActionPreference='Stop';
$pfx='{pfx_str}';
$pwd=ConvertTo-SecureString -String 'trimurti' -Force -AsPlainText;
$existing=Get-ChildItem Cert:\LocalMachine\Root | Where-Object {{ $_.Subject -eq 'CN=TrimurtiLocal' }} | Select-Object -First 1;
if (-not (Test-Path $pfx) -or -not $existing) {{
  $domains=@('localhost','127.0.0.1','*.ys4fun.com','*.yongshigame.com','*.yostar.net',{domains});
  $cert=New-SelfSignedCertificate -Subject 'CN=TrimurtiLocal' -DnsName ($domains | Select-Object -Unique) -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(3);
  Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd | Out-Null;
  $store=New-Object System.Security.Cryptography.X509Certificates.X509Store('Root','LocalMachine');
  $store.Open('ReadWrite');
  $store.Add($cert);
  $store.Close();
}}"#,
        domains = DOMAINS
            .iter()
            .map(|d| format!("'{d}'"))
            .collect::<Vec<_>>()
            .join(",")
    );

    let mut cmd = Command::new("powershell");
    cmd.args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", &script])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);

    let out = cmd.output().map_err(|e| format!("Error ejecutando PowerShell para TLS: {e}"))?;
    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr);
        return Err(format!("No se pudo preparar el certificado TLS local: {err}"));
    }
    Ok(pfx_path)
}

pub fn server_root(settings: &Settings) -> PathBuf {
    PathBuf::from(&settings.workspace_dir)
}

pub fn is_running(state: &ManagerState) -> bool {
    if port_open(5180) {
        return true;
    }
    state
        .child
        .lock()
        .ok()
        .and_then(|mut g| g.as_mut().map(|c| matches!(c.try_wait(), Ok(None))))
        .unwrap_or(false)
}

pub fn get_status(state: &ManagerState) -> Status {
    let settings = load_settings();
    let ws = server_root(&settings);
    let running = is_running(state);

    let mut busy_ports = Vec::new();
    if !running {
        for p in [80u16, 443, 5180, 8102, 8105] {
            if port_open(p) {
                busy_ports.push(p);
            }
        }
    }

    let installed = crate::updater::is_installed(&ws);
    let built = crate::updater::is_built(&ws);
    let build_commit = crate::updater::build_commit(&ws);
    let server_mode = if built {
        format!("Compilado ({})", build_commit.chars().take(7).collect::<String>())
    } else if installed {
        "Pendiente de compilar".to_string()
    } else {
        "No instalado".to_string()
    };
    let game_found = !settings.game_dir.trim().is_empty()
        && Path::new(&settings.game_dir).join("AetherGazer.exe").exists();

    Status {
        app_version: env!("CARGO_PKG_VERSION").to_string(),
        cert_ready: server_data_dir(&ws).join("trimurti-local.pfx").exists(),
        log_path: ws.join("logs").join("trimurti-live.log").to_string_lossy().to_string(),
        server_dir: ws.to_string_lossy().to_string(),
        source_commit: crate::updater::source_commit(&ws),
        dotnet_ready: crate::updater::dotnet_exe().is_file(),
        stage: state.stage.lock().map(|g| g.clone()).unwrap_or_default(),
        settings,
        server_running: running,
        hosts_active: check_hosts_active(),
        game_found,
        busy_ports,
        server_mode,
        installed,
        built,
        build_commit,
    }
}

/// Descarga/compila lo que haga falta (ver updater::prepare). `force_check` consulta GitHub aunque haya versión.
pub fn prepare(state: &ManagerState, force_check: bool) -> Result<String, String> {
    {
        let mut busy = state.busy.lock().map_err(|_| "Estado bloqueado")?;
        if *busy {
            return Err("Ya hay una preparación en curso.".to_string());
        }
        *busy = true;
    }
    let ws = server_root(&load_settings());
    let result = crate::updater::prepare(&ws, force_check, &|msg| state.set_stage(msg));
    state.set_stage("");
    if let Ok(mut busy) = state.busy.lock() {
        *busy = false;
    }
    result
}

pub fn start_server(state: &ManagerState) -> Result<String, String> {
    let settings = load_settings();
    let ws = server_root(&settings);
    fs::create_dir_all(&ws).map_err(|e| format!("No se pudo crear la carpeta del servidor: {e}"))?;

    if port_open(5180) {
        add_trimurti_hosts()?;
        return Ok("El servidor ya estaba activo; hosts verificado.".to_string());
    }

    let commit = prepare(state, false)?;
    state.set_stage("Iniciando el servidor…");
    let pfx = ensure_local_cert(&ws)?;
    add_trimurti_hosts()?;

    if !settings.game_dir.trim().is_empty() {
        let pend = Path::new(&settings.game_dir)
            .join("AetherGazer_Data")
            .join("StreamingAssets")
            .join("Windows")
            .join("DownLoadingPend.bytes");
        if pend.exists() {
            let _ = fs::write(&pend, []);
        }
    }

    let app = crate::updater::app_dir(&ws);
    let data = server_data_dir(&ws);
    let logs = ws.join("logs");
    let _ = fs::create_dir_all(&data);
    let _ = fs::create_dir_all(&logs);
    let mut cmd = Command::new(crate::updater::dotnet_exe());
    cmd.arg(app.join(crate::updater::SERVER_DLL))
        .current_dir(&app)
        .env("TRIMURTI_HTTP_PORT", "80")
        .env("TRIMURTI_HTTPS_PORT", "443")
        .env("TRIMURTI_CERT_PFX", pfx.to_string_lossy().to_string())
        .env("TRIMURTI_CERT_PASSWORD", "trimurti")
        .env("TRIMURTI_DB_PATH", data.join("trimurti.db"))
        .env("TRIMURTI_LOG_DIR", &logs)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    crate::updater::dotnet_env(&mut cmd);
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);

    let child = cmd.spawn().map_err(|e| format!("No se pudo iniciar el servidor: {e}"));
    state.set_stage("");
    let child = child?;
    if let Ok(mut guard) = state.child.lock() {
        *guard = Some(child);
    }
    Ok(format!(
        "Servidor iniciado (versión {}) con hosts y TLS activos.",
        commit.chars().take(7).collect::<String>()
    ))
}

pub fn stop_server(state: &ManagerState) -> Result<String, String> {
    if let Ok(mut guard) = state.child.lock() {
        if let Some(mut child) = guard.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }

    let mut kill = Command::new("taskkill");
    kill.args(["/IM", "Trimūrti.Server.exe", "/F", "/T"])
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(windows)]
    kill.creation_flags(CREATE_NO_WINDOW);
    let _ = kill.status();

    remove_trimurti_hosts()?;
    Ok("Servidor detenido y archivo hosts restaurado.".to_string())
}

pub fn launch_game() -> Result<String, String> {
    let settings = load_settings();
    if settings.game_dir.trim().is_empty() {
        return Err("Configura primero la carpeta de Aether Gazer.".to_string());
    }
    let exe = Path::new(&settings.game_dir).join("AetherGazer.exe");
    if !exe.exists() {
        return Err(format!("No se encontró {}", exe.display()));
    }
    Command::new(&exe)
        .current_dir(&settings.game_dir)
        .spawn()
        .map_err(|e| format!("No se pudo iniciar AetherGazer.exe: {e}"))?;
    Ok("Aether Gazer iniciado.".to_string())
}

pub fn read_log_tail(lines: usize) -> String {
    let settings = load_settings();
    let log_file = server_root(&settings).join("logs").join("trimurti-live.log");
    match fs::read_to_string(&log_file) {
        Ok(content) => {
            let all: Vec<&str> = content.lines().collect();
            let start = all.len().saturating_sub(lines);
            all[start..].join("\n")
        }
        Err(_) => "Sin registro todavía (logs/trimurti-live.log).".to_string(),
    }
}

pub fn open_folder(target: &str) -> Result<(), String> {
    let settings = load_settings();
    let ws = server_root(&settings);
    let path = match target {
        "logs" => ws.join("logs"),
        "game" => PathBuf::from(&settings.game_dir),
        _ => ws,
    };
    if !path.exists() {
        return Err(format!("La carpeta {} no existe.", path.display()));
    }
    Command::new("explorer")
        .arg(path)
        .spawn()
        .map_err(|e| format!("No se pudo abrir Explorer: {e}"))?;
    Ok(())
}
