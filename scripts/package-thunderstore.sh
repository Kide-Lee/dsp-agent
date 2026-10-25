#!/usr/bin/env bash
# 打 Thunderstore 包：dist/IcarusII-<version>.zip
#
#   scripts/package-thunderstore.sh                 # 含 UI 资源包（默认）
#   SKIP_UI=1 scripts/package-thunderstore.sh       # 不含 UI 资源包（不重分发游戏美术资源）
#   SKIP_SIDECAR=1 scripts/package-thunderstore.sh  # 复用 dist/icarus-ii-sidecar.exe
#   SKIP_BUILD=1 scripts/package-thunderstore.sh    # 不重新编译 mod
#
# 产物布局（Thunderstore / r2modman 约定）：
#   manifest.json  icon.png  README.md  CHANGELOG.md
#   plugins/IcarusII/{IcarusII.dll, IcarusII.pdb, sidecar/*.exe, ui/icarusii-ui}
#
# 元数据（manifest/README/CHANGELOG/icon）的**源文件**在 packaging/thunderstore/，
# dist/ 只放生成物。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
META="$ROOT/packaging/thunderstore"
STAGE="$ROOT/dist/thunderstore"
CONFIG="${CONFIG:-Release}"
PLUGIN="$STAGE/plugins/IcarusII"

log()  { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[!]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

[[ -f "$META/manifest.json" ]] || die "缺少 $META/manifest.json"

VERSION="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['version_number'])" "$META/manifest.json")"
[[ -n "$VERSION" ]] || die "manifest.json 里没有 version_number"

MOD_VERSION="$(grep -oP 'PluginVersion\s*=\s*"\K[^"]+' "$ROOT/mod/src/IcarusII/Plugin.cs" || true)"
[[ "$MOD_VERSION" == "$VERSION" ]] || die "manifest 版本 $VERSION 与 PluginVersion $MOD_VERSION 不一致"

# ---------------------------------------------------------------- 编译
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  log "编译 mod（$CONFIG）"
  CONFIG="$CONFIG" "$ROOT/scripts/build-mod.sh" >/dev/null
fi
DLL="$ROOT/mod/src/IcarusII/bin/$CONFIG/IcarusII.dll"
PDB="$ROOT/mod/src/IcarusII/bin/$CONFIG/IcarusII.pdb"
[[ -f "$DLL" ]] || die "找不到 $DLL"

# ---------------------------------------------------------------- sidecar
EXE="$ROOT/dist/icarus-ii-sidecar.exe"
if [[ "${SKIP_SIDECAR:-0}" != "1" || ! -f "$EXE" ]]; then
  log "构建 sidecar（windows-x64，约 100 MB，耐心等）"
  "$ROOT/scripts/build-sidecar.sh" windows-x64
fi
[[ -f "$EXE" ]] || die "缺少 $EXE"

# ---------------------------------------------------------------- UI bundle
BUNDLE="$ROOT/dsp-game/Build/IcarusIIBundle/icarusii-ui"
if [[ "${SKIP_UI:-0}" != "1" && ! -f "$BUNDLE" ]]; then
  warn "找不到 UI bundle：$BUNDLE"
  warn "先跑 scripts/build-ui-bundle.sh；这次按 SKIP_UI=1 处理（界面会退回代码构建）"
  SKIP_UI=1
fi

# ---------------------------------------------------------------- 组装
log "组装 $STAGE"
rm -rf "$STAGE"
mkdir -p "$PLUGIN/sidecar"
cp "$META/manifest.json" "$META/icon.png" "$META/README.md" "$META/CHANGELOG.md" "$STAGE/"
cp "$DLL" "$PLUGIN/"
[[ -f "$PDB" ]] && cp "$PDB" "$PLUGIN/" || true
cp "$EXE" "$PLUGIN/sidecar/"
if [[ "${SKIP_UI:-0}" != "1" ]]; then
  mkdir -p "$PLUGIN/ui"
  cp "$BUNDLE" "$PLUGIN/ui/"
else
  # 变体包：README 顶部说明"没有 UI 资源包"，避免玩家以为是完整版
  python3 - "$STAGE/README.md" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding="utf-8").read()
note = ("> ⚠️ **本包不含 UI 资源包**（`ui/icarusii-ui`）：窗口会用代码构建，功能一样，"
        "外观不如截图精致。这样做的原因是不重分发游戏自带的美术资源。\n\n")
s = s.replace("# 伊卡洛斯·贰 / Icarus II\n", "# 伊卡洛斯·贰 / Icarus II\n\n" + note, 1)
open(p, "w", encoding="utf-8").write(s)
PY
fi

# ---------------------------------------------------------------- 校验
log "校验"
python3 - "$STAGE" "$VERSION" "${SKIP_UI:-0}" <<'PY'
import json, os, re, sys
from struct import unpack

stage, version, skip_ui = sys.argv[1], sys.argv[2], sys.argv[3] == "1"
fail = []

def check(cond, msg):
    print(("  ok   " if cond else "  FAIL ") + msg)
    if not cond: fail.append(msg)

m = json.load(open(os.path.join(stage, "manifest.json")))
check(re.fullmatch(r"[A-Za-z0-9_]+", m.get("name", "")) is not None, "name 只允许字母/数字/下划线")
check(m.get("version_number") == version, "version_number 与目录一致")
check(re.fullmatch(r"\d+\.\d+\.\d+", version or "") is not None, "version 是语义化版本")
check(len(m.get("description", "")) <= 250, "description ≤ 250 字符")
check(isinstance(m.get("dependencies"), list), "dependencies 是数组")
check(os.path.isfile(os.path.join(stage, "README.md")), "README.md 在根目录")
check(os.path.isfile(os.path.join(stage, "CHANGELOG.md")), "CHANGELOG.md 在根目录")
icon = os.path.join(stage, "icon.png")
if os.path.isfile(icon):
    b = open(icon, "rb").read()
    ok_png = b[:8] == b"\x89PNG\r\n\x1a\n"
    w, h = unpack(">II", b[16:24]) if ok_png else (0, 0)
    check(ok_png and (w, h) == (256, 256), f"icon.png 是 256x256 PNG（实际 {w}x{h}）")
else:
    check(False, "icon.png 存在")
base = os.path.join(stage, "plugins", "IcarusII")
check(os.path.isfile(os.path.join(base, "IcarusII.dll")), "plugins/IcarusII/IcarusII.dll")
check(os.path.isfile(os.path.join(base, "sidecar", "icarus-ii-sidecar.exe")), "sidecar 在 plugins/IcarusII/sidecar/")
ui_path = os.path.join(base, "ui", "icarusii-ui")
check(os.path.isfile(ui_path) != skip_ui,
      "ui bundle 与 SKIP_UI 一致" + ("（变体包不应有）" if skip_ui else ""))

sys.exit(1 if fail else 0)
PY

# ---------------------------------------------------------------- 打包
SUFFIX=""
[[ "${SKIP_UI:-0}" == "1" ]] && SUFFIX="-nogameassets"
ZIP="$ROOT/dist/IcarusII-$VERSION$SUFFIX.zip"
rm -f "$ZIP"
( cd "$STAGE" && zip -r -9 -q "$ZIP" manifest.json icon.png README.md CHANGELOG.md plugins )

log "完成：$ZIP（$(du -h "$ZIP" | cut -f1)）"
( cd "$STAGE" && find . -type f -printf '  %-56p %10s\n' | sort )
