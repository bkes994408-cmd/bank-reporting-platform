# IIS 環境變數範例

這份範例對應 `backend/Program.cs` 與 `backend/Infrastructure.cs` 的實際讀取方式。

## 必要環境變數

```powershell
JWT_SECRET=請放至少32字元的高熵秘密
REPORTING_MASTER_KEY=請放32 bytes的字串或base64(32 bytes)
```

## 建議環境變數

```powershell
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
PERSISTENCE_PROVIDER=sqlserver
JWT_ISSUER=bank-reporting
JWT_AUDIENCE=bank-reporting-web
JWT_TTL_MINUTES=30
```

## SQL Server 模式

二選一：

```powershell
ConnectionStrings__Default=Server=sql01;Database=BankReporting;Trusted_Connection=True;TrustServerCertificate=True
```

或：

```powershell
SQLSERVER_CONNECTION_STRING=Server=sql01;Database=BankReporting;User Id=bank_reporting;Password=***;TrustServerCertificate=True
```

## JSON persistence 模式

若先不接 SQL Server，可用：

```powershell
PERSISTENCE_PROVIDER=json
PERSISTENCE_FILE=D:\Sites\BankReporting\data\app-state.json
```

## 建議資料夾

```powershell
BANK_REPORTING_ROOT=D:\Sites\BankReporting
BANK_REPORTING_DATA=D:\Sites\BankReporting\data
BANK_REPORTING_KEYS=D:\Sites\BankReporting\keys
BANK_REPORTING_LOGS=D:\Sites\BankReporting\logs
```

## 前端部署時的 API Base 建議

- 同網域 + `/api` 反向代理：`/api`
- 分站方案：`https://api.reporting.example.com`

## 備註

- `access_token` cookie 會使用 `Secure=true` 與 `SameSite=Strict`
- 因此正式環境請務必使用 HTTPS
- 如果未來要讓 IIS 反向代理保留真實協定/來源，請在後端補上 forwarded headers
