# bank-reporting-platform 建置與本機啟動指南（繁體中文）

> 這份文件是給開發者的「可直接照做」版本，內容以目前 repo 實作與 README 為準。

## 1) 先決條件（Prerequisites）

- 作業系統：macOS / Linux / Windows（能跑 .NET 與 Node.js）
- .NET SDK：`10.0.x`
- Node.js + npm：可執行 frontend build（目前 repo 用到 `npm run build`）
- （選用）SQL Server：當你要切到 SQL 持久化模式時需要

快速檢查：

```bash
dotnet --version
node --version
npm --version
```

---

## 2) 專案結構重點

- 後端 API：`backend/`
- 後端測試：`backend.tests/`
- 前端：`frontend/`
- SQL migration：`backend/database/sqlserver/`
- Solution：`BankReporting.sln`

---

## 3) 後端建置與測試

在 repo root（`bank-reporting-platform/`）執行：

```bash
dotnet restore BankReporting.sln
dotnet build BankReporting.sln -c Release --no-restore
dotnet test backend.tests/BankReporting.Tests.csproj -c Release --no-build
```

> 目前實測可通過（`17` tests passed）。

---

## 4) 前端建置

```bash
cd frontend
npm ci
npm run build
```

目前 `build` script 會輸出：`frontend build ok`。

---

## 5) 設定檔與環境變數（appsettings / env）

ASP.NET Core 設定覆蓋順序（後者蓋前者）：

1. `backend/appsettings.json`
2. `backend/appsettings.{Environment}.json`（例如 Development）
3. 環境變數（Environment Variables）

### 5.1 本機最小可跑設定（Development）

最常見做法：

```bash
export ASPNETCORE_ENVIRONMENT=Development
```

`appsettings.Development.json` 已提供 dev 用 JWT secret。

### 5.2 生產或非 Development 環境必備

若不是 Development，請至少提供：

- `Security__Jwt__Secret`（或舊 key `JWT_SECRET`）

否則啟動會因 JWT secret 不足直接失敗（fail-fast）。

### 5.3 建議用新式 key（仍相容舊 key）

- JWT：
  - `Security__Jwt__Secret`
  - `Security__Jwt__Issuer`
  - `Security__Jwt__Audience`
  - `Security__Jwt__TtlMinutes`
- 持久化：
  - `Persistence__Provider`（`json` / `sqlserver`）
  - `ConnectionStrings__Default`
  - `Persistence__SqlServer__MigrationsPath`

> 舊版 env（如 `JWT_SECRET`、`PERSISTENCE_PROVIDER`、`SQLSERVER_CONNECTION_STRING`）目前仍可用於相容。

---

## 6) SQL / Migration 啟動注意事項

切換 SQL Server 持久化時，至少設定：

```bash
export Persistence__Provider=sqlserver
export ConnectionStrings__Default='Server=localhost;Database=BankReporting;User Id=sa;Password=<your-password>;Encrypt=True;TrustServerCertificate=True'
# 需要自訂路徑時才設；預設會使用 backend/database/sqlserver
# export Persistence__SqlServer__MigrationsPath='database/sqlserver'
```

目前 migration source of truth：

- `backend/database/sqlserver/0001_create_app_state_snapshots.sql`
- `backend/database/sqlserver/0002_seed_initial_snapshot_row.sql`
- `backend/database/sqlserver/0003_create_schema_migrations.sql`

啟動流程會：

1. 檢查/建立 `dbo.SchemaMigrations`
2. 讀取 migration 檔案並比對 checksum
3. 套用未執行 migration
4. 若 DB 狀態與程式 migration 不一致（缺檔、checksum 改變、版本漂移）會 **fail-fast 停止啟動**

---

## 7) 本機啟動（Local Run）

### 7.1 JSON persistence（建議開發預設）

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://127.0.0.1:5179
dotnet run --project backend
```

健康檢查：

```bash
curl http://127.0.0.1:5179/health
```

預期回傳：

```json
{"status":"ok","ts":"..."}
```

### 7.2 前端（靜態資源）

目前 repo 只定義 build script，若你要改前端程式碼，先確保：

```bash
cd frontend
npm run build
```

---

## 8) 常用驗證 / 檢查指令

### 8.1 一次跑完整檢查

```bash
dotnet restore BankReporting.sln && \
dotnet build BankReporting.sln -c Release --no-restore && \
dotnet test backend.tests/BankReporting.Tests.csproj -c Release --no-build && \
cd frontend && npm ci && npm run build
```

### 8.2 只驗證後端 API 是否活著

```bash
curl -sS http://127.0.0.1:5179/health | jq .
```

（沒有 `jq` 也可直接 `curl`）

### 8.3 常見啟動失敗排查

1. `JWT_SECRET must be configured...`
   - 你可能在 Production 環境啟動但沒給 JWT secret。
   - 解法：設定 `ASPNETCORE_ENVIRONMENT=Development`（本機）或提供 `Security__Jwt__Secret`。

2. SQL 模式啟動失敗（connection/migration）
   - 先檢查 `ConnectionStrings__Default`
   - 再檢查 migration 路徑與檔案是否存在
   - 確認 DB 使用者有建表/寫入權限

---

## 9) 參考文件

- 專案總覽：`README.md`
- AD/LDAP 第一階段：`docs/AD_LDAP_INTEGRATION_SLICE1.md`
- Production readiness：`docs/PRODUCTION_READINESS_STATUS.md`

---

## 10) 本文件驗證紀錄（2026-03-28）

以下命令已在本 repo 實際執行：

- `dotnet restore BankReporting.sln`
- `dotnet build BankReporting.sln -c Release --no-restore`
- `dotnet test backend.tests/BankReporting.Tests.csproj -c Release --no-build`（17 passed）
- `cd frontend && npm ci && npm run build`（`frontend build ok`）
- `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5179 dotnet run --project backend` + `/health` 檢查
