import { fromStr } from '../../vendor/dsp-calc/src/blueprint/parser.js';
import { validateParsed } from './validate.mjs';
import { busSpecFromPlan, layoutBus } from './layout.mjs';
import { layoutFactory } from './factory.mjs';
import vanilla from '../../vendor/dsp-calc/data/Vanilla.mjs';

const itemNameById = new Map(vanilla.items.map((item) => [item.ID, item.Name]));

const MAX_WIDTH = 1000;
const MAX_HEIGHT = 50;
const MAX_FACTORY_HEIGHT = 100;
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

/** 蓝图统计：建筑清单、材料、物流站配置、空间利用率。 */
function reportOf(parsed, plan, meta) {
  const counts = new Map();
  for (const building of parsed.buildings) counts.set(building.itemId, (counts.get(building.itemId) ?? 0) + 1);
  const rows = [...counts].map(([itemId, count]) => ({ itemId, name: itemName(itemId), count }));
  rows.sort((a, b) => b.count - a.count);
  const sum = (from, to) => rows.filter((row) => row.itemId >= from && row.itemId <= to).reduce((total, row) => total + row.count, 0);
  const stations = [];
  for (const building of parsed.buildings) {
    if (building.itemId !== 2103 && building.itemId !== 2104) continue;
    const storage = (building.parameters?.storage ?? [])
      .filter((slot) => slot.itemId)
      .map((slot) => ({ item: itemName(slot.itemId), max: slot.max, role: slot.localRole === 1 ? '供给' : slot.localRole === 2 ? '需求' : '无' }));
    const ports = (building.parameters?.slots ?? [])
      .map((slot, index) => ({ index, dir: slot.dir, storageIdx: slot.storageIdx }))
      .filter((slot) => slot.dir);
    stations.push({ itemId: building.itemId, x: building.localOffset[0].x, y: building.localOffset[0].y, storage, ports });
  }
  const area = meta.width * meta.height;
  // 2300..2399 / 2900..2999 是生产机器；2001..2014 是带子/分拣器，2103/2104 是物流站。
  const isMachine = (itemId) => (itemId >= 2300 && itemId < 2400) || (itemId >= 2900 && itemId < 3000);
  const machineArea = rows.filter((row) => isMachine(row.itemId)).reduce((total, row) => total + row.count * 2.25, 0);
  return {
    area: { width: meta.width, height: meta.height },
    buildings: rows,
    belts: sum(2001, 2009),
    sorters: sum(2011, 2014),
    stations,
    materials: plan.raw,
    byproducts: plan.byproducts ?? [],
    utilization: area > 0 ? Math.round((machineArea / area) * 1000) / 10 : 0,
  };
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

  const shared = {
    beltId: 2001 + clampTier(options.beltType, 2, 2),
    sorterId: 2011 + clampTier(options.insertType, 2, 3),
    title: options.name,
    gameVersion: options.gameVersion || options.version,
  };
  const built = options.layout === 'factory'
    ? layoutFactory({ ...busSpec, ...shared })
    : layoutBus({
    ...busSpec,
    ...shared,
    rows: options.rows,
    ...(options.supply ? { supply: options.supply } : {}),
  });
  const parsed = fromStr(built.text);
  const maxHeight = options.layout === 'factory' ? MAX_FACTORY_HEIGHT : MAX_HEIGHT;
  const hard = validateParsed(parsed, { maxWidth: MAX_WIDTH, maxHeight, limitsAreErrors: true });
  if (!hard.ok) {
    const layoutName = options.layout === 'factory' ? '工厂布局' : '总线布局';
    throw new Error(`${layoutName}未通过硬校验：${hard.errors.slice(0, 3).join('；')}`);
  }

  const machines = plan.recipes.reduce((sum, row) => sum + (row.buildingCount || 0), 0);
  return {
    text: built.text,
    meta: {
      rows: built.meta.rows ?? 1,
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
      layout: options.layout === 'factory' ? 'factory' : 'bus',
      report: reportOf(parsed, plan, built.meta),
    },
  };
}
