#!/usr/bin/env bash
#
# 把 IcarusII 从 dist/pkg 更新到 r2modman 配置档（默认），
# 或手动安装的游戏目录（显式 --game-dir，或本机没有 r2modman 时）。
#
# 只覆盖这四个文件：
#   BepInEx/plugins/IcarusII/IcarusII.dll
#   BepInEx/plugins/IcarusII/IcarusII.pdb
#   BepInEx/plugins/IcarusII/sidecar/icarus-ii-sidecar.exe
#   BepInEx/plugins/IcarusII/ui/icarusii-ui
#
# 绝不触碰：
#   <配置档>/mods.yml（r2modman 的 mod 清单）
#   <配置档>/BepInEx/plugins/ 下的其他 mod
#   <配置档>/BepInEx/config/icarus-ii.cfg（只在目标不存在时从旧游戏目录迁移一次）
#   %APPDATA%/icarus-ii/config.json / runtime.json / sessions / agent
#
# 用法：
#   ./update-steam-mod.sh                    # 自动：优先 r2modman 配置档
#   ./update-steam-mod.sh --dry-run          # 只打印将要做什么
#   ./update-steam-mod.sh --profile DSP      # 有多个配置档时指定
#   ./update-steam-mod.sh --r2modman-dir ~/.config/r2modmanPlus-local
#   ./update-steam-mod.sh --game-dir "/path/to/Dyson Sphere Program"   # 手动安装
#   ./update-steam-mod.sh --remove-legacy    # 迁移后把游戏目录旧插件移出游戏目录
#   ./update-steam-mod.sh --source /path/to/pkg/IcarusII/BepInEx/plugins/IcarusII
#   ./update-steam-mod.sh --no-backup        # 不建议
#
set -euo pipefail

APPID=1366540
SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SELF_DIR/../../../.." && pwd)"
SRC="$ROOT/dist/pkg/IcarusII/BepInEx/plugins/IcarusII"

GAME_DIR_ARG=""
R2_DIR_ARG=""
PROFILE_ARG=""
DRY=0
BACKUP=1
REMOVE_LEGACY=0

MODE=""
R2_GAME_DATA=""
PROFILE=""
PROFILE_DIR=""
GAME_DIR=""
PLUGIN=""
BEPINEX_CORE=""
CONFIG_DIR=""
LOG=""

FILES=(
  "IcarusII.dll"
  "IcarusII.pdb"
  "sidecar/icarus-ii-sidecar.exe"
  "ui/icarusii-ui"
)

log()  { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[!]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --game-dir)      GAME_DIR_ARG="${2:?}"; shift 2 ;;
    --r2modman-dir)  R2_DIR_ARG="${2:?}"; shift 2 ;;
    --profile)       PROFILE_ARG="${2:?}"; shift 2 ;;
    --source)        SRC="${2:?}"; shift 2 ;;
    --dry-run)       DRY=1; shift ;;
    --no-backup)     BACKUP=0; shift ;;
    --remove-legacy) REMOVE_LEGACY=1; shift ;;
    -h|--help)       sed -n '2,27p' "$0"; exit 0 ;;
    *) die "未知参数：$1" ;;
  esac
done

# ---------------------------------------------------------------- 定位 Steam 游戏
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

# ---------------------------------------------------------------- 定位 r2modman
r2modman_roots() {
  local roots=(
    "${XDG_CONFIG_HOME:-$HOME/.config}/r2modmanPlus-local"
    "$HOME/.config/r2modmanPlus-local"
    "$HOME/.var/app/io.github.ebkr.r2modman/config/r2modmanPlus-local"
    "$HOME/.var/app/com.github.ebkr.r2modman/config/r2modmanPlus-local"
  )
  local r seen=""
  for r in "${roots[@]}"; do
    [[ -d "$r" ]] || continue
    case "$seen" in *"|$r|"*) continue ;; esac
    seen="$seen|$r|"
    printf '%s\n' "$r"
  done
}

find_r2modman_game_data() {
  local root
  while IFS= read -r root; do
    if [[ -d "$root/DysonSphereProgram/profiles" ]]; then
      printf '%s\n' "$root/DysonSphereProgram"; return 0
    fi
  done < <(r2modman_roots)
  return 1
}

profile_has_bepinex() {
  [[ -f "$1/BepInEx/core/BepInEx.dll" ]]
}

