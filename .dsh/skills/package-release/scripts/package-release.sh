#!/usr/bin/env bash
# 打包 IcarusII 发布产物：dist/pkg（r2modman 配置档/手动安装），可选 Thunderstore zip。
#
#   scripts/package-release.sh                       # 完整包（含依赖版本检查）
#   scripts/package-release.sh --skip-sidecar        # 复用已有 sidecar exe
#   scripts/package-release.sh --with-thunderstore   # 额外打 Thunderstore zip
#   scripts/package-release.sh --with-thunderstore --skip-ui   # zip 不含 UI 资源包
#   scripts/package-release.sh --skip-build          # Thunderstore 阶段不重新编译
#   scripts/package-release.sh --update-deps         # 依赖过期时自动更新并跑 sidecar 校验

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
SKIP_DEP_CHECK=0
UPDATE_DEPS=0

for arg in "$@"; do
  case "$arg" in
    --skip-sidecar) SKIP_SIDECAR=1 ;;
    --with-thunderstore) THUNDER=1 ;;
    --skip-ui) SKIP_UI=1 ;;
    --skip-build) SKIP_BUILD=1 ;;
    --update-deps) UPDATE_DEPS=1 ;;
    --skip-dep-check) SKIP_DEP_CHECK=1 ;;
    -h|--help) sed -n '2,9p' "$SELF"; exit 0 ;;
    *) die "未知参数：$arg" ;;
  esac
done

# 依赖检查：每次打包都跑，避免带着过期依赖发布。
# 只有同大版本可更新项会阻塞；跨大版本（如 Node 运行时）只提示，由人决定。
if [ "$SKIP_DEP_CHECK" = 0 ]; then
  command -v node >/dev/null 2>&1 \
    || die "找不到 node，依赖检查与 sidecar 构建都需要它；把 Node.js 的 bin 目录加入 PATH"
  log "检查依赖版本"
  if ! node scripts/check-deps.mjs --check; then
    [ "$UPDATE_DEPS" = 1 ] \
      || die "依赖不是最新：先跑 node scripts/check-deps.mjs --update 与 verify-changes 再打包，或加 --update-deps 自动更新并校验"
    log "更新依赖"
    node scripts/check-deps.mjs --update
    log "更新后跑 sidecar 校验"
    ( cd sidecar && npm run typecheck && npm test ) \
      || die "依赖更新后校验失败，不要把这次更新带进发布包"
  fi
else
  log "跳过依赖检查（--skip-dep-check）"
fi

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
