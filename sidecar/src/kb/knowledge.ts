/**
 * 知识库内存实现（M3a + M3b）。
 *
 * - 搜索：名称/ID/SID/描述的子串匹配，中文直接按字符子串；数据量 ~700 条，不需要 FTS。
 * - 精确查询：id / 名称 / SID。
 * - 配方树：按游戏 ItemProto.maincraft 的等价规则选主配方，递归展开并汇总原料。
 *
 * 这里不读文件、不碰网络；加载/导出在 store.ts / manager.ts。
 */

import {
  KB_SCHEMA_VERSION,
  type KbCount,
  type KbData,
  type KbGetResult,
  type KbItem,
  type KbKind,
  type KbRecipe,
  type KbModel,
  type KbSearchHit,
  type KbSignal,
  type KbTech,
  type KbVein,
  type KbText,
  type KbTextKind,
  type RecipeTreeNode,
  type RecipeTreeResult,
} from './types.js';

const PROTO_KINDS: readonly KbKind[] = ['item', 'recipe', 'tech', 'model', 'vein', 'signal'];
const TEXT_KINDS: readonly KbKind[] = [
  'tip',
  'tutorial',
  'milestone',
  'goal',
  'prompt',
  'glossary',
  'achievement',
  'journal',
];
export const KB_KINDS: readonly KbKind[] = [...PROTO_KINDS, ...TEXT_KINDS];

export class KbSchemaError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'KbSchemaError';
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function asArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? (value as T[]) : [];
}

/** 宽松校验：只保证顶层结构可用；单条坏数据跳过而不是整库失败。 */
export function parseKbData(value: unknown): KbData {
  if (!isObject(value)) throw new KbSchemaError('知识库必须是 JSON 对象');
  if (value['schemaVersion'] !== KB_SCHEMA_VERSION) {
    throw new KbSchemaError(
      `知识库 schemaVersion=${String(value['schemaVersion'])}，期望 ${KB_SCHEMA_VERSION}`,
    );
  }
  const gameVersion = typeof value['gameVersion'] === 'string' ? value['gameVersion'] : '';
  const locale = typeof value['locale'] === 'string' ? value['locale'] : '';
  if (!gameVersion) throw new KbSchemaError('知识库缺少 gameVersion');
  if (!locale) throw new KbSchemaError('知识库缺少 locale');

  const clean = <T>(rows: unknown, valid: (row: unknown) => row is T): T[] =>
    asArray<unknown>(rows).filter(valid);

  return {
    schemaVersion: KB_SCHEMA_VERSION,
    gameVersion,
    locale,
    builtAt: typeof value['builtAt'] === 'string' ? value['builtAt'] : undefined,
    source:
      value['source'] === 'runtime-export' || value['source'] === 'offline-extract'
        ? value['source']
        : undefined,
    items: clean(value['items'], (row): row is KbItem => isObject(row) && typeof row['name'] === 'string' && Number.isInteger(row['id'])),
    recipes: clean(
      value['recipes'],
      (row): row is KbRecipe =>
        isObject(row) && typeof row['name'] === 'string' && Number.isInteger(row['id']) && Array.isArray(row['inputs']) && Array.isArray(row['outputs']),
    ),
    techs: clean(value['techs'], (row): row is KbTech => isObject(row) && typeof row['name'] === 'string' && Number.isInteger(row['id'])),
    models: clean(value['models'], (row): row is { id: number; name: string } => isObject(row) && typeof row['name'] === 'string' && Number.isInteger(row['id'])) as KbData['models'],
    veins: clean(value['veins'], (row): row is { id: number; name: string } => isObject(row) && typeof row['name'] === 'string' && Number.isInteger(row['id'])) as KbData['veins'],
    signals: clean(value['signals'], (row): row is { id: number; name: string } => isObject(row) && typeof row['name'] === 'string' && Number.isInteger(row['id'])) as KbData['signals'],
    texts: clean(
      value['texts'],
      (row): row is KbText =>
        isObject(row) &&
        typeof row['kind'] === 'string' &&
        TEXT_KINDS.includes(row['kind'] as KbKind) &&
        typeof row['body'] === 'string' &&
        (typeof row['id'] === 'number' || typeof row['id'] === 'string'),
    ),
  };
}

function normalize(value: string): string {
  return value
    .normalize('NFKC')
    .toLowerCase()
    .replace(/[\s\p{P}\p{S}]+/gu, '');
}

