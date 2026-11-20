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
}

pub struct ManagerState {
    pub child: Mutex<Option<Child>>,
}

impl ManagerState {
    pub fn new() -> Self {
        Self {
            child: Mutex::new(None),
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

fn detect_workspace_dir() -> Option<PathBuf> {
    let mut candidates = Vec::new();
    if let Ok(cwd) = std::env::current_dir() {
        candidates.push(cwd.clone());
        if let Some(p) = cwd.parent() {
            candidates.push(p.to_path_buf());
        }
        if let Some(p) = cwd.parent().and_then(|x| x.parent()) {
            candidates.push(p.to_path_buf());
        }
    }
    if let Ok(exe) = std::env::current_exe() {
        let mut cur = exe.parent().map(|p| p.to_path_buf());
        for _ in 0..6 {
            if let Some(dir) = cur {
                candidates.push(dir.clone());
                cur = dir.parent().map(|p| p.to_path_buf());
            }
        }
    }

    for dir in candidates {
        if dir.join("src").join("Trimūrti.Server").join("Trimūrti.Server.csproj").exists()
            || dir.join("Trimūrti.Server.exe").exists()
        {
            return Some(dir);
        }
    }
    None
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

fn find_server_launch(ws: &Path) -> Result<(String, Vec<String>, PathBuf, String), String> {
    let mut search_dirs = vec![ws.to_path_buf()];
    if let Some(repo) = detect_workspace_dir() {
        if repo != ws {
            search_dirs.push(repo);
        }
    }
    for dir in search_dirs {
        let direct_exe = dir.join("Trimūrti.Server.exe");
        if direct_exe.exists() {
            return Ok((
                direct_exe.to_string_lossy().to_string(),
                Vec::new(),
                dir,
                "Ejecutable directo".to_string(),
            ));
        }
        let csproj = dir.join("src").join("Trimūrti.Server").join("Trimūrti.Server.csproj");
        if csproj.exists() {
            return Ok((
                "dotnet".to_string(),
                vec![
                    "run".to_string(),
                    "--project".to_string(),
                    csproj.to_string_lossy().to_string(),
                ],
                dir,
                "dotnet run (.NET 10)".to_string(),
            ));
        }
    }
    Err(format!(
        "No se encontró Trimūrti.Server.csproj ni Trimūrti.Server.exe en {}",
        ws.display()
    ))
}

pub fn get_status(state: &ManagerState) -> Status {
    let settings = load_settings();
    let ws = PathBuf::from(&settings.workspace_dir);
    let mut running = port_open(5180);
    if let Ok(mut guard) = state.child.lock() {
        if let Some(child) = guard.as_mut() {
            match child.try_wait() {
                Ok(None) => running = true,
                _ => *guard = None,
            }
        }
    }

    let mut busy_ports = Vec::new();
    if !running {
        for p in [80u16, 443, 5180, 8102, 8105] {
            if port_open(p) {
                busy_ports.push(p);
            }
        }
    }

    let pfx_exists = server_data_dir(&ws).join("trimurti-local.pfx").exists();
    let game_found = !settings.game_dir.trim().is_empty()
        && Path::new(&settings.game_dir).join("AetherGazer.exe").exists();
    let (_, _, _, server_mode) = find_server_launch(&ws)
        .unwrap_or_else(|_| (String::new(), Vec::new(), ws.clone(), "No encontrado".into()));
    let log_path = ws.join("logs").join("trimurti-live.log");

    Status {
        app_version: env!("CARGO_PKG_VERSION").to_string(),
        settings,
        server_running: running,
        hosts_active: check_hosts_active(),
        cert_ready: pfx_exists,
        game_found,
        busy_ports,
        server_mode,
        log_path: log_path.to_string_lossy().to_string(),
    }
}

pub fn start_server(state: &ManagerState) -> Result<String, String> {
    let settings = load_settings();
    let ws = PathBuf::from(&settings.workspace_dir);
    if !ws.exists() {
        return Err("La carpeta del servidor (workspace) no existe.".to_string());
    }

    if port_open(5180) {
        add_trimurti_hosts()?;
        return Ok("El servidor ya estaba activo; hosts verificado.".to_string());
    }

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

    let (program, args, cwd, mode) = find_server_launch(&ws)?;
    let mut cmd = Command::new(&program);
    cmd.args(&args)
        .current_dir(&cwd)
        .env("TRIMURTI_HTTP_PORT", "80")
        .env("TRIMURTI_HTTPS_PORT", "443")
        .env("TRIMURTI_CERT_PFX", pfx.to_string_lossy().to_string())
        .env("TRIMURTI_CERT_PASSWORD", "trimurti")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);

    let child = cmd
        .spawn()
        .map_err(|e| format!("No se pudo iniciar Trimūrti.Server ({mode}): {e}"))?;

    if let Ok(mut guard) = state.child.lock() {
        *guard = Some(child);
    }

    Ok(format!("Servidor iniciado ({mode}) con hosts y TLS activos."))
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
    let log_file = PathBuf::from(&settings.workspace_dir)
        .join("logs")
        .join("trimurti-live.log");
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
    let ws = PathBuf::from(&settings.workspace_dir);
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
