import { computeBlueprint, generateBlueprint } from '../../vendor/dsp-calc/src/global_blueprint.js';
import { fromStr, toStr } from '../../vendor/dsp-calc/src/blueprint/parser.js';
import { validateParsed } from './validate.mjs';
import { busSpecFromPlan, chainSpecFromPlan, layoutBus, layoutChain } from './layout.mjs';
import vanilla from '../../vendor/dsp-calc/data/Vanilla.mjs';
import buildingsData from '../../vendor/dsp-calc/data/Buildings.mjs';

const recipes = vanilla.recipes;
const items = vanilla.items;
const buildings = buildingsData.buildings;

const itemNameById = new Map(items.map((item) => [item.ID, item.Name]));
const recipeIndexById = new Map(recipes.map((recipe, index) => [recipe.ID, index]));
const buildingKeyByItemId = new Map();
for (const [key, building] of Object.entries(buildings)) {
  if (building && typeof building.itemId === 'number') buildingKeyByItemId.set(building.itemId, key);
}

const MAX_WIDTH = 1000;
const MAX_HEIGHT = 50;
const MAX_ROWS = 8;
const DEFAULT_FACTORY = buildingKeyByItemId.get(2303) ?? Object.keys(buildings)[0] ?? '制造台\u00a0Mk.I';
const RAW_FACTORY = buildingKeyByItemId.get(2301) ?? DEFAULT_FACTORY;

function itemName(itemId) {
  const name = itemNameById.get(itemId);
  if (!name) throw new Error(`规划器数据缺少物品 ${itemId}（dsp-calc 数据与当前游戏版本不一致）`);
  return name;
}

function recipeIndex(recipeId) {
  const index = recipeIndexById.get(recipeId);
  if (index === undefined) {
    throw new Error(`规划器数据缺少配方 ${recipeId}（dsp-calc 数据与当前游戏版本不一致）`);
  }
  return index;
}

function buildingKey(buildingId, fallbackRecipeIndex) {
  const direct = buildingId !== undefined ? buildingKeyByItemId.get(buildingId) : undefined;
  if (direct) return direct;
  const recipe = recipes[fallbackRecipeIndex];
  for (const factoryItemId of recipe?.Factories ?? []) {
    const key = buildingKeyByItemId.get(factoryItemId);
    if (key) return key;
  }
  return DEFAULT_FACTORY;
}

/**
 * 矿物/流体不是由配方产出的，但 `computeBlueprint` 要求每个 unit 都有一个合法配方下标。
 * 这里优先找消耗该原料的配方（熔炼/精炼/化工），找不到再找产出它的配方，最后退回 0。
 * 该配方只用于占位：`isMineralized: true` 的 unit 不会生成机器。
 */
function rawRecipeIndex(itemId) {
  // 优先用 `[无中生有]` 这类 Type < 0 的占位配方：它的 recipeId 不会和真实生产配方冲突，
  // 否则 `produceMap` 会按 recipeId 去重，把同 ID 的真实机器阶段覆盖掉（铁块/铜块就踩过）。
  let index = recipes.findIndex(
    (recipe) => (recipe.Type ?? 0) < 0 && Array.isArray(recipe.Results) && recipe.Results.includes(itemId),
  );
  if (index < 0) {
    index = recipes.findIndex((recipe) => Array.isArray(recipe.Items) && recipe.Items.includes(itemId));
  }
  if (index < 0) {
    index = recipes.findIndex((recipe) => Array.isArray(recipe.Results) && recipe.Results.includes(itemId));
  }
  return index >= 0 ? index : 0;
}

/**
 * vendor 的 compute/generate 里有大量 console.log（甚至整个矩阵）。
 * 同步执行期间临时静默，避免刷爆 sidecar stdout；Node 同步代码不会和别的请求交错。
 */
function silently(fn) {
  const original = console.log;
  console.log = () => {};
  try {
    return fn();
  } finally {
    console.log = original;
  }
}

function clampTier(value, fallback, max) {
  const tier = Number.isFinite(value) ? Math.floor(value) : fallback;
  return Math.max(0, Math.min(max, tier));
}

function clampRows(value) {
  const rows = Number.isFinite(value) ? Math.floor(value) : 1;
  return Math.max(1, Math.min(MAX_ROWS, rows));
}