resolve_profile() {
  local gd="$1" wanted="$2" p name count=0 found="" names=""
  if [[ -n "$wanted" ]]; then
    [[ -d "$gd/profiles/$wanted" ]] || die "配置档不存在：$gd/profiles/$wanted"
    profile_has_bepinex "$gd/profiles/$wanted" \
      || die "配置档没有 BepInEx：$gd/profiles/$wanted（先在 r2modman 里给该配置档装好 BepInEx）"
    PROFILE="$wanted"
    PROFILE_DIR="$gd/profiles/$wanted"
    return 0
  fi
  for p in "$gd"/profiles/*/; do
    [[ -d "$p" ]] || continue
    name="${p%/}"; name="${name##*/}"
    names="$names $name"
    profile_has_bepinex "$gd/profiles/$name" || continue
    count=$((count + 1))
    found="$name"
  done
  if [[ $count -eq 1 ]]; then
    PROFILE="$found"
    PROFILE_DIR="$gd/profiles/$found"
    return 0
  fi
  if [[ $count -eq 0 ]]; then
    die "r2modman 里没有装好 BepInEx 的配置档：$gd/profiles（已有：${names:-无}）"
  fi
  die "r2modman 有 $count 个可用配置档，请用 --profile 指定：$names"
}

# ---------------------------------------------------------------- 选择目标
if [[ -n "$GAME_DIR_ARG" ]]; then
  MODE="game"
elif [[ -n "$R2_DIR_ARG" || -n "$PROFILE_ARG" ]]; then
  MODE="r2modman"
  if [[ -z "$R2_DIR_ARG" ]]; then
    R2_GAME_DATA="$(find_r2modman_game_data || true)"
  fi
elif R2_GAME_DATA="$(find_r2modman_game_data)"; then
  MODE="r2modman"
else
  MODE="game"
fi

if [[ $MODE == "r2modman" ]]; then
  if [[ -n "$R2_DIR_ARG" ]]; then
    if [[ -d "$R2_DIR_ARG/DysonSphereProgram/profiles" ]]; then
      R2_GAME_DATA="$R2_DIR_ARG/DysonSphereProgram"
    elif [[ -d "$R2_DIR_ARG/profiles" ]]; then
      R2_GAME_DATA="$R2_DIR_ARG"
    else
      die "--r2modman-dir 下没有 DysonSphereProgram/profiles：$R2_DIR_ARG"
    fi
  fi
  [[ -n "$R2_GAME_DATA" ]] || die "找不到 r2modman 的 DysonSphereProgram 数据目录"
  resolve_profile "$R2_GAME_DATA" "$PROFILE_ARG"
  PLUGIN="$PROFILE_DIR/BepInEx/plugins/IcarusII"
  BEPINEX_CORE="$PROFILE_DIR/BepInEx/core/BepInEx.dll"
  CONFIG_DIR="$PROFILE_DIR/BepInEx/config"
  LOG="$PROFILE_DIR/BepInEx/LogOutput.log"
  GAME_DIR="$(detect_game_dir || true)"
else
  GAME_DIR="$GAME_DIR_ARG"
  [[ -n "$GAME_DIR" ]] || GAME_DIR="$(detect_game_dir || true)"
  [[ -n "$GAME_DIR" ]] || die "找不到 DSP 游戏目录；用 --game-dir 指定。"
  [[ -f "$GAME_DIR/DSPGAME.exe" ]] || die "不是 DSP 游戏目录（缺 DSPGAME.exe）：$GAME_DIR"
  PLUGIN="$GAME_DIR/BepInEx/plugins/IcarusII"
  BEPINEX_CORE="$GAME_DIR/BepInEx/core/BepInEx.dll"
  CONFIG_DIR="$GAME_DIR/BepInEx/config"
  LOG="$GAME_DIR/BepInEx/LogOutput.log"
fi

# ---------------------------------------------------------------- 前置检查
[[ -f "$BEPINEX_CORE" ]] || die "BepInEx 未安装：$BEPINEX_CORE"
for f in "${FILES[@]}"; do
  [[ -f "$SRC/$f" ]] || die "缺少源文件：$SRC/$f（先构建/打包）"
done

if pgrep -x 'DSPGAME.exe' >/dev/null 2>&1; then
  die "DSPGAME 正在运行，请先退出游戏再更新（否则 DLL/UI 可能被占用）。"
fi
if pgrep -x wineserver >/dev/null 2>&1; then
  warn "wineserver 正在运行；本脚本只覆盖文件、不写注册表，通常没问题。"
fi

if [[ $MODE == "r2modman" && -f "$PROFILE_DIR/mods.yml" ]] \
   && grep -Eq '^[[:space:]]*name: (from_dsp_bwiki-)?IcarusII[[:space:]]*$' "$PROFILE_DIR/mods.yml"; then
  warn "该配置档里的 IcarusII 由 r2modman 管理；手写文件会在 r2modman 更新/切换配置档时被覆盖。"
  warn "本地开发请保留手写部署，不要再从 Online 装同名 mod。"
