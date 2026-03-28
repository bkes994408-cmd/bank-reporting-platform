import { state } from './state.js';
import { apiBase } from './dom.js';

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
    const msg = typeof body === 'string' ? body : (body?.message || JSON.stringify(body));
    throw new Error(`${res.status} ${msg}`);
  }

  return body;
}
