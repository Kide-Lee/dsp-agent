#!/usr/bin/env bash
# 把聊天窗 prefab 打成 Windows 的 AssetBundle。
#
# 为什么用批处理模式而不是让 Unity 常驻：打包是纯机械步骤，批处理可复现、
# 不进 GUI、也不会污染你正在编辑的会话状态。
#
# 注意：批处理会**独占工程**，运行前请关闭 Unity 编辑器。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/dsp-game"
UNITY_VERSION="${UNITY_VERSION:-2022.3.62f3}"
UNITY="${UNITY:-$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity}"

# 默认只打包，绝不重建 prefab（窗口 prefab 是手工维护的，整棵重建会冲掉人的改动）。
# 传 --add-parts 会先跑一次"只做加法"的移植工具（幂等：加/刷新 Header、正文、信标待办输入框）。
METHOD="IcarusII.EditorTools.IcarusIIBundleBuilder.BuildAll"
if [ "${1:-}" = "--add-parts" ]; then
  METHOD="IcarusII.EditorTools.IcarusIIBundleBuilder.AddPartsAndBuild"
fi

DEST_ASSETS="$PROJ/Assets/IcarusII/Editor"
LOG="$ROOT/.deps/unity-bundle-build.log"
OUT="$PROJ/Build/IcarusIIBundle"

die() { echo "错误：$*" >&2; exit 1; }

[ -x "$UNITY" ] || die "找不到 Unity：$UNITY"
[ -d "$PROJ" ] || die "找不到工程：$PROJ"
[ -e "$PROJ/Temp/UnityLockfile" ] && die "Unity 正在运行并独占该工程，请先关闭编辑器"

mkdir -p "$DEST_ASSETS" "$(dirname "$LOG")"
cp "$ROOT"/mod/unity/Editor/*.cs "$DEST_ASSETS/"
echo "==> 已同步编辑器脚本到 $DEST_ASSETS"

echo "==> 以批处理模式调用 Unity（$UNITY_VERSION），日志：$LOG"
# 不加 -nographics：打包 UI 资源时保留图形栈更稳（不显示窗口，但仍初始化）
set +e
"$UNITY" -batchmode -quit -projectPath "$PROJ" \
  -executeMethod "$METHOD" \
  -logFile "$LOG"
RC=$?
set -e

if [ $RC -ne 0 ]; then
  echo "!! Unity 退出码 $RC，日志末尾："
  tail -40 "$LOG" || true
  exit $RC
fi

BUNDLE="$OUT/icarusii-ui"
[ -f "$BUNDLE" ] || { tail -40 "$LOG" || true; die "没有产物：$BUNDLE"; }

# prefab 位于 dsp-game/（已 gitignore），所以回存一份到仓库以便版本化与复现。
# 它引用了游戏贴图的 GUID，回存保留 .meta 才能在恢复后不丢引用。
# button-agent 是 Game Menu 的聊天入口按钮；console.png 是它自己的图标
# （原路径 Assets/IcarusII/Resources/console.png，回存时放平在同一目录）。
SNAP="$ROOT/mod/unity/Prefab"
mkdir -p "$SNAP"
for f in \
  "$PROJ/Assets/IcarusII/ChatWindow.prefab" \
  "$PROJ/Assets/IcarusII/ChatWindow.prefab.meta" \
  "$PROJ/Assets/IcarusII/button-agent.prefab" \
  "$PROJ/Assets/IcarusII/button-agent.prefab.meta" \
  "$PROJ/Assets/IcarusII/Resources/console.png" \
  "$PROJ/Assets/IcarusII/Resources/console.png.meta"; do
  [ -f "$f" ] && cp "$f" "$SNAP/"
done
echo "==> 已回存 prefab 快照到 $SNAP"

SIZE=$(stat -c%s "$BUNDLE")
echo "==> AssetBundle 就绪：$BUNDLE（$((SIZE/1024)) KB）"
echo "==> 平台：StandaloneWindows64（与游戏 DSPGAME.exe 一致）"
