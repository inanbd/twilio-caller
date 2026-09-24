'use strict';

/*
 * Twilio Caller portal: a single-file management UI for the backend.
 *
 * It signs in against /api/auth and then talks to the same API the Flutter app
 * uses, so anything the app can manage is manageable here too. Administrators
 * additionally get the /api/admin surface to manage every account.
 */

const TOKEN_KEY = 'twilio_caller_portal_token';

const state = {
  token: null,
  session: null,      // /api/auth/session payload
  selectedNumber: null,
  selectedPeer: null,
};

try { state.token = localStorage.getItem(TOKEN_KEY); } catch { /* private mode */ }

// ---------- tiny helpers ----------

const app = document.getElementById('app');

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function fmtDate(value) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString();
}

function saveToken(token) {
  state.token = token;
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token);
    else localStorage.removeItem(TOKEN_KEY);
  } catch { /* storage unavailable; the session just won't survive a reload */ }
}

function isAdmin() {
  return !!state.session?.roles?.includes('Administrator');
}

// ---------- API ----------

class ApiError extends Error {
  constructor(status, code, message) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function api(path, { method = 'GET', body } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (state.token) headers['Authorization'] = `Bearer ${state.token}`;

  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  let json = null;
  try { json = await response.json(); } catch { /* empty body is fine */ }

  if (!response.ok) {
    if (response.status === 401 && state.token) {
      // Expired or revoked session: back to the login screen.
      signOut();
      throw new ApiError(401, 'unauthorized', 'Your session has ended. Sign in again.');
    }
    throw new ApiError(
      response.status,
      json?.error || `http_${response.status}`,
      json?.detail || json?.error || `Request failed (${response.status}).`);
  }

  return json;
}

function signOut() {
  saveToken(null);
  state.session = null;
  location.hash = '#/login';
  render();
}

// ---------- router ----------

const routes = {
  '#/login': renderAuth,
  '#/dashboard': renderDashboard,
  '#/twilio': renderTwilio,
  '#/numbers': renderNumbers,
  '#/messages': renderMessages,
  '#/calls': renderCalls,
  '#/account': renderAccount,
  '#/admin': renderAdmin,
};

window.addEventListener('hashchange', render);

async function render() {
  if (!state.token) {
    location.hash = '#/login';
    renderAuth();
    return;
  }

  if (!state.session) {
    try {
      state.session = await api('/api/auth/session');
    } catch (error) {
      if (!state.token) { renderAuth(error.message); return; }
      app.innerHTML = `<div class="auth-wrap"><div class="card auth-card">
        <h1>Portal unavailable</h1>
        <p class="lead">${esc(error.message)}</p>
        <button class="primary" onclick="location.reload()">Retry</button>
      </div></div>`;
      return;
    }
  }

  let route = routes[location.hash] ? location.hash : '#/dashboard';
  if (route === '#/login') route = '#/dashboard';
  if (route === '#/admin' && !isAdmin()) route = '#/dashboard';
  routes[route]();
}

// ---------- chrome ----------

function shell(active, content) {
  const admin = isAdmin()
    ? `<a class="nav ${active === 'admin' ? 'active' : ''}" href="#/admin">Administration</a>`
    : '';

  app.innerHTML = `
    <div class="shell">
      <nav class="sidebar">
        <div class="brand">Twilio Caller<small>backend portal</small></div>
        <a class="nav ${active === 'dashboard' ? 'active' : ''}" href="#/dashboard">Dashboard</a>
        <a class="nav ${active === 'twilio' ? 'active' : ''}" href="#/twilio">Twilio account</a>
        <a class="nav ${active === 'numbers' ? 'active' : ''}" href="#/numbers">Phone numbers</a>
        <a class="nav ${active === 'messages' ? 'active' : ''}" href="#/messages">Messages</a>
        <a class="nav ${active === 'calls' ? 'active' : ''}" href="#/calls">Calls</a>
        <a class="nav ${active === 'account' ? 'active' : ''}" href="#/account">My account</a>
        ${admin}
        <div class="spacer"></div>
        <div class="whoami">${esc(state.session.email)}${isAdmin() ? ' · admin' : ''}</div>
        <button class="ghost" id="signout">Sign out</button>
      </nav>
      <main class="main">${content}</main>
    </div>`;

  document.getElementById('signout').onclick = signOut;
}

function banner(kind, text) {
  return text ? `<div class="banner ${kind}">${esc(text)}</div>` : '';
}

function connectionBadge() {
  return state.session.connection
    ? `<span class="badge ok">connected</span>`
    : `<span class="badge warn">no Twilio account</span>`;
}

async function refreshSession() {
  state.session = await api('/api/auth/session');
}

// A helper for views that need a Twilio connection before they can do anything.
function needsTwilio(active, title) {
  if (state.session.connection) return false;
  shell(active, `
    <h1>${esc(title)}</h1>
    <div class="card">
      <p class="lead">This account has no Twilio account connected yet.</p>
      <a href="#/twilio"><button class="primary">Connect Twilio</button></a>
    </div>`);
  return true;
}

// ---------- auth view ----------

function renderAuth(error = null, mode = 'login') {
  const isLogin = mode === 'login';

  app.innerHTML = `
    <div class="auth-wrap">
      <div class="card auth-card">
        <h1>Twilio Caller</h1>
        <p class="lead">Backend portal — sign in to manage your numbers, messages and calls.</p>
        <div class="tabs">
          <button id="tab-login" class="${isLogin ? 'primary' : 'ghost'}">Sign in</button>
          <button id="tab-register" class="${isLogin ? 'ghost' : 'primary'}">Register</button>
        </div>
        ${banner('error', error)}
        <form id="auth-form">
          ${isLogin ? '' : `
            <label for="displayName">Name</label>
            <input id="displayName" autocomplete="name" placeholder="How should we call you?">`}
          <label for="email">Email</label>
          <input id="email" type="email" required autocomplete="username">
          <label for="password">Password</label>
          <input id="password" type="password" required minlength="8"
                 autocomplete="${isLogin ? 'current-password' : 'new-password'}">
          <div class="row end mt">
            <button type="submit" class="primary" id="auth-submit">
              ${isLogin ? 'Sign in' : 'Create account'}
            </button>
          </div>
        </form>
      </div>
    </div>`;

  document.getElementById('tab-login').onclick = () => renderAuth(null, 'login');
  document.getElementById('tab-register').onclick = () => renderAuth(null, 'register');

  document.getElementById('auth-form').onsubmit = async event => {
    event.preventDefault();
    const submit = document.getElementById('auth-submit');
    submit.disabled = true;

    try {
      const result = await api(isLogin ? '/api/auth/login' : '/api/auth/register', {
        method: 'POST',
        body: {
          email: document.getElementById('email').value.trim(),
          password: document.getElementById('password').value,
          displayName: isLogin ? undefined : document.getElementById('displayName').value.trim(),
          platform: 'portal',
          supportsVoip: false,
        },
      });

      saveToken(result.sessionToken);
      state.session = null;
      location.hash = '#/dashboard';
      render();
    } catch (err) {
      renderAuth(err.message, mode);
    }
  };
}

// ---------- dashboard ----------

function renderDashboard() {
  const s = state.session;
  const c = s.connection;

  shell('dashboard', `
    <h1>Welcome, ${esc(s.displayName || s.email)}</h1>
    <p class="lead">Everything the app can do, manageable from here. ${connectionBadge()}</p>

    <div class="card">
      <h2>Twilio account</h2>
      ${c ? `
        <table>
          <tr><th>Account</th><td>${esc(c.friendlyName)} <span class="mono">(${esc(c.accountSid)})</span></td></tr>
          <tr><th>In-app calling</th><td>${c.voiceReady
            ? '<span class="badge ok">ready</span>'
            : '<span class="badge warn">not provisioned yet</span>'}</td></tr>
          <tr><th>Fallback forward</th><td>${esc(c.fallbackForwardNumber || 'none')}</td></tr>
          <tr><th>Connected</th><td>${esc(fmtDate(c.createdAt))}</td></tr>
        </table>
        <div class="row mt">
          <a href="#/numbers"><button>Manage numbers</button></a>
          <a href="#/messages"><button>Messages</button></a>
          <a href="#/calls"><button>Calls</button></a>
        </div>
      ` : `
        <p class="muted">Connect your Twilio account to start calling and texting from your numbers.</p>
        <a href="#/twilio"><button class="primary">Connect Twilio</button></a>
      `}
    </div>

    <div class="card">
      <h2>Your account</h2>
      <table>
        <tr><th>Email</th><td>${esc(s.email)}</td></tr>
        <tr><th>Role</th><td>${isAdmin() ? 'Administrator' : 'User'}</td></tr>
        <tr><th>This session</th><td class="mono">${esc(s.identity)}</td></tr>
      </table>
    </div>`);
}

// ---------- twilio connection ----------

function renderTwilio(notice = null, error = null) {
  const c = state.session.connection;

  shell('twilio', `
    <h1>Twilio account</h1>
    <p class="lead">The API key secret is stored encrypted on the backend and never shown again.</p>
    ${banner('ok', notice)}
    ${banner('error', error)}

    ${c ? `
      <div class="card">
        <h2>Connected: ${esc(c.friendlyName)}</h2>
        <p class="muted mono">${esc(c.accountSid)}</p>
        <div class="row mt">
          <button class="danger" id="disconnect">Disconnect this Twilio account</button>
        </div>
      </div>` : ''}

    <div class="card">
      <h2>${c ? 'Replace credentials' : 'Connect your Twilio account'}</h2>
      <form id="twilio-form">
        <label for="accountSid">Account SID</label>
        <input id="accountSid" placeholder="AC..." required value="${esc(c?.accountSid || '')}">
        <label for="apiKeySid">API Key SID</label>
        <input id="apiKeySid" placeholder="SK..." required>
        <label for="apiKeySecret">API Key Secret</label>
        <input id="apiKeySecret" type="password" required>
        <label for="authToken">Auth token (optional, verifies webhook signatures)</label>
        <input id="authToken" type="password">
        <div class="row end mt">
          <button type="submit" class="primary" id="twilio-submit">
            ${c ? 'Update connection' : 'Connect'}
          </button>
        </div>
      </form>
    </div>`);

  document.getElementById('twilio-form').onsubmit = async event => {
    event.preventDefault();
    const submit = document.getElementById('twilio-submit');
    submit.disabled = true;

    try {
      await api('/api/twilio/connect', {
        method: 'POST',
        body: {
          accountSid: document.getElementById('accountSid').value.trim(),
          apiKeySid: document.getElementById('apiKeySid').value.trim(),
          apiKeySecret: document.getElementById('apiKeySecret').value.trim(),
          authToken: document.getElementById('authToken').value.trim() || null,
        },
      });
      await refreshSession();
      renderTwilio('Twilio account connected.');
    } catch (err) {
      renderTwilio(null, err.message);
    }
  };

  const disconnect = document.getElementById('disconnect');
  if (disconnect) {
    disconnect.onclick = async () => {
      if (!confirm('Disconnect this Twilio account? Numbers will stop routing here.')) return;
      try {
        await api('/api/twilio/connection', { method: 'DELETE' });
        await refreshSession();
        renderTwilio('Twilio account disconnected.');
      } catch (err) {
        renderTwilio(null, err.message);
      }
    };
  }
}

// ---------- numbers ----------

async function renderNumbers(notice = null, error = null) {
  if (needsTwilio('numbers', 'Phone numbers')) return;

  shell('numbers', `<h1>Phone numbers</h1><p class="lead">Loading…</p>`);

  let numbers = [];
  try {
    numbers = await api('/api/numbers');
  } catch (err) {
    if (!state.token) return;
    shell('numbers', `<h1>Phone numbers</h1>${banner('error', err.message)}`);
    return;
  }

  const rows = numbers.map(n => `
    <tr>
      <td><input type="checkbox" class="pick" value="${esc(n.sid)}" ${n.wiredToThisBackend ? 'checked' : ''}></td>
      <td><strong>${esc(n.phoneNumber)}</strong><br><span class="muted">${esc(n.friendlyName)}</span></td>
      <td>
        ${n.voiceEnabled ? '<span class="badge muted">voice</span>' : ''}
        ${n.smsEnabled ? '<span class="badge muted">sms</span>' : ''}
        ${n.mmsEnabled ? '<span class="badge muted">mms</span>' : ''}
      </td>
      <td>${n.wiredToThisBackend
        ? '<span class="badge ok">routes here</span>'
        : '<span class="badge warn">not wired</span>'}</td>
    </tr>`).join('');

  shell('numbers', `
    <h1>Phone numbers</h1>
    <p class="lead">Tick the numbers whose calls and texts should route to this backend, then save.</p>
    ${banner('ok', notice)}
    ${banner('error', error)}
    <div class="card">
      ${numbers.length === 0
        ? '<div class="empty">This Twilio account has no phone numbers yet. Buy one in the Twilio console.</div>'
        : `<table>
            <tr><th></th><th>Number</th><th>Capabilities</th><th>Routing</th></tr>
            ${rows}
          </table>
          <label for="fallback">Fallback forward number (rings when no app is available)</label>
          <input id="fallback" placeholder="+15551234567"
                 value="${esc(state.session.connection.fallbackForwardNumber || '')}">
          <div class="row end mt">
            <button class="primary" id="provision">Save routing</button>
          </div>`}
    </div>`);

  const provision = document.getElementById('provision');
  if (provision) {
    provision.onclick = async () => {
      provision.disabled = true;
      const sids = [...document.querySelectorAll('.pick:checked')].map(cb => cb.value);
      try {
        const result = await api('/api/numbers/provision', {
          method: 'POST',
          body: {
            phoneNumberSids: sids,
            fallbackForwardNumber: document.getElementById('fallback').value.trim() || null,
          },
        });
        await refreshSession();
        renderNumbers(result.failures?.length
          ? `Saved, but some numbers failed: ${result.failures.join(', ')}`
          : 'Routing saved.');
      } catch (err) {
        renderNumbers(null, err.message);
      }
    };
  }
}

// ---------- shared number picker ----------

function numberPicker(numbers, selected, id) {
  const options = numbers.map(n => `
    <option value="${esc(n.phoneNumber)}" ${n.phoneNumber === selected ? 'selected' : ''}>
      ${esc(n.phoneNumber)}${n.wiredToThisBackend ? '' : ' (not wired)'}
    </option>`).join('');
  return `<select id="${id}">${options}</select>`;
}

async function loadNumbersFor(view) {
  const numbers = await api('/api/numbers');
  if (numbers.length === 0) {
    shell(view, `
      <h1>${view === 'messages' ? 'Messages' : 'Calls'}</h1>
      <div class="card"><div class="empty">
        This Twilio account has no phone numbers. Buy one in the Twilio console,
        then wire it up under Phone numbers.
      </div></div>`);
    return null;
  }

  if (!state.selectedNumber || !numbers.some(n => n.phoneNumber === state.selectedNumber)) {
    const wired = numbers.find(n => n.wiredToThisBackend);
    state.selectedNumber = (wired || numbers[0]).phoneNumber;
  }

  return numbers;
}

// ---------- messages ----------

async function renderMessages(error = null) {
  if (needsTwilio('messages', 'Messages')) return;
  shell('messages', `<h1>Messages</h1><p class="lead">Loading…</p>`);

  let numbers, conversations = [];
  try {
    numbers = await loadNumbersFor('messages');
    if (!numbers) return;
    conversations = await api(
      `/api/messages/conversations?number=${encodeURIComponent(state.selectedNumber)}`);
  } catch (err) {
    if (!state.token) return;
    shell('messages', `<h1>Messages</h1>${banner('error', err.message)}`);
    return;
  }

  if (state.selectedPeer && !conversations.some(c => c.peerNumber === state.selectedPeer)) {
    conversations = [{ peerNumber: state.selectedPeer, lastBody: '', messageCount: 0 }, ...conversations];
  }

  const convoRows = conversations.map(c => `
    <div class="convo ${c.peerNumber === state.selectedPeer ? 'active' : ''}"
         data-peer="${esc(c.peerNumber)}">
      <div class="peer">${esc(c.peerNumber)}</div>
      <div class="preview">${esc(c.lastBody || '')}</div>
    </div>`).join('');

  shell('messages', `
    <h1>Messages</h1>
    ${banner('error', error)}
    <div class="row" style="margin-bottom:16px">
      <label style="margin:0">From number</label>
      ${numberPicker(numbers, state.selectedNumber, 'msg-number')}
      <button class="small" id="new-thread">New conversation</button>
    </div>
    <div class="split">
      <div class="card convo-list" id="convos">
        ${convoRows || '<div class="empty">No conversations yet.</div>'}
      </div>
      <div class="card">
        <div id="thread-pane">
          ${state.selectedPeer
            ? '<div class="empty">Loading conversation…</div>'
            : '<div class="empty">Pick a conversation, or start a new one.</div>'}
        </div>
      </div>
    </div>`);

  document.getElementById('msg-number').onchange = event => {
    state.selectedNumber = event.target.value;
    state.selectedPeer = null;
    renderMessages();
  };

  document.getElementById('new-thread').onclick = () => {
    const peer = prompt('Send to which number? Use E.164, e.g. +15551234567');
    if (!peer) return;
    state.selectedPeer = peer.trim();
    renderMessages();
  };

  document.querySelectorAll('.convo').forEach(node => {
    node.onclick = () => {
      state.selectedPeer = node.dataset.peer;
      renderMessages();
    };
  });

  if (state.selectedPeer) await renderThread();
}

async function renderThread() {
  const pane = document.getElementById('thread-pane');
  let messages = [];
  try {
    messages = await api(
      `/api/messages?number=${encodeURIComponent(state.selectedNumber)}` +
      `&peer=${encodeURIComponent(state.selectedPeer)}`);
  } catch (err) {
    if (!state.token) return;
    pane.innerHTML = banner('error', err.message);
    return;
  }

  const bubbles = [...messages].reverse().map(m => `
    <div class="bubble ${m.direction === 'inbound' ? 'in' : 'out'}">
      ${esc(m.body || (m.numMedia ? `${m.numMedia} attachment(s)` : ''))}
      <span class="meta">${esc(fmtDate(m.sentAt))} · ${esc(m.status)}</span>
    </div>`).join('');

  pane.innerHTML = `
    <h2>${esc(state.selectedPeer)}</h2>
    <div class="thread" id="thread">${bubbles || '<div class="empty">No messages yet.</div>'}</div>
    <form class="sendbox" id="send-form">
      <input id="send-body" placeholder="Type a message…" autocomplete="off">
      <button type="submit" class="primary">Send</button>
    </form>`;

  const thread = document.getElementById('thread');
  thread.scrollTop = thread.scrollHeight;

  document.getElementById('send-form').onsubmit = async event => {
    event.preventDefault();
    const input = document.getElementById('send-body');
    const body = input.value.trim();
    if (!body) return;
    input.disabled = true;

    try {
      await api('/api/messages', {
        method: 'POST',
        body: { from: state.selectedNumber, to: state.selectedPeer, body },
      });
      await renderThread();
    } catch (err) {
      input.disabled = false;
      alert(err.message);
    }
  };
}

// ---------- calls ----------

async function renderCalls(notice = null, error = null) {
  if (needsTwilio('calls', 'Calls')) return;
  shell('calls', `<h1>Calls</h1><p class="lead">Loading…</p>`);

  let numbers, calls = [];
  try {
    numbers = await loadNumbersFor('calls');
    if (!numbers) return;
    calls = await api(`/api/calls?number=${encodeURIComponent(state.selectedNumber)}`);
  } catch (err) {
    if (!state.token) return;
    shell('calls', `<h1>Calls</h1>${banner('error', err.message)}`);
    return;
  }

  const rows = calls.map(c => `
    <tr>
      <td>${c.direction?.startsWith('outbound') ? '↗ out' : '↙ in'}</td>
      <td class="mono">${esc(c.from)}</td>
      <td class="mono">${esc(c.to)}</td>
      <td>${esc(c.status)}</td>
      <td>${c.durationSeconds != null ? `${c.durationSeconds}s` : '—'}</td>
      <td>${esc(fmtDate(c.startedAt))}</td>
    </tr>`).join('');

  shell('calls', `
    <h1>Calls</h1>
    <p class="lead">The browser has no Voice SDK, so calls from here use dial-out:
      Twilio rings your own phone first, then bridges the call.</p>
    ${banner('ok', notice)}
    ${banner('error', error)}

    <div class="card">
      <h2>Place a call (dial-out)</h2>
      <form id="dial-form">
        <div class="row">
          <div style="flex:1;min-width:180px">
            <label>From (your Twilio number)</label>
            ${numberPicker(numbers, state.selectedNumber, 'call-number')}
          </div>
          <div style="flex:1;min-width:180px">
            <label for="dial-to">Call</label>
            <input id="dial-to" placeholder="+15551234567" required>
          </div>
          <div style="flex:1;min-width:180px">
            <label for="dial-bridge">Ring me on</label>
            <input id="dial-bridge" placeholder="${esc(state.session.connection.fallbackForwardNumber || '+1...')}"
                   value="${esc(state.session.connection.fallbackForwardNumber || '')}">
          </div>
        </div>
        <div class="row end mt">
          <button type="submit" class="primary">Call</button>
        </div>
      </form>
    </div>

    <div class="card">
      <h2>History for <span class="mono">${esc(state.selectedNumber)}</span></h2>
      ${rows
        ? `<table><tr><th></th><th>From</th><th>To</th><th>Status</th><th>Duration</th><th>When</th></tr>${rows}</table>`
        : '<div class="empty">No calls yet.</div>'}
    </div>`);

  document.getElementById('call-number').onchange = event => {
    state.selectedNumber = event.target.value;
    renderCalls();
  };

  document.getElementById('dial-form').onsubmit = async event => {
    event.preventDefault();
    try {
      await api('/api/calls/dial-out', {
        method: 'POST',
        body: {
          from: state.selectedNumber,
          to: document.getElementById('dial-to').value.trim(),
          bridgeTo: document.getElementById('dial-bridge').value.trim(),
        },
      });
      renderCalls('Calling: your phone rings first, then the call is bridged.');
    } catch (err) {
      renderCalls(null, err.message);
    }
  };
}

// ---------- my account ----------

function renderAccount(notice = null, error = null) {
  shell('account', `
    <h1>My account</h1>
    <p class="lead">${esc(state.session.email)}</p>
    ${banner('ok', notice)}
    ${banner('error', error)}
    <div class="card">
      <h2>Change password</h2>
      <form id="pw-form">
        <label for="pw-current">Current password</label>
        <input id="pw-current" type="password" required autocomplete="current-password">
        <label for="pw-new">New password (at least 8 characters)</label>
        <input id="pw-new" type="password" required minlength="8" autocomplete="new-password">
        <div class="row end mt">
          <button type="submit" class="primary">Change password</button>
        </div>
      </form>
      <p class="muted mt">Changing your password signs you out everywhere else,
        including the app.</p>
    </div>`);

  document.getElementById('pw-form').onsubmit = async event => {
    event.preventDefault();
    try {
      const result = await api('/api/auth/change-password', {
        method: 'POST',
        body: {
          currentPassword: document.getElementById('pw-current').value,
          newPassword: document.getElementById('pw-new').value,
        },
      });
      saveToken(result.sessionToken); // the old token died with the password
      renderAccount('Password changed.');
    } catch (err) {
      renderAccount(null, err.message);
    }
  };
}

// ---------- administration ----------

async function renderAdmin(notice = null, error = null) {
  shell('admin', `<h1>Administration</h1><p class="lead">Loading…</p>`);

  let users = [];
  try {
    users = await api('/api/admin/users');
  } catch (err) {
    if (!state.token) return;
    shell('admin', `<h1>Administration</h1>${banner('error', err.message)}`);
    return;
  }

  const me = state.session.userId;
  const rows = users.map(u => {
    const admin = u.roles.includes('Administrator');
    const self = u.id === me;
    return `
      <tr>
        <td>
          <strong>${esc(u.displayName)}</strong>${self ? ' <span class="badge muted">you</span>' : ''}
          <br><span class="muted">${esc(u.email)}</span>
        </td>
        <td>${admin ? '<span class="badge ok">admin</span>' : '<span class="badge muted">user</span>'}
            ${u.disabled ? '<span class="badge danger">disabled</span>' : ''}</td>
        <td>${u.accountSid
          ? `${esc(u.twilioFriendlyName || '')}<br><span class="mono muted">${esc(u.accountSid)}</span>`
          : '<span class="muted">—</span>'}</td>
        <td>${u.deviceCount}<br><span class="muted">${esc(fmtDate(u.lastSeenAt))}</span></td>
        <td>
          <div class="row">
            <button class="small" data-act="role" data-id="${esc(u.id)}" data-to="${!admin}">
              ${admin ? 'Revoke admin' : 'Make admin'}
            </button>
            <button class="small" data-act="disable" data-id="${esc(u.id)}" data-to="${!u.disabled}"
              ${self ? 'disabled' : ''}>
              ${u.disabled ? 'Enable' : 'Disable'}
            </button>
            <button class="small" data-act="reset" data-id="${esc(u.id)}" data-email="${esc(u.email)}">
              Reset password
            </button>
            <button class="small danger" data-act="delete" data-id="${esc(u.id)}"
              data-email="${esc(u.email)}" ${self ? 'disabled' : ''}>
              Delete
            </button>
          </div>
        </td>
      </tr>`;
  }).join('');

  shell('admin', `
    <h1>Administration</h1>
    <p class="lead">Every account on this backend. ${users.length} account(s).</p>
    ${banner('ok', notice)}
    ${banner('error', error)}

    <div class="card">
      <table>
        <tr><th>Account</th><th>Status</th><th>Twilio</th><th>Devices · last seen</th><th>Actions</th></tr>
        ${rows}
      </table>
    </div>

    <div class="card">
      <h2>Create an account</h2>
      <form id="create-form">
        <div class="row">
          <div style="flex:1;min-width:160px">
            <label for="new-email">Email</label>
            <input id="new-email" type="email" required>
          </div>
          <div style="flex:1;min-width:160px">
            <label for="new-name">Name</label>
            <input id="new-name">
          </div>
          <div style="flex:1;min-width:160px">
            <label for="new-password">Password</label>
            <input id="new-password" type="password" required minlength="8">
          </div>
        </div>
        <div class="row mt">
          <label style="margin:0"><input type="checkbox" id="new-admin" style="width:auto"> Administrator</label>
          <div class="spacer" style="flex:1"></div>
          <button type="submit" class="primary">Create account</button>
        </div>
      </form>
    </div>`);

  document.querySelectorAll('button[data-act]').forEach(btn => {
    btn.onclick = async () => {
      const { act, id, to, email } = btn.dataset;
      try {
        if (act === 'role') {
          await api(`/api/admin/users/${id}/role`, {
            method: 'PUT', body: { isAdministrator: to === 'true' },
          });
          renderAdmin('Role updated. The account must sign in again.');
        } else if (act === 'disable') {
          const disabling = to === 'true';
          if (disabling && !confirm(`Disable ${email || 'this account'}? They are signed out immediately.`)) return;
          await api(`/api/admin/users/${id}/disabled`, {
            method: 'PUT', body: { disabled: disabling },
          });
          renderAdmin(disabling ? 'Account disabled.' : 'Account enabled.');
        } else if (act === 'reset') {
          const password = prompt(`New password for ${email} (at least 8 characters):`);
          if (!password) return;
          await api(`/api/admin/users/${id}/reset-password`, {
            method: 'POST', body: { newPassword: password },
          });
          renderAdmin('Password reset. Their old sessions are signed out.');
        } else if (act === 'delete') {
          if (!confirm(`Delete ${email} permanently, including their Twilio connection?`)) return;
          await api(`/api/admin/users/${id}`, { method: 'DELETE' });
          renderAdmin('Account deleted.');
        }
      } catch (err) {
        renderAdmin(null, err.message);
      }
    };
  });

  document.getElementById('create-form').onsubmit = async event => {
    event.preventDefault();
    try {
      await api('/api/admin/users', {
        method: 'POST',
        body: {
          email: document.getElementById('new-email').value.trim(),
          displayName: document.getElementById('new-name').value.trim() || null,
          password: document.getElementById('new-password').value,
          isAdministrator: document.getElementById('new-admin').checked,
        },
      });
      renderAdmin('Account created.');
    } catch (err) {
      renderAdmin(null, err.message);
    }
  };
}

// ---------- boot ----------

render();
