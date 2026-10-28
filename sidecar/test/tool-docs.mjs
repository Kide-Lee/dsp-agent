#!/usr/bin/env node
/**
 * 工具文档一致性测试。
 *
 * `docs/tools/*.md` 与 `docs/tools/tools.json` 是生成物：只要改了任何工具的
 * description / promptGuidelines / 参数 / risk，就必须重新生成，否则模型真实看到的
 * 文本会和文档不一致（这正是手写文档最容易烂的地方）。
 *
 * 这里只做“是否最新”的判定，具体差异由生成器 --check 输出。
 *
 *   node test/tool-docs.mjs
 */

import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { check, summary } from './helpers.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const tsx = join(root, 'node_modules', '.bin', process.platform === 'win32' ? 'tsx.cmd' : 'tsx');

const result = spawnSync(tsx, [join(root, 'scripts', 'gen-tool-docs.mts'), '--check'], {
  cwd: root,
  encoding: 'utf8',
  timeout: 120_000,
});

const output = `${result.stdout ?? ''}${result.stderr ?? ''}`.trim();
check(
  '工具文档与当前工具定义一致',
  result.status === 0,
  result.status === 0
    ? ''
    : `${output}\n（运行：npx tsx sidecar/scripts/gen-tool-docs.mts 重新生成）`,
);

summary('tool-docs');
