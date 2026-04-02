param(
    [string]$SiteName = 'BankReporting-Web',
    [string]$ApiSiteName = 'BankReporting-Api'
)

# 中文註解：這支腳本不做破壞性修改，只輸出部署前檢查清單
Write-Host '=== IIS 部署前檢查清單 ==='
Write-Host '1) 已安裝 .NET 10 ASP.NET Core Hosting Bundle'
Write-Host '2) 已安裝 IIS 與 Static Content / Default Document / Request Filtering'
Write-Host '3) 已安裝 URL Rewrite 與 ARR，且 ARR Proxy 已啟用（若採反向代理）'
Write-Host '4) 已建立 App Pool：' $ApiSiteName '與' $SiteName
Write-Host '5) 後端 publish 目錄存在，且 web.config 正確'
Write-Host '6) 已完成 HTTPS 443 binding 與憑證（deploy-frontend 不會自動綁憑證，需手動/IIS Manager/netsh）'
Write-Host '7) 已設定環境變數：JWT_SECRET、REPORTING_MASTER_KEY'
Write-Host '8) 若用 JSON persistence，data 目錄有寫入權限'
Write-Host '9) 若用 SQL Server，連線字串與 DB 權限已測試'
Write-Host '10) 前端 API Base 已設定為 /api（推薦）或 API 子網域'
Write-Host '11) 已用瀏覽器驗證 /health、登入、MFA、logout、audit logs'
