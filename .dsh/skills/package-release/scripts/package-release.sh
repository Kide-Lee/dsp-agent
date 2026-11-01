#!/usr/bin/env bash
# 打包 IcarusII 发布产物：dist/pkg（r2modman 配置档/手动安装），可选 Thunderstore zip。
#
#   scripts/package-release.sh                       # 完整包
#   scripts/package-release.sh --skip-sidecar        # 复用已有 sidecar exe
#   scripts/package-release.sh --with-thunderstore   # 额外打 Thunderstore zip
#   scripts/package-release.sh --with-thunderstore --skip-ui   # zip 不含 UI 资源包
#   scripts/package-release.sh --skip-build          # Thunderstore 阶段不重新编译

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
cd "$ROOT"

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[31m!!\033[0m %s\n' "$*" >&2; exit 1; }

SKIP_SIDECAR=0
THUNDER=0
SKIP_UI=0
SKIP_BUILD=0

for arg in "$@"; do
  case "$arg" in
    --skip-sidecar) SKIP_SIDECAR=1 ;;
    --with-thunderstore) THUNDER=1 ;;
    --skip-ui) SKIP_UI=1 ;;
    --skip-build) SKIP_BUILD=1 ;;
    -h|--help) sed -n '2,8p' "$SELF"; exit 0 ;;
    *) die "未知参数：$arg" ;;
  esac
done

log "打包 dist/pkg（SKIP_SIDECAR=$SKIP_SIDECAR）"
SKIP_SIDECAR="$SKIP_SIDECAR" scripts/package.sh

PKG="$ROOT/dist/pkg/IcarusII"
PLUGIN="$PKG/BepInEx/plugins/IcarusII"

[ -f "$PLUGIN/IcarusII.dll" ] || die "缺少 $PLUGIN/IcarusII.dll"
[ -f "$PLUGIN/sidecar/icarus-ii-sidecar.exe" ] || die "缺少 sidecar exe"
[ -f "$PLUGIN/ui/icarusii-ui" ] \
  || log "警告：包里没有 UI bundle，游戏会回退到代码构建的界面（先跑 rebuild-ui-bundle）"

log "包内容："
( cd "$PKG" && find . -type f -printf '  %-56p %9s\n' | sort )
log "总计 $(du -sh "$PKG" | cut -f1) → $PKG"

if [ "$THUNDER" = 1 ]; then
  log "打包 Thunderstore（SKIP_UI=$SKIP_UI）"
  SKIP_UI="$SKIP_UI" SKIP_SIDECAR="$SKIP_SIDECAR" SKIP_BUILD=1 scripts/package-thunderstore.sh
  ls -1 dist/IcarusII-*.zip >/dev/null 2>&1 || die "没有生成 Thunderstore zip"
  log "zip：$(ls -1 dist/IcarusII-*.zip | tr '\n' ' ')"
fi

log "下一步：update-steam-mod（默认 r2modman 配置档，先 --dry-run），或按 docs/publishing.md 上传 Thunderstore"
