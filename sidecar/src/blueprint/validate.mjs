// 蓝图硬校验：只解码蓝图本身，按游戏放置规则逐条检查。
// 出图后必须全过；它拦的是“贴进游戏报红/拒绝”的那几类：建筑碰撞、分拣器够不着、断带、越界。
import { fromStr } from '../../vendor/dsp-calc/src/blueprint/parser.js';
import vanilla from '../../vendor/dsp-calc/data/Vanilla.mjs';
import buildingData from '../../vendor/dsp-calc/data/Buildings.mjs';
import { COLLIDERS } from './colliders.mjs';

const ITEMS = new Map(vanilla.items.map((item) => [item.ID, item]));
const AREA = new Map(Object.values(buildingData.buildings).map((b) => [b.itemId, b.attributes?.area ?? [1, 1]]));
const EPS = 1e-6;
const LEVEL_M = 4 / 3; // 蓝图 z 差 1 层 ≈ 1.333 格高
const BELT_LINK_M = Math.sqrt(5.3); // 同一格里竖直相连允许的层差
const SORTER_IDS = new Set([2011, 2012, 2013, 2014]);
const BELT_IDS = new Set([2001, 2002, 2003]);
const GROUND_Z = 0.5; // z 小于它算地面：地面物件之间才查碰撞

const isBelt = (b) => BELT_IDS.has(b.itemId);
const isSorter = (b) => SORTER_IDS.has(b.itemId);
const itemName = (id) => ITEMS.get(id)?.Name ?? `未知物品_${id}`;

/**
 * 建筑在平面上的两种盒子：
 *   cell —— 占地格（area 向上取整、按左下角锚定）；带子和建筑之间用这一对，物流站的口位带子除外。
 *   phys —— 游戏 buildCollider（工厂之间、带子之间用这一对）。
 * 三种组合是拿编辑器里的游戏实测出来的，不要合并成一套：
 *   工厂(10,10) + 带子(11,10) → Collide；+ 带子(12,10) → Ok；
 *   工厂(30,10) + 工厂(31,10) / (32,10) → 都 Collide。
 */
function shapesOf(building) {
  const p = building.localOffset[0];
  if (isBelt(building)) {
    const beltCollider = COLLIDERS.get(building.itemId);
    return [
      { kind: 'belt-cell', x: p.x, y: p.y, hw: 0.5, hh: 0.5 },
      { kind: 'belt-phys', x: p.x, y: p.y, hw: beltCollider?.ext[0] ?? 0.28, hh: beltCollider?.ext[2] ?? 0.28 },
    ];
  }
  const collider = COLLIDERS.get(building.itemId);
  const area = AREA.get(building.itemId) ?? [1, 1];
  const n = Math.max(1, Math.ceil(area[0]));
  const m = Math.max(1, Math.ceil(area[1]));
  const shapes = [
    { kind: 'cell', x: p.x + (n - 1) / 2, y: p.y + (m - 1) / 2, hw: n / 2, hh: m / 2, station: !!collider?.station },
  ];
  if (collider) {
    const yaw = ((Math.round(building.yaw?.[0] ?? 0) % 360) + 360) % 360;
    const [cx, , cy] = collider.pos;
    const [hx, , hy] = collider.ext;
    const [ox, oy] = yaw === 90 ? [cy, -cx] : yaw === 180 ? [-cx, -cy] : yaw === 270 ? [-cy, cx] : [cx, cy];
    const [dx, dy] = yaw % 180 ? [hy, hx] : [hx, hy];
    shapes.push({ kind: 'phys', x: p.x + ox, y: p.y + oy, hw: dx, hh: dy });
  }
  return shapes;
}

function overlaps(a, b) {
  return Math.abs(a.x - b.x) < a.hw + b.hw - EPS && Math.abs(a.y - b.y) < a.hh + b.hh - EPS;
}

/** 两个盒子该不该参与碰撞：带子按占地格压建筑，带子/建筑各自的物理盒只在同类之间比。 */
function collides(a, b) {
  const beltCell = a.kind === 'belt-cell' ? a : b.kind === 'belt-cell' ? b : null;
  const cell = a.kind === 'cell' ? a : b.kind === 'cell' ? b : null;
  if (beltCell && cell) return !cell.station; // 物流站的 12 个口位本来就要插带子
  if (a.kind === 'belt-phys' && b.kind === 'belt-phys') return true;
  return a.kind === 'phys' && b.kind === 'phys';
}

function beltsAdjacent(from, to) {
  const a = from.localOffset[0];
  const b = to.localOffset[0];
  // 竖直升降的写法会给同一格的每节带子加 0.0016 级的错位，判定前先按 0.02 吸附。
  const snap = (v) => (Math.abs(v - Math.round(v)) < 0.05 ? Math.round(v) : v);
  const dx = snap(b.x - a.x);
  const dy = snap(b.y - a.y);
  const dz = snap(b.z - a.z);
  const flat = Math.abs(dx) + Math.abs(dy);
  if (flat === 1) return true; // 平一格：同层或坡道，坡度单独由游戏判定
  if (flat !== 0) return false;
  return Math.abs(dz) * LEVEL_M <= BELT_LINK_M + EPS;
}

/**
 * 校验一张已解码的蓝图。
 * @param {object} bp fromStr() 的结果
 * @param {{maxWidth?: number, maxHeight?: number}} [options]
 * @returns {{ok: boolean, errors: string[], warnings: string[], stats: object}}
 */
