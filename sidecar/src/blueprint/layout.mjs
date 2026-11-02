// 布局 v1：一期只做“单段机器行”——一排机器 + 最多三条输入带 + 一条输出带。
// 多段串联、分支和物流站在后续里程碑补，这里先把出图器与几何跑通。
import buildingData from '../../vendor/dsp-calc/data/Buildings.mjs';
import { COLLIDERS } from './colliders.mjs';
import { BlueprintBuilder } from './emit.mjs';

const BUILDINGS = buildingData.buildings;

function buildingOf(itemId) {
  for (const building of Object.values(BUILDINGS)) if (building.itemId === itemId) return building;
  return null;
}

/** 机器间距：不小于碰撞体宽度，再留一点余量。 */
export function machineSpacing(itemId) {
  const collider = COLLIDERS.get(itemId);
  return Math.max(2, Math.ceil((collider?.ext[0] ?? 1) * 2));
}

/**
 * 单段机器行。
 * @param {object} spec
 * @param {number} spec.buildingId 机器物品 ID
 * @param {number} spec.count 台数
 * @param {number[]} spec.inputs 输入物品 ID（最多 3 个，顺序对应三条输入带）
 * @param {number} spec.output 输出物品 ID
 * @param {number} [spec.beltId] 传送带物品 ID（2001/2002/2003）
 * @param {number} [spec.sorterId] 分拣器物品 ID（2011..2014）
 * @param {string} [spec.title]
 * @param {string} [spec.gameVersion]
 */
export function layoutSingleStage(spec) {
  const { buildingId, count, inputs, output } = spec;
  if (!Number.isInteger(count) || count < 1) throw new Error(`机器台数无效 ${count}`);
  if (!Array.isArray(inputs) || inputs.length < 1 || inputs.length > 3) throw new Error('输入物品需要 1..3 个');
  if (!buildingOf(buildingId)) throw new Error(`没有建筑数据 ${buildingId}`);

  const beltId = spec.beltId ?? 2003;
  const sorterId = spec.sorterId ?? 2013;
  const spacing = machineSpacing(buildingId);
  const x0 = 2;
  const machineY = 5;
  const lastX = x0 + (count - 1) * spacing;
  const laneStart = x0 - 1;
  const laneEnd = lastX + 1;
  const width = laneEnd + 2;
  const height = 9;

  const builder = new BlueprintBuilder({
    width,
    height,
    title: spec.title ?? '单段产线',
    gameVersion: spec.gameVersion,
    iconId: output,
  });

  const machines = [];
  for (let i = 0; i < count; i += 1) machines.push(builder.add(buildingId, x0 + i * spacing, machineY, 0, { recipeId: 0 }));

  const buildLane = (y, itemId, label) => {
    const belts = [];
    for (let x = laneStart; x <= laneEnd; x += 1) belts.push(builder.addBelt(beltId, x, y, 0, 'E'));
    for (let i = 0; i + 1 < belts.length; i += 1) builder.link(belts[i], belts[i + 1]);
    if (label) builder.label(belts[0], itemId);
    return belts;
  };

  const laneByX = new Map();
  const lanes = inputs.map((itemId, index) => {
    const y = machineY - 2 - index;
    const belts = buildLane(y, itemId, true);
    for (const belt of belts) laneByX.set(`${belt.localOffset[0].x},${belt.localOffset[0].y}`, belt);
    return { itemId, y, offset: index - 1 };
  });

  for (const machine of machines) {
    const mx = machine.localOffset[0].x;
    for (const lane of lanes) {
      const belt = laneByX.get(`${mx + lane.offset},${lane.y}`);
      if (!belt) throw new Error(`输入带缺少格子 (${mx + lane.offset},${lane.y})`);
      builder.addSorter(sorterId, { belt, factory: machine, side: 'bottom', offset: lane.offset, io: 'in' });
    }
  }

  const outputLane = buildLane(machineY + 2, output, false);
  const outByX = new Map(outputLane.map((belt) => [belt.localOffset[0].x, belt]));
  for (const machine of machines) {
    const belt = outByX.get(machine.localOffset[0].x);
    if (!belt) throw new Error('输出带缺少对应格子');
    builder.addSorter(sorterId, { belt, factory: machine, side: 'top', offset: 0, io: 'out', itemIdFilter: output });
  }

  return { text: builder.finish(), meta: { width, height, machines: machines.length, inputs: inputs.length } };
}
