import { Show, createSignal, onCleanup, onMount } from 'solid-js';
import { invoke } from '@tauri-apps/api/core';
import { translations, type Language } from '../i18n';

export type ServerStatus = {
  appVersion: string;
  settings: { gameDir: string; workspaceDir: string };
  serverRunning: boolean;
  hostsActive: boolean;
  certReady: boolean;
  gameFound: boolean;
  busyPorts: number[];
  serverMode: string;
  logPath: string;
};

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
  let timer: number | undefined;

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
    void refresh();
    timer = window.setInterval(() => void refresh(), 2500);
  });
  onCleanup(() => {
    if (timer) window.clearInterval(timer);
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
                <button class="primary-button" disabled={working()} onClick={start}>
                  <i class="fa-solid fa-play" /> {t('startAll')}
                </button>
              }
            >
              <button class="danger-button" disabled={working()} onClick={stop}>
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