fi

log "目标：$([[ $MODE == "r2modman" ]] && echo "r2modman 配置档 $PROFILE" || echo "手动安装（游戏目录）")"
log "插件目录：$PLUGIN"
log "源包目录：$SRC"

# ---------------------------------------------------------------- 迁移旧配置
if [[ $MODE == "r2modman" && -n "$GAME_DIR" ]]; then
  LEGACY_CFG="$GAME_DIR/BepInEx/config/icarus-ii.cfg"
  if [[ -f "$LEGACY_CFG" && ! -f "$CONFIG_DIR/icarus-ii.cfg" ]]; then
    if [[ $DRY -eq 1 ]]; then
      log "[dry-run] cp -p $LEGACY_CFG -> $CONFIG_DIR/icarus-ii.cfg"
    else
      mkdir -p "$CONFIG_DIR"
      cp -p "$LEGACY_CFG" "$CONFIG_DIR/icarus-ii.cfg"
      log "已迁移旧配置：$CONFIG_DIR/icarus-ii.cfg"
    fi
  fi
fi

# ---------------------------------------------------------------- 备份
TS="$(date +%Y%m%d-%H%M%S)"
BK="$PLUGIN/.backup-$TS"
BK_CREATED=0
if [[ $BACKUP -eq 1 && $DRY -eq 0 ]]; then
  for f in "${FILES[@]}"; do
    [[ -f "$PLUGIN/$f" ]] || continue
    mkdir -p "$BK/$(dirname "$f")"
    cp -a "$PLUGIN/$f" "$BK/$f"
    BK_CREATED=1
  done
  [[ $BK_CREATED -eq 1 ]] && log "已备份旧文件：$BK"
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

# ---------------------------------------------------------------- 移走游戏目录旧插件
if [[ $REMOVE_LEGACY -eq 1 ]]; then
  [[ $MODE == "r2modman" ]] || die "--remove-legacy 只用于 r2modman 模式（游戏目录安装就是目标本身）。"
  [[ -n "$GAME_DIR" ]] || die "找不到游戏目录，无法定位旧插件。"
  LEGACY="$GAME_DIR/BepInEx/plugins/IcarusII"
  if [[ -e "$LEGACY" ]]; then
    DEST="$ROOT/dist/legacy-install/IcarusII-$TS"
    if [[ $DRY -eq 1 ]]; then
      log "[dry-run] mv $LEGACY -> $DEST"
    else
      mkdir -p "$(dirname "$DEST")"
      mv "$LEGACY" "$DEST"
      log "已把游戏目录旧插件移出游戏目录：$DEST"
      log "确认新部署工作正常后可删除该备份（$(du -sh "$DEST" | cut -f1)）。"
    fi
  else
    log "游戏目录没有旧插件（$LEGACY），无需清理。"
  fi
fi

# ---------------------------------------------------------------- 配置迁移自检
PREFIX=""
while IFS= read -r root; do
  if [[ -d "$root/steamapps/compatdata/$APPID/pfx" ]]; then
    PREFIX="$root/steamapps/compatdata/$APPID/pfx"
    break
  fi
done < <(find_steam_roots)

CFG_JSON=""
if [[ -n "$PREFIX" ]]; then
  for c in "$PREFIX"/drive_c/users/*/AppData/Roaming/icarus-ii/config.json; do
    [[ -f "$c" ]] && { CFG_JSON="$c"; break; }
  done
fi

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
if [[ -n "$PREFIX" ]]; then
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
fi

# ---------------------------------------------------------------- 结论
if [[ $MODE == "r2modman" ]]; then
  START_HINT="在 r2modman 里点 Start modded 启动（不要从 Steam 直接启动）。"
else
  START_HINT="从 Steam 直接启动游戏。"
fi

if [[ $DRY -eq 1 ]]; then
  BK_NOTE="(dry-run，未备份)"
elif [[ $BK_CREATED -eq 1 ]]; then
  BK_NOTE="$BK"
else
  BK_NOTE="(首次部署，无旧文件)"
fi

cat <<EOF

==> 更新完成

  目标：$([[ $MODE == "r2modman" ]] && echo "r2modman 配置档 $PROFILE" || echo "游戏目录")
  版本文件：
    $PLUGIN/IcarusII.dll
    $PLUGIN/sidecar/icarus-ii-sidecar.exe
    $PLUGIN/ui/icarusii-ui
  旧文件备份：$BK_NOTE

  $START_HINT
  日志：$LOG
  侧车配置仍在：
    ${CFG_JSON:-%APPDATA%\\icarus-ii\\config.json}
  旧版单供应商 config.json 会在新 sidecar 启动时自动迁移，key 保留。
EOF
