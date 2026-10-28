#!/usr/bin/env bash
# 按改动范围跑 IcarusII 的验证矩阵。
#
#   scripts/verify.sh                 # mod + sidecar + protocol
#   scripts/verify.sh mod            # 只编译 mod
#   scripts/verify.sh sidecar        # typecheck + build + 离线回归
#   scripts/verify.sh protocol       # C# 协议用例
#   scripts/verify.sh e2e            # 起临时 sidecar 跑端到端
#   scripts/verify.sh all            # mod + sidecar + protocol
#
# 环境变量：CONFIG（构建配置，默认 Release）
#           SIDECAR_E2E_PORT / SIDECAR_E2E_TOKEN（默认 8799 / testtoken）

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
cd "$ROOT"

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[31m!!\033[0m %s\n' "$*" >&2; exit 1; }

run_mod() {
  log "mod：scripts/build-mod.sh（CONFIG=${CONFIG:-Release}）"
  scripts/build-mod.sh
}

run_sidecar() {
  log "sidecar：npm test（typecheck + build + 离线回归）"
  ( cd sidecar && npm test )
}

run_protocol() {
  log "protocol：dotnet run --project tests/ProtocolTests"
  dotnet run --project tests/ProtocolTests
}

run_e2e() {
  local port="${SIDECAR_E2E_PORT:-8799}"
  local token="${SIDECAR_E2E_TOKEN:-testtoken}"
  local entry="sidecar/dist/index.js"

  [ -f "$entry" ] || die "缺少 $entry；先跑 verify.sh sidecar（或 cd sidecar && npm run build）"

  log "e2e：在 127.0.0.1:$port 起临时 sidecar（echo agent）"
  node "$entry" serve --port "$port" --token "$token" --agent echo &
  local pid=$!
  trap 'kill '"$pid"' 2>/dev/null || true' EXIT

  # 等端口真的能连上，避免固定 sleep 的竞态
  local i
  for i in $(seq 1 60); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
      exec 3>&- 2>/dev/null || true
      break
    fi
    kill -0 "$pid" 2>/dev/null || die "sidecar 提前退出，端口 $port 未监听"
    sleep 0.25
  done

  dotnet run --project tests/ProtocolTests -- e2e "ws://127.0.0.1:$port" "$token"
  log "e2e 通过"
}

scopes=("$@")
if [ ${#scopes[@]} -eq 0 ]; then
  scopes=(mod sidecar protocol)
fi

for scope in "${scopes[@]}"; do
  case "$scope" in
    mod) run_mod ;;
    sidecar) run_sidecar ;;
    protocol) run_protocol ;;
    e2e) run_e2e ;;
    all) run_mod; run_sidecar; run_protocol ;;
    -h|--help)
      sed -n '2,12p' "$SELF"
      exit 0
      ;;
    *) die "未知范围：$scope（可用：mod sidecar protocol e2e all）" ;;
  esac
done

log "全部通过"
