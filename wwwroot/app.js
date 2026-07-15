const video = document.querySelector('#output-video');
const idleMessage = document.querySelector('#idle-message');
const statusMessage = document.querySelector('#status-message');
const indicator = document.querySelector('#capture-indicator');
const trackName = document.querySelector('#track-name');
const confidence = document.querySelector('#confidence');
const position = document.querySelector('#position');
const trackList = document.querySelector('#track-list');
const deviceSelect = document.querySelector('#device-select');
const levelMeter = document.querySelector('#level-meter');
const levelFill = document.querySelector('#level-fill');
const levelValue = document.querySelector('#level-value');
let currentSourceKey = null;
let resyncToleranceSeconds = 2;
let updateInProgress = false;

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
  return response.json();
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
async function waitForMetadata() {
  if (video.readyState >= 1) return;
  await new Promise((resolve, reject) => {
    video.addEventListener('loadedmetadata', resolve, { once: true });
    video.addEventListener('error', () => reject(new Error('動画を読み込めませんでした。')), { once: true });
  });
}

// 再生元を切り替え、ループとミュートを常に強制した状態で再生します。
async function playSource(sourceKey, sourceUrl, seekSeconds = 0) {
  video.loop = true;
  video.muted = true;
  video.controls = false;
  if (currentSourceKey !== sourceKey) {
    currentSourceKey = sourceKey;
    video.src = sourceUrl;
    await waitForMetadata();
    video.currentTime = Math.min(Math.max(0, seekSeconds), Math.max(0, video.duration - 0.05));
  }
  await video.play();
  idleMessage.hidden = true;
}

// 未検出時の汎用動画を先頭から連続ループ再生します。
async function playFallback() {
  await playSource('fallback', '/api/material/common', 0);
}

// 音声トラックから指紋を生成済みの登録動画を一覧へ描画します。
async function loadTracks() {
  const tracks = await request('/api/tracks');
  trackList.replaceChildren(...tracks.map((track) => {
    const item = document.createElement('article');
    const name = document.createElement('strong');
    const duration = document.createElement('span');
    name.textContent = track.videoFile;
    duration.textContent = formatTime(track.durationSeconds);
    item.append(name, duration);
    return item;
  }));
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
        await playSource(sourceKey, `/api/media/${state.trackId}`, state.positionSeconds);
      } else if (Math.abs(video.currentTime - state.positionSeconds) > resyncToleranceSeconds) {
        video.currentTime = state.positionSeconds;
      }
      if (video.paused) {
        await video.play();
      }
    } else {
      await playFallback();
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
  document.querySelector('#rebuild-button').addEventListener('click', async (event) => {
    event.currentTarget.disabled = true;
    statusMessage.textContent = '素材を解析中…';
    try { await request('/api/catalog/rebuild', { method: 'POST' }); await loadTracks(); }
    catch (error) { alert(error.message); }
    finally { event.currentTarget.disabled = false; }
  });
  document.querySelector('#fullscreen-button').addEventListener('click', () => {
    document.querySelector('.stage').requestFullscreen();
  });
  deviceSelect.addEventListener('change', async () => {
    try { await postJson('/api/capture/device', { deviceName: deviceSelect.value }); }
    catch (error) { alert(error.message); }
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
  await loadDevices(state.selectedInputDevice);
  await loadTracks();
  await playFallback();
  await updateStatus();
  window.setInterval(updateStatus, 1000);
}

initialize();
