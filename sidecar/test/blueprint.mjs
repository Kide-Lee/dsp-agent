#!/usr/bin/env node
/**
 * M4h 蓝图仓库检索测试（离线，本地 GitHub 模拟）。
 *
 * 覆盖：索引缓存、关键词过滤、排序、来源署名、蓝图格式体检、404/坏路径。
 */

import { createServer } from 'node:http';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
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

console.log('\n── 4. 保存到游戏蓝图目录 ──');
{
  const dir = mkdtempSync(join(tmpdir(), 'icarus-bp-'));
  process.env.ICARUS_BLUEPRINT_DIR = dir;
  const saved = JSON.parse(await run('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt' }));
  check('saved=true', saved.saved === true, JSON.stringify(saved));
  const file = join(dir, saved.fileName);
  check('文件已写入', existsSync(file));
  check('内容与仓库一致', readFileSync(file, 'utf8') === validText);
  check('默认文件名可用', saved.fileName.includes('卡西米尔') && saved.fileName.endsWith('.txt'), saved.fileName);
  check('保留许可', saved.license === 'CC BY-NC-SA 4.0');

  const second = JSON.parse(await run('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt' }));
  check('同名自动加序号', second.fileName.endsWith('(2).txt'), second.fileName);

  const overwritten = JSON.parse(await run('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt', overwrite: true }));
  check('overwrite 覆盖原文件', overwritten.fileName === saved.fileName);

  const evil = JSON.parse(await run('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt', name: '../evil/name' }));
  check('非法文件名被清洗', !evil.fileName.includes('/') && !evil.fileName.includes('..'), evil.fileName);

  const bad = await run('blueprint_save', { path: '产线/坏.txt' });
  check('坏蓝图不保存', bad.includes('[blueprint_invalid]'));

  rmSync(dir, { recursive: true, force: true });
  delete process.env.ICARUS_BLUEPRINT_DIR;
}

console.log('\n── 5. header/二进制元数据（卡片用） ──');
{
  // 按 BlueprintData.headerStr + Import 的字段顺序造一份 v1 蓝图：
  // version/cursor/dragBox/primaryArea 7×int32 + areaCount(1) + area(14) + buildingCount(4)
  const makeBlueprint = ({ name, author, version, desc, width, height, buildings }) => {
    const header = `BLUEPRINT:1,10,1206,0,0,0,0,0,0,9.9.9,${encodeURIComponent(name)},`
      + `${encodeURIComponent(author)},${encodeURIComponent(version)},,${encodeURIComponent(desc)}`;
    const areaCount = 1;
    const binary = Buffer.alloc(29 + areaCount * 14 + 4);
    binary.writeInt32LE(2, 0);
    binary.writeInt32LE(width, 16);
    binary.writeInt32LE(height, 20);
    binary[28] = areaCount;
    binary.writeInt32LE(buildings, 29 + areaCount * 14);
    return `${header}"${gzipSync(binary).toString('base64')}"${'c'.repeat(32)}`;
  };

  const summary = inspectBlueprintText(makeBlueprint({
    name: '电磁涡轮 20',
    author: '自动生成',
    version: '1.2.3',
    desc: '一条测试产线',
    width: 12,
    height: 5,
    buildings: 24,
  }));
  check('v1 名称', summary.name === '电磁涡轮 20', String(summary.name));
  check('作者', summary.author === '自动生成', String(summary.author));
  check('自定义版本', summary.version === '1.2.3', String(summary.version));
  check('介绍', summary.description === '一条测试产线', String(summary.description));
  check('图标物品', summary.iconItemId === 1206, String(summary.iconItemId));
  check('建筑数', summary.buildings === 24, String(summary.buildings));
  check('占地', summary.width === 12 && summary.height === 5);
  check('老格式不误报元数据', inspectBlueprintText(validText).name === undefined);
}

console.log('\n── 6. 元数据 ──');
check('四个蓝图工具', tools.length === 4 && BLUEPRINT_TOOL_NAMES.length === 4);
check('描述带署名', byName['blueprint_search'].description.includes('CC BY-NC-SA 4.0'));

console.log(`\n${failed === 0 ? '✅' : '❌'} blueprint 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
server.close();
process.exit(failed === 0 ? 0 : 1);
