/**
 * 生成 docs/tools/ 下的工具参考文档。
 *
 * 为什么生成而不是手写：工具定义分散在 8 个 TS 模块里，50 个工具的
 * description / promptGuidelines / 参数 schema 手抄必然漂移。这里直接从
 * 运行期 ToolDefinition 取值，并补上源码里的 timeout / 能力位 / risk，
 * 以及 mod 侧 C# 实现里的返回字段名。
 *
 * 用法：
 *   npx tsx sidecar/scripts/gen-tool-docs.mts           # 生成
 *   npx tsx sidecar/scripts/gen-tool-docs.mts --check   # 只校验是否最新
 *
 * 改了任何工具的 description / guidelines / 参数后必须重新运行，
 * 否则 `npm test` 里的 tool-docs 用例会失败。
 */

import fs from 'node:fs';
import path from 'node:path';

import { createGameTools, toolNamesForCapabilities } from '../src/tools/registry.js';
import { createAskTool } from '../src/tools/ask.js';
import { createScriptTool } from '../src/tools/script.js';
import { createKbTools } from '../src/kb/tools.js';
import { createWikiTools } from '../src/wiki/tools.js';
import { createBlueprintTools } from '../src/blueprint/tools.js';
import { isWriteToolName, writeToolRisk } from '../src/tools/write.js';
import { TOOL_FIELD_NOTES, type FieldNote } from './tool-field-notes.mjs';

const ROOT = path.resolve(import.meta.dirname, '../..');
const DOCS_DIR = path.join(ROOT, 'docs/tools');
const SNAPSHOT = path.join(DOCS_DIR, 'tools.json');

/** 能力位顺序：与 toolNamesForCapabilities 无关，只用于反查映射。 */
const CAPABILITIES = [
  'game.read.v1',
  'game.entity.v1',
  'game.progress.v1',
  'game.view.v1',
  'game.analyze.v1',
  'game.write.v1',
  'game.drop.v1',
  'game.build.v1',
  'game.storage.v1',
  'game.tether.v1',
  'game.todo.v1',
  'mod.bridge.v1',
  'ui.tree.v1',
] as const;

/** 文档分组：每个文件覆盖一组能力位（或本地工具）。 */
interface DocGroup {
  file: string;
  title: string;
  intro: string;
  /** 按能力位匹配；`null` 表示按工具名匹配。 */
  capabilities?: string[];
  names?: string[];
}

const GROUPS: DocGroup[] = [
  {
    file: 'game-read.md',
    title: '只读 · 存档与产线概览',
    intro:
      '最常用的入口：先看清玩家在哪、有什么、缺什么，再决定要不要下钻。' +
      '六个工具都只读存档快照，不改变游戏状态。',
    capabilities: ['game.read.v1'],
  },
  {
    file: 'game-entity.md',
    title: '只读 · 建筑、星球与拓扑',
    intro:
      '从“这片区域有什么”到“这条线断在哪”。实体 id 是这一组工具的共同货币：' +
      '先用列表类工具拿 id，再用详情类工具下钻。',
    capabilities: ['game.entity.v1'],
  },
  {
    file: 'game-progress.md',
    title: '只读 · 进度、截图与区域分析',
    intro:
      '回答“我现在到哪一步”“画面上是什么”“这片产线多大”。' +
      '截图会以图片内容块返回，其余是文本。',
    capabilities: ['game.progress.v1', 'game.view.v1', 'game.analyze.v1'],
  },
  {
    file: 'game-write-basic.md',
    title: '写操作 · 标记、面板、研究与丢弃',
    intro:
      '低风险的界面/标记类写操作，以及两个会把物品或研究状态写进存档的工具。' +
      '确认与撤销语义见 [index.md](index.md#写操作的确认与撤销)。',
    capabilities: ['game.write.v1', 'game.drop.v1'],
  },
  {
    file: 'game-write-build.md',
    title: '写操作 · 配方、投料与仓储',
    intro:
      '改变产线和库存的写操作。配方与投料可撤销一层；`game_take_output` 是**不可撤销**的，' +
      '执行后撤销栈顶会被它占住。',
    capabilities: ['game.build.v1', 'game.storage.v1'],
  },
  {
    file: 'game-assist.md',
    title: '辅助 · 方位指示与游戏目标',
    intro:
      'tether 在屏幕上给玩家指方向；todo 写的是游戏原生“目标”面板，不是 mod 自己的清单。',
    capabilities: ['game.tether.v1', 'game.todo.v1'],
  },
  {
    file: 'kb.md',
    title: '知识库（sidecar 本地）',
    intro:
      '离线数据，不依赖 mod 连接。数值与文本以游戏版本 + 语言为缓存键，' +
      '不会让模型凭记忆补数值。',
    capabilities: [],
    names: ['kb_search', 'kb_get', 'kb_recipe_tree', 'kb_plan'],
  },
  {
    file: 'wiki.md',
    title: 'Wiki（sidecar 本地，联网）',
    intro: '社区 Wiki 检索。内容属于外部数据，与游戏内事实分开引用。',
    capabilities: [],
    names: ['wiki_search', 'wiki_page'],
  },
  {
    file: 'blueprint.md',
    title: '蓝图（本地文件 + 社区站点）',
    intro:
      '蓝图只操作游戏蓝图根目录内的受限路径；`blueprint_save` 与 `blueprint_generate` ' +
      '写入的是 `.txt` 蓝图文本，不改变游戏状态。',
    capabilities: [],
    names: [
      'blueprint_fs',
      'blueprint_search',
      'blueprint_info',
      'blueprint_save',
      'blueprint_generate',
    ],
  },
  {
    file: 'interactive.md',
    title: '交互与脚本',
    intro:
      '`ask_user` 停下来问玩家；`run_script` 在 QuickJS-WASM 沙箱里批量调用上面的工具。',
    capabilities: [],
    names: ['ask_user', 'run_script'],
  },
];

