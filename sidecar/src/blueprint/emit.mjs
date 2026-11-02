// 我们自己的蓝图出图器：只管“往蓝图里放建筑、连接”，不掺布局策略。
// 建筑模板来自 dsp-calc 的 Buildings 数据（Mulan PSL v2，只取模型编号/槽位/默认参数），
// 编码走同一份 vendored parser（蓝图格式本身）。
import buildingData from '../../vendor/dsp-calc/data/Buildings.mjs';
import { toStr } from '../../vendor/dsp-calc/src/blueprint/parser.js';
import { PORT_OFFSETS, rotateOffset } from './ports.mjs';

const TEMPLATES = new Map(Object.values(buildingData.buildings).map((b) => [b.itemId, b]));
const HEADING = { E: 90, W: 270, N: 0, S: 180 };

// 分拣器槽位（yaw 0 的建筑）：下侧从左到右 8/7/6，上侧 0/1/2 对应 -1/0/+1。
const SIDE_SLOT = { bottom: { '-1': 8, 0: 7, 1: 6 }, top: { '-1': 0, 0: 1, 1: 2 } };

const isFinite = (v) => typeof v === 'number' && Number.isFinite(v);

/** 搭蓝图：建筑按放入顺序编号，索引就是 outputObjIdx/inputObjIdx 引用的值。 */
export class BlueprintBuilder {
  constructor({ width, height, title = '手搓产线', gameVersion = '0.10.30.3100', desc = '', iconId = 0 }) {
    this.width = width;
    this.height = height;
    this.title = title;
    this.gameVersion = gameVersion;
    this.desc = desc;
    this.iconId = iconId;
    this.buildings = [];
  }

  /** 克隆模板并放入；index 与数组下标一致。 */
  add(itemId, x, y, z = 0, options = {}) {
    const template = TEMPLATES.get(itemId);
    if (!template) throw new Error(`出图器没有建筑模板 ${itemId}`);
    if (!isFinite(x) || !isFinite(y) || !isFinite(z)) throw new Error(`建筑坐标无效 ${itemId} (${x},${y},${z})`);
    const building = structuredClone(template);
    delete building.itemName;
    delete building.attributes;
    building.index = this.buildings.length;
    building.localOffset = [
      { x, y, z },
      { x, y, z },
    ];
    const yaw = options.yaw ?? 0;
    building.yaw = [yaw, yaw];
    for (const key of Object.keys(options)) {
      if (key === 'yaw') continue;
      building[key] = options[key];
    }
    this.buildings.push(building);
    return building;
  }

  addBelt(itemId, x, y, z = 0, heading = 'E') {
    return this.add(itemId, x, y, z, { yaw: HEADING[heading] ?? 0, parameters: null });
  }

  /** 传送带首尾相接：from → to。 */
  link(from, to) {
    if (!from || !to) throw new Error('link 需要两条传送带');
    from.outputObjIdx = to.index;
    from.outputToSlot = 1;
    return from;
  }

  /**
   * 分拣器：工厂某个侧面槽位 ↔ 传送带一格。
   * side: 'bottom' | 'top'；offset: -1 | 0 | 1（槽位在工厂中心的横向偏移）。
   * 机器一侧的端点是工厂边缘格（中心 ±1），另一端是带子那一格，length 就是两端曼哈顿距离。
   */
  addSorter(itemId, { belt, factory, side = 'bottom', offset = 0, io, itemIdFilter = 0, sorterId }) {
    const slot = SIDE_SLOT[side]?.[offset];
    if (slot === undefined) throw new Error(`分拣器槽位无效 ${side}/${offset}`);
    const fx = factory.localOffset[0].x;
    const fy = factory.localOffset[0].y;
    const edgeX = fx + offset;
    const edgeY = fy + (side === 'bottom' ? -1 : 1);
    const bx = belt.localOffset[0].x;
    const by = belt.localOffset[0].y;
    if (bx !== edgeX) throw new Error(`分拣器两端不在同一条轴线上：带子 x=${bx}，槽位 x=${edgeX}`);
    const length = Math.abs(by - edgeY);
    if (length < 1 || length > 3) throw new Error(`分拣器长度 ${length} 超出 1..3（${side}/${offset}）`);

    const sorter = this.add(sorterId ?? itemId, belt.localOffset[0].x, Math.min(by, edgeY), 0, {
      parameters: { length },
      outputObjIdx: -1,
      inputObjIdx: -1,
    });
    const beltPoint = { x: bx, y: by, z: belt.localOffset[0].z };
    const edgePoint = { x: edgeX, y: edgeY, z: 0 };
    if (io === 'in') {
      sorter.localOffset = [beltPoint, edgePoint];
      sorter.inputObjIdx = belt.index;
      sorter.inputFromSlot = -1;
      sorter.outputObjIdx = factory.index;
      sorter.outputToSlot = slot;
      sorter.filterId = 0;
    } else {
      sorter.localOffset = [edgePoint, beltPoint];
      sorter.inputObjIdx = factory.index;
      sorter.inputFromSlot = slot;
      sorter.outputObjIdx = belt.index;
      sorter.outputToSlot = -1;
      sorter.filterId = itemIdFilter;
    }
    // 朝向按两端高低定：终点在上 → 0，否则 180。
    const up = sorter.localOffset[1].y > sorter.localOffset[0].y;
    sorter.yaw = up ? [0, 0] : [180, 180];
    return sorter;
  }

