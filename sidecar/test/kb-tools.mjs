#!/usr/bin/env node
/**
 * M3 知识库工具与导出编排测试（离线）。
 *
 *   node test/kb-tools.mjs
 */

import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { KnowledgeStore } from '../dist/kb/store.js';
import { KnowledgeManager } from '../dist/kb/manager.js';
import { createKbTools, KB_TOOL_NAMES } from '../dist/kb/tools.js';
import { toolNamesForCapabilities } from '../dist/tools/registry.js';
import { buildToolCallPayload } from '../dist/protocol.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const here = dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(readFileSync(join(here, 'fixtures', 'kb-test.json'), 'utf8'));
const temp = mkdtempSync(join(tmpdir(), 'icarus-kb-'));

function tool(tools, name) {
  return tools.find((item) => item.name === name);
}

async function runTool(tools, name, params) {
  const target = tool(tools, name);
  if (!target) throw new Error(`missing tool ${name}`);
  return target.execute('call-1', params);
}

try {
  console.log('── 1. 工具名与协议 ──');
  check('kb 工具数 3', KB_TOOL_NAMES.length === 3);
  const noGame = toolNamesForCapabilities([]);
  check('无 game 能力仍列出 kb_search', noGame.includes('kb_search'));
  check('无 game 能力不列出 game_get_overview', !noGame.includes('game_get_overview'));
  const withGame = toolNamesForCapabilities(['game.read.v1']);
  check('有 game 能力同时列出两类', withGame.includes('game_get_overview') && withGame.includes('kb_get'));
  const internalCall = buildToolCallPayload({ callId: 'c1', name: 'game_export_prototypes', args: {}, risk: 0, timeoutMs: 1000, internal: true });
  check('internal=true 写入 tool_call', internalCall.internal === true);
  const normalCall = buildToolCallPayload({ callId: 'c2', name: 'x', args: {}, risk: 0, timeoutMs: 1000 });
  check('普通 tool_call 不带 internal', normalCall.internal === undefined);

  console.log('── 2. 未建立时返回 kb_unavailable ──');
  const emptyStore = new KnowledgeStore(join(temp, 'empty'));
  const emptyTools = createKbTools(emptyStore);
  const empty = await runTool(emptyTools, 'kb_search', { query: '铁块' });
  check('kb_search 返回 kb_unavailable', empty.content[0].text.includes('[kb_unavailable]'));
  check('kb_get 返回 kb_unavailable', (await runTool(emptyTools, 'kb_get', { kind: 'item', idOrName: '1101' })).content[0].text.includes('[kb_unavailable]'));
  check('kb_recipe_tree 返回 kb_unavailable', (await runTool(emptyTools, 'kb_recipe_tree', { item: '磁线圈' })).content[0].text.includes('[kb_unavailable]'));

  console.log('── 3. 导出编排（多分块 + internal） ──');
  const store = new KnowledgeStore(join(temp, 'kb'));
  const manager = new KnowledgeManager({ store, chunkSize: 2, timeoutMs: 1000 });
  const calls = [];
  const callOptions = [];
  const sets = {
    items: fixture.items,
    recipes: fixture.recipes,
    techs: fixture.techs,
    models: fixture.models ?? [],
    veins: fixture.veins ?? [],
    signals: fixture.signals ?? [],
  };
  const transport = {
    async callTool(name, args, options) {
      calls.push({ name, args });
      callOptions.push(options);
      if (name === 'game_export_text') {
        const rows = fixture.texts ?? [];
        const offset = Number(args.offset ?? 0);
        const limit = Number(args.limit ?? 2);
        const page = rows.slice(offset, offset + limit);
        return {
          ok: true,
          data: { offset, limit, total: rows.length, done: offset + page.length >= rows.length, rows: page },
          error: null,
          meta: {},
        };
      }
      if (name !== 'game_export_prototypes') throw new Error(`unexpected tool ${name}`);
      const set = args.set;
      const rows = sets[set] ?? [];
      const offset = Number(args.offset ?? 0);
      const limit = Number(args.limit ?? 2);
      const page = rows.slice(offset, offset + limit);
      return {
        ok: true,
        data: { set, offset, limit, total: rows.length, done: offset + page.length >= rows.length, rows: page },
        error: null,
        meta: {},
      };
    },
  };
  const started = Date.now();
  const okExport = await manager.ensure(transport, 'test-1.0', 'zh-CN');
  check('导出成功', okExport === true);
  check('导出调用都是 internal', callOptions.every((options) => options?.internal === true), JSON.stringify(callOptions.slice(0, 2)));
  check('items 分块 > 1', calls.filter((call) => call.args.set === 'items').length > 1);
  check('store 已就绪', store.isReady === true);
  check('导出耗时 < 1s', Date.now() - started < 1000);
  const meta = store.meta();
  check('导出计数 items', meta.counts?.items === 11, JSON.stringify(meta.counts));
  check('导出计数 texts', meta.counts?.texts === 5, JSON.stringify(meta.counts));
  check('缓存文件可重新加载', new KnowledgeStore(join(temp, 'kb')).loadFromDisk('test-1.0', 'zh-CN')?.stats.items === 11);

  const callsBefore = calls.length;
  const okCached = await manager.ensure(transport, 'test-1.0', 'zh-CN');
  check('同版本二次 ensure 不重新导出', okCached === true && calls.length === callsBefore);

  console.log('── 4. kb 工具可用 ──');
  const tools = createKbTools(store);
  check('工具数 3', tools.length === 3);
  const search = await runTool(tools, 'kb_search', { query: '磁线圈' });
  check('kb_search 命中物品', search.content[0].text.includes('item#1201'));
  check('kb_search 命中配方', search.content[0].text.includes('recipe#3'));
  const searchKinds = await runTool(tools, 'kb_search', { query: '铁', kinds: ['tech'] });
  check('kinds 过滤生效', !searchKinds.content[0].text.includes('item#'));
  const get = await runTool(tools, 'kb_get', { kind: 'item', idOrName: '1101' });
  check('kb_get 返回名称', get.content[0].text.includes('铁块'));
  const missing = await runTool(tools, 'kb_get', { kind: 'item', idOrName: '999999' });
  check('kb_get 缺失返回 not_found', missing.content[0].text.includes('[not_found]'));
  const tree = await runTool(tools, 'kb_recipe_tree', { item: '磁线圈', amount: 4 });
  check('kb_recipe_tree 返回原料', tree.content[0].text.includes('原料'));
  check('kb_recipe_tree 原料铁矿 2', tree.content[0].text.includes('铁矿 2.00'));
  const perMin = await runTool(tools, 'kb_recipe_tree', { item: '磁线圈', rate: 60 });
  check('kb_recipe_tree rate 口径', perMin.content[0].text.includes('铁矿 30.00'));
  const badTree = await runTool(tools, 'kb_recipe_tree', { item: '不存在' });
  check('kb_recipe_tree 未知物品报错', badTree.content[0].text.includes('[bad_request]'));
  const emptyQuery = await runTool(tools, 'kb_search', { query: '  ' });
  check('kb_search 空 query 报错', emptyQuery.content[0].text.includes('[bad_request]'));
  const tip = await runTool(tools, 'kb_search', { query: '低空飞行' });
  check('kb_search 命中文本条目', tip.content[0].text.includes('tip#1'));
  const storyDefault = await runTool(tools, 'kb_search', { query: '前情介绍' });
  check('kb_search 默认过滤 story', !storyDefault.content[0].text.includes('tutorial#1'));
  const storyExplicit = await runTool(tools, 'kb_search', { query: '前情介绍', spoiler: 'story' });
  check('kb_search 显式 spoiler=story', storyExplicit.content[0].text.includes('tutorial#1'));

  console.log('── 5. 失败导出不崩 ──');
  const failStore = new KnowledgeStore(join(temp, 'fail'));
  const failManager = new KnowledgeManager({ store: failStore, chunkSize: 2, timeoutMs: 1000 });
  const failTransport = {
    async callTool() {
      return { ok: false, data: null, error: { code: 'tool_failed', message: 'fake failure', retryable: false }, meta: {} };
    },
  };
  const failOk = await failManager.ensure(failTransport, 'test-1.0', 'zh-CN');
  check('导出失败返回 false', failOk === false);
  check('失败状态有 error', String(failStore.meta().error ?? '').includes('fake failure'));
  check('失败后工具仍返回 kb_unavailable', (await runTool(createKbTools(failStore), 'kb_search', { query: '铁' })).content[0].text.includes('[kb_unavailable]'));
} finally {
  rmSync(temp, { recursive: true, force: true });
}

console.log(`\n${failed === 0 ? '✅' : '❌'} kb-tools 测试${failed === 0 ? '全部通过' : '有失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
