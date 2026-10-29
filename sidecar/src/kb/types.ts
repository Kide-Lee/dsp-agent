/**
 * 知识库 schema v1（M3）。
 *
 * 数据来源有两类，形状必须一致：
 *  - 开发期：tools/build-kb.py 从游戏安装的 Prototypes/Locale/Wiki 离线导出；
 *  - 运行期：mod 的 game_export_prototypes 分块导出（见 mod/src/IcarusII/Game/Tools）。
 *
 * 设计约束：
 *  - 只保留 AI 有用的字段；贴图/GUID/prefab 路径等不进库。
 *  - 所有 id 都是游戏内的 int id；sid 是稳定字符串（可能为空）。
 *  - 名称/描述是**已经本地化**的文本；locale 记录生成时的语言。
 *  - 文本类条目（M3b）统一放在 texts[]，用 kind 区分。
 */

export const KB_SCHEMA_VERSION = 1;

/** 结构化原型类别。 */
export type KbProtoKind = 'item' | 'recipe' | 'tech' | 'model' | 'vein' | 'signal';

/** 文本/指引类别（M3b）。 */
export type KbTextKind =
  | 'tip'
  | 'tutorial'
  | 'milestone'
  | 'goal'
  | 'prompt'
  | 'glossary'
  | 'achievement'
  | 'journal';

export type KbKind = KbProtoKind | KbTextKind;

export interface KbCount {
  id: number;
  count: number;
}

/** 建筑参数。只有运行期导出才有：prefabDesc 不在 LDB 的 asset 里。 */
export interface KbBuilding {
  assemblerSpeed?: number;
  /**
   * 该建筑能承接的配方类型（`PrefabDesc.assemblerRecipeType`）。
   * `kb_plan` 用它把配方映射到机器；离线导出没有这个字段。
   */
  assemblerRecipeType?: number;
  workEnergyPerTick?: number;
  minerPeriod?: number;
  beltSpeed?: number;
  storageCol?: number;
  storageRow?: number;
  stationMaxEnergyAcc?: number;
  isCollectStation?: boolean;
}

export interface KbItem {
  id: number;
  sid?: string;
  name: string;
  description?: string;
  type?: number;
  subId?: number;
  stackSize?: number;
  grade?: number;
  isFluid?: boolean;
  isEntity?: boolean;
  canBuild?: boolean;
  buildInGas?: boolean;
  gridIndex?: number;
  iconTag?: string;
  miningFrom?: string;
  produceFrom?: string;
  modelIndex?: number;
  modelCount?: number;
  unlockKey?: number;
  preTechOverride?: number;
  productive?: boolean;
  /** 主要合成配方（游戏 ItemProto.maincraft 的等价物）。 */
  mainRecipeId?: number;
  /** 主要手搓配方。 */
  handRecipeId?: number;
  building?: KbBuilding;
}

export interface KbRecipe {
  id: number;
  sid?: string;
  name: string;
  description?: string;
  type?: number;
  handcraft?: boolean;
  explicit?: boolean;
  /** 游戏 tick 数；DSP 固定 60 tick/s。 */
  timeSpend: number;
  timeSeconds: number;
  inputs: KbCount[];
  outputs: KbCount[];
  nonProductive?: boolean;
  iconTag?: string;
}

export interface KbTech {
  id: number;
  sid?: string;
  name: string;
  description?: string;
  conclusion?: string;
  level?: number;
  maxLevel?: number;
  preTechs?: number[];
  preTechsImplicit?: number[];
  items?: number[];
  itemPoints?: number[];
  hashNeeded?: number;
  unlockRecipes?: number[];
  unlockFunctions?: number[];
  unlockValues?: number[];
  published?: boolean;
  isHiddenTech?: boolean;
  isObsolete?: boolean;
  iconTag?: string;
}

export interface KbModel {
  id: number;
  sid?: string;
  name: string;
  objectType?: number;
  prefabPath?: string;
}

export interface KbVein {
  id: number;
  sid?: string;
  name: string;
  description?: string;
  miningItem?: number;
  miningTime?: number;
  iconTag?: string;
}

export interface KbSignal {
  id: number;
  sid?: string;
  name: string;
  iconTag?: string;
}

/** M3b 文本条目。body 已经过清洗；模板/按键占位符用标记保留。 */
export interface KbText {
  kind: KbTextKind;
  id: number | string;
  title?: string;
  body: string;
  /** 可选：所属阶段/层级，用于剧透过滤。 */
  phase?: number;
  /** low | none | story；默认 low。 */
  spoiler?: 'none' | 'low' | 'story';
  /** 来源（base.txt / prototype.txt / Wiki/Tutorial/...），便于排障。 */
  source?: string;
  /** 文本里仍有 {0} 之类的模板参数。 */
  templated?: boolean;
  /** 文本里仍有未解析的 $LKEY_n; 按键占位符。 */
  unresolvedKeys?: boolean;
  /** 可选的附加字段（Goal 的层级、Milestone 的条件等）。 */
  extra?: Record<string, unknown>;
}

