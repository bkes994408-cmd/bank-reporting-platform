# Windows Server + IIS 部署指南

> 適用於本專案的 .NET 10 ASP.NET Core API + 靜態前端（`frontend/index.html`）。
> 這份指南以 **Windows Server + IIS** 為主，不以 Docker / Linux / Nginx 為前提。

## 1. 專案現況與部署判讀

先根據 repo 實際結構確認部署型態：

- 後端：`backend/BankReporting.Api.csproj`
  - ASP.NET Core Web API（`Microsoft.NET.Sdk.Web`）
  - 會讀取環境變數：`JWT_SECRET`、`REPORTING_MASTER_KEY`、`PERSISTENCE_PROVIDER`、`SQLSERVER_CONNECTION_STRING` / `ConnectionStrings__Default`、`JWT_ISSUER`、`JWT_AUDIENCE`、`JWT_TTL_MINUTES`
  - 驗證使用 JWT + 安全 Cookie（`access_token`）
  - 目前沒有 `UsePathBase()`，也沒有內建 CORS 設定
- 前端：`frontend/index.html`
  - 單檔靜態管理台，沒有 SPA bundler 依賴
  - 目前 `API Base` 預設值是 `http://localhost:5000`
  - 呼叫 API 時使用 `fetch(..., credentials: 'include')`，且可額外帶 `X-Auth-Token`
- 測試：`dotnet test BankReporting.sln` 可通過
- 前端 build：`npm run build` 只是 smoke test，沒有真正產生 bundle

## 2. 建議的 IIS 拓樸：推薦方案

### 方案 A：前端與後端分開 IIS Site（雙站）

- 前端站：`https://reporting.example.com/`
- 後端站：`https://api.reporting.example.com/`
- 前端直接對 API 子網域發送請求

**優點**
- 設定直觀，前後端可獨立部署與回滾
- API 可獨立做憑證、頻寬、WAF、日誌隔離
- 適合未來多個前端或多個 API 入口

**缺點**
- 會變成跨網域，必須處理 CORS
- Cookie / SameSite / Secure 規則更容易踩雷
- 前端如果仍用 cookie 驗證，跨站情境要特別確認瀏覽器行為

### 方案 B：同一個主網域，前端站 + `/api` 反向代理（單站/雙站混合）

- 使用者看到：`https://reporting.example.com/`
- 前端站在 IIS 服務靜態檔
- `/api/*` 透過 IIS URL Rewrite + ARR 代理到內部後端站（例如 `http://127.0.0.1:5001`）

**優點**
- 同源，前端與 API 同一個站台來源
- 幾乎不需要 CORS
- `access_token` Cookie、`credentials: include`、SameSite 行為最穩定
- 對現有前端硬編碼 `API Base` 的修改最少

**缺點**
- 需要 IIS URL Rewrite / ARR
- 代理設定多一層，但可接受

### 推薦：方案 B

本 repo 的前端目前直接用 `fetch(apiBase + path, credentials: 'include')`，且後端已經以 `access_token` Cookie 與 `X-Auth-Token` 雙路徑設計。若採用 **同網域 + `/api` 代理**：

- 不必處理跨站 CORS
- Cookie 安全屬性較單純
- 更符合此專案目前「管理台 + API」的實際型態
- 也較容易日後把前端 `API Base` 改成預設 `/api`

---

## 3. 部署前準備

### 3.1 安裝主機必要元件

1. **.NET 10 ASP.NET Core Hosting Bundle**
   - 這會安裝 ASP.NET Core Module (ANCM)
   - 沒裝通常會遇到 `500.19`、`502.5`、`HTTP Error 500.31/500.35`

2. **IIS 功能**
   - Web Server (IIS)
   - Static Content
   - Default Document
   - HTTP Errors
   - Request Filtering
   - Logging Tools
   - ASP.NET / .NET Extensibility（若系統要求）

3. **IIS 擴充**
   - URL Rewrite
   - Application Request Routing (ARR)

> 若要用 `/api` 反向代理，ARR 必須啟用 **Proxy**。

### 3.2 建議磁碟路徑

- 前端：`D:\Sites\BankReporting\frontend`
- 後端：`D:\Sites\BankReporting\backend`
- 資料：`D:\Sites\BankReporting\data`
- Data Protection keys：`D:\Sites\BankReporting\keys`
- 日誌：`D:\Sites\BankReporting\logs`

### 3.3 建議服務帳號

- IIS App Pool 身分：`ApplicationPoolIdentity`
- 若後端要存取 SQL Server、檔案、SMB、或共享 key folder，請額外賦予 ACL

---

## 4. 環境變數與設定檔

請先準備後端環境變數。可參考：`docs/ENVIRONMENT_EXAMPLE_IIS.md`

### 必要

- `JWT_SECRET`：至少 32 字元，正式環境請用隨機高熵值
- `REPORTING_MASTER_KEY`：AES-256-GCM at-rest 加密用，必須是 32 bytes（或 base64 的 32 bytes）

### 常用

