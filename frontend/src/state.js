export const state = {
  token: sessionStorage.getItem('admin_token') || '',
  currentUser: null,
  users: [],
  audits: [],
  mfaPolicy: null
};
