# bank-reporting-platform

銀行／金融申報平台，用來處理報表定義、申報送審、審核、歷史查詢、匯出、通知與稽核追蹤。

## 這個專案做什麼
- JWT + 安全 Cookie 的登入驗證
- MFA / TOTP 支援
- 報表定義與版本管理
- 送審流程：草稿 → 待審 → 審核 → 通過／退回
- 歷史查詢與下載（JSON／CSV／XLSX）
- 儀表板進度追蹤
- 提醒執行
- 金鑰管理
- 通知與稽核紀錄
- 最小可用的管理介面

## 目前狀態
這個 repo 仍在持續開發中，但已經包含可運作的後端 API、持久化、安全基礎、匯出功能，以及最小管理前端。

## 本機開發
實作細節請看 backend 與 frontend 資料夾。

## 備註
- Repo 名稱：`bank-reporting-platform`
- 這是新專案，不會沿用舊的 legacy repo
