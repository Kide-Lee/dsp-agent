/**
 * M2 只读游戏工具定义（协议附录 B）。
 *
 * 这些工具只声明 schema 与调用方式；真正的采集在 mod 侧。
 * 工具名使用下划线：OpenAI 兼容端点会把非 [a-zA-Z0-9_-] 字符替换成 `_`，
 * 用点号会导致模型看到的工具名与回传的 tool_call.name 不一致。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import type { JsonObject, ToolResultPayload } from '../protocol.js';
import { GameToolHost, registerGameToolsFactory } from './host.js';
import { KB_TOOL_NAMES } from '../kb/tools.js';

export const CAPABILITY_GAME_READ = 'game.read.v1';

export const GAME_TOOL_NAMES = [
  'game_get_overview',
  'game_get_player',
  'game_get_planet',
  'game_get_power',
  'game_get_research',
  'game_get_production',
] as const;

const MAX_RESULT_CHARS = 9000;

type ToolArgs = JsonObject;

function isObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function formatToolResult(result: ToolResultPayload): string {
  if (!result.ok) {
    const code = result.error?.code ?? 'tool_failed';
    const message = result.error?.message ?? '未知错误';
    return `[${code}] ${message}`;
  }

  const data = result.data;
  const display = isObject(data) && typeof data['display'] === 'string' ? data['display'] : '';
  let json: string;
  try {
    json = JSON.stringify(data);
  } catch {
    json = String(data);
  }

  const meta = result.meta ?? {};
  const notes: string[] = [];
  if (typeof meta['gameTick'] === 'number') notes.push(`gameTick=${meta['gameTick']}`);
  if (typeof meta['saveKey'] === 'string') notes.push(`save=${meta['saveKey']}`);
  if (meta['truncated'] === true) notes.push('结果已截断，可用 offset 继续');

  let text = `${display ? `${display}\n` : ''}${json}`;
  if (notes.length > 0) text += `\n(${notes.join(', ')})`;
  if (text.length > MAX_RESULT_CHARS) {
    text = `${text.slice(0, MAX_RESULT_CHARS)}\n…（结果过长，已截断；请用 offset/limit 缩小范围）`;
  }
  return text;
}

function gameTool(
  host: GameToolHost,
  spec: {
    name: string;
    label: string;
    description: string;
    promptSnippet: string;
    promptGuidelines?: string[];
    parameters: ReturnType<typeof Type.Object>;
    timeoutMs: number;
  },
): ToolDefinition {
  return {
    name: spec.name,
    label: spec.label,
    description: spec.description,
    promptSnippet: spec.promptSnippet,
    promptGuidelines: spec.promptGuidelines,
    parameters: spec.parameters,
    executionMode: 'sequential',
    async execute(_toolCallId, params, signal) {
      const result = await host.call(spec.name, (params ?? {}) as ToolArgs, {
        timeoutMs: spec.timeoutMs,
        signal,
      });
      return {
        content: [{ type: 'text', text: formatToolResult(result) }],
        details: result,
      };
    },
  };
}

export function createGameTools(host: GameToolHost): ToolDefinition[] {
  if (!host.has(CAPABILITY_GAME_READ)) return [];

  const planetArg = Type.Optional(
    Type.Integer({ description: '行星 id；省略时使用当前所在行星' }),
  );
  const offsetArg = Type.Optional(Type.Integer({ description: '分页偏移，默认 0' }));
  const limitArg = Type.Optional(Type.Integer({ description: '返回条数上限，默认 25' }));

  return [
    gameTool(host, {
      name: 'game_get_overview',
      label: '存档概览',
      description:
        '读取当前存档概览：存档名、游戏时刻、当前行星/星系、玩家坐标、是否暂停。' +
        '玩家问“我在哪”“现在什么情况”时先调用它；不要凭印象回答。',
      promptSnippet: '读取当前存档、星球、时间与玩家位置的概览',
      promptGuidelines: [
        'Use game_get_overview 当玩家询问当前存档、所在星球、游戏时间或“现在什么情况”时。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'game_get_player',
      label: '玩家状态',
      description:
        '读取伊卡洛斯的能量、护盾、反应堆能量与背包物品（id、名称、数量）。' +
        '玩家问“我还有多少能量”“背包里有什么”时调用。',
      promptSnippet: '读取玩家机甲能量、护盾与背包物品',
      promptGuidelines: [
        'Use game_get_player 当玩家询问自己的能量、护盾或背包物品时。',
      ],
      parameters: Type.Object({
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 6000,
    }),
    gameTool(host, {
      name: 'game_get_planet',
      label: '行星状态',
      description:
        '读取指定行星（省略则为当前行星）的类型、半径、矿脉按资源汇总、建筑按原型计数。' +
        '远程行星未加载工厂时只返回静态矿脉，并明确标注 factoryLoaded=false。',
      promptSnippet: '读取行星矿脉汇总与建筑计数',
      promptGuidelines: [
        'Use game_get_planet 当玩家询问某个星球有什么矿、建了多少建筑时。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 12000,
    }),
    gameTool(host, {
      name: 'game_get_power',
      label: '电力状态',
      description:
        '读取指定行星（省略则为当前行星）的电网：需求、已供、容量、储能、满足率与充放电。' +
        '所有功率单位为 kW，储能为 kWh；玩家问“电够不够”时调用。',
      promptSnippet: '读取电网需求、供给、容量、储能与满足率',
      promptGuidelines: [
        'Use game_get_power 当玩家询问电力是否足够、为什么停电或电网负载时。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
      }),
      timeoutMs: 6000,
    }),
    gameTool(host, {
      name: 'game_get_research',
      label: '研究状态',
      description:
        '读取当前研究科技、进度、队列、研究速度与已解锁科技数量。' +
        '玩家问“现在研究什么”“还差多少”时调用。',
      promptSnippet: '读取当前研究、队列、进度与已解锁数量',
      promptGuidelines: [
        'Use game_get_research 当玩家询问研究进度、科技队列或已解锁科技时。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'game_get_production',
      label: '产线状态',
      description:
        '读取指定行星（省略则为当前行星）按物品聚合的产量/消耗/库存（每分钟口径），' +
        '按净产出从低到高排序，并给出库存还能撑几分钟。玩家问“我缺什么”时调用它，' +
        '再结合 game_get_power 判断瓶颈。',
      promptSnippet: '读取产线净产出、消耗、库存与缺料分钟数',
      promptGuidelines: [
        'Use game_get_production 当玩家问“我缺什么”、某条产线为什么停、库存还能撑多久时。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        itemFilter: Type.Optional(
          Type.String({ description: '按物品名或 id 过滤，例如“铁块”或“1001”' }),
        ),
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 12000,
    }),
  ];
}

registerGameToolsFactory(createGameTools);

/** `ready.payload.tools` 用：按能力返回本次连接实际可用的工具名。 */
export function toolNamesForCapabilities(capabilities: readonly string[]): string[] {
  // kb_* 是 sidecar 本地工具，不依赖 mod 能力；始终列出，未建立时返回 kb_unavailable。
  const game = capabilities.includes(CAPABILITY_GAME_READ) ? [...GAME_TOOL_NAMES] : [];
  return [...game, ...KB_TOOL_NAMES];
}
