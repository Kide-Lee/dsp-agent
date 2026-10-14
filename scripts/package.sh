#!/usr/bin/env bash
# 生成可分发目录 dist/pkg/DSPAgent/。
#
#   scripts/package.sh                 # 默认 Release + windows-x64 sidecar
#   SKIP_SIDECAR=1 scripts/package.sh  # 只打 mod（sidecar 已存在时省时间）

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="${CONFIG:-Release}"
PKG="$ROOT/dist/pkg/DSPAgent"
PLUGIN="$PKG/BepInEx/plugins/DSPAgent"

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }

log "清理 $PKG"
rm -rf "$ROOT/dist/pkg"
mkdir -p "$PLUGIN/sidecar" "$PKG/docs"

log "编译 mod"
CONFIG="$CONFIG" "$ROOT/scripts/build-mod.sh"
cp "$ROOT/mod/src/DSPAgent/bin/$CONFIG/DSPAgent.dll" "$PLUGIN/"

EXE="$ROOT/dist/dsp-agent-sidecar.exe"
if [[ "${SKIP_SIDECAR:-0}" != "1" || ! -f "$EXE" ]]; then
  log "构建 sidecar 自包含可执行文件"
  "$ROOT/scripts/build-sidecar.sh" windows-x64
fi
[[ -f "$EXE" ]] || { echo "缺少 $EXE" >&2; exit 1; }
cp "$EXE" "$PLUGIN/sidecar/"

log "复制文档与安装脚本"
cp "$ROOT/scripts/install.sh" "$PKG/install.sh"
chmod +x "$PKG/install.sh"
cp "$ROOT/README.md" "$PKG/README.md"
cp "$ROOT/docs/protocol-v1.md" "$PKG/docs/"
cp "$ROOT/docs/m0-verification.md" "$PKG/docs/" 2>/dev/null || true

log "打包内容："
( cd "$PKG" && find . -type f -printf '  %-52p %8s\n' 2>/dev/null | sort ) || ( cd "$PKG" && find . -type f | sort )

TOTAL=$(du -sh "$PKG" | cut -f1)
log "完成：$PKG（$TOTAL）"
