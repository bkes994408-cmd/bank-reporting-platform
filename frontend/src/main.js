import { state } from './state.js';
import { byId, esc, setButtonBusy, toast } from './dom.js';
import { api, setUnauthorizedHandler } from './api.js';

const tabs = ['overview', 'users', 'audit'];
const THEME_KEY = 'admin_theme';

function setAuthUi(loggedIn) {
  byId('loginCard').classList.toggle('hidden', loggedIn);
  byId('app').classList.toggle('hidden', !loggedIn);
  document.body.classList.toggle('is-authenticated', loggedIn);
}

function setTopStatus(text = '', tone = 'muted') {
  const el = byId('topStatus');
  el.textContent = text;
  el.classList.remove('status--warn', 'status--ok');
  if (tone === 'warn') el.classList.add('status--warn');
  if (tone === 'ok') el.classList.add('status--ok');
}

function setLoading(loading, text = '資料更新中...') {
  if (loading) {
    setTopStatus(text);
  }
  document.body.classList.toggle('is-loading', loading);
  byId('refreshBtn').disabled = loading;
}

function applyTheme(mode) {
  const next = mode === 'dark' ? 'dark' : 'light';
  document.documentElement.dataset.theme = next;
  const btn = byId('themeToggle');
  if (btn) {
    const dark = next === 'dark';
    btn.textContent = dark ? '☀️ 明亮模式' : '🌙 暗黑模式';
    btn.setAttribute('aria-pressed', dark ? 'true' : 'false');
  }
}

function initTheme() {
  const saved = localStorage.getItem(THEME_KEY);
  if (saved === 'dark' || saved === 'light') {
    applyTheme(saved);
    return;
  }

  const prefersDark = window.matchMedia?.('(prefers-color-scheme: dark)').matches;
  applyTheme(prefersDark ? 'dark' : 'light');
}

function toggleTheme() {
  const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  localStorage.setItem(THEME_KEY, next);
  applyTheme(next);
}

function saveFilters() {
  sessionStorage.setItem('admin_user_query', byId('userQuery').value);
  sessionStorage.setItem('admin_status_filter', byId('statusFilter').value);
  sessionStorage.setItem('admin_audit_query', byId('auditActionFilter').value);
  sessionStorage.setItem('admin_audit_top', byId('auditTop').value);
}

function restoreFilters() {
  byId('userQuery').value = sessionStorage.getItem('admin_user_query') || '';
  byId('statusFilter').value = sessionStorage.getItem('admin_status_filter') || 'all';
  byId('auditActionFilter').value = sessionStorage.getItem('admin_audit_query') || '';
  byId('auditTop').value = sessionStorage.getItem('admin_audit_top') || '200';
}

function showTab(tab, save = true) {
  const next = tabs.includes(tab) ? tab : 'overview';
  tabs.forEach((name) => {
    byId(`tab${name[0].toUpperCase() + name.slice(1)}`).classList.toggle('hidden', name !== next);
    byId(`tab${name[0].toUpperCase() + name.slice(1)}Btn`).classList.toggle('active', name === next);
  });

  state.activeTab = next;
  if (save) {
    sessionStorage.setItem('admin_active_tab', next);
  }
}

function statusBadge(status) {
  const cls = status?.toLowerCase().includes('pending') ? 'pending' : status?.toLowerCase();
  return `<span class="badge ${esc(cls)}">${esc(status || '-')}</span>`;
}

function renderOverview() {
  const pending = state.users.filter((u) => u.status === 'PendingApproval').length;
  const active = state.users.filter((u) => u.status === 'Active').length;
  const locked = state.users.filter((u) => u.status === 'Locked').length;
  const admins = state.users.filter((u) => u.role === 'Admin').length;
  const adUsers = state.users.filter((u) => u.isAdUser).length;
  const localUsers = state.users.filter((u) => !u.isAdUser).length;

  byId('kpi').innerHTML = [
    ['總使用者', state.users.length],
    ['待審核', pending],
    ['已啟用', active],
    ['鎖定', locked],
    ['管理員', admins],
    ['AD 使用者', adUsers],
    ['Local 使用者', localUsers],
    ['稽核事件', state.audits.length]
  ]
    .map(([k, v]) => `<div class="kpi"><div class="muted">${esc(k)}</div><div class="v">${esc(v)}</div></div>`)
    .join('');

  const overview = byId('tabOverview');
  if (overview) {
    overview.dataset.count = `${state.users.length} users`;
  }
}