/**
 * 把 KB 的产线规划（`kb_plan`）翻译成 dsp-calc 的 ProduceUnit，并生成蓝图文本。
 *
 * 关键点（不要再退回旧实现）：
 *  - 机器数直接来自 `plan.recipes[].buildingCount`，它已经按游戏 tick + 机器速度算过；
 *  - 每个中间阶段都进入 `allProduceUnits`，不再把直接输入标记成原矿；
 *  - 原矿只来自 `plan.raw`，由 `isMineralized` 标记；
 *  - `rows` 会按阶段拆分机器单元，让单阶段过宽时也能折行；
 *  - 生成后回读蓝图，校验机器数/尺寸，超出上限时自动加行重试。
 */
export function planBlueprint(plan, options = {}) {
  if (!plan || !plan.item || !Array.isArray(plan.recipes) || !Array.isArray(plan.raw)) {
    throw new Error('缺少产线规划数据');
  }
  const rootName = itemName(plan.item.id);
  const targetRate = Number(plan.ratePerMin);
  if (!(targetRate > 0)) throw new Error('目标产量必须大于 0');

  if (plan.recipes.length < 2) {
    throw new Error('当前混带布局器至少需要两个生产阶段；单阶段配方（例如只造铁块）暂不支持。');
  }

  // 新总线布局：一条蛇形混带穿过所有机器；放不下就依次回退到线性链、旧布局器。
  const busSpec = busSpecFromPlan(plan);
  if (busSpec) {
    try {
      const built = layoutBus({
        ...busSpec,
        beltId: 2001 + clampTier(options.beltType, 2, 2),
        sorterId: 2011 + clampTier(options.insertType, 2, 3),
        title: options.name,
        gameVersion: options.gameVersion || options.version,
        rows: options.rows,
        ...(options.supply ? { supply: options.supply } : {}),
      });
      const parsed = fromStr(built.text);
      const hard = validateParsed(parsed, { maxWidth: MAX_WIDTH, maxHeight: MAX_HEIGHT, limitsAreErrors: true });
      if (hard.ok) {
        const machines = plan.recipes.reduce((sum, row) => sum + (row.buildingCount || 0), 0);
        return {
          text: built.text,
          meta: {
            rows: built.meta.rows,
            requestedRows: clampRows(options.rows ?? built.meta.rows),
            width: built.meta.width,
            height: built.meta.height,
            buildings: parsed.buildings.length,
            machines,
            stages: plan.recipes.map((row) => ({
              recipeId: row.recipeId,
              recipeName: row.recipeName ?? '',
              primaryItemId: row.primaryItemId ?? plan.item.id,
              primaryItem: itemName(row.primaryItemId ?? plan.item.id),
              buildingId: row.buildingId,
              buildingName: row.buildingName ?? '',
              buildingCount: row.buildingCount,
              craftsPerMin: row.craftsPerMin,
              outputsPerMin: row.outputsPerMin,
            })),
            raw: plan.raw,
            byproducts: plan.byproducts ?? [],
            warnings: [...(plan.warnings ?? [])],
            expectedByBuilding: {},
            layout: 'bus',
          },
        };
      }
    } catch (error) {
      // 容量不够要交给调用方按张数拆分，不能悄悄回退到旧布局器。
      if (error?.code === 'bus-capacity') throw error;
      // 其它原因（宽度/高度/模板缺失）就继续走后面的布局。
    }
  }

  // 线性链（每段只吃上一段的产物）走新布局；其余仍用 dsp-calc，直到 M3 补上分支。
  const chain = chainSpecFromPlan(plan);
  if (chain) {
    try {
      const built = layoutChain({
        stages: chain.stages,
        beltId: 2001 + clampTier(options.beltType, 2, 2),
        sorterId: 2011 + clampTier(options.insertType, 2, 3),
        title: options.name,
        gameVersion: options.gameVersion || options.version,
      });
      const chained = fromStr(built.text);
      const hard = validateParsed(chained, { maxWidth: MAX_WIDTH, maxHeight: MAX_HEIGHT, limitsAreErrors: true });
      if (hard.ok) {
        const machines = plan.recipes.reduce((sum, row) => sum + (row.buildingCount || 0), 0);
        return {
          text: built.text,
          meta: {
            rows: 1,
            requestedRows: clampRows(options.rows ?? 1),
            width: built.meta.width,
            height: built.meta.height,
            buildings: chained.buildings.length,
            machines,
            stages: plan.recipes.map((row) => ({
              recipeId: row.recipeId,
              recipeName: row.recipeName ?? '',
              primaryItemId: row.primaryItemId ?? plan.item.id,
              primaryItem: itemName(row.primaryItemId ?? plan.item.id),
              buildingId: row.buildingId,
              buildingName: row.buildingName ?? '',
              buildingCount: row.buildingCount,
              craftsPerMin: row.craftsPerMin,
              outputsPerMin: row.outputsPerMin,
            })),
            raw: plan.raw,
            byproducts: plan.byproducts ?? [],
            warnings: [...(plan.warnings ?? [])],
            expectedByBuilding: {},
            layout: 'chain',
          },
        };
      }
    } catch {
      // 新布局排不下就回退旧布局器，不让功能比现在更差。
    }
  }

  const byproducts = Array.isArray(plan.byproducts) ? plan.byproducts : [];
  if (byproducts.length > 1) {
    throw new Error(
      `这条产线有多个净副产物（${byproducts.map((row) => row.item).join('、')}），当前蓝图布局器只支持一个；` +
      '请先缩小产量或改用“从中间产物开始”的配方。',
    );
  }
  const surplusList = {};
  if (byproducts.length === 1) {
    const row = byproducts[0];
    surplusList[itemName(row.itemId)] = row.amount;
  }

  const stages = [];
  const expectedByBuilding = new Map();
  const requestedRows = clampRows(options.rows ?? 1);
  let best = null;
  let lastError = null;

  for (let rows = requestedRows; rows <= MAX_ROWS; rows += 1) {
    let text;
    try {
      const units = buildUnits(plan, rows, byproducts, stages, expectedByBuilding);
      const blueprint = silently(() => computeBlueprint({
        allProduceUnits: units,
        surplusList,
        produces: { [rootName]: targetRate },
        beltType: options.beltType ?? 2,
        insertType: options.insertType ?? 2,
        recycleMode: options.recycleMode ?? 1,
        rows,
        stackSize: options.stackSize ?? 1,
        floor: options.floor ?? 1,
        stationPiler: options.stationPiler ?? 2,
        proNum: 0,
      }));
      text = silently(() => generateBlueprint(blueprint, {
        title: options.name || undefined,
        author: options.author ?? '自动生成',
        version: options.version ?? options.gameVersion ?? '0.10.35.29151',
        attributes: options.attributes ?? '',
        description: options.description ?? '',
        iconId: plan.item.id,
        gameVersion: options.gameVersion || undefined,
      }));
    } catch (err) {
      lastError = err instanceof Error ? err : new Error(String(err));
      break;
    }

    const parsed = fromStr(text);
    const area = parsed.areas && parsed.areas[0];
    const width = area && area.size ? area.size.x : 0;
    const height = area && area.size ? area.size.y : 0;
    if (best === null || width < best.width || (width === best.width && height < best.height)) {
      best = { width, height, rows };
    }
    if (width > MAX_WIDTH || height > MAX_HEIGHT) {
      lastError = new Error(`蓝图尺寸 ${width}x${height} 超出安全范围（宽度上限 ${MAX_WIDTH} 格、高度上限 ${MAX_HEIGHT} 格）`);
      continue;
    }

    const validationWarnings = validatePlacements(parsed, expectedByBuilding);
    if (validationWarnings.some((warning) => warning.includes('实际放置 0 台'))) {
      throw new Error(`布局器没有放下规划中的机器：${validationWarnings.join('；')}`);
    }
    parsed.header.author = options.author ?? '自动生成';
    parsed.header.customVersion = options.version ?? options.gameVersion ?? '0.10.35.29151';
    parsed.header.externalFields = options.attributes ?? '';
    parsed.header.desc = options.description ?? '';
    breakBeltCycles(parsed);
    // 硬校验：碰撞、分拣器够不着、断带、越界，任何一条都说明贴进游戏会报红。
    const hard = validateParsed(parsed, { maxWidth: MAX_WIDTH, maxHeight: MAX_HEIGHT, limitsAreErrors: true });
    if (!hard.ok) {
      lastError = new Error(`蓝图未通过硬校验：${hard.errors.slice(0, 3).join('；')}`);
      continue;
    }
    const finalText = toStr(parsed);
    const machines = plan.recipes.reduce((sum, row) => sum + (row.buildingCount || 0), 0);
    return {
      text: finalText,
      meta: {
        rows,
        requestedRows,
        width,
        height,
        buildings: parsed.buildings.length,
        machines,
        stages,
        raw: plan.raw,
        byproducts,
        warnings: [...(plan.warnings ?? []), ...validationWarnings],
        expectedByBuilding: Object.fromEntries(expectedByBuilding),
      },
    };
  }

  const hint = best
    ? `最长只能排到 ${best.width}x${best.height}（rows=${best.rows}）。` +
      `建议把产量降到约 ${Math.max(1, Math.floor(targetRate * (MAX_WIDTH / Math.max(1, best.width)) * 0.9))}/min，` +
      '或拆成多张蓝图。'
    : '';
  throw new Error(`${lastError ? lastError.message : '蓝图生成失败'}${hint ? ` ${hint}` : ''}`);
}

