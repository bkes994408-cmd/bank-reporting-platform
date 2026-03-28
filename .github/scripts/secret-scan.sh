#!/usr/bin/env bash
set -euo pipefail

# 安全掃描策略：
# - 只掃描 git tracked files，降低 build artifact 噪音
# - 命中即失敗，讓 CI 在最前段阻擋可疑祕密
# Scan tracked files only to avoid false positives from build artifacts.
files=$(git ls-files)

if [[ -z "$files" ]]; then
  echo "No tracked files to scan"
  exit 0
fi

patterns=(
  # 目前先涵蓋常見雲端/平台金鑰樣式；
  # 若新增第三方整合，請同步補上對應 pattern。
  'AKIA[0-9A-Z]{16}'
  'ghp_[A-Za-z0-9]{30,}'
  '-----BEGIN (RSA|EC|DSA|OPENSSH) PRIVATE KEY-----'
)

failed=0
for p in "${patterns[@]}"; do
  if grep -RInE --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj "$p" $files >/tmp/secret_hits.txt 2>/dev/null; then
    echo "[secret-scan] pattern hit: $p"
    cat /tmp/secret_hits.txt
    failed=1
  fi
done

if [[ $failed -ne 0 ]]; then
  echo "[secret-scan] FAILED"
  exit 1
fi

echo "[secret-scan] OK"
