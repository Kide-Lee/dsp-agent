#!/usr/bin/env node
/**
 * 把 sidecar/prompts/*.md 编译成 src/prompts.generated.ts。
 *
 * 为什么是编译期生成，而不是运行时读文件：
 *   sidecar 用 esbuild 打成单文件、再塞进 Node SEA（scripts/build-sidecar.sh）。
 *   运行时读 prompts/ 目录意味着要额外分发资源文件，和单文件发布方式冲突。
 *   生成成 TS 常量后，提示词跟二进制一起走，行为可复现、可 diff、可快照测试。
 *
 * 占位符：
 *   {{NAME}}   助手名字（ICARUS_II_PERSONA_NAME）
 *   {{FORMAT}} 回复格式片段（legacy / rich 两版）
 *
 * 未替换的占位符会让构建直接失败——不要静默回退（旧的 indexOf 方案就踩过这个坑）。
 */

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const SIDECAR_ROOT = join(HERE, '..');
export const PROMPTS_DIR = join(SIDECAR_ROOT, 'prompts');
export const OUTPUT_PATH = join(SIDECAR_ROOT, 'src', 'prompts.generated.ts');

/** 助手身份标识（mod 名）。测试会断言最终 system message 里含这个串。 */
export const ICARUS_II_PERSONA_NAME = '伊卡洛斯·贰';

const PLACEHOLDER = /\{\{([A-Z_]+)\}\}/g;

function readPrompt(name) {
  return readFileSync(join(PROMPTS_DIR, name), 'utf8').replace(/\r\n/g, '\n').trim();
}

/** 替换占位符；未知或残留占位符一律抛错，避免把 `{{FORMAT}}` 原样发给模型。 */
export function fillPlaceholders(template, values, label) {
  const unknown = [];
  const filled = template.replace(PLACEHOLDER, (_match, key) => {
    if (Object.prototype.hasOwnProperty.call(values, key)) return values[key];
    unknown.push(key);
    return '';
  });
  if (unknown.length > 0) {
    throw new Error(`${label}：未知占位符 ${unknown.map((key) => `{{${key}}}`).join('、')}`);
  }
  const leftover = filled.match(PLACEHOLDER);
  if (leftover) {
    throw new Error(`${label}：占位符未替换 ${leftover.join('、')}`);
  }
  return filled;
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
  const system = readPrompt('system.zh.md');
  const legacyFormat = readPrompt('format.legacy.zh.md');
  const richFormat = readPrompt('format.rich.zh.md');

  const name = ICARUS_II_PERSONA_NAME;
  const legacy = fillPlaceholders(system, { NAME: name, FORMAT: legacyFormat }, 'prompts/system.zh.md');
  const rich = fillPlaceholders(system, { NAME: name, FORMAT: richFormat }, 'prompts/system.zh.md');

  return [
    '// 本文件由 scripts/gen-prompts.mjs 从 prompts/*.md 生成，不要手改。',
    '// 改提示词：编辑 sidecar/prompts/*.md，然后运行 `npm run gen:prompts`（build/typecheck 会自动跑）。',
    '',
    `export const ICARUS_II_PERSONA_NAME = ${JSON.stringify(name)};`,
    '',
    '/** 最终发给上游的 system message（legacy 富文本格式；不含任何模型平台的工作目录/工具段落）。 */',
    `export const ICARUS_II_SYSTEM_PROMPT = ${toTemplateLiteral(legacy)};`,
    '',
    '/** 支持 ui.richtext.v2 的 mod 用这一份：只有“回复格式”段不同。 */',
    `export const ICARUS_II_RICH_TEXT_PROMPT = ${toTemplateLiteral(rich)};`,
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