function textSnippet(value: string, max = 80): string {
  const flat = value.replace(/\s+/g, ' ').trim();
  return flat.length > max ? `${flat.slice(0, max)}…` : flat;
}

function sortByAmount<T extends { amount: number }>(rows: T[]): T[] {
  return rows.sort((a, b) => b.amount - a.amount);
}

function round4(value: number): number {
  return Math.round(value * 10000) / 10000;
}

interface NameEntry {
  kind: KbKind;
  id: number | string;
  name: string;
  norm: string;
  sidNorm?: string;
  idText: string;
  sid?: string;
  summary?: string;
  bodyNorm?: string;
  iconTag?: string;
  spoiler?: 'none' | 'low' | 'story';
}

interface RecipeBinding {
  recipe: KbRecipe;
  outputIndex: number;
  outputCount: number;
}

export interface RecipeTreeOptions {
  /** 目标物品数量；默认 1。 */
  amount?: number;
  /** 每分钟目标产量；给出时 amount 被忽略，所有结果都是每分钟口径。 */
  rate?: number;
  /** 递归深度，默认 8，上限 16。 */
  depth?: number;
  /** 只对根物品指定配方（用于替代配方）；默认用主配方。 */
  recipeId?: number;
}

export class KnowledgeBase {
  readonly data: KbData;
  readonly gameVersion: string;
  readonly locale: string;

  private readonly items = new Map<number, KbItem>();
  private readonly recipes = new Map<number, KbRecipe>();
  private readonly techs = new Map<number, KbTech>();
  private readonly models = new Map<number, KbModel>();
  private readonly veins = new Map<number, KbVein>();
  private readonly signals = new Map<number, KbSignal>();
  private readonly texts: KbText[] = [];
  private readonly textByKey = new Map<string, KbText>();
  private readonly entries: NameEntry[] = [];
  private readonly recipesByOutput = new Map<number, RecipeBinding[]>();
  private readonly mainRecipeCache = new Map<number, RecipeBinding | null>();

  constructor(data: KbData) {
    this.data = data;
    this.gameVersion = data.gameVersion;
    this.locale = data.locale;

    for (const item of data.items) this.items.set(item.id, item);
    for (const recipe of data.recipes) this.recipes.set(recipe.id, recipe);
    for (const tech of data.techs) this.techs.set(tech.id, tech);
    for (const model of data.models ?? []) this.models.set(model.id, model);
    for (const vein of data.veins ?? []) this.veins.set(vein.id, vein);
    for (const signal of data.signals ?? []) this.signals.set(signal.id, signal);
    for (const text of data.texts ?? []) {
      this.texts.push(text);
      this.textByKey.set(`${text.kind}:${String(text.id)}`, text);
    }

    this.indexNames();
    this.indexRecipes();
  }

  get stats(): { gameVersion: string; locale: string; items: number; recipes: number; techs: number; texts: number } {
    return {
      gameVersion: this.gameVersion,
      locale: this.locale,
      items: this.items.size,
      recipes: this.recipes.size,
      techs: this.techs.size,
      texts: this.texts.length,
    };
  }

  // ---------------------------------------------------------------- 索引

  private addEntry(entry: NameEntry): void {
    if (!entry.name) return;
    this.entries.push({
      ...entry,
      norm: normalize(entry.name),
      sidNorm: entry.sid ? normalize(entry.sid) : undefined,
      spoiler: entry.spoiler ?? 'none',
    });
  }

