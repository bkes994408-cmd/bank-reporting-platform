import { state } from './state.js';
import { byId, esc, toast } from './dom.js';
import { api } from './api.js';

const tabs = ['overview', 'users', 'audit'];

function showTab(tab) {
  tabs.forEach((name) => {
    byId(`tab${name[0].toUpperCase() + name.slice(1)}`).classList.toggle('hidden', name !== tab);
    byId(`tab${name[0].toUpperCase() + name.slice(1)}Btn`).classList.toggle('active', name === tab);
  });
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

  byId('kpi').innerHTML = [
    ['總使用者', state.users.length],
    ['待審核', pending],
    ['已啟用', active],
    ['鎖定', locked],
    ['管理員', admins],
    ['稽核事件', state.audits.length],
    ['本次登入角色', state.currentUser?.role || '-'],
    ['本次登入信箱', state.currentUser?.email || '-']
  ]
    .map(([k, v]) => `<div class="kpi"><div class="muted">${esc(k)}</div><div class="v">${esc(v)}</div></div>`)
    .join('');
}

function renderRecentAudit() {
  const rows = state.audits.slice(0, 10).map((a) => `<tr>
    <td>${esc(new Date(a.at).toLocaleString())}</td>
    <td>${esc(a.action)}</td>
    <td>${esc(a.userName || '-')}</td>
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

  const rows = filtered.map((u) => {
    const pendingControls = u.status === 'PendingApproval' ? `
      <div class="row" style="margin-top:6px">
        <select id="role-${u.id}">
          ${['Clerk', 'Supervisor', 'ReadOnly', 'Admin'].map((r) => `<option value="${r}" ${u.role === r ? 'selected' : ''}>${r}</option>`).join('')}
        </select>
        <input id="inst-${u.id}" value="${esc(u.institutionCode || '')}" placeholder="Institution" style="width:110px" />
        <button class="ok" data-action="approve" data-user-id="${u.id}">核准</button>
        <button class="danger" data-action="reject" data-user-id="${u.id}">拒絕</button>
      </div>` : '';

    return `<tr>
      <td>${esc(u.name)}<div class="muted">${esc(u.email)}</div></td>
      <td>${statusBadge(u.status)}</td>
      <td>${esc(u.role)}</td>
      <td>${esc(u.institutionCode || '-')}</td>
      <td>${esc(u.department || '-')} / ${esc(u.title || '-')}</td>
      <td>${u.mfaEnabled ? '✅' : '❌'}</td>
      <td>${u.isAdUser ? 'AD' : 'Local'}</td>
      <td>
        <button data-action="revoke" data-user-id="${u.id}">Revoke Sessions</button>
        ${pendingControls}
      </td>
    </tr>`;
  }).join('');

  byId('usersTable').innerHTML = `<thead><tr>
    <th>使用者</th><th>狀態</th><th>角色</th><th>機構</th><th>部門/職稱</th><th>MFA</th><th>Auth</th><th>操作</th>
  </tr></thead><tbody>${rows || '<tr><td colspan="8" class="muted">無符合資料</td></tr>'}</tbody>`;

  renderOverview();
}

function renderAudit() {
  const q = byId('auditActionFilter').value.trim().toLowerCase();
  const filtered = state.audits.filter((a) => !q || (a.action || '').toLowerCase().includes(q) || (a.summary || '').toLowerCase().includes(q));

  const rows = filtered.map((a) => `<tr>
    <td>${esc(new Date(a.at).toLocaleString())}</td>
    <td>${esc(a.action)}</td>
    <td>${esc(a.userName || '-')}</td>
    <td>${esc(a.entityType)}</td>
    <td>${esc(a.entityId)}</td>
    <td>${esc(a.summary)}</td>
    <td>${esc(a.ipAddress || '-')}</td>
  </tr>`).join('');

  byId('auditTable').innerHTML = `<thead><tr><th>時間</th><th>Action</th><th>User</th><th>Entity</th><th>ID</th><th>Summary</th><th>IP</th></tr></thead>
    <tbody>${rows || '<tr><td colspan="7" class="muted">無資料</td></tr>'}</tbody>`;

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

async function refreshAll() {
  try {
    await loadUsers();
    await loadMfaPolicy();
    await loadAudit();
  } catch (e) {
    toast(`資料載入失敗：${e.message}`, true);
  }
}

async function login() {
  try {
    const body = {
      email: byId('email').value.trim(),
      password: byId('password').value,
      mfaCode: byId('mfaCode').value.trim() || null
    };

    const data = await api('/auth/login', { method: 'POST', body: JSON.stringify(body) });
    state.token = data.token || state.token;
    if (state.token) {
      sessionStorage.setItem('admin_token', state.token);
    }

    state.currentUser = data.user;
    byId('whoami').textContent = `${data.user?.name || ''} (${data.user?.role || ''})`;
    byId('loginCard').classList.add('hidden');
    byId('app').classList.remove('hidden');
    await refreshAll();
    toast('登入成功');
  } catch (e) {
    toast(`登入失敗：${e.message}`, true);
  }
}

async function logout() {
  try {
    await api('/auth/logout', { method: 'POST' });
  } catch {
    // ignore best effort logout
  }

  state.token = '';
  sessionStorage.removeItem('admin_token');
  byId('app').classList.add('hidden');
  byId('loginCard').classList.remove('hidden');
  toast('已登出');
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

    toast(approve ? '已核准帳號' : '已拒絕帳號');
    await refreshAll();
  } catch (e) {
    toast(`操作失敗：${e.message}`, true);
  }
}

async function revokeSessions(userId) {
  if (!confirm('確定要撤銷此使用者所有 active sessions？')) {
    return;
  }

  try {
    const data = await api(`/admin/users/${userId}/revoke-sessions`, { method: 'POST' });
    toast(`已撤銷 ${data.revokedCount} 個 sessions`);
    await loadAudit();
  } catch (e) {
    toast(`撤銷失敗：${e.message}`, true);
  }
}

async function updateMfaPolicy() {
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

    toast(`MFA 政策已更新，撤銷 sessions: ${data.revokedCount}`);
    await refreshAll();
  } catch (e) {
    toast(`更新政策失敗：${e.message}`, true);
  }
}

function registerEvents() {
  byId('loginBtn').addEventListener('click', login);
  byId('logoutBtn').addEventListener('click', logout);
  byId('refreshBtn').addEventListener('click', refreshAll);
  byId('updateMfaBtn').addEventListener('click', updateMfaPolicy);
  byId('loadAuditBtn').addEventListener('click', loadAudit);

  byId('userQuery').addEventListener('input', renderUsers);
  byId('statusFilter').addEventListener('change', renderUsers);
  byId('auditActionFilter').addEventListener('input', renderAudit);

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

    if (action === 'approve') {
      await approveUser(userId, true);
    }
    if (action === 'reject') {
      await approveUser(userId, false);
    }
    if (action === 'revoke') {
      await revokeSessions(userId);
    }
  });
}

async function bootstrapSession() {
  if (!state.token) {
    return;
  }

  byId('loginCard').classList.add('hidden');
  byId('app').classList.remove('hidden');
  byId('whoami').textContent = '使用既有 token session';
  await refreshAll();
}

function init() {
  registerEvents();
  bootstrapSession();
}

init();
