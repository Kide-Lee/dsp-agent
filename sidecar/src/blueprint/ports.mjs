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
]);

/** 按建筑 yaw 旋转本地偏移（yaw 0/90/180/270）。 */
export function rotateOffset(dx, dy, yaw) {
  const deg = ((Math.round(yaw ?? 0) % 360) + 360) % 360;
  if (deg === 90) return { dx: dy, dy: -dx };
  if (deg === 180) return { dx: -dx, dy: -dy };
  if (deg === 270) return { dx: -dy, dy: dx };
  return { dx, dy };
}
