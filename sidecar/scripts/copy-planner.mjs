import { copyFileSync, mkdirSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// tsc 只编译 .ts；blueprint/ 下的纯 JS（planner / validate / colliders）需要手动复制到 dist。
const root = dirname(dirname(fileURLToPath(import.meta.url)));
const sourceDir = join(root, 'src', 'blueprint');
const targetDir = join(root, 'dist', 'blueprint');
mkdirSync(targetDir, { recursive: true });
for (const file of readdirSync(sourceDir)) {
  if (!file.endsWith('.mjs')) continue;
  copyFileSync(join(sourceDir, file), join(targetDir, file));
}
