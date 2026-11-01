#!/usr/bin/env bash
# 拉取构建期依赖（目前只有 BepInEx 的参考程序集）。
#
# 只需要跑一次。产物放在 <repo>/.deps/bepinex，仅供**编译**引用；
# 真正部署由 .dsh/skills/update-steam-mod/scripts/update-steam-mod.sh（默认 r2modman 配置档）或 scripts/install.sh（手动安装）负责。
#
# 版本锁定说明：**编译期固定用社区包当前版本**（2026-11-01 为 5.4.17）。
#
# 为什么跟社区包、不跟上游最新（上游 2026-11-01 是 5.4.23.6）：
# BepInEx 会检查插件引用的 BepInEx 版本，"插件目标的版本 > 正在运行的版本" 时会打印
#   Plugin [x] targets a wrong version of BepInEx (…)
# 玩家通过 r2modman / Thunderstore 装的都是 xiaoye97-BepInEx，它至今只有 5.4.17 一个版本；
# 该社区里 400 个依赖 BepInEx 的 mod 最新版全部声明 5.4.17。
# 所以编译目标高于它等于给自己刷一条吓人的警告。
#
# 这个判断不是写死的：scripts/check-deps.mjs 每次打包都会向 Thunderstore 查社区包版本，
# 编译目标高于社区版本会阻塞打包，社区发新版只提示、是否跟进由人决定。
#
# 5.4.23.x 的 changelog 那条 "Fix for unity 2022.3.62f3 not having
# get_graphicsDeviceID" **不影响本项目**：那段代码只在
# Application.isBatchMode 取不到时才会碰 graphicsDeviceID，
# 而本游戏（Unity 2022.3.62f3）两个 getter 都在。
# 2026-10-17 实机验证：BepInEx 5.4.17 + 本插件在 0.10.30.3100 上正常加载。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BEPINEX_VERSION="${BEPINEX_VERSION:-5.4.17}"
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
