param(
    [string]$SiteName = 'BankReporting-Api',
    [string]$AppPoolName = 'BankReporting-Api-AppPool',
    [string]$PublishRoot = 'D:\Sites\BankReporting',
    [string]$PublishFolder = 'backend',
    [string]$HostName = '',
    [int]$Port = 5001,
    [string]$PhysicalPath = '',
    [string]$BindingIp = '127.0.0.1'
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$sitePath = if ($PhysicalPath) { $PhysicalPath } else { Join-Path $PublishRoot $PublishFolder }
if (-not (Test-Path $sitePath)) { throw "找不到部署目錄：$sitePath" }

# 中文註解：預設綁定 127.0.0.1，避免後端 API 直接對外暴露；若需對外或內網可改 -BindingIp
if (-not (Get-WebAppPoolState -Name $AppPoolName -ErrorAction SilentlyContinue)) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value Integrated
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value AlwaysRunning
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value ApplicationPoolIdentity
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value $false

if (Test-Path "IIS:\Sites\$SiteName") {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $sitePath
} else {
    if ([string]::IsNullOrWhiteSpace($HostName)) {
        New-Website -Name $SiteName -Port $Port -IPAddress $BindingIp -PhysicalPath $sitePath -ApplicationPool $AppPoolName | Out-Null
    } else {
        New-Website -Name $SiteName -Port $Port -IPAddress $BindingIp -HostHeader $HostName -PhysicalPath $sitePath -ApplicationPool $AppPoolName | Out-Null
    }
}

Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName

Write-Host "已部署後端站台：$SiteName"
Write-Host "後端綁定：http://$BindingIp:$Port/"
Write-Host "建議確認：http://$BindingIp:$Port/health"
