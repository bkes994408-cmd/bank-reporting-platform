export const state = {
  token: sessionStorage.getItem('admin_token') || '',
  currentUser: null,
  users: [],
  audits: [],
  mfaPolicy: null,
  activeTab: sessionStorage.getItem('admin_active_tab') || 'overview',
  sessionExpiredHandled: false
};
