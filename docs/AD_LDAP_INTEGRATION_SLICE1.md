# AD / LDAP 整合（第一階段）

本文件描述 `bank-reporting-platform` 目前已實作的第一個實用切片：
在保留既有登入與 session 模型下，讓 AD 帳號可改用真實 LDAP bind 驗證。

## 目標與範圍

### 這一階段已完成
- 以設定切換 AD 驗證模式：`mock` 或 `ldap`
- AD 帳號登入支援 LDAP bind 驗證（`Auth:Ad:Mode=ldap`）
- LDAP 異常（不可達、TLS 問題、設定錯誤）與帳密錯誤分流處理
- 維持現有 JWT + SessionStore + MFA 流程，不做破壞式調整

### 這一階段刻意不做
- AD 群組 / OU 對應角色同步
- 使用 service account 搜尋使用者後再 bind
- Kerberos / SAML / OIDC SSO

---

## 設定結構

`backend/appsettings*.json`：

```json
{
  "Auth": {
    "Ad": {
      "Enabled": true,
      "Mode": "ldap",
      "MockUsersJson": "{}",
      "Ldap": {
        "Host": "ad01.bank.local",
        "Port": 636,
        "UseSsl": true,
        "StartTls": false,
        "IgnoreCertificateErrors": false,
        "BindDnTemplate": "{email}",
        "UpnDomain": "bank.local",
        "ConnectTimeoutSeconds": 5,
        "OperationTimeoutSeconds": 10
      }
    }
  }
}
```

### 重要欄位說明
- `Auth:Ad:Enabled`
  - 是否啟用 AD 驗證邏輯。
- `Auth:Ad:Mode`
  - `mock`：走 `MockUsersJson`
  - `ldap`：走 LDAP bind
- `Auth:Ad:Ldap:BindDnTemplate`
  - 支援 `{email}`、`{username}` 代入。
  - 例如：`{username}@bank.local` 或 `CN={username},OU=Users,DC=bank,DC=local`
- `Auth:Ad:Ldap:UpnDomain`
  - 當輸入不是 email（不含 `@`）時，會補成 `username@UpnDomain`。

---

## 登入流程行為（AD 帳號）

1. 先檢查本地 lockout 狀態
2. 呼叫 AD 驗證（mock/ldap）
3. 結果分流：
   - `Success`：進入既有 MFA / token / session 流程
   - `InvalidCredentials`：視為登入失敗，沿用既有失敗次數與鎖定機制
   - `DirectoryUnavailable` / `Misconfigured`：回應 `503`，不累計失敗次數

這樣可以避免「目錄服務故障」時，把大量合法帳號誤鎖。

---

## 安全建議

- 生產環境請啟用 `UseSsl=true` 或 `StartTls=true`。
- `IgnoreCertificateErrors` 僅限短期測試；正式環境必須為 `false`。
- 建議把 LDAP 連線參數放到環境變數或 secret manager，不直接寫死在版本庫。

---

## 與既有模型相容性

本次變更不影響：
- JWT 內容與簽發方式
- SessionStore（token 撤銷 / revocation）
- MFA 啟用與政策檢查
- Admin 端既有使用者流程

因此可以在不重做身份系統的前提下，逐步導入真實 AD。

---

## 建議的下一步（PR 續作）

1. 新增「LDAP 健康檢查／連線測試」admin endpoint（不含密碼）
2. 新增 service account + search + bind 雙步驟模式，降低 DN/UPN 差異問題
3. 導入 AD 群組到本地角色映射（最少 Admin / Supervisor）
4. 補強整合測試（可用 docker/openldap 測試容器）
