//! Preparación del servidor sin depender de nada instalado en el equipo (ni Git ni .NET):
//!
//! 1. Código: compara el último commit de `main` en GitHub con `.trimurti-commit`; si cambió (o no hay
//!    servidor), descarga el ZIP de la rama y lo copia encima sin tocar datos, registros ni certificados.
//! 2. Entorno: un SDK de .NET privado en `%LOCALAPPDATA%\TrimurtiGMTools\dotnet`, descargado de Microsoft
//!    y verificado con el SHA-512 que publica Microsoft en su metadata de versiones.
//! 3. Compilación: `dotnet publish` a `<servidor>\app` solo cuando `.trimurti-build` no coincide con el commit.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

pub const REPO: &str = "jhon1466/Trimurti";
pub const BRANCH: &str = "main";
const SOURCE_MARKER: &str = ".trimurti-commit";
const BUILD_MARKER: &str = ".trimurti-build";
const DOTNET_CHANNEL: &str = "10.0";
pub const SERVER_DLL: &str = "Trimūrti.Server.dll";

fn hidden(cmd: &mut Command) -> &mut Command {
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd
}

fn powershell(script: &str, envs: &[(&str, &Path)]) -> Result<String, String> {
    let mut cmd = Command::new("powershell.exe");
    cmd.args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for (k, v) in envs {
        cmd.env(k, v);
    }
    let out = hidden(&mut cmd).output().map_err(|e| format!("PowerShell: {e}"))?;
    if !out.status.success() {
        return Err(String::from_utf8_lossy(&out.stderr).trim().to_string());
    }
    Ok(String::from_utf8_lossy(&out.stdout).trim().to_string())
}

