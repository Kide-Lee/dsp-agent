import { fromStr } from '../../vendor/dsp-calc/src/blueprint/parser.js';
import { validateParsed } from './validate.mjs';
import { busSpecFromPlan, layoutBus } from './layout.mjs';
import vanilla from '../../vendor/dsp-calc/data/Vanilla.mjs';

const itemNameById = new Map(vanilla.items.map((item) => [item.ID, item.Name]));

const MAX_WIDTH = 1000;
const MAX_HEIGHT = 50;
const MAX_ROWS = 8;

function itemName(itemId) {
  const name = itemNameById.get(itemId);
  if (!name) throw new Error(`规划器数据缺少物品 ${itemId}（dsp-calc 数据与当前游戏版本不一致）`);
  return name;
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
 * 把知识库的产线规划交给总线布局出图。
 *
 * 只有一条路径：总线（混带）布局。放不下就抛 `bus-capacity` 让上层按张数拆分，
 * 其它错误直接抛出，不再回退到线性链或旧布局器。
 */
export function planBlueprint(plan, options = {}) {
  if (!plan || !plan.item || !Array.isArray(plan.recipes) || !Array.isArray(plan.raw)) {
    throw new Error('缺少产线规划数据');
  }
  const targetRate = Number(plan.ratePerMin);
  if (!(targetRate > 0)) throw new Error('目标产量必须大于 0');

  const busSpec = busSpecFromPlan(plan);
  if (!busSpec) {
    throw new Error('这条产线含有总线布局不支持的建筑，暂时出不了图；可以换机器或降低产量再试。');
  }

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
  if (!hard.ok) {
    throw new Error(`总线布局未通过硬校验：${hard.errors.slice(0, 3).join('；')}`);
  }

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
      layout: 'bus',
    },
  };
}
