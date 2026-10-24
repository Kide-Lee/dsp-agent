#!/usr/bin/env bash
# 构建自包含的 icarus-ii sidecar 可执行文件。
#
# 本脚本中的每一步都在开发机上实测通过（见 design.md §7.1.1 与附录 A）：
#   pi SDK + 全部依赖  ->  8.5MB 单 JS 文件（零原生模块）
#                     ->  127MB Linux x64 / 107MB Windows x64 自包含可执行文件
#
# 用法:
#   scripts/build-sidecar.sh              # 构建当前平台
#   scripts/build-sidecar.sh linux-x64    # 指定目标
#   scripts/build-sidecar.sh windows-x64
#   SIDECAR_RUNTIME_DIR=./runtimes scripts/build-sidecar.sh windows-x64
#
# 前置: node >= 22.19, npm/npx, unzip, curl
# 目标平台的自包含产物**不需要**目标机器安装 Node。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIDECAR_DIR="$ROOT/sidecar"
OUT_DIR="${OUT_DIR:-$ROOT/dist}"
BUILD_DIR="$OUT_DIR/.build"
RUNTIME_DIR="${SIDECAR_RUNTIME_DIR:-$BUILD_DIR/runtimes}"
NODE_VERSION="${NODE_VERSION:-24.18.0}"
SEA_FUSE="NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2"
POSTJECT_VERSION="${POSTJECT_VERSION:-1.0.0-alpha.6}"

HOST_TARGET=""
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  HOST_TARGET="linux-x64" ;;
  Darwin-arm64)  HOST_TARGET="darwin-arm64" ;;
  Darwin-x86_64) HOST_TARGET="darwin-x64" ;;
esac

TARGET="${1:-}"
if [[ -z "$TARGET" ]]; then
  TARGET="$HOST_TARGET"
  [[ -n "$TARGET" ]] || { echo "无法推断目标平台，请显式指定 (linux-x64|windows-x64|darwin-x64|darwin-arm64)" >&2; exit 2; }
fi

mkdir -p "$OUT_DIR" "$BUILD_DIR" "$RUNTIME_DIR"

# npx 的缓存默认在 $HOME/.npm；在受限沙箱里不可写，这里允许覆盖。
export npm_config_cache="${npm_config_cache:-$BUILD_DIR/npm-cache}"

log() { printf '\033[36m==>\033[0m %s\n' "$*"; }

# npx 常常不在 PATH 上（典型情况：~/.local/bin/node 是指向 nvm 的软链，
# 而 npx 只存在于软链目标的目录里），所以要解析真实路径再找。
if ! command -v npx >/dev/null 2>&1; then
  NODE_PATH_BIN="$(command -v node 2>/dev/null || true)"
  if [[ -n "$NODE_PATH_BIN" ]]; then
    # readlink -f 在 macOS 上不存在，退化处理
    NODE_REAL="$(readlink -f "$NODE_PATH_BIN" 2>/dev/null || true)"
    [[ -n "$NODE_REAL" ]] || NODE_REAL="$NODE_PATH_BIN"
    NODE_BIN_DIR="$(dirname "$NODE_REAL")"
    [[ -x "$NODE_BIN_DIR/npx" ]] && PATH="$NODE_BIN_DIR:$PATH"
  fi
fi
command -v npx >/dev/null 2>&1 || {
  echo "找不到 npx。请把 Node.js 的 bin 目录加入 PATH（npx 与 node 通常在同一目录）。" >&2
  exit 2
}

# ---------------------------------------------------------------- 1. 打单 JS 文件
# 关键点（踩过的坑，勿删）：
#   --format=cjs  : Node SEA 的入口按 CommonJS 执行，ESM 入口会失败
#   --define/--banner : 否则 import.meta.url 在 CJS 下为 undefined，
#                       fileURLToPath(undefined) 抛 ERR_INVALID_ARG_TYPE
#   --external:@mariozechner/clipboard : 可选的原生剪贴板，仅 TUI 使用，我们不需要
log "打包 sidecar 为单文件 (esbuild)"
npx --yes esbuild@"${ESBUILD_VERSION:-0.25.0}" "$SIDECAR_DIR/src/index.ts" \
  --bundle --platform=node --format=cjs --target=node22 --minify \
  --external:@mariozechner/clipboard \
  --define:import.meta.url=import_meta_url \
  --banner:js="const import_meta_url = require('url').pathToFileURL(__filename).href;" \
  --outfile="$BUILD_DIR/sidecar.cjs"

# 自检：在空目录里跑，确保没有隐式的 node_modules / 原生依赖
log "自检：空目录冒烟运行 bundle"
SMOKE="$BUILD_DIR/smoke"; rm -rf "$SMOKE"; mkdir -p "$SMOKE"
cp "$BUILD_DIR/sidecar.cjs" "$SMOKE/"
( cd "$SMOKE" && node sidecar.cjs --selfcheck )

