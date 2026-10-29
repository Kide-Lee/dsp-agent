/**
 * M3 知识库工具（sidecar 本地执行，不经过 mod）。
 *
 * 工具名用下划线，和 M2 的 game_* 一致：
 *   kb_search / kb_get / kb_recipe_tree / kb_plan
 *
 * 知识库未建立时仍然注册，execute 返回 kb_unavailable：
 * 这样模型不会“看不到工具就编造”，玩家也能得到明确原因。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import type { JsonObject } from '../protocol.js';
import type { KnowledgeStore } from './store.js';
import { KB_KINDS } from './knowledge.js';
import type { KbKind } from './types.js';

export const KB_TOOL_NAMES = ['kb_search', 'kb_get', 'kb_recipe_tree', 'kb_plan'] as const;

const MAX_RESULT_CHARS = 9000;

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function clip(text: string): string {
  if (text.length <= MAX_RESULT_CHARS) return text;
  return `${text.slice(0, MAX_RESULT_CHARS)}\n…（结果过长，已截断；请用更精确的 item/kind 或更小的 depth）`;
}

function json(value: unknown): string {
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function unavailable(): { content: { type: 'text'; text: string }[]; details: unknown } {
  const message =
    '知识库尚未建立：需要先进入存档，等 sidecar 从游戏导出原型数据（约 1 秒）。' +
    '现在不要凭记忆回答配方/科技数值；可以先用 game_* 读当前状态，或明确告诉玩家“知识库还在建立”。';
  return {
    content: [{ type: 'text', text: `[kb_unavailable] ${message}` }],
    details: { ok: false, error: { code: 'kb_unavailable', message } },
  };
}

function kindsFrom(value: unknown): KbKind[] | undefined {
  const raw = typeof value === 'string' ? value.split(',') : Array.isArray(value) ? value : [];
  const out: KbKind[] = [];
  for (const item of raw) {
    if (typeof item !== 'string') continue;
    const kind = item.trim() as KbKind;
    if (KB_KINDS.includes(kind) && !out.includes(kind)) out.push(kind);
  }
  return out.length > 0 ? out : undefined;
}

export function createKbTools(store: KnowledgeStore): ToolDefinition[] {
  const search: ToolDefinition = {
    name: 'kb_search',
    label: '知识库搜索',
    description:
      '在游戏本体知识库里搜索物品、配方、科技、建筑、矿脉，以及提示/教程/里程碑/指引/术语。' +
      '玩家问“X 是什么”“哪个科技解锁 X”“有没有相关提示”时先用它定位；再按 id 用 kb_get 或 kb_recipe_tree。',
    promptGuidelines: [
      '玩家提到具体物品、配方、科技、建筑或游戏机制名称，而你需要确认官方数据时使用。',
      '先用 kb_search 拿到 kind + id，再用 kb_get 或 kb_recipe_tree；不要凭记忆编造配方/数值。',
      '默认 spoiler=low 不返回剧情教程；只有玩家明确问剧情/教程时才传 spoiler="story"，不要主动剧透。',
    ],
    parameters: Type.Object({
      query: Type.String({ description: '名称、ID 或 SID，例如“量子芯片”“1201”“coil”' }),
      kinds: Type.Optional(
        Type.Array(Type.String(), {
          description:
            '可选类别过滤：item/recipe/tech/model/vein/signal/tip/tutorial/milestone/goal/prompt/glossary/achievement/journal',
        }),
      ),
      limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 30, description: '返回条数，默认 8' })),
      spoiler: Type.Optional(
        Type.String({
          description: '剧透上限：none（只要无剧透）/low（默认）/story（含剧情/教程）。默认 low',
        }),
      ),
    }),
    async execute(_toolCallId, params) {
      const kb = store.snapshot;
      if (!kb) return unavailable();
      const args = (params ?? {}) as JsonObject;
      const query = typeof args['query'] === 'string' ? args['query'] : '';
      if (!query.trim()) {
        return {
          content: [{ type: 'text', text: '[bad_request] query 不能为空' }],
          details: { ok: false, error: { code: 'bad_request', message: 'query 不能为空' } },
        };
      }
      const kinds = kindsFrom(args['kinds']);
      const limit = typeof args['limit'] === 'number' ? args['limit'] : 8;
      const spoilerArg = typeof args['spoiler'] === 'string' ? args['spoiler'] : 'low';
      const maxSpoiler =
        spoilerArg === 'none' || spoilerArg === 'story' ? spoilerArg : 'low';
      const hits = kb.search(query, kinds, limit, maxSpoiler);
      const lines = hits.map((hit) => {
        const tag = hit.iconTag ? ` iconTag=${hit.iconTag}` : '';
        const summary = hit.summary ? ` — ${hit.summary}` : '';
        return `- [${hit.kind}#${String(hit.id)}] ${hit.name}${tag}${summary}`;
      });
      const text = `查询：${query}（${hits.length} 条）\n${lines.join('\n') || '（没有匹配）'}`;
      return {
        content: [{ type: 'text', text: clip(text) }],
        details: { ok: true, data: { query, hits } },
      };
    },
  };

  const get: ToolDefinition = {
    name: 'kb_get',
    label: '知识库条目',
    description:
      '按类别 + id/名称/SID 精确读取一条知识库条目（物品、配方、科技、建筑、矿脉、提示、教程、里程碑、指引、术语等）。' +
      '通常先用 kb_search 拿到 id，再用它读完整字段。',
    promptGuidelines: [
      'kb_search 已经给出 kind + id，或玩家明确给出条目 id 时使用。',
      'kb_get 返回的是游戏本体数据；不要用它之外的信息补全配方/科技数值。',
    ],
    parameters: Type.Object({
      kind: Type.String({ description: 'item/recipe/tech/model/vein/signal/tip/tutorial/...' }),
      idOrName: Type.String({ description: '数字 id、名称或 SID' }),
    }),
    async execute(_toolCallId, params) {
      const kb = store.snapshot;
      if (!kb) return unavailable();
      const args = (params ?? {}) as JsonObject;
      const kind = typeof args['kind'] === 'string' ? (args['kind'] as KbKind) : 'item';
      const idOrName = typeof args['idOrName'] === 'string' ? args['idOrName'] : '';
      if (!KB_KINDS.includes(kind)) {
        return {
          content: [{ type: 'text', text: `[bad_request] 未知类别：${kind}` }],
          details: { ok: false, error: { code: 'bad_request', message: `未知类别：${kind}` } },
        };
      }
      if (!idOrName.trim()) {
        return {
          content: [{ type: 'text', text: '[bad_request] idOrName 不能为空' }],
          details: { ok: false, error: { code: 'bad_request', message: 'idOrName 不能为空' } },
        };
      }
      const found = kb.get(kind, idOrName);
      if (!found) {
        return {
          content: [
            {
              type: 'text',
              text: `[not_found] 知识库里没有 ${kind} / ${idOrName}；先用 kb_search 找准确名称或 id。`,
            },
          ],
          details: { ok: false, error: { code: 'not_found', message: `${kind}/${idOrName} 不存在` } },
        };
      }
      const value = isObject(found.value) ? found.value : { value: found.value };
      return {
        content: [{ type: 'text', text: clip(json(value)) }],
        details: { ok: true, data: found },
      };
    },
  };

  const recipeTree: ToolDefinition = {
    name: 'kb_recipe_tree',
    label: '配方树',
    description:
      '按游戏本体的主配方递归展开一个物品，计算直接输入、原料总量与副产物。' +
      '玩家问“造 N 个 X 需要多少原料”“每分钟 N 个 X 怎么配”时用它，不要自己心算。',
    promptGuidelines: [
      '玩家问某个物品需要哪些原料、需要多少、每分钟产量怎么换算时使用。',
      'rate 表示目标物品每分钟产量；不传 rate 时 amount 表示一次目标产量（默认 1）。',
      '结果里的 raw 是原料汇总，byproducts 是副产物；副产物未从原料中抵扣，回答时要说明。',
    ],
    parameters: Type.Object({
      item: Type.String({ description: '物品名称、id 或 SID' }),
      amount: Type.Optional(Type.Number({ exclusiveMinimum: 0, description: '目标数量，默认 1' })),
      rate: Type.Optional(Type.Number({ exclusiveMinimum: 0, description: '目标每分钟产量；给出时忽略 amount' })),
      depth: Type.Optional(Type.Integer({ minimum: 1, maximum: 16, description: '递归深度，默认 8' })),
      recipeId: Type.Optional(Type.Integer({ description: '可选：指定根物品的配方 id（替代配方）' })),
      from: Type.Optional(
        Type.String({ description: '起点：ore（默认，从原矿）/ smelted（从冶炼产物）/ 物品名或 id' }),
      ),
    }),
    async execute(_toolCallId, params) {
      const kb = store.snapshot;
      if (!kb) return unavailable();
      const args = (params ?? {}) as JsonObject;
      const item = typeof args['item'] === 'string' ? args['item'] : '';
      if (!item.trim()) {
        return {
          content: [{ type: 'text', text: '[bad_request] item 不能为空' }],
          details: { ok: false, error: { code: 'bad_request', message: 'item 不能为空' } },
        };
      }
      const fromRaw = typeof args['from'] === 'string' ? args['from'].trim() : '';
      let from: 'ore' | 'smelted' | number | undefined;
      if (fromRaw === 'ore' || fromRaw === 'smelted') {
        from = fromRaw;
      } else if (fromRaw) {
        const fromItem = kb.resolveItem(fromRaw);
        if (!fromItem) {
          return {
            content: [{ type: 'text', text: `[bad_request] 找不到起点物品：${fromRaw}` }],
            details: { ok: false, error: { code: 'bad_request', message: `找不到起点物品：${fromRaw}` } },
          };
        }
        from = fromItem.id;
      }
      try {
        const result = kb.recipeTree(item, {
          amount: typeof args['amount'] === 'number' ? args['amount'] : undefined,
          rate: typeof args['rate'] === 'number' ? args['rate'] : undefined,
          depth: typeof args['depth'] === 'number' ? args['depth'] : undefined,
          recipeId: typeof args['recipeId'] === 'number' ? args['recipeId'] : undefined,
          ...(from !== undefined ? { from } : {}),
        });
        const unit = result.perMinute ? '/min' : '';
        const direct = result.tree.inputs
          .map((input) => `${input.item} ${input.amount.toFixed(2)}${unit}`)
          .join('、');
        const raw = result.raw
          .map((row) => `${row.item} ${row.amount.toFixed(2)}${unit}`)
          .join('、');
        const summary = [
          `${result.item.name} ${result.amount}${unit}${result.perMinute ? '' : '（单次）'}`,
          result.tree.recipe ? `配方：${result.tree.recipe}` : '配方：无（按原料处理）',
          direct ? `直接输入：${direct}` : '',
          raw ? `原料：${raw}` : '',
          result.byproducts.length > 0
            ? `副产物：${result.byproducts.map((row) => `${row.item} ${row.amount.toFixed(2)}${unit}`).join('、')}`
            : '',
          ...result.notes,
        ]
          .filter((line) => line.length > 0)
          .join('\n');
        return {
          content: [{ type: 'text', text: clip(`${summary}\n\n${json(result)}`) }],
          details: { ok: true, data: result },
        };
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        return {
          content: [{ type: 'text', text: `[bad_request] ${message}` }],
          details: { ok: false, error: { code: 'bad_request', message } },
        };
      }
    },
  };

  const plan: ToolDefinition = {
    name: 'kb_plan',
    label: '产线规划',
    description:
      '给定目标物品的每分钟产量，算出要建多少台什么机器、总功率、每条输入要几条传送带，' +
      '并复用 kb_recipe_tree 给出原料与副产物。' +
      '玩家问“我要建多少台机器”“这条线要多少电”“需要几条带”时用它。',
    promptGuidelines: [
      '玩家问某条产线要多少机器、多少电、几条传送带时使用。',
      '玩家问“怎么把 X 做到 N/min / 怎么规划 / 要准备什么”时，第一步就调 kb_plan，不要先查现状再心算。',
      'rate 是目标物品的每分钟产量，必填；机器数已向上取整（exactCount 是未取整值）。',
      '结果里的 assumptions 必须转述给玩家：未建模矿脉产率、建筑堆叠与喷涂消耗。',
      '玩家提到增产剂/喷涂时传 proliferator=mk1/mk2/mk3；默认 extra（额外产出），要加速则 proliferatorMode=speed。',
    ],
    parameters: Type.Object({
      item: Type.String({ description: '目标物品名称、id 或 SID' }),
      rate: Type.Number({ exclusiveMinimum: 0, description: '目标每分钟产量' }),
      depth: Type.Optional(Type.Integer({ minimum: 1, maximum: 16, description: '递归深度，默认 8' })),
      recipeId: Type.Optional(Type.Integer({ description: '可选：指定根配方 id' })),
      buildingTier: Type.Optional(
        Type.String({ description: '机器档位：base（默认，最低速）/ mk2 / mk3' }),
      ),
      buildingId: Type.Optional(Type.Integer({ description: '可选：直接指定机器原型 id' })),
      proliferator: Type.Optional(
        Type.Union([Type.Literal('none'), Type.Literal('mk1'), Type.Literal('mk2'), Type.Literal('mk3')], {
          description: '增产剂档位，默认 none',
        }),
      ),
      proliferatorMode: Type.Optional(
        Type.Union([Type.Literal('extra'), Type.Literal('speed')], {
          description: 'extra=额外产出（默认），speed=加速生产',
        }),
      ),
      beltSpeed: Type.Optional(
        Type.Number({ exclusiveMinimum: 0, description: '单条带吞吐（个/秒），默认 6（Mk.I）' }),
      ),
      from: Type.Optional(
        Type.String({ description: '起点：ore（默认，从原矿）/ smelted（从冶炼产物）/ 物品名或 id' }),
      ),
    }),
    async execute(_toolCallId, params) {
      const kb = store.snapshot;
      if (!kb) return unavailable();
      const args = (params ?? {}) as JsonObject;
      const item = typeof args['item'] === 'string' ? args['item'] : '';
      if (!item.trim()) {
        return {
          content: [{ type: 'text', text: '[bad_request] item 不能为空' }],
          details: { ok: false, error: { code: 'bad_request', message: 'item 不能为空' } },
        };
      }
      const rate = args['rate'];
      if (typeof rate !== 'number' || !(rate > 0)) {
        return {
          content: [{ type: 'text', text: '[bad_request] rate（每分钟产量）必须大于 0' }],
          details: { ok: false, error: { code: 'bad_request', message: 'rate 必须大于 0' } },
        };
      }

      const tierRaw = args['buildingTier'];
      const buildingTier =
        tierRaw === 'mk2' || tierRaw === 'mk3' || tierRaw === 'base' ? tierRaw : undefined;

      const fromRaw = typeof args['from'] === 'string' ? args['from'].trim() : '';
      let from: 'ore' | 'smelted' | number | undefined;
      if (fromRaw === 'ore' || fromRaw === 'smelted') {
        from = fromRaw;
      } else if (fromRaw) {
        const fromItem = kb.resolveItem(fromRaw);
        if (!fromItem) {
          return {
            content: [{ type: 'text', text: `[bad_request] 找不到起点物品：${fromRaw}` }],
            details: { ok: false, error: { code: 'bad_request', message: `找不到起点物品：${fromRaw}` } },
          };
        }
        from = fromItem.id;
      }
      try {
        const result = kb.plan(item, {
          rate,
          depth: typeof args['depth'] === 'number' ? args['depth'] : undefined,
          recipeId: typeof args['recipeId'] === 'number' ? args['recipeId'] : undefined,
          ...(buildingTier ? { buildingTier } : {}),
          buildingId: typeof args['buildingId'] === 'number' ? args['buildingId'] : undefined,
          beltSpeed: typeof args['beltSpeed'] === 'number' ? args['beltSpeed'] : undefined,
          ...(from !== undefined ? { from } : {}),
        });

        const machines = result.recipes
          .map((row) =>
            row.buildingName
              ? `${row.buildingName} ×${row.buildingCount}（${row.recipeName}）`
              : `${row.recipeName} ×${row.buildingCount} 台`,
          )
          .join('、');
        const summary = [
          `${result.item.name} ${result.ratePerMin}/min`,
          machines ? `机器：${machines}` : '机器：无需机器（直接是原料）',
          `总功率：${result.totalPowerKw.toFixed(1)} kW`,
          `最大单路输入：${result.belts.maxSingleInputPerMin.toFixed(1)}/min` +
            `（${result.belts.itemsPerSec}/s 的带 → ${result.belts.maxBelts} 条）`,
          ...result.assumptions.map((line) => `· ${line}`),
          ...result.warnings.map((line) => `[警告] ${line}`),
        ]
          .filter((line) => line.length > 0)
          .join('\n');

        return {
          content: [{ type: 'text', text: clip(`${summary}\n\n${json(result)}`) }],
          details: { ok: true, data: result },
        };
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        return {
          content: [{ type: 'text', text: `[bad_request] ${message}` }],
          details: { ok: false, error: { code: 'bad_request', message } },
        };
      }
    },
  };

  return [search, get, recipeTree, plan];
}
