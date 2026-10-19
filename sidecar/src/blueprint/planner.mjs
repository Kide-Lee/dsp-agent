import { computeBlueprint, generateBlueprint } from '../../vendor/dsp-calc/src/global_blueprint.js';
import vanilla from '../../vendor/dsp-calc/data/Vanilla.mjs';
import buildingsData from '../../vendor/dsp-calc/data/Buildings.mjs';

const recipes = vanilla.recipes;
const items = vanilla.items;
const buildings = buildingsData.buildings;

const itemById = new Map(items.map((i) => [i.ID, i]));
const buildingByItemId = new Map();
for (const [name, b] of Object.entries(buildings)) buildingByItemId.set(b.itemId, name);
const recipeIndexById = new Map(recipes.map((r, i) => [r.ID, i]));

function recipeFor(itemId) {
  return recipes.findIndex((r) => r.Results && r.Results.includes(itemId));
}

function unitFor(itemId, rate, mineralized) {
  const ri = recipeFor(itemId);
  if (ri < 0) return null;
  const r = recipes[ri];
  const name = itemById.get(itemId)?.Name ?? String(itemId);
  const factoryItem = (r.Factories || []).find((id) => buildingByItemId.has(id));
  const factory = factoryItem ? buildingByItemId.get(factoryItem) : '制造台\u00a0Mk.I';
  const outCount = r.ResultCounts?.[0] || 1;
  const perMachine = (outCount * 60) / Math.max(1, r.TimeSpend || 1);
  const factoryNumber = mineralized ? 0 : Math.max(1, Math.ceil(rate / perMachine));
  return {
    item: name,
    grossOutput: rate,
    factory,
    recipe: ri,
    factoryNumber,
    isMineralized: !!mineralized,
    sideProducts: {},
    proNum: 0,
    theoryOutput: rate,
  };
}

export function findItemIdByName(name) {
  const hit = items.find((i) => i.Name === name);
  return hit ? hit.ID : 0;
}

export function planSingle(itemId, rate, options = {}) {
  const rowCount = Math.max(1, Math.min(8, Math.floor(options.rows ?? 1)));
  const ri = recipeFor(itemId);
  if (ri < 0) throw new Error(`没有配方能产出物品 ${itemId}`);
  const r = recipes[ri];
  const perRow = rate / rowCount;
  const units = [];
  for (let r = 0; r < rowCount; r++) units.push(unitFor(itemId, perRow, false));
  for (let i = 0; i < (r.Items || []).length; i++) {
    const inputId = r.Items[i];
    const need = (r.ItemCounts?.[i] || 1) * perRow / (r.ResultCounts?.[0] || 1);
    const u = unitFor(inputId, need, true);
    if (u) units.push(u);
  }
  const produces = { [units[0].item]: rate };
  const blueprint = computeBlueprint({
    allProduceUnits: units,
    surplusList: {},
    produces,
    beltType: options.beltType ?? 2,
    insertType: options.insertType ?? 2,
    recycleMode: options.recycleMode ?? 1,
    rows: rowCount,
    stackSize: options.stackSize ?? 1,
    floor: options.floor ?? 1,
    stationPiler: options.stationPiler ?? 2,
    proNum: 0,
  });
  return generateBlueprint(blueprint, {
    title: options.name || undefined,
    author: options.author ?? '自动生成',
    version: options.version ?? '0.10.35.29151',
    attributes: options.attributes ?? '',
    description: options.description ?? '',
    iconId: itemId,
    gameVersion: options.gameVersion || undefined,
  });
}
