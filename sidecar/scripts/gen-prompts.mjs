#!/usr/bin/env node
/**
 * 把 sidecar/prompts/system.zh.md 编译成 src/prompts.generated.ts。
 *
 * 为什么是编译期生成，而不是运行时读文件：
 *   sidecar 用 esbuild 打成单文件、再塞进 Node SEA（scripts/build-sidecar.sh）。
 *   运行时读 prompts/ 目录意味着要额外分发资源文件，和单文件发布方式冲突。
 *   生成成 TS 常量后，提示词跟二进制一起走，行为可复现、可 diff、可快照测试。
 *
 * 只有一份提示词：mod 已统一支持 ui.richtext.v2，不再保留“只用游戏标签”的 legacy 版本，
 * 也不再需要 {{FORMAT}} / {{NAME}} 之类的占位符——文本直接写在 markdown 里。
 */

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const SIDECAR_ROOT = join(HERE, '..');
export const PROMPTS_DIR = join(SIDECAR_ROOT, 'prompts');
export const PROMPT_FILE = 'system.zh.md';
export const OUTPUT_PATH = join(SIDECAR_ROOT, 'src', 'prompts.generated.ts');

/** 读取并规范化提示词源文件。 */
export function readPromptSource() {
  return readFileSync(join(PROMPTS_DIR, PROMPT_FILE), 'utf8').replace(/\r\n/g, '\n').trim();
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
  const text = readPromptSource();
  // 占位符机制已移除；残留的 {{...}} 说明有人还在旧写法上改，直接让构建失败。
  if (text.includes('{{')) {
    throw new Error(`${PROMPT_FILE}：不再支持占位符，请把 {{...}} 换成实际文本`);
  }

  return [
    '// 本文件由 scripts/gen-prompts.mjs 从 prompts/system.zh.md 生成，不要手改。',
    '// 改提示词：编辑 sidecar/prompts/system.zh.md，然后运行 `npm run gen:prompts`（build/typecheck 会自动跑）。',
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
