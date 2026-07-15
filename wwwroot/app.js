const video = document.querySelector('#output-video');
const idleMessage = document.querySelector('#idle-message');
const statusMessage = document.querySelector('#status-message');
const indicator = document.querySelector('#capture-indicator');
const trackName = document.querySelector('#track-name');
const confidence = document.querySelector('#confidence');
const position = document.querySelector('#position');
const trackList = document.querySelector('#track-list');
let currentTrackId = null;

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

// 最新の検出状態を反映し、動画の曲と位置を必要な場合だけ同期します。
async function updateStatus() {
  try {
    const state = await request('/api/status');
    statusMessage.textContent = state.message;
    indicator.classList.toggle('active', state.captureRunning);
    trackName.textContent = state.trackName || '—';
    confidence.textContent = `${(state.confidence * 100).toFixed(1)}%`;
    position.textContent = formatTime(state.positionSeconds);

    if (state.trackId !== null) {
      idleMessage.hidden = true;
      if (currentTrackId !== state.trackId) {
        currentTrackId = state.trackId;
        video.src = `/api/media/${state.trackId}`;
        video.currentTime = state.positionSeconds;
        await video.play();
      } else if (Math.abs(video.currentTime - state.positionSeconds) > 2.0) {
        video.currentTime = state.positionSeconds;
      }
    }
  } catch (error) {
    statusMessage.textContent = `通信エラー: ${error.message}`;
  }
}

// 操作ボタンにAPI処理を割り当てます。
function bindActions() {
  document.querySelector('#start-button').addEventListener('click', async () => {
    try { await request('/api/capture/start', { method: 'POST' }); } catch (error) { alert(error.message); }
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
}

// 初期一覧を読み込み、状態ポーリングを開始します。
async function initialize() {
  bindActions();
  await loadTracks();
  await updateStatus();
  window.setInterval(updateStatus, 1000);
}

initialize();
