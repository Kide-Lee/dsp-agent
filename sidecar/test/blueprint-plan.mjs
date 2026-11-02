#!/usr/bin/env node
/**
 * blueprint_generate 规划正确性测试（离线）。
 *
 * 覆盖：
 *   - 机器数直接来自 kb_plan（60/min 电磁涡轮 = 29 台，7 个阶段）
 *   - 生成物里真的有中间阶段（14 台熔炉 + 15 台制造台），不是只放末段
 *   - rows 能按阶段拆机器：宽度下降、总机器数不变
 *   - 超出单张混带总线容量时自动拆成多张（300/min → 3 张，600/min → 5 张）
 *   - 知识库缺失 / 物品不存在时返回明确错误
 */

import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { KnowledgeStore } from '../dist/kb/store.js';
import { createBlueprintTools } from '../dist/blueprint/tools.js';
import { BlueprintWorkspace } from '../dist/blueprint/workspace.js';
import { fromStr } from '../vendor/dsp-calc/src/blueprint/parser.js';
import { validateBlueprint } from '../src/blueprint/validate.mjs';
import { makeTempDir } from './helpers.mjs';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const here = dirname(fileURLToPath(import.meta.url));
const root = makeTempDir('icarus-ii-blueprint-plan-');
const dataDir = join(root, 'data');
const blueprintDir = join(root, 'bp');

const fixture = JSON.parse(readFileSync(join(here, 'fixtures', 'kb-blueprint.json'), 'utf8'));
const store = new KnowledgeStore(dataDir);
store.setData(fixture);

const workspace = new BlueprintWorkspace({ root: blueprintDir });
const tools = createBlueprintTools(undefined, store, undefined, undefined, workspace);
const generate = tools.find((tool) => tool.name === 'blueprint_generate');
if (!generate) {
  console.error('缺少 blueprint_generate');
  process.exit(1);
}

async function run(params) {
  const result = await generate.execute('call-1', params);
  return result.details;
}

function parse(fileNameOrRel) {
  const rel = fileNameOrRel.includes('/') ? fileNameOrRel : join('IcarusII', fileNameOrRel);
  return fromStr(readFileSync(join(blueprintDir, rel), 'utf8'));
}

function countBuilding(blueprint, itemId) {
  return blueprint.buildings.filter((building) => building.itemId === itemId).length;
}

