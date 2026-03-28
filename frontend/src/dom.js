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

let toastTimer = null;

export function toast(message, type = 'info') {
  const t = byId('toast');
  t.textContent = message;
  t.classList.remove('hidden');
  t.classList.remove('toast--error', 'toast--success', 'toast--warn');

  if (type === 'error') t.classList.add('toast--error');
  if (type === 'success') t.classList.add('toast--success');
  if (type === 'warn') t.classList.add('toast--warn');

  if (toastTimer) {
    clearTimeout(toastTimer);
  }

  toastTimer = setTimeout(() => t.classList.add('hidden'), 3000);
}

export function setButtonBusy(button, busy, busyText = '處理中...') {
  if (!button) return;

  if (busy) {
    if (!button.dataset.originalText) {
      button.dataset.originalText = button.textContent;
    }
    button.disabled = true;
    button.textContent = busyText;
    return;
  }

  button.disabled = false;
  if (button.dataset.originalText) {
    button.textContent = button.dataset.originalText;
    delete button.dataset.originalText;
  }
}