interface RawTool {
  name: string;
  label: string;
  source: string;
  description: string;
  promptGuidelines: string[];
  parameters: unknown;
}

/**
 * 少数工具的返回字段不在 handler 块里（数据来自 GameGoals 等辅助类），
 * 这里按源码手工补，`source` 记录出处，避免以后误以为是自动提取的。
 */
const MANUAL_FIELDS: Record<string, CsInfo & { source: string }> = {
  game_todo_templates: {
    timeoutMs: 5000,
    topFields: ['templates', 'count'],
    allFields: ['id', 'name', 'determinator', 'level', 'parentId', 'params'],
    source: 'GameGoals.Templates()',
  },
  game_todo_list: {
    timeoutMs: 5000,
    topFields: ['goals', 'count'],
    allFields: ['groupId', 'goalId', 'group', 'title', 'templateId', 'current', 'target', 'stage'],
    source: 'GameGoals.List()',
  },
};

interface CsInfo {
  timeoutMs: number | null;
  topFields: string[];
  allFields: string[];
}

interface DocTool extends RawTool {
  capability: string | null;
  risk: 0 | 1 | 2;
  timeoutMs: number | null;
  group: string;
  cs: CsInfo;
  /** 人工维护的返回字段说明；null 表示本地工具（返回文本，无 data 字段）。 */
  notes: Record<string, FieldNote> | null;
}

function mockHost(): unknown {
  return {
    has: () => true,
    tools: () => [],
    call: async () => ({ ok: true, data: {}, meta: {} }),
    confirmLocalWrite: async () => true,
  };
}

function collectRawTools(): RawTool[] {
  const host = mockHost();
  const out: RawTool[] = [];
  const push = (source: string, tools: RawTool[]) => {
    for (const tool of tools) out.push({ ...tool, source });
  };

  const game = createGameTools(host as never) as unknown as RawTool[];
  push('game', game);
  push('ask', [createAskTool(host as never) as unknown as RawTool]);
  push('script', [createScriptTool(host as never) as unknown as RawTool]);
  push('kb', createKbTools({} as never) as unknown as RawTool[]);
  push('wiki', createWikiTools() as unknown as RawTool[]);
  push('blueprint', createBlueprintTools() as unknown as RawTool[]);
  return out;
}

function capabilityMap(): Map<string, string> {
  const map = new Map<string, string>();
  for (const capability of CAPABILITIES) {
    for (const name of toolNamesForCapabilities([capability])) {
      if ((name.startsWith('game_') || name === 'mods_list' || name.startsWith('ui_')) && !map.has(name)) map.set(name, capability);
    }
  }
  return map;
}

