// 布局 v1：一期只做“单段机器行”——一排机器 + 最多三条输入带 + 一条输出带。
// 多段串联、分支和物流站在后续里程碑补，这里先把出图器与几何跑通。
import buildingData from '../../vendor/dsp-calc/data/Buildings.mjs';
import vanilla from '../../vendor/dsp-calc/data/Vanilla.mjs';
import { COLLIDERS } from './colliders.mjs';
import { SLOTS } from './slots.mjs';
import { PORT_OFFSETS, rotateOffset } from './ports.mjs';
import { BlueprintBuilder } from './emit.mjs';

const BUILDINGS = buildingData.buildings;
const RECIPES = new Map(vanilla.recipes.map((recipe) => [recipe.ID, recipe]));
const ITEM_NAMES = new Map(vanilla.items.map((item) => [item.ID, item.Name]));

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
 * 总线布局怎么摆这台机器：
 *   top    —— 正上方有中间槽位（制造台/熔炉），机器在带子下方、朝向 0，分拣器长度 1。
 *   bottom —— 只有下方中间槽位（精炼厂），转 180° 让端口朝上，用小数端口分拣器。
 * 两种都放不了就返回 null，由调用方回退旧布局。
 */
export function busPortPlan(itemId) {
  const offsets = PORT_OFFSETS.get(itemId);
  const collider = COLLIDERS.get(itemId);
  if (!offsets || !collider) return null;
  for (const yaw of [0, 180]) {
    const ports = [...offsets.entries()]
      .map(([slot, offset]) => ({ slot, ...rotateOffset(offset.dx, offset.dy, yaw) }))
      .filter((port) => port.dy > 0.5);
    if (ports.length < 2) continue;
    const input = ports.reduce((best, port) => (Math.abs(port.dx) < Math.abs(best.dx) ? port : best));
    const outputs = ports.filter((port) => port.slot !== input.slot);
    if (!outputs.length) continue;
    const output = outputs.reduce((best, port) => (Math.abs(port.dx - 1) < Math.abs(best.dx - 1) ? port : best));
    const inCell = Math.round(input.dx);
    const outCell = Math.round(output.dx);
    if (Math.abs(input.dx - inCell) > 0.6 || Math.abs(output.dx - outCell) > 0.6) continue;
    if (inCell === outCell) continue;
    return {
      yaw,
      yOffset: Math.ceil(input.dy + 1 - 1e-9),
      inputSlot: input.slot,
      outputSlot: output.slot,
      inCell,
      outCell,
      halfX: collider.ext[0] + Math.abs(collider.pos[0]),
      halfZ: collider.ext[2] + Math.abs(collider.pos[2]),
    };
  }
  return null;
}

/**
 * 总线布局怎么摆这台机器：
 *   top  —— 正上方有中间槽位（制造台/熔炉），机器朝 0、带子在上、分拣器长度 1。
 *   port —— 有端口偏移数据（精炼厂/位面熔炉/化工厂…），按数据选一侧端口和朝向。
 * 两种都放不了就返回 null，由调用方回退旧布局。
 */
export function busStageMode(itemId) {
  const collider = COLLIDERS.get(itemId);
  const slots = SLOTS.get(itemId);
  if (!collider) return null;
  const topCenter = slots?.some((slot) => Math.abs(slot.x) <= 0.3 && slot.z >= 0.9 && slot.z <= 1.2);
  if (topCenter && collider.ext[0] <= 2.5 && collider.ext[2] <= 2.5) {
    return { mode: 'top', yOffset: 2, yaw: 0, halfX: collider.ext[0] + Math.abs(collider.pos[0]), halfZ: collider.ext[2] + Math.abs(collider.pos[2]) };
  }
  const port = busPortPlan(itemId);
  if (port) return { mode: 'port', ...port };
  return null;
}