function buildUnits(plan, rows, byproducts, stages, expectedByBuilding) {
  stages.length = 0;
  expectedByBuilding.clear();
  const units = [];
  const surplusByItem = new Map(byproducts.map((row) => [row.itemId, row]));

  for (const row of plan.recipes) {
    const index = recipeIndex(row.recipeId);
    const recipe = recipes[index];
    const primaryId = row.primaryItemId ?? plan.item.id;
    const primaryName = itemName(primaryId);
    const outputs = Array.isArray(row.outputsPerMin)
      ? row.outputsPerMin
      : (recipe.Results ?? []).map((id, i) => ({
          itemId: id,
          item: itemName(id),
          amount: (recipe.ResultCounts?.[i] ?? 1) * row.craftsPerMin,
        }));
    const primaryOutput = outputs.find((output) => output.itemId === primaryId);
    const grossOutput = primaryOutput ? primaryOutput.amount : row.craftsPerMin;
    const factory = buildingKey(row.buildingId, index);
    const perUnitSideProducts = {};
    for (const output of outputs) {
      if (output.itemId === primaryId) continue;
      if (!surplusByItem.has(output.itemId)) continue;
      perUnitSideProducts[output.item] = (perUnitSideProducts[output.item] ?? 0) + output.amount / rows;
    }

    const split = rows > 1 && row.buildingCount > rows ? rows : 1;
    const perCount = Math.max(1, Math.ceil(row.buildingCount / split));
    const perGross = grossOutput / split;
    for (let i = 0; i < split; i += 1) {
      units.push({
        item: primaryName,
        grossOutput: perGross,
        factory,
        recipe: index,
        recipeId: row.recipeId,
        factoryNumber: perCount,
        isMineralized: false,
        sideProducts: { ...perUnitSideProducts },
        proNum: 0,
        theoryOutput: perGross,
      });
    }

    stages.push({
      recipeId: row.recipeId,
      recipeName: row.recipeName,
      primaryItemId: primaryId,
      primaryItem: primaryName,
      buildingId: row.buildingId,
      buildingName: row.buildingName,
      buildingCount: row.buildingCount,
      craftsPerMin: row.craftsPerMin,
      outputsPerMin: outputs,
    });
    if (row.buildingId !== undefined) {
      expectedByBuilding.set(row.buildingId, (expectedByBuilding.get(row.buildingId) ?? 0) + perCount * split);
    }
  }

  for (const raw of plan.raw) {
    const name = itemName(raw.itemId);
    const index = rawRecipeIndex(raw.itemId);
    units.push({
      item: name,
      grossOutput: raw.amount,
      factory: RAW_FACTORY,
      recipe: index,
      recipeId: recipes[index].ID,
      factoryNumber: 0,
      isMineralized: true,
      sideProducts: {},
      proNum: 0,
      theoryOutput: raw.amount,
    });
  }

  return units;
}

