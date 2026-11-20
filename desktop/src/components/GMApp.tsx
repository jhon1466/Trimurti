import { For, Show, createMemo, createSignal, onMount } from 'solid-js';
import { invoke } from '@tauri-apps/api/core';
import ServerPanel, { type ServerStatus } from './ServerPanel';
import catalog from '../data/aether-catalog.json';
import { LANG_LABELS, translations, type Language } from '../i18n';

type Tab = 'server' | 'accounts' | 'overview' | 'heroes' | 'items' | 'mail';
type AccountSummary = { uid: string; account: string; nickname: string; level: number; heroCount: number };
type HeroInfo = { id: number; level: number; star: number; trustLevel: number; trustExperience: number };
type AccountDetail = {
  profile?: { uid: number; nickname: string; level: number; experience: number };
  heroCount: number;
  heroes?: HeroInfo[];
  inventory?: { id: number; count: number; source: string }[];
  equips?: unknown[];
  servants?: unknown[];
  mails?: unknown[];
};

const ZONE_OPTIONS = ['Olympus', 'Nile', 'Shinou', 'Yggdrasil', 'Asterism', 'Tian Yuan'];

export default function GMApp() {
  const savedLang = (typeof localStorage !== 'undefined' ? localStorage.getItem('trimurti_lang') : null) as Language | null;
  const [lang, setLang] = createSignal<Language>(savedLang && savedLang in translations ? savedLang : 'es');
  const t = (k: keyof typeof translations.es) => translations[lang()][k];

  const [tab, setTab] = createSignal<Tab>('server');
  const [serverStatus, setServerStatus] = createSignal<ServerStatus>();
  const [accounts, setAccounts] = createSignal<AccountSummary[]>([]);
  const [uid, setUid] = createSignal('3000000002');
  const [detail, setDetail] = createSignal<AccountDetail | null>(null);
  const [busy, setBusy] = createSignal(false);
  const [toast, setToast] = createSignal<{ msg: string; error?: boolean } | null>(null);

  // Account edit/delete state
  const [editNick, setEditNick] = createSignal('');
  const [editLevel, setEditLevel] = createSignal('100');
  const [confirmDeleteUid, setConfirmDeleteUid] = createSignal<string | null>(null);

  // Heroes filter
  const [heroQuery, setHeroQuery] = createSignal('');
  const [zoneFilter, setZoneFilter] = createSignal('ALL');

  // Items grant form
  const [itemId, setItemId] = createSignal('1');
  const [itemCount, setItemCount] = createSignal('10000');
  const [itemQuery, setItemQuery] = createSignal('');

  // Mail form
  const [mailTitle, setMailTitle] = createSignal('Recompensa Trimūrti');
  const [mailSender, setMailSender] = createSignal('Sistema Trimūrti');
  const [mailContent, setMailContent] = createSignal('Objetos enviados desde Trimūrti GM Tools.');
  const [mailAttachId, setMailAttachId] = createSignal('1');
  const [mailAttachNum, setMailAttachNum] = createSignal('5000');

  let toastTimer: number | undefined;
  function notify(msg: string, error = false) {
    setToast({ msg, error });
    if (toastTimer) window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => setToast(null), 4000);
  }

  function changeLang(next: Language) {
    setLang(next);
    if (typeof localStorage !== 'undefined') localStorage.setItem('trimurti_lang', next);
  }

  async function gm<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
    return invoke<T>('gm_request', { method, path, body: body ?? null });
  }

  async function refreshAccounts() {
    try {
      const list = await gm<AccountSummary[]>('GET', '/api/gm/accounts');
      setAccounts(list);
      if (list.length > 0 && !list.some((a) => a.uid === uid())) {
        setUid(list[0].uid);
      }
      await loadAccount();
    } catch (e) {
      if (serverStatus()?.serverRunning) notify(String(e), true);
    }
  }

  async function loadAccount() {
    if (!uid()) return;
    try {
      const data = await gm<AccountDetail>('GET', `/api/gm/accounts/${uid()}`);
      setDetail(data);
      if (data.profile) {
        setEditNick(data.profile.nickname);
        setEditLevel(String(data.profile.level));
      }
    } catch (e) {
      setDetail(null);
      if (serverStatus()?.serverRunning) notify(String(e), true);
    }
  }

  async function runGm(fn: () => Promise<unknown>, okText: string, reloadList = false) {
    setBusy(true);
    try {
      await fn();
      notify(okText);
      if (reloadList) await refreshAccounts();
      else await loadAccount();
    } catch (e) {
      notify(String(e), true);
    } finally {
      setBusy(false);
    }
  }

  const ownedHeroMap = createMemo(() => {
    const map = new Map<number, HeroInfo>();
    for (const h of detail()?.heroes ?? []) map.set(h.id, h);
    return map;
  });

  const filteredHeroes = createMemo(() => {
    const q = heroQuery().trim().toLowerCase();
    const z = zoneFilter();
    return catalog.heroes.filter((h) => {
      if (z !== 'ALL' && h.zone !== z) return false;
      if (!q) return true;
      return String(h.id).includes(q) || h.zone.toLowerCase().includes(q) || h.name.toLowerCase().includes(q);
    });
  });

  const filteredItems = createMemo(() => {
    const q = itemQuery().trim().toLowerCase();
    return catalog.items.filter((it) => {
      if (!q) return true;
      return String(it.id).includes(q) || it.name.toLowerCase().includes(q) || it.cat.toLowerCase().includes(q);
    });
  });

  onMount(() => {
    void refreshAccounts();
  });

  const navItems: { id: Tab; key: keyof typeof translations.es; icon: string }[] = [
    { id: 'server', key: 'navServer', icon: 'fa-solid fa-server' },
    { id: 'accounts', key: 'navAccounts', icon: 'fa-solid fa-users-gear' },
    { id: 'overview', key: 'navOverview', icon: 'fa-solid fa-sliders' },
    { id: 'heroes', key: 'navHeroes', icon: 'fa-solid fa-user-shield' },
    { id: 'items', key: 'navItems', icon: 'fa-solid fa-cubes' },
    { id: 'mail', key: 'navMail', icon: 'fa-solid fa-envelope' },
  ];

  return (
    <div class="app-shell flex h-screen w-screen overflow-hidden">
      <aside class="sidebar w-64 shrink-0 border-r border-cyan-300/10 p-4 flex flex-col justify-between">
        <div class="flex flex-col gap-5">
          <div class="flex items-center gap-3 px-1">
            <img
              src="/icon.png"
              alt="Trimūrti"
              class="brand-mark h-10 w-10 rounded-xl object-cover border border-cyan-300/35"
            />
            <div>
              <div class="text-sm font-extrabold tracking-wide text-ice-100">TRIMŪRTI</div>
              <div class="text-[10px] tracking-widest uppercase text-cyan-300/80">{t('brandSub')}</div>
            </div>
          </div>

          <nav class="flex flex-col gap-1.5">
            <For each={navItems}>
              {(item) => (
                <button
                  class={`nav-item flex items-center gap-3 rounded-lg px-3 py-2.5 text-xs font-semibold text-ice-300 ${tab() === item.id ? 'active-nav' : 'hover:bg-white/5'}`}
                  onClick={() => setTab(item.id)}
                >
                  <i class={`${item.icon} w-4 text-center ${tab() === item.id ? 'text-cyan-300' : 'text-ice-500'}`} />
                  <span>{t(item.key)}</span>
                </button>
              )}
            </For>
          </nav>
        </div>

        <div class="flex flex-col gap-3">
          <div class="flex items-center gap-2">
            <i class="fa-solid fa-globe text-xs text-cyan-300/80" />
            <select
              class="input-field flex-1 min-h-8 text-[11px]"
              value={lang()}
              onChange={(e) => changeLang(e.currentTarget.value as Language)}
            >
              <For each={Object.keys(LANG_LABELS) as Language[]}>
                {(code) => <option value={code}>{LANG_LABELS[code]}</option>}
              </For>
            </select>
          </div>

          <div class="surface-card p-3 flex flex-col gap-2">
            <div class="flex items-center justify-between text-[11px]">
              <span class="text-ice-500">{t('status')}</span>
              <span class="flex items-center gap-1.5 font-semibold text-ice-100">
                <i class={`status-dot ${serverStatus()?.serverRunning ? 'online' : ''}`} />
                {serverStatus()?.serverRunning ? t('connected') : t('offline')}
              </span>
            </div>
            <div class="text-[10px] text-ice-500">
              {t('activeUid')}: <span class="text-cyan-300 font-mono">{uid()}</span>
            </div>
          </div>
        </div>
      </aside>

      <main class="flex-1 flex flex-col min-w-0 overflow-hidden">
        <header class="topbar h-14 shrink-0 border-b border-cyan-300/10 px-6 flex items-center justify-between gap-4">
          <div class="flex items-center gap-3">
            <span class="tiny-chip">
              <i class="fa-solid fa-shield-halved" /> {t('adminConsole')}
            </span>
            <Show when={detail()?.profile}>
              <span class="text-xs text-ice-300">
                <b class="text-ice-100">{detail()?.profile?.nickname}</b> · {t('levelShort')}
                {detail()?.profile?.level} · {detail()?.heroCount} {t('modifiersCount')}
              </span>
            </Show>
          </div>
          <div class="flex items-center gap-2">
            <select
              class="input-field w-56"
              value={uid()}
              onChange={(e) => {
                setUid(e.currentTarget.value);
                void loadAccount();
              }}
            >
              <Show when={accounts().length > 0} fallback={<option value={uid()}>UID {uid()}</option>}>
                <For each={accounts()}>
                  {(a) => (
                    <option value={a.uid}>
                      {a.nickname} ({a.uid}) · {t('levelShort')}
                      {a.level}
                    </option>
                  )}
                </For>
              </Show>
            </select>
            <button class="quiet-button" onClick={() => void refreshAccounts()}>
              <i class="fa-solid fa-rotate" />
            </button>
          </div>
        </header>

        <div class="content-scroll flex-1 overflow-y-auto p-6">
          <Show when={tab() === 'server'}>
            <ServerPanel
              language={lang()}
              onStarted={() => void refreshAccounts()}
              onStatusChange={(s) => setServerStatus(s)}
              notify={notify}
            />
          </Show>

          <Show when={tab() === 'accounts'}>
            <div class="flex flex-col gap-5">
              <div>
                <h2 class="section-title">{t('accTitle')}</h2>
                <p class="section-subtitle">{t('accHint')}</p>
              </div>

              <div class="grid grid-cols-1 lg:grid-cols-3 gap-4">
                <div class="surface-card p-4 lg:col-span-2 flex flex-col gap-3">
                  <div class="flex items-center justify-between">
                    <h3 class="card-title flex items-center gap-2">
                      <i class="fa-solid fa-address-book text-cyan-300" /> {t('accList')} ({accounts().length})
                    </h3>
                    <button class="quiet-button" onClick={() => void refreshAccounts()}>
                      <i class="fa-solid fa-rotate" /> {t('refresh')}
                    </button>
                  </div>

                  <Show
                    when={accounts().length > 0}
                    fallback={<div class="text-xs text-ice-500 py-6 text-center">{t('accEmpty')}</div>}
                  >
                    <div class="flex flex-col divide-y divide-cyan-300/10">
                      <For each={accounts()}>
                        {(acc) => (
                          <div class="py-3 flex flex-wrap items-center justify-between gap-3">
                            <div class="flex items-center gap-3">
                              <div class="h-9 w-9 rounded-lg bg-cyan-500/10 border border-cyan-300/25 grid place-items-center text-cyan-300 font-bold text-xs">
                                <i class="fa-solid fa-user-astronaut" />
                              </div>
                              <div>
                                <div class="text-xs font-bold text-ice-100">
                                  {acc.nickname}{' '}
                                  <span class="text-[10px] font-mono text-cyan-300">({acc.uid})</span>
                                </div>
                                <div class="text-[11px] text-ice-500">
                                  {acc.account} · {t('levelShort')}
                                  {acc.level} · {acc.heroCount} {t('modifiersCount')}
                                </div>
                              </div>
                            </div>
                            <div class="flex items-center gap-2">
                              <button
                                class={uid() === acc.uid ? 'tiny-chip cursor-default' : 'quiet-button'}
                                onClick={() => {
                                  setUid(acc.uid);
                                  setConfirmDeleteUid(null);
                                  void loadAccount();
                                }}
                              >
                                <i class="fa-solid fa-check" /> {uid() === acc.uid ? t('accSelected') : t('accSelect')}
                              </button>
                              <button
                                class="danger-button min-h-8 px-3 text-[11px]"
                                disabled={busy()}
                                onClick={() => {
                                  if (confirmDeleteUid() !== acc.uid) {
                                    setConfirmDeleteUid(acc.uid);
                                    notify(t('accConfirmDel'));
                                    return;
                                  }
                                  setConfirmDeleteUid(null);
                                  void runGm(
                                    () => gm('DELETE', `/api/gm/accounts/${acc.uid}`),
                                    t('accDeleted'),
                                    true,
                                  );
                                }}
                              >
                                <i class="fa-solid fa-trash" /> {t('accDelete')}
                              </button>
                            </div>
                          </div>
                        )}
                      </For>
                    </div>
                  </Show>
                </div>

                <div class="surface-card p-4 flex flex-col gap-3 h-fit">
                  <h3 class="card-title flex items-center gap-2">
                    <i class="fa-solid fa-user-pen text-gold-300" /> {t('accEditTitle')}
                  </h3>
                  <div class="text-[11px] font-mono text-cyan-300">UID: {uid()}</div>
                  <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                    <span>{t('accNick')}</span>
                    <input class="input-field" value={editNick()} onInput={(e) => setEditNick(e.currentTarget.value)} />
                  </label>
                  <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                    <span>{t('accLevel')}</span>
                    <input
                      class="input-field"
                      type="number"
                      min="1"
                      max="100"
                      value={editLevel()}
                      onInput={(e) => setEditLevel(e.currentTarget.value)}
                    />
                  </label>
                  <button
                    class="primary-button w-full"
                    disabled={busy() || !detail()?.profile}
                    onClick={() =>
                      runGm(
                        () =>
                          gm('PUT', `/api/gm/accounts/${uid()}`, {
                            nickname: editNick(),
                            level: Number(editLevel()),
                          }),
                        t('accUpdated'),
                        true,
                      )
                    }
                  >
                    <i class="fa-solid fa-floppy-disk" /> {t('accSave')}
                  </button>
                </div>
              </div>
            </div>
          </Show>

          <Show when={tab() === 'overview'}>
            <div class="flex flex-col gap-5">
              <div>
                <h2 class="section-title">{t('ovTitle')}</h2>
                <p class="section-subtitle">{t('ovHint')}</p>
              </div>

              <div class="grid grid-cols-2 md:grid-cols-4 gap-3">
                <div class="stat-card">
                  <div class="text-[11px] text-ice-500 flex items-center gap-1.5">
                    <i class="fa-solid fa-user-shield text-cyan-300" /> {t('modifiersCount')}
                  </div>
                  <div class="mt-2 text-2xl font-black text-cyan-300">{detail()?.heroCount ?? 0}</div>
                </div>
                <div class="stat-card">
                  <div class="text-[11px] text-ice-500 flex items-center gap-1.5">
                    <i class="fa-solid fa-gem text-ice-300" /> {t('sigils')}
                  </div>
                  <div class="mt-2 text-2xl font-black text-ice-100">{detail()?.equips?.length ?? 0}</div>
                </div>
                <div class="stat-card">
                  <div class="text-[11px] text-ice-500 flex items-center gap-1.5">
                    <i class="fa-solid fa-wand-magic-sparkles text-gold-300" /> {t('servants')}
                  </div>
                  <div class="mt-2 text-2xl font-black text-gold-300">{detail()?.servants?.length ?? 0}</div>
                </div>
                <div class="stat-card">
                  <div class="text-[11px] text-ice-500 flex items-center gap-1.5">
                    <i class="fa-solid fa-envelope text-ice-300" /> {t('mails')}
                  </div>
                  <div class="mt-2 text-2xl font-black text-ice-100">{detail()?.mails?.length ?? 0}</div>
                </div>
              </div>

              <div class="surface-card p-5 flex flex-col gap-3">
                <h3 class="card-title flex items-center gap-2">
                  <i class="fa-solid fa-bolt text-gold-300" /> {t('massActions')}
                </h3>
                <p class="card-caption">{t('massHint')}</p>
                <div class="flex flex-wrap gap-2 pt-1">
                  <button
                    class="primary-button"
                    disabled={busy()}
                    onClick={() =>
                      runGm(() => gm('POST', `/api/gm/unlock-all-heroes?uid=${uid()}`), t('allHeroesOk'), true)
                    }
                  >
                    <i class="fa-solid fa-users" /> {t('unlockAllHeroes')}
                  </button>
                  <button
                    class="secondary-button"
                    disabled={busy()}
                    onClick={() =>
                      runGm(() => gm('POST', `/api/gm/unlock-all-skins?uid=${uid()}`), t('allSkinsOk'))
                    }
                  >
                    <i class="fa-solid fa-shirt" /> {t('unlockAllSkins')}
                  </button>
                  <button
                    class="gold-button"
                    disabled={busy()}
                    onClick={() =>
                      runGm(() => gm('POST', `/api/gm/grant-all-items?uid=${uid()}&count=9999`), t('allItemsOk'))
                    }
                  >
                    <i class="fa-solid fa-boxes-stacked" /> {t('grantAllItems')}
                  </button>
                </div>
              </div>
            </div>
          </Show>

          <Show when={tab() === 'heroes'}>
            <div class="flex flex-col gap-4">
              <div class="flex flex-wrap items-end justify-between gap-3">
                <div>
                  <h2 class="section-title">
                    {t('heroTitle')} ({catalog.heroes.length})
                  </h2>
                  <p class="section-subtitle">{t('heroHint')}</p>
                </div>
                <div class="flex flex-wrap gap-2">
                  <div class="relative">
                    <i class="fa-solid fa-magnifying-glass text-xs absolute left-3 top-3 text-ice-500" />
                    <input
                      class="input-field pl-8 w-52"
                      value={heroQuery()}
                      onInput={(e) => setHeroQuery(e.currentTarget.value)}
                      placeholder={t('heroSearch')}
                    />
                  </div>
                  <select
                    class="input-field w-40"
                    value={zoneFilter()}
                    onChange={(e) => setZoneFilter(e.currentTarget.value)}
                  >
                    <option value="ALL">{t('allZones')}</option>
                    <For each={ZONE_OPTIONS}>{(z) => <option value={z}>{z}</option>}</For>
                  </select>
                </div>
              </div>

              <div class="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
                <For each={filteredHeroes()}>
                  {(h) => {
                    const owned = () => ownedHeroMap().get(h.id);
                    return (
                      <div class="surface-card p-3.5 flex flex-col justify-between gap-3">
                        <div class="flex items-start justify-between">
                          <div>
                            <div class="text-sm font-bold text-ice-100">{h.name}</div>
                            <div class="text-[11px] text-cyan-300/80">
                              {h.zone} · ID {h.id}
                            </div>
                          </div>
                          <span class={`tiny-chip ${owned() ? '' : 'muted'}`}>
                            {owned() ? `${t('levelShort')}${owned()!.level}` : t('locked')}
                          </span>
                        </div>
                        <Show
                          when={!owned()}
                          fallback={
                            <div class="text-[11px] text-ice-500">
                              {'★'.repeat(Math.max(1, Math.round(owned()!.star / 100)))} · {t('affection')} {t('levelShort')}
                              {owned()!.trustLevel}
                            </div>
                          }
                        >
                          <button
                            class="primary-button w-full"
                            disabled={busy()}
                            onClick={() =>
                              runGm(
                                () => gm('POST', '/api/gm/unlock-hero', { uid: Number(uid()), heroId: h.id }),
                                `${t('unlockedOk')}: ${h.id}`,
                                true,
                              )
                            }
                          >
                            <i class="fa-solid fa-unlock" /> {t('unlock')}
                          </button>
                        </Show>
                      </div>
                    );
                  }}
                </For>
              </div>
            </div>
          </Show>

          <Show when={tab() === 'items'}>
            <div class="flex flex-col gap-4">
              <div>
                <h2 class="section-title">{t('itemTitle')}</h2>
                <p class="section-subtitle">{t('itemHint')}</p>
              </div>

              <div class="surface-card p-4 grid grid-cols-1 md:grid-cols-4 gap-3 items-end">
                <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                  <span>{t('itemId')}</span>
                  <input class="input-field" value={itemId()} onInput={(e) => setItemId(e.currentTarget.value)} />
                </label>
                <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                  <span>{t('itemCount')}</span>
                  <input class="input-field" value={itemCount()} onInput={(e) => setItemCount(e.currentTarget.value)} />
                </label>
                <button
                  class="primary-button md:col-span-2"
                  disabled={busy()}
                  onClick={() =>
                    runGm(
                      () =>
                        gm('POST', '/api/gm/grant', {
                          uid: Number(uid()),
                          itemId: Number(itemId()),
                          count: Number(itemCount()),
                        }),
                      `${t('grantedOk')}: ${itemId()} x${itemCount()}`,
                    )
                  }
                >
                  <i class="fa-solid fa-gift" /> {t('grantBtn')} ({uid()})
                </button>
              </div>

              <div class="flex items-center justify-between gap-2">
                <h3 class="card-title">
                  {t('quickCatalog')} ({filteredItems().length})
                </h3>
                <input
                  class="input-field w-64"
                  value={itemQuery()}
                  onInput={(e) => setItemQuery(e.currentTarget.value)}
                  placeholder={t('itemSearch')}
                />
              </div>

              <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-2.5">
                <For each={filteredItems()}>
                  {(it) => (
                    <button
                      class="surface-card p-3 text-left hover:border-cyan-300/50 transition cursor-pointer flex items-center justify-between gap-2"
                      onClick={() => setItemId(String(it.id))}
                    >
                      <div>
                        <div class="text-xs font-bold text-ice-100">{it.name}</div>
                        <div class="text-[10px] text-ice-500">
                          ID {it.id} · {it.cat}
                        </div>
                      </div>
                      <span class="tiny-chip muted">{t('choose')}</span>
                    </button>
                  )}
                </For>
              </div>
            </div>
          </Show>

          <Show when={tab() === 'mail'}>
            <div class="flex flex-col gap-4 max-w-2xl">
              <div>
                <h2 class="section-title">{t('mailTitle')}</h2>
                <p class="section-subtitle">{t('mailHint')}</p>
              </div>
              <div class="surface-card p-5 flex flex-col gap-3">
                <div class="grid grid-cols-2 gap-3">
                  <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                    <span>{t('mailSubject')}</span>
                    <input class="input-field" value={mailTitle()} onInput={(e) => setMailTitle(e.currentTarget.value)} />
                  </label>
                  <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                    <span>{t('mailSender')}</span>
                    <input
                      class="input-field"
                      value={mailSender()}
                      onInput={(e) => setMailSender(e.currentTarget.value)}
                    />
                  </label>
                </div>
                <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                  <span>{t('mailBody')}</span>
                  <textarea
                    class="input-field py-2 min-h-24"
                    value={mailContent()}
                    onInput={(e) => setMailContent(e.currentTarget.value)}
                  />
                </label>
                <div class="grid grid-cols-2 gap-3">
                  <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                    <span>{t('mailAttachId')}</span>
                    <input
                      class="input-field"
                      value={mailAttachId()}
                      onInput={(e) => setMailAttachId(e.currentTarget.value)}
                    />
                  </label>
                  <label class="flex flex-col gap-1 text-[11px] text-ice-300">
                    <span>{t('itemCount')}</span>
                    <input
                      class="input-field"
                      value={mailAttachNum()}
                      onInput={(e) => setMailAttachNum(e.currentTarget.value)}
                    />
                  </label>
                </div>
                <button
                  class="primary-button"
                  disabled={busy()}
                  onClick={() => {
                    const id = Number(mailAttachId());
                    const count = Number(mailAttachNum());
                    const attachments = id > 0 && count > 0 ? [{ itemId: id, count }] : [];
                    void runGm(
                      () =>
                        gm('POST', '/api/gm/mail', {
                          uid: Number(uid()),
                          title: mailTitle(),
                          sender: mailSender(),
                          content: mailContent(),
                          attachments,
                        }),
                      t('mailSentOk'),
                    );
                  }}
                >
                  <i class="fa-solid fa-paper-plane" /> {t('mailSend')}
                </button>
              </div>
            </div>
          </Show>
        </div>
      </main>

      <Show when={toast()}>
        <div class={`toast ${toast()?.error ? 'error' : ''}`}>
          <i class={`fa-solid ${toast()?.error ? 'fa-circle-exclamation' : 'fa-circle-check'}`} />
          <span>{toast()?.msg}</span>
        </div>
      </Show>
    </div>
  );
}
