#!/usr/bin/env node
/**
 * M3 知识库内核测试（离线）。
 *
 *   node test/kb.mjs
 *
 * 覆盖：schema 校验、中英文/ID/SID 搜索、精确查询、配方树（多级/副产物/循环/深度/rate）、
 * 文本类搜索与查询、错误输入。
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { KnowledgeBase, KbSchemaError, parseKbData } from '../dist/kb/knowledge.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));
const near = (a, b, eps = 1e-6) => Math.abs(a - b) <= eps;

const here = dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(readFileSync(join(here, 'fixtures', 'kb-test.json'), 'utf8'));
const kb = new KnowledgeBase(parseKbData(fixture));

console.log('── 1. schema ──');
check('stats.items', kb.stats.items === 11, String(kb.stats.items));
check('stats.recipes', kb.stats.recipes === 9, String(kb.stats.recipes));
check('stats.techs', kb.stats.techs === 2, String(kb.stats.techs));
check('stats.texts', kb.stats.texts === 5, String(kb.stats.texts));
try {
  parseKbData({ schemaVersion: 999, gameVersion: 'x', locale: 'zh-CN' });
  fail('schemaVersion 不匹配应抛错');
} catch (err) {
  check('schemaVersion 不匹配应抛错', err instanceof KbSchemaError);
}
try {
  parseKbData({ schemaVersion: 1, locale: 'zh-CN' });
  fail('缺 gameVersion 应抛错');
} catch (err) {
  check('缺 gameVersion 应抛错', err instanceof KbSchemaError);
}

console.log('── 2. 搜索 ──');
const iron = kb.search('铁块');
check('搜索“铁块”有结果', iron.length > 0);
check('搜索“铁块”命中物品 1101', iron.some((hit) => hit.kind === 'item' && hit.id === 1101));
check('搜索“铁块”命中配方 1', iron.some((hit) => hit.kind === 'recipe' && hit.id === 1));
const coil = kb.search('磁线圈');
check('搜索“磁线圈”首个是物品', coil[0]?.kind === 'item' && coil[0]?.id === 1201, JSON.stringify(coil[0]));
check('搜索“磁线圈”也命中配方', coil.some((hit) => hit.kind === 'recipe' && hit.id === 3));
check('按 id 搜索 1201', kb.search('1201').some((hit) => hit.id === 1201));
check('按 SID 搜索 coil', kb.search('coil').some((hit) => hit.id === 1201));
check('中文片段搜索“电路”', kb.search('电路').some((hit) => hit.id === 1202));
check('空查询返回空', kb.search('   ').length === 0);
check('kinds 过滤只返回 tech', kb.search('电磁', ['tech']).every((hit) => hit.kind === 'tech'));
check('limit 生效', kb.search('铁', undefined, 2).length <= 2);

console.log('── 2b. 剧透过滤 ──');
check('默认 low 不返回 story 教程', !kb.search('前情介绍').some((hit) => hit.kind === 'tutorial' && hit.id === 1));
check('显式 story 返回 story 教程', kb.search('前情介绍', undefined, 10, 'story').some((hit) => hit.kind === 'tutorial' && hit.id === 1));
check('none 仍返回无剧透条目', kb.search('低空飞行', undefined, 10, 'none').every((hit) => hit.kind !== 'tutorial'));

console.log('── 3. 精确查询 ──');
check('get item by id', kb.get('item', 1101)?.value?.name === '铁块');
check('get item by sid', kb.get('item', 'iron-ingot')?.value?.id === 1101);
check('get item by name', kb.get('item', '磁线圈')?.value?.id === 1201);
check('get tech by id', kb.get('tech', 2)?.value?.name === '电磁学');
check('get missing 返回 null', kb.get('item', 999999) === null);
check('get text by id', kb.get('tutorial', 1)?.value?.title === '前情介绍');
check('get text by title', kb.get('tip', '低空飞行')?.value?.id === 1);
check('get missing text 返回 null', kb.get('tutorial', 999) === null);

console.log('── 4. 配方树 ──');
const tree = kb.recipeTree('磁线圈', { amount: 4 });
check('root item', tree.item.id === 1201);
check('root recipe 3', tree.tree.recipeId === 3);
check('root crafts 2', near(tree.tree.crafts, 2), String(tree.tree.crafts));
check('root 输入磁铁 2', near(tree.tree.inputs.find((i) => i.itemId === 1102)?.amount ?? 0, 2));
check('root 输入铜块 2', near(tree.tree.inputs.find((i) => i.itemId === 1104)?.amount ?? 0, 2));
const rawIron = tree.raw.find((r) => r.itemId === 1001)?.amount ?? 0;
const rawCopper = tree.raw.find((r) => r.itemId === 1103)?.amount ?? 0;
check('原料铁矿 2', near(rawIron, 2), String(rawIron));
check('原料铜矿 2', near(rawCopper, 2), String(rawCopper));
check('非每分钟口径', tree.perMinute === false);

const perMinute = kb.recipeTree('磁线圈', { rate: 60 });
check('rate 口径 perMinute', perMinute.perMinute === true);
check('rate 60/min → crafts 30/min', near(perMinute.tree.crafts, 30), String(perMinute.tree.crafts));
check('rate 60/min → 铁矿 30/min', near(perMinute.raw.find((r) => r.itemId === 1001)?.amount ?? 0, 30));

const depth1 = kb.recipeTree('电路板', { depth: 1 });
check('depth=1 标记 depthLimited', depth1.depthLimited === true);
check('depth=1 把中间品当原料', depth1.raw.some((r) => r.itemId === 1201));
check('depth=1 不展开到铁矿', !depth1.raw.some((r) => r.itemId === 1001));

const cycle = kb.recipeTree('循环甲', { depth: 8 });
check('循环被检测', cycle.cycles.length > 0, JSON.stringify(cycle.cycles));
check('循环仍返回原料', cycle.raw.length > 0);

const forced = kb.recipeTree('磁线圈', { amount: 2, recipeId: 3 });
check('显式 recipeId 生效', forced.tree.recipeId === 3);
try {
  kb.recipeTree('不存在的物品');
  fail('未知物品应抛错');
} catch (err) {
  check('未知物品应抛错', /找不到物品/.test(String(err)));
}

console.log('── 4b. 副产物抵扣 ──');
const byKb = new KnowledgeBase(parseKbData({
  schemaVersion: 1,
  gameVersion: 'test-by',
  locale: 'zh-CN',
  items: [
    { id: 1, name: '矿' },
    { id: 2, name: '锭' },
  ],
  recipes: [
    { id: 1, name: '锭', timeSpend: 60, timeSeconds: 1, inputs: [{ id: 1, count: 1 }], outputs: [{ id: 2, count: 1 }, { id: 1, count: 1 }] },
  ],
  techs: [],
}));
const byTree = byKb.recipeTree('锭', { amount: 1 });
check('副产物与原料同名时被抵扣', !byTree.raw.some((row) => row.itemId === 1));
check('抵扣后没有剩余副产物', !byTree.byproducts.some((row) => row.itemId === 1));

console.log('── 5. 文本类 ──');
const tips = kb.search('低空飞行');
check('搜索“低空飞行”命中 tip', tips.some((hit) => hit.kind === 'tip' && hit.id === 1));
check('搜索“低空飞行”命中 milestone', tips.some((hit) => hit.kind === 'milestone' && hit.id === 1));
check('get goal by id', kb.get('goal', 1000)?.value?.title === '基础操作');
check('get glossary by id', kb.get('glossary', '戴森球(字典)')?.value?.title === '戴森球');

console.log(`\n${failed === 0 ? '✅' : '❌'} kb 测试${failed === 0 ? '全部通过' : '有失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
