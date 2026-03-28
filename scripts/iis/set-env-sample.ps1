param(
    [string]$Root = 'D:\Sites\BankReporting',
    [string]$JwtSecret = '請替換成至少32字元的高熵秘密',
    [string]$MasterKey = '請替換成32 bytes或base64(32 bytes)',
    [string]$PersistenceProvider = 'sqlserver',
    [string]$ConnectionString = 'Server=sql01;Database=BankReporting;Trusted_Connection=True;TrustServerCertificate=True'
)

# 中文註解：這支腳本只是示範如何把 IIS/Windows 的環境變數樣本列出來
$envPath = Join-Path $Root 'env.sample.txt'
$lines = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    'ASPNETCORE_FORWARDEDHEADERS_ENABLED=true',
    "JWT_SECRET=$JwtSecret",
    "REPORTING_MASTER_KEY=$MasterKey",
    "PERSISTENCE_PROVIDER=$PersistenceProvider",
    "ConnectionStrings__Default=$ConnectionString",
    'JWT_ISSUER=bank-reporting',
    'JWT_AUDIENCE=bank-reporting-web',
    'JWT_TTL_MINUTES=30'
)

New-Item -ItemType Directory -Force -Path $Root | Out-Null
Set-Content -Path $envPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8
Write-Host "已輸出樣本：$envPath"
Write-Host '請把內容搬到 IIS Site / App Pool 對應的 Environment Variables 或 web.config <environmentVariables> 區塊。'
