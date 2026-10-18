#!/usr/bin/env bash
#
# 把 IcarusII 从 dist/pkg 安全更新到 Steam/Proton 的 DSP 安装。
#
# 只覆盖这四个文件：
#   BepInEx/plugins/IcarusII/IcarusII.dll
#   BepInEx/plugins/IcarusII/IcarusII.pdb
#   BepInEx/plugins/IcarusII/sidecar/icarus-ii-sidecar.exe
#   BepInEx/plugins/IcarusII/ui/icarusii-ui
#
# 绝不触碰：
#   BepInEx/config/icarus-ii.cfg
#   %APPDATA%/icarus-ii/config.json / runtime.json / sessions / agent
#
# 用法：
#   ./update-steam-mod.sh                 # 自动定位游戏目录
#   ./update-steam-mod.sh --dry-run       # 只打印将要做什么
#   ./update-steam-mod.sh --game-dir "..."
#   ./update-steam-mod.sh --source /path/to/pkg/IcarusII/BepInEx/plugins/IcarusII
#   ./update-steam-mod.sh --no-backup     # 不建议
#
set -euo pipefail

APPID=1366540
SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SELF_DIR/../../../.." && pwd)"
SRC="$ROOT/dist/pkg/IcarusII/BepInEx/plugins/IcarusII"
GAME_DIR=""
DRY=0
BACKUP=1

log()  { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[!]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --game-dir) GAME_DIR="${2:?}"; shift 2 ;;
    --source)   SRC="${2:?}"; shift 2 ;;
    --dry-run)  DRY=1; shift ;;
    --no-backup) BACKUP=0; shift ;;
    -h|--help)  sed -n '2,26p' "$0"; exit 0 ;;
    *) die "未知参数：$1" ;;
  esac
done

# ---------------------------------------------------------------- 定位游戏
find_steam_roots() {
  local roots=(
    "$HOME/.local/share/Steam"
    "$HOME/.steam/steam"
    "$HOME/.var/app/com.valvesoftware.Steam/data/Steam"
    "$HOME/Library/Application Support/Steam"
  )
  local r
  for r in "${roots[@]}"; do
    [[ -d "$r/steamapps" ]] && printf '%s\n' "$r"
  done
}

detect_game_dir() {
  local root lib vdf entry
  while IFS= read -r root; do
    [[ -d "$root/steamapps/common/Dyson Sphere Program" ]] && {
      printf '%s\n' "$root/steamapps/common/Dyson Sphere Program"; return 0;
    }
    vdf="$root/steamapps/libraryfolders.vdf"
    [[ -f "$vdf" ]] || continue
    while IFS= read -r lib; do
      [[ -n "$lib" ]] || continue
      entry="$lib/steamapps/common/Dyson Sphere Program"
      [[ -d "$entry" ]] && { printf '%s\n' "$entry"; return 0; }
    done < <(grep -oE '"path"[[:space:]]*"[^"]+"' "$vdf" 2>/dev/null | sed -E 's/.*"([^"]+)"$/\1/')
  done < <(find_steam_roots)
  return 1
}

[[ -n "$GAME_DIR" ]] || GAME_DIR="$(detect_game_dir || true)"
[[ -n "$GAME_DIR" ]] || die "找不到 DSP 游戏目录；用 --game-dir 指定。"
[[ -f "$GAME_DIR/DSPGAME.exe" ]] || die "不是 DSP 游戏目录（缺 DSPGAME.exe）：$GAME_DIR"

PLUGIN="$GAME_DIR/BepInEx/plugins/IcarusII"
FILES=(
  "IcarusII.dll"
  "IcarusII.pdb"
  "sidecar/icarus-ii-sidecar.exe"
  "ui/icarusii-ui"
)

# ---------------------------------------------------------------- 前置检查
[[ -f "$GAME_DIR/BepInEx/core/BepInEx.dll" ]] || die "BepInEx 未安装：$GAME_DIR/BepInEx/core/BepInEx.dll"
for f in "${FILES[@]}"; do
  [[ -f "$SRC/$f" ]] || die "缺少源文件：$SRC/$f（先构建/打包）"
done

if pgrep -af "DSPGAME\.exe" | grep -vq pgrep; then
  die "DSPGAME 正在运行，请先退出游戏再更新（否则 DLL/UI 可能被占用）。"
fi
if pgrep -x wineserver >/dev/null 2>&1; then
  warn "wineserver 正在运行；本脚本只覆盖文件、不写注册表，通常没问题。"
fi

