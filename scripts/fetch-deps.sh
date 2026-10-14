#!/usr/bin/env bash
# 拉取构建期依赖（目前只有 BepInEx 的参考程序集）。
#
# 只需要跑一次。产物放在 <repo>/.deps/bepinex，仅供**编译**引用；
# 真正安装到游戏目录由 scripts/install.sh（玩家侧）或 scripts/dev-install.sh（开发侧）负责。
#
# 版本锁定说明：5.4.23.5 的 changelog 里含
#   "Fix for unity 2022.3.62f3 not having get_graphicsDeviceID"
# 而本项目的目标游戏正是 Unity 2022.3.62f3c1，所以必须用这一版。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BEPINEX_VERSION="${BEPINEX_VERSION:-5.4.23.5}"
DEPS_DIR="$ROOT/.deps"
TARGET="$DEPS_DIR/bepinex"
ZIP="$DEPS_DIR/BepInEx_win_x64_${BEPINEX_VERSION}.zip"
URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }

if [[ -f "$TARGET/BepInEx/core/BepInEx.dll" ]]; then
  log "BepInEx $BEPINEX_VERSION 已存在：$TARGET"
  exit 0
fi

mkdir -p "$DEPS_DIR"

if [[ ! -f "$ZIP" ]]; then
  log "下载 BepInEx $BEPINEX_VERSION (win_x64)"
  curl -fsSL -o "$ZIP" "$URL"
fi

log "解压到 $TARGET"
rm -rf "$TARGET"
mkdir -p "$TARGET"
unzip -oq "$ZIP" -d "$TARGET"

for f in winhttp.dll doorstop_config.ini BepInEx/core/BepInEx.dll BepInEx/core/0Harmony.dll; do
  [[ -e "$TARGET/$f" ]] || { echo "解压后缺少 $f" >&2; exit 1; }
done

log "完成：$TARGET"
