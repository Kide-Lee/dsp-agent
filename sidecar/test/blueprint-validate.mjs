#!/usr/bin/env node
/**
 * 蓝图硬校验器测试（离线）。
 *
 * 覆盖：合法蓝图通过；分拣器超长、建筑碰撞、带子跳格、区域超高、未知物品被拦住。
 * 校验器只解码蓝图本身，不依赖生成器。
 */
import { toStr } from '../vendor/dsp-calc/src/blueprint/parser.js';
import { validateBlueprint } from '../src/blueprint/validate.mjs';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const base = (index, itemId, x, y, extra = {}) => ({
  index,
  itemId,
  modelIndex: 0,
  areaIndex: 0,
  localOffset: [{ x, y, z: 0 }, { x, y, z: 0 }],
  yaw: [0, 0],
  tilt: 0,
  pitch: 0,
  tilt2: 0,
  pitch2: 0,
  outputObjIdx: -1,
  inputObjIdx: -1,
  outputToSlot: 0,
  inputFromSlot: 0,
  outputFromSlot: 0,
  inputToSlot: 0,
  outputOffset: 0,
  inputOffset: 0,
  recipeId: 0,
  filterId: 0,
  parameters: null,
  ...extra,
});

function makeText(buildings, size = { x: 40, y: 40 }) {
  return toStr({
    header: { layout: 10, icons: [0, 0, 0, 0, 0], time: new Date('2026-01-01T00:00:00Z'), gameVersion: '0.10.30.3100', shortDesc: '校验测试', desc: '' },
    version: 1,
    cursorOffset: { x: 0, y: 0 },
    cursorTargetArea: 0,
    dragBoxSize: size,
    primaryAreaIdx: 0,
    areas: [{ index: 0, parentIndex: -1, tropicAnchor: 0, areaSegments: 200, anchorLocalOffset: { x: 0, y: 0 }, size }],
    buildings,
  });
}

function sorter(index, x0, y0, x1, y1, length, extra = {}) {
  const b = base(index, 2013, x0, y0, { parameters: { length }, ...extra });
  b.localOffset[1] = { x: x1, y: y1, z: 0 };
  return b;
}

const errorsOf = (result) => result.errors.join(' | ');

// 1. 合法：一台制造台 + 一根 1 格分拣器 + 两节相邻带子。
{
  const belts = [base(0, 2003, 10, 8), base(1, 2003, 10, 9)];
  belts[0].outputObjIdx = 1;
  belts[0].outputToSlot = 1;
  const factory = base(2, 2303, 10, 12);
  const s = sorter(3, 10, 10, 10, 12, 2, { outputObjIdx: 2, inputObjIdx: 1, outputToSlot: 1 });
  const result = validateBlueprint(makeText([...belts, factory, s]));
  check('合法蓝图通过', result.ok, errorsOf(result));
}

// 2. 分拣器超过 3 格。
{
  const factory = base(0, 2303, 10, 14);
  const s = sorter(1, 10, 9, 10, 14, 5, { outputObjIdx: 0 });
  const result = validateBlueprint(makeText([factory, s]));
  check('分拣器超长被拦住', !result.ok && result.errors.some((e) => e.includes('超出游戏上限 3 格')), errorsOf(result));
}

// 3. 两台制造台叠在一起。
{
  const a = base(0, 2303, 10, 10);
  const b = base(1, 2303, 10, 11);
  const result = validateBlueprint(makeText([a, b]));
  check('建筑碰撞被拦住', !result.ok && result.errors.some((e) => e.includes('建筑碰撞')), errorsOf(result));
}

// 4. 带子跳格。
{
  const a = base(0, 2003, 5, 5, { outputObjIdx: 1, outputToSlot: 1 });
  const b = base(1, 2003, 9, 9);
  const result = validateBlueprint(makeText([a, b]));
  check('带子跳格被拦住', !result.ok && result.errors.some((e) => e.includes('不相邻')), errorsOf(result));
}

// 5. 区域高于 50 格（离赤道会跨回归线）。
{
  const result = validateBlueprint(makeText([base(0, 2303, 5, 5)], { x: 30, y: 60 }));
  check('区域超高被拦住', !result.ok && result.errors.some((e) => e.includes('高度 60')), errorsOf(result));
}

// 6. 未知物品。
{
  const result = validateBlueprint(makeText([base(0, 9999, 5, 5)]));
  check('未知物品被拦住', !result.ok && result.errors.some((e) => e.includes('不在游戏数据里')), errorsOf(result));
}

// 7. 解析失败。
{
  const result = validateBlueprint('not a blueprint');
  check('解析失败返回错误', !result.ok && result.errors[0].includes('解析失败'), errorsOf(result));
}

console.log(`\n${failed === 0 ? '全部通过' : `${failed} 项失败`}（${passed} 通过 / ${failed} 失败）`);
process.exit(failed === 0 ? 0 : 1);
