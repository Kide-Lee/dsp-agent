// 工厂布局：每个配方阶段一个独立模块，模块之间用物流站供货/收货，整张图只出一张蓝图。
// 模块内部仍然用带子：上方进料站 → 进料带 → 机器，机器 → 出料带 → 出料站。
import { BlueprintBuilder } from './emit.mjs';
import { COLLIDERS } from './colliders.mjs';

const STATION_ID = 2103;
const INPUT_PORTS = [6, 7, 8];
const OUTPUT_PORT = 4;

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

const reachX = (itemId) => {
  const collider = COLLIDERS.get(itemId);
  return collider ? collider.ext[0] + Math.abs(collider.pos[0]) : 1;
};

const storageOf = (items, role) => Array.from({ length: 4 }, (_, i) => ({
  itemId: items[i] ?? 0,
  localRole: items[i] ? role : 0,
  remoteRole: 0,
  max: items[i] ? 5000 : 0,
  lockAmount: 0,
}));

function stationParameters(storageItems, role, portDirs) {
  const slots = Array.from({ length: 12 }, (_, i) => {
    const port = portDirs.get(i);
    return port ? { dir: port.dir, storageIdx: port.storageIdx } : { dir: 0, storageIdx: 0 };
  });
  return { storage: storageOf(storageItems, role), slots, ...STATION_PARAM_DEFAULTS };
}

/** 一个阶段的模块：返回模块宽度和顶部 Y。 */
function placeModule(builder, stage, x0, yTop, ids) {
  const outputs = stage.outputs ?? [{ itemId: stage.output }];
  const inputs = stage.inputs ?? [];
  const stations = [];

  // 机器从左到右排开，间距按碰撞盒算。
  let x = x0 + 3;
  const last = { x, half: reachX(stage.buildingId) };
  const machines = [];
  for (let i = 0; i < stage.count; i += 1) {
    if (i > 0) {
      const need = Math.max(3, Math.ceil(last.half + reachX(stage.buildingId)));
      x = last.x + need;
      last.x = x;
      last.half = reachX(stage.buildingId);
    }
    const machine = builder.add(stage.buildingId, x, yTop - 2, 0, { yaw: 0, recipeId: stage.recipeId ?? 0 });
    machines.push(machine);
  }
  const xEnd = x + 2;
  const yIn = yTop;
  const yOut = yTop - 4;

  // 进料站：站放在模块上方，三个口用竖带接到进料带。
  const inCx = Math.round((x0 + xEnd) / 2);
  const inStation = builder.add(STATION_ID, inCx, yIn + 3, 0, {
    yaw: 0,
    parameters: stationParameters(inputs, 2, new Map(INPUT_PORTS.slice(0, inputs.length).map((port, i) => [port, { dir: 1, storageIdx: i + 1 }]))),
  });
  stations.push({ station: inStation, role: 'input', items: inputs });

  const inBelt = [];
  for (let bx = x0 + 1; bx <= xEnd; bx += 1) inBelt.push(builder.addBelt(ids.beltId, bx, yIn, 0, 'E'));
  for (let i = 0; i + 1 < inBelt.length; i += 1) builder.link(inBelt[i], inBelt[i + 1]);
  const inIndexOf = new Map(inBelt.map((belt) => [belt.localOffset[0].x, belt]));
  for (let i = 0; i < inputs.length; i += 1) {
    const port = INPUT_PORTS[i];
    const lane = builder.addBelt(ids.beltId, inCx + (port - 7), yIn + 1, 0, 'S');
    lane.inputObjIdx = inStation.index;
    lane.inputFromSlot = port;
    builder.addBeltSorter(ids.sorterId, { from: lane, to: inIndexOf.get(lane.localOffset[0].x) });
  }
  for (const machine of machines) {
    builder.addSorter(ids.sorterId, { belt: inIndexOf.get(machine.localOffset[0].x), factory: machine, side: 'top', offset: 0, io: 'in' });
  }

  // 出料带：机器下方一排带子往东，末端接进一个出料站。
  const outBelt = [];
  for (let bx = x0 + 1; bx <= xEnd; bx += 1) outBelt.push(builder.addBelt(ids.beltId, bx, yOut, 0, 'E'));
  for (let i = 0; i + 1 < outBelt.length; i += 1) builder.link(outBelt[i], outBelt[i + 1]);
  const outIndexOf = new Map(outBelt.map((belt) => [belt.localOffset[0].x, belt]));
  for (const machine of machines) {
    builder.addSorter(ids.sorterId, { belt: outIndexOf.get(machine.localOffset[0].x), factory: machine, side: 'bottom', offset: 0, io: 'out', itemIdFilter: outputs[0]?.itemId ?? 0 });
  }
  const outStation = builder.add(STATION_ID, xEnd + 3, yOut, 0, {
    yaw: 0,
    parameters: stationParameters(outputs.map((output) => output.itemId), 1, new Map([[OUTPUT_PORT, { dir: 2, storageIdx: 1 }]])),
  });
  const portBelt = builder.addBelt(ids.beltId, xEnd + 1, yOut, 0, 'E');
  builder.link(outBelt.at(-1), portBelt);
  portBelt.outputObjIdx = outStation.index;
  portBelt.outputToSlot = OUTPUT_PORT;
  stations.push({ station: outStation, role: 'output', items: outputs.map((output) => output.itemId) });

  return { width: xEnd - x0 + 7, machines, stations };
}

export function layoutFactory(spec) {
  const { stages, raws = [], title = '工厂' } = spec;
  const ids = { beltId: spec.beltId ?? 2003, sorterId: spec.sorterId ?? 2013 };
  const maxRowWidth = 200;

  const builder = new BlueprintBuilder({ width: 1, height: 1, title, gameVersion: spec.gameVersion });
  let cursorX = 2;
  let cursorY = 12;
  let rowHeight = 0;
  const placed = [];
  for (const stage of stages) {
    const estimate = stage.count * Math.max(3, Math.ceil(reachX(stage.buildingId) * 2)) + 12;
    if (cursorX > 2 && cursorX + estimate > maxRowWidth) {
      cursorX = 2;
      cursorY -= rowHeight;
      rowHeight = 0;
    }
    const placedModule = placeModule(builder, stage, cursorX, cursorY, ids);
    placed.push(placedModule);
    cursorX += placedModule.width + 2;
    rowHeight = Math.max(rowHeight, 17);
  }

  const minX = Math.min(...builder.buildings.map((b) => b.localOffset[0].x));
  const maxX = Math.max(...builder.buildings.map((b) => b.localOffset[0].x));
  const minY = Math.min(...builder.buildings.map((b) => b.localOffset[0].y));
  const maxY = Math.max(...builder.buildings.map((b) => b.localOffset[0].y));
  const width = Math.ceil(maxX - minX + 8);
  const height = Math.ceil(maxY - minY + 8);
  const dx = 1 - minX;
  const dy = 4 - minY;
  for (const building of builder.buildings) {
    for (const point of building.localOffset) {
      point.x += dx;
      point.y += dy;
    }
  }
  builder.width = width;
  builder.height = height;
  const text = builder.finish();

  return {
    text,
    meta: {
      layout: 'factory',
      width,
      height,
      modules: placed.length,
      machines: placed.reduce((sum, module) => sum + module.machines.length, 0),
      stations: placed.reduce((sum, module) => sum + module.stations.length, 0),
      raw: raws,
    },
  };
}
