const description = document.querySelector('#welcome-description');
const pinForm = document.querySelector('#pin-form');
const pinInput = document.querySelector('#pin-input');
const pinError = document.querySelector('#pin-error');
const authenticatedMessage = document.querySelector('#authenticated-message');
const welcomeActions = document.querySelector('#welcome-actions');

// 認証APIを呼び、失敗理由を利用者向けの例外へ変換します。
async function request(path, options = {}) {
  const response = await fetch(path, options);
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.message || `HTTP ${response.status}`);
  }
  return response.json();
}

// PIN入力を隠し、認証済みの場合だけ完了メッセージとページリンクを表示します。
function showPageLinks(authenticated) {
  pinForm.hidden = true;
  authenticatedMessage.hidden = !authenticated;
  welcomeActions.hidden = false;
  description.textContent = '映像出力と操作画面を別々のタブで開きます。';
}

// 直接アクセスから戻された場合だけ、認証後に目的ページへ移動します。
function getSafeReturnUrl() {
  const value = new URLSearchParams(window.location.search).get('returnUrl');
  return ['/output', '/output.html', '/setting', '/setting.html'].includes(value) ? value : null;
}

// 4桁PINを送信し、成功時はセッションCookieを使って保護ページへ進みます。
async function authenticate(event) {
  event.preventDefault();
  pinError.hidden = true;
  try {
    await request('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: pinInput.value }),
    });
    const returnUrl = getSafeReturnUrl();
    if (returnUrl) {
      window.location.assign(returnUrl);
      return;
    }
    showPageLinks(true);
  } catch (error) {
    pinError.textContent = error.message;
    pinError.hidden = false;
    pinInput.select();
  }
}

// LAN公開モードと現在の認証状態に応じて初期画面を切り替えます。
async function initialize() {
  pinForm.addEventListener('submit', authenticate);
  const status = await request('/api/auth/status');
  if (!status.required) {
    showPageLinks(false);
    return;
  }
  if (status.authenticated) {
    showPageLinks(true);
    return;
  }
  description.textContent = 'LAN内からアクセスするには4桁のPINを入力してください。';
  authenticatedMessage.hidden = true;
  pinForm.hidden = false;
  pinInput.focus();
}

initialize().catch((error) => {
  description.textContent = `接続状態を確認できません: ${error.message}`;
});