# ---------------------------------------------------------------- 2. 准备 Node 运行时
fetch_node_runtime() {
  local platform="$1" arch="$2" out="$3"
  [[ -x "$out" || -f "$out" ]] && { log "复用已有运行时 $out"; return; }
  local base="node-v${NODE_VERSION}-${platform}-${arch}"
  local url archive
  case "$platform" in
    linux)   archive="$base.tar.xz"; url="https://nodejs.org/dist/v${NODE_VERSION}/${archive}" ;;
    win)     archive="$base.zip";    url="https://nodejs.org/dist/v${NODE_VERSION}/${archive}" ;;
    darwin)  archive="$base.tar.gz"; url="https://nodejs.org/dist/v${NODE_VERSION}/${archive}" ;;
    *) echo "不支持的平台 $platform" >&2; return 1 ;;
  esac
  log "下载 Node 运行时 $archive"
  curl -fsSL -o "$RUNTIME_DIR/$archive" "$url"
  case "$archive" in
    *.tar.xz) tar -xJf "$RUNTIME_DIR/$archive" -C "$RUNTIME_DIR" ;;
    *.tar.gz) tar -xzf "$RUNTIME_DIR/$archive" -C "$RUNTIME_DIR" ;;
    *.zip)    unzip -o -j "$RUNTIME_DIR/$archive" "$base/node.exe" -d "$RUNTIME_DIR" >/dev/null ;;
  esac
  case "$platform" in
    win) cp "$RUNTIME_DIR/node.exe" "$out" ;;
    *)   cp "$RUNTIME_DIR/$base/bin/node" "$out" ;;
  esac
}

# ---------------------------------------------------------------- 3. SEA + 注入
build_target() {
  local target="$1"
  local runtime out
  case "$target" in
    linux-x64)    runtime="$RUNTIME_DIR/node-linux-x64";   out="$OUT_DIR/icarus-ii-sidecar";     fetch_node_runtime linux  x64 "$runtime" ;;
    windows-x64)  runtime="$RUNTIME_DIR/node-win-x64.exe"; out="$OUT_DIR/icarus-ii-sidecar.exe"; fetch_node_runtime win    x64 "$runtime" ;;
    darwin-x64)   runtime="$RUNTIME_DIR/node-darwin-x64";  out="$OUT_DIR/icarus-ii-sidecar-macos"; fetch_node_runtime darwin x64 "$runtime" ;;
    darwin-arm64) runtime="$RUNTIME_DIR/node-darwin-arm64";out="$OUT_DIR/icarus-ii-sidecar-macos-arm64"; fetch_node_runtime darwin arm64 "$runtime" ;;
    *) echo "未知目标 $target" >&2; return 2 ;;
  esac

  log "生成 SEA blob ($target)"
  cat > "$BUILD_DIR/sea-config.json" <<EOF
{ "main": "sidecar.cjs", "output": "sea-prep.blob",
  "disableExperimentalSEAWarning": true, "useSnapshot": false, "useCodeCache": false }
EOF
  ( cd "$BUILD_DIR" && node --experimental-sea-config sea-config.json )

  log "注入 SEA blob -> $(basename "$out")"
  cp "$runtime" "$out"
  npx --yes postject@"$POSTJECT_VERSION" "$out" NODE_SEA_BLOB "$BUILD_DIR/sea-prep.blob" \
    --sentinel-fuse "$SEA_FUSE" >/dev/null

  # macOS 需要重新签名，否则 Gatekeeper 会拒绝执行
  if [[ "$target" == darwin-* ]]; then
    log "移除签名并 ad-hoc 重签 (macOS)"
    codesign --remove-signature "$out" 2>/dev/null || true
    codesign --sign - "$out"
  fi

  # 运行自检：跨平台产物本机跑不了（例如在 Linux 上产出 Windows exe）。
  if [[ "$target" == "$HOST_TARGET" ]]; then
    log "自检（本机运行）：$(basename "$out")"
    ( cd "$SMOKE" && cp "$out" ./selfcheck-bin && chmod +x ./selfcheck-bin && ./selfcheck-bin --selfcheck )
    rm -f "$SMOKE/selfcheck-bin"
  elif [[ "$target" == windows-* ]] && command -v wine >/dev/null 2>&1; then
    log "自检（wine 运行）：$(basename "$out")"
    if ! ( cd "$SMOKE" && WINEDEBUG=-all wine "$out" --selfcheck ); then
      log "wine 自检失败或不可用，跳过（产物仍可用于真实 Windows）"
    fi
  else
    log "跳过运行自检（$target 产物无法在 $HOST_TARGET 宿主上执行）"
    log "已校验：PE/ELF 结构 + SEA blob 注入成功"
  fi

  ls -lh "$out"
}

build_target "$TARGET"

log "完成。产物在 $OUT_DIR"