function renderRecentAudit() {
  const rows = state.audits.slice(0, 10).map((a) => `<tr>
    <td>${esc(new Date(a.at).toLocaleString())}</td>
    <td>${esc(a.action)}</td>
    <td>${esc(a.userName || '-')}
      <div class="muted">${esc(a.userRole || '')}</div>
    </td>
    <td>${esc(a.entityType)} / ${esc(a.entityId)}</td>
    <td>${esc(a.summary)}</td>
    <td>${esc(a.ipAddress || '-')}</td>
  </tr>`).join('');

  byId('recentAudit').innerHTML = `<table><thead><tr><th>時間</th><th>Action</th><th>User</th><th>Entity</th><th>Summary</th><th>IP</th></tr></thead><tbody>${rows || '<tr><td colspan="6" class="muted">無資料</td></tr>'}</tbody></table>`;
}

function renderUsers() {
  const q = byId('userQuery').value.trim().toLowerCase();
  const status = byId('statusFilter').value;

  const filtered = state.users.filter((u) => {
    const matchQ = !q || [u.name, u.email, u.institutionCode, u.department, u.title].some((x) =>
      (x || '').toLowerCase().includes(q));
    const matchS = status === 'all' || u.status === status;
    return matchQ && matchS;
  });

  const empty = `<tr><td colspan="8" class="muted empty-state">沒有符合條件的使用者</td></tr>`;
  const rows = filtered.map((u) => {
    const pendingControls = u.status === 'PendingApproval' ? `
      <div class="inline-actions">
        <select id="role-${u.id}">
          ${['Clerk', 'Supervisor', 'ReadOnly', 'Admin'].map((r) => `<option value="${r}" ${u.role === r ? 'selected' : ''}>${r}</option>`).join('')}
        </select>
        <input id="inst-${u.id}" value="${esc(u.institutionCode || '')}" placeholder="Institution" class="inline-input" />
        <button class="ok" data-action="approve" data-user-id="${u.id}">核准</button>
        <button class="danger" data-action="reject" data-user-id="${u.id}">拒絕</button>
      </div>` : '';

    return `<tr>
      <td>
        <div class="user-cell">
          <strong>${esc(u.name)}</strong>
          <div class="muted">${esc(u.email)}</div>
        </div>
      </td>
      <td>${statusBadge(u.status)}</td>
      <td>${esc(u.role)}</td>
      <td>${esc(u.institutionCode || '-')}</td>
      <td>${esc(u.department || '-')} / ${esc(u.title || '-')}</td>
      <td>${u.mfaEnabled ? '✅' : '❌'}</td>
      <td>${u.isAdUser ? 'AD' : 'Local'}</td>
      <td>
        <div class="inline-actions">
          <button data-action="revoke" data-user-id="${u.id}">Revoke Sessions</button>
          ${pendingControls}
        </div>
      </td>
    </tr>`;
  }).join('');

  byId('usersTable').innerHTML = `<thead><tr>
    <th>使用者</th><th>狀態</th><th>角色</th><th>機構</th><th>部門/職稱</th><th>MFA</th><th>Auth</th><th>操作</th>
  </tr></thead><tbody>${rows || empty}</tbody>`;

  const usersPanel = byId('tabUsers');
  if (usersPanel) {
    usersPanel.dataset.count = `${filtered.length} users`;
  }

  renderOverview();
}

