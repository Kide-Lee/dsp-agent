/**
 * system prompt 文件覆盖（开发/评测用，不进游戏 UI）。
 *
 * 为什么只做“启动读一次”：线上行为必须可复现。热重载会让同一版本出现
 * “别人能跑你不能跑”的提示词状态，bug report 也无法对上版本。
 * 为什么失败要抛错：静默回落到内置人格，会让人以为文件生效了（旧 indexOf
 * 静默回退就踩过这个坑）。
 */

import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

export interface PromptOverride {
  /** 绝对路径。 */
  path: string;
  text: string;
  /** 文本的 SHA-256；日志与测试用它对齐“到底用了哪份”。 */
  sha256: string;
}

/**
 * 读取并校验覆盖文件。
 *
 * 覆盖的是整份人格（含回复格式段），rich/legacy 两个变体会收到同一份文本：
 * 评测需要的是一个完全可控的 prompt，而不是半替换。
 */
export function loadPromptOverride(filePath: string, read: (path: string) => string = defaultRead): PromptOverride {
  const path = resolve(filePath);
  let text: string;
  try {
    text = read(path);
  } catch (err) {
    throw new Error(`--system-prompt-file 读取失败：${path}（${(err as Error).message}）`);
  }
  const normalized = text.replace(/\r\n/g, '\n').trim();
  if (normalized.length === 0) {
    throw new Error(`--system-prompt-file 是空文件：${path}`);
  }
  return {
    path,
    text: normalized,
    sha256: createHash('sha256').update(normalized, 'utf8').digest('hex'),
  };
}

function defaultRead(path: string): string {
  return readFileSync(path, 'utf8');
}
