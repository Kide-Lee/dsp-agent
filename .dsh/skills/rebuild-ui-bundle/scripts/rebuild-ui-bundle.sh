#!/usr/bin/env bash
# 重打聊天窗 UI AssetBundle，并在构建前后 dump 对比 tool-bar 的右锚点。
#
#   scripts/rebuild-ui-bundle.sh                 # 只打包
#   scripts/rebuild-ui-bundle.sh --add-parts     # 先跑"只做加法"的移植工具再打包
#
# 前置：Unity 已关闭（批处理独占工程）。编辑器路径用 UNITY 覆盖。
# 产物：dsp-game/Build/IcarusIIBundle/icarusii-ui + mod/unity/Prefab 快照。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
cd "$ROOT"

UNITY_VERSION="${UNITY_VERSION:-2022.3.62f3}"
UNITY="${UNITY:-$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity}"
PROJ="$ROOT/dsp-game"
SCRATCH="$ROOT/.scratch"
DUMP_DIR="$SCRATCH/ui-dump"
DUMP="$SCRATCH/chatwindow-dump.txt"   # IcarusIIDumpPrefab 里写死的输出路径

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[31m!!\033[0m %s\n' "$*" >&2; exit 1; }

ADD_PARTS=0
ALLOW_ANCHOR_CHANGE=0
for arg in "$@"; do
  case "$arg" in
    --add-parts) ADD_PARTS=1; ALLOW_ANCHOR_CHANGE=1 ;;
    -h|--help) sed -n '2,8p' "$SELF"; exit 0 ;;
    *) die "未知参数：$arg（可用：--add-parts）" ;;
  esac
done

[ -x "$UNITY" ] || die "找不到 Unity：$UNITY（用 UNITY=/path/to/Unity 覆盖）"
[ -d "$PROJ" ] || die "找不到工程目录：$PROJ"
[ -e "$PROJ/Temp/UnityLockfile" ] && die "Unity 正在运行并独占工程，请先关闭编辑器"

mkdir -p "$DUMP_DIR"

# dump 工具要先同步到工程的 Editor 目录（与 build-ui-bundle.sh 的做法一致）
mkdir -p "$PROJ/Assets/IcarusII/Editor"
cp "$ROOT"/mod/unity/Editor/*.cs "$PROJ/Assets/IcarusII/Editor/"

dump_prefab() {
  local name="$1"
  rm -f "$DUMP"
  log "dump（$name）"
  "$UNITY" -batchmode -nographics -quit -projectPath "$PROJ" \
    -executeMethod IcarusII.EditorTools.IcarusIIDumpPrefab.DumpChatWindow \
    -logFile "$DUMP_DIR/$name.log" \
    || { tail -30 "$DUMP_DIR/$name.log" >&2 || true; die "dump 失败（$name）"; }
  [ -f "$DUMP" ] || die "没有 dump 产物：$DUMP（检查 IcarusIIDumpPrefab 的 OUT_PATH）"
  cp "$DUMP" "$DUMP_DIR/$name.txt"
}

anchors() {
  local f="$1"
  grep -E 'tool-bar/(send-button|permission-group|thinking-group|model-group) +\[' "$f" \
    | sed -E 's#.*tool-bar/([a-z-]+) +\[.*pos=\(([^)]*)\) sd=\(([^)]*)\)\].*#\1 pos=\2 size=\3#'
}

dump_prefab before

log "重打 UI AssetBundle"
if [ "$ADD_PARTS" = 1 ]; then
  scripts/build-ui-bundle.sh --add-parts
else
  scripts/build-ui-bundle.sh
fi

dump_prefab after

anchors "$DUMP_DIR/before.txt" > "$DUMP_DIR/before.anchors"
anchors "$DUMP_DIR/after.txt" > "$DUMP_DIR/after.anchors"
[ -s "$DUMP_DIR/before.anchors" ] || die "dump 里没找到 tool-bar 分组，检查 dump 是否完整"

if diff -u "$DUMP_DIR/before.anchors" "$DUMP_DIR/after.anchors"; then
  log "tool-bar 锚点未漂移"
else
  if [ "$ALLOW_ANCHOR_CHANGE" = 1 ]; then
    log "锚点有变化（--add-parts 允许），请人工确认差异是否预期"
  else
    die "tool-bar 锚点漂移：先恢复 mod/unity/Prefab/ChatWindow.prefab 快照再重打，不要改运行期代码补位置"
  fi
fi

cmp -s "$PROJ/Assets/IcarusII/ChatWindow.prefab" "$ROOT/mod/unity/Prefab/ChatWindow.prefab" \
  || die "prefab 快照与工作副本不一致：检查 build-ui-bundle.sh 的回存步骤"

log "完成：$PROJ/Build/IcarusIIBundle/icarusii-ui"
log "dump：$DUMP_DIR/{before,after}.txt"