- `PERSISTENCE_PROVIDER=sqlserver` 或 `json`
- `ConnectionStrings__Default=...`
- `JWT_ISSUER=bank-reporting`
- `JWT_AUDIENCE=bank-reporting-web`
- `JWT_TTL_MINUTES=30`
- `ASPNETCORE_ENVIRONMENT=Production`
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`（若前面有反向代理）

### 前端 API Base

目前前端 `frontend/index.html` 有一個 `API Base` 輸入框，預設為 `http://localhost:5000`。

部署到 IIS 後建議：

- 單網域 / 反向代理方案：改成 `https://reporting.example.com/api`
- 雙站方案：改成 `https://api.reporting.example.com`

---

## 5. IIS App Pool 設定

### 後端 App Pool

- `.NET CLR Version`：`No Managed Code`
- `Managed pipeline mode`：`Integrated`
- `Enable 32-Bit Applications`：`False`
- `Start Mode`：`AlwaysRunning`（建議）
- `Idle Time-out`：可先保留預設或視需求調整

### 前端 App Pool

- 一般靜態網站可共用或用獨立 App Pool
- 若採純靜態檔，.NET CLR 也可用 `No Managed Code`

---

## 6. 兩種部署作法

## 6.1 方案 B（推薦）：前端站 + `/api` 反向代理

### 流程

1. 發佈後端到獨立資料夾，啟動在內部埠，例如 `127.0.0.1:5001`
2. IIS 前端站提供靜態檔
3. URL Rewrite 將 `/api/*` 轉發到後端站
4. 前端 `API Base` 設成 `/api`

### 前端站 web.config 範例

請見 `scripts/iis/web.config.frontend.sample`。

### 後端站 web.config 範例

請見 `scripts/iis/web.config.backend.sample`。

### 優點

- 前端與 API 同源
- 幾乎免 CORS
- Cookie 驗證最穩定
- 對 MFA / Session / SameSite / Secure 影響最小

## 6.2 方案 A：前後端分開 IIS Site

### 流程

1. 前端站綁定 `https://reporting.example.com`
2. 後端站綁定 `https://api.reporting.example.com`
3. 前端 `API Base` 設成 API 子網域
4. 後端加入 CORS（若之後程式碼補強）或至少確保同站來源策略

### 此方案的風險點

- 若前端在不同網域，`access_token` Cookie 可能受 SameSite / 瀏覽器政策影響
- 若未設定正確 CORS，會直接遇到 `CORS blocked`
- 若前端端點與 API 端點憑證或網域不同，瀏覽器可能不送 cookie

---

## 7. 檔案部署步驟

### 7.1 先發佈後端

可使用：

```powershell
pwsh .\scripts\iis\publish-backend.ps1 -Configuration Release -PublishRoot D:\Sites\BankReporting
```

腳本會：
- restore/build/publish 後端
- 輸出到指定資料夾
- 生成（或保留）IIS 需要的 `web.config`

### 7.2 安裝後端到 IIS

```powershell
pwsh .\scripts\iis\deploy-backend.ps1 -SiteName BankReporting-Api -AppPoolName BankReporting-Api-AppPool -PublishRoot D:\Sites\BankReporting
```

### 7.3 安裝前端到 IIS

```powershell
pwsh .\scripts\iis\deploy-frontend.ps1 -SiteName BankReporting-Web -SiteRoot D:\Sites\BankReporting\frontend -BackendBaseUrl http://127.0.0.1:5001 -UseReverseProxy
```

若採雙站，可把 `-UseReverseProxy` 關掉並直接把 `-ApiBaseUrl` 指向 `https://api.reporting.example.com`

---

## 8. Data Protection key persistence

此專案目前沒有明確使用 ASP.NET Core Data Protection API，但正式環境仍建議預先規劃：

- 若日後加入 Cookie 認證或 Anti-Forgery
- 若要多台機器或輪替部署

建議把 keys 放在固定資料夾，例如：`D:\Sites\BankReporting\keys`

同時確保：
- App Pool 身分可讀寫
- 不要放在臨時目錄或會被清掉的 profile 資料夾

---

## 9. ACL 與檔案權限

至少要保證：

- 後端 publish 目錄可讀執行
- `data` 目錄可讀寫（若使用 JSON persistence）
- `logs` 目錄可寫
- `keys` 目錄可讀寫
- SQL Server 連線帳號有正確資料庫權限

建議授權範例：

```powershell
icacls D:\Sites\BankReporting\backend /grant "IIS AppPool\BankReporting-Api-AppPool:(OI)(CI)(RX)" /T
icacls D:\Sites\BankReporting\data /grant "IIS AppPool\BankReporting-Api-AppPool:(OI)(CI)(M)" /T
icacls D:\Sites\BankReporting\keys /grant "IIS AppPool\BankReporting-Api-AppPool:(OI)(CI)(M)" /T
icacls D:\Sites\BankReporting\logs /grant "IIS AppPool\BankReporting-Api-AppPool:(OI)(CI)(M)" /T
```

---

