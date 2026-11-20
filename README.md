# Trimūrti

**[Español](#español) · [English](#english) · [中文](#中文) · [日本語](#日本語)**

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
| Actualizaciones | Comprueba si hay commits nuevos en este repositorio, descarga la actualización, conserva tu base de datos y reinicia el servidor. Puede hacerlo automáticamente. |
| Panel GM | Cuentas, modificadores (personajes), objetos y correo del jugador. |
| Registro en vivo | Muestra `logs/trimurti-live.log` y copia un diagnóstico al portapapeles. |
| Idiomas | Español, English, 中文 y 日本語. |

### Requisitos

- Windows 10/11 de 64 bits.
- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- [Git](https://git-scm.com/) (recomendado; sin él, GM Tools descarga las actualizaciones en ZIP).
- Aether Gazer para PC instalado (por defecto en `C:\Program Files\AetherGazerLauncher\AetherGazer`).
- Ejecutar GM Tools **como administrador** (necesario para el archivo `hosts` y el certificado).
- Puertos libres: 80, 443, 5180 (HTTP/HTTPS/API GM), 8102, 8105 (TCP del juego) y 6105 (UDP de batalla).

### Instalación y uso

1. Descarga o compila Trimūrti GM Tools (ver *Compilar GM Tools*).
2. Ábrelo como administrador.
3. En la pestaña **Servidor**, indica la carpeta del juego y la carpeta del servidor. Si la carpeta del servidor está vacía, GM Tools descarga el servidor de este repositorio.
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

- Base de datos: `src/Trimūrti.Server/bin/Debug/net10.0/Data/trimurti.db` dentro de la carpeta del servidor (o la ruta de `TRIMURTI_DB_PATH`).
- Registro: `logs/trimurti-live.log`.
- Ajustes del operador (sistemas abiertos, anuncios, contenido oculto…): `src/Trimūrti.Server/Data/Configs/server_settings.json`.
- Las actualizaciones nunca sobrescriben la base de datos ni los registros. Aun así, copia `trimurti.db*` antes de cambios importantes.

### Desarrollo

```powershell
dotnet build "src/Trimūrti.Server"
```

### Roadmap

✅ funciona · 🟡 parcial · ❌ no disponible

| Área | Estado | Detalle |
|---|---|---|
| Cuentas y login SDK | ✅ | Cuentas locales, login por token, lista de zonas, avisos. |
| Pantalla de inicio de sesión | ✅ | Todo el estado inicial se genera con datos del jugador y del servidor. |
| Perfil y personalización | ✅ | Nombre, firma, avatar, marco, tarjeta, burbuja, etiquetas, cumpleaños, música. |
| Personajes | ✅ | Nivel, ruptura, estrellas, habilidades, astrolabio, skins, confianza, juramento, chips. |
| Armas, sirvientes y sigils | ✅ | Mejora, ruptura, mejora rápida, encantamientos. |
| Inventario y objetos | ✅ | Uso de objetos, cofres, conversión de piezas. |
| Tienda | 🟡 | Tiendas de las tablas del cliente con límites y refrescos. La tienda de skins aparece pero sin artículos. |
| Gacha | ✅ | Bandas, probabilidad garantizada, selección de UP, historial. |
| Correo, tareas y logros | 🟡 | Correo, tareas diarias/semanales y la mayoría de logros; faltan algunos logros de estadísticas de combate. |
| Firmas diarias | 🟡 | Firma de 7 días y acumulativas; la firma mensual necesita su tabla de premios. |
| Batallas | ✅ | Servidor de batalla UDP local, resultado real, estrellas, botín, barrido. |
| Capítulos e historia | ✅ | Mapa de capítulos, tesoros, historias. |
| Dormitorio, comedor e ídolos | 🟡 | Habitaciones, muebles, comedor, encargos, entrenamiento de ídolos, PVE y tablón semanal. Sin PVP de ídolos. |
| Contenido semanal | ✅ | 梦境再构, 黑区净化, abismo, 迭代校验, 介质攫取, 因果观测, matriz (矩阵), poliedro, RogueTeam, 心象梦魇. |
| Encantamiento y equipo (combates) | ✅ | Ofertas, refrescos, suit UP, contadores de garantía. |
| Minijuegos y modos de evento | ✅ | Música, vehicle ball, Kadas, 诡谈夜话, autoajedrez (PVE) y modos de evento de los capítulos. |
| Actividades temporales | 🟡 | Solo se envían si su actividad está abierta; sorteos, firmas de skin y PT scroll aún sin comandos de juego. |
| Matriz (矩阵) | 🟡 | Partida completa; los premios por rango de puntos no dan objetos. |
| Amigos, chat, gremio y cooperativo | ❌ | No hay sistema multijugador. |
| PVP y clasificaciones | ❌ | No disponible. |
| Pagos reales | ❌ | Las compras con dinero real no están disponibles. |
| Otros sistemas del cliente | ❌ | Unos 470 comandos sin implementar (pesca, actividades de gremio, juego de navegación, etc.). |
| GM Tools | ✅ | Arranque, hosts, TLS, panel GM y actualización automática. |

### Aviso

Proyecto no oficial, sin relación con los desarrolladores ni distribuidores de Aether Gazer. Solo para uso personal y local. No se incluyen archivos del cliente del juego.

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
| Updates | Checks this repository for new commits, downloads the update, keeps your database and restarts the server. Can do it automatically. |
| GM panel | Player accounts, modifiers (characters), items and mail. |
| Live log | Shows `logs/trimurti-live.log` and copies a diagnostic to the clipboard. |
| Languages | Español, English, 中文 and 日本語. |

### Requirements

- 64-bit Windows 10/11.
- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- [Git](https://git-scm.com/) (recommended; without it GM Tools downloads updates as a ZIP).
- Aether Gazer for PC installed (default `C:\Program Files\AetherGazerLauncher\AetherGazer`).
- Run GM Tools **as administrator** (required for the `hosts` file and the certificate).
- Free ports: 80, 443, 5180 (HTTP/HTTPS/GM API), 8102, 8105 (game TCP) and 6105 (battle UDP).

### Install and use

1. Download or build Trimūrti GM Tools (see *Building GM Tools*).
2. Open it as administrator.
3. In the **Server** tab, set the game folder and the server folder. If the server folder is empty, GM Tools downloads the server from this repository.
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

- Database: `src/Trimūrti.Server/bin/Debug/net10.0/Data/trimurti.db` inside the server folder (or the `TRIMURTI_DB_PATH` path).
- Log: `logs/trimurti-live.log`.
- Operator settings (open systems, announcements, hidden content…): `src/Trimūrti.Server/Data/Configs/server_settings.json`.
- Updates never overwrite the database or the logs. Still, copy `trimurti.db*` before major changes.

### Development

```powershell
dotnet build "src/Trimūrti.Server"
```

### Roadmap

✅ works · 🟡 partial · ❌ not available

| Area | Status | Details |
|---|---|---|
| Accounts and SDK login | ✅ | Local accounts, token login, zone list, notices. |
| Login screen | ✅ | The whole initial state is built from player and server data. |
| Profile and customization | ✅ | Name, signature, avatar, frame, card, chat bubble, tags, birthday, music. |
| Characters | ✅ | Level, breakthrough, stars, skills, astrolabe, skins, trust, oath, chips. |
| Weapons, servants and sigils | ✅ | Upgrade, breakthrough, quick upgrade, enchantments. |
| Inventory and items | ✅ | Item use, chests, piece conversion. |
| Shop | 🟡 | Shops from the client tables with limits and refreshes. The skin shop appears but has no goods. |
| Gacha | ✅ | Banners, pity, UP selection, history. |
| Mail, tasks and achievements | 🟡 | Mail, daily/weekly tasks and most achievements; some combat-statistics achievements are missing. |
| Daily sign-in | 🟡 | 7-day and cumulative sign-ins; the monthly sign-in needs its reward table. |
| Battles | ✅ | Local UDP battle server, real result, stars, drops, sweep. |
| Chapters and story | ✅ | Chapter map, treasures, stories. |
| Dorm, canteen and idols | 🟡 | Rooms, furniture, canteen, entrusts, idol training, PVE and weekly board. No idol PVP. |
| Weekly content | ✅ | 梦境再构, 黑区净化, abyss, 迭代校验, 介质攫取, 因果观测, matrix (矩阵), polyhedron, RogueTeam, 心象梦魇. |
| Enchantment and equipment battles | ✅ | Offers, refreshes, UP suit, guarantee counters. |
| Minigames and event modes | ✅ | Music, vehicle ball, Kadas, 诡谈夜话, autochess (PVE) and the chapters' event modes. |
| Limited-time activities | 🟡 | Sent only while their activity is open; draws, skin sign-ins and PT scroll still lack their gameplay commands. |
| Matrix (矩阵) | 🟡 | Full run; point-rank rewards give no items. |
| Friends, chat, guild and co-op | ❌ | No multiplayer system. |
| PVP and rankings | ❌ | Not available. |
| Real payments | ❌ | Real-money purchases are not available. |
| Other client systems | ❌ | About 470 commands not implemented (fishing, guild activities, sailing game, etc.). |
| GM Tools | ✅ | Startup, hosts, TLS, GM panel and automatic updates. |

### Notice

Unofficial project, not affiliated with the developers or publishers of Aether Gazer. For personal, local use only. No game client files are included.

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
| 更新 | 检查本仓库是否有新提交，下载更新，保留数据库并重启服务器。可设为自动更新。 |
| GM 面板 | 玩家账号、修正者（角色）、道具与邮件。 |
| 实时日志 | 显示 `logs/trimurti-live.log`，并可复制诊断信息到剪贴板。 |
| 语言 | Español、English、中文、日本語。 |

### 运行要求

- 64 位 Windows 10/11。
- [.NET 10 SDK](https://dotnet.microsoft.com/download)。
- [Git](https://git-scm.com/)（推荐；没有 Git 时 GM Tools 以 ZIP 方式下载更新）。
- 已安装 PC 版深空之眼（默认 `C:\Program Files\AetherGazerLauncher\AetherGazer`）。
- 以 **管理员身份** 运行 GM Tools（修改 `hosts` 文件和安装证书需要）。
- 需要空闲端口：80、443、5180（HTTP/HTTPS/GM API），8102、8105（游戏 TCP），6105（战斗 UDP）。

### 安装与使用

1. 下载或编译 Trimūrti GM Tools（见“编译 GM Tools”）。
2. 以管理员身份运行。
3. 在 **服务器** 页面设置游戏目录和服务器目录。若服务器目录为空，GM Tools 会从本仓库下载服务器。
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

- 数据库：服务器目录下的 `src/Trimūrti.Server/bin/Debug/net10.0/Data/trimurti.db`（或 `TRIMURTI_DB_PATH` 指定的路径）。
- 日志：`logs/trimurti-live.log`。
- 运营设置（开放系统、公告、隐藏内容等）：`src/Trimūrti.Server/Data/Configs/server_settings.json`。
- 更新不会覆盖数据库和日志。重大改动前仍建议备份 `trimurti.db*`。

### 开发

```powershell
dotnet build "src/Trimūrti.Server"
```

### 路线图

✅ 可用 · 🟡 部分可用 · ❌ 不可用

| 模块 | 状态 | 说明 |
|---|---|---|
| 账号与 SDK 登录 | ✅ | 本地账号、令牌登录、服务器列表、公告。 |
| 登录流程 | ✅ | 全部初始数据均由玩家与服务器数据生成。 |
| 个人资料与外观 | ✅ | 昵称、签名、头像、头像框、名片、聊天气泡、标签、生日、音乐。 |
| 修正者（角色） | ✅ | 等级、突破、星级、技能、星盘、皮肤、信赖、誓约、芯片。 |
| 武器、钥从与刻印 | ✅ | 强化、突破、快速强化、附魔。 |
| 背包与道具 | ✅ | 使用道具、宝箱、碎片转换。 |
| 商店 | 🟡 | 客户端配置表中的商店，含限购与刷新。换装商店可见但没有商品。 |
| 抽卡 | ✅ | 卡池、保底、UP 选择、记录。 |
| 邮件、任务与成就 | 🟡 | 邮件、每日/每周任务与大部分成就；部分战斗统计类成就尚未实现。 |
| 签到 | 🟡 | 七日签到与累计签到；月签到缺少奖励表。 |
| 战斗 | ✅ | 本地 UDP 战斗服务器、真实结果、星级、掉落、扫荡。 |
| 章节与剧情 | ✅ | 章节地图、宝箱、剧情。 |
| 宿舍、餐厅与偶像 | 🟡 | 房间、家具、餐厅、委托、偶像训练、PVE 与每周公告板。暂无偶像 PVP。 |
| 每周内容 | ✅ | 梦境再构、黑区净化、失序深阱、迭代校验、介质攫取、因果观测、多维变量（矩阵）、多面体、RogueTeam、心象梦魇。 |
| 附魔战与装备战 | ✅ | 关卡刷新、UP 套装、保底计数。 |
| 小游戏与活动玩法 | ✅ | 音乐、vehicle ball、Kadas、诡谈夜话、自走棋（PVE）及章节活动玩法。 |
| 限时活动 | 🟡 | 仅在活动开启时下发；抽取、皮肤签到、PT 卷轴等玩法指令尚未实现。 |
| 多维变量（矩阵） | 🟡 | 完整流程；积分段位奖励不发放道具。 |
| 好友、聊天、公会与协作 | ❌ | 无多人系统。 |
| PVP 与排行榜 | ❌ | 不可用。 |
| 真实支付 | ❌ | 不支持真实货币购买。 |
| 其他客户端系统 | ❌ | 约 470 条指令尚未实现（钓鱼、公会活动、航海玩法等）。 |
| GM Tools | ✅ | 启动、hosts、TLS、GM 面板与自动更新。 |

### 声明

非官方项目，与深空之眼的开发商及发行商无任何关联。仅供个人本地使用。仓库不包含任何游戏客户端文件。

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
| アップデート | このリポジトリの新しいコミットを確認し、データベースを保持したまま更新してサーバーを再起動します。自動更新も可能です。 |
| GM パネル | プレイヤーアカウント、修正者（キャラクター）、アイテム、メール。 |
| ライブログ | `logs/trimurti-live.log` を表示し、診断情報をクリップボードにコピーします。 |
| 言語 | Español、English、中文、日本語。 |

### 動作環境

- 64 ビット版 Windows 10/11。
- [.NET 10 SDK](https://dotnet.microsoft.com/download)。
- [Git](https://git-scm.com/)（推奨。ない場合、GM Tools は ZIP で更新をダウンロードします）。
- PC 版 Aether Gazer がインストールされていること（既定は `C:\Program Files\AetherGazerLauncher\AetherGazer`）。
- GM Tools を **管理者として** 実行すること（`hosts` ファイルと証明書の操作に必要）。
- 空きポート：80、443、5180（HTTP/HTTPS/GM API）、8102、8105（ゲーム TCP）、6105（バトル UDP）。

### インストールと使い方

1. Trimūrti GM Tools をダウンロードまたはビルドします（「GM Tools のビルド」を参照）。
2. 管理者として起動します。
3. **サーバー** タブでゲームフォルダとサーバーフォルダを設定します。サーバーフォルダが空の場合、GM Tools がこのリポジトリからサーバーをダウンロードします。
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

- データベース：サーバーフォルダ内の `src/Trimūrti.Server/bin/Debug/net10.0/Data/trimurti.db`（または `TRIMURTI_DB_PATH` で指定したパス）。
- ログ：`logs/trimurti-live.log`。
- 運営設定（開放システム、お知らせ、非表示コンテンツなど）：`src/Trimūrti.Server/Data/Configs/server_settings.json`。
- アップデートはデータベースとログを上書きしません。それでも大きな変更の前には `trimurti.db*` をバックアップしてください。

### 開発

```powershell
dotnet build "src/Trimūrti.Server"
```

### ロードマップ

✅ 動作 · 🟡 一部動作 · ❌ 未対応

| 項目 | 状態 | 詳細 |
|---|---|---|
| アカウントと SDK ログイン | ✅ | ローカルアカウント、トークンログイン、サーバー一覧、お知らせ。 |
| ログイン処理 | ✅ | 初期データはすべてプレイヤーとサーバーのデータから生成。 |
| プロフィールとカスタマイズ | ✅ | 名前、署名、アイコン、フレーム、名刺、吹き出し、タグ、誕生日、BGM。 |
| 修正者（キャラクター） | ✅ | レベル、突破、星、スキル、星盤、スキン、信頼、誓約、チップ。 |
| 武器・従者・刻印 | ✅ | 強化、突破、一括強化、エンチャント。 |
| インベントリとアイテム | ✅ | アイテム使用、宝箱、欠片の変換。 |
| ショップ | 🟡 | クライアントのテーブルにあるショップ（購入制限・更新あり）。スキンショップは表示されるが商品なし。 |
| ガチャ | ✅ | ピックアップ、天井、UP 選択、履歴。 |
| メール・タスク・実績 | 🟡 | メール、デイリー/ウィークリータスク、大半の実績。一部の戦闘統計実績は未対応。 |
| ログインボーナス | 🟡 | 7 日間と累計ログイン。月間ログインは報酬テーブルが必要。 |
| バトル | ✅ | ローカル UDP バトルサーバー、実際の結果、星、ドロップ、スイープ。 |
| チャプターとストーリー | ✅ | チャプターマップ、宝箱、ストーリー。 |
| 寮・食堂・アイドル | 🟡 | 部屋、家具、食堂、依頼、アイドル育成、PVE、週間掲示板。アイドル PVP は未対応。 |
| ウィークリーコンテンツ | ✅ | 梦境再构、黑区净化、深淵、迭代校验、介质攫取、因果观测、マトリックス（矩阵）、多面体、RogueTeam、心象梦魇。 |
| エンチャント戦・装備戦 | ✅ | ステージ更新、UP セット、天井カウンター。 |
| ミニゲームとイベントモード | ✅ | 音楽、vehicle ball、Kadas、诡谈夜话、オートチェス（PVE）、各チャプターのイベントモード。 |
| 期間限定イベント | 🟡 | イベント開催中のみ送信。抽選、スキンログインボーナス、PT スクロールのゲーム内コマンドは未対応。 |
| マトリックス（矩阵） | 🟡 | 一連のプレイが可能。ポイント段位報酬はアイテムなし。 |
| フレンド・チャット・ギルド・協力 | ❌ | マルチプレイ機能なし。 |
| PVP とランキング | ❌ | 未対応。 |
| 実際の課金 | ❌ | 実際のお金での購入は不可。 |
| その他のクライアント機能 | ❌ | 約 470 個のコマンドが未実装（釣り、ギルドイベント、航海ゲームなど）。|
| GM Tools | ✅ | 起動、hosts、TLS、GM パネル、自動更新。 |

### 注意事項

非公式プロジェクトであり、Aether Gazer の開発元・運営元とは一切関係ありません。個人のローカル利用のみを目的としています。ゲームクライアントのファイルは含まれていません。