export interface KbData {
  schemaVersion: number;
  gameVersion: string;
  locale: string;
  builtAt?: string;
  source?: 'runtime-export' | 'offline-extract';
  items: KbItem[];
  recipes: KbRecipe[];
  techs: KbTech[];
  models?: KbModel[];
  veins?: KbVein[];
  signals?: KbSignal[];
  texts?: KbText[];
}

/** 搜索命中。 */
export interface KbSearchHit {
  kind: KbKind;
  id: number | string;
  name: string;
  summary?: string;
  iconTag?: string;
}

export interface KbGetResult {
  kind: KbKind;
  value: unknown;
}

export interface RecipeTreeNode {
  itemId: number;
  item: string;
  amount: number;
  recipeId?: number;
  recipe?: string;
  crafts?: number;
  timeSeconds?: number;
  inputs: { itemId: number; item: string; amount: number }[];
  byproducts: { itemId: number; item: string; amount: number }[];
}

export interface RecipeTreeResult {
  item: { id: number; name: string };
  amount: number;
  perMinute: boolean;
  tree: RecipeTreeNode;
  raw: { itemId: number; item: string; amount: number }[];
  byproducts: { itemId: number; item: string; amount: number }[];
  cycles: { itemId: number; item: string }[];
  depthLimited: boolean;
  notes: string[];
}

// ---------------------------------------------------------------- 产线规划（M4b）

export interface PlanOptions {
  /** 从哪一层开始：ore（默认，从原矿）/ smelted（从冶炼产物）/ 物品 id（从该中间产物）。 */
  from?: 'ore' | 'smelted' | number;
  /** 目标物品的每分钟产量。 */
  rate: number;
  /** 展开深度；与 kb_recipe_tree 同一口径。 */
  depth?: number;
  /** 强制根配方（用于同物品多配方的情况）。 */
  recipeId?: number;
  /** 机器档位：base = 最低速，mk2/mk3 = 依次更快。 */
  buildingTier?: 'base' | 'mk2' | 'mk3';
  /** 直接指定机器原型 id；给了就不再按 recipe.type 找。 */
  buildingId?: number;
  /** 单条传送带吞吐（物品/秒），默认 6（Mk.I）。 */
  beltSpeed?: number;
  /** 增产剂档位：none（默认）/ mk1 / mk2 / mk3。 */
  proliferator?: 'none' | 'mk1' | 'mk2' | 'mk3';
  /** 增产剂模式：extra = 额外产出（默认），speed = 加速生产。 */
  proliferatorMode?: 'extra' | 'speed';
}

/** 一台机器的功率单位是 kW；`workEnergyPerTick` 是每 tick 工作功率。 */
export interface PlanRecipeRow {
  recipeId: number;
  recipeName: string;
  recipeType?: number;
  buildingId?: number;
  buildingName?: string;
  /** 向上取整后的机器数（实际要建的）。 */
  buildingCount: number;
  /** 未取整的机器数（用于看余量）。 */
  exactCount: number;
  speedMultiplier: number;
  craftsPerMin: number;
  timeSeconds: number;
  powerKw: number;
  inputsPerMin: { itemId: number; item: string; amount: number }[];
  beltsPerInput: number;
  /** 这一行配方在本链里被需要的那个产物；多产物配方用它区分主/副产。 */
  primaryItemId?: number;
  primaryItem?: string;
  /** 本行按 craftsPerMin 折算后的全部产出（含副产物）。 */
  outputsPerMin?: { itemId: number; item: string; amount: number }[];
}

export interface PlanResult {
  item: { id: number; name: string };
  ratePerMin: number;
  recipes: PlanRecipeRow[];
  totalPowerKw: number;
  belts: { itemsPerSec: number; maxSingleInputPerMin: number; maxBelts: number };
  raw: { itemId: number; item: string; amount: number }[];
  byproducts: { itemId: number; item: string; amount: number }[];
  cycles: { itemId: number; item: string }[];
  depthLimited: boolean;
  proliferator: {
    tier: 'none' | 'mk1' | 'mk2' | 'mk3';
    mode: 'extra' | 'speed';
    outputMultiplier: number;
    speedMultiplier: number;
  };
  assumptions: string[];
  warnings: string[];
}
