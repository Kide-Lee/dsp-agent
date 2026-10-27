#!/usr/bin/env node
/**
 * 把 sidecar/prompts/system.zh.md 编译成 src/prompts.generated.ts。
 *
 * 为什么是编译期生成，而不是运行时读文件：
 *   sidecar 用 esbuild 打成单文件、再塞进 Node SEA（scripts/build-sidecar.sh）。
 *   运行时读 prompts/ 目录意味着要额外分发资源文件，和单文件发布方式冲突。
 *   生成成 TS 常量后，提示词跟二进制一起走，行为可复现、可 diff、可快照测试。
 *
 * 本地化引用：`{{顾问字幕42}}` 会在构建期从游戏安装目录的
 *   `Locale/<lcid>/base.txt`（UTF-16LE、Tab 分隔）取第 4 列文本并替换。
 *   - 目录探测顺序：ICARUS_II_LOCALE_DIR / DSP_LOCALE_DIR → Steam 常见路径；
 *   - locale 默认 2052（zh-CN），可用 ICARUS_II_PROMPT_LOCALE 覆盖；
 *   - 原文里的游戏标记会规范化成 mod 语法：`<color=#RRGGBBAA>` → `<color value="#RRGGBBAA">`、
 *     字面量 `\r\n` → 换行、`<b>` 去掉、U+00A0/U+2009 → 普通空格；
 *   - 缺条目、含 `$LKEY` 参数引用（运行期才能展开）都会让构建失败，不静默放过。
 */

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const SIDECAR_ROOT = join(HERE, '..');
export const PROMPTS_DIR = join(SIDECAR_ROOT, 'prompts');
export const PROMPT_FILE = 'system.zh.md';
export const OUTPUT_PATH = join(SIDECAR_ROOT, 'src', 'prompts.generated.ts');

const DEFAULT_LOCALE = '2052';
const REFERENCE = /\{\{\s*([^{}]+?)\s*\}\}/g;

/** 读取并规范化提示词源文件。 */
export function readPromptSource() {
  return readFileSync(join(PROMPTS_DIR, PROMPT_FILE), 'utf8').replace(/\r\n/g, '\n').trim();
}

/** 候选 Locale 目录；env 可以指向 `Locale/`，也可以指向游戏根目录。 */
export function candidateLocaleDirs(env = process.env, home = homedir()) {
  const dirs = [];
  const override = env.ICARUS_II_LOCALE_DIR ?? env.DSP_LOCALE_DIR;
  if (override) {
    dirs.push(override, join(override, 'Locale'));
  }
  dirs.push(
    join(home, '.local', 'share', 'Steam', 'steamapps', 'common', 'Dyson Sphere Program', 'Locale'),
    join(home, '.steam', 'steam', 'steamapps', 'common', 'Dyson Sphere Program', 'Locale'),
  );
  const programFiles = env['ProgramFiles(x86)'] ?? env.ProgramFiles;
  if (programFiles) {
    dirs.push(join(programFiles, 'Steam', 'steamapps', 'common', 'Dyson Sphere Program', 'Locale'));
  }
  return [...new Set(dirs)];
}

/** 解析 locale id；只接受数字，避免拿用户输入拼出奇怪路径。 */
export function resolveLocaleId(env = process.env) {
  const raw = env.ICARUS_II_PROMPT_LOCALE ?? DEFAULT_LOCALE;
  if (!/^\d{3,5}$/.test(raw)) {
    throw new Error(`ICARUS_II_PROMPT_LOCALE 必须是数字 locale id（例如 2052），收到 "${raw}"`);
  }
  return raw;
}

/** 找到 `Locale/<locale>/base.txt`；找不到时给出可操作的报错。 */
export function findLocaleFile(localeId, env = process.env, home = homedir()) {
  const dirs = candidateLocaleDirs(env, home);
  for (const dir of dirs) {
    const file = join(dir, localeId, 'base.txt');
    if (existsSync(file)) return file;
  }
  throw new Error(
    `找不到游戏本地化文件 Locale/${localeId}/base.txt；` +
      `请设置 ICARUS_II_LOCALE_DIR=/path/to/Dyson Sphere Program/Locale（或指向游戏根目录）。` +
      `已尝试：${dirs.join('、')}`,
  );
}

/**
 * 解析 base.txt：UTF-16LE、CRLF、Tab 分隔 4 列（key / ? / ? / text）。
 * 同一 key 只取第一次出现（游戏文件里没有重复，但保持确定性）。
 */
export function parseLocaleTable(content) {
  const table = new Map();
  const text = content.replace(/^\uFEFF/, '');
  for (const row of text.split(/\r?\n/)) {
    if (!row) continue;
    const columns = row.split('\t');
    if (columns.length < 4) continue;
    const key = columns[0].trim();
    if (!key || table.has(key)) continue;
    table.set(key, columns.slice(3).join('\t'));
  }
  return table;
}

