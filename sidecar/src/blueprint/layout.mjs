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
// 物流站 12 个端口在半径 2 的环上（来自游戏保存的真实站蓝图，见 docs/plans）。
const STATION_PORT_RING = [
  [1, 2], [0, 2], [-1, 2],
  [-2, 1], [-2, 0], [-2, -1],
  [-1, -2], [0, -2], [1, -2],
  [2, -1], [2, 0], [2, 1],
];
const STATION_PARAM_DEFAULTS = {
  workEnergyPerTick: 12,
  tripRangeOfDrones: 180,
  tripRangeOfShips: 10000,
  includeOrbitCollector: true,
  warpEnableDistance: 12,
  warperNecessary: true,
  deliveryAmountOfDrones: 10,
  deliveryAmountOfShips: 100,
  pilerCount: 0,
  miningSpeed: 0,
  droneAutoReplenish: false,
  shipAutoReplenish: false,
};

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
  const stationSupply = spec.supply === 'station';
  let rowGap = Math.max(8, Math.ceil(Math.max(...stages.map((stage) => stage.mode.yOffset + stage.mode.halfZ + 0.5))));
  if (stationSupply) rowGap = Math.max(rowGap, 10);
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
  let height = 4 + rowGap * (rows - 1) + 4;
  if (stationSupply) height = Math.max(height, 15); // 行首物流站顶到 y≈12.8，区域要盖住它。

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

  const events = new Map();
  const addEvent = (itemId, index, delta) => {
    if (!events.has(itemId)) events.set(itemId, []);
    events.get(itemId).push({ index, delta });
  };
  const rawRateOf = (itemId) => spec.rawRates?.get(itemId)
    ?? stages.reduce((sum, stage) => sum + (stage.inputRates?.get(itemId) ?? 0), 0);
  let headerEnd;
  if (stationSupply) {
    // 物流站供料：站放在行首上方，下侧三个口的带子各拉一条竖带，再用带间分拣器插进总线。
    if (raws.length > 3) {
      const error = new Error('物流站供料最多支持 3 种原料');
      error.code = 'bus-unsupported';
      throw error;
    }
    const busY = 4;
    const sx = 6;
    const sy = 10;
    const storage = Array.from({ length: 4 }, (_, i) => ({
      itemId: raws[i] ?? 0,
      localRole: raws[i] ? 1 : 0,
      remoteRole: raws[i] ? 2 : 0,
      max: raws[i] ? 1000 : 0,
      lockAmount: 0,
    }));
    const slots = STATION_PORT_RING.map((_, i) => {
      const index = i - 6;
      return index >= 0 && index < raws.length ? { dir: 1, storageIdx: index + 1 } : { dir: 0, storageIdx: 0 };
    });
    const station = builder.add(2103, sx, sy, 0, { yaw: 0, parameters: { storage, slots, ...STATION_PARAM_DEFAULTS } });
    for (const [i, itemId] of raws.entries()) {
      const port = 6 + i;
      const px = sx + STATION_PORT_RING[port][0];
      const py = sy + STATION_PORT_RING[port][1];
      const lane = [builder.addBelt(beltId, px, py, 0, 'S')];
      for (let y = py - 1; y >= busY + 1; y -= 1) lane.push(builder.addBelt(beltId, px, y, 0, 'S'));
      lane[0].inputObjIdx = station.index;
      lane[0].inputFromSlot = port;
      for (let k = 0; k + 1 < lane.length; k += 1) builder.link(lane[k], lane[k + 1]);
      const bus = belts[indexOf.get(`${px},${busY}`)];
      builder.addBeltSorter(sorterId, { from: lane.at(-1), to: bus });
      addEvent(itemId, indexOf.get(`${px},${busY}`), rawRateOf(itemId));
    }
    headerEnd = sx + 6;
  } else {
    // 原料：行首上方各拉一条短带，再用带间分拣器放到总线上。
    for (const [i, itemId] of raws.entries()) {
      const x0 = 1 + 5 * i;
      const lane = [builder.addBelt(beltId, x0, 6, 0, 'E'), builder.addBelt(beltId, x0 + 1, 6, 0, 'E'), builder.addBelt(beltId, x0 + 2, 6, 0, 'E')];
      builder.link(lane[0], lane[1]);
      builder.link(lane[1], lane[2]);
      builder.label(lane[0], itemId);
      const bus = belts[indexOf.get(`${x0 + 1},4`)];
      builder.addBeltSorter(sorterId, { from: lane[1], to: bus });
      addEvent(itemId, indexOf.get(`${x0 + 1},4`), rawRateOf(itemId));
    }
    headerEnd = 1 + 5 * raws.length;
  }

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
        if (cursor + 1 >= path.length) {
          const total = stages.reduce((sum, stage) => sum + stage.count, 0);
          const error = new Error(`总线长度不够放完所有机器（已放 ${machines}/${total}，宽度 ${width}，行数 ${rows}）`);
          // 排不下等价于“这张图放不下”，交给上层按张数拆，别再回退旧布局。
          error.code = 'bus-capacity';
          error.tiles = Math.max(2, Math.ceil(total / Math.max(1, machines)));
          throw error;
        }
        const cell = path[cursor];
        const margin = Math.ceil(half + 0.6);
        if (cell.x < Math.max(1 + margin, headerEnd - 1) || cell.x > width - 1 - margin) {
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