  private indexNames(): void {
    for (const item of this.items.values()) {
      this.addEntry({
        kind: 'item',
        id: item.id,
        name: item.name,
        norm: '',
        idText: String(item.id),
        sid: item.sid,
        summary: item.description ?? item.produceFrom ?? item.miningFrom,
        iconTag: item.iconTag,
      });
    }
    for (const recipe of this.recipes.values()) {
      this.addEntry({
        kind: 'recipe',
        id: recipe.id,
        name: recipe.name,
        norm: '',
        idText: String(recipe.id),
        sid: recipe.sid,
        summary: recipe.description,
        iconTag: recipe.iconTag,
      });
    }
    for (const tech of this.techs.values()) {
      this.addEntry({
        kind: 'tech',
        id: tech.id,
        name: tech.name,
        norm: '',
        idText: String(tech.id),
        sid: tech.sid,
        summary: tech.description,
        iconTag: tech.iconTag,
      });
    }
    for (const model of this.models.values()) {
      this.addEntry({ kind: 'model', id: model.id, name: model.name, norm: '', idText: String(model.id), sid: model.sid });
    }
    for (const vein of this.veins.values()) {
      this.addEntry({ kind: 'vein', id: vein.id, name: vein.name, norm: '', idText: String(vein.id), sid: vein.sid, summary: vein.description });
    }
    for (const signal of this.signals.values()) {
      this.addEntry({ kind: 'signal', id: signal.id, name: signal.name, norm: '', idText: String(signal.id), sid: signal.sid });
    }
    for (const text of this.texts) {
      const title = text.title || `${text.kind} ${String(text.id)}`;
      this.addEntry({
        kind: text.kind,
        id: text.id,
        name: title,
        norm: '',
        idText: String(text.id),
        summary: textSnippet(text.body),
        bodyNorm: normalize(text.body),
        spoiler: text.spoiler ?? 'low',
      });
    }
  }

  private indexRecipes(): void {
    for (const recipe of this.recipes.values()) {
      for (let index = 0; index < recipe.outputs.length; index += 1) {
        const output = recipe.outputs[index];
        if (!output) continue;
        const list = this.recipesByOutput.get(output.id) ?? [];
        list.push({ recipe, outputIndex: index, outputCount: output.count });
        this.recipesByOutput.set(output.id, list);
      }
    }
  }

  /** 与 ItemProto.FindRecipes 一致的“主配方”：结果下标最小，其次 id 最小。 */
  private mainRecipeFor(itemId: number): RecipeBinding | null {
    if (this.mainRecipeCache.has(itemId)) return this.mainRecipeCache.get(itemId) ?? null;
    const item = this.items.get(itemId);
    if (item?.mainRecipeId !== undefined) {
      const direct = this.recipes.get(item.mainRecipeId);
      if (direct) {
        const index = direct.outputs.findIndex((output) => output.id === itemId);
        if (index >= 0) {
          const binding = { recipe: direct, outputIndex: index, outputCount: direct.outputs[index]?.count ?? 1 };
          this.mainRecipeCache.set(itemId, binding);
          return binding;
        }
      }
    }
    const candidates = (this.recipesByOutput.get(itemId) ?? []).slice().sort((a, b) => {
      if (a.outputIndex !== b.outputIndex) return a.outputIndex - b.outputIndex;
      return a.recipe.id - b.recipe.id;
    });
    const best = candidates[0] ?? null;
    this.mainRecipeCache.set(itemId, best);
    return best;
  }

  // ---------------------------------------------------------------- 搜索 / 查询

  search(
    query: string,
    kinds?: readonly KbKind[],
    limit = 10,
    maxSpoiler: 'none' | 'low' | 'story' = 'low',
  ): KbSearchHit[] {
    const raw = query.trim();
    if (!raw) return [];
    const q = normalize(raw);
    const tokens = raw
      .split(/\s+/)
      .map((token) => normalize(token))
      .filter((token) => token.length > 0);
    const allowed = kinds && kinds.length > 0 ? new Set(kinds) : null;
    const spoilerRank = { none: 0, low: 1, story: 2 } as const;
    const maxRank = spoilerRank[maxSpoiler];
    const best = new Map<string, KbSearchHit>();

    for (const entry of this.entries) {
      if (allowed && !allowed.has(entry.kind)) continue;
      if (spoilerRank[entry.spoiler ?? 'none'] > maxRank) continue;
      let score = 0;
      if (entry.idText === q) score += 80;
      if (entry.norm === q) score += 100;
      else if (entry.norm.startsWith(q)) score += 50;
      else if (entry.norm.includes(q)) score += 30;
      if (tokens.length > 1 && tokens.every((token) => entry.norm.includes(token))) score += 20;
      if (entry.sidNorm && entry.sidNorm.includes(q)) score += 40;
      if (entry.summary && normalize(entry.summary).includes(q)) score += 10;
      if (entry.bodyNorm && entry.bodyNorm.includes(q)) score += 10;
      if (score <= 0) continue;

      const key = `${entry.kind}:${String(entry.id)}`;
      const previous = best.get(key);
      if (previous && previous.score >= score) continue;
      best.set(key, {
        kind: entry.kind,
        id: entry.id,
        name: entry.name,
        summary: entry.summary,
        iconTag: entry.iconTag,
        score,
      });
    }

    return [...best.values()]
      .sort((a, b) => {
        if (b.score !== a.score) return b.score - a.score;
        return a.name.length - b.name.length || a.name.localeCompare(b.name);
      })
      .slice(0, Math.max(1, Math.min(limit, 50)));
  }

