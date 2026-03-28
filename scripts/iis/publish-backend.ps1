param(
    [string]$Configuration = 'Release',
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\..\backend\BankReporting.Api.csproj'),
    [string]$PublishRoot = 'D:\Sites\BankReporting',
    [string]$PublishFolder = 'backend'
)

$ErrorActionPreference = 'Stop'

$project = (Resolve-Path $ProjectPath).Path
$publishPath = Join-Path $PublishRoot $PublishFolder
$logPath = Join-Path $PublishRoot 'logs'
$dataPath = Join-Path $PublishRoot 'data'
$keysPath = Join-Path $PublishRoot 'keys'

# 中文註解：先建立正式環境需要的資料夾，避免 publish 後還要手動補
New-Item -ItemType Directory -Force -Path $publishPath, $logPath, $dataPath, $keysPath | Out-Null

Write-Host "[1/3] 還原與發佈後端..."
dotnet publish $project -c $Configuration -o $publishPath

$webConfig = Join-Path $publishPath 'web.config'
if (-not (Test-Path $webConfig)) {
    Write-Warning 'publish 產物中沒有 web.config，請確認已安裝 ASP.NET Core Hosting Bundle。'
}

Write-Host "[2/3] 發佈完成：$publishPath"
Write-Host "[3/3] 建議後續 ACL："
Write-Host "  icacls `"$publishPath`" /grant `"IIS AppPool\\BankReporting-Api-AppPool:(OI)(CI)(RX)`" /T"
Write-Host "  icacls `"$dataPath`" /grant `"IIS AppPool\\BankReporting-Api-AppPool:(OI)(CI)(M)`" /T"
Write-Host "  icacls `"$keysPath`" /grant `"IIS AppPool\\BankReporting-Api-AppPool:(OI)(CI)(M)`" /T"
Write-Host "  icacls `"$logPath`" /grant `"IIS AppPool\\BankReporting-Api-AppPool:(OI)(CI)(M)`" /T"

Write-Host '完成。'
