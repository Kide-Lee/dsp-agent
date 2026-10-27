#!/usr/bin/env node
/**
 * 提示词装配的离线回归。
 *
 * 覆盖：
 *   - prompts/*.md → src/prompts.generated.ts 的生成物是否最新（防止手改生成物/忘记重生成）；
 *   - 人格里不再手写工具清单、不含 pi 默认 prompt 的特征串、不含里程碑标记；
 *   - legacy / rich 两版只在“回复格式”段不同；
 *   - 能力段落按注册表分组生成、规则段落真的渲染出 promptGuidelines；
 *   - --system-prompt-file 读取失败/空文件必须报错，不能静默回落。
 *
 *   node test/prompts.mjs
 */

import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import {
  ICARUS_II_PERSONA_NAME,
  ICARUS_II_RICH_TEXT_PROMPT,
  ICARUS_II_SYSTEM_PROMPT,
} from '../dist/persona.js';
import {
  groupToolsForPrompt,
  renderCapabilitySection,
  renderToolRules,
  toolsSignature,
} from '../dist/prompt/sections.js';
import { loadPromptOverride } from '../dist/prompt/override.js';
import { generatePromptsSource, OUTPUT_PATH } from '../scripts/gen-prompts.mjs';
import { check, summary } from './helpers.mjs';

/** pi 默认 coding prompt 的特征串：一个都不能出现在人格里。 */
const PI_DEFAULT_MARKERS = [
  'You are an expert coding assistant',
  'coding agent harness',
  'Available tools:',
  'Current working directory',
  'Pi documentation',
  'AGENTS.md',
  'CLAUDE.md',
];

/** 伪造一个只带名字与规则的 ToolDefinition（渲染器只读这两个字段）。 */
function fakeTool(name, guidelines) {
  return { name, description: '', promptGuidelines: guidelines };
}

console.log('── 生成物新鲜度 ──');
{
  const expected = generatePromptsSource();
  const actual = readFileSync(OUTPUT_PATH, 'utf8');
  check(
    'src/prompts.generated.ts 与 prompts/*.md 一致',
    actual === expected,
    '生成物已过期：先运行 `npm run gen:prompts`（build/typecheck 会自动跑）',
  );
}

console.log('\n── 人格内容 ──');
{
  const text = ICARUS_II_SYSTEM_PROMPT;
  check('含人格标识', text.includes(ICARUS_II_PERSONA_NAME), ICARUS_II_PERSONA_NAME);
  check('没有未替换的占位符', !text.includes('{{'), text.slice(0, 80));
  for (const marker of PI_DEFAULT_MARKERS) {
    check(`不含 pi 特征串「${marker}」`, !text.includes(marker));
  }
  check('不含里程碑标记（M1–M6）', !/\bM[1-6]\b/.test(text), (text.match(/\bM[1-6]\b/) ?? []).join(','));
  // 工具清单由 prompt/sections.ts 按注册表生成；人格里点名具体工具 = 迟早腐烂。
  const toolNames = text.match(/\b(?:game|kb|wiki|blueprint)_[a-z_]+\b/g) ?? [];
  check('人格不手写具体工具名', toolNames.length === 0, toolNames.join(','));
  check('人格保留格式占位内容（读取游戏状态段）', text.includes('读取游戏状态'));
  check('人格保留能力边界', text.includes('能力边界') && text.includes('以本轮函数列表为唯一权威'));
  check('人格长度在预算内（≤ 3000 字符）', text.length <= 3000, `${text.length} 字符`);
}

console.log('\n── legacy / rich 两个变体 ──');
{
  const legacy = ICARUS_II_SYSTEM_PROMPT;
  const rich = ICARUS_II_RICH_TEXT_PROMPT;
  check('legacy 用游戏富文本', legacy.includes('不要输出 Markdown'));
  check('rich 用标准 Markdown', rich.includes('标准 Markdown'));
  check('legacy 不含 Markdown 格式说明', !legacy.includes('标准 Markdown'));
  check('rich 不含 legacy 的告警标题', !rich.includes('否则玩家看到的是一堆符号'));
  for (const section of ['读取游戏状态', '知识库', '能力边界']) {
    check(`两版都含「${section}」段`, legacy.includes(section) && rich.includes(section));
  }
  check('两版都不是同一份文本', legacy !== rich);
}

