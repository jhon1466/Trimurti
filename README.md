<div align="center">

<img src="desktop/public/icon.png" alt="Trimūrti" width="150" />

# Trimūrti

**Servidor privado local de Aether Gazer · Aether Gazer local private server · Aether Gazer 本地私服 · Aether Gazer ローカルプライベートサーバー**

[![Release](https://img.shields.io/github/v/release/jhon1466/Trimurti?style=flat&color=0d9488&label=release)](https://github.com/jhon1466/Trimurti/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jhon1466/Trimurti/total?style=flat&color=ca8a04)](https://github.com/jhon1466/Trimurti/releases)
[![Last commit](https://img.shields.io/github/last-commit/jhon1466/Trimurti?style=flat&color=6366f1)](https://github.com/jhon1466/Trimurti/commits)

![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C#-server-239120?style=flat)
![SQLite](https://img.shields.io/badge/SQLite-database-003B57?style=flat&logo=sqlite&logoColor=white)
![Tauri](https://img.shields.io/badge/Tauri-2-24C8DB?style=flat&logo=tauri&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D4?style=flat)

<a href="https://github.com/jhon1466/Trimurti/releases/latest"><img src="https://img.shields.io/badge/Trimurti%20GM%20Tools-0d9488?style=flat&logo=github&logoColor=white" alt="Trimurti GM Tools" /></a>

**[Español](#español) · [English](#english) · [中文](#中文) · [日本語](#日本語)**

</div>

---

## Español

### ¿Qué es Trimūrti?

Trimūrti es un servidor privado local para **Aether Gazer** (cliente de PC, versiones de recursos 5.2.1–5.3.1), escrito en **.NET 10**. Todo el estado del jugador se guarda en una base de datos SQLite local y las reglas del juego se calculan con las tablas de configuración del propio cliente.

> **Importante:** el servidor se usa a través de **Trimūrti GM Tools** (carpeta [`desktop/`](desktop)). Esta aplicación de escritorio prepara el certificado TLS local, redirige temporalmente los dominios del juego a tu equipo, arranca y detiene el servidor, abre el juego, mantiene el servidor actualizado con este repositorio e incluye el panel GM. No hace falta ejecutar el servidor a mano.

### Trimūrti GM Tools

| Función | Descripción |
|---|---|
| Iniciar todo | Crea e instala el certificado TLS local, añade la redirección de hosts y arranca el servidor. |
| Detener | Detiene el servidor y restaura el archivo `hosts` tal como estaba. |
| Abrir Aether Gazer | Lanza el juego instalado desde la carpeta configurada. |
| Actualizaciones | Comprueba si hay commits nuevos en este repositorio; si los hay, descarga el código, lo compila con su .NET privado, conserva tu base de datos y reinicia el servidor. Muestra si ya usas la última versión y puede hacerlo automáticamente. |
| Panel GM | Cuentas, modificadores (personajes), objetos y correo del jugador. |
| Registro en vivo | Muestra `logs/trimurti-live.log` y copia un diagnóstico al portapapeles. |
| Idiomas | Español, English, 中文 y 日本語. |

### Requisitos

- Windows 10/11 de 64 bits.
- Conexión a internet la primera vez. No hace falta instalar .NET ni Git: GM Tools descarga el servidor y un .NET privado (verificado con el hash oficial de Microsoft) en `%LOCALAPPDATA%\TrimurtiGMTools`.
- Aether Gazer para PC instalado (por defecto en `C:\Program Files\AetherGazerLauncher\AetherGazer`).
- Ejecutar GM Tools **como administrador** (necesario para el archivo `hosts` y el certificado).
- Puertos libres: 80, 443, 5180 (HTTP/HTTPS/API GM), 8102, 8105 (TCP del juego) y 6105 (UDP de batalla).

### Instalación y uso

1. Descarga o compila Trimūrti GM Tools (ver *Compilar GM Tools*).
2. Ábrelo como administrador.
3. En la pestaña **Servidor**, indica la carpeta del juego. La carpeta del servidor por defecto es `%APPDATA%\TrimurtiGMTools\server`; la primera vez GM Tools descarga y compila el servidor ahí (tarda unos minutos).
4. Pulsa **Iniciar todo** y espera a que aparezca *SERVIDOR ACTIVO*.
5. Pulsa **Abrir Aether Gazer** e inicia sesión con una cuenta local.
6. Al terminar, pulsa **Detener** para restaurar el archivo `hosts`.

### Compilar GM Tools

Requiere Node.js 20+ y Rust estable.

```powershell
cd desktop
npm install
npm run tauri:build
```

El ejecutable queda en `desktop/src-tauri/target/release/trimurti-gm-tools.exe`.

### Datos y copias de seguridad

- Base de datos: `data\trimurti.db` dentro de la carpeta del servidor.
- Registros: `logs\trimurti-live.log` (servidor) y `logs\build.log` (última compilación).
- Ajustes del operador (sistemas abiertos, anuncios, contenido oculto…): `src/Trimūrti.Server/Data/Configs/server_settings.json`.
- Las actualizaciones nunca sobrescriben la base de datos ni los registros. Aun así, copia `trimurti.db*` antes de cambios importantes.

### Desarrollo

```powershell
dotnet build "src/Trimūrti.Server"
```

### Roadmap

![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) funciona · ![Parcial](https://img.shields.io/badge/Parcial-eab308?style=flat-square) parcial · ![No disponible](https://img.shields.io/badge/No%20disponible-ef4444?style=flat-square) no disponible

| Área | Estado | Detalle |
|---|---|---|
| Cuentas y login SDK | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Cuentas locales, login por token, lista de zonas, avisos. |
| Pantalla de inicio de sesión | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Todo el estado inicial se genera con datos del jugador y del servidor. |
| Perfil y personalización | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Nombre, firma, avatar, marco, tarjeta, burbuja, etiquetas, cumpleaños, música. |
| Personajes | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Nivel, ruptura, estrellas, habilidades, astrolabio, skins, confianza, juramento, chips. |
| Armas, sirvientes y sigils | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Mejora, ruptura, mejora rápida, encantamientos. |
| Inventario y objetos | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Uso de objetos, cofres, conversión de piezas. |
| Tienda | ![Parcial](https://img.shields.io/badge/Parcial-eab308?style=flat-square) | Tiendas de las tablas del cliente con límites y refrescos. La tienda de skins aparece pero sin artículos. |
| Gacha | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Bandas, probabilidad garantizada, selección de UP, historial. |
| Correo, tareas y logros | ![Parcial](https://img.shields.io/badge/Parcial-eab308?style=flat-square) | Correo, tareas diarias/semanales y la mayoría de logros; faltan algunos logros de estadísticas de combate. |
| Firmas diarias | ![Parcial](https://img.shields.io/badge/Parcial-eab308?style=flat-square) | Firma de 7 días y acumulativas; la firma mensual necesita su tabla de premios. |
| Batallas | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Servidor de batalla UDP local, resultado real, estrellas, botín, barrido. |
| Capítulos e historia | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Mapa de capítulos, tesoros, historias. |
| Dormitorio, comedor e ídolos | ![Parcial](https://img.shields.io/badge/Parcial-eab308?style=flat-square) | Habitaciones, muebles, comedor, encargos, entrenamiento de ídolos, PVE y tablón semanal. Sin PVP de ídolos. |
| Contenido semanal | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | 梦境再构, 黑区净化, abismo, 迭代校验, 介质攫取, 因果观测, matriz (矩阵), poliedro, RogueTeam, 心象梦魇. |
| Encantamiento y equipo (combates) | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Ofertas, refrescos, suit UP, contadores de garantía. |
| Minijuegos y modos de evento | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Música, vehicle ball, Kadas, 诡谈夜话, autoajedrez (PVE) y modos de evento de los capítulos. |
| Actividades temporales | ![Parcial](https://img.shields.io/badge/Parcial-eab308?style=flat-square) | Solo se envían si su actividad está abierta; sorteos, firmas de skin y PT scroll aún sin comandos de juego. |
| Matriz (矩阵) | ![Parcial](https://img.shields.io/badge/Parcial-eab308?style=flat-square) | Partida completa; los premios por rango de puntos no dan objetos. |
| Amigos, chat, gremio y cooperativo | ![No disponible](https://img.shields.io/badge/No%20disponible-ef4444?style=flat-square) | No hay sistema multijugador. |
| PVP y clasificaciones | ![No disponible](https://img.shields.io/badge/No%20disponible-ef4444?style=flat-square) | No disponible. |
| Pagos reales | ![No disponible](https://img.shields.io/badge/No%20disponible-ef4444?style=flat-square) | Las compras con dinero real no están disponibles. |
| Otros sistemas del cliente | ![No disponible](https://img.shields.io/badge/No%20disponible-ef4444?style=flat-square) | Unos 470 comandos sin implementar (pesca, actividades de gremio, juego de navegación, etc.). |
| GM Tools | ![Funciona](https://img.shields.io/badge/Funciona-22c55e?style=flat-square) | Arranque, hosts, TLS, panel GM y actualización automática. |

### Aviso

Proyecto no oficial, sin relación con los desarrolladores ni distribuidores de Aether Gazer. Solo para uso personal y local. No se incluyen archivos del cliente del juego.

---

---

## English

### What is Trimūrti?

Trimūrti is a local private server for **Aether Gazer** (PC client, resource versions 5.2.1–5.3.1) written in **.NET 10**. All player state lives in a local SQLite database, and game rules are computed from the client's own configuration tables.

> **Important:** the server is used through **Trimūrti GM Tools** (the [`desktop/`](desktop) folder). This desktop app prepares the local TLS certificate, temporarily redirects the game domains to your machine, starts and stops the server, launches the game, keeps the server up to date with this repository and includes the GM panel. You do not need to run the server by hand.

### Trimūrti GM Tools

| Feature | Description |
|---|---|
| Start all | Creates and installs the local TLS certificate, adds the hosts redirect and starts the server. |
| Stop | Stops the server and restores the `hosts` file as it was. |
| Open Aether Gazer | Launches the installed game from the configured folder. |
| Updates | Checks this repository for new commits; when there are any, downloads the code, compiles it with its private .NET, keeps your database and restarts the server. Shows whether you are on the latest version and can do it automatically. |
| GM panel | Player accounts, modifiers (characters), items and mail. |
| Live log | Shows `logs/trimurti-live.log` and copies a diagnostic to the clipboard. |
| Languages | Español, English, 中文 and 日本語. |

### Requirements

- 64-bit Windows 10/11.
- Internet connection the first time. You do not need to install .NET or Git: GM Tools downloads the server and a private .NET (verified against Microsoft's official hash) into `%LOCALAPPDATA%\TrimurtiGMTools`.
- Aether Gazer for PC installed (default `C:\Program Files\AetherGazerLauncher\AetherGazer`).
- Run GM Tools **as administrator** (required for the `hosts` file and the certificate).
- Free ports: 80, 443, 5180 (HTTP/HTTPS/GM API), 8102, 8105 (game TCP) and 6105 (battle UDP).

### Install and use

1. Download or build Trimūrti GM Tools (see *Building GM Tools*).
2. Open it as administrator.
3. In the **Server** tab, set the game folder. The default server folder is `%APPDATA%\TrimurtiGMTools\server`; the first time, GM Tools downloads and compiles the server there (it takes a few minutes).
4. Press **Start all** and wait for *SERVER ONLINE*.
5. Press **Open Aether Gazer** and sign in with a local account.
6. When done, press **Stop** to restore the `hosts` file.

### Building GM Tools

Requires Node.js 20+ and stable Rust.

```powershell
cd desktop
npm install
npm run tauri:build
```

The executable is written to `desktop/src-tauri/target/release/trimurti-gm-tools.exe`.

### Data and backups

- Database: `data\trimurti.db` inside the server folder.
- Logs: `logs\trimurti-live.log` (server) and `logs\build.log` (last build).
- Operator settings (open systems, announcements, hidden content…): `src/Trimūrti.Server/Data/Configs/server_settings.json`.
- Updates never overwrite the database or the logs. Still, copy `trimurti.db*` before major changes.

### Development

```powershell
dotnet build "src/Trimūrti.Server"
```

### Roadmap

![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) works · ![Partial](https://img.shields.io/badge/Partial-eab308?style=flat-square) partial · ![Unavailable](https://img.shields.io/badge/Unavailable-ef4444?style=flat-square) not available

| Area | Status | Details |
|---|---|---|
| Accounts and SDK login | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Local accounts, token login, zone list, notices. |
| Login screen | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | The whole initial state is built from player and server data. |
| Profile and customization | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Name, signature, avatar, frame, card, chat bubble, tags, birthday, music. |
| Characters | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Level, breakthrough, stars, skills, astrolabe, skins, trust, oath, chips. |
| Weapons, servants and sigils | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Upgrade, breakthrough, quick upgrade, enchantments. |
| Inventory and items | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Item use, chests, piece conversion. |
| Shop | ![Partial](https://img.shields.io/badge/Partial-eab308?style=flat-square) | Shops from the client tables with limits and refreshes. The skin shop appears but has no goods. |
| Gacha | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Banners, pity, UP selection, history. |
| Mail, tasks and achievements | ![Partial](https://img.shields.io/badge/Partial-eab308?style=flat-square) | Mail, daily/weekly tasks and most achievements; some combat-statistics achievements are missing. |
| Daily sign-in | ![Partial](https://img.shields.io/badge/Partial-eab308?style=flat-square) | 7-day and cumulative sign-ins; the monthly sign-in needs its reward table. |
| Battles | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Local UDP battle server, real result, stars, drops, sweep. |
| Chapters and story | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Chapter map, treasures, stories. |
| Dorm, canteen and idols | ![Partial](https://img.shields.io/badge/Partial-eab308?style=flat-square) | Rooms, furniture, canteen, entrusts, idol training, PVE and weekly board. No idol PVP. |
| Weekly content | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | 梦境再构, 黑区净化, abyss, 迭代校验, 介质攫取, 因果观测, matrix (矩阵), polyhedron, RogueTeam, 心象梦魇. |
| Enchantment and equipment battles | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Offers, refreshes, UP suit, guarantee counters. |
| Minigames and event modes | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Music, vehicle ball, Kadas, 诡谈夜话, autochess (PVE) and the chapters' event modes. |
| Limited-time activities | ![Partial](https://img.shields.io/badge/Partial-eab308?style=flat-square) | Sent only while their activity is open; draws, skin sign-ins and PT scroll still lack their gameplay commands. |
| Matrix (矩阵) | ![Partial](https://img.shields.io/badge/Partial-eab308?style=flat-square) | Full run; point-rank rewards give no items. |
| Friends, chat, guild and co-op | ![Unavailable](https://img.shields.io/badge/Unavailable-ef4444?style=flat-square) | No multiplayer system. |
| PVP and rankings | ![Unavailable](https://img.shields.io/badge/Unavailable-ef4444?style=flat-square) | Not available. |
| Real payments | ![Unavailable](https://img.shields.io/badge/Unavailable-ef4444?style=flat-square) | Real-money purchases are not available. |
| Other client systems | ![Unavailable](https://img.shields.io/badge/Unavailable-ef4444?style=flat-square) | About 470 commands not implemented (fishing, guild activities, sailing game, etc.). |
| GM Tools | ![Working](https://img.shields.io/badge/Working-22c55e?style=flat-square) | Startup, hosts, TLS, GM panel and automatic updates. |

### Notice

Unofficial project, not affiliated with the developers or publishers of Aether Gazer. For personal, local use only. No game client files are included.

---

---

## 中文

### Trimūrti 是什么？

Trimūrti 是 **深空之眼（Aether Gazer）** 的本地私人服务器（PC 客户端，资源版本 5.2.1–5.3.1），使用 **.NET 10** 编写。所有玩家数据保存在本地 SQLite 数据库中，游戏规则依据客户端自身的配置表计算。

> **重要：** 服务器通过 **Trimūrti GM Tools**（[`desktop/`](desktop) 目录）使用。该桌面程序会准备本地 TLS 证书、临时将游戏域名重定向到本机、启动和停止服务器、启动游戏、根据本仓库自动更新服务器，并内置 GM 面板。无需手动运行服务器。

### Trimūrti GM Tools

| 功能 | 说明 |
|---|---|
| 全部启动 | 生成并安装本地 TLS 证书，添加 hosts 重定向并启动服务器。 |
| 停止 | 停止服务器，并将 `hosts` 文件恢复原状。 |
| 启动深空之眼 | 从设置的目录启动已安装的游戏。 |
| 更新 | 检查本仓库是否有新提交；如有，则下载代码、使用专用 .NET 编译、保留数据库并重启服务器。会显示是否已是最新版本，可设为自动更新。 |
| GM 面板 | 玩家账号、修正者（角色）、道具与邮件。 |
| 实时日志 | 显示 `logs/trimurti-live.log`，并可复制诊断信息到剪贴板。 |
| 语言 | Español、English、中文、日本語。 |

### 运行要求

- 64 位 Windows 10/11。
- 首次运行需要联网。无需安装 .NET 或 Git：GM Tools 会将服务器和专用 .NET（使用微软官方哈希校验）下载到 `%LOCALAPPDATA%\TrimurtiGMTools`。
- 已安装 PC 版深空之眼（默认 `C:\Program Files\AetherGazerLauncher\AetherGazer`）。
- 以 **管理员身份** 运行 GM Tools（修改 `hosts` 文件和安装证书需要）。
- 需要空闲端口：80、443、5180（HTTP/HTTPS/GM API），8102、8105（游戏 TCP），6105（战斗 UDP）。

### 安装与使用

1. 下载或编译 Trimūrti GM Tools（见“编译 GM Tools”）。
2. 以管理员身份运行。
3. 在 **服务器** 页面设置游戏目录。服务器目录默认为 `%APPDATA%\TrimurtiGMTools\server`；首次运行时 GM Tools 会在此下载并编译服务器（需要几分钟）。
4. 点击 **全部启动**，等待显示“服务器运行中”。
5. 点击 **启动深空之眼**，使用本地账号登录。
6. 结束后点击 **停止**，恢复 `hosts` 文件。

### 编译 GM Tools

需要 Node.js 20+ 和 Rust 稳定版。

```powershell
cd desktop
npm install
npm run tauri:build
```

生成的程序位于 `desktop/src-tauri/target/release/trimurti-gm-tools.exe`。

### 数据与备份

- 数据库：服务器目录下的 `data\trimurti.db`。
- 日志：`logs\trimurti-live.log`（服务器）与 `logs\build.log`（最近一次编译）。
- 运营设置（开放系统、公告、隐藏内容等）：`src/Trimūrti.Server/Data/Configs/server_settings.json`。
- 更新不会覆盖数据库和日志。重大改动前仍建议备份 `trimurti.db*`。

### 开发

```powershell
dotnet build "src/Trimūrti.Server"
```

### 路线图

![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) 可用 · ![部分](https://img.shields.io/badge/部分-eab308?style=flat-square) 部分可用 · ![不可用](https://img.shields.io/badge/不可用-ef4444?style=flat-square) 不可用

| 模块 | 状态 | 说明 |
|---|---|---|
| 账号与 SDK 登录 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 本地账号、令牌登录、服务器列表、公告。 |
| 登录流程 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 全部初始数据均由玩家与服务器数据生成。 |
| 个人资料与外观 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 昵称、签名、头像、头像框、名片、聊天气泡、标签、生日、音乐。 |
| 修正者（角色） | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 等级、突破、星级、技能、星盘、皮肤、信赖、誓约、芯片。 |
| 武器、钥从与刻印 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 强化、突破、快速强化、附魔。 |
| 背包与道具 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 使用道具、宝箱、碎片转换。 |
| 商店 | ![部分](https://img.shields.io/badge/部分-eab308?style=flat-square) | 客户端配置表中的商店，含限购与刷新。换装商店可见但没有商品。 |
| 抽卡 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 卡池、保底、UP 选择、记录。 |
| 邮件、任务与成就 | ![部分](https://img.shields.io/badge/部分-eab308?style=flat-square) | 邮件、每日/每周任务与大部分成就；部分战斗统计类成就尚未实现。 |
| 签到 | ![部分](https://img.shields.io/badge/部分-eab308?style=flat-square) | 七日签到与累计签到；月签到缺少奖励表。 |
| 战斗 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 本地 UDP 战斗服务器、真实结果、星级、掉落、扫荡。 |
| 章节与剧情 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 章节地图、宝箱、剧情。 |
| 宿舍、餐厅与偶像 | ![部分](https://img.shields.io/badge/部分-eab308?style=flat-square) | 房间、家具、餐厅、委托、偶像训练、PVE 与每周公告板。暂无偶像 PVP。 |
| 每周内容 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 梦境再构、黑区净化、失序深阱、迭代校验、介质攫取、因果观测、多维变量（矩阵）、多面体、RogueTeam、心象梦魇。 |
| 附魔战与装备战 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 关卡刷新、UP 套装、保底计数。 |
| 小游戏与活动玩法 | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 音乐、vehicle ball、Kadas、诡谈夜话、自走棋（PVE）及章节活动玩法。 |
| 限时活动 | ![部分](https://img.shields.io/badge/部分-eab308?style=flat-square) | 仅在活动开启时下发；抽取、皮肤签到、PT 卷轴等玩法指令尚未实现。 |
| 多维变量（矩阵） | ![部分](https://img.shields.io/badge/部分-eab308?style=flat-square) | 完整流程；积分段位奖励不发放道具。 |
| 好友、聊天、公会与协作 | ![不可用](https://img.shields.io/badge/不可用-ef4444?style=flat-square) | 无多人系统。 |
| PVP 与排行榜 | ![不可用](https://img.shields.io/badge/不可用-ef4444?style=flat-square) | 不可用。 |
| 真实支付 | ![不可用](https://img.shields.io/badge/不可用-ef4444?style=flat-square) | 不支持真实货币购买。 |
| 其他客户端系统 | ![不可用](https://img.shields.io/badge/不可用-ef4444?style=flat-square) | 约 470 条指令尚未实现（钓鱼、公会活动、航海玩法等）。 |
| GM Tools | ![可用](https://img.shields.io/badge/可用-22c55e?style=flat-square) | 启动、hosts、TLS、GM 面板与自动更新。 |

### 声明

非官方项目，与深空之眼的开发商及发行商无任何关联。仅供个人本地使用。仓库不包含任何游戏客户端文件。

---

---

## 日本語

### Trimūrti とは

Trimūrti は **Aether Gazer（深空之眼）** の PC クライアント（リソースバージョン 5.2.1–5.3.1）向けローカルプライベートサーバーで、**.NET 10** で書かれています。プレイヤーのデータはすべてローカルの SQLite データベースに保存され、ゲームのルールはクライアント自身の設定テーブルに基づいて計算されます。

> **重要：** サーバーは **Trimūrti GM Tools**（[`desktop/`](desktop) フォルダ）を通して使用します。このデスクトップアプリはローカル TLS 証明書の準備、ゲームドメインの一時的なリダイレクト、サーバーの起動と停止、ゲームの起動、このリポジトリに基づくサーバーの自動更新を行い、GM パネルも備えています。サーバーを手動で実行する必要はありません。

### Trimūrti GM Tools

| 機能 | 説明 |
|---|---|
| すべて起動 | ローカル TLS 証明書を作成・インストールし、hosts リダイレクトを追加してサーバーを起動します。 |
| 停止 | サーバーを停止し、`hosts` ファイルを元に戻します。 |
| Aether Gazer を起動 | 設定したフォルダからインストール済みのゲームを起動します。 |
| アップデート | このリポジトリの新しいコミットを確認し、ある場合はコードをダウンロードして専用の .NET でコンパイルし、データベースを保持したままサーバーを再起動します。最新版かどうかを表示し、自動更新も可能です。 |
| GM パネル | プレイヤーアカウント、修正者（キャラクター）、アイテム、メール。 |
| ライブログ | `logs/trimurti-live.log` を表示し、診断情報をクリップボードにコピーします。 |
| 言語 | Español、English、中文、日本語。 |

### 動作環境

- 64 ビット版 Windows 10/11。
- 初回のみインターネット接続が必要です。.NET や Git のインストールは不要です。GM Tools がサーバーと専用の .NET（Microsoft 公式ハッシュで検証）を `%LOCALAPPDATA%\TrimurtiGMTools` にダウンロードします。
- PC 版 Aether Gazer がインストールされていること（既定は `C:\Program Files\AetherGazerLauncher\AetherGazer`）。
- GM Tools を **管理者として** 実行すること（`hosts` ファイルと証明書の操作に必要）。
- 空きポート：80、443、5180（HTTP/HTTPS/GM API）、8102、8105（ゲーム TCP）、6105（バトル UDP）。

### インストールと使い方

1. Trimūrti GM Tools をダウンロードまたはビルドします（「GM Tools のビルド」を参照）。
2. 管理者として起動します。
3. **サーバー** タブでゲームフォルダを設定します。サーバーフォルダの既定は `%APPDATA%\TrimurtiGMTools\server` で、初回は GM Tools がそこにサーバーをダウンロードしてコンパイルします（数分かかります）。
4. **すべて起動** を押し、「稼働中」と表示されるまで待ちます。
5. **Aether Gazer を起動** を押し、ローカルアカウントでログインします。
6. 終了したら **停止** を押して `hosts` ファイルを元に戻します。

### GM Tools のビルド

Node.js 20 以上と安定版 Rust が必要です。

```powershell
cd desktop
npm install
npm run tauri:build
```

実行ファイルは `desktop/src-tauri/target/release/trimurti-gm-tools.exe` に作成されます。

### データとバックアップ

- データベース：サーバーフォルダ内の `data\trimurti.db`。
- ログ：`logs\trimurti-live.log`（サーバー）と `logs\build.log`（直近のコンパイル）。
- 運営設定（開放システム、お知らせ、非表示コンテンツなど）：`src/Trimūrti.Server/Data/Configs/server_settings.json`。
- アップデートはデータベースとログを上書きしません。それでも大きな変更の前には `trimurti.db*` をバックアップしてください。

### 開発

```powershell
dotnet build "src/Trimūrti.Server"
```

### ロードマップ

![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) 動作 · ![一部](https://img.shields.io/badge/一部-eab308?style=flat-square) 一部動作 · ![未対応](https://img.shields.io/badge/未対応-ef4444?style=flat-square) 未対応

| 項目 | 状態 | 詳細 |
|---|---|---|
| アカウントと SDK ログイン | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | ローカルアカウント、トークンログイン、サーバー一覧、お知らせ。 |
| ログイン処理 | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | 初期データはすべてプレイヤーとサーバーのデータから生成。 |
| プロフィールとカスタマイズ | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | 名前、署名、アイコン、フレーム、名刺、吹き出し、タグ、誕生日、BGM。 |
| 修正者（キャラクター） | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | レベル、突破、星、スキル、星盤、スキン、信頼、誓約、チップ。 |
| 武器・従者・刻印 | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | 強化、突破、一括強化、エンチャント。 |
| インベントリとアイテム | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | アイテム使用、宝箱、欠片の変換。 |
| ショップ | ![一部](https://img.shields.io/badge/一部-eab308?style=flat-square) | クライアントのテーブルにあるショップ（購入制限・更新あり）。スキンショップは表示されるが商品なし。 |
| ガチャ | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | ピックアップ、天井、UP 選択、履歴。 |
| メール・タスク・実績 | ![一部](https://img.shields.io/badge/一部-eab308?style=flat-square) | メール、デイリー/ウィークリータスク、大半の実績。一部の戦闘統計実績は未対応。 |
| ログインボーナス | ![一部](https://img.shields.io/badge/一部-eab308?style=flat-square) | 7 日間と累計ログイン。月間ログインは報酬テーブルが必要。 |
| バトル | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | ローカル UDP バトルサーバー、実際の結果、星、ドロップ、スイープ。 |
| チャプターとストーリー | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | チャプターマップ、宝箱、ストーリー。 |
| 寮・食堂・アイドル | ![一部](https://img.shields.io/badge/一部-eab308?style=flat-square) | 部屋、家具、食堂、依頼、アイドル育成、PVE、週間掲示板。アイドル PVP は未対応。 |
| ウィークリーコンテンツ | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | 梦境再构、黑区净化、深淵、迭代校验、介质攫取、因果观测、マトリックス（矩阵）、多面体、RogueTeam、心象梦魇。 |
| エンチャント戦・装備戦 | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | ステージ更新、UP セット、天井カウンター。 |
| ミニゲームとイベントモード | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | 音楽、vehicle ball、Kadas、诡谈夜话、オートチェス（PVE）、各チャプターのイベントモード。 |
| 期間限定イベント | ![一部](https://img.shields.io/badge/一部-eab308?style=flat-square) | イベント開催中のみ送信。抽選、スキンログインボーナス、PT スクロールのゲーム内コマンドは未対応。 |
| マトリックス（矩阵） | ![一部](https://img.shields.io/badge/一部-eab308?style=flat-square) | 一連のプレイが可能。ポイント段位報酬はアイテムなし。 |
| フレンド・チャット・ギルド・協力 | ![未対応](https://img.shields.io/badge/未対応-ef4444?style=flat-square) | マルチプレイ機能なし。 |
| PVP とランキング | ![未対応](https://img.shields.io/badge/未対応-ef4444?style=flat-square) | 未対応。 |
| 実際の課金 | ![未対応](https://img.shields.io/badge/未対応-ef4444?style=flat-square) | 実際のお金での購入は不可。 |
| その他のクライアント機能 | ![未対応](https://img.shields.io/badge/未対応-ef4444?style=flat-square) | 約 470 個のコマンドが未実装（釣り、ギルドイベント、航海ゲームなど）。|
| GM Tools | ![対応](https://img.shields.io/badge/対応-22c55e?style=flat-square) | 起動、hosts、TLS、GM パネル、自動更新。 |

### 注意事項

非公式プロジェクトであり、Aether Gazer の開発元・運営元とは一切関係ありません。個人のローカル利用のみを目的としています。ゲームクライアントのファイルは含まれていません。