function validatePlacements(parsed, expectedByBuilding) {
  const warnings = [];
  if (expectedByBuilding.size === 0) return warnings;
  const actual = new Map();
  for (const building of parsed.buildings) {
    actual.set(building.itemId, (actual.get(building.itemId) ?? 0) + 1);
  }
  for (const [buildingId, expected] of expectedByBuilding) {
    const got = actual.get(buildingId) ?? 0;
    if (got !== expected) {
      warnings.push(`建筑 ${buildingId} 实际放置 ${got} 台，规划 ${expected} 台`);
    }
  }
  return warnings;
}

/**
 * 带子首尾相连：只在环上断开“回边”，合法长带子（非环）保持不动。
 * 这是参考实现的边界处理，避免粘贴时报“距离太远”。
 */
function breakBeltCycles(parsed) {
  const isBelt = (building) => building && building.itemId >= 2001 && building.itemId <= 2009;
  const out = new Map();
  for (const building of parsed.buildings) {
    if (isBelt(building)) out.set(building.index, building.outputObjIdx);
  }
  const state = new Map();
  const visit = (index) => {
    if (state.get(index) === 1) return true;
    if (state.get(index) === 2) return false;
    state.set(index, 1);
    const next = out.get(index);
    if (typeof next === 'number' && next >= 0 && out.has(next) && visit(next)) {
      const building = parsed.buildings.find((row) => row.index === index);
      if (building) building.outputObjIdx = -1;
      state.set(index, 2);
      return false;
    }
    state.set(index, 2);
    return false;
  };
  for (const index of out.keys()) visit(index);
}