  get(kind: KbKind, idOrName: string | number): KbGetResult | null {
    if (kind === 'item' || kind === 'recipe' || kind === 'tech' || kind === 'model' || kind === 'vein' || kind === 'signal') {
      const numeric = typeof idOrName === 'number' ? idOrName : Number.parseInt(String(idOrName), 10);
      if (Number.isInteger(numeric)) {
        const value = this.getProtoById(kind, numeric);
        if (value) return { kind, value };
      }
      const needle = normalize(String(idOrName));
      const candidates: { id: number; value: unknown }[] = [];
      const source: Iterable<unknown> =
        kind === 'item'
          ? this.items.values()
          : kind === 'recipe'
            ? this.recipes.values()
            : kind === 'tech'
              ? this.techs.values()
              : kind === 'model'
                ? this.models.values()
                : kind === 'vein'
                  ? this.veins.values()
                  : this.signals.values();
      for (const value of source) {
        const row = value as { id: number; name?: string; sid?: string };
        if (normalize(row.name ?? '') === needle || normalize(row.sid ?? '') === needle) {
          candidates.push({ id: row.id, value });
        }
      }
      if (candidates.length === 1) return { kind, value: candidates[0]?.value };
      return null;
    }

    const key = `${kind}:${String(idOrName)}`;
    const direct = this.textByKey.get(key);
    if (direct) return { kind, value: direct };
    const needle = normalize(String(idOrName));
    const matches = this.texts.filter((text) => text.kind === kind && normalize(text.title ?? '') === needle);
    if (matches.length === 1) return { kind, value: matches[0] };
    return null;
  }

  private getProtoById(kind: KbKind, id: number): unknown | null {
    switch (kind) {
      case 'item':
        return this.items.get(id) ?? null;
      case 'recipe':
        return this.recipes.get(id) ?? null;
      case 'tech':
        return this.techs.get(id) ?? null;
      case 'model':
        return this.models.get(id) ?? null;
      case 'vein':
        return this.veins.get(id) ?? null;
      case 'signal':
        return this.signals.get(id) ?? null;
      default:
        return null;
    }
  }

  resolveItem(ref: string | number): KbItem | null {
    if (typeof ref === 'number' || /^\d+$/.test(ref)) {
      const item = this.items.get(Number(ref));
      if (item) return item;
    }
    const needle = normalize(String(ref));
    for (const item of this.items.values()) {
      if (normalize(item.name) === needle || normalize(item.sid ?? '') === needle) return item;
    }
    return null;
  }

  // ---------------------------------------------------------------- 配方树

