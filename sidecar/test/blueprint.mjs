#!/usr/bin/env node
/**
 * M4h 蓝图仓库检索测试（离线，本地 GitHub 模拟）。
 *
 * 覆盖：索引缓存、关键词过滤、排序、来源署名、蓝图格式体检、404/坏路径。
 */

import { createServer } from 'node:http';
import { gzipSync } from 'node:zlib';
import { BlueprintClient, createBlueprintTools, inspectBlueprintText, BLUEPRINT_TOOL_NAMES } from '../dist/blueprint/tools.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const payload = gzipSync(Buffer.from('blueprint-binary'));
const validText = `BLUEPRINT:0,10,0,0,0,0,0,0,0,0,0,0,0"${payload.toString('base64')}"${'a'.repeat(32)}`;
const badText = `BLUEPRINT:0,10"${Buffer.from('not-gzip').toString('base64')}"${'b'.repeat(32)}`;

let treeRequests = 0;
const server = createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname.includes('/git/trees/HEAD')) {
    treeRequests += 1;
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({
      truncated: false,
      tree: [
        { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt', type: 'blob', size: 1234 },
        { path: '产线/白糖/白糖 120/min.txt', type: 'blob', size: 2345 },
        { path: '杂项/说明.txt', type: 'blob', size: 10 },
        { path: '图/预览.png', type: 'blob', size: 999 },
      ],
    }));
    return;
  }
  if (url.pathname.startsWith('/raw/')) {
    const path = decodeURIComponent(url.pathname);
    if (path.endsWith('缺失.txt')) {
      res.writeHead(404);
      res.end('nope');
      return;
    }
    res.end(path.endsWith('坏.txt') ? badText : validText);
    return;
  }
  res.writeHead(404);
  res.end('{}');
});

await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port;
const base = `http://127.0.0.1:${port}`;
const client = new BlueprintClient({ apiBase: base, rawBase: `${base}/raw`, timeoutMs: 3000 });
const tools = createBlueprintTools(client);
const byName = Object.fromEntries(tools.map((tool) => [tool.name, tool]));

async function run(name, params) {
  const result = await byName[name].execute('call_1', params, undefined);
  return result.content[0].text;
}

console.log('── 1. 格式体检 ──');
{
  const good = inspectBlueprintText(validText);
  check('合法蓝图', good.valid === true && good.decodedBytes === 16, JSON.stringify(good));
  check('保留头部', good.header.startsWith('BLUEPRINT:'));
  const bad = inspectBlueprintText(badText);
  check('坏 gzip 被拒绝', bad.valid === false && bad.reason === 'gzip');
  check('非蓝图文本被拒绝', inspectBlueprintText('hello').valid === false);
}

console.log('\n── 2. 搜索与署名 ──');
{
  const result = JSON.parse(await run('blueprint_search', { query: '卡西米尔', limit: 5 }));
  check('命中 1 条', result.results.length === 1, JSON.stringify(result.results));
  const first = result.results[0];
  check('path 正确', first.path.includes('卡西米尔'), first.path);
  check('仓库链接', first.blobUrl.includes('github.com/DSPBluePrints/FactoryBluePrints'));
  check('原始链接', first.rawUrl.includes('/raw/DSPBluePrints'));
  check('许可证', first.license === 'CC BY-NC-SA 4.0');
  check('不返回 png', result.results.every((row) => row.path.endsWith('.txt')));
  check('带署名说明', result.note.includes('CC BY-NC-SA 4.0'));

  const again = JSON.parse(await run('blueprint_search', { query: '白糖' }));
  check('索引缓存', treeRequests === 1 && again.results.length === 1, `treeRequests=${treeRequests}`);

  const multi = JSON.parse(await run('blueprint_search', { query: '白糖 120' }));
  check('多关键词 AND', multi.results.length === 1);
  const none = JSON.parse(await run('blueprint_search', { query: '不存在的东西' }));
  check('无结果', none.results.length === 0 && none.ok !== false);
}

console.log('\n── 3. info 与失败 ──');
{
  const info = JSON.parse(await run('blueprint_info', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt' }));
  check('valid=true', info.valid === true, JSON.stringify(info));
  check('有字节数', info.bytes > 0 && info.decodedBytes === 16);
  check('带许可证', info.license === 'CC BY-NC-SA 4.0');

  const bad = await run('blueprint_info', { path: '产线/坏.txt' });
  check('坏文件报 blueprint_invalid', bad.includes('[blueprint_invalid]'), bad.slice(0, 120));

  const missing = await run('blueprint_info', { path: '产线/缺失.txt' });
  check('404 报 blueprint_not_found', missing.includes('[blueprint_not_found]'), missing.slice(0, 120));

  const traversal = await run('blueprint_info', { path: '../etc/passwd.txt' });
  check('路径穿越被拒绝', traversal.includes('[blueprint_unavailable]') || traversal.includes('[blueprint_not_found]'));

  const empty = await run('blueprint_search', { query: '  ' });
  check('空 query 报错', empty.includes('[bad_request]'));
}

console.log('\n── 4. 元数据 ──');
check('两个蓝图工具', tools.length === 2 && BLUEPRINT_TOOL_NAMES.length === 2);
check('描述带署名', byName['blueprint_search'].description.includes('CC BY-NC-SA 4.0'));

console.log(`\n${failed === 0 ? '✅' : '❌'} blueprint 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
server.close();
process.exit(failed === 0 ? 0 : 1);
