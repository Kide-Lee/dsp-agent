#!/usr/bin/env bash
# 生成可分发目录 dist/pkg/IcarusII/。
#
#   scripts/package.sh                 # 默认 Release + windows-x64 sidecar
#   SKIP_SIDECAR=1 scripts/package.sh  # 只打 mod（sidecar 已存在时省时间）

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="${CONFIG:-Release}"
PKG="$ROOT/dist/pkg/IcarusII"
PLUGIN="$PKG/BepInEx/plugins/IcarusII"

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }

log "清理 $PKG"
rm -rf "$ROOT/dist/pkg"
mkdir -p "$PLUGIN/sidecar" "$PLUGIN/ui" "$PKG/docs"

log "编译 mod"
CONFIG="$CONFIG" "$ROOT/scripts/build-mod.sh"
cp "$ROOT/mod/src/IcarusII/bin/$CONFIG/IcarusII.dll" "$PLUGIN/"

EXE="$ROOT/dist/icarus-ii-sidecar.exe"
if [[ "${SKIP_SIDECAR:-0}" != "1" || ! -f "$EXE" ]]; then
  log "构建 sidecar 自包含可执行文件"
  "$ROOT/scripts/build-sidecar.sh" windows-x64
fi
[[ -f "$EXE" ]] || { echo "缺少 $EXE" >&2; exit 1; }
cp "$EXE" "$PLUGIN/sidecar/"

# UI AssetBundle（Windows，在 Unity 编辑器里用 Icarus II 菜单或 BuildAll 生成）。
# 聊天窗不再有代码构建兜底，缺 bundle 直接拒绝打包。
BUNDLE="$ROOT/dsp-game/Build/IcarusIIBundle/icarusii-ui"
[ -f "$BUNDLE" ] || {
  echo "缺少 UI AssetBundle：$BUNDLE" >&2
  echo "请先在 Unity 编辑器里执行 Icarus II / Build UI AssetBundle (Windows)" >&2
  exit 1
}
cp "$BUNDLE" "$PLUGIN/ui/"
log "已打入 UI AssetBundle（$(($(stat -c%s "$BUNDLE")/1024)) KB）"

log "复制文档与安装脚本"
cp "$ROOT/scripts/install.sh" "$PKG/install.sh"
chmod +x "$PKG/install.sh"
# 玩家向说明优先用 Thunderstore 版（packaging/thunderstore/README.md），
# 仓库根 README 是开发向的，不适合直接塞给玩家。
if [ -f "$ROOT/packaging/thunderstore/README.md" ]; then
  cp "$ROOT/packaging/thunderstore/README.md" "$PKG/README.md"
else
  cp "$ROOT/README.md" "$PKG/README.md"
fi
cp "$ROOT/THIRD-PARTY.md" "$PKG/" 2>/dev/null || true
cp "$ROOT/LICENSE" "$PKG/" 2>/dev/null || true
cp "$ROOT/docs/protocol-v1.md" "$PKG/docs/"
cp "$ROOT/docs/m0-verification.md" "$PKG/docs/" 2>/dev/null || true
# 调试符号：玩家报错时能给出可读堆栈
cp "$ROOT/mod/src/IcarusII/bin/$CONFIG/IcarusII.pdb" "$PLUGIN/" 2>/dev/null || true

log "打包内容："
( cd "$PKG" && find . -type f -printf '  %-52p %8s\n' 2>/dev/null | sort ) || ( cd "$PKG" && find . -type f | sort )

TOTAL=$(du -sh "$PKG" | cut -f1)
log "完成：$PKG（$TOTAL）"