function renderAudit() {
  const q = byId('auditActionFilter').value.trim().toLowerCase();
  const filtered = state.audits.filter((a) => !q || (a.action || '').toLowerCase().includes(q) || (a.summary || '').toLowerCase().includes(q));

  const empty = `<tr><td colspan="7" class="muted empty-state">目前沒有稽核紀錄</td></tr>`;
  const rows = filtered.map((a) => `<tr>
    <td>${esc(new Date(a.at).toLocaleString())}</td>
    <td><span class="audit-action">${esc(a.action)}</span></td>
    <td>
      <div class="user-cell">
        <strong>${esc(a.userName || '-')}</strong>
        <div class="muted">${esc(a.userRole || '-')}</div>
      </div>
    </td>
    <td>${esc(a.entityType)}</td>
    <td>${esc(a.entityId)}</td>
    <td>${esc(a.summary)}</td>
    <td>${esc(a.ipAddress || '-')}</td>
  </tr>`).join('');

  byId('auditTable').innerHTML = `<thead><tr><th>時間</th><th>Action</th><th>User</th><th>Entity</th><th>ID</th><th>Summary</th><th>IP</th></tr></thead>
    <tbody>${rows || empty}</tbody>`;

  const auditPanel = byId('tabAudit');
  if (auditPanel) {
    auditPanel.dataset.count = `${filtered.length} events`;
  }

  renderOverview();
}

async function loadUsers() {
  state.users = await api('/admin/users');
  renderUsers();
}

async function loadMfaPolicy() {
  state.mfaPolicy = await api('/admin/security/mfa-policy');
  byId('mfaScope').value = state.mfaPolicy.scope || 'Disabled';
  byId('mfaEnforce').checked = !!state.mfaPolicy.enforceOnPrivilegedEndpoints;
  byId('mfaRevoke').checked = false;
  byId('mfaPolicyHint').textContent = `目前：scope=${state.mfaPolicy.scope} / enforce=${state.mfaPolicy.enforceOnPrivilegedEndpoints ? 'on' : 'off'}`;
}

async function loadAudit() {
  const top = Number(byId('auditTop').value || 200);
  state.audits = await api(`/admin/audit-logs?top=${top}`);
  renderAudit();
  renderRecentAudit();
}

async function refreshAll(button = byId('refreshBtn')) {
  setLoading(true);
  setButtonBusy(button, true, '更新中...');
  try {
    await loadUsers();
    await loadMfaPolicy();
    await loadAudit();
    setTopStatus('資料已更新', 'ok');
  } catch (e) {
    if (!state.sessionExpiredHandled) {
      toast(`資料載入失敗：${e.message}`, 'error');
    }
  } finally {
    setLoading(false);
    setButtonBusy(button, false);
  }
}

async function login() {
  const loginBtn = byId('loginBtn');
  setButtonBusy(loginBtn, true, '登入中...');

  try {
    const body = {
      email: byId('email').value.trim(),
      password: byId('password').value,
      mfaCode: byId('mfaCode').value.trim() || null
    };

    const data = await api('/auth/login', { method: 'POST', body: JSON.stringify(body) });
    state.token = data.token || state.token;
    state.sessionExpiredHandled = false;

    if (state.token) {
      sessionStorage.setItem('admin_token', state.token);
    }

    state.currentUser = data.user;
    byId('whoami').textContent = `${data.user?.name || ''} (${data.user?.role || ''})`;
    setAuthUi(true);
    await refreshAll(loginBtn);
    showTab(state.activeTab);
    toast('登入成功', 'success');
  } catch (e) {
    toast(`登入失敗：${e.message}`, 'error');
  } finally {
    setButtonBusy(loginBtn, false);
  }
}

function clearSessionState() {
  state.token = '';
  state.currentUser = null;
  state.users = [];
  state.audits = [];
  sessionStorage.removeItem('admin_token');
  byId('whoami').textContent = '';
}

async function logout() {
  const logoutBtn = byId('logoutBtn');
  setButtonBusy(logoutBtn, true, '登出中...');

  try {
    await api('/auth/logout', { method: 'POST' });
  } catch {
    // ignore best effort logout
  }

  clearSessionState();
  setAuthUi(false);
  setTopStatus('');
  toast('已登出', 'success');
  setButtonBusy(logoutBtn, false);
}

function forceLogoutForExpiredSession() {
  if (state.sessionExpiredHandled) {
    return;
  }

  state.sessionExpiredHandled = true;
  clearSessionState();
  setAuthUi(false);
  setTopStatus('登入狀態已過期，請重新登入', 'warn');
  toast('登入已過期或 token 無效，請重新登入', 'warn');
}

