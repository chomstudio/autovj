const videos = [...document.querySelectorAll('.output-video')];
const glitchVideo = document.querySelector('#glitch-video');
const idleMessage = document.querySelector('#idle-message');
let currentSourceKey = null;
let activeVideoIndex = 0;
let updateInProgress = false;
let lastAppliedMatchRevision = -1;
let lastTransitionRevision = -1;
let activeGlitchIndex = null;
let clientConfig = {
  resyncToleranceSeconds: 2,
  playbackRateTolerance: 0.015,
  randomizeCommonStart: true,
  minimumPlaybackRate: 0.8,
  maximumPlaybackRate: 1.2,
  transition: { enabled: true, durationMilliseconds: 1200 },
  glitch: { enabled: true, files: [] },
};

// APIを呼び、失敗時にはサーバーから返された理由を例外にします。
async function request(path) {
  const response = await fetch(path);
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.detail || `HTTP ${response.status}`);
  }
  return response.json();
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

// 黒背景との合成で暗転しやすいモードを、保護付き二段階切り替えの対象にします。
function requiresProtectedBlend(mode) {
  return !['normal', 'screen', 'difference'].includes(mode);
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

// サーバーが選んだ共通ブレンドモードで2枚の動画を切り替えます。
async function playSource(sourceKey, sourceUrl, seekSeconds, rate, startFraction, blendMode) {
  const activeVideo = getActiveVideo();
  if (currentSourceKey === sourceKey) {
    activeVideo.playbackRate = Math.min(clientConfig.maximumPlaybackRate, Math.max(clientConfig.minimumPlaybackRate, rate));
    if (activeVideo.paused) await activeVideo.play();
    return;
  }

  const initialPlayback = currentSourceKey === null;
  const incomingIndex = initialPlayback ? activeVideoIndex : 1 - activeVideoIndex;
  const incomingVideo = videos[incomingIndex];
  const outgoingVideo = initialPlayback ? null : activeVideo;
  const duration = clientConfig.transition.enabled && !initialPlayback
    ? Math.max(0, clientConfig.transition.durationMilliseconds) : 0;
  const mode = duration > 0 ? blendMode : 'normal';

  incomingVideo.style.transition = 'none';
  incomingVideo.style.opacity = '0';
  incomingVideo.style.zIndex = '2';
  incomingVideo.style.mixBlendMode = mode;
  incomingVideo.loop = true;
  incomingVideo.muted = true;
  incomingVideo.src = sourceUrl;
  await waitForMetadata(incomingVideo);
  incomingVideo.playbackRate = Math.min(clientConfig.maximumPlaybackRate, Math.max(clientConfig.minimumPlaybackRate, rate));
  const requestedPosition = startFraction === null
    ? seekSeconds
    : startFraction * Math.max(0, incomingVideo.duration - 0.05);
  incomingVideo.currentTime = Math.min(Math.max(0, requestedPosition), Math.max(0, incomingVideo.duration - 0.05));
  await incomingVideo.play();

  if (outgoingVideo && duration > 0 && requiresProtectedBlend(mode)) {
    const blendInDuration = Math.round(duration * 0.25);
    const blendOutDuration = Math.round(duration * 0.15);
    const crossfadeDuration = Math.max(0, duration - blendInDuration - blendOutDuration);
    outgoingVideo.style.zIndex = '1';
    outgoingVideo.style.opacity = '1';
    incomingVideo.style.transition = `opacity ${blendInDuration}ms linear`;
    await waitForAnimationFrame();
    incomingVideo.style.opacity = '1';
    await waitForOpacityTransition(incomingVideo, blendInDuration);
    incomingVideo.style.transition = `opacity ${blendOutDuration}ms linear`;
    incomingVideo.style.opacity = '0';
    await waitForOpacityTransition(incomingVideo, blendOutDuration);
    incomingVideo.style.mixBlendMode = 'normal';
    outgoingVideo.style.transition = `opacity ${crossfadeDuration}ms linear`;
    incomingVideo.style.transition = `opacity ${crossfadeDuration}ms linear`;
    await waitForAnimationFrame();
    outgoingVideo.style.opacity = '0';
    incomingVideo.style.opacity = '1';
    await waitForOpacityTransition(incomingVideo, crossfadeDuration);
  } else {
    if (outgoingVideo) {
      outgoingVideo.style.zIndex = '1';
      outgoingVideo.style.transition = duration > 0 ? `opacity ${duration}ms linear` : 'none';
      incomingVideo.style.transition = duration > 0 ? `opacity ${duration}ms linear` : 'none';
      await waitForAnimationFrame();
      outgoingVideo.style.opacity = '0';
    }
    incomingVideo.style.opacity = '1';
    await waitForOpacityTransition(incomingVideo, duration);
  }

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

// サーバーが選んだ素材番号に従い、全タブで同じグリッチ動画を重ねます。
function updateGlitchEffect(state) {
  const index = clientConfig.glitch.enabled ? state.glitchFileIndex : null;
  if (index === activeGlitchIndex) return;
  activeGlitchIndex = index;
  if (index === null || clientConfig.glitch.files[index] === undefined) {
    glitchVideo.classList.remove('active');
    glitchVideo.pause();
    return;
  }
  glitchVideo.src = clientConfig.glitch.files[index];
  glitchVideo.currentTime = 0;
  glitchVideo.classList.add('active');
  glitchVideo.play().catch(() => {
    glitchVideo.classList.remove('active');
    activeGlitchIndex = null;
  });
}

// 最新状態と公開設定を反映し、動画・位置・速度・演出を同期します。
async function updateStatus() {
  if (updateInProgress) return;
  updateInProgress = true;
  try {
    const [state, newClientConfig] = await Promise.all([
      request('/api/status'),
      request('/api/client-config'),
    ]);
    clientConfig = newClientConfig;
    updateGlitchEffect(state);
    const transitionMode = state.transitionBlendMode || 'normal';
    if (state.trackId !== null) {
      const sourceKey = `track-${state.trackId}`;
      if (currentSourceKey !== sourceKey) {
        await playSource(sourceKey, `/api/media/${state.trackId}`, state.positionSeconds, state.tempoRatio, null, transitionMode);
        lastAppliedMatchRevision = state.matchRevision;
        lastTransitionRevision = state.transitionRevision;
      } else if (lastAppliedMatchRevision !== state.matchRevision) {
        const activeVideo = getActiveVideo();
        if (Math.abs(activeVideo.currentTime - state.positionSeconds) > clientConfig.resyncToleranceSeconds) {
          activeVideo.currentTime = state.positionSeconds;
        }
        if (Math.abs(activeVideo.playbackRate - state.tempoRatio) > clientConfig.playbackRateTolerance) {
          activeVideo.playbackRate = state.tempoRatio;
        }
        lastAppliedMatchRevision = state.matchRevision;
      }
      const desiredRate = Math.min(clientConfig.maximumPlaybackRate, Math.max(clientConfig.minimumPlaybackRate, state.tempoRatio));
      if (Math.abs(getActiveVideo().playbackRate - desiredRate) > clientConfig.playbackRateTolerance) {
        getActiveVideo().playbackRate = desiredRate;
      }
    } else {
      const startFraction = clientConfig.randomizeCommonStart ? state.commonStartFraction : 0;
      await playSource('fallback', '/api/material/common', 0, 1, startFraction, transitionMode);
      lastTransitionRevision = state.transitionRevision;
    }
  } catch (error) {
    console.error(error);
    idleMessage.textContent = `映像を更新できません: ${error.message}`;
    idleMessage.hidden = false;
  } finally {
    updateInProgress = false;
  }
}

// 全画面操作と定期同期を開始します。
async function initialize() {
  document.querySelector('#fullscreen-button').addEventListener('click', () => {
    document.querySelector('#stage').requestFullscreen();
  });
  await updateStatus();
  window.setInterval(updateStatus, 1000);
}

initialize();
