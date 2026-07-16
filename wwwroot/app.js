const videos = [...document.querySelectorAll('.output-video')];
const idleMessage = document.querySelector('#idle-message');
const statusMessage = document.querySelector('#status-message');
const indicator = document.querySelector('#capture-indicator');
const trackName = document.querySelector('#track-name');
const confidence = document.querySelector('#confidence');
const position = document.querySelector('#position');
const referenceBpm = document.querySelector('#reference-bpm');
const inputBpm = document.querySelector('#input-bpm');
const tempoRatio = document.querySelector('#tempo-ratio');
const playbackRate = document.querySelector('#playback-rate');
const fingerprintMethod = document.querySelector('#fingerprint-method');
const trackList = document.querySelector('#track-list');
const deviceSelect = document.querySelector('#device-select');
const levelMeter = document.querySelector('#level-meter');
const levelFill = document.querySelector('#level-fill');
const levelValue = document.querySelector('#level-value');
const transitionMode = document.querySelector('#transition-mode');
let currentSourceKey = null;
let activeVideoIndex = 0;
let resyncToleranceSeconds = 2;
let playbackRateTolerance = 0.015;
let updateInProgress = false;
let lastAppliedMatchRevision = -1;
let registeredTracks = [];
let testTrackIndex = 0;
let trackRefreshInProgress = false;
let transitionConfig = {
  enabled: true,
  durationMilliseconds: 1200,
  blendModesEnabled: true,
  randomizeBlendMode: true,
  blendModes: ['normal'],
};

// 秒数をモニター向けの分:秒表記へ変換します。
function formatTime(seconds) {
  const minutes = Math.floor(seconds / 60);
  const rest = (seconds % 60).toFixed(1).padStart(4, '0');
  return `${minutes}:${rest}`;
}

// APIを呼び、失敗時にはサーバーから返された理由を例外にします。
async function request(path, options = {}) {
  const response = await fetch(path, options);
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.detail || `HTTP ${response.status}`);
  }
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

