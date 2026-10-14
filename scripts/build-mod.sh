#!/usr/bin/env bash
# 构建 mod 插件。
#
#   scripts/build-mod.sh              # Debug
#   CONFIG=Release scripts/build-mod.sh
#   scripts/build-mod.sh -p:DspGameDir="/path/to/game"

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="${CONFIG:-Release}"

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }

"$ROOT/scripts/fetch-deps.sh"

# NuGet 缓存在受限环境里可能不可写，统一落到仓库内
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.deps/nuget}"

log "编译 VibeDsp ($CONFIG)"
dotnet build "$ROOT/mod/src/VibeDsp/VibeDsp.csproj" \
  -c "$CONFIG" -v q --nologo "$@"

OUT="$ROOT/mod/src/VibeDsp/bin/$CONFIG/VibeDsp.dll"
[[ -f "$OUT" ]] || { echo "未找到产物 $OUT" >&2; exit 1; }
log "产物：$OUT ($(du -h "$OUT" | cut -f1))"
