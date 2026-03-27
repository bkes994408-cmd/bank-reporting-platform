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
- 管理前端控制台（單頁模式）
- 登入 / 登出 / session handling
- 總覽頁（KPI + 最近稽核事件）
- 使用者管理（搜尋、篩選、核准 / 拒絕）
- Session 撤銷操作
- 稽核紀錄查詢與篩選

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
- AD mock 整合
- 匯出
- 通知
- 完整化的 admin console
- SQL Server persistence
- 安全強化

### 尚未完成
- 真實 AD / LDAP / SSO
- 更完整的 Identity 整合
- 正規化 SQL schema
- 更完整前端管理 UI
- 更完整的營運與安全強化

## 本機開發
### 後端
主要程式碼在 `backend/`，測試在 `backend.tests/`。

### 前端
主要入口在 `frontend/`。

## 環境變數
### 必要
- `JWT_SECRET`
- `REPORTING_MASTER_KEY`

### SQL Server 模式
- `PERSISTENCE_PROVIDER=sqlserver`
- `ConnectionStrings__Default` 或 `SQLSERVER_CONNECTION_STRING`
- migration SQL 檔案（source of truth）：
  - `backend/database/sqlserver/0001_create_app_state_snapshots.sql`
  - `backend/database/sqlserver/0002_seed_initial_snapshot_row.sql`
  - `backend/database/sqlserver/0003_create_schema_migrations.sql`
- 啟動時會自動依序執行未套用 migration，並寫入 `dbo.SchemaMigrations`（含 migration id / script name / SHA-256 checksum / applied time）。
- 啟動 mismatch guard：若資料庫記錄的 migration 在程式碼中不存在，或已套用 migration 的 SQL 檔 checksum 改變，服務會在啟動階段直接 fail-fast，避免在不一致 schema 上繼續運行。

### 選用
- `JWT_ISSUER`
- `JWT_AUDIENCE`
- `JWT_TTL_MINUTES`
- `NOTIFICATION_WEBHOOK_URL`
- `SQLSERVER_MIGRATIONS_PATH`（選填，預設為執行檔旁 `database/sqlserver`）
- `ENABLE_HTTPS_REDIRECT`（預設：Development 關閉、其他環境啟用）
- `ENABLE_FORWARDED_HEADERS`（預設：`true`，可在非反向代理環境關閉）
- `FORWARDED_HEADERS_TRUSTED_PROXIES`（逗號分隔 IP，例如 `10.0.0.2,10.0.0.3`）
- `FORWARDED_HEADERS_TRUSTED_NETWORKS`（逗號分隔 CIDR，例如 `10.0.0.0/8,192.168.0.0/16`）

## 驗證
目前已通過：
- `dotnet test`
- `dotnet test backend.tests/BankReporting.Tests.csproj`
- 前端 build（`npm run build`）

## 備註
- Repo 名稱：`bank-reporting-platform`
- 這是新專案，不沿用舊的 legacy repo
