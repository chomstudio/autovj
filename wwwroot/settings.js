const statusMessage = document.querySelector('#status-message');
const indicator = document.querySelector('#capture-indicator');
const deviceSelect = document.querySelector('#device-select');
const captureToggleButton = document.querySelector('#capture-toggle-button');
const levelMeter = document.querySelector('#level-meter');
const levelFill = document.querySelector('#level-fill');
const levelValue = document.querySelector('#level-value');
const settingsForm = document.querySelector('#settings-form');
const settingsMessage = document.querySelector('#settings-message');
const trackList = document.querySelector('#track-list');
let latestState = null;
let registeredTracks = [];
let testTrackIndex = 0;
let statusUpdateInProgress = false;
let loadedOffsetSourceId = null;

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
    throw new Error(problem.detail || problem.message || `HTTP ${response.status}`);
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

// 録音入力とループバック入力を同じ選択欄へ分類表示します。
async function loadDevices(selectedSourceId) {
  const sources = await request('/api/audio/devices');
  const groups = [
    ['capture', '録音入力'],
    ['loopback', 'WASAPIループバック'],
  ].map(([type, label]) => {
    const group = document.createElement('optgroup');
    group.label = label;
    group.append(...sources.filter((source) => source.type === type).map((source) => {
      const option = document.createElement('option');
      option.value = source.id;
      option.textContent = source.name;
      option.selected = source.id === selectedSourceId;
      return option;
    }));
    return group;
  });
  deviceSelect.replaceChildren(...groups);
}

// 音声トラックから指紋を生成済みの登録動画を一覧へ描画します。
async function loadTracks() {
  registeredTracks = await request('/api/tracks');
  trackList.replaceChildren(...registeredTracks.map((track) => {
    const item = document.createElement('article');
    const name = document.createElement('strong');
    const duration = document.createElement('span');
    name.textContent = track.videoFile;
    duration.textContent = `${formatTime(track.durationSeconds)} / ${track.bpm === null ? 'BPM未解析' : `BPM ${track.bpm.toFixed(1)}`}`;
    item.append(name, duration);
    return item;
  }));
}

// 最新の共有状態を検出モニターへ反映します。
async function updateStatus() {
  if (statusUpdateInProgress) return;
  statusUpdateInProgress = true;
  try {
    const state = await request('/api/status');
    latestState = state;
    statusMessage.textContent = state.message;
    indicator.classList.toggle('active', state.captureRunning);
    captureToggleButton.textContent = state.captureRunning ? '一時停止' : '再開';
    captureToggleButton.classList.toggle('primary', !state.captureRunning);
    document.querySelector('#track-name').textContent = state.trackName || '—';
    document.querySelector('#confidence').textContent = `${(state.confidence * 100).toFixed(1)}%`;
    document.querySelector('#position').textContent = formatTime(state.positionSeconds);
    document.querySelector('#position-offset').textContent = `${state.positionOffsetMilliseconds} ms`;
    document.querySelector('#reference-bpm').textContent = state.referenceBpm === null ? '—' : state.referenceBpm.toFixed(1);
    document.querySelector('#input-bpm').textContent = state.inputBpm === null ? '—' : state.inputBpm.toFixed(1);
    document.querySelector('#tempo-ratio').textContent = state.tempoRatio.toFixed(2);
    document.querySelector('#playback-rate').textContent = state.tempoRatio.toFixed(2);
    document.querySelector('#fingerprint-method').textContent = `${state.fingerprintMethod} v${state.fingerprintVersion}`;
    document.querySelector('#transition-mode').textContent = state.transitionBlendMode;
    const levelPercent = Math.round(state.inputLevel * 100);
    levelFill.style.width = `${levelPercent}%`;
    levelMeter.setAttribute('aria-valuenow', String(levelPercent));
    levelValue.textContent = `${state.inputDecibels.toFixed(1)} dB`;
    if (deviceSelect.value !== state.selectedInputSourceId
        && [...deviceSelect.options].some((option) => option.value === state.selectedInputSourceId)) {
      deviceSelect.value = state.selectedInputSourceId;
    }
    if (loadedOffsetSourceId !== state.selectedInputSourceId) {
      await loadPositionOffset();
    }
  } catch (error) {
    statusMessage.textContent = `通信エラー: ${error.message}`;
  } finally {
    statusUpdateInProgress = false;
  }
}

// 詳細設定値からブレンドモードのチェックボックスを構築します。
function renderBlendModeOptions(settings) {
  const container = document.querySelector('#blend-mode-options');
  container.replaceChildren(...settings.availableBlendModes.map((mode) => {
    const label = document.createElement('label');
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.name = 'blend-mode';
    input.value = mode;
    input.checked = settings.blendModes.includes(mode);
    label.append(input, document.createTextNode(mode));
    return label;
  }));
}