export function loadLocaleTable(localeId, env = process.env) {
  const file = findLocaleFile(localeId, env);
  return { file, table: parseLocaleTable(readFileSync(file, 'utf16le')) };
}

/**
 * 把游戏本地化原文规范化成 mod 认的富文本语法。
 * 规则窄且显式：只转换 color/size 的 `key=value` 写法，剥掉 `<b>`，其余标签原样保留。
 */
export function normalizeLocalizedText(text) {
  if (/\$LKEY/.test(text)) {
    throw new Error('该条目含 $LKEY 运行期参数引用，无法静态展开');
  }
  const normalized = text
    // 游戏文本里的字面量转义（注意是反斜杠 + 字母，不是控制字符）。
    .replace(/\\r\\n/g, '\n')
    .replace(/\\n/g, '\n')
    .replace(/\\t/g, ' ')
    // 窄空格/不换行空格统一成普通空格，避免提示词里出现看不见的排版字符。
    .replace(/[\u00A0\u2009]/g, ' ')
    // 原文偶发「枯竭 !」这种空格+标点；转成中文习惯的“枯竭!”。
    .replace(/[ \t]+([!?！？，,。.；;：:])/g, '$1')
    // <color=#RRGGBB[AA]> / <color="#RRGGBB[AA]"> → <color value="#RRGGBB[AA]">
    .replace(/<color\s*=\s*"?([#0-9A-Za-z]+)"?\s*>/gi, '<color value="$1">')
    // <size=20> / <size="20"> → <size value="20">
    .replace(/<size\s*=\s*"?([0-9]+)"?\s*>/gi, '<size value="$1">')
    // mod 的渲染器不支持 <b>，粗体要写 Markdown。
    .replace(/<\/?b>/gi, '');
  return normalized.trim();
}

/** 替换 `{{本地化键}}`。缺条目或缺 locale 都直接抛错。 */
export function resolvePromptReferences(template, table) {
  const missing = new Set();
  const resolved = template.replace(REFERENCE, (_match, rawKey) => {
    const key = rawKey.trim();
    const value = table.get(key);
    if (value === undefined) {
      missing.add(key);
      return '';
    }
    try {
      return normalizeLocalizedText(value);
    } catch (err) {
      throw new Error(`本地化条目「${key}」：${err.message}`);
    }
  });
  if (missing.size > 0) {
    throw new Error(`本地化条目缺失：${[...missing].map((key) => `{{${key}}}`).join('、')}`);
  }
  return resolved;
}

/** 转成可读的模板字符串字面量：转义反斜杠、反引号和 `${`。 */
function toTemplateLiteral(text) {
  const escaped = text
    .replace(/\\/g, '\\\\')
    .replace(/`/g, '\\`')
    .replace(/\$\{/g, '\\${');
  return `\`${escaped}\``;
}

/**
 * 生成 prompts.generated.ts 的内容。
 * 纯函数，方便测试直接对比“仓库里的生成物是否过期”。
 */
export function generatePromptsSource() {
  let text = readPromptSource();
  if (text.includes('{{')) {
    const localeId = resolveLocaleId();
    const { table } = loadLocaleTable(localeId);
    text = resolvePromptReferences(text, table);
    if (text.includes('{{')) {
      throw new Error(`${PROMPT_FILE}：本地化引用未完全替换`);
    }
  }

  return [
    '// 本文件由 scripts/gen-prompts.mjs 从 prompts/system.zh.md 生成，不要手改。',
    '// 改提示词：编辑 sidecar/prompts/system.zh.md，然后运行 `npm run gen:prompts`（build/typecheck 会自动跑）。',
    '// {{本地化键}} 在生成时从游戏 Locale/<lcid>/base.txt 取文本并规范化。',
    '',
    '/** 最终发给上游的 system message（不含任何模型平台的工作目录/工具段落）。 */',
    `export const ICARUS_II_SYSTEM_PROMPT = ${toTemplateLiteral(text)};`,
    '',
  ].join('\n');
}

export function writePromptsSource({ check = false } = {}) {
  const source = generatePromptsSource();
  let current = null;
  try {
    current = readFileSync(OUTPUT_PATH, 'utf8');
  } catch {
    current = null;
  }
  if (check) {
    return { changed: current !== source, source };
  }
  if (current !== source) {
    writeFileSync(OUTPUT_PATH, source, 'utf8');
  }
  return { changed: current !== source, source };
}

const invokedDirectly =
  process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;

if (invokedDirectly) {
  const { changed } = writePromptsSource();
  process.stdout.write(
    changed ? `已生成 ${OUTPUT_PATH}\n` : `提示词已是生成后的最新状态：${OUTPUT_PATH}\n`,
  );
}