// JSON本文を付けたPOST要求をAPIへ送信します。
async function postJson(path, data = {}) {
  return request(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
}

// 動画メタデータが利用可能になるまで待ち、安全にシークできる状態にします。
async function waitForMetadata(video) {
  if (video.readyState >= 1) return;
  await new Promise((resolve, reject) => {
    let timeoutId;
    const cleanup = () => {
      window.clearTimeout(timeoutId);
      video.removeEventListener('loadedmetadata', onLoaded);
      video.removeEventListener('error', onError);
    };
    const onLoaded = () => { cleanup(); resolve(); };
    const onError = () => { cleanup(); reject(new Error('動画を読み込めませんでした。')); };
    timeoutId = window.setTimeout(() => {
      cleanup();
      reject(new Error('動画メタデータの読み込みがタイムアウトしました。'));
    }, 10000);
    video.addEventListener('loadedmetadata', onLoaded);
    video.addEventListener('error', onError);
  });
}

// 現在画面に表示している動画レイヤーを返します。
function getActiveVideo() {
  return videos[activeVideoIndex];
}

// 設定候補から今回の切り替えに使うブレンドモードを選びます。
function chooseBlendMode() {
  if (!transitionConfig.blendModesEnabled || transitionConfig.blendModes.length === 0) {
    return 'normal';
  }
  if (!transitionConfig.randomizeBlendMode) {
    return transitionConfig.blendModes[0];
  }
  return transitionConfig.blendModes[Math.floor(Math.random() * transitionConfig.blendModes.length)];
}

// CSSトランジションを確実に開始させるため、描画フレームを2回待ちます。
async function waitForAnimationFrame() {
  await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
}

// opacityの切り替え完了を待ち、イベント欠落時は時間経過で復帰します。
async function waitForOpacityTransition(video, durationMilliseconds) {
  if (durationMilliseconds <= 0) return;
  await new Promise((resolve) => {
    let completed = false;
    const finish = () => {
      if (completed) return;
      completed = true;
      video.removeEventListener('transitionend', onTransitionEnd);
      resolve();
    };
    const onTransitionEnd = (event) => {
      if (event.propertyName === 'opacity') finish();
    };
    video.addEventListener('transitionend', onTransitionEnd);
    window.setTimeout(finish, durationMilliseconds + 150);
  });
}

// 2枚の動画を重ね、設定された時間とブレンドモードでクロスフェードします。
async function playSource(sourceKey, sourceUrl, seekSeconds = 0, rate = 1) {
  const activeVideo = getActiveVideo();
  if (currentSourceKey === sourceKey) {
    activeVideo.loop = true;
    activeVideo.muted = true;
    activeVideo.controls = false;
    activeVideo.playbackRate = rate;
    await activeVideo.play();
    return;
  }

  const initialPlayback = currentSourceKey === null;
  const incomingIndex = initialPlayback ? activeVideoIndex : 1 - activeVideoIndex;
  const incomingVideo = videos[incomingIndex];
  const outgoingVideo = initialPlayback ? null : activeVideo;
  const duration = transitionConfig.enabled && !initialPlayback
    ? Math.max(0, transitionConfig.durationMilliseconds)
    : 0;
  const blendMode = duration > 0 ? chooseBlendMode() : 'normal';

  incomingVideo.style.transition = 'none';
  incomingVideo.style.opacity = '0';
  incomingVideo.style.zIndex = '2';
  incomingVideo.style.mixBlendMode = blendMode;
  incomingVideo.loop = true;
  incomingVideo.muted = true;
  incomingVideo.controls = false;
  incomingVideo.playbackRate = rate;
  incomingVideo.src = sourceUrl;
  await waitForMetadata(incomingVideo);
  // src設定で再生速度が既定値へ戻るブラウザがあるため、読込後に倍率を確定します。
  incomingVideo.playbackRate = rate;
  incomingVideo.currentTime = Math.min(Math.max(0, seekSeconds), Math.max(0, incomingVideo.duration - 0.05));
  await incomingVideo.play();

  if (outgoingVideo) {
    outgoingVideo.style.zIndex = '1';
    outgoingVideo.style.transition = duration > 0 ? `opacity ${duration}ms linear` : 'none';
    incomingVideo.style.transition = duration > 0 ? `opacity ${duration}ms linear` : 'none';
    await waitForAnimationFrame();
    outgoingVideo.style.opacity = '0';
  }
  incomingVideo.style.opacity = '1';
  transitionMode.textContent = duration > 0
    ? `${blendMode} / ${(duration / 1000).toFixed(1)}秒`
    : '即時切替';
  await waitForOpacityTransition(incomingVideo, duration);

  if (outgoingVideo) {
    outgoingVideo.pause();
    outgoingVideo.removeAttribute('src');
    outgoingVideo.load();
    outgoingVideo.style.transition = 'none';
    outgoingVideo.style.mixBlendMode = 'normal';
    outgoingVideo.style.zIndex = '0';
  }
  incomingVideo.style.transition = 'none';
  incomingVideo.style.mixBlendMode = 'normal';
  incomingVideo.style.zIndex = '1';
  activeVideoIndex = incomingIndex;
  currentSourceKey = sourceKey;
  idleMessage.hidden = true;
}

// 未検出時の汎用動画を先頭から連続ループ再生します。
async function playFallback() {
  await playSource('fallback', '/api/material/common', 0, 1);
}

// 音声トラックから指紋を生成済みの登録動画を一覧へ描画します。
async function loadTracks() {
  const tracks = await request('/api/tracks');
  registeredTracks = tracks;
  trackList.replaceChildren(...tracks.map((track) => {
    const item = document.createElement('article');
    const name = document.createElement('strong');
    const duration = document.createElement('span');
    name.textContent = track.videoFile;
    duration.textContent = `${formatTime(track.durationSeconds)} / ${track.bpm === null ? 'BPM未解析' : `BPM ${track.bpm.toFixed(1)}`}`;
    item.append(name, duration);
    return item;
  }));
}

// 解析アプリによるDB更新を、現在の再生を止めずに一覧へ反映します。
async function refreshTracks() {
  if (trackRefreshInProgress) return;
  trackRefreshInProgress = true;
  try {
    await loadTracks();
  } catch (error) {
    console.warn(`登録動画一覧を更新できませんでした: ${error.message}`);
  } finally {
    trackRefreshInProgress = false;
  }
}

// Windowsの録音デバイス一覧を選択欄へ読み込みます。
async function loadDevices(selectedDevice) {
  const devices = await request('/api/audio/devices');
  deviceSelect.replaceChildren(...devices.map((device) => {
    const option = document.createElement('option');
    option.value = device;
    option.textContent = device;
    option.selected = device === selectedDevice;
    return option;
  }));
}

// 最新の検出状態を反映し、動画の曲と位置を必要な場合だけ同期します。
async function updateStatus() {
  if (updateInProgress) return;
  updateInProgress = true;
  try {
    const state = await request('/api/status');
    statusMessage.textContent = state.message;
    indicator.classList.toggle('active', state.captureRunning);
    trackName.textContent = state.trackName || '—';
    confidence.textContent = `${(state.confidence * 100).toFixed(1)}%`;
    position.textContent = formatTime(state.positionSeconds);
    referenceBpm.textContent = state.referenceBpm === null ? '—' : state.referenceBpm.toFixed(1);
    inputBpm.textContent = state.inputBpm === null ? '—' : state.inputBpm.toFixed(1);
    tempoRatio.textContent = state.tempoRatio.toFixed(2);
    fingerprintMethod.textContent = `${state.fingerprintMethod} v${state.fingerprintVersion}`;
    const levelPercent = Math.round(state.inputLevel * 100);
    levelFill.style.width = `${levelPercent}%`;
    levelMeter.setAttribute('aria-valuenow', String(levelPercent));
    levelValue.textContent = `${state.inputDecibels.toFixed(1)} dB`;
    if (deviceSelect.value !== state.selectedInputDevice
        && [...deviceSelect.options].some((option) => option.value === state.selectedInputDevice)) {
      deviceSelect.value = state.selectedInputDevice;
    }

    if (state.trackId !== null) {
      const sourceKey = `track-${state.trackId}`;
      if (currentSourceKey !== sourceKey) {
        await playSource(sourceKey, `/api/media/${state.trackId}`, state.positionSeconds, state.tempoRatio);
        lastAppliedMatchRevision = state.matchRevision;
      } else if (lastAppliedMatchRevision !== state.matchRevision) {
        const activeVideo = getActiveVideo();
        if (Math.abs(activeVideo.currentTime - state.positionSeconds) > resyncToleranceSeconds) {
          activeVideo.currentTime = state.positionSeconds;
        }
        if (Math.abs(activeVideo.playbackRate - state.tempoRatio) > playbackRateTolerance) {
          activeVideo.playbackRate = state.tempoRatio;
        }
        lastAppliedMatchRevision = state.matchRevision;
      }
      const currentVideo = getActiveVideo();
      playbackRate.textContent = currentVideo.playbackRate.toFixed(2);
      if (currentVideo.paused) {
        await currentVideo.play();
      }
    } else {
      await playFallback();
      playbackRate.textContent = '1.00';
    }
  } catch (error) {
    statusMessage.textContent = `通信エラー: ${error.message}`;
    idleMessage.hidden = false;
  } finally {
    updateInProgress = false;
  }
}

// 操作ボタンにAPI処理を割り当てます。
function bindActions() {
  document.querySelector('#start-button').addEventListener('click', async () => {
    try { await postJson('/api/capture/start', { deviceName: deviceSelect.value }); } catch (error) { alert(error.message); }
    await updateStatus();
  });
  document.querySelector('#stop-button').addEventListener('click', async () => {
    await request('/api/capture/stop', { method: 'POST' });
    await updateStatus();
  });
  document.querySelector('#fullscreen-button').addEventListener('click', () => {
    document.querySelector('.stage').requestFullscreen();
  });
  deviceSelect.addEventListener('change', async () => {
    try { await postJson('/api/capture/device', { deviceName: deviceSelect.value }); }
    catch (error) { alert(error.message); }
    await updateStatus();
  });
  document.querySelector('#test-next-button').addEventListener('click', async () => {
    if (registeredTracks.length === 0) return;
    const track = registeredTracks[testTrackIndex % registeredTracks.length];
    testTrackIndex++;
    await postJson(`/api/test/match/${track.id}`);
    await updateStatus();
  });
  document.querySelector('#test-fallback-button').addEventListener('click', async () => {
    await postJson('/api/test/fallback');
    await updateStatus();
  });
}

// 初期一覧を読み込み、状態ポーリングを開始します。
async function initialize() {
  bindActions();
  const [state, clientConfig] = await Promise.all([
    request('/api/status'),
    request('/api/client-config'),
  ]);
  resyncToleranceSeconds = clientConfig.resyncToleranceSeconds;
  playbackRateTolerance = clientConfig.playbackRateTolerance;
  transitionConfig = clientConfig.transition;
  document.querySelector('#test-controls').hidden = !clientConfig.testApiEnabled;
  await loadDevices(state.selectedInputDevice);
  await loadTracks();
  await playFallback();
  await updateStatus();
  window.setInterval(updateStatus, 1000);
  window.setInterval(refreshTracks, 5000);
}

initialize();
