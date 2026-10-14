#!/usr/bin/env bash
#
# 把 IcarusII 安装到 Dyson Sphere Program 的游戏目录。
#
# 前提：**BepInEx 5 已安装**。本脚本不会替你装 BepInEx
#       （除非显式加 --with-bepinex，那是开发自用路径）。
#
# 用法：
#   ./install.sh                          # 自动定位游戏目录
#   ./install.sh --game-dir "/path/to/Dyson Sphere Program"
#   ./install.sh --with-bepinex            # 顺带从 ../.deps 安装 BepInEx（开发用）
#   ./install.sh --no-registry             # 不写 Proton 的 DLL 覆盖，改为打印手动步骤
#   ./install.sh --uninstall
#
# 脚本做的事：
#   1. 定位游戏目录（Steam libraryfolders.vdf + 常见路径）
#   2. 校验 BepInEx 是否就位
#   3. 复制 BepInEx/plugins/IcarusII/
#   4. 若是 Proton 运行：向前缀注册表写入 winhttp 覆盖，
#      使 BepInEx 无需 Steam 启动项即可生效
#
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_DIR="${PKG_DIR:-$SELF_DIR}"          # 包根目录（内含 BepInEx/）
APPID=1366540
GAME_DIR=""
WITH_BEPINEX=0
NO_REGISTRY=0
UNINSTALL=0
PLUGIN_SUBDIR="BepInEx/plugins/IcarusII"

log()  { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[!]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --game-dir)    GAME_DIR="${2:?}"; shift 2 ;;
    --with-bepinex) WITH_BEPINEX=1; shift ;;
    --no-registry) NO_REGISTRY=1; shift ;;
    --uninstall)   UNINSTALL=1; shift ;;
    -h|--help)     sed -n '2,25p' "$0"; exit 0 ;;
    *) die "未知参数：$1" ;;
  esac
done

# ---------------------------------------------------------------- 定位游戏目录
find_steam_roots() {
  local candidates=(
    "$HOME/.local/share/Steam"
    "$HOME/.steam/steam"
    "$HOME/.var/app/com.valvesoftware.Steam/data/Steam"
    "$HOME/Library/Application Support/Steam"
  )
  local root
  for root in "${candidates[@]}"; do
    [[ -d "$root/steamapps" ]] && printf '%s\n' "$root"
  done
}

detect_game_dir() {
  local root lib entry
  while IFS= read -r root; do
    # 主库
    if [[ -d "$root/steamapps/common/Dyson Sphere Program" ]]; then
      printf '%s\n' "$root/steamapps/common/Dyson Sphere Program"; return 0
    fi
    # 其它库：从 libraryfolders.vdf 里抓 path
    local vdf="$root/steamapps/libraryfolders.vdf"
    [[ -f "$vdf" ]] || continue
    while IFS= read -r lib; do
      [[ -n "$lib" ]] || continue
      entry="$lib/steamapps/common/Dyson Sphere Program"
      if [[ -d "$entry" ]]; then printf '%s\n' "$entry"; return 0; fi
    done < <(grep -oE '"path"[[:space:]]*"[^"]+"' "$vdf" 2>/dev/null | sed -E 's/.*"([^"]+)"$/\1/')
  done < <(find_steam_roots)
  return 1
}

if [[ -z "$GAME_DIR" ]]; then
  GAME_DIR="$(detect_game_dir || true)"
fi
[[ -n "$GAME_DIR" ]] || die "无法自动定位游戏目录，请用 --game-dir 指定。"
[[ -d "$GAME_DIR" ]] || die "游戏目录不存在：$GAME_DIR"
[[ -f "$GAME_DIR/DSPGAME.exe" ]] || die "$GAME_DIR 看起来不是 DSP 游戏目录（缺少 DSPGAME.exe）。"
log "游戏目录：$GAME_DIR"

# ---------------------------------------------------------------- 卸载
if [[ $UNINSTALL -eq 1 ]]; then
  if [[ -d "$GAME_DIR/$PLUGIN_SUBDIR" ]]; then
    rm -rf "${GAME_DIR:?}/$PLUGIN_SUBDIR"
    log "已删除 $GAME_DIR/$PLUGIN_SUBDIR"
  else
    log "未安装（$PLUGIN_SUBDIR 不存在）"
  fi
  warn "Proton 的 winhttp 覆盖未改动（其它 mod 可能也在用）。如需移除，请手工编辑前缀 user.reg。"
  exit 0
fi

# ---------------------------------------------------------------- BepInEx 检查
if [[ ! -f "$GAME_DIR/BepInEx/core/BepInEx.dll" ]]; then
  if [[ $WITH_BEPINEX -eq 1 ]]; then
    SRC="${BEPINEX_SOURCE:-$(cd "$SELF_DIR/.." && pwd)/.deps/bepinex}"
    [[ -f "$SRC/BepInEx/core/BepInEx.dll" ]] || die "--with-bepinex 但找不到 $SRC，请先运行 scripts/fetch-deps.sh"
    log "安装 BepInEx（来自 $SRC）"
    cp -f "$SRC/winhttp.dll" "$GAME_DIR/"
    cp -f "$SRC/doorstop_config.ini" "$GAME_DIR/"
    cp -rf "$SRC/BepInEx" "$GAME_DIR/"
  else
    die "未检测到 BepInEx：$GAME_DIR/BepInEx/core/BepInEx.dll
    请先安装 BepInEx 5（https://github.com/BepInEx/BepInEx/releases，选 win_x64），
    或加 --with-bepinex 使用随仓库的 .deps/bepinex。"
  fi
