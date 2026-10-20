import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// tsc 只编译 .ts；planner.mjs 是 vendored 的纯 JS，需要手动复制到 dist。
const root = dirname(dirname(fileURLToPath(import.meta.url)));
const source = join(root, 'src', 'blueprint', 'planner.mjs');
const target = join(root, 'dist', 'blueprint', 'planner.mjs');
mkdirSync(dirname(target), { recursive: true });
copyFileSync(source, target);