/**
 * 从 mod 侧 C# 实现里提取超时与返回字段。
 *
 * 只认 `data.Set("x", …)` 这种顶层字段；嵌套对象（planetObj 等）单独归到
 * allFields，文档里标成“嵌套字段线索”，不假装知道完整层级。
 */
function parseCsTools(): Map<string, CsInfo> {
  const dir = path.join(ROOT, 'mod/src/IcarusII/Game/Tools');
  const result = new Map<string, CsInfo>();
  if (!fs.existsSync(dir)) return result;

  for (const fileName of fs.readdirSync(dir).filter((name) => name.endsWith('.cs'))) {
    const text = fs.readFileSync(path.join(dir, fileName), 'utf8');
    // 只读工具写 `public string Name`，写工具写 `public override string Name`。
    const nameRe = /public (?:override |virtual |abstract )?string Name => "([a-z_]+)";/g;
    const matches = [...text.matchAll(nameRe)];
    for (let index = 0; index < matches.length; index += 1) {
      const match = matches[index];
      const name = match[1];
      // 块范围：从 handler 的 class 起点到**下一个 handler 的 Name**。
      // 不能用“下一个 class”：有的 handler 把构造逻辑放在内部 Job 类里
      // （PlanetJob / TraceLineTool 的 job），按 class 切会把返回字段切掉。
      const classStart = text.lastIndexOf('class ', match.index);
      const nextName = matches[index + 1]?.index ?? text.length;
      const block = text.slice(classStart < 0 ? 0 : classStart, nextName);

      const timeout = /public (?:override |virtual )?int TimeoutMs => (\d+);/.exec(block);
      const topFields = new Set<string>();
      const allFields = new Set<string>();
      let fieldMatch: RegExpExecArray | null;

      const topRe = /\bdata\.Set\("([A-Za-z0-9_]+)"/g;
      while ((fieldMatch = topRe.exec(block)) !== null) topFields.add(fieldMatch[1]);

      const anyRe = /\.Set\("([A-Za-z0-9_]+)"/g;
      while ((fieldMatch = anyRe.exec(block)) !== null) allFields.add(fieldMatch[1]);

      // 写工具继承 WriteToolBase，基类默认 8000ms，子类通常不重复声明。
      const timeoutMs = timeout ? Number(timeout[1]) : null;

      result.set(name, {
        timeoutMs,
        topFields: [...topFields],
        // meta.Set 等不是 data 的字段，从“嵌套线索”里去掉噪音。
        allFields: [...allFields].filter((field) => !topFields.has(field)),
      });
    }
  }
  return result;
}

function buildTools(): DocTool[] {
  const raw = collectRawTools();
  const caps = capabilityMap();
  const cs = parseCsTools();

  const groupOf = (name: string): string => {
    for (const group of GROUPS) {
      if (group.names?.includes(name)) return group.file;
      const capability = caps.get(name);
      if (group.capabilities?.length && capability && group.capabilities.includes(capability)) {
        return group.file;
      }
    }
    return 'index.md';
  };

  return raw.map((tool) => {
    const capability = caps.get(tool.name) ?? null;
    const write = isWriteToolName(tool.name);
    const manual = MANUAL_FIELDS[tool.name];
    const csInfo = manual
      ? { timeoutMs: manual.timeoutMs, topFields: manual.topFields, allFields: manual.allFields }
      : (cs.get(tool.name) ?? { timeoutMs: null, topFields: [], allFields: [] });
    return {
      ...tool,
      capability,
      risk: write ? (writeToolRisk(tool.name) as 1 | 2) : 0,
      // 写工具继承 WriteToolBase，基类 TimeoutMs => 8000，子类通常不再声明。
      timeoutMs: csInfo.timeoutMs ?? (write ? 8000 : null),
      group: groupOf(tool.name),
      cs: csInfo,
      notes: TOOL_FIELD_NOTES[tool.name] ?? null,
    };
  });
}

// ---------------------------------------------------------------- 渲染

function schemaType(schema: Record<string, unknown>): string {
  const anyOf = schema['anyOf'];
  if (Array.isArray(anyOf)) {
    return anyOf.map((item) => schemaType(item as Record<string, unknown>)).join(' \\| ');
  }
  const type = schema['type'];
  if (type === 'array') {
    const items = schema['items'] as Record<string, unknown> | undefined;
    return `${items ? schemaType(items) : 'any'}[]`;
  }
  return typeof type === 'string' ? type : 'any';
}

function schemaConstraints(schema: Record<string, unknown>): string {
  const parts: string[] = [];
  if (Array.isArray(schema['enum'])) parts.push(`enum: ${schema['enum'].join(' / ')}`);
  if (typeof schema['default'] !== 'undefined') parts.push(`默认 ${JSON.stringify(schema['default'])}`);
  for (const key of ['minimum', 'maximum', 'minLength', 'maxLength', 'minItems', 'maxItems']) {
    if (typeof schema[key] === 'number') parts.push(`${key}=${schema[key]}`);
  }
  if (schema['const'] !== undefined) parts.push(`固定 ${JSON.stringify(schema['const'])}`);
  return parts.join('，') || '—';
}

function renderParams(schema: unknown): string {
  const root = (schema ?? {}) as Record<string, unknown>;
  const properties = (root['properties'] ?? {}) as Record<string, Record<string, unknown>>;
  const required = new Set((root['required'] as string[] | undefined) ?? []);
  const names = Object.keys(properties);

  if (names.length === 0) return '无参数，调用时传 `{}`。';

  const lines = ['| 参数 | 类型 | 必填 | 约束 | 说明 |', '| --- | --- | --- | --- | --- |'];
  for (const name of names) {
    const prop = properties[name];
    const description = typeof prop['description'] === 'string' ? prop['description'] : '—';
    lines.push(
      `| \`${name}\` | \`${schemaType(prop)}\` | ${required.has(name) ? '是' : '否'} | ` +
        `${schemaConstraints(prop)} | ${description.replace(/\|/g, '\\|')} |`,
    );
  }
  return lines.join('\n');
}

function riskText(risk: 0 | 1 | 2): string {
  if (risk === 0) return '0（只读）';
  if (risk === 1) return '1（低风险写，可逆）';
  return '2（改存档写）';
}

/** sidecar 本地工具返回文本，不走 mod 的 data 信封。 */
const LOCAL_RETURN: Record<string, string> = {
  kb: '返回给模型的是一段**文本**（`content` 块），不是 `data` JSON 信封：条目已由 sidecar 渲染成列表或表格。',
  wiki: '返回页面正文的纯文本（不超过 8000 字），不是 JSON 信封；重定向会被自动跟随。',
  blueprint:
    '返回文本摘要。写入类工具还会广播蓝图卡片事件（协议 `blueprint_card` / `link_card`），' +
    '卡片走协议通道，不在工具返回值里。',
  ask: '返回玩家选择的答案；玩家跳过时带 `skipped=true`（不要重复追问同一题）。',
  script: '返回脚本最后 `return` 的文本（不超过 6000 字符），不走 `data` 信封。',
};

function renderTool(tool: DocTool): string {
  const lines: string[] = [];
  lines.push(`### \`${tool.name}\``);
  lines.push('');
  const meta: string[] = [
    `**label** ${tool.label}`,
    tool.capability ? `**能力位** \`${tool.capability}\`` : '**能力位** 无（sidecar 本地工具）',
    `**风险** ${riskText(tool.risk)}`,
    tool.timeoutMs ? `**超时** ${tool.timeoutMs} ms` : '**超时** —',
  ];
  lines.push(meta.join(' · '));
  lines.push('');
  lines.push('#### 模型可见 · 工具描述（tools 数组）');
  lines.push('');
  lines.push('```text');
  lines.push(tool.description.trim());
  lines.push('```');
  lines.push('');

  if (tool.promptGuidelines.length > 0) {
    lines.push('#### 模型可见 · 使用规则（system prompt）');
    lines.push('');
    for (const rule of tool.promptGuidelines) lines.push(`- ${rule.trim()}`);
    lines.push('');
  }

  lines.push('#### 参数');
  lines.push('');
  lines.push(renderParams(tool.parameters));
  lines.push('');

  lines.push('#### 返回');
  lines.push('');
  const notes = tool.notes;
  const noteEntries = notes ? Object.entries(notes) : [];
  const nonDisplay = noteEntries.filter(([name]) => name !== 'display');
  const noteKeys = new Set(noteEntries.map(([name]) => name));

  if (nonDisplay.length > 0) {
    lines.push('| 字段 | 类型 | 说明 |');
    lines.push('| --- | --- | --- |');
    for (const [name, note] of noteEntries) {
      lines.push(`| \`${name}\` | \`${note[0]}\` | ${note[1].replace(/\|/g, '\\|')} |`);
    }
  } else if (noteEntries.length === 1) {
    lines.push(`只返回 \`display\`：${noteEntries[0][1][1]}`);
  } else if (LOCAL_RETURN[tool.source]) {
    lines.push(LOCAL_RETURN[tool.source]);
  } else if (tool.risk > 0) {
    lines.push(
      '写工具的 `data` 只有 `display`（一句话确认，`WriteToolBase.Ok`）；' +
        '失败时 `ok=false` 并带 `error.code`。撤销语义见 [index.md](index.md#写操作的确认与撤销)。',
    );
  } else {
    lines.push('未从实现提取到顶层字段；以实际返回为准。');
  }

  // 说明里已经用反引号点名的字段（planet: `id` / `name` …）不再重复列。
  const described = new Set<string>();
  for (const [, note] of noteEntries) {
    for (const match of note[1].matchAll(/`([A-Za-z0-9_]+)`/g)) described.add(match[1]);
  }
  const nested = tool.cs.allFields.filter(
    (field) => !noteKeys.has(field) && !described.has(field),
  );
  if (nested.length > 0) {
    const shown = nested.slice(0, 12);
    const rest = nested.length - shown.length;
    lines.push('');
    lines.push(
      `嵌套对象内部字段（未逐个展开）：${shown.map((f) => `\`${f}\``).join('、')}` +
        (rest > 0 ? ` 等 ${nested.length} 个` : ''),
    );
  }
  lines.push('');
  if (tool.source === 'game') {
    lines.push(
      '> 信封（`ok` / `data` / `error` / `meta` / `image`）与截断规则见 ' +
        '[index.md](index.md#通用返回信封)。',
    );
    lines.push('');
  }
  return lines.join('\n');
}

/**
 * 校验：C# 里出现的每个顶层返回字段，都必须在 TOOL_FIELD_NOTES 里有类型与说明。
 * 反向不校验——notes 可以写只在部分分支出现的字段（veinId / buildingId 等）。
 */
function validateFieldNotes(tools: DocTool[]): string[] {
  const problems: string[] = [];
  for (const tool of tools) {
    if (tool.cs.topFields.length === 0) continue;
    const notes = TOOL_FIELD_NOTES[tool.name];
    if (!notes) {
      problems.push(`${tool.name}: 有顶层返回字段，但 tool-field-notes.mts 里没有该工具的说明`);
      continue;
    }
    for (const field of tool.cs.topFields) {
      if (!notes[field]) problems.push(`${tool.name}: 返回字段 ${field} 缺少类型/说明`);
    }
  }
  return problems;
}

function renderGroup(group: DocGroup, tools: DocTool[]): string {
  const lines: string[] = [];
  lines.push(`# ${group.title}`);
  lines.push('');
  lines.push(group.intro);
  lines.push('');
  lines.push(`共 ${tools.length} 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。`);
  lines.push('');
  lines.push('---');
  lines.push('');
  for (const tool of tools) {
    lines.push(renderTool(tool));
    lines.push('---');
    lines.push('');
  }
  return `${lines.join('\n').trimEnd()}\n`;
}

function firstSentence(text: string): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  const cut = flat.split(/(?<=。)/)[0] ?? flat;
  return cut.length > 90 ? `${cut.slice(0, 88)}…` : cut;
}

function renderIndex(tools: DocTool[]): string {
  const byCap = new Map<string, DocTool[]>();
  for (const tool of tools) {
    const key = tool.capability ?? 'sidecar 本地';
    if (!byCap.has(key)) byCap.set(key, []);
    byCap.get(key)!.push(tool);
  }

  const lines: string[] = [];
  lines.push('# 工具参考');
  lines.push('');
  lines.push(
    '本目录是工具的第二层文档：**提示词原文 + 参数 + 返回字段线索**。' +
      '模型看到的文本如何拼装，看 [prompt-shape.md](prompt-shape.md)；' +
      '协议字段与能力位协商，看 [protocol-v1.md](../protocol-v1.md) 附录 B。',
  );
  lines.push('');
  lines.push('> 本页与各分组页由 `sidecar/scripts/gen-tool-docs.mts` 生成，**不要手改**。');
  lines.push('> 改工具的 description / promptGuidelines / 参数后运行 `npx tsx sidecar/scripts/gen-tool-docs.mts`。');
  lines.push('');
  lines.push('## 工具清单');
  lines.push('');
  lines.push(`共 ${tools.length} 个模型可见工具。`);
  lines.push('');
  lines.push('| 工具 | label | 能力位 | 风险 | 超时 | 一句话 |');
  lines.push('| --- | --- | --- | --- | --- | --- |');
  const ordered = [...tools].sort((a, b) => {
    const ga = GROUPS.findIndex((g) => g.file === a.group);
    const gb = GROUPS.findIndex((g) => g.file === b.group);
    return ga - gb || a.name.localeCompare(b.name);
  });
  for (const tool of ordered) {
    const risk = tool.risk === 0 ? '只读' : `risk ${tool.risk}`;
    const timeout = tool.timeoutMs ? `${tool.timeoutMs}ms` : '—';
    lines.push(
      `| [\`${tool.name}\`](${tool.group}#${tool.name}) | ${tool.label} | ` +
        `${tool.capability ? `\`${tool.capability}\`` : '本地'} | ${risk} | ${timeout} | ` +
        `${firstSentence(tool.description).replace(/\|/g, '\\|')} |`,
    );
  }
  lines.push('');
  lines.push('## 分组');
  lines.push('');
  lines.push('| 文件 | 内容 | 数量 |');
  lines.push('| --- | --- | --- |');
  for (const group of GROUPS) {
    const count = tools.filter((tool) => tool.group === group.file).length;
    lines.push(`| [${group.file}](${group.file}) | ${group.title} | ${count} |`);
  }
  lines.push('');

  lines.push('### 参数的“必填”列');
  lines.push('');
  lines.push(
    '文档按 JSON Schema 的 `required` 渲染。当前工具的 TypeBox schema 大多没有设置 `required`，' +
      '所以会出现整列都是“否”，但工具实现仍要求二选一的情况——例如 ' +
      '`game_get_entity` 的 `entityId` / `entityIds`、`ask_user` 的 `question` / `questions`。' +
      '这类约束写在工具描述或参数说明里，以那里为准。',
  );
  lines.push('');

  lines.push('## 能力位');
  lines.push('');
  lines.push(
    'mod 在 `hello.capabilities` 里声明了哪一组，sidecar 才注册并广告哪一组。' +
      '老 mod 看不到新工具，是刻意的（避免 `tool_not_found`）。',
  );
  lines.push('');
  lines.push('| 能力位 | 工具数 | 代表工具 |');
  lines.push('| --- | --- | --- |');
  for (const [capability, list] of byCap) {
    const sample = list
      .slice(0, 3)
      .map((tool) => `\`${tool.name}\``)
      .join('、');
    lines.push(`| ${capability === 'sidecar 本地' ? '本地（始终注册）' : `\`${capability}\``} | ${list.length} | ${sample} |`);
  }
  lines.push('');
  lines.push('### 不在模型工具列表里的工具');
  lines.push('');
  lines.push('| 工具 | 状态 | 原因 |');
  lines.push('| --- | --- | --- |');
  lines.push(
    '| `game_export_prototypes` / `game_export_text` | mod 有实现，不注册 | ' +
      'KB 导出用的内部工具（协议附录 B.5），由 sidecar 导出流程直调，不广告给模型 |',
  );
  lines.push(
    '| `game_spawn_item` / `game_teleport` | **尚未实现** | ' +
      '沙盒专属；`tools/sandbox.ts` 只保留了名字与门禁。见 [prompt-shape.md](prompt-shape.md) 的沙盒段 |',
  );
  lines.push(
    '| `mods_config_list` / `mods_config_get` / `mods_config_set` | 内部线级工具 | ' +
      '`mod_<id>_config` 的实现细节，由 sidecar 带固定 `guid` 直调；不广告给模型 |',
  );
  lines.push(
    '| `mod_<id>_config` | 动态注册 | 每个已加载的第三方插件一个；插件不在 `hello.extensions` 里时不存在 |',
  );
  lines.push(
    '| `mod_<id>_<name>` | 动态注册 | manifest 声明的第三方工具；同样随插件加载/卸载出现和消失 |',
  );
  lines.push('');
  lines.push(
    '所以：mod 侧共 39 个 `Name` 声明，其中 37 个进入模型工具列表，2 个是内部导出工具；' +
      '第三方 mod 工具按 `hello.extensions` 动态生成，不在固定清单里。',
  );
  lines.push('');

  lines.push('### 返回字段表怎么来的');
  lines.push('');
  lines.push(
    '每个工具「返回」下的字段表：字段名由生成器从 mod 侧 C# 实现里提取，' +
      '类型与说明来自 `sidecar/scripts/tool-field-notes.mts`（**人工维护**）。' +
      'C# 里出现、说明表里没有的字段会让 `npm test` 的 `test:tool-docs` 失败，' +
      '所以不会悄悄漏字段。',
  );
  lines.push('');
  lines.push(
    '嵌套对象只展开一层：对象/数组字段的内部结构写在它自己的说明里（例如 `inputs` 的元素含 ' +
      '`itemId` / `name` / `count`）。更深的结构以 C# 实现为准。',
  );
  lines.push('');

  lines.push('## 通用返回信封');
  lines.push('');
  lines.push(
    '**只有 `game_*` 工具**走 mod 的 `data` 信封（协议附录 B.3）。' +
      '`kb_*` / `wiki_*` / `blueprint_*` / `ask_user` / `run_script` 是 sidecar 本地工具，' +
      '返回给模型的是文本内容块，不是 JSON 信封。',
  );
  lines.push('');
  lines.push('');
  lines.push('```json');
  lines.push('{');
  lines.push('  "ok": true,');
  lines.push('  "data": { "display": "给人看的一句话摘要", "...": "工具自己的字段" },');
  lines.push('  "error": null,');
  lines.push('  "meta": { "gameTick": 12345, "saveKey": "…", "truncated": false },');
  lines.push('  "image": null');
  lines.push('}');
  lines.push('```');
  lines.push('');
  lines.push('失败时：');
  lines.push('');
  lines.push('```json');
  lines.push('{ "ok": false, "data": null, "error": { "code": "not_found", "message": "…", "retryable": false } }');
  lines.push('```');
  lines.push('');
  lines.push(
    '模型看到的文本不是原始 JSON 而是渲染结果：`display` → JSON → 尾注。' +
      '超过 9000 字符截断，追加「请用 offset/limit 缩小范围」。详见 [prompt-shape.md](prompt-shape.md#23-工具结果tool_result)。',
  );
  lines.push('');
  lines.push('### 常见错误码');
  lines.push('');
  lines.push('code 取自 `mod/src/Shared/Protocol/Protocol.cs` 的 `Code*` 常量：');
  lines.push('');
  lines.push('| code | 来源 | 含义 |');
  lines.push('| --- | --- | --- |');
  lines.push('| `tool_not_found` | sidecar | 调用了未注册的工具 |');
  lines.push('| `tool_unavailable` | sidecar | 没有绑定到游戏连接（断线/未进存档） |');
  lines.push('| `not_in_game` | mod | 未进入存档，或游戏已退出 |');
  lines.push('| `invalid_args` | mod / sidecar | 参数不满足 schema |');
  lines.push('| `busy` | mod | 同时执行的工具过多，请串行调用 |');
  lines.push('| `timeout` | mod | 超过工具超时或分帧预算 |');
  lines.push('| `cancelled` | mod | 调用被取消（断线/换档） |');
  lines.push('| `sandbox_required` | sidecar | 沙盒专属工具在未启用沙盒的存档里被调用 |');
  lines.push('| `confirmation_required` | mod | 写操作需要确认，但调用方没走确认流程 |');
  lines.push('| `confirmation_denied` | mod | 玩家在确认页拒绝 |');
  lines.push('| `confirmation_expired` | mod | 确认超时（一次性令牌失效） |');
  lines.push('| `confirmation_invalid` | mod | 令牌/参数指纹不匹配 |');
  lines.push('| `debug_disabled` / `debug_not_allowed` | sidecar | 调试直调未开启 / 工具不在只读白名单 |');
  lines.push('| `tool_required` | sidecar | 强制工具回合没有调用指定工具 |');
  lines.push('');
  lines.push(
    '注意：`stale=true`、`reason="not_found"` 是**返回字段**（`ok` 仍为 true），不是错误码。' +
      '工具自身的业务失败也可能把 `internal` 作为 code 透出，以 `error.message` 为准。',
  );
  lines.push('');
  lines.push(
    '`confirmation_required` / `confirmation_expired` / `confirmation_invalid` 属于确认流程' +
      '（`confirm_request` → `confirm_result`）的 code；工具调用被拒时，模型看到的是' +
      '`confirmation_denied`。',
  );
  lines.push('');
  lines.push('## 写操作的确认与撤销');
  lines.push('');
  lines.push('| 风险 | 权限档位「只读」 | 「受限」（默认） | 「接管」 |');
  lines.push('| --- | --- | --- | --- |');
  lines.push('| 0 只读 | 直接执行 | 直接执行 | 直接执行 |');
  lines.push('| 1 低风险写 | 先确认 | 直接执行 | 直接执行 |');
  lines.push('| 2 改存档写 | 先确认 | 先确认 | 直接执行 |');
  lines.push('');
  lines.push(
    '一次性令牌始终校验，权限档位只决定是否弹确认页。' +
      '「接管」也要求模型先确认目标明确（见 [prompt-shape.md](prompt-shape.md#6-权限段)）。',
  );
  lines.push('');
  lines.push(
    '撤销只保留**一层**，且只对真正压栈的写操作有效：' +
      '`game_place_marker`、`game_open_panel`、`game_set_research`、`game_set_recipe`、' +
      '`game_set_assembler_batch`、`game_storage_*`、`game_feed_input`。',
  );
  lines.push('');
  lines.push(
    '以下写操作**不可撤销**，但会占用栈顶（`WriteActionStack.MarkNonUndoable`），' +
      '让 `game_undo_last_write` 明确告知“最近一步不可撤销”，而不是跳过它去撤销更早的操作：' +
      '`game_drop_item`、`game_take_output`、`game_tether_add`、`game_tether_clear`、' +
      '`game_todo_add`、`game_todo_remove`。',
  );
  lines.push('');
  return `${lines.join('\n').trimEnd()}\n`;
}

// ---------------------------------------------------------------- 主流程

function buildAll(): { tools: DocTool[]; files: Map<string, string> } {
  const tools = buildTools();
  const files = new Map<string, string>();
  for (const group of GROUPS) {
    const subset = tools.filter((tool) => tool.group === group.file);
    if (subset.length === 0) continue;
    files.set(group.file, renderGroup(group, subset));
  }
  files.set('index.md', renderIndex(tools));
  return { tools, files };
}

function snapshot(tools: DocTool[]): string {
  const payload = {
    generatedBy: 'sidecar/scripts/gen-tool-docs.mts',
    tools: [...tools].sort((a, b) => a.name.localeCompare(b.name)),
  };
  return `${JSON.stringify(payload, null, 2)}\n`;
}

function main(): void {
  const check = process.argv.includes('--check');
  const { tools, files } = buildAll();
  const nextSnapshot = snapshot(tools);

  if (check) {
    const problems: string[] = [...validateFieldNotes(tools)];
    const current = fs.existsSync(SNAPSHOT) ? fs.readFileSync(SNAPSHOT, 'utf8') : '';
    if (current !== nextSnapshot) problems.push('docs/tools/tools.json 与当前工具定义不一致');
    for (const [file, content] of files) {
      const target = path.join(DOCS_DIR, file);
      if (!fs.existsSync(target) || fs.readFileSync(target, 'utf8') !== content) {
        problems.push(`docs/tools/${file} 不是最新`);
      }
    }
    if (problems.length > 0) {
      console.error('工具文档过期：');
      for (const problem of problems) console.error(`  - ${problem}`);
      console.error('\n运行：npx tsx sidecar/scripts/gen-tool-docs.mts');
      process.exit(1);
    }
    console.log(`工具文档已是最新（${tools.length} 个工具）。`);
    return;
  }

  fs.mkdirSync(DOCS_DIR, { recursive: true });
  fs.writeFileSync(SNAPSHOT, nextSnapshot, 'utf8');
  for (const [file, content] of files) {
    fs.writeFileSync(path.join(DOCS_DIR, file), content, 'utf8');
  }
  console.log(`已生成 ${files.size} 个文档文件，覆盖 ${tools.length} 个工具。`);
}

main();
