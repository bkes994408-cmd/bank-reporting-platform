import { state } from './state.js';
import { apiBase } from './dom.js';

let unauthorizedHandler = null;

export function setUnauthorizedHandler(handler) {
  unauthorizedHandler = handler;
}

function isAuthError(status, body, raw) {
  if (status !== 401 && status !== 403) {
    return false;
  }

  // Common backend auth failures return empty 401/403 (e.g. Results.Unauthorized()).
  // Treat empty bodies as auth errors so expired-session fallback still runs.
  if ((body == null || body === '') && (!raw || raw.trim() === '')) {
    return true;
  }

  const text = `${typeof body === 'string' ? body : JSON.stringify(body || {})} ${raw || ''}`.toLowerCase();
  return text.includes('token') || text.includes('jwt') || text.includes('unauthorized') || text.includes('forbidden');
}

export async function api(path, opt = {}) {
  const headers = { 'Content-Type': 'application/json', ...(opt.headers || {}) };
  if (state.token) {
    headers['X-Auth-Token'] = state.token;
  }

  const res = await fetch(apiBase() + path, {
    ...opt,
    headers,
    credentials: 'include'
  });

  const raw = await res.text();
  let body = raw;
  try {
    body = raw ? JSON.parse(raw) : null;
  } catch {
    // Keep raw text when body is not JSON
  }

  if (!res.ok) {
    if (unauthorizedHandler && isAuthError(res.status, body, raw)) {
      unauthorizedHandler({ status: res.status, body, path });
    }

    const msg = typeof body === 'string' ? body : (body?.message || JSON.stringify(body));
    throw new Error(`${res.status} ${msg}`);
  }

  return body;
}