  /**
   * 带小数端口的建筑分拣器（精炼厂这类槽位不在整数格上的机器）。
   * 机器一侧的端点是端口真实位置，带子一侧的端点沿用端口的横向坐标、取带子所在的行。
   */
  addPortSorter(itemId, { belt, factory, slot, io, itemIdFilter = 0, sorterId }) {
    const offsets = PORT_OFFSETS.get(factory.itemId);
    const local = offsets?.get(slot);
    if (!local) throw new Error(`建筑 ${factory.itemId} 没有 ${slot} 号端口的偏移数据`);
    const yaw = factory.yaw?.[0] ?? 0;
    const { dx, dy } = rotateOffset(local.dx, local.dy, yaw);
    const portX = factory.localOffset[0].x + dx;
    const portY = factory.localOffset[0].y + dy;
    const beltX = belt.localOffset[0].x;
    const beltY = belt.localOffset[0].y;
    if (Math.abs(beltX - portX) > 0.5) throw new Error(`端口与带子错开 ${Math.abs(beltX - portX).toFixed(2)} 格`);
    const length = Math.floor(Math.abs(beltY - portY) + Math.abs(beltX - portX) + 1e-6);
    if (length < 1 || length > 3) throw new Error(`端口分拣器长度 ${length} 超出 1..3`);
    const sorter = this.add(sorterId ?? itemId, portX, Math.min(portY, beltY), 0, {
      parameters: { length },
      outputObjIdx: -1,
      inputObjIdx: -1,
    });
    const portPoint = { x: portX, y: portY, z: 0 };
    const beltPoint = { x: portX, y: beltY, z: belt.localOffset[0].z };
    if (io === 'in') {
      sorter.localOffset = [beltPoint, portPoint];
      sorter.inputObjIdx = belt.index;
      sorter.inputFromSlot = -1;
      sorter.outputObjIdx = factory.index;
      sorter.outputToSlot = slot;
      sorter.filterId = 0;
    } else {
      sorter.localOffset = [portPoint, beltPoint];
      sorter.inputObjIdx = factory.index;
      sorter.inputFromSlot = slot;
      sorter.outputObjIdx = belt.index;
      sorter.outputToSlot = -1;
      sorter.filterId = itemIdFilter;
    }
    const up = sorter.localOffset[1].y > sorter.localOffset[0].y;
    sorter.yaw = up ? [0, 0] : [180, 180];
    return sorter;
  }

  /** 带子 → 带子的分拣器（原料带把物品放到总线上）。 */
  addBeltSorter(itemId, { from, to, sorterId }) {
    const a = from.localOffset[0];
    const b = to.localOffset[0];
    if (a.x !== b.x) throw new Error('带子之间的分拣器两端不在同一列');
    const length = Math.abs(b.y - a.y);
    if (length < 1 || length > 3) throw new Error(`带间分拣器长度 ${length} 超出 1..3`);
    const sorter = this.add(sorterId ?? itemId, a.x, Math.min(a.y, b.y), 0, {
      parameters: { length },
      outputObjIdx: -1,
      inputObjIdx: -1,
    });
    sorter.localOffset = [
      { x: a.x, y: a.y, z: a.z },
      { x: b.x, y: b.y, z: b.z },
    ];
    sorter.inputObjIdx = from.index;
    sorter.inputFromSlot = -1;
    sorter.outputObjIdx = to.index;
    sorter.outputToSlot = -1;
    sorter.outputFromSlot = 0;
    sorter.inputToSlot = 1;
    sorter.filterId = 0;
    const up = sorter.localOffset[1].y > sorter.localOffset[0].y;
    sorter.yaw = up ? [0, 0] : [180, 180];
    return sorter;
  }

  /** 给一条带子的头一节贴物品标签（游戏里显示“这是什么”）。 */
  label(belt, itemId, count = 0) {
    belt.parameters = { iconId: itemId, count };
    return belt;
  }

  finish() {
    const size = { x: this.width, y: this.height };
    return toStr({
      header: {
        layout: 10,
        icons: [this.iconId, 0, 0, 0, 0],
        time: new Date(),
        gameVersion: this.gameVersion,
        shortDesc: this.title,
        desc: this.desc,
      },
      version: 1,
      cursorOffset: { x: Math.floor(size.x / 2), y: Math.floor(size.y / 2) },
      cursorTargetArea: 0,
      dragBoxSize: size,
      primaryAreaIdx: 0,
      areas: [{ index: 0, parentIndex: -1, tropicAnchor: 0, areaSegments: 200, anchorLocalOffset: { x: 0, y: 0 }, size }],
      buildings: this.buildings,
    });
  }
}
