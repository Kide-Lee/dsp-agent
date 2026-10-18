#!/usr/bin/env node
/**
 * M4b 产线规划（kb_plan）测试（离线）。
 *
 * 用一份**带建筑参数**的最小知识库验证：
 *   - 机器数 = 每分钟制作次数 × 耗时 ÷ 60 ÷ 速度倍率（向上取整）
 *   - 多级链：齿轮 ← 铁块 ← 铁矿
 *   - 电力按 workEnergyPerTick 折算
 *   - 传送带按单路输入折算
 *   - 缺建筑参数时降级为 1x 并给出 warning
 *   - 工具层输出包含机器清单与 assumptions
 *
 *   node test/kb-plan.mjs
 */

import { KnowledgeBase, parseKbData } from '../dist/kb/knowledge.js';
import { createKbTools } from '../dist/kb/tools.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));
const near = (a, b, eps = 1e-6) => Math.abs(a - b) <= eps;

// ---------------------------------------------------------------- fixture

function baseFixture() {
  return {
    schemaVersion: 1,
    gameVersion: 'test-plan',
    locale: 'zh-CN',
    items: [
      { id: 1001, name: '铁矿', type: 1 },
      { id: 1101, name: '铁块' },
      { id: 1201, name: '齿轮' },
      {
        id: 2302,
        name: '熔炉',
        building: { assemblerSpeed: 10000, workEnergyPerTick: 1000, assemblerRecipeType: 1 },
      },
      {
        id: 2303,
        name: '熔炉Mk2',
        building: { assemblerSpeed: 20000, workEnergyPerTick: 2000, assemblerRecipeType: 1 },
      },
      {
        id: 2304,
        name: '化工厂',
        building: { assemblerSpeed: 10000, workEnergyPerTick: 1000, assemblerRecipeType: 2 },
      },
    ],
    recipes: [
      {
        id: 1,
        name: '铁块',
        type: 1,
        timeSpend: 120,
        timeSeconds: 2,
        inputs: [{ id: 1001, count: 1 }],
        outputs: [{ id: 1101, count: 1 }],
      },
      {
        id: 2,
        name: '齿轮',
        type: 1,
        timeSpend: 60,
        timeSeconds: 1,
        inputs: [{ id: 1101, count: 2 }],
        outputs: [{ id: 1201, count: 1 }],
      },
    ],
    techs: [],
  };
}

function makeKb(fixture) {
  return new KnowledgeBase(parseKbData(fixture));
}

// ---------------------------------------------------------------- 1. 多级链

console.log('── 1. 多级链：齿轮 30/min ──');
const kb = makeKb(baseFixture());
const plan = kb.plan('齿轮', { rate: 30 });

check('目标物品', plan.item.id === 1201 && plan.item.name === '齿轮');
check('两个配方', plan.recipes.length === 2, JSON.stringify(plan.recipes.map((r) => r.recipeName)));

const gear = plan.recipes.find((row) => row.recipeId === 2);
const plate = plan.recipes.find((row) => row.recipeId === 1);
check('齿轮行存在', gear !== undefined);
check('铁块行存在', plate !== undefined);

check('齿轮 exactCount = 0.5', near(gear.exactCount, 0.5), String(gear.exactCount));
check('齿轮机器数向上取整 = 1', gear.buildingCount === 1);
check('齿轮选用最低速机器', gear.buildingId === 2302);
check('齿轮功率 = 60 kW', near(gear.powerKw, 60), String(gear.powerKw));

check('铁块 exactCount = 2', near(plate.exactCount, 2), String(plate.exactCount));
check('铁块机器数 = 2', plate.buildingCount === 2);
check('铁块功率 = 120 kW', near(plate.powerKw, 120), String(plate.powerKw));

check('总功率 = 180 kW', near(plan.totalPowerKw, 180), String(plan.totalPowerKw));
check('原料 铁矿 60/min', plan.raw.some((row) => row.itemId === 1001 && near(row.amount, 60)), JSON.stringify(plan.raw));
check('最大单路输入 60/min', near(plan.belts.maxSingleInputPerMin, 60), String(plan.belts.maxSingleInputPerMin));
check('传送带 1 条', plan.belts.maxBelts === 1);
check('假设里写明未建模增产剂', plan.assumptions.some((line) => line.includes('增产剂')));
check('没有警告', plan.warnings.length === 0, JSON.stringify(plan.warnings));