const PS_PRELUDE: &str = r#"$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
function Download-File($url, $dest) {
  if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    & curl.exe -f -s -S -L -o $dest $url
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $dest -PathType Leaf)) { return }
  }
  Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $dest -UserAgent 'TrimurtiGMTools'
}
function Remove-Temp($dir) {
  $resolved=(Resolve-Path -LiteralPath $dir).Path; $root=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
  if ($resolved.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
"#;

fn local_app_dir() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(|p| PathBuf::from(p).join("TrimurtiGMTools"))
        .unwrap_or_else(|| PathBuf::from("TrimurtiGMTools"))
}

pub fn dotnet_exe() -> PathBuf {
    local_app_dir().join("dotnet").join("dotnet.exe")
}

pub fn app_dir(root: &Path) -> PathBuf {
    root.join("app")
}

pub fn is_installed(root: &Path) -> bool {
    root.join("src").join("Trimūrti.Server").join("Trimūrti.Server.csproj").is_file()
}

pub fn is_built(root: &Path) -> bool {
    app_dir(root).join(SERVER_DLL).is_file()
}

fn read_marker(root: &Path, name: &str) -> String {
    fs::read_to_string(root.join(name)).map(|s| s.trim().to_string()).unwrap_or_default()
}

pub fn source_commit(root: &Path) -> String {
    read_marker(root, SOURCE_MARKER)
}

pub fn build_commit(root: &Path) -> String {
    read_marker(root, BUILD_MARKER)
}

/// Último commit de `main` en GitHub: (sha, primera línea del mensaje), o None sin conexión.
pub fn latest_remote_commit() -> Option<(String, String)> {
    let script = format!(
        "{PS_PRELUDE}$c=Invoke-RestMethod -UseBasicParsing -TimeoutSec 15 -Headers @{{ 'User-Agent'='TrimurtiGMTools'; 'Accept'='application/vnd.github+json' }} -Uri 'https://api.github.com/repos/{REPO}/commits/{BRANCH}'\n$c.sha\n($c.commit.message -split \"`n\")[0]"
    );
    let out = powershell(&script, &[]).ok()?;
    let mut lines = out.lines();
    let sha = lines.next()?.trim().to_string();
    if sha.len() != 40 || !sha.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    Some((sha, lines.next().unwrap_or_default().trim().to_string()))
}

/// Descarga el código de la rama y lo copia encima de `root`, conservando datos locales.
fn download_source(root: &Path) -> Result<(), String> {
    let script = format!(
        r#"{PS_PRELUDE}$tempDir = Join-Path $env:TEMP ('trimurti-src-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$archive = Join-Path $tempDir 'source.zip'
Download-File 'https://github.com/{REPO}/archive/refs/heads/{BRANCH}.zip' $archive
Expand-Archive -LiteralPath $archive -DestinationPath $tempDir
$source = Get-ChildItem -LiteralPath $tempDir -Directory | Select-Object -First 1
if (-not $source -or -not (Test-Path -LiteralPath (Join-Path $source.FullName 'src'))) {{ throw 'El archivo descargado no contiene el servidor.' }}
New-Item -ItemType Directory -Force -Path $env:TRIMURTI_ROOT | Out-Null
Copy-Item -Path (Join-Path $source.FullName '*') -Destination $env:TRIMURTI_ROOT -Recurse -Force
Remove-Temp $tempDir
"#
    );
    powershell(&script, &[("TRIMURTI_ROOT", root)]).map_err(|e| format!("No se pudo descargar el servidor: {e}"))?;
    if !is_installed(root) {
        return Err("La descarga no contiene el servidor esperado.".into());
    }
    Ok(())
}

/// SDK de .NET privado, verificado con el SHA-512 publicado por Microsoft.
fn ensure_dotnet() -> Result<PathBuf, String> {
    let exe = dotnet_exe();
    if exe.is_file() {
        return Ok(exe);
    }
    let home = exe.parent().ok_or("Ruta del entorno .NET no válida.")?.to_path_buf();
    let script = format!(
        r#"{PS_PRELUDE}$meta = Invoke-RestMethod -UseBasicParsing -TimeoutSec 30 -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/{DOTNET_CHANNEL}/releases.json'
$sdkVersion = $meta.'latest-sdk'
$file = $null
foreach ($r in $meta.releases) {{
  foreach ($s in @($r.sdk) + @($r.sdks)) {{
    if ($s -and $s.version -eq $sdkVersion) {{ $file = $s.files | Where-Object {{ $_.name -eq 'dotnet-sdk-win-x64.zip' }} | Select-Object -First 1 }}
    if ($file) {{ break }}
  }}
  if ($file) {{ break }}
}}
if (-not $file) {{ throw "No se encontró el SDK $sdkVersion para Windows x64." }}
$tempDir = Join-Path $env:TEMP ('trimurti-dotnet-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$archive = Join-Path $tempDir 'dotnet.zip'
Download-File $file.url $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash.ToUpperInvariant()) {{ throw 'La descarga de .NET no coincide con el hash publicado por Microsoft.' }}
New-Item -ItemType Directory -Force -Path $env:TRIMURTI_DOTNET | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $env:TRIMURTI_DOTNET -Force
Remove-Temp $tempDir
"#
    );
    powershell(&script, &[("TRIMURTI_DOTNET", &home)]).map_err(|e| format!("No se pudo preparar .NET: {e}"))?;
    if !exe.is_file() {
        return Err("El entorno .NET descargado está incompleto.".into());
    }
    Ok(exe)
}

/// Variables para usar el SDK privado sin tocar el sistema.
pub fn dotnet_env(cmd: &mut Command) {
    let home = local_app_dir().join("dotnet");
    cmd.env("DOTNET_ROOT", &home)
        .env("DOTNET_MULTILEVEL_LOOKUP", "0")
        .env("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        .env("DOTNET_NOLOGO", "1")
        .env("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
        .env("NUGET_PACKAGES", local_app_dir().join("nuget"));
}

fn build(root: &Path, dotnet: &Path, commit: &str) -> Result<(), String> {
    let csproj = root.join("src").join("Trimūrti.Server").join("Trimūrti.Server.csproj");
    let mut cmd = Command::new(dotnet);
    cmd.arg("publish")
        .arg(&csproj)
        .args(["-c", "Release", "-o"])
        .arg(app_dir(root))
        .current_dir(root)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    dotnet_env(&mut cmd);
    let out = hidden(&mut cmd).output().map_err(|e| format!("No se pudo compilar el servidor: {e}"))?;
    let log = format!("{}\n{}", String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
    let _ = fs::create_dir_all(root.join("logs"));
    let _ = fs::write(root.join("logs").join("build.log"), &log);
    if !out.status.success() {
        let errors: Vec<&str> = log.lines().filter(|l| l.contains(" error ")).take(5).collect();
        return Err(format!("La compilación falló (logs/build.log): {}", errors.join(" | ")));
    }
    fs::write(root.join(BUILD_MARKER), commit).map_err(|e| e.to_string())
}

/// Deja el servidor listo para ejecutarse: código al día, .NET privado y compilado.
/// `stage` recibe mensajes de progreso. Devuelve el commit compilado.
pub fn prepare(root: &Path, force_check: bool, stage: &dyn Fn(&str)) -> Result<String, String> {
    let installed = is_installed(root);
    let local = source_commit(root);
    if !installed || force_check || local.is_empty() {
        stage("Comprobando la última versión…");
        match latest_remote_commit() {
            Some((remote, _)) if !installed || remote != local => {
                stage(if installed { "Descargando la actualización del servidor…" } else { "Descargando el servidor…" });
                download_source(root)?;
                fs::write(root.join(SOURCE_MARKER), &remote).map_err(|e| e.to_string())?;
            }
            Some(_) => {}
            None if !installed => return Err("Sin conexión con GitHub: no se pudo descargar el servidor.".into()),
            None => {}
        }
    }
    let commit = source_commit(root);
    if !is_built(root) || build_commit(root) != commit {
        stage("Preparando .NET (solo la primera vez)…");
        let dotnet = ensure_dotnet()?;
        stage("Compilando el servidor…");
        build(root, &dotnet, &commit)?;
    }
    stage("");
    Ok(commit)
}

#[cfg(test)]
mod tests {
    /// Prueba real (red + descarga de .NET + compilación): `cargo test -- --ignored prepare_from_scratch`.
    #[test]
    #[ignore]
    fn prepare_from_scratch() {
        let root = std::env::temp_dir().join("trimurti-updater-test");
        let _ = std::fs::remove_dir_all(&root);
        let commit = super::prepare(&root, false, &|m| if !m.is_empty() { println!("[stage] {m}") }).expect("prepare");
        assert_eq!(commit.len(), 40);
        assert!(super::is_built(&root));
        // Segunda vez: nada que descargar ni compilar.
        let again = super::prepare(&root, true, &|m| if !m.is_empty() { println!("[stage-2] {m}") }).expect("prepare 2");
        assert_eq!(again, commit);
    }
}