  recipeTree(ref: string | number, options: RecipeTreeOptions = {}): RecipeTreeResult {
    const item = this.resolveItem(ref);
    if (!item) throw new Error(`找不到物品：${String(ref)}`);
    const perMinute = typeof options.rate === 'number' && options.rate > 0;
    const amount = perMinute ? (options.rate as number) : Math.max(0, options.amount ?? 1);
    if (!(amount > 0)) throw new Error('amount/rate 必须大于 0');
    const depth = Math.max(1, Math.min(16, Math.floor(options.depth ?? 8)));
    const rootRecipe = options.recipeId !== undefined ? this.recipes.get(options.recipeId) : undefined;
    if (options.recipeId !== undefined && !rootRecipe) throw new Error(`找不到配方：${options.recipeId}`);

    const raw = new Map<number, number>();
    const byproducts = new Map<number, number>();
    const cycles = new Set<number>();
    let depthLimited = false;

    const nameOf = (id: number): string => this.items.get(id)?.name ?? `#${id}`;
    const add = (map: Map<number, number>, id: number, value: number): void => {
      if (!(value > 0)) return;
      map.set(id, (map.get(id) ?? 0) + value);
    };

    const expand = (itemId: number, qty: number, remaining: number, path: number[], forcedRecipe?: KbRecipe): RecipeTreeNode => {
      const node: RecipeTreeNode = {
        itemId,
        item: nameOf(itemId),
        amount: qty,
        inputs: [],
        byproducts: [],
      };
      if (qty <= 0) return node;

      if (path.includes(itemId)) {
        cycles.add(itemId);
        add(raw, itemId, qty);
        node.recipe = '（循环，已按原料处理）';
        return node;
      }

      const binding = forcedRecipe
        ? (() => {
            const index = forcedRecipe.outputs.findIndex((output) => output.id === itemId);
            return index >= 0
              ? { recipe: forcedRecipe, outputIndex: index, outputCount: forcedRecipe.outputs[index]?.count ?? 1 }
              : null;
          })()
        : this.mainRecipeFor(itemId);

      if (!binding) {
        add(raw, itemId, qty);
        return node;
      }

      if (remaining <= 0) {
        depthLimited = true;
        add(raw, itemId, qty);
        node.recipeId = binding.recipe.id;
        node.recipe = binding.recipe.name;
        return node;
      }

      const crafts = qty / Math.max(1, binding.outputCount);
      node.recipeId = binding.recipe.id;
      node.recipe = binding.recipe.name;
      node.crafts = round4(crafts);
      node.timeSeconds = binding.recipe.timeSeconds;

      for (const input of binding.recipe.inputs) {
        const childQty = input.count * crafts;
        node.inputs.push({ itemId: input.id, item: nameOf(input.id), amount: round4(childQty) });
        expand(input.id, childQty, remaining - 1, [...path, itemId]);
      }

      for (let index = 0; index < binding.recipe.outputs.length; index += 1) {
        if (index === binding.outputIndex) continue;
        const output = binding.recipe.outputs[index];
        if (!output) continue;
        const extra = output.count * crafts;
        node.byproducts.push({ itemId: output.id, item: nameOf(output.id), amount: round4(extra) });
        add(byproducts, output.id, extra);
      }

      return node;
    };

    // 递归过程中直接写 raw/byproducts。
    const tree = expand(item.id, amount, depth, [], rootRecipe);

    // 副产物按同名原料抵扣：避免“需要 86 精炼油，同时副产 566 精炼油”这种误导。
    // 抵扣后仍为负的部分保留为副产物余额。
    for (const itemId of [...raw.keys()]) {
      const by = byproducts.get(itemId) ?? 0;
      if (by <= 0) continue;
      const net = (raw.get(itemId) ?? 0) - by;
      if (net > 0) {
        raw.set(itemId, net);
        byproducts.delete(itemId);
      } else {
        raw.delete(itemId);
        if (net < 0) byproducts.set(itemId, -net);
        else byproducts.delete(itemId);
      }
    }

    const notes: string[] = [];
    if (perMinute) notes.push('数量为每分钟口径（rate）。');
    else notes.push('数量为单次目标产量口径；如需每分钟请传 rate。');
    if (byproducts.size > 0) notes.push('副产物已按同名原料抵扣；剩余副产物列在 byproducts。');
    else notes.push('副产物已按同名原料抵扣；没有剩余副产物。');
    if (cycles.size > 0) notes.push('检测到配方循环，循环项按原料处理。');
    if (depthLimited) notes.push('达到深度上限，未继续展开的项按原料处理。');

    const rawRows = [...raw.entries()].map(([itemId, value]) => ({
      itemId,
      item: nameOf(itemId),
      amount: round4(value),
    }));
    const byRows = [...byproducts.entries()].map(([itemId, value]) => ({
      itemId,
      item: nameOf(itemId),
      amount: round4(value),
    }));

    return {
      item: { id: item.id, name: item.name },
      amount,
      perMinute,
      tree,
      raw: sortByAmount(rawRows),
      byproducts: sortByAmount(byRows),
      cycles: [...cycles].map((itemId) => ({ itemId, item: nameOf(itemId) })),
      depthLimited,
      notes,
    };
  }

  /** 给工具展示用：所有类别的数量。 */
  get kindCounts(): Record<string, number> {
    const counts: Record<string, number> = {
      item: this.items.size,
      recipe: this.recipes.size,
      tech: this.techs.size,
      model: this.models.size,
      vein: this.veins.size,
      signal: this.signals.size,
    };
    for (const text of this.texts) counts[text.kind] = (counts[text.kind] ?? 0) + 1;
    return counts;
  }
}

// 让 KbCount 在类型层可见（被 recipe 数据结构使用）。
export type { KbCount, KbTextKind };