// ---------------------------------------------------------------- 2. 档位

console.log('\n── 2. 机器档位 ──');
const mk2 = kb.plan('齿轮', { rate: 30, buildingTier: 'mk2' });
const gearMk2 = mk2.recipes.find((row) => row.recipeId === 2);
const plateMk2 = mk2.recipes.find((row) => row.recipeId === 1);
check('mk2 齿轮选 2303', gearMk2.buildingId === 2303);
check('mk2 齿轮 exactCount = 0.25', near(gearMk2.exactCount, 0.25), String(gearMk2.exactCount));
check('mk2 齿轮机器数 = 1', gearMk2.buildingCount === 1);
check('mk2 铁块机器数 = 1', plateMk2.buildingCount === 1);
check('mk2 齿轮功率 = 120 kW', near(gearMk2.powerKw, 120), String(gearMk2.powerKw));
check('mk2 总功率 = 240 kW', near(mk2.totalPowerKw, 240), String(mk2.totalPowerKw));

const forced = kb.plan('齿轮', { rate: 30, buildingId: 2303 });
check('buildingId 优先于 tier', forced.recipes.every((row) => row.buildingId === 2303));

// ---------------------------------------------------------------- 3. 降级

console.log('\n── 3. 缺建筑参数时降级 ──');
const bare = baseFixture();
for (const item of bare.items) delete item.building;
const bareKb = makeKb(bare);
const barePlan = bareKb.plan('齿轮', { rate: 30 });
check('降级后仍能算机器数', barePlan.recipes.every((row) => row.buildingCount >= 1));
check('降级后速度为 1x', barePlan.recipes.every((row) => near(row.speedMultiplier, 1)));
check('降级后功率为 0', near(barePlan.totalPowerKw, 0));
check('给出缺建筑参数的警告', barePlan.warnings.some((line) => line.includes('找不到对应机器')), JSON.stringify(barePlan.warnings));

// ---------------------------------------------------------------- 4. 错误输入

console.log('\n── 4. 错误输入 ──');
try {
  kb.plan('齿轮', { rate: 0 });
  fail('rate=0 应该抛错');
} catch (err) {
  check('rate=0 抛错', err instanceof Error && err.message.includes('rate'));
}
try {
  kb.plan('不存在的物品', { rate: 1 });
  fail('未知物品应该抛错');
} catch (err) {
  check('未知物品抛错', err instanceof Error && err.message.includes('找不到物品'));
}
try {
  kb.plan('齿轮', { rate: 1, recipeId: 999 });
  fail('未知配方应该抛错');
} catch (err) {
  check('未知配方抛错', err instanceof Error && err.message.includes('找不到配方'));
}

// ---------------------------------------------------------------- 5. 工具层

console.log('\n── 5. kb_plan 工具输出 ──');
const store = { snapshot: kb };
const tools = createKbTools(store);
const planTool = tools.find((tool) => tool.name === 'kb_plan');
check('kb_plan 已注册', planTool !== undefined);
if (planTool) {
  const result = await planTool.execute('call_1', { item: '齿轮', rate: 30 }, undefined);
  const text = result.content[0].text;
  check('文本含机器清单', text.includes('机器：') && text.includes('熔炉'), text.slice(0, 200));
  check('文本含总功率', text.includes('180.0 kW'), text.slice(0, 200));
  check('文本含假设', text.includes('增产剂'));
  check('details 是结构化结果', result.details?.ok === true && result.details?.data?.item?.id === 1201);

  const bad = await planTool.execute('call_2', { item: '齿轮' }, undefined);
  check('缺 rate 报 bad_request', bad.content[0].text.includes('[bad_request]'));

  const unknown = await planTool.execute('call_3', { item: '不存在', rate: 1 }, undefined);
  check('未知物品报 bad_request', unknown.content[0].text.includes('[bad_request]'));
}

// ---------------------------------------------------------------- main

console.log(`\n${failed === 0 ? '✅' : '❌'} kb-plan 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
