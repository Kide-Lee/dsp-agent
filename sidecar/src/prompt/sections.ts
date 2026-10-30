/**
 * 每轮 system prompt 的“能力段落”：按实际注册的工具生成，不再手写清单。
 *
 * 为什么生成：人格里手写工具名会腐烂——新增工具后模型不知道，删掉工具后模型还在假装能做。
 * （实机踩过：人格仍称“只能读”，模型据此拒绝调用写工具。）
 *
 * 段落顺序按“稳定 → 易变”排：只读 → 写 → 知识库/Wiki/蓝图 → 脚本/交互，
 * 这样同一能力集下字节稳定，对上游的 prompt cache 友好。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { isWriteToolName, writeToolRisk } from '../tools/write.js';

export const CAPABILITY_HEADER = '本会话注册的工具（以本轮函数列表为唯一权威）：';

interface CapabilityGroup {
  title: string;
  names: readonly string[];
}

/**
 * 把工具按「只读 / 低风险写 / 改存档写 / 知识库 / Wiki / 蓝图 / 脚本 / 交互」分组。
 * 未知名字落到“其他”，保证不静默丢工具。
 */
export function groupToolsForPrompt(tools: readonly ToolDefinition[]): CapabilityGroup[] {
  const names = tools.map((tool) => tool.name);
  const game = names.filter((name) => name.startsWith('game_'));
  const gameWrites = game.filter((name) => isWriteToolName(name));
  const groups: CapabilityGroup[] = [
    { title: '游戏只读', names: game.filter((name) => !isWriteToolName(name)) },
    {
      title: '低风险写（不改存档；受限档直接执行，只读档仍会确认）',
      names: gameWrites.filter((name) => writeToolRisk(name) === 1),
    },
    {
      title: '改存档写（受限/只读档会先请求确认；接管档直接执行）',
      names: gameWrites.filter((name) => writeToolRisk(name) === 2),
    },
    {
      title: '第三方 mod（发现与配置；set 会按权限确认）',
      names: names.filter((name) => name.startsWith('mods_') || name.startsWith('mod_')),
    },
    {
      title: '知识库（只读，sidecar 本地数据）',
      names: names.filter((name) => name.startsWith('kb_')),
    },
    { title: 'Wiki（只读）', names: names.filter((name) => name.startsWith('wiki_')) },
    {
      title: '蓝图（读/写本地蓝图文件；写入按权限档位确认）',
      names: names.filter((name) => name.startsWith('blueprint_')),
    },
    {
      title: '脚本（默认只读；脚本内写调用需脚本级确认）',
      names: names.filter((name) => name === 'run_script'),
    },
    { title: '交互（向玩家提问）', names: names.filter((name) => name === 'ask_user') },
  ];

  const covered = groups.flatMap((group) => group.names);
  const other = names.filter((name) => !covered.includes(name));
  if (other.length > 0) groups.push({ title: '其他', names: other });

  return groups.filter((group) => group.names.length > 0);
}

/** 渲染能力段落；没有工具时返回 null（纯对话/连接异常）。 */
export function renderCapabilitySection(tools: readonly ToolDefinition[]): string | null {
  if (tools.length === 0) return null;
  const lines = [`${CAPABILITY_HEADER}共 ${tools.length} 个`];
  for (const group of groupToolsForPrompt(tools)) {
    lines.push(`- ${group.title}（${group.names.length} 个）：${group.names.join('、')}`);
  }
  return lines.join('\n');
}

/**
 * 把各工具的 `promptGuidelines` 渲染成“工具使用规则”段落。
 *
 * pi 的 DefaultResourceLoader 只会在默认 coding prompt 分支里拼这些规则；
 * 我们整轮替换 system prompt，所以必须自己渲染——否则这些规则是死代码
 * （既不在 system prompt，也不在工具描述里，模型永远看不到）。
 */
export function renderToolRules(tools: readonly ToolDefinition[]): string | null {
  const entries: string[] = [];
  for (const tool of tools) {
    const rules = (tool.promptGuidelines ?? []).map((rule) => rule.trim()).filter(Boolean);
    if (rules.length === 0) continue;
    const body = rules.map((rule) => `  - ${rule}`).join('\n');
    entries.push(`- ${tool.name}：\n${body}`);
  }
  if (entries.length === 0) return null;
  return ['工具使用规则（与工具描述同等效力）：', ...entries].join('\n');
}

/** 供测试断言“同一工具集生成同一字符串”。 */
export function toolsSignature(tools: readonly ToolDefinition[]): string {
  return tools
    .map((tool) => `${tool.name}:${(tool.promptGuidelines ?? []).length}`)
    .join(',');
}