export function validateParsed(bp, options = {}) {
  const maxWidth = options.maxWidth ?? 1000;
  const maxHeight = options.maxHeight ?? 50;
  const errors = [];
  const warnings = [];
  const buildings = bp.buildings ?? [];
  const area = bp.areas?.[0];
  const width = area?.size?.x ?? 0;
  const height = area?.size?.y ?? 0;

  if (!area) errors.push('蓝图没有区域');
  if (width <= 0 || height <= 0) errors.push(`蓝图区域尺寸无效：${width}x${height}`);
  if (width > maxWidth) errors.push(`蓝图宽度 ${width} 超过上限 ${maxWidth}`);
  if (height > maxHeight) errors.push(`蓝图高度 ${height} 超过上限 ${maxHeight}（离赤道会跨回归线，贴不上）`);

  const byItem = new Map();
  for (const building of buildings) {
    byItem.set(building.itemId, (byItem.get(building.itemId) ?? 0) + 1);
    if (!ITEMS.has(building.itemId)) {
      errors.push(`建筑 #${building.index} 物品 ${building.itemId} 不在游戏数据里`);
      continue;
    }
    for (const key of ['outputObjIdx', 'inputObjIdx']) {
      const target = building[key] ?? -1;
      if (target < -1 || target >= buildings.length) {
        errors.push(`建筑 #${building.index}（${itemName(building.itemId)}）的 ${key}=${target} 越界（共 ${buildings.length} 台）`);
      }
    }
    const p = building.localOffset?.[0];
    if (!p || !Number.isFinite(p.x) || !Number.isFinite(p.y) || !Number.isFinite(p.z)) {
      errors.push(`建筑 #${building.index} 坐标无效`);
      continue;
    }
    if (p.x < -0.5 || p.x > width + 0.5 || p.y < -0.5 || p.y > height + 0.5) {
      errors.push(`建筑 #${building.index}（${itemName(building.itemId)}）在区域外 (${p.x},${p.y})`);
    }
  }

  // 分拣器：长度 1..3，参数与几何一致。
  for (const building of buildings) {
    if (!isSorter(building)) continue;
    const a = building.localOffset[0];
    const b = building.localOffset[1];
    const length = Math.floor(Math.abs(b.x - a.x) + Math.abs(b.y - a.y) + EPS);
    if (length < 1 || length > 3) {
      errors.push(`分拣器 #${building.index} 长度 ${length}，超出游戏上限 3 格`);
    }
    const declared = building.parameters?.length;
    if (declared !== length) {
      errors.push(`分拣器 #${building.index} 长度 ${length} 与参数 ${declared ?? '空'} 不一致`);
    }
    if (Math.abs(a.z - b.z) > EPS) errors.push(`分拣器 #${building.index} 两端高度不同（${a.z} / ${b.z}）`);
  }

  // 传送带：连接必须逐格相邻，不能跳格或连到非传送带。
  for (const building of buildings) {
    if (!isBelt(building)) continue;
    for (const key of ['outputObjIdx', 'inputObjIdx']) {
      const target = building[key];
      if (typeof target !== 'number' || target < 0) continue;
      const other = buildings[target];
      // 带子可以直接接进物流站、分流器、监测器这类建筑（靠槽位），只有接到带子时才需要逐格相邻。
      if (other && isBelt(other) && !beltsAdjacent(building, other)) {
        const a = building.localOffset[0];
        const b = other.localOffset[0];
        errors.push(`传送带 #${building.index}→#${target} 不相邻（${a.x},${a.y},${a.z} → ${b.x},${b.y},${b.z}）`);
      }
    }
  }

  // 地面碰撞：地面物件两两不相交；分拣器不算障碍（它本来就架在带子和工厂之间）。
  const shapes = [];
  for (const building of buildings) {
    if (isSorter(building)) continue;
    if (building.localOffset[0].z >= GROUND_Z) continue;
    if (!isBelt(building) && !COLLIDERS.has(building.itemId)) {
      warnings.push(`建筑 ${building.itemId} 没有碰撞体数据，跳过碰撞检查`);
      continue;
    }
    for (const shape of shapesOf(building)) shapes.push({ shape, building });
  }

  const bucket = 6;
  const grid = new Map();
  const reported = new Set();
  const report = (a, b) => {
    const [lo, hi] = a.index < b.index ? [a, b] : [b, a];
    const pair = `${lo.index},${hi.index}`;
    if (reported.has(pair)) return;
    reported.add(pair);
    errors.push(
      `建筑碰撞：#${lo.index}（${itemName(lo.itemId)}，z=${lo.localOffset[0].z}）与 #${hi.index}（${itemName(hi.itemId)}，z=${hi.localOffset[0].z}）`,
    );
  };
  for (let i = 0; i < shapes.length; i += 1) {
    const { shape, building } = shapes[i];
    const gx = Math.floor(shape.x / bucket);
    const gy = Math.floor(shape.y / bucket);
    for (let dx = -1; dx <= 1; dx += 1) {
      for (let dy = -1; dy <= 1; dy += 1) {
        const list = grid.get(`${gx + dx},${gy + dy}`);
        if (!list) continue;
        for (const j of list) {
          const other = shapes[j];
          if (other.building === building) continue;
          if (collides(shape, other.shape) && overlaps(shape, other.shape)) report(building, other.building);
        }
      }
    }
    const key = `${gx},${gy}`;
    const list = grid.get(key);
    if (list) list.push(i);
    else grid.set(key, [i]);
  }

  return {
    ok: errors.length === 0,
    errors,
    warnings,
    stats: {
      width,
      height,
      buildings: buildings.length,
      byItem: Object.fromEntries([...byItem].sort((a, b) => b[1] - a[1])),
    },
  };
}

/** 校验蓝图字符串；解析失败也算不通过。 */
export function validateBlueprint(text, options = {}) {
  let parsed;
  try {
    parsed = fromStr(text);
  } catch (error) {
    return { ok: false, errors: [`蓝图解析失败：${error.message}`], warnings: [], stats: null };
  }
  return validateParsed(parsed, options);
}