fi
log "BepInEx 已就位。"

# ---------------------------------------------------------------- 复制插件
SRC_PLUGIN="$PKG_DIR/$PLUGIN_SUBDIR"
if [[ ! -f "$SRC_PLUGIN/IcarusII.dll" ]]; then
  # 允许直接从未打包的构建输出安装（开发便利）
  ALT="$(cd "$SELF_DIR/.." && pwd)/mod/src/IcarusII/bin/Release/IcarusII.dll"
  if [[ -f "$ALT" ]]; then
    log "使用构建输出：$ALT"
    mkdir -p "$SRC_PLUGIN"
    cp -f "$ALT" "$SRC_PLUGIN/IcarusII.dll"
  else
    die "找不到 $SRC_PLUGIN/IcarusII.dll"
  fi
fi

mkdir -p "$GAME_DIR/$PLUGIN_SUBDIR"
cp -rf "$SRC_PLUGIN/." "$GAME_DIR/$PLUGIN_SUBDIR/"
log "已安装插件：$GAME_DIR/$PLUGIN_SUBDIR"
ls -1 "$GAME_DIR/$PLUGIN_SUBDIR"

# ---------------------------------------------------------------- Proton DLL 覆盖
# Wine 默认优先加载 builtin 的 winhttp.dll，会盖掉游戏目录里 BepInEx 的代理 DLL。
# 社区做法是加 Steam 启动项 WINEDLLOVERRIDES="winhttp=n,b" %command%，
# 但那会破坏"解压即用"。更好的位置是前缀注册表的按 exe 覆盖：
#   [Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
#   "winhttp"="native,builtin"
# 该机制是 Proton 自己在用的（本机前缀里已有 20+ 条 Steam 写入的同类条目）。
install_proton_override() {
  local pfx=""
  local root
  while IFS= read -r root; do
    if [[ -d "$root/steamapps/compatdata/$APPID/pfx" ]]; then
      pfx="$root/steamapps/compatdata/$APPID/pfx"; break
    fi
  done < <(find_steam_roots)

  if [[ -z "$pfx" ]]; then
    log "未发现 Proton 前缀（可能是原生 Windows 运行），无需 DLL 覆盖。"
    return 0
  fi

  local reg="$pfx/user.reg"
  [[ -f "$reg" ]] || { warn "找不到 $reg，跳过。"; return 0; }

  if grep -q 'AppDefaults\\\\DSPGAME.exe\\\\DllOverrides' "$reg" 2>/dev/null; then
    log "Proton DLL 覆盖已存在，跳过。"
    return 0
  fi

  # 真正危险的只有 Wine 进程：它在退出时会把内存中的注册表整体写回 user.reg，
  # 从而覆盖我们手工加的段落。该前缀的 wineserver 不在运行时，user.reg 并未驻留内存，
  # 直接追加是安全的。Steam 客户端本身不碰前缀注册表，仅提示。
  if pgrep -x wineserver >/dev/null 2>&1 \
     || pgrep -x wine64     >/dev/null 2>&1 \
     || pgrep -x wine       >/dev/null 2>&1 \
     || pgrep -x wine-preloader >/dev/null 2>&1; then
    warn "检测到 Wine/wineserver 正在运行。它会在退出时把内存中的注册表写回，覆盖本次修改。"
    warn "请完全退出游戏（以及任何使用该前缀的 Wine 程序）后重新运行本脚本。"
    return 1
  fi

  if pgrep -x steam >/dev/null 2>&1; then
    log "Steam 客户端在运行——不影响本次写入（只有 Wine 进程会写回 user.reg），继续。"
  fi

  cp -f "$reg" "$reg.dspagent.bak"
  {
    printf '\n[Software\\\\Wine\\\\AppDefaults\\\\DSPGAME.exe\\\\DllOverrides] %s\n' "$(date +%s)"
    printf '#time=%x\n' "$(date +%s)"
    printf '"winhttp"="native,builtin"\n'
  } >> "$reg"

  log "已写入 Proton DLL 覆盖（备份：$reg.dspagent.bak）"
  return 0
}

if [[ $NO_REGISTRY -eq 0 ]]; then
  install_proton_override || warn "Proton DLL 覆盖未写入。"
else
  cat <<'EOF'

[!] 已跳过 Proton DLL 覆盖。若你在 Linux/Proton 下运行，需要手动二选一：

  方式 A（推荐，一次性）：在 Steam 该游戏属性的启动选项里填
      WINEDLLOVERRIDES="winhttp=n,b" %command%

  方式 B：向 Proton 前缀写入按 exe 的 DLL 覆盖（重跑本脚本不加 --no-registry）
EOF
fi

cat <<EOF

==> 安装完成

  1. 启动游戏
  2. 按 R 打开聊天窗口
  3. 首次使用需在窗口内填入 API Key

  日志：$GAME_DIR/BepInEx/LogOutput.log
  配置：$GAME_DIR/BepInEx/config/icarus-ii.cfg
  数据：%APPDATA%\\icarus-ii\\（Proton 下在 compatdata/$APPID/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii）
EOF