console.log('\n── 工具规则语言 ──');
{
  // 规则会和工具描述一起进 system prompt；英文模板句（"Use X when..."）已统一成中文。
  const sources = [
    '../src/tools/registry.ts',
    '../src/tools/write.ts',
    '../src/tools/ask.ts',
    '../src/tools/script.ts',
    '../src/kb/tools.ts',
    '../src/wiki/tools.ts',
    '../src/blueprint/tools.ts',
  ];
  for (const relative of sources) {
    const text = readFileSync(new URL(relative, import.meta.url), 'utf8');
    check(
      `${relative} 的规则不含英文模板句`,
      !/\bUse (?:game|kb|wiki|blueprint|run_script|ask_user)/.test(text),
    );
  }
}

console.log('\n── 能力段落（按注册表生成） ──');
{
  const tools = [
    fakeTool('game_get_overview'),
    fakeTool('game_get_planet'),
    fakeTool('game_place_marker'), // risk 1
    fakeTool('game_set_recipe'), // risk 2
    fakeTool('game_copy_blueprint'), // risk 1（在 WRITE_TOOL_NAMES 里）
    fakeTool('kb_search'),
    fakeTool('wiki_search'),
    fakeTool('blueprint_fs'),
    fakeTool('run_script'),
    fakeTool('ask_user'),
    fakeTool('unexpected_tool'),
  ];
  const section = renderCapabilitySection(tools);
  check('空工具集返回 null', renderCapabilitySection([]) === null);
  check('段落非空', typeof section === 'string' && section.length > 0);
  check('标注总数', section.includes(`共 ${tools.length} 个`), section.split('\n')[0]);
  check('只读组含概览', /游戏只读（2 个）：game_get_overview、game_get_planet/.test(section));
  check('低风险写组含标记与复制蓝图', /低风险写[^\n]*game_place_marker、game_copy_blueprint/.test(section));
  check('存档写组含改配方', /改存档写[^\n]*game_set_recipe/.test(section));
  check('知识库/Wiki/蓝图/脚本/交互各自成组', ['知识库（只读', 'Wiki（只读', '蓝图（读/写', '脚本（默认只读', '交互（向玩家提问'].every((s) => section.includes(s)));
  check('未知工具落到“其他”组', section.includes('其他（1 个）：unexpected_tool'));
  for (const tool of tools) {
    check(`列出 ${tool.name}`, section.includes(tool.name));
  }

  const groups = groupToolsForPrompt(tools);
  const flattened = groups.flatMap((group) => group.names);
  check('每个工具只出现一次', flattened.length === new Set(flattened).size, flattened.join(','));
  check('分组不丢工具', flattened.length === tools.length, `${flattened.length}/${tools.length}`);
}

console.log('\n── 工具规则段落 ──');
{
  const rules = [
    fakeTool('game_get_entity', ['stale=true 时不要使用这条数据。', '先只取需要的段。']),
    fakeTool('game_get_overview', []),
    fakeTool('run_script', ['不要用 Date.now/Math.random。']),
  ];
  const section = renderToolRules(rules);
  check('无规则时返回 null', renderToolRules([fakeTool('a', [])]) === null);
  check('有标题', section.startsWith('工具使用规则'));
  check('按工具分组', section.includes('- game_get_entity：') && section.includes('- run_script：'));
  check('保留每条规则', section.includes('stale=true 时不要使用这条数据。') && section.includes('不要用 Date.now/Math.random。'));
  check('没有规则的工具体不出现', !section.includes('game_get_overview'));

  const signature = toolsSignature(rules);
  check('签名随工具集变化', signature !== toolsSignature(rules.slice(0, 2)), signature);
}

console.log('\n── system prompt 文件覆盖 ──');
{
  const text = '自定义人格 {{NOT_A_PLACEHOLDER}}\n';
  const override = loadPromptOverride('/tmp/prompt.md', () => text);
  check('返回绝对路径', override.path.endsWith('/tmp/prompt.md'), override.path);
  check('文本已 trim', override.text === text.trim(), JSON.stringify(override.text));
  check(
    'sha256 是文本摘要',
    override.sha256 === createHash('sha256').update(text.trim(), 'utf8').digest('hex'),
  );

  let threw = '';
  try {
    loadPromptOverride('/tmp/empty.md', () => '   \n');
  } catch (err) {
    threw = err.message;
  }
  check('空文件报错', threw.includes('空文件'), threw);

  threw = '';
  try {
    loadPromptOverride('/tmp/missing.md', () => {
      throw new Error('ENOENT');
    });
  } catch (err) {
    threw = err.message;
  }
  check('读取失败报错且带路径', threw.includes('读取失败') && threw.includes('/tmp/missing.md'), threw);
}

const ok = summary('提示词装配测试');
process.exit(ok ? 0 : 1);