## 10. HTTPS / Bindings

### 必做

- 站台必須使用 HTTPS
- 設定憑證綁定到 IIS Site
- 80 port 建議只做 301 轉址到 HTTPS

### 建議

- 同網域方案：`reporting.example.com`
- 反向代理到後端：`127.0.0.1:5001` 或內網埠，不要直接對外開放

---

## 11. Cookie / JWT / MFA / SameSite / Secure

本 repo 的登入機制特性：

- 後端登入成功時會回傳 JWT，並同時寫入 `access_token` Cookie
- Cookie 設定為：`HttpOnly = true`、`Secure = true`、`SameSite = Strict`
- 前端會 `credentials: include`
- MFA 為 TOTP，登入流程可帶 `mfaCode`

### 部署時的注意點

1. **必須 HTTPS**
   - `Secure=true` 的 cookie 在 HTTP 下不會正常送出
2. **同源更穩定**
   - 建議用同網域 + `/api`
3. **若分網域**
   - 需特別驗證瀏覽器對 cookie / SameSite 的行為
4. **若未來改成跨站 cookie**
   - 需要重新評估 `SameSite=None; Secure`

---

## 12. 驗證流程

### 基本檢查

- `https://reporting.example.com/` 可以打開前端
- `https://reporting.example.com/api/health` 回應 `{ status: 'ok' }`
- 使用 `admin@bank.local / Admin#12345678` 可以登入（測試種子）
- 登入後可以讀到 `/admin/users` 與 `/admin/audit-logs`

### 命令驗證

- `dotnet test BankReporting.sln`
- `npm run build`（前端 smoke test）

### IIS 健檢建議

可先執行：

```powershell
pwsh .\scripts\iis\setup-iis-checklist.ps1 -SiteName BankReporting-Web -ApiSiteName BankReporting-Api
```

---

## 13. 常見錯誤與排查

### 13.1 `500.19`

通常是：
- `web.config` XML 格式錯誤
- 缺少 IIS 模組（URL Rewrite / ASP.NET Core Module）
- 設定段被鎖定

排查：
- 先確認 Hosting Bundle 已安裝
- 用 `appcmd list config` 或 IIS Manager 檢查錯誤行號

### 13.2 `502.5` / `HTTP Error 500.31`

通常是：
- .NET Runtime / Hosting Bundle 不完整
- `web.config` 的 `processPath` 或 `arguments` 錯誤
- App 啟動時拋出例外（例如缺少 `JWT_SECRET` / `REPORTING_MASTER_KEY`）

排查：
- 看 stdout log
- 檢查 Windows Event Viewer
- 檢查環境變數是否存在

### 13.3 CORS

若採分站方案，前端打 API 可能遇到：
- `CORS blocked`
- `credentials` 不允許

處理：
- 優先改成同網域 `/api` 代理
- 或在後端補 CORS policy

### 13.4 Cookie 問題

若登入成功但後續 API 一直 401：
- cookie 沒有送出
- SameSite / Secure 不合
- 網域 / 協定不一致

處理：
- 確認 HTTPS
- 盡量同源
- 檢查瀏覽器 DevTools 的 cookie 與 request headers

### 13.5 路徑問題

若前端部署在子路徑：
- `index.html` 內的 API Base 要相對應修正
- `/api` 轉發規則要避開 static asset

### 13.6 權限問題

若 JSON persistence 寫不進去：
- `data` 資料夾 ACL 不足
- App Pool identity 無法寫入

### 13.7 SQL Server 連線問題

若使用 SQL Server 模式：
- `PERSISTENCE_PROVIDER=sqlserver`
- `ConnectionStrings__Default` 或 `SQLSERVER_CONNECTION_STRING` 必須正確
- SQL 帳號要有建立/寫入 snapshot table 的權限

---

## 14. 建議的正式部署順序

1. 安裝 Hosting Bundle + IIS + Rewrite + ARR
2. 建立資料夾與 ACL
3. 先部署後端並驗證 `/health`
4. 再部署前端
5. 設定 HTTPS binding
6. 驗證登入、MFA、session revoke、audit logs
7. 再切換流量到正式網域

---

## 15. Phase 2 改進建議

- 在後端加入正式的 `UseForwardedHeaders()` 與 CORS policy
- 前端把 API Base 預設改為 `/api`
- 把 Data Protection 與 cookie 認證完全標準化
- 增加健康檢查端點與 IIS 探針路徑
- 加入自動化 DB migration / schema check
- 加入部署後 smoke test 腳本（curl / health / login / audit）

---

## 16. 參考檔案

- `docs/ENVIRONMENT_EXAMPLE_IIS.md`
- `scripts/iis/publish-backend.ps1`
- `scripts/iis/deploy-backend.ps1`
- `scripts/iis/deploy-frontend.ps1`
- `scripts/iis/setup-iis-checklist.ps1`
- `scripts/iis/set-env-sample.ps1`
- `scripts/iis/web.config.backend.sample`
- `scripts/iis/web.config.frontend.sample`
