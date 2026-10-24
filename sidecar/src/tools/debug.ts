/**
 * 调试直调（`!tool`）的只读工具白名单。
 *
 * `tool_debug` 不经过 AI，也不经过 `tool_call` 确认钩子；因此 v1 只允许
 * 不改变游戏状态、不写文件的工具。这里刻意**不**包含：
 *   - `game_copy_blueprint`（写蓝图文件）
 *   - `blueprint_fs`（可 write/rm/rmdir）
 *   - `blueprint_save` / `blueprint_generate`（写蓝图文件）
 *   - `run_script`（脚本目前还能通过 dsp.call 调到 game_copy_blueprint）
 *
 * 新增工具时同时更新这里和 `docs/protocol-v1.md` 的调试白名单。
 */
export const DEBUG_TOOL_NAMES = [
  // game.*：只读查询/分析/截图
  'game_get_overview',
  'game_get_player',
  'game_get_planet',
  'game_get_power',
  'game_get_research',
  'game_get_production',
  'game_get_entity',
  'game_get_entities',
  'game_get_progress',
  'game_capture_view',
  'game_render_layout',
  'game_get_planets',
  'game_trace_line',
  'game_get_logistics',
  'game_analyze_area',
  // sidecar 本地只读工具
  'kb_search',
  'kb_get',
  'kb_recipe_tree',
  'kb_plan',
  'wiki_search',
  'wiki_page',
  'blueprint_search',
  'blueprint_info',
] as const;

const ALLOWED = new Set<string>(DEBUG_TOOL_NAMES);

/** 是否允许通过 `tool_debug` 直接调用。 */
export function isDebugToolAllowed(name: string): boolean {
  return ALLOWED.has(name);
}
