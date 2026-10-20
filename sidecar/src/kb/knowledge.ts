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
  type PlanOptions,
  type PlanRecipeRow,
  type PlanResult,
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
  /** 从哪一层开始：ore（默认，从原矿）/ smelted（从冶炼产物）/ 物品 id（从该中间产物）。 */
  from?: 'ore' | 'smelted' | number;
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

  /**
   * `from` → 停止展开的物品集合。被停止的物品按原料处理（计入 plan.raw），
   * 不再生成上游机器。根物品永远不停止。
   */
  private stopAtFor(from: PlanOptions['from'] | RecipeTreeOptions['from'], rootId: number): Set<number> {
    const stop = new Set<number>();
    if (from === undefined || from === 'ore') return stop;
    if (typeof from === 'number') {
      if (from !== rootId) stop.add(from);
      return stop;
    }
    // 冶炼产物：主配方 type === 1（电弧熔炉/位面熔炉/负熵熔炉）。
    for (const item of this.items.values()) {
      if (item.id === rootId) continue;
      const binding = this.mainRecipeFor(item.id);
      if (binding && binding.recipe.type === 1) stop.add(item.id);
    }
    return stop;
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
    const stopAt = this.stopAtFor(options.from, item.id);

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

      if (path.length > 0 && stopAt.has(itemId)) {
        add(raw, itemId, qty);
        node.recipe = '（从中间产物开始，未展开）';
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

  // ---------------------------------------------------------------- 产线规划

  /**
   * 找出能承接该配方类型的机器原型，按装配速度升序（base → mk2 → mk3）。
   *
   * 依据是 `PrefabDesc.assemblerRecipeType`：它才是游戏判断"这台机器能做什么"的字段。
   * 离线 KB 没有 prefabDesc，所以这里可能返回空数组——调用方必须降级而不是瞎猜。
   */
  private buildingsForRecipe(recipe: KbRecipe): KbItem[] {
    const type = recipe.type;
    if (typeof type !== 'number') return [];
    const out: KbItem[] = [];
    for (const item of this.items.values()) {
      const building = item.building;
      if (!building || building.assemblerRecipeType === undefined) continue;
      if (building.assemblerRecipeType !== type) continue;
      if (!building.assemblerSpeed || building.assemblerSpeed <= 0) continue;
      out.push(item);
    }
    out.sort((a, b) => (a.building?.assemblerSpeed ?? 0) - (b.building?.assemblerSpeed ?? 0));
    return out;
  }

  /**
   * 产线规划：给定目标物品的每分钟产量，算机器数 / 电力 / 传送带 / 原料。
   *
   * 与 `kb_recipe_tree` 的分工：
   *  - 配方树负责"原料总量与副产物抵扣"（已实现且测试充分）；
   *  - 这里负责"要建多少台什么机器、耗多少电、要几条带"。
   *
   * 刻意不建模：增产剂、矿脉产率、堆叠与建筑叠加——这些需要运行时参数，
   * 先留成 `assumptions` 里的显式声明，避免给出看似精确的错数。
   */
  plan(ref: string | number, options: PlanOptions): PlanResult {
    const item = this.resolveItem(ref);
    if (!item) throw new Error(`找不到物品：${String(ref)}`);

    const rate = Number(options.rate);
    if (!(rate > 0)) throw new Error('rate（目标物品的每分钟产量）必须大于 0');

    const depth = Math.max(1, Math.min(16, Math.floor(options.depth ?? 8)));
    const tier = options.buildingTier ?? 'base';
    const beltItemsPerSec =
      typeof options.beltSpeed === 'number' && options.beltSpeed > 0 ? options.beltSpeed : 6;

    const proliferatorTier = options.proliferator ?? 'none';
    const proliferatorMode = options.proliferatorMode ?? 'extra';
    // 额外产出：mk1 +12.5% / mk2 +20% / mk3 +25%；加速：+25% / +50% / +100%。
    const proExtra: Record<string, number> = { none: 0, mk1: 0.125, mk2: 0.2, mk3: 0.25 };
    const proSpeed: Record<string, number> = { none: 0, mk1: 0.25, mk2: 0.5, mk3: 1.0 };
    const outputMultiplier =
      proliferatorMode === 'extra' ? 1 + (proExtra[proliferatorTier] ?? 0) : 1;
    const speedMultiplier =
      proliferatorMode === 'speed' ? 1 + (proSpeed[proliferatorTier] ?? 0) : 1;

    const rootRecipe =
      options.recipeId !== undefined ? this.recipes.get(options.recipeId) : undefined;
    if (options.recipeId !== undefined && !rootRecipe) {
      throw new Error(`找不到配方：${options.recipeId}`);
    }

    // 原料/副产物直接用配方树的结果，保证两个工具永远说同一套话。
    const tree = this.recipeTree(item.id, {
      rate,
      depth,
      ...(options.from !== undefined ? { from: options.from } : {}),
      ...(options.recipeId !== undefined ? { recipeId: options.recipeId } : {}),
    });
    const stopAt = this.stopAtFor(options.from, item.id);

    // 额外产出会减少制作次数，原料/副产物按同一比例缩放（配方树本身不知道增产剂）。
    const craftScale = 1 / outputMultiplier;
    const rawScaled = tree.raw.map((row) => ({ ...row, amount: round4(row.amount * craftScale) }));
    const byproductScaled = tree.byproducts.map((row) => ({
      ...row,
      amount: round4(row.amount * craftScale),
    }));

    const forcedBinding = rootRecipe
      ? (() => {
          const index = rootRecipe.outputs.findIndex((output) => output.id === item.id);
          if (index < 0) return null;
          const output = rootRecipe.outputs[index];
          return output
            ? { recipe: rootRecipe, outputIndex: index, outputCount: output.count }
            : null;
        })()
      : null;

    const aggregates = new Map<number, { recipe: KbRecipe; craftsPerMin: number; primaryItemId: number }>();
    const warnings: string[] = [];
    let depthCut = false;

    const expand = (
      itemId: number,
      itemRate: number,
      remaining: number,
      path: number[],
      isRoot: boolean,
    ): void => {
      if (!(itemRate > 0)) return;
      if (path.includes(itemId)) return;
      if (!isRoot && stopAt.has(itemId)) return;

      const binding = isRoot && forcedBinding ? forcedBinding : this.mainRecipeFor(itemId);
      if (!binding) return;

      // 额外产出模式下，同样的制作次数会多出产物；反推制作次数时要除掉倍率。
      const craftsPerMin = itemRate / Math.max(1, binding.outputCount) / outputMultiplier;
      const previous = aggregates.get(binding.recipe.id);
      if (previous) previous.craftsPerMin += craftsPerMin;
      else {
        aggregates.set(binding.recipe.id, {
          recipe: binding.recipe,
          craftsPerMin,
          primaryItemId: itemId,
        });
      }

      if (remaining <= 0) {
        depthCut = true;
        return;
      }
      for (const input of binding.recipe.inputs) {
        expand(input.id, craftsPerMin * input.count, remaining - 1, [...path, itemId], false);
      }
    };

    expand(item.id, rate, depth, [], true);

    const chooseBuilding = (recipe: KbRecipe): { chosen?: KbItem; speed: number } => {
      if (options.buildingId !== undefined) {
        const forced = this.items.get(options.buildingId);
        const speed = forced?.building?.assemblerSpeed;
        if (!forced || !speed || speed <= 0) {
          warnings.push(`指定的建筑 ${options.buildingId} 没有装配速度，按 1x 计算`);
          return { speed: 1 };
        }
        return { chosen: forced, speed: speed / 10000 };
      }

      const candidates = this.buildingsForRecipe(recipe);
      const index = tier === 'mk3' ? 2 : tier === 'mk2' ? 1 : 0;
      const chosen = candidates[Math.min(index, candidates.length - 1)];
      const speed = chosen?.building?.assemblerSpeed;
      if (!chosen || !speed || speed <= 0) return { speed: 1 };
      return { chosen, speed: speed / 10000 };
    };

    const rows: PlanRecipeRow[] = [];
    let totalPowerKw = 0;
    let maxSingleInputPerMin = 0;
    let maxBelts = 0;
    const seenWarnings = new Set<string>();
    const warn = (message: string): void => {
      if (seenWarnings.has(message)) return;
      seenWarnings.add(message);
      warnings.push(message);
    };

    for (const aggregate of aggregates.values()) {
      const { recipe, craftsPerMin } = aggregate;
      if (!(craftsPerMin > 0)) continue;

      const { chosen, speed } = chooseBuilding(recipe);
      if (!chosen && options.buildingId === undefined) {
        warn(`配方「${recipe.name}」找不到对应机器（离线知识库缺少建筑参数），按 1x 计算`);
      }

      // 机器数 = 每分钟制作次数 × 每件耗时 ÷ 60 ÷ 速度倍率。
      const exactCount = (craftsPerMin * recipe.timeSeconds) / 60 / (speed * speedMultiplier);
      const buildingCount = Math.max(1, Math.ceil(exactCount - 1e-9));

      const perBuildingKw = chosen?.building?.workEnergyPerTick
        ? (chosen.building.workEnergyPerTick * 60) / 1000
        : 0;
      const powerKw = round4(perBuildingKw * buildingCount);
      totalPowerKw += powerKw;

      const inputsPerMin = recipe.inputs.map((input) => ({
        itemId: input.id,
        item: this.items.get(input.id)?.name ?? `#${input.id}`,
        amount: round4(input.count * craftsPerMin),
      }));

      let beltsPerInput = 0;
      for (const input of inputsPerMin) {
        if (input.amount > maxSingleInputPerMin) maxSingleInputPerMin = input.amount;
        beltsPerInput = Math.max(
          beltsPerInput,
          Math.ceil(input.amount / (beltItemsPerSec * 60) - 1e-9),
        );
      }
      if (beltsPerInput > maxBelts) maxBelts = beltsPerInput;

      const outputsPerMin = recipe.outputs.map((output) => ({
        itemId: output.id,
        item: this.items.get(output.id)?.name ?? `#${output.id}`,
        amount: round4(output.count * craftsPerMin),
      }));
      rows.push({
        recipeId: recipe.id,
        recipeName: recipe.name,
        primaryItemId: aggregate.primaryItemId,
        primaryItem: this.items.get(aggregate.primaryItemId)?.name,
        outputsPerMin,
        ...(typeof recipe.type === 'number' ? { recipeType: recipe.type } : {}),
        ...(chosen ? { buildingId: chosen.id, buildingName: chosen.name } : {}),
        buildingCount,
        exactCount: round4(exactCount),
        speedMultiplier: round4(speed),
        craftsPerMin: round4(craftsPerMin),
        timeSeconds: recipe.timeSeconds,
        powerKw,
        inputsPerMin,
        beltsPerInput,
      });
    }

    rows.sort((a, b) => b.craftsPerMin - a.craftsPerMin);

    if (depthCut) warn('达到深度上限，未继续展开的项按原料处理（可用 depth 放宽）');

    const assumptions = [
      '机器数向上取整；exactCount 是未取整值，用来看余量。',
      `单条传送带按 ${beltItemsPerSec} 个/秒折算；实际带数取决于布线。`,
      '电力只算工作功率（workEnergyPerTick × 60 ÷ 1000），不含待机、电网损耗与建筑自耗。',
      '未建模矿脉产率与建筑堆叠；原料是"到厂需求"，不是"矿脉开采需求"。',
      proliferatorTier === 'none'
        ? '增产剂未启用；需要时传 proliferator=mk1/mk2/mk3。'
        : `增产剂 ${proliferatorTier}（${proliferatorMode === 'extra' ? '额外产出' : '加速生产'}）已计入机器数与原料；喷涂消耗与电力增幅未建模。`,
    ];

    return {
      item: { id: item.id, name: item.name },
      ratePerMin: round4(rate),
      recipes: rows,
      totalPowerKw: round4(totalPowerKw),
      belts: {
        itemsPerSec: beltItemsPerSec,
        maxSingleInputPerMin: round4(maxSingleInputPerMin),
        maxBelts,
      },
      raw: rawScaled,
      byproducts: byproductScaled,
      cycles: tree.cycles,
      depthLimited: tree.depthLimited || depthCut,
      proliferator: {
        tier: proliferatorTier,
        mode: proliferatorMode,
        outputMultiplier: round4(outputMultiplier),
        speedMultiplier: round4(speedMultiplier),
      },
      assumptions,
      warnings,
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