try {
  console.log('── 1. 60/min 电磁涡轮：全链路机器数 ──');
  {
    const result = await run({ item: '电磁涡轮', rate: 60 });
    check('saved', result.saved === true, JSON.stringify(result));
    check('单张', result.tileCount === 1);
    check('7 个阶段', result.stages.length === 7, String(result.stages.length));
    check('总机器数 29', result.machines === 29, String(result.machines));
    check('无 warning', result.warnings.length === 0, JSON.stringify(result.warnings));
    check('默认保存到 IcarusII', result.relPath.startsWith('IcarusII/'), String(result.relPath));
    const blueprint = parse(result.relPath ?? result.fileName);
    check('14 台电弧熔炉（铁块/磁铁/铜块）', countBuilding(blueprint, 2302) === 14, String(countBuilding(blueprint, 2302)));
    check('15 台制造台 Mk.I（中间 + 末段）', countBuilding(blueprint, 2303) === 15, String(countBuilding(blueprint, 2303)));
    check('尺寸在安全范围', result.width <= 1000 && result.height <= 50, `${result.width}x${result.height}`);
  }

  console.log('\n── 1b. 保存到指定子目录 ──');
  {
    const nested = await run({ item: '电磁涡轮', rate: 60, dir: 'IcarusII/测试/子目录' });
    check('dir 生效', nested.relPath.startsWith('IcarusII/测试/子目录/'), String(nested.relPath));
    check('嵌套目录文件存在', existsSync(join(blueprintDir, nested.relPath)), nested.relPath);
  }

  console.log('\n── 2. rows 拆阶段：宽度下降、机器数不变 ──');
  {
    const single = await run({ item: '电磁涡轮', rate: 60, rows: 1 });
    const doubled = await run({ item: '电磁涡轮', rate: 60, rows: 2 });
    check('rows=1 单行', single.rows === 1);
    check('rows=2 实际用了 2 行', doubled.rows === 2);
    check('宽度下降', doubled.width < single.width, `${single.width} -> ${doubled.width}`);
    check('机器数不变', doubled.machines === single.machines, `${single.machines} -> ${doubled.machines}`);
    check('rows=2 无 warning', doubled.warnings.length === 0, JSON.stringify(doubled.warnings));
  }

  console.log('\n── 3. 高产量拆多张（总线容量上限）──');
  {
    const three = await run({ item: '电磁涡轮', rate: 300 });
    check('300/min 拆 3 张', three.tileCount === 3, String(three.tileCount));
    check('每张约 100/min', Math.abs(three.ratePerTile - 100) < 1e-6, String(three.ratePerTile));
    check('tiles 数组长度', three.tiles.length === 3);
    check('每个文件都落盘', three.tiles.every((tile) => existsSync(tile.path)), JSON.stringify(three.tiles.map((tile) => tile.path)));
    check('机器数为三张之和', three.tiles.reduce((sum, tile) => sum + tile.machines, 0) === three.machines);

    const six = await run({ item: '电磁涡轮', rate: 600 });
    check('600/min 拆 5 张', six.tileCount === 5, String(six.tileCount));
    const huge = await run({ item: '电磁涡轮', rate: 1200 });
    check('1200/min 拆 9 张', huge.tileCount === 9, String(huge.tileCount));
    check('每张都过硬校验', huge.tiles.every((tile) => validateBlueprint(readFileSync(join(blueprintDir, tile.relPath), 'utf8')).ok));
    check('每张约 120/min', Math.abs(six.ratePerTile - 120) < 1e-6, String(six.ratePerTile));
    check('每张尺寸安全', six.tiles.every((tile) => tile.width <= 1000 && tile.height <= 50));
  }

  console.log('\n── 4. 档位 ──');
  {
    const mk3 = await run({ item: '电磁涡轮', rate: 60, buildingTier: 'mk3' });
    const base = await run({ item: '电磁涡轮', rate: 60, buildingTier: 'base' });
    check('mk3 机器数更少', mk3.machines < base.machines, `${base.machines} -> ${mk3.machines}`);
    const motor = mk3.stages.find((stage) => stage.primaryItem === '电动机');
    check('mk3 选择制造台 Mk.III', motor?.buildingId === 2305, JSON.stringify(motor));
  }

  console.log('\n── 5. 别名（Wiki/curated 解析后进入生成）──');
  {
    const aliasTools = createBlueprintTools(undefined, store, {
      resolve: async (kb, input) => ({
        input,
        item: kb.resolveItem('电磁涡轮'),
        alias: '电磁涡轮',
        source: 'wiki',
        candidates: [],
      }),
    }, undefined, workspace);
    const aliasGenerate = aliasTools.find((tool) => tool.name === 'blueprint_generate');
    const aliased = await aliasGenerate.execute('call-alias', { item: '绿马达', rate: 60 });
    check('别名解析到电磁涡轮', aliased.details.item === '电磁涡轮', JSON.stringify(aliased.details));
    check('结果带 alias 来源', aliased.details.alias?.source === 'wiki', JSON.stringify(aliased.details.alias));
    check('别名蓝图可生成', aliased.details.saved === true && aliased.details.machines === 29);
  }

  console.log('\n── 6. from：从冶炼产物/中间产物开始 ──');
  {
    const smelted = await run({ item: '电磁涡轮', rate: 60, from: 'smelted' });
    check('smelted 只保留 4 个装配阶段', smelted.stages.length === 4, JSON.stringify(smelted.stages.map((s) => s.primaryItem)));
    check('smelted 机器数 15', smelted.machines === 15, String(smelted.machines));
    check('smelted 原料含铁块', smelted.raw.some((row) => row.item === '铁块'), JSON.stringify(smelted.raw));
    const smeltedBlueprint = parse(smelted.relPath ?? smelted.fileName);
    check('smelted 不再放熔炉', countBuilding(smeltedBlueprint, 2302) === 0, String(countBuilding(smeltedBlueprint, 2302)));

    const fromMotor = await run({ item: '电磁涡轮', rate: 60, from: '电动机' });
    check('从电动机开始 9 台机器', fromMotor.machines === 9, String(fromMotor.machines));
    check('原料含电动机', fromMotor.raw.some((row) => row.item === '电动机'), JSON.stringify(fromMotor.raw));
  }

  console.log('\n── 7. 错误路径 ──');
  {
    const noKbTool = createBlueprintTools(undefined).find((tool) => tool.name === 'blueprint_generate');
    const noKb = await noKbTool.execute('call-2', { item: '电磁涡轮', rate: 60 });
    check('知识库缺失返回 kb_unavailable', noKb.content[0].text.includes('[kb_unavailable]'), noKb.content[0].text);

    const missing = await run({ item: '不存在的物品', rate: 60 });
    check('未知物品返回 bad_request', missing?.error === '找不到物品：不存在的物品', JSON.stringify(missing));

    const single = await run({ item: '铁块', rate: 60 });
    check(
      '单阶段配方返回明确错误',
      typeof single?.error === 'string' && single.error.includes('至少需要两个生产阶段'),
      JSON.stringify(single),
    );
  }
} finally {
  rmSync(root, { recursive: true, force: true });
}

if (failed > 0) {
  console.error(`\n❌ blueprint-plan 测试失败：${failed} 项`);
  process.exit(1);
}
console.log(`\n✅ blueprint-plan 测试全部通过（${passed} 项）`);
