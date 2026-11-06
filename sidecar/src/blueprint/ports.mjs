// 建筑端口相对“蓝图坐标里的建筑中心”的偏移（小数）。
// 游戏 slotPoses 与蓝图坐标之间差一个模型原点变换，这里用真实蓝图反解出来的值：
// 2308 精炼厂的 6/7/8 号槽位偏移在 88 个样本里完全一致。
export const PORT_OFFSETS = new Map([
  [
    2308,
    new Map([
      [6, { dx: -0.782, dy: -2.866 }],
      [7, { dx: 0, dy: -2.862 }],
      [8, { dx: 0.782, dy: -2.866 }],
    ]),
  ],
  // 位面熔炉：上方 0/1/2，下方 4/5/6（离中心更近）。
  [
    2309,
    new Map([
      [0, { dx: -1, dy: 2 }],
      [1, { dx: 0, dy: 2 }],
      [2, { dx: 1, dy: 2 }],
      [4, { dx: 1, dy: -0.716 }],
      [5, { dx: 0, dy: -0.716 }],
      [6, { dx: -1, dy: -0.716 }],
    ]),
  ],
  // 化工厂：只有偏在一侧的端口。
  [
    2310,
    new Map([
      [0, { dx: -0.803, dy: 1.828 }],
      [1, { dx: -1.605, dy: 1.827 }],
      [7, { dx: -1.6, dy: -1.83 }],
      [8, { dx: -0.8, dy: -1.829 }],
    ]),
  ],
  // 量子化工厂：上方一排槽位（z=2.1，蓝图里落在 2 格），下方一排（z=-0.9）。
  [
    2317,
    new Map([
      [0, { dx: -1, dy: 2 }],
      [1, { dx: 0, dy: 2 }],
      [2, { dx: 1, dy: 2 }],
      [7, { dx: 2, dy: 2 }],
      [3, { dx: 2, dy: -0.9 }],
      [4, { dx: 1, dy: -0.9 }],
      [5, { dx: 0, dy: -0.9 }],
      [6, { dx: -1, dy: -0.9 }],
    ]),
  ],
  // 自演化研究站：上方三格（z=2.3），下方三格。
  [
    2902,
    new Map([
      [0, { dx: 1, dy: 2 }],
      [1, { dx: 0, dy: 2 }],
      [2, { dx: -1, dy: 2 }],
      [6, { dx: -1, dy: -2 }],
      [7, { dx: 0, dy: -2 }],
      [8, { dx: 1, dy: -2 }],
    ]),
  ],
  // 研究所：上下各一侧。
  [
    2901,
    new Map([
      [0, { dx: 0.803, dy: 1.828 }],
      [2, { dx: -0.803, dy: 1.828 }],
      [7, { dx: 0, dy: -1.829 }],
    ]),
  ],
]);

/** 按建筑 yaw 旋转本地偏移（yaw 0/90/180/270）。 */
export function rotateOffset(dx, dy, yaw) {
  const deg = ((Math.round(yaw ?? 0) % 360) + 360) % 360;
  if (deg === 90) return { dx: dy, dy: -dx };
  if (deg === 180) return { dx: -dx, dy: -dy };
  if (deg === 270) return { dx: -dy, dy: dx };
  return { dx, dy };
}

// 物流站 12 个进料/出料口：本地端口位置来自游戏 prefabDesc.portPoses，
// 带子只能落在相邻的整数格（把端口位置四舍五入），朝向是“从带子看向站”的反方向。
export const STATION_PORT_RING = [
  [1, 2], [0, 2], [-1, 2],
  [-2, 1], [-2, 0], [-2, -1],
  [-1, -2], [0, -2], [1, -2],
  [2, -1], [2, 0], [2, 1],
];
export const STATION_PORT_CELLS = new Map([
  [2103, STATION_PORT_RING.map(([x, y]) => ({ x, y }))],
  [2104, STATION_PORT_RING.map(([x, y]) => ({ x, y }))],
]);

/** 带子接站时应该放的格子（相对站中心，yaw 0）。 */
export function stationPortCell(itemId, port) {
  const cells = STATION_PORT_CELLS.get(itemId);
  if (!cells) return null;
  const cell = cells[port];
  if (!cell) return null;
  return { x: cell.x, y: cell.y };
}
