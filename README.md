# bank-reporting-platform

銀行／金融申報平台，用來處理報表定義、申報送審、審核、歷史查詢、匯出、通知與稽核追蹤。

## 專案目標
這個專案的目標是把銀行監理申報流程做成一個可持續擴充的平台，讓使用者可以：

- 建立與維護報表定義及版本
- 進行報表填報、送審、審核與退回
- 查詢歷史申報紀錄
- 匯出 JSON / CSV / XLSX 檔案
- 追蹤申報進度與提醒
- 管理金鑰、通知與稽核紀錄
- 提供管理者操作入口

## 目前已完成的功能

### 核心流程
- JWT + 安全 Cookie 登入驗證
- MFA / TOTP 啟用與驗證
- MFA 管理政策（可設定 scope、授權端點強制與不合規 session 撤銷）
- JWT `jti` 撤銷與 session 失效流程
- 密碼變更與密碼歷史重複防止
- 報表定義與版本管理
- 申報流程：草稿 → 待審 → 審核 → 通過／退回
- 歷史查詢與明細查詢
- 歷史資料下載（JSON / CSV / XLSX）
- 儀表板進度追蹤
- 提醒執行
- 金鑰管理
- 通知與稽核紀錄

### 持久化與安全
- JSON persistence
- SQL Server persistence
- JWT 簽章與驗證
- Token SHA-256 hash 保存
- Rate limiting
- 安全標頭 middleware
- AES-256-GCM at-rest 加密

### 管理介面
- 管理前端控制台（模組化單頁模式）
- 登入 / 登出 / session handling
- 總覽頁（KPI + 最近稽核事件）
- 使用者管理（搜尋、篩選、核准 / 拒絕）
- MFA 政策管理（scope / endpoint enforcement / session revocation）
- Session 撤銷操作
- 稽核紀錄查詢與篩選
- 現代化 admin-console 版型（sidebar + card/table 視覺）

## 專案現況
這個 repo 仍在持續開發中，但已經具備：

- 可運作的後端 API
- 可切換的持久化層
- 基本安全基礎
- 匯出與通知功能
- 最小管理前端

## 技術方向
- Backend: .NET / ASP.NET Core
- Persistence: JSON / SQL Server
- Auth: JWT + Cookie + MFA
- Frontend: 最小管理頁面

## 開發狀態摘要
### 已完成
- 可用骨架
- 核心 API
- session / auth
- MFA
- JWT `jti` 撤銷 / token revocation
- 密碼變更與密碼歷史重複防止
- AD mock + LDAP（第一階段）整合
- 匯出
- 通知
- 完整化的 admin console
- SQL Server persistence
- 安全強化

### 尚未完成
- 完整企業級 AD / LDAP（群組對應、service account 搜尋）/ SSO
- 更完整的 Identity 整合
- 正規化 SQL schema
- 更完整前端管理 UI
- 更完整的營運與安全強化

## 本機開發
### 後端
主要程式碼在 `backend/`，測試在 `backend.tests/`。

### 前端
主要入口在 `frontend/`。

目前前端已拆分為：
- `frontend/index.html`：頁面骨架與語意化區塊
- `frontend/src/styles.css`：集中樣式（admin-console 風格）
- `frontend/src/main.js`：事件註冊與畫面流程
- `frontend/src/api.js`、`frontend/src/dom.js`、`frontend/src/state.js`：API、DOM 工具與狀態管理

## 設定檔與環境變數
專案已改為標準 ASP.NET Core 設定流程，載入順序如下（後者覆蓋前者）：
1. `backend/appsettings.json`
2. `backend/appsettings.{Environment}.json`（例如 Development）
3. Environment Variables

### 內建設定檔
- `backend/appsettings.json`：共用且可提交的安全預設值（不含密鑰）
- `backend/appsettings.Development.json`：本機開發用預設（僅 dev 假資料）
- `backend/appsettings.Example.json`：給部署與新環境初始化參考的範本

### 重要安全原則
- **不要把真實密鑰寫進 Git 版本控制**。
- 生產環境請用環境變數（或 Secret Manager / Key Vault）覆蓋，例如：
  - `Security__Jwt__Secret`
  - `REPORTING_MASTER_KEY`
  - `ConnectionStrings__Default`

