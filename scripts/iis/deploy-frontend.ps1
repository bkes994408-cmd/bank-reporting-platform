param(
    [string]$SiteName = 'BankReporting-Web',
    [string]$AppPoolName = 'BankReporting-Web-AppPool',
    [string]$SiteRoot = 'D:\Sites\BankReporting\frontend',
    [string]$HostName = '',
    [int]$Port = 443,
    [string]$ApiBaseUrl = '/api',
    [switch]$UseReverseProxy,
    [string]$BackendOrigin = 'http://127.0.0.1:5001'
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

if (-not (Test-Path $SiteRoot)) { throw "找不到前端目錄：$SiteRoot" }

$index = Join-Path $SiteRoot 'index.html'
if (-not (Test-Path $index)) { throw '前端站台根目錄必須包含 index.html' }

# 中文註解：前端是靜態頁，IIS 只需提供靜態內容與必要的 rewrite 規則
if (-not (Get-WebAppPoolState -Name $AppPoolName -ErrorAction SilentlyContinue)) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value Integrated
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value ApplicationPoolIdentity
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value $false

$webConfigPath = Join-Path $SiteRoot 'web.config'
if (-not (Test-Path $webConfigPath)) {
    $sample = Join-Path $PSScriptRoot 'web.config.frontend.sample'
    Copy-Item $sample $webConfigPath -Force
}

$content = Get-Content $index -Raw
$content = $content -replace 'value="http://localhost:5000"', ('value="{0}"' -f $ApiBaseUrl)
Set-Content -Path $index -Value $content -Encoding UTF8

if ($UseReverseProxy) {
    [xml]$webConfigXml = Get-Content $webConfigPath -Raw
    $rewriteRule = $webConfigXml.configuration.'system.webServer'.rewrite.rules.rule |
        Where-Object { $_.name -eq 'ReverseProxyApi' }

    if (-not $rewriteRule) {
        throw "web.config 找不到 ReverseProxyApi 規則：$webConfigPath"
    }

    $backendOriginTrimmed = $BackendOrigin.TrimEnd('/')
    $rewriteRule.action.url = "$backendOriginTrimmed/{R:1}"
    $webConfigXml.Save($webConfigPath)

    Write-Host '已啟用反向代理情境：請確認 IIS 已安裝 URL Rewrite 與 ARR，且 Proxy 已開啟。'
    Write-Host "已將 ReverseProxyApi 上游來源更新為：$backendOriginTrimmed"
    Write-Host "建議將前端 API Base 設成 /api，並讓 /api/* 轉發到 $backendOriginTrimmed"
}

if (Test-Path "IIS:\Sites\$SiteName") {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SiteRoot
} else {
    if ([string]::IsNullOrWhiteSpace($HostName)) {
        New-Website -Name $SiteName -Port $Port -PhysicalPath $SiteRoot -ApplicationPool $AppPoolName | Out-Null
    } else {
        New-Website -Name $SiteName -Port $Port -HostHeader $HostName -PhysicalPath $SiteRoot -ApplicationPool $AppPoolName | Out-Null
    }
}

Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName

Write-Host "已部署前端站台：$SiteName"
Write-Host "建議確認：https://<你的網域>/ 可開啟前端"