// 保存済み設定をフォームへ読み込みます。
async function loadSettings() {
  const settings = await request('/api/settings');
  loadedOffsetSourceId = settings.selectedInputSourceId;
  document.querySelector('#setting-position-offset').value = settings.positionOffsetMilliseconds;
  document.querySelector('#setting-lost-timeout').value = settings.detectionLostTimeoutSeconds;
  document.querySelector('#setting-resync-tolerance').value = settings.resyncToleranceSeconds;
  document.querySelector('#setting-minimum-rate').value = settings.minimumPlaybackRate;
  document.querySelector('#setting-maximum-rate').value = settings.maximumPlaybackRate;
  document.querySelector('#setting-minimum-bpm').value = settings.minimumBpm;
  document.querySelector('#setting-maximum-bpm').value = settings.maximumBpm;
  document.querySelector('#setting-transition-enabled').checked = settings.transitionEnabled;
  document.querySelector('#setting-transition-duration').value = settings.transitionDurationMilliseconds;
  document.querySelector('#setting-blend-enabled').checked = settings.blendModesEnabled;
  document.querySelector('#setting-randomize-blend').checked = settings.randomizeBlendMode;
  document.querySelector('#setting-glitch-threshold').value = Math.round(settings.glitchConfidenceThreshold * 100);
  renderBlendModeOptions(settings);
}

// 入力元を切り替えた際、そのデバイスに紐づく位置補正だけを再読み込みします。
async function loadPositionOffset() {
  const settings = await request('/api/settings');
  loadedOffsetSourceId = settings.selectedInputSourceId;
  document.querySelector('#setting-position-offset').value = settings.positionOffsetMilliseconds;
}

// 入力された設定を保存し、サーバーで即時利用できる状態にします。
async function saveSettings(event) {
  event.preventDefault();
  settingsMessage.hidden = true;
  const selectedModes = [...document.querySelectorAll('input[name="blend-mode"]:checked')].map((input) => input.value);
  const settings = {
    selectedInputSourceId: deviceSelect.value,
    positionOffsetMilliseconds: Number(document.querySelector('#setting-position-offset').value),
    detectionLostTimeoutSeconds: Number(document.querySelector('#setting-lost-timeout').value),
    resyncToleranceSeconds: Number(document.querySelector('#setting-resync-tolerance').value),
    minimumPlaybackRate: Number(document.querySelector('#setting-minimum-rate').value),
    maximumPlaybackRate: Number(document.querySelector('#setting-maximum-rate').value),
    minimumBpm: Number(document.querySelector('#setting-minimum-bpm').value),
    maximumBpm: Number(document.querySelector('#setting-maximum-bpm').value),
    transitionEnabled: document.querySelector('#setting-transition-enabled').checked,
    transitionDurationMilliseconds: Number(document.querySelector('#setting-transition-duration').value),
    blendModesEnabled: document.querySelector('#setting-blend-enabled').checked,
    randomizeBlendMode: document.querySelector('#setting-randomize-blend').checked,
    blendModes: selectedModes,
    glitchConfidenceThreshold: Number(document.querySelector('#setting-glitch-threshold').value) / 100,
  };
  try {
    const result = await postJson('/api/settings', settings);
    settingsMessage.textContent = result.message;
    settingsMessage.classList.remove('error');
  } catch (error) {
    settingsMessage.textContent = error.message;
    settingsMessage.classList.add('error');
  }
  settingsMessage.hidden = false;
}

// 入力停止・入力元変更・設定保存を各APIへ割り当てます。
function bindActions() {
  captureToggleButton.addEventListener('click', async () => {
    try {
      if (latestState?.captureRunning) {
        await request('/api/capture/stop', { method: 'POST' });
      } else {
        await postJson('/api/capture/start', { sourceId: deviceSelect.value });
      }
    } catch (error) {
      window.alert(error.message);
    }
    await updateStatus();
  });
  deviceSelect.addEventListener('change', async () => {
    try {
      await postJson('/api/capture/device', { sourceId: deviceSelect.value });
      await loadPositionOffset();
    } catch (error) {
      window.alert(error.message);
    }
    await updateStatus();
  });
  settingsForm.addEventListener('submit', saveSettings);
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

// 初期データを並行取得し、設定ページの定期更新を開始します。
async function initialize() {
  bindActions();
  const [state, clientConfig] = await Promise.all([
    request('/api/status'),
    request('/api/client-config'),
  ]);
  latestState = state;
  document.querySelector('#test-controls').hidden = !clientConfig.testApiEnabled;
  await Promise.all([
    loadDevices(state.selectedInputSourceId),
    loadTracks(),
    loadSettings(),
  ]);
  await updateStatus();
  window.setInterval(updateStatus, 1000);
  window.setInterval(() => loadTracks().catch(console.warn), 5000);
}

initialize().catch((error) => {
  statusMessage.textContent = `初期化エラー: ${error.message}`;
});