async function approveUser(userId, approve) {
  try {
    const role = byId(`role-${userId}`)?.value || 'Clerk';
    const institutionCode = byId(`inst-${userId}`)?.value?.trim() || '';
    if (!institutionCode) {
      throw new Error('InstitutionCode 必填');
    }

    await api(`/admin/users/${userId}/approve`, {
      method: 'POST',
      body: JSON.stringify({ approve, role, institutionCode })
    });

    toast(approve ? '已核准帳號' : '已拒絕帳號', 'success');
    await refreshAll();
  } catch (e) {
    toast(`操作失敗：${e.message}`, 'error');
  }
}

async function revokeSessions(userId) {
  if (!confirm('確定要撤銷此使用者所有 active sessions？')) {
    return;
  }

  try {
    const data = await api(`/admin/users/${userId}/revoke-sessions`, { method: 'POST' });
    toast(`已撤銷 ${data.revokedCount} 個 sessions`, 'success');
    await loadAudit();
  } catch (e) {
    toast(`撤銷失敗：${e.message}`, 'error');
  }
}

async function updateMfaPolicy() {
  const updateBtn = byId('updateMfaBtn');
  setButtonBusy(updateBtn, true, '更新中...');

  try {
    const payload = {
      scope: byId('mfaScope').value,
      enforceOnPrivilegedEndpoints: byId('mfaEnforce').checked,
      revokeNonCompliantSessions: byId('mfaRevoke').checked
    };

    const data = await api('/admin/security/mfa-policy', {
      method: 'PUT',
      body: JSON.stringify(payload)
    });

    toast(`MFA 政策已更新，撤銷 sessions: ${data.revokedCount}`, 'success');
    await refreshAll(updateBtn);
  } catch (e) {
    toast(`更新政策失敗：${e.message}`, 'error');
  } finally {
    setButtonBusy(updateBtn, false);
  }
}

function registerEvents() {
  byId('loginBtn').addEventListener('click', login);
  byId('logoutBtn').addEventListener('click', logout);
  byId('refreshBtn').addEventListener('click', () => refreshAll(byId('refreshBtn')));
  byId('updateMfaBtn').addEventListener('click', updateMfaPolicy);
  byId('themeToggle').addEventListener('click', toggleTheme);

  byId('loadAuditBtn').addEventListener('click', async () => {
    saveFilters();
    const btn = byId('loadAuditBtn');
    setButtonBusy(btn, true, '載入中...');
    try {
      await loadAudit();
      toast('稽核資料已更新', 'success');
    } catch (e) {
      toast(`載入稽核失敗：${e.message}`, 'error');
    } finally {
      setButtonBusy(btn, false);
    }
  });

  byId('userQuery').addEventListener('input', () => {
    saveFilters();
    renderUsers();
  });

  byId('statusFilter').addEventListener('change', () => {
    saveFilters();
    renderUsers();
  });

  byId('auditActionFilter').addEventListener('input', () => {
    saveFilters();
    renderAudit();
  });

  byId('auditTop').addEventListener('change', saveFilters);

  document.querySelectorAll('[data-tab]').forEach((btn) => {
    btn.addEventListener('click', () => showTab(btn.dataset.tab));
  });

  byId('usersTable').addEventListener('click', async (event) => {
    const target = event.target.closest('button[data-action]');
    if (!target) {
      return;
    }

    const userId = target.dataset.userId;
    const action = target.dataset.action;

    setButtonBusy(target, true);
    if (action === 'approve') {
      await approveUser(userId, true);
    }
    if (action === 'reject') {
      await approveUser(userId, false);
    }
    if (action === 'revoke') {
      await revokeSessions(userId);
    }
    setButtonBusy(target, false);
  });
}

async function bootstrapSession() {
  if (!state.token) {
    return;
  }

  setAuthUi(true);
  byId('whoami').textContent = '使用既有 token session';
  await refreshAll();
}

function init() {
  setUnauthorizedHandler(({ path }) => {
    if (path !== '/auth/login') {
      forceLogoutForExpiredSession();
    }
  });

  initTheme();
  registerEvents();
  restoreFilters();
  showTab(state.activeTab, false);
  bootstrapSession();
}

init();
