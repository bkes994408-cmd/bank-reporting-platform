# bank-reporting-platform

Banking / regulatory reporting platform for handling report definitions, submissions, approvals, history, exports, notifications, and audit tracking.

## What this project does
- Authentication with JWT + secure cookie session support
- MFA / TOTP support
- Report definition and version management
- Submission workflow: draft → pending → review → approved / rejected
- History queries and downloads (JSON / CSV / XLSX)
- Dashboard progress tracking
- Reminder execution
- Key management
- Notifications and audit logs
- Minimal admin UI

## Current state
This repo is in active development. It already includes a working backend API, persistence, security baseline, export support, and a minimal admin frontend.

## Local development
See the backend and frontend folders for implementation details.

## Notes
- Repo name: `bank-reporting-platform`
- Original legacy repo is intentionally not used for this new project
