# Production Readiness Status (2026-03-27)

Source baseline: `docs/SRS_v1.2_銀行監理資料數位申報平台.docx`, `docs/TSD_v1.0_銀行監理資料數位申報平台.docx`

## Completed in this iteration

### 1) JWT revocation / session invalidation
- Added `JwtTokenService` for signed JWT issue/validate with configurable `JWT_SECRET`, `JWT_ISSUER`, `JWT_AUDIENCE`, `JWT_TTL_MINUTES`.
- `SessionStore` now:
  - stores SHA-256 token hashes (not raw token),
  - supports single-token revoke (`RevokeToken`),
  - supports per-user bulk revoke (`RevokeUserSessions`),
  - tracks revoked JWT `jti` and enforces invalidation checks on access.
- Added admin API for force session revocation:
  - `POST /admin/users/{userId}/revoke-sessions`

### 2) Password change + history enforcement
- Added `/auth/change-password` flow safeguards:
  - verify current password,
  - enforce strong password policy,
  - prevent reuse against password history,
  - keep rolling password history window.
- Added/updated smoke tests to assert password history behavior.

### 3) Admin UI expansion
- Expanded `frontend/index.html` admin console capabilities:
  - user approve/reject actions,
  - revoke sessions action,
  - richer admin operational panel tied to new backend endpoints.

### 4) SQL persistence hardening and schema bootstrap
- Added `IStateRepository` abstraction and SQL Server implementation path (`SqlStateRepository`).
- Added DB creation SQL bootstrap (`dbo.AppStateSnapshots`) with transactional upsert (`MERGE`).
- Added SQL-driven schema initialization (`EnsureSchema`) on startup for SQL mode.
- Runtime persistence selection:
  - `PERSISTENCE_PROVIDER=sqlserver` (or `sql`) + `ConnectionStrings:Default` / `SQLSERVER_CONNECTION_STRING`
  - fallback remains JSON persistence.
- Documented next-step target: expand from snapshot persistence toward normalized schema slices (`auth`/`report`/`key`/`audit`).

### 5) Validation status
- `dotnet test` passed: **9/9**.

## Config checklist for secured runtime

Required:
- `JWT_SECRET`
- `REPORTING_MASTER_KEY`

For SQL Server mode:
- `PERSISTENCE_PROVIDER=sqlserver`
- `ConnectionStrings__Default` (or `SQLSERVER_CONNECTION_STRING`)

Optional:
- `JWT_ISSUER`, `JWT_AUDIENCE`, `JWT_TTL_MINUTES`
- `NOTIFICATION_WEBHOOK_URL`
