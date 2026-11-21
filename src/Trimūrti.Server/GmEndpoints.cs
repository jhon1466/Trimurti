namespace Trimūrti.Server;

public sealed record GmGrantRequest(long Uid, long ItemId, long Count);
public sealed record GmUnlockHeroRequest(long Uid, long HeroId);
public sealed record GmMailRequest(long Uid, string Title, string Content, string? Sender, IReadOnlyList<GmAttachment>? Attachments);
public sealed record GmAttachment(long ItemId, long Count);
public sealed record GmUpdateAccountRequest(string? Nickname, int? Level);

/// <summary>Localhost GM / administration API for inspecting and mutating player accounts.</summary>
public static class GmEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/gm", () => Results.Content(GmPanelHtml, "text/html; charset=utf-8"));

        app.MapGet("/api/gm/accounts", async (GameAccountRepository repo, CancellationToken ct) =>
        {
            var accounts = await repo.ListAccountsAsync(ct);
            return Results.Ok(accounts.Select(a => new { uid = a.Uid.ToString(), a.Account, a.Nickname, a.Level, a.HeroCount }));
        });

        app.MapGet("/api/gm/accounts/{uid:long}", async (long uid, GameAccountRepository repo, CancellationToken ct) =>
        {
            var state = await repo.GetInitialLoginStateAsync(uid, ct);
            if (state is null) return Results.NotFound(new { error = $"Account {uid} not found." });
            var equips = await repo.GetEquipsAsync(uid, ct);
            var servants = await repo.GetServantsAsync(uid, ct);
            var mails = await repo.GetMailsAsync(uid, ct);
            return Results.Ok(new
            {
                state.Profile,
                heroCount = state.Heroes.Count,
                state.Heroes,
                state.Inventory,
                equips,
                servants,
                mails
            });
        });

        app.MapPut("/api/gm/accounts/{uid:long}", async (long uid, GmUpdateAccountRequest req, GameAccountRepository repo, CancellationToken ct) =>
        {
            var ok = await repo.UpdateAccountAdminAsync(uid, req.Nickname, req.Level, ct);
            return ok ? Results.Ok(new { uid, updated = true }) : Results.NotFound(new { error = $"Account {uid} not found or no changes." });
        });

        app.MapDelete("/api/gm/accounts/{uid:long}", async (long uid, GameAccountRepository repo, CancellationToken ct) =>
        {
            var ok = await repo.DeleteAccountAsync(uid, ct);
            return ok ? Results.Ok(new { uid, deleted = true }) : Results.NotFound(new { error = $"Account {uid} not found." });
        });

        app.MapPost("/api/gm/grant", async (GmGrantRequest req, GameAccountRepository repo, CancellationToken ct) =>
        {
            if (req.Uid <= 0 || req.ItemId <= 0 || req.Count <= 0)
                return Results.BadRequest(new { error = "uid, itemId, and count must be positive." });
            var newBalance = await repo.AddItemAsync(req.Uid, req.ItemId, req.Count, ct);
            return Results.Ok(new { req.Uid, req.ItemId, granted = req.Count, balance = newBalance });
        });

        app.MapPost("/api/gm/unlock-hero", async (GmUnlockHeroRequest req, GameAccountRepository repo, CancellationToken ct) =>
        {
            var (ok, alreadyOwned) = await repo.UnlockHeroAsync(req.Uid, req.HeroId, cancellationToken: ct);
            return ok
                ? Results.Ok(new { req.Uid, req.HeroId, unlocked = !alreadyOwned, alreadyOwned })
                : Results.BadRequest(new { error = "Invalid uid or heroId." });
        });

        app.MapPost("/api/gm/unlock-all-heroes", async (long uid, GameAccountRepository repo, SourceGameConfig config, CancellationToken ct) =>
        {
            if (uid <= 0) return Results.BadRequest(new { error = "Invalid uid." });
            var added = await repo.UnlockAllHeroesAsync(uid, config.GetAllHeroIds(), ct);
            return Results.Ok(new { uid, addedHeroes = added });
        });

        app.MapPost("/api/gm/unlock-all-skins", async (long uid, GameAccountRepository repo, SourceGameConfig config, CancellationToken ct) =>
        {
            if (uid <= 0) return Results.BadRequest(new { error = "Invalid uid." });
            var added = await repo.UnlockAllSkinsAsync(uid, config.GetSkinHeroMap(), ct);
            return Results.Ok(new { uid, addedSkins = added });
        });

        app.MapPost("/api/gm/grant-all-items", async (long uid, long? count, GameAccountRepository repo, SourceGameConfig config, CancellationToken ct) =>
        {
            if (uid <= 0) return Results.BadRequest(new { error = "Invalid uid." });
            var amount = Math.Clamp(count ?? 9999, 1, 9_999_999);
            var granted = await repo.GrantAllItemsAsync(uid, config.GetAllItemIds(), amount, ct);
            return Results.Ok(new { uid, grantedItems = granted, countEach = amount });
        });

        app.MapPost("/api/gm/mail", async (GmMailRequest req, GameAccountRepository repo, CancellationToken ct) =>
        {
            if (req.Uid <= 0 || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "uid and title are required." });
            static bool IsBlockedMailAttachment(long id) =>
                id == 30054 || id is >= 1_000_000_000 and <= 2_000_000_000 || id is >= 830_000 and <= 859_999;
            var attachments = (req.Attachments ?? [])
                .Where(a => a.ItemId > 0 && a.Count > 0 && !IsBlockedMailAttachment(a.ItemId))
                .Select(a => (a.ItemId, a.Count))
                .ToArray();
            var mailId = await repo.SendSystemMailAsync(req.Uid, req.Title, req.Content ?? "", req.Sender ?? "Trimūrti GM", attachments, ct);
            return Results.Ok(new { req.Uid, mailId, attachments = attachments.Length });
        });
    }

    private const string GmPanelHtml = """
<!doctype html>
<html lang="es">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Trimūrti GM Tools</title>
  <style>
    :root {
      --bg: #0b0f19; --card: #131a2a; --border: #232e47;
      --text: #e5ecf6; --muted: #8ea0bd; --accent: #f59e0b;
      --accent-hover: #fbbf24; --ok: #10b981; --err: #ef4444;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0; font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
      background: radial-gradient(circle at top, #172036 0%, var(--bg) 60%);
      color: var(--text); min-height: 100vh; padding: 24px;
    }
    .wrap { max-width: 1020px; margin: 0 auto; }
    header {
      display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between;
      margin-bottom: 20px; padding-bottom: 14px; border-bottom: 1px solid var(--border);
    }
    h1 { margin: 0; font-size: 1.4rem; letter-spacing: .4px; }
    .badge { font-size: .78rem; color: var(--accent); background: rgba(245,158,11,.12); padding: 4px 10px; border-radius: 999px; border: 1px solid rgba(245,158,11,.35); }
    .uid-bar { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 16px; }
    .card {
      background: var(--card); border: 1px solid var(--border); border-radius: 12px;
      padding: 16px; display: flex; flex-direction: column; gap: 10px;
    }
    .card h2 { margin: 0; font-size: 1.05rem; color: #fff; }
    .muted { color: var(--muted); font-size: .86rem; margin: 0; }
    label { font-size: .82rem; color: var(--muted); display: flex; flex-direction: column; gap: 4px; }
    input, select, textarea {
      background: #0d1322; border: 1px solid var(--border); color: var(--text);
      border-radius: 8px; padding: 8px 10px; font-size: .92rem; width: 100%;
    }
    input:focus, select:focus, textarea:focus { outline: none; border-color: var(--accent); }
    .row { display: flex; gap: 8px; flex-wrap: wrap; }
    .row > * { flex: 1 1 110px; }
    button {
      cursor: pointer; border: 0; border-radius: 8px; padding: 9px 14px;
      font-weight: 600; font-size: .9rem; transition: .15s ease;
      background: var(--accent); color: #111827;
    }
    button:hover { background: var(--accent-hover); }
    button.sec { background: #1e293b; color: var(--text); border: 1px solid var(--border); }
    button.sec:hover { background: #293548; }
    .presets { display: flex; flex-wrap: wrap; gap: 6px; }
    .presets button { font-size: .78rem; padding: 5px 9px; }
    pre {
      background: #080c15; border: 1px solid var(--border); border-radius: 10px;
      padding: 12px; font-size: .82rem; overflow: auto; max-height: 280px; margin: 0;
    }
    .status { font-size: .86rem; padding: 8px 12px; border-radius: 8px; display: none; }
    .status.ok { display: block; background: rgba(16,185,129,.12); border: 1px solid rgba(16,185,129,.4); color: #6ee7b7; }
    .status.err { display: block; background: rgba(239,68,68,.12); border: 1px solid rgba(239,68,68,.4); color: #fca5a5; }
  </style>
</head>
<body>
  <div class="wrap">
    <header>
      <div>
        <h1>Trimūrti GM Tools</h1>
        <p class="muted">Panel local de administración. Reinicia sesión en el juego para ver los cambios.</p>
      </div>
      <div class="uid-bar">
        <label style="flex-direction:row;align-items:center;gap:6px;">
          <span>Cuenta</span>
          <select id="accountSelect" style="width:240px;" onchange="$('uid').value=this.value; loadAccount();"></select>
        </label>
        <label style="flex-direction:row;align-items:center;gap:6px;">
          <span>UID</span>
          <input id="uid" value="3000000002" style="width:140px;" />
        </label>
        <button class="sec" onclick="loadAccount()">Cargar cuenta</button>
      </div>
    </header>

    <div id="status" class="status" style="margin-bottom:14px;"></div>

    <div class="grid">
      <div class="card">
        <h2>Personajes y Skins</h2>
        <p class="muted">Desbloquea todos los modificadores, todas las skins o uno por ID (ej. 1011, 1084).</p>
        <button onclick="unlockAll()">Desbloquear TODOS los personajes</button>
        <button onclick="unlockAllSkins()">Desbloquear TODAS las skins</button>
        <div class="row">
          <label>Hero ID <input id="heroId" type="number" value="1011" /></label>
        </div>
        <button class="sec" onclick="unlockOne()">Desbloquear personaje</button>
      </div>

      <div class="card">
        <h2>Monedas e Ítems</h2>
        <p class="muted">Otorga recursos directamente a la cuenta o todos los ítems del catálogo.</p>
        <button onclick="grantAllItems()">Dar TODOS los ítems y monedas (x9999)</button>
        <div class="presets">
          <button class="sec" onclick="setItem(1, 1000000)">Oro (ID 1)</button>
          <button class="sec" onclick="setItem(2, 100000)">Estrellas (ID 2)</button>
          <button class="sec" onclick="setItem(5, 200)">Tickets (ID 5)</button>
          <button class="sec" onclick="setItem(19, 200)">Tickets Precisos (ID 19)</button>
          <button class="sec" onclick="setItem(4, 240)">Energía (ID 4)</button>
        </div>
        <div class="row">
          <label>Item ID <input id="itemId" type="number" value="2" /></label>
          <label>Cantidad <input id="itemCount" type="number" value="50000" /></label>
        </div>
        <button onclick="grantItem()">Enviar ítem / moneda</button>
      </div>

      <div class="card">
        <h2>Enviar Correo</h2>
        <label>Título <input id="mailTitle" value="Regalo GM" /></label>
        <label>Mensaje <textarea id="mailBody" rows="2">Recompensa enviada desde Trimūrti GM Tools.</textarea></label>
        <div class="row">
          <label>Adjunto Item ID <input id="mailItemId" type="number" value="2" /></label>
          <label>Cantidad <input id="mailItemCount" type="number" value="10000" /></label>
        </div>
        <button onclick="sendMail()">Enviar correo</button>
      </div>

      <div class="card" style="grid-column: 1 / -1;">
        <div style="display:flex;justify-content:space-between;align-items:center;">
          <h2>Estado / Respuesta</h2>
          <span class="badge" id="summary">Sin cargar</span>
        </div>
        <pre id="output">Pulsa "Cargar cuenta" o ejecuta una acción...</pre>
      </div>
    </div>
  </div>

  <script>
    const $ = id => document.getElementById(id);
    function show(ok, msg, data) {
      const s = $('status');
      s.className = 'status ' + (ok ? 'ok' : 'err');
      s.textContent = msg;
      if (data !== undefined) $('output').textContent = JSON.stringify(data, null, 2);
    }
    async function api(url, opts) {
      try {
        const res = await fetch(url, opts);
        const text = await res.text();
        let data; try { data = JSON.parse(text); } catch { data = text; }
        if (!res.ok) { show(false, `Error ${res.status}`, data); return null; }
        return data;
      } catch (e) { show(false, e.message); return null; }
    }
    function uid() { return $('uid').value.trim().replace(/\D/g, ''); }
    function setItem(id, cnt) { $('itemId').value = id; $('itemCount').value = cnt; }
    async function loadAccount() {
      const data = await api(`/api/gm/accounts/${uid()}`);
      if (!data) return;
      $('summary').textContent = `${data.profile?.nickname ?? 'Jugador'} · Nivel ${data.profile?.level ?? '?'} · ${data.heroCount ?? 0} personajes`;
      show(true, 'Cuenta cargada correctamente.', data);
    }
    async function unlockAll() {
      const data = await api(`/api/gm/unlock-all-heroes?uid=${uid()}`, { method: 'POST' });
      if (data) show(true, `Personajes añadidos: ${data.addedHeroes}. Vuelve a entrar al juego para verlos.`, data);
    }
    async function unlockAllSkins() {
      const data = await api(`/api/gm/unlock-all-skins?uid=${uid()}`, { method: 'POST' });
      if (data) show(true, `Skins desbloqueadas: ${data.addedSkins}. Vuelve a entrar al juego para verlas.`, data);
    }
    async function grantAllItems() {
      const data = await api(`/api/gm/grant-all-items?uid=${uid()}&count=9999`, { method: 'POST' });
      if (data) show(true, `Ítems y monedas otorgados: ${data.grantedItems} (x${data.countEach}). Vuelve a entrar al juego para verlos.`, data);
    }
    async function unlockOne() {
      const data = await api('/api/gm/unlock-hero', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: `{"uid":${uid()},"heroId":${Number($('heroId').value)}}`
      });
      if (data) show(true, 'Operación completada.', data);
    }
    async function grantItem() {
      const data = await api('/api/gm/grant', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: `{"uid":${uid()},"itemId":${Number($('itemId').value)},"count":${Number($('itemCount').value)}}`
      });
      if (data) show(true, `Saldo actualizado: ${data.balance}`, data);
    }
    async function sendMail() {
      const itemId = Number($('mailItemId').value);
      const count = Number($('mailItemCount').value);
      const attachments = itemId > 0 && count > 0 ? [{ itemId, count }] : [];
      const payload = JSON.stringify({
        title: $('mailTitle').value,
        content: $('mailBody').value,
        sender: 'Trimūrti GM',
        attachments
      });
      const data = await api('/api/gm/mail', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: `{"uid":${uid()},${payload.slice(1)}`
      });
      if (data) show(true, `Correo enviado (ID ${data.mailId}).`, data);
    }
    async function initAccounts() {
      const list = await api('/api/gm/accounts');
      if (Array.isArray(list) && list.length > 0) {
        const sel = $('accountSelect');
        sel.innerHTML = '';
        for (const a of list) {
          const opt = document.createElement('option');
          opt.value = a.uid;
          opt.textContent = `${a.uid} (${a.account || a.nickname})`;
          sel.appendChild(opt);
        }
        $('uid').value = list[0].uid;
      }
      await loadAccount();
    }
    initAccounts();
  </script>
</body>
</html>
""";
}
