#!/usr/bin/env node
/**
 * 出图器 + 单段布局测试（离线）。
 *
 * 覆盖：单段机器行的蓝图能过硬校验；带子首尾相连；分拣器超长/未知建筑会抛错。
 */
import { BlueprintBuilder } from '../src/blueprint/emit.mjs';
import { busCompatible, layoutBus, layoutSingleStage } from '../src/blueprint/layout.mjs';
import { STATION_PORT_CELLS, stationPortCell } from '../src/blueprint/ports.mjs';
import { validateBlueprint } from '../src/blueprint/validate.mjs';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

// 1. 两输入、两台机器：过硬校验。
{
  const { text, meta } = layoutSingleStage({ buildingId: 2303, count: 2, inputs: [1101, 1202], output: 1204, title: 'two' });
  const result = validateBlueprint(text);
  check('两输入单段通过硬校验', result.ok, result.errors.slice(0, 3).join('；'));
  check('宽度/台数', meta.machines === 2 && meta.width === 9, JSON.stringify(meta));
}

// 2. 三条输入带：分拣器长度分别 1/2/3。
{
  const { text } = layoutSingleStage({ buildingId: 2303, count: 1, inputs: [1101, 1104, 1202], output: 1301 });
  const result = validateBlueprint(text);
  check('三输入单段通过硬校验', result.ok, result.errors.slice(0, 3).join('；'));
}

// 3. 带子链：除最后一节外每一节都指向下一节。
{
  const b = new BlueprintBuilder({ width: 10, height: 10 });
  const belts = [b.addBelt(2003, 1, 1), b.addBelt(2003, 2, 1), b.addBelt(2003, 3, 1)];
  for (let i = 0; i + 1 < belts.length; i += 1) b.link(belts[i], belts[i + 1]);
  const broken = belts.slice(0, -1).every((belt, i) => belt.outputObjIdx === belts[i + 1].index);
  check('带子首尾相连', broken && belts.at(-1).outputObjIdx === -1);
}

// 4. 分拣器超长：带子离工厂 4 格，应抛错。
{
  const b = new BlueprintBuilder({ width: 20, height: 20 });
  const factory = b.add(2303, 5, 5);
  const belt = b.addBelt(2003, 5, 0);
  let threw = false;
  try {
    b.addSorter(2013, { belt, factory, side: 'bottom', offset: 0, io: 'in' });
  } catch {
    threw = true;
  }
  check('分拣器超长抛错', threw);
}

// 5. 总线布局：两段（熔炉→制造台），能过硬校验。
{
  const stages = [
    { buildingId: 2302, count: 2, recipeId: 1, output: 1101, inputs: [1001], outputRate: 120, inputRates: new Map([[1001, 120]]) },
    { buildingId: 2303, count: 2, recipeId: 5, output: 1201, inputs: [1101], outputRate: 60, inputRates: new Map([[1101, 60]]) },
  ];
  const { text, meta } = layoutBus({ stages, target: 1201, title: 'bus' });
  const result = validateBlueprint(text);
  check('总线布局通过硬校验', result.ok, result.errors.slice(0, 3).join('；'));
  check('总线机器数', meta.machines === 4, JSON.stringify(meta));
}

// 6. 总线容量：单条黄带放不下就抛错。
{
  const stages = [
    { buildingId: 2302, count: 2, recipeId: 1, output: 1101, inputs: [1001], outputRate: 120, inputRates: new Map([[1001, 9000]]) },
  ];
  let threw = false;
  try {
    layoutBus({ stages, target: 1101, beltId: 2001 });
  } catch (error) {
    threw = String(error.message).includes('传送带最大容量');
  }
  check('总线容量超限抛错', threw);
}

// 7. 总线支持的机器：正上方有中间槽位的，和下端口可以通过转 180° 朝上的。
{
  check('2303 适合总线', busCompatible(2303));
  check('2302 适合总线', busCompatible(2302));
  check('2308 精炼厂适合总线', busCompatible(2308));
  const refinery = layoutBus({
    stages: [{ buildingId: 2308, count: 2, recipeId: 16, output: 1120, outputs: [{ itemId: 1120, rate: 60 }, { itemId: 1114, rate: 120 }], inputs: [1007], inputRates: new Map([[1007, 180]]), outputRate: 60 }],
    target: 1120,
    rawRates: new Map([[1007, 180]]),
  });
  const result = validateBlueprint(refinery.text);
  check('精炼厂总线通过硬校验', result.ok, result.errors.slice(0, 3).join('；'));
}

// 8. 有端口偏移数据的化工厂/位面熔炉也能上总线。
{
  for (const [buildingId, name] of [[2310, '化工厂'], [2309, '位面熔炉']]) {
    check(`总线支持${name}`, busCompatible(buildingId));
    const built = layoutBus({
      stages: [{ buildingId, count: 2, recipeId: 16, output: 1120, inputs: [1007], inputRates: new Map([[1007, 180]]), outputRate: 60 }],
      target: 1120,
      rawRates: new Map([[1007, 180]]),
    });
    const result = validateBlueprint(built.text);
    check(`${name}总线通过硬校验`, result.ok, result.errors.slice(0, 3).join('；'));
  }
}

// 9. 没有端口数据、槽位又不在正上方的大建筑仍拒绝。
{
  check('2312 不适合总线', !busCompatible(2312));
  let threw = false;
  try {
    layoutBus({
      stages: [{ buildingId: 2312, count: 1, recipeId: 16, output: 1120, inputs: [1007], inputRates: new Map([[1007, 60]]), outputRate: 60 }],
      target: 1120,
    });
  } catch (error) {
    threw = error.code === 'bus-unsupported';
  }
  check('总线拒绝 2312', threw);
}

// 10. 物流站：站本身能出图；12 个端口格互不重复。
{
  const builder = new BlueprintBuilder({ width: 40, height: 40, title: 'station' });
  builder.add(2103, 20, 20, 0, { yaw: 0 });
  const result = validateBlueprint(builder.finish());
  check('物流站单体能过硬校验', result.ok, result.errors.slice(0, 3).join('；'));
  const cells = STATION_PORT_CELLS.get(2103).map((cell) => `${cell.x},${cell.y}`);
  check('12 个端口格互不重复', new Set(cells).size === 12, cells.join(' '));
  check('0 号端口在 (1,3)', stationPortCell(2103, 0).x === 1 && stationPortCell(2103, 0).y === 3);
}

// 11. 未知建筑。
{
  const b = new BlueprintBuilder({ width: 10, height: 10 });
  let threw = false;
  try {
    b.add(9999, 1, 1);
  } catch {
    threw = true;
  }
  check('未知建筑抛错', threw);
}

console.log(`\n${failed === 0 ? '全部通过' : `${failed} 项失败`}（${passed} 通过 / ${failed} 失败）`);
process.exit(failed === 0 ? 0 : 1);
