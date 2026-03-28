export const byId = (id) => document.getElementById(id);

export const esc = (s) =>
  (s ?? '').toString().replace(/[&<>\"']/g, (c) => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '\"': '&quot;',
    "'": '&#39;'
  }[c]));

export function apiBase() {
  return byId('apiBase').value.trim().replace(/\/$/, '');
}

export function toast(message, isError = false) {
  const t = byId('toast');
  t.textContent = message;
  t.classList.remove('hidden');
  t.style.background = isError ? '#c62828' : '#1f2a44';
  setTimeout(() => t.classList.add('hidden'), 2600);
}
