#!/usr/bin/env node
/**
 * 蓝图 DSL（JS 子集）测试：沙箱执行、IR、编译出图、诊断错误码。
 * 只组装输入与断言输出，不复制业务逻辑。
 */
import { readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { KnowledgeStore } from '../dist/kb/store.js';
import { BlueprintWorkspace } from '../dist/blueprint/workspace.js';
import { createBlueprintTools } from '../dist/blueprint/tools.js';
import { runDsl } from '../dist/blueprint/dsl/sandbox.js';
import { makeTempDir } from './helpers.mjs';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const fixture = JSON.parse(readFileSync(new URL('./fixtures/kb-blueprint.json', import.meta.url), 'utf8'));
const root = makeTempDir('icarus-ii-dsl-');
const store = new KnowledgeStore(join(root, 'data'));
store.setData(fixture);
const workspace = new BlueprintWorkspace({ root: join(root, 'bp') });
const tools = createBlueprintTools(undefined, store, undefined, undefined, workspace);
const compileTool = tools.find((tool) => tool.name === 'blueprint_compile');

const run = async (source, extra = {}) => (await compileTool.execute('call-1', { source, ...extra })).details;

console.log('\n── 1. 沙箱与 IR ──');
{
  const ir = await runDsl(`
    target('电磁涡轮', 60);
    raw('铁矿', '铜矿');
    options({ belt: '极速传送带' });
    const a = module('电动机');
    connect(a.out, 'm2.in');
    group('g', [a]);
    station('原料站', { side: 'west' });
    constraint('入口在左');
  `);
  check('target 解析', ir.targets.length === 1 && ir.targets[0].rate === 60);
  check('raw 解析', ir.raw.length === 2);
  check('options 解析', ir.options.belt === '极速传送带');
  check('module/connect 解析', ir.modules.length === 1 && ir.connections[0].from === 'm1.out');
}

console.log('\n── 2. 编译出图 ──');
{
  const result = await run("target('电磁涡轮', 60);");
  check('保存成功', result.saved === true, JSON.stringify(result));
  check('尺寸与建筑数', result.width > 0 && result.height > 0 && result.buildings > 0);
  check('返回报告', Array.isArray(result.report?.buildings) && result.report.buildings.length > 0);
  check('机器数与规划一致', result.machines > 0);
}

console.log('\n── 3. 诊断 ──');
{
  const noTarget = await run("raw('铁矿');");
  check('缺 target 报 E004', noTarget.code === 'E004', JSON.stringify(noTarget));

  const bad = await run("target('电磁涡轮', 60");
  check('语法错误报 E008', bad.code === 'E008', JSON.stringify(bad));

  const forbidden = await run("require('fs');");
  check('禁用 API 报 E008', forbidden.code === 'E008', JSON.stringify(forbidden));

  const twoTargets = await run("target('电磁涡轮', 60); target('铁块', 60);");
  check('多目标报 E102', twoTargets.code === 'E102', JSON.stringify(twoTargets));
}

console.log('\n── 4. 未参与布局的特性给警告 ──');
{
  const result = await run(`
    target('电磁涡轮', 60);
    raw('铁矿');
    module('电动机');
  `);
  check('原始原料警告', result.warnings.some((line) => line.includes('W501')), JSON.stringify(result.warnings));
  check('自定义模块警告', result.warnings.some((line) => line.includes('W502')), JSON.stringify(result.warnings));
}

rmSync(root, { recursive: true, force: true });
if (failed > 0) {
  console.error(`\n❌ blueprint-dsl 测试失败：${failed} 项`);
  process.exit(1);
}
console.log(`\n✅ blueprint-dsl 测试全部通过（${passed} 项）`);