log "游戏目录：$GAME_DIR"
log "源包目录：$SRC"

# ---------------------------------------------------------------- 备份
TS="$(date +%Y%m%d-%H%M%S)"
BK="$PLUGIN/.backup-$TS"
if [[ $BACKUP -eq 1 && $DRY -eq 0 ]]; then
  mkdir -p "$BK/sidecar" "$BK/ui"
  for f in "${FILES[@]}"; do
    [[ -f "$PLUGIN/$f" ]] && { mkdir -p "$BK/$(dirname "$f")"; cp -a "$PLUGIN/$f" "$BK/$f"; }
  done
  log "已备份旧文件：$BK"
fi

# ---------------------------------------------------------------- 复制
for f in "${FILES[@]}"; do
  if [[ $DRY -eq 1 ]]; then
    log "[dry-run] cp -f $SRC/$f -> $PLUGIN/$f"
  else
    mkdir -p "$PLUGIN/$(dirname "$f")"
    cp -f "$SRC/$f" "$PLUGIN/$f"
    log "已更新 $f"
  fi
done

# ---------------------------------------------------------------- 哈希校验
if [[ $DRY -eq 0 ]]; then
  log "校验 SHA-256："
  for f in "${FILES[@]}"; do
    a="$(sha256sum "$SRC/$f" | awk '{print $1}')"
    b="$(sha256sum "$PLUGIN/$f" | awk '{print $1}')"
    [[ "$a" == "$b" ]] || die "哈希不一致：$f（$a != $b）"
    printf '   ok  %s  %s\n' "${a:0:12}" "$f"
  done
fi

# ---------------------------------------------------------------- 配置迁移自检
STEAMAPPS="$(dirname "$(dirname "$GAME_DIR")")"
PREFIX="$STEAMAPPS/compatdata/$APPID/pfx"
CFG_JSON=""
for c in "$PREFIX"/drive_c/users/*/AppData/Roaming/icarus-ii/config.json; do
  [[ -f "$c" ]] && { CFG_JSON="$c"; break; }
done

if [[ -n "$CFG_JSON" && -f "$ROOT/sidecar/dist/config.js" ]] && command -v node >/dev/null 2>&1; then
  TMP="$(mktemp -d)"
  trap 'rm -rf "$TMP"' EXIT
  cp "$CFG_JSON" "$TMP/config.json"
  log "配置迁移自检（只读取副本，不打印 key）："
  node --input-type=module - "$TMP/config.json" "$ROOT/sidecar/dist/config.js" <<'NODE'
import { pathToFileURL } from 'node:url';
const [cfgPath, modPath] = process.argv.slice(2);
const { ConfigStore } = await import(pathToFileURL(modPath).href);
const s = new ConfigStore({ path: cfgPath, requireApiKey: true });
const v = s.view();
const d = s.get();
const active = d.providers[d.provider];
console.log(JSON.stringify({
  state: v.state,
  active: v.active,
  providers: v.providers.map((p) => ({ id: p.id, hasKey: p.hasKey, models: p.models.map((m) => m.id) })),
  activeKeyPresent: Boolean(active?.apiKey),
}, null, 2));
NODE
  rm -rf "$TMP"
else
  warn "跳过配置迁移自检（没有 config.json / node / dist/config.js）。"
fi

# ---------------------------------------------------------------- Proton 覆盖
REG="$PREFIX/user.reg"
if [[ -f "$REG" ]]; then
  if grep -q 'AppDefaults\\\\DSPGAME.exe\\\\DllOverrides' "$REG" 2>/dev/null; then
    log "Proton winhttp 覆盖已存在。"
  else
    warn "Proton winhttp 覆盖不存在；BepInEx 可能不加载。需要时在 game prefix 的 user.reg 加："
    warn '  [Software\\\\Wine\\\\AppDefaults\\\\DSPGAME.exe\\\\DllOverrides]'
    warn '  "winhttp"="native,builtin"'
  fi
fi

cat <<EOF

==> 更新完成

  版本文件：
    $PLUGIN/IcarusII.dll
    $PLUGIN/sidecar/icarus-ii-sidecar.exe
    $PLUGIN/ui/icarusii-ui
  旧文件备份：$([[ $DRY -eq 1 ]] && echo "(dry-run，未备份)" || echo "$BK")

  启动游戏后按 R 打开聊天窗；侧车配置仍在：
    $CFG_JSON
  旧版单供应商 config.json 会在新 sidecar 启动时自动迁移，key 保留。
EOF
