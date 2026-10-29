#!/usr/bin/env node
/**
 * 被动档位（save.bind.blueprintTiers）测试（离线）。
 *
 * 验证：
 *   - parseSaveBindPayload 接受/裁剪/忽略非法 tiers；
 *   - blueprint_generate 默认使用 mod 上报的档位（mk3/蓝带/堆叠）；
 *   - 显式参数覆盖 mod 档位；
 *   - 未上报时退回默认档；
 *   - 低档分拣器给出明确 warning。
 */

import { readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { parseSaveBindPayload, parseBlueprintTiers } from '../dist/protocol.js';
import { KnowledgeStore } from '../dist/kb/store.js';
import { createBlueprintTools } from '../dist/blueprint/tools.js';
import { BlueprintWorkspace } from '../dist/blueprint/workspace.js';
import { makeTempDir } from './helpers.mjs';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const fixture = JSON.parse(readFileSync(new URL('./fixtures/kb-blueprint.json', import.meta.url), 'utf8'));
const root = makeTempDir('icarus-ii-tiers-');
const store = new KnowledgeStore(join(root, 'data'));
const workspace = new BlueprintWorkspace({ root: join(root, 'bp') });
store.setData(fixture);

console.log('── 1. 解析与裁剪 ──');
{
  const parsed = parseSaveBindPayload({
    saveKey: 'save-a',
    saveName: '档 A',
    blueprintTiers: { assembler: 2, belt: 1, inserter: 3, piler: true, stacking: false },
  });
  check('解析 assembler', parsed?.blueprintTiers?.assembler === 2);
  check('解析 belt', parsed?.blueprintTiers?.belt === 1);
  check('解析 piler', parsed?.blueprintTiers?.piler === true);
  check('解析 stacking', parsed?.blueprintTiers?.stacking === false);

  const clamped = parseBlueprintTiers({ assembler: 99, belt: -3, inserter: 9.8 });
  check('超范围裁剪', clamped?.assembler === 2 && clamped?.belt === 0 && clamped?.inserter === 3, JSON.stringify(clamped));
  check('空对象为 null', parseBlueprintTiers({}) === null);
  check('非法类型为 null', parseBlueprintTiers('x') === null);
  const noTiers = parseSaveBindPayload({ saveKey: 'save-a' });
  check('缺省 tiers 为 null', noTiers?.blueprintTiers === null);
}

async function generate(tiers, params = {}) {
  const tools = createBlueprintTools(undefined, store, undefined, () => tiers, workspace);
  const tool = tools.find((item) => item.name === 'blueprint_generate');
  return (await tool.execute('call-1', { item: '电磁涡轮', rate: 60, ...params })).details;
}

console.log('\n── 2. mod 档位默认生效 ──');
{
  const high = await generate({ assembler: 2, belt: 2, inserter: 3, piler: true, stacking: true });
  check('tierSource=mod', high.tierSource === 'mod', String(high.tierSource));
  check('buildingTier=mk3', high.buildingTier === 'mk3', String(high.buildingTier));
  check('mk3 机器数更少', high.machines < 29, String(high.machines));
  check('无 warning', high.warnings.length === 0, JSON.stringify(high.warnings));

  const low = await generate({ assembler: 0, belt: 0, inserter: 3, piler: true, stacking: true });
  check('低档也用 base', low.buildingTier === 'base', String(low.buildingTier));
  check('低档仍可生成', low.saved === true);
  check('低档无 warning', low.warnings.length === 0, JSON.stringify(low.warnings));
}

console.log('\n── 3. 显式覆盖 / 未上报 / 低档分拣器 ──');
{
  const explicit = await generate(
    { assembler: 2, belt: 2, inserter: 3, piler: true, stacking: true },
    { buildingTier: 'base', beltType: 2, insertType: 3, stackSize: 1 },
  );
  check('显式覆盖 tierSource=explicit', explicit.tierSource === 'explicit', String(explicit.tierSource));
  check('显式覆盖 buildingTier=base', explicit.buildingTier === 'base', String(explicit.buildingTier));

  const none = await generate(null);
  check('未上报 tierSource=default', none.tierSource === 'default', String(none.tierSource));
  check('未上报仍可生成', none.saved === true);

  const lowInserter = await generate({ assembler: 0, belt: 2, inserter: 2, piler: false, stacking: false });
  check('低档分拣器给 warning', lowInserter.warnings.some((line) => line.includes('集装分拣器')), JSON.stringify(lowInserter.warnings));
}

rmSync(root, { recursive: true, force: true });
if (failed > 0) {
  console.error(`\n❌ blueprint-tiers 测试失败：${failed} 项`);
  process.exit(1);
}
console.log(`\n✅ blueprint-tiers 测试全部通过（${passed} 项）`);