### 相容舊版環境變數（仍支援）
為了平滑遷移，目前仍可使用舊 key，系統會自動 fallback：
- JWT：`JWT_SECRET`、`JWT_ISSUER`、`JWT_AUDIENCE`、`JWT_TTL_MINUTES`
- Persistence：`PERSISTENCE_PROVIDER`、`PERSISTENCE_FILE`、`SQLSERVER_CONNECTION_STRING`、`SQLSERVER_MIGRATIONS_PATH`
- Security：`ENABLE_HTTPS_REDIRECT`、`ENABLE_FORWARDED_HEADERS`、`FORWARDED_HEADERS_FORWARD_LIMIT`、`FORWARDED_HEADERS_TRUSTED_PROXIES`、`FORWARDED_HEADERS_TRUSTED_NETWORKS`
- Notification：`NOTIFICATION_WEBHOOK_URL`
- AD / LDAP：`AD_ENABLED`、`AD_MODE`（`mock`/`ldap`）、`AD_MOCK_USERS_JSON`、`AD_LDAP_HOST`、`AD_LDAP_PORT`、`AD_LDAP_USE_SSL`、`AD_LDAP_STARTTLS`、`AD_LDAP_IGNORE_CERT_ERRORS`、`AD_LDAP_BIND_DN_TEMPLATE`、`AD_LDAP_UPN_DOMAIN`、`AD_LDAP_CONNECT_TIMEOUT_SECONDS`、`AD_LDAP_OPERATION_TIMEOUT_SECONDS`

### SQL Server 模式
- 建議設定：
  - `Persistence__Provider=sqlserver`
  - `ConnectionStrings__Default=<your-connection-string>`
- migration SQL 檔案（source of truth）：
  - `backend/database/sqlserver/0001_create_app_state_snapshots.sql`
  - `backend/database/sqlserver/0002_seed_initial_snapshot_row.sql`
  - `backend/database/sqlserver/0003_create_schema_migrations.sql`
- 啟動時會自動依序執行未套用 migration，並寫入 `dbo.SchemaMigrations`（migration id / script name / SHA-256 checksum / applied time）。
- 若資料庫 migration 與程式碼不一致（缺檔或 checksum 變更），服務會 fail-fast 停止啟動。

## MFA 管理政策 API（第一階段）
- `GET /admin/security/mfa-policy`
  - 讀取目前 MFA 管理政策（僅 Admin）
- `PUT /admin/security/mfa-policy`
  - 更新政策（僅 Admin）
  - 請求欄位：
    - `scope`: `Disabled` / `AdminOnly` / `AdminAndSupervisor` / `AllUsers`
    - `enforceOnPrivilegedEndpoints`: 是否在授權端點強制 MFA 合規
    - `revokeNonCompliantSessions`: 是否立即撤銷不合規使用者 sessions

> 備註：第一階段採「授權端點強制」，不改動現有 JWT / session 發放模型，降低相容性風險。

## AD / LDAP 整合（第一階段）
- 支援兩種模式：
  - `mock`：延續既有本機開發假資料帳密
  - `ldap`：以 LDAP bind 驗證 AD 帳密（不改動既有 JWT/session 發放模型）
- 主要設計重點：
  - AD 帳號 (`IsAdUser=true`) 登入時走目錄服務驗證
  - LDAP 服務不可用或設定錯誤時，回應 `503`，且**不累計**失敗次數，避免誤鎖帳號
  - 單純帳密錯誤仍沿用既有鎖定策略（5 次失敗鎖 30 分鐘）
- 建議：
  - 生產環境使用 `UseSsl=true` 或 `StartTls=true`
  - `IgnoreCertificateErrors=true` 僅限測試環境，避免中間人風險

## 驗證
目前已通過：
- `dotnet test`
- `dotnet test backend.tests/BankReporting.Tests.csproj`
- 前端 build（`npm run build`）

## 備註
- Repo 名稱：`bank-reporting-platform`
- 這是新專案，不沿用舊的 legacy repo
- AD/LDAP 第一階段細節請見：`docs/AD_LDAP_INTEGRATION_SLICE1.md`