export function busCompatible(itemId) {
  return busStageMode(itemId) !== null;
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

/**
 * 线性链：每段一行机器，段与段之间用右侧竖直连接带串起来，原料从左边缘进。
 * 约束（v1）：每段最多 3 个输入；第 s 段的 primary 必须来自第 s-1 段，其余算原料。
 *
 * @param {object} spec
 * @param {{buildingId:number, count:number, primary?:number, raws:number[], output:number, recipeId?:number}[]} spec.stages
 */
export function layoutChain(spec) {
  const stages = spec.stages;
  if (!Array.isArray(stages) || stages.length < 2) throw new Error('线性链至少需要两段');
  const beltId = spec.beltId ?? 2003;
  const sorterId = spec.sorterId ?? 2013;
  const rowGap = 9;
  const x0 = 2;

  const lastXs = stages.map((s) => x0 + (s.count - 1) * machineSpacing(s.buildingId));
  const laneEnds = lastXs.map((x) => x + 1);
  const connectors = [];
  for (let s = 1; s < stages.length; s += 1) connectors.push(Math.max(laneEnds[s - 1], laneEnds[s]) + 2);

  const width = Math.max(...laneEnds, ...connectors) + 2;
  const height = 8 + rowGap * (stages.length - 1) + 3;
  const builder = new BlueprintBuilder({
    width,
    height,
    title: spec.title ?? '线性产线',
    gameVersion: spec.gameVersion,
    iconId: stages.at(-1).output,
  });

  const lane = (from, to, y, dir) => {
    const belts = [];
    const step = dir === 'E' ? 1 : -1;
    for (let x = from; step > 0 ? x <= to : x >= to; x += step) belts.push(builder.addBelt(beltId, x, y, 0, dir));
    for (let i = 0; i + 1 < belts.length; i += 1) builder.link(belts[i], belts[i + 1]);
    return belts;
  };

  let pendingConnector = null;
  for (let s = 0; s < stages.length; s += 1) {
    const stage = stages[s];
    const y = 8 + rowGap * s;
    const laneStart = 1;
    const laneEnd = laneEnds[s];
    const inputs = [];

    if (s > 0) {
      const c = connectors[s - 1];
      const primary = stage.primary;
      if (!primary) throw new Error(`第 ${s + 1} 段缺少 primary 输入`);
      const belts = lane(c, laneStart, y - 2, 'W');
      if (pendingConnector) builder.link(pendingConnector, belts[0]);
      pendingConnector = null;
      inputs.push({ itemId: primary, y: y - 2, offset: -1, belts });
    }

    for (const [index, itemId] of stage.raws.entries()) {
      const slot = s > 0 ? index + 1 : index;
      const beltY = y - 2 - slot;
      const belts = lane(laneStart, laneEnd, beltY, 'E');
      builder.label(belts[0], itemId);
      inputs.push({ itemId, y: beltY, offset: slot - 1, belts });
    }
    if (inputs.length > 3) throw new Error(`第 ${s + 1} 段输入超过 3 条`);

    const machines = [];
    for (let i = 0; i < stage.count; i += 1) {
      machines.push(builder.add(stage.buildingId, x0 + i * machineSpacing(stage.buildingId), y, 0, { recipeId: stage.recipeId ?? 0 }));
    }

    const laneByXY = new Map();
    for (const input of inputs) for (const belt of input.belts) laneByXY.set(`${belt.localOffset[0].x},${belt.localOffset[0].y}`, belt);

    for (const machine of machines) {
      const mx = machine.localOffset[0].x;
      for (const input of inputs) {
        const belt = laneByXY.get(`${mx + input.offset},${input.y}`);
        if (!belt) throw new Error(`第 ${s + 1} 段输入带缺少格子 (${mx + input.offset},${input.y})`);
        builder.addSorter(sorterId, { belt, factory: machine, side: 'bottom', offset: input.offset, io: 'in' });
      }
    }

    const outputEnd = s + 1 < stages.length ? connectors[s] : laneEnd;
    const outLane = lane(1, outputEnd, y + 2, 'E');
    const outByX = new Map(outLane.map((belt) => [belt.localOffset[0].x, belt]));
    for (const machine of machines) {
      const belt = outByX.get(machine.localOffset[0].x);
      if (!belt) throw new Error('输出带缺少对应格子');
      builder.addSorter(sorterId, { belt, factory: machine, side: 'top', offset: 0, io: 'out', itemIdFilter: stage.output });
    }
    if (s + 1 === stages.length) builder.label(outLane.at(-1), stage.output);

    if (s + 1 < stages.length) {
      const c = connectors[s];
      let prev = outByX.get(c);
      if (!prev) throw new Error('输出带没有接到连接带上');
      for (let cy = y + 3; cy <= y + rowGap - 3; cy += 1) {
        const belt = builder.addBelt(beltId, c, cy, 0, 'N');
        builder.link(prev, belt);
        prev = belt;
      }
      pendingConnector = prev;
    }
  }

  return { text: builder.finish(), meta: { width, height, stages: stages.length } };
}

/**
 * 从 KB 产线规划里认出“线性链”：每段只吃上一段的产物，其余输入都是原料。
 * KB 的 recipes 是“目标在前、上游在后”，这里先按依赖做拓扑排序；
 * 认不出就返回 null，由调用方回退到旧布局器。
 */
export function chainSpecFromPlan(plan) {
  if (!plan?.item || !Array.isArray(plan.recipes) || plan.recipes.length < 2) return null;
  if (Array.isArray(plan.byproducts) && plan.byproducts.length > 0) return null;

  const nodes = [];
  const producerOf = new Map();
  for (const [index, row] of plan.recipes.entries()) {
    const recipe = RECIPES.get(row.recipeId);
    if (!recipe) return null;
    const primary = row.primaryItemId ?? plan.item.id;
    const outputs = Array.isArray(row.outputsPerMin) && row.outputsPerMin.length
      ? row.outputsPerMin.map((output) => output.itemId)
      : (recipe.Results ?? []);
    if (outputs.length !== 1 || outputs[0] !== primary) return null;
    if (producerOf.has(primary)) return null; // 同一物品两段生产，链不唯一
    producerOf.set(primary, index);
    nodes.push({ index, row, recipe, primary });
  }

  // 生产者 → 消费者的边，按依赖拓扑排序；有环就不是产线。
  const order = [];
  const indegree = new Map(nodes.map((node) => [node.index, 0]));
  const consumers = new Map(nodes.map((node) => [node.index, []]));
  for (const node of nodes) {
    for (const input of node.recipe.Items ?? []) {
      const producer = producerOf.get(input);
      if (producer === undefined) continue;
      if (producer === node.index) return null;
      consumers.get(producer).push(node.index);
      indegree.set(node.index, indegree.get(node.index) + 1);
    }
  }
  const queue = nodes.filter((node) => indegree.get(node.index) === 0).map((node) => node.index);
  while (queue.length) {
    const index = queue.shift();
    order.push(index);
    for (const next of consumers.get(index)) {
      indegree.set(next, indegree.get(next) - 1);
      if (indegree.get(next) === 0) queue.push(next);
    }
  }
  if (order.length !== nodes.length) return null;

  const byIndex = new Map(nodes.map((node) => [node.index, node]));
  const specs = [];
  for (const [position, index] of order.entries()) {
    const { row, recipe, primary } = byIndex.get(index);
    const inputs = recipe.Items ?? [];
    const fromStage = inputs.filter((id) => producerOf.has(id));
    const raws = inputs.filter((id) => !producerOf.has(id));
    if (position === 0) {
      if (fromStage.length !== 0 || raws.length > 3) return null;
      specs.push({ buildingId: row.buildingId, count: row.buildingCount, raws, output: primary, recipeId: row.recipeId });
      continue;
    }
    if (fromStage.length !== 1 || producerOf.get(fromStage[0]) !== order[position - 1]) return null;
    if (raws.length > 2) return null;
    specs.push({
      buildingId: row.buildingId,
      count: row.buildingCount,
      primary: fromStage[0],
      raws,
      output: primary,
      recipeId: row.recipeId,
    });
  }
  return { stages: specs };
}

/** 把 KB 规划整理成“任意 DAG”的段列表（拓扑排序）。认不出返回 null。 */
export function busSpecFromPlan(plan) {
  if (!plan?.item || !Array.isArray(plan.recipes) || plan.recipes.length === 0) return null;
  const nodes = [];
  const producersOf = new Map();
  for (const [index, row] of plan.recipes.entries()) {
    const recipe = RECIPES.get(row.recipeId);
    if (!recipe) return null;
    const primary = row.primaryItemId ?? plan.item.id;
    const outputs = (row.outputsPerMin ?? []).map((output) => ({ itemId: output.itemId, rate: output.amount }));
    if (outputs.length === 0 || !outputs.some((output) => output.itemId === primary)) return null;
    for (const output of outputs) {
      if (!producersOf.has(output.itemId)) producersOf.set(output.itemId, []);
      producersOf.get(output.itemId).push(index);
    }
    nodes.push({ index, row, recipe, primary, outputs });
  }
  const indegree = new Map(nodes.map((node) => [node.index, 0]));
  const consumers = new Map(nodes.map((node) => [node.index, []]));
  for (const node of nodes) {
    const linked = new Set();
    for (const input of node.recipe.Items ?? []) {
      for (const producer of producersOf.get(input) ?? []) {
        if (producer === node.index || linked.has(producer)) continue;
        linked.add(producer);
        consumers.get(producer).push(node.index);
        indegree.set(node.index, indegree.get(node.index) + 1);
      }
    }
  }
  const queue = nodes.filter((node) => indegree.get(node.index) === 0).map((node) => node.index);
  const order = [];
  while (queue.length) {
    const index = queue.shift();
    order.push(index);
    for (const next of consumers.get(index)) {
      indegree.set(next, indegree.get(next) - 1);
      if (indegree.get(next) === 0) queue.push(next);
    }
  }
  if (order.length !== nodes.length) return null;
  const byIndex = new Map(nodes.map((node) => [node.index, node]));
  return {
    stages: order.map((index) => {
      const { row, recipe, primary, outputs } = byIndex.get(index);
      return {
        buildingId: row.buildingId,
        count: row.buildingCount,
        recipeId: row.recipeId,
        output: primary,
        outputs,
        inputs: recipe.Items ?? [],
        inputRates: new Map((row.inputsPerMin ?? []).map((input) => [input.itemId, input.amount])),
      };
    }),
    target: plan.item.id,
    rawRates: new Map((plan.raw ?? []).map((row) => [row.itemId, row.amount])),
  };
}

const BELT_CAPACITY_PER_MIN = { 2001: 360, 2002: 720, 2003: 1800 };

/**
 * 总线布局：一条蛇形传送带穿过所有机器，机器在带子下方，
 * 输入分拣器从带子取料、输出分拣器把产物放回带子下游；原料在行首用带间分拣器放上总线。
 */
export function layoutBus(spec) {
  const { stages, target } = spec;
  if (!Array.isArray(stages) || stages.length === 0) throw new Error('总线布局至少需要一段');
  const beltId = spec.beltId ?? 2003;
  const sorterId = spec.sorterId ?? 2013;
  for (const stage of stages) {
    const mode = busStageMode(stage.buildingId);
    if (!mode) {
      const error = new Error(`建筑 ${stage.buildingId} 的槽位不在正上方，总线布局放不了`);
      error.code = 'bus-unsupported';
      throw error;
    }
    stage.mode = mode;
  }
  const rowGap = Math.max(8, Math.ceil(Math.max(...stages.map((stage) => stage.mode.yOffset + stage.mode.halfZ + 0.5))));
  const spacingOf = (stage) => machineSpacing(stage.buildingId);

  let totalCells = 0;
  for (const stage of stages) totalCells += stage.count * (spacingOf(stage) + 1);
  const raws = [...new Set(stages.flatMap((stage) => stage.inputs.filter((id) => !stages.some((s) => s.output === id))))];
  totalCells += raws.length * 5 + 4;

  let width = Math.ceil(Math.sqrt(totalCells * 7));
  width = Math.max(40, Math.min(240, width));
  let rows = Math.max(1, Math.ceil(totalCells / (width - 2)));
  const maxHeight = spec.maxHeight ?? 50;
  const maxRows = Math.max(1, Math.floor((maxHeight - 8) / rowGap) + 1);
  const requestedRows = Number.isFinite(spec.rows) ? Math.floor(spec.rows) : 0;
  if (requestedRows >= 1) {
    // rows 只是偏好：按它算出来的宽度放不进 240 格就回到自动分页，避免挤成一行放不下。
    const wantRows = Math.max(1, Math.min(64, Math.min(requestedRows, maxRows)));
    const wantWidth = Math.ceil(totalCells / wantRows) + 2;
    if (wantWidth <= 240) {
      rows = wantRows;
      width = Math.max(40, wantWidth);
    }
  }
  // 行距变大时（精炼厂等）同样行数会把高度顶出上限，先加宽再降行数。
  while (rows > maxRows && width < 240) {
    width = Math.min(240, width + 10);
    rows = Math.max(1, Math.ceil(totalCells / (width - 2)));
  }
  if (rows > maxRows) {
    const error = new Error(`总线布局在高度 ${maxHeight} 内排不下（需要 ${rows} 行）`);
    error.code = 'bus-capacity';
    error.tiles = 2;
    throw error;
  }
  const height = 4 + rowGap * (rows - 1) + 4;

  const path = [];
  for (let r = 0; r < rows; r += 1) {
    const y = 4 + rowGap * r;
    if (r % 2 === 0) {
      for (let x = 1; x <= width - 1; x += 1) path.push({ x, y, dir: 'E' });
      if (r + 1 < rows) for (let yy = y + 1; yy < y + rowGap; yy += 1) path.push({ x: width - 1, y: yy, dir: 'N' });
    } else {
      for (let x = width - 1; x >= 1; x -= 1) path.push({ x, y, dir: 'W' });
      if (r + 1 < rows) for (let yy = y + 1; yy < y + rowGap; yy += 1) path.push({ x: 1, y: yy, dir: 'N' });
    }
  }
  const indexOf = new Map(path.map((cell, index) => [`${cell.x},${cell.y}`, index]));

  const builder = new BlueprintBuilder({ width, height, title: spec.title ?? '总线产线', gameVersion: spec.gameVersion, iconId: target });
  const belts = path.map((cell) => builder.addBelt(beltId, cell.x, cell.y, 0, cell.dir));
  for (let i = 0; i + 1 < belts.length; i += 1) builder.link(belts[i], belts[i + 1]);
  builder.label(belts.at(-1), target);

  // 原料：行首上方各拉一条短带，再用带间分拣器放到总线上。
  const events = new Map();
  const addEvent = (itemId, index, delta) => {
    if (!events.has(itemId)) events.set(itemId, []);
    events.get(itemId).push({ index, delta });
  };
  for (const [i, itemId] of raws.entries()) {
    const x0 = 1 + 5 * i;
    const lane = [builder.addBelt(beltId, x0, 6, 0, 'E'), builder.addBelt(beltId, x0 + 1, 6, 0, 'E'), builder.addBelt(beltId, x0 + 2, 6, 0, 'E')];
    builder.link(lane[0], lane[1]);
    builder.link(lane[1], lane[2]);
    builder.label(lane[0], itemId);
    const bus = belts[indexOf.get(`${x0 + 1},4`)];
    builder.addBeltSorter(sorterId, { from: lane[1], to: bus });
    const rawRate = spec.rawRates?.get(itemId)
      ?? stages.reduce((sum, stage) => sum + (stage.inputRates?.get(itemId) ?? 0), 0);
    addEvent(itemId, indexOf.get(`${x0 + 1},4`), rawRate);
  }
  const headerEnd = 1 + 5 * raws.length;

  let cursor = headerEnd;
  let machines = 0;
  let lastPlacement = null;
  const halfOf = (id) => (COLLIDERS.get(id) ? COLLIDERS.get(id).ext[0] + Math.abs(COLLIDERS.get(id).pos[0]) : 1);
  for (const stage of stages) {
    const half = halfOf(stage.buildingId);
    for (let m = 0; m < stage.count; m += 1) {
      for (;;) {
        if (lastPlacement) {
          const need = Math.max(3, Math.ceil(lastPlacement.half + half));
          if (cursor < lastPlacement.index + need) cursor = lastPlacement.index + need;
        }
        while (cursor + 1 < path.length) {
          const cell = path[cursor];
          const next = path[cursor + 1];
          if (cell.dir !== 'N' && next.dir !== 'N' && next.y === cell.y && Math.abs(next.x - cell.x) === 1) break;
          cursor += 1;
        }
        if (cursor + 1 >= path.length) throw new Error(`总线长度不够放完所有机器（已放 ${machines}/${stages.reduce((sum, stage) => sum + stage.count, 0)}，宽度 ${width}，行数 ${rows}）`);
        const cell = path[cursor];
        const margin = Math.ceil(half + 0.6);
        if (cell.x < 1 + margin || cell.x > width - 1 - margin) {
          cursor += 1;
          continue;
        }
        if (stage.mode.mode === 'port') {
          const inX = cell.x + stage.mode.inCell;
          const outX = cell.x + stage.mode.outCell;
          const a = indexOf.get(`${inX},${cell.y}`);
          const b = indexOf.get(`${outX},${cell.y}`);
          if (inX < 1 || inX > width - 2 || outX < 1 || outX > width - 2 || a === undefined || b === undefined || path[a].dir === 'N' || path[b].dir === 'N') {
            cursor += 1;
            continue;
          }
        }
        break;
      }
      const cell = path[cursor];
      const mode = stage.mode;
      const machine = builder.add(stage.buildingId, cell.x, cell.y - mode.yOffset, 0, { recipeId: stage.recipeId, yaw: mode.yaw });
      let inputIndex;
      let outputIndex;
      // 混带：一个输入分拣器就够，机器只吃自己配方认的物品。
      if (mode.mode === 'port') {
        inputIndex = indexOf.get(`${cell.x + mode.inCell},${cell.y}`);
        outputIndex = indexOf.get(`${cell.x + mode.outCell},${cell.y}`);
        builder.addPortSorter(sorterId, { belt: belts[inputIndex], factory: machine, slot: mode.inputSlot, io: 'in' });
        builder.addPortSorter(sorterId, { belt: belts[outputIndex], factory: machine, slot: mode.outputSlot, io: 'out', itemIdFilter: stage.output });
      } else {
        const dx = cell.dir === 'E' ? 1 : -1;
        inputIndex = cursor;
        outputIndex = cursor + 1;
        const inBus = belts[inputIndex];
        const outBus = belts[outputIndex];
        if (!inBus || !outBus) throw new Error('总线缺少输入/输出格');
        builder.addSorter(sorterId, { belt: inBus, factory: machine, side: 'top', offset: 0, io: 'in' });
        builder.addSorter(sorterId, { belt: outBus, factory: machine, side: 'top', offset: dx, io: 'out', itemIdFilter: stage.output });
      }
      for (const itemId of stage.inputs) {
        addEvent(itemId, inputIndex, -(stage.inputRates.get(itemId) ?? 0) / stage.count);
      }
      for (const output of stage.outputs ?? [{ itemId: stage.output, rate: stage.outputRate ?? 0 }]) {
        addEvent(output.itemId, outputIndex, output.rate / stage.count);
      }
      machines += 1;
      lastPlacement = { index: cursor, half };
      cursor += 1;
    }
  }

  // 每条物品按位置排事件，累计净值就是该段带上在途的数量。
  const loads = new Float64Array(path.length + 1);
  for (const itemEvents of events.values()) {
    itemEvents.sort((a, b) => a.index - b.index);
    let current = 0;
    let previous = 0;
    for (const event of itemEvents) {
      for (let i = previous; i < event.index; i += 1) loads[i] += current;
      current += event.delta;
      previous = event.index;
    }
  }
  const capacity = BELT_CAPACITY_PER_MIN[beltId] ?? 1800;
  let worst = 0;
  for (const load of loads) worst = Math.max(worst, load);
  if (worst > capacity + 1e-6) {
    const error = new Error(`分配 ${ITEM_NAMES.get(target) ?? target} 时超过传送带最大容量（需要 ${Math.ceil(worst)}/min，单条 ${capacity}/min）`);
    error.code = 'bus-capacity';
    error.tiles = Math.ceil(worst / capacity);
    throw error;
  }

  return { text: builder.finish(), meta: { width, height, machines, rows, rawCount: raws.length } };
}
