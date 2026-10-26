/**
 * 权限档位对工具集的约束。
 *
 * 这里只放「工具名级」的判断；具体执行时的双重门禁分别在：
 *   - `session.ts`：所有 game 工具（risk / 工具名）
 *   - `tools/script.ts`：脚本里的字面量写调用
 *   - `blueprint/tools.ts`：blueprint_fs 的 mkdir/write/rm/rmdir
 *
 * 把名单放在一处，避免「只读」在某个入口漏掉。
 */

import { WRITE_TOOL_NAMES } from './tools/write.js';

/**
 * 只读档位下完全不注册、也不执行的工具。
 *
 * 注意：`blueprint_fs` 与 `run_script` 不在这里——它们既有读操作也可能写，
 * 由各自执行入口按 op/脚本文本判断，保留只读用法（ls/cat/纯查询脚本）。
 */
export const READONLY_BLOCKED_TOOL_NAMES: ReadonlySet<string> = new Set<string>([
  ...WRITE_TOOL_NAMES,
  // 只写蓝图文件、不改游戏状态的工具：有限/接管放行，只读拒绝。
  'game_copy_blueprint',
  'blueprint_save',
  'blueprint_generate',
]);

export function isReadonlyBlockedTool(name: string): boolean {
  return READONLY_BLOCKED_TOOL_NAMES.has(name);
}
