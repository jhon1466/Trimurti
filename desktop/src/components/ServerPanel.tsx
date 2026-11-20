import { Show, createSignal, onCleanup, onMount } from 'solid-js';
import { invoke } from '@tauri-apps/api/core';
import { translations, type Language } from '../i18n';

export type ServerStatus = {
  appVersion: string;
  settings: { gameDir: string; workspaceDir: string; autoUpdate: boolean };
  serverRunning: boolean;
  hostsActive: boolean;
  certReady: boolean;
  gameFound: boolean;
  busyPorts: number[];
  serverMode: string;
  logPath: string;
  serverDir: string;
  installed: boolean;
  built: boolean;
  sourceCommit: string;
  buildCommit: string;
  dotnetReady: boolean;
  stage: string;
};

type UpdateInfo = {
  installed: boolean;
  localCommit: string;
  buildCommit: string;
  remoteCommit: string;
  remoteMessage: string;
  updateAvailable: boolean;
  online: boolean;
};

const UPDATE_EVERY_MS = 10 * 60 * 1000;

export default function ServerPanel(props: {
  language: Language;
  onStarted: () => void;
  onStatusChange?: (s: ServerStatus) => void;
  notify: (msg: string, error?: boolean) => void;
}) {
  const t = (k: keyof typeof translations.es) => translations[props.language][k];
  const [status, setStatus] = createSignal<ServerStatus>();
  const [working, setWorking] = createSignal(false);
  const [log, setLog] = createSignal('');
  const [gameDir, setGameDir] = createSignal('');
  const [workspaceDir, setWorkspaceDir] = createSignal('');
  const [update, setUpdate] = createSignal<UpdateInfo>();
  const [updating, setUpdating] = createSignal(false);
  const [checkedAt, setCheckedAt] = createSignal('');
  let timer: number | undefined;
  let updateTimer: number | undefined;

  const short = (sha?: string) => (sha ? sha.slice(0, 7) : '—');
  const busy = () => working() || updating() || !!status()?.stage;

  async function checkUpdate(silent = false) {
    try {
      const info = await invoke<UpdateInfo>('updater_check');
      setUpdate(info);
      setCheckedAt(new Date().toLocaleTimeString());
      // Auto-update only an installed server; the first download happens on "Start all".
      if (info.online && info.installed && info.updateAvailable && status()?.settings.autoUpdate && !busy()) await applyUpdate();
    } catch (e) {
      if (!silent) props.notify(String(e), true);
    }
  }

  async function applyUpdate() {
    setUpdating(true);
    try {
      props.notify(await invoke<string>('updater_apply'));
    } catch (e) {
      props.notify(String(e), true);
    } finally {
      setUpdating(false);
      await refresh();
      await checkUpdate(true);
    }
  }

  async function setAutoUpdate(enabled: boolean) {
    await invoke('manager_set_auto_update', { enabled });
    await refresh();
    if (enabled) await checkUpdate(true);
  }

  async function refresh() {
    try {
      const s = await invoke<ServerStatus>('manager_status');
      setStatus(s);
      props.onStatusChange?.(s);
      if (!gameDir() && s.settings.gameDir) setGameDir(s.settings.gameDir);
      if (!workspaceDir() && s.settings.workspaceDir) setWorkspaceDir(s.settings.workspaceDir);
      setLog(await invoke<string>('manager_log_tail', { lines: 140 }));
    } catch (e) {
      props.notify(String(e), true);
    }
  }

  async function runAction(fn: () => Promise<unknown>, okMsg?: string) {
    setWorking(true);
    try {
      const res = await fn();
      if (typeof res === 'string') props.notify(res);
      else if (okMsg) props.notify(okMsg);
    } catch (e) {
      props.notify(String(e), true);
    } finally {
      setWorking(false);
      void refresh();
    }
  }

  const start = () =>
    runAction(async () => {
      const msg = await invoke<string>('manager_start');
      window.setTimeout(props.onStarted, 3000);
      void checkUpdate(true);
      return msg;
    });

  const stop = () => runAction(() => invoke<string>('manager_stop'));
  const launchGame = () => runAction(() => invoke<string>('manager_launch_game'));
  const savePaths = () =>
    runAction(
      () => invoke('manager_save_settings', { gameDir: gameDir(), workspaceDir: workspaceDir() }),
      t('pathsSaved'),
    );

  function copyDiag() {
    const s = status();
    const lines = [
      `Trimūrti GM Tools ${s?.appVersion ?? '-'}`,
      `running=${s?.serverRunning} hosts=${s?.hostsActive} cert=${s?.certReady} mode=${s?.serverMode}`,
      `busyPorts=${s?.busyPorts.join(',') || '-'}`,
      `workspace=${s?.settings.workspaceDir ?? '-'}`,
      `gameDir=${s?.settings.gameDir ?? '-'}`,
      '',
      log().split('\n').slice(-50).join('\n'),
    ];
    void navigator.clipboard.writeText(lines.join('\n')).then(() => props.notify(t('diagCopied')));
  }

  onMount(() => {
    void refresh().then(() => checkUpdate(true));
    timer = window.setInterval(() => void refresh(), 2500);
    updateTimer = window.setInterval(() => void checkUpdate(true), UPDATE_EVERY_MS);
  });
  onCleanup(() => {
    if (timer) window.clearInterval(timer);
    if (updateTimer) window.clearInterval(updateTimer);
  });

  return (
    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 class="section-title">{t('srvTitle')}</h2>
          <p class="section-subtitle">{t('srvHint')}</p>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <span class={`tiny-chip ${status()?.serverRunning ? '' : 'muted'}`}>
            <i class="fa-solid fa-server" /> {status()?.serverRunning ? t('srvRunning') : t('srvStopped')}
          </span>
          <span class={`tiny-chip ${status()?.hostsActive ? 'gold' : 'muted'}`}>
            <i class="fa-solid fa-network-wired" /> {status()?.hostsActive ? t('hostsActive') : t('hostsClean')}
          </span>
          <span class={`tiny-chip ${status()?.certReady ? '' : 'muted'}`}>
            <i class="fa-solid fa-lock" /> {status()?.certReady ? t('tlsReady') : t('tlsMissing')}
          </span>
        </div>
      </div>

      <div class="surface-card p-5 flex flex-col gap-4">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <div class="flex flex-wrap items-center gap-2">
            <Show
              when={status()?.serverRunning}
              fallback={
                <button class="primary-button" disabled={busy()} onClick={start}>
                  <i class="fa-solid fa-play" /> {t('startAll')}
                </button>
              }
            >
              <button class="danger-button" disabled={busy()} onClick={stop}>
                <i class="fa-solid fa-stop" /> {t('stopAll')}
              </button>
            </Show>
            <button class="gold-button" disabled={working() || !status()?.gameFound} onClick={launchGame}>
              <i class="fa-solid fa-gamepad" /> {t('launchGame')}
            </button>
            <button class="secondary-button" disabled={working()} onClick={() => void refresh()}>
              <i class="fa-solid fa-rotate" /> {t('refresh')}
            </button>
          </div>
          <span class="text-[11px] text-ice-500">
            {t('mode')}: {status()?.serverMode ?? '...'}
          </span>
        </div>

        <Show when={(status()?.busyPorts.length ?? 0) > 0 && !status()?.serverRunning}>
          <div class="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-xs text-rose-200 flex items-center gap-2">
            <i class="fa-solid fa-triangle-exclamation" />
            <span>
              {t('busyPorts')} {status()?.busyPorts.join(', ')}
            </span>
          </div>
        </Show>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-3 pt-1">
          <label class="flex flex-col gap-1.5 text-[11px] text-ice-300">
            <span>{t('gameDirLabel')}</span>
            <input
              class="input-field"
              value={gameDir()}
              onInput={(e) => setGameDir(e.currentTarget.value)}
              placeholder="C:\Program Files\AetherGazerLauncher\AetherGazer"
            />
          </label>
          <label class="flex flex-col gap-1.5 text-[11px] text-ice-300">
            <span>{t('serverDirLabel')}</span>
            <input
              class="input-field"
              value={workspaceDir()}
              onInput={(e) => setWorkspaceDir(e.currentTarget.value)}
              placeholder="%APPDATA%\TrimurtiGMTools\server"
            />
          </label>
        </div>

        <div class="flex flex-wrap items-center justify-between gap-2">
          <div class="flex flex-wrap gap-2">
            <button class="quiet-button" onClick={() => void invoke('manager_open_folder', { target: 'workspace' })}>
              <i class="fa-solid fa-folder-open" /> {t('openServerDir')}
            </button>
            <button class="quiet-button" onClick={() => void invoke('manager_open_folder', { target: 'logs' })}>
              <i class="fa-solid fa-file-lines" /> {t('openLogs')}
            </button>
            <button class="quiet-button" onClick={copyDiag}>
              <i class="fa-solid fa-copy" /> {t('copyDiag')}
            </button>
          </div>
          <button class="secondary-button" disabled={working()} onClick={savePaths}>
            <i class="fa-solid fa-floppy-disk" /> {t('savePaths')}
          </button>
        </div>
      </div>

      <div class="surface-card p-5 flex flex-col gap-3">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 class="card-title">{t('updTitle')}</h3>
            <p class="card-caption">
              {t('srvDirInUse')}: {status()?.serverDir ?? '...'}
            </p>
          </div>
          <span
            class={`tiny-chip ${update()?.online && update()?.installed && !update()?.updateAvailable ? '' : update()?.online ? 'gold' : 'muted'}`}
          >
            <i
              class={`fa-solid ${
                !update() ? 'fa-circle-question' : !update()!.online ? 'fa-plug-circle-xmark' : update()!.installed && !update()!.updateAvailable ? 'fa-circle-check' : 'fa-circle-arrow-up'
              }`}
            />{' '}
            {!update()
              ? `${t('updCheck')}…`
              : !update()!.online
                ? t('updOffline')
                : !update()!.installed
                  ? t('updNotInstalled')
                  : update()!.updateAvailable
                    ? t('updAvailable')
                    : t('updLatest')}
          </span>
        </div>

        <p class="text-[11px] text-ice-500">{t('updHint')}</p>

        <Show when={status()?.stage}>
          <div class="rounded-lg border border-sky-500/40 bg-sky-950/30 px-3 py-2 text-xs text-sky-200 flex items-center gap-2">
            <i class="fa-solid fa-spinner fa-spin" />
            <span>{status()?.stage}</span>
          </div>
        </Show>

        <div class="grid grid-cols-1 md:grid-cols-3 gap-2 text-xs text-ice-300">
          <div>
            {t('updLocal')}: <span class="font-mono text-ice-100">{short(status()?.sourceCommit)}</span>
          </div>
          <div>
            {t('updBuilt')}: <span class="font-mono text-ice-100">{short(status()?.buildCommit)}</span>
          </div>
          <div>
            {t('updRemote')}: <span class="font-mono text-ice-100">{short(update()?.remoteCommit)}</span>
          </div>
          <div class="md:col-span-3 text-ice-500">
            {update()?.remoteMessage ? `${update()!.remoteMessage} · ` : ''}
            {status()?.dotnetReady ? t('dotnetReady') : t('dotnetMissing')} · {t('updChecked')}: {checkedAt() || '—'}
          </div>
        </div>

        <div class="flex flex-wrap items-center justify-between gap-2">
          <div class="flex flex-wrap gap-2">
            <button class="secondary-button" disabled={busy()} onClick={() => void checkUpdate()}>
              <i class="fa-solid fa-magnifying-glass" /> {t('updCheck')}
            </button>
            <button
              class="primary-button"
              disabled={busy() || !update()?.online || !update()?.updateAvailable}
              onClick={() => void applyUpdate()}
            >
              <i class={`fa-solid ${updating() ? 'fa-spinner fa-spin' : 'fa-download'}`} /> {updating() ? t('updWorking') : t('updApply')}
            </button>
          </div>
          <label class="flex items-center gap-2 text-xs text-ice-300 cursor-pointer">
            <input
              type="checkbox"
              checked={status()?.settings.autoUpdate ?? true}
              onChange={(e) => void setAutoUpdate(e.currentTarget.checked)}
            />
            {t('updAuto')}
          </label>
        </div>
      </div>

      <div class="surface-card p-4 flex flex-col gap-2">
        <div class="flex items-center justify-between">
          <div>
            <h3 class="card-title">{t('liveLog')}</h3>
            <p class="card-caption">{status()?.logPath ?? ''}</p>
          </div>
          <span class="tiny-chip muted">HTTP 80/443/5180 · TCP 8102/8105</span>
        </div>
        <pre class="log-box">{log() || t('waitingLog')}</pre>
      </div>
    </div>
  );
}
