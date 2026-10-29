#!/usr/bin/env node
/**
 * M4h 蓝图仓库检索测试（离线，本地 GitHub 模拟）。
 *
 * 覆盖：索引缓存、关键词过滤、排序、来源署名、蓝图格式体检、404/坏路径。
 */

import { createServer } from 'node:http';
import { existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';
import { BlueprintClient, createBlueprintTools, inspectBlueprintText, BLUEPRINT_TOOL_NAMES } from '../dist/blueprint/tools.js';
import { BlueprintWorkspace } from '../dist/blueprint/workspace.js';
import { DysonSphereBlueprintsClient } from '../dist/blueprint/site.js';
import { makeTempDir } from './helpers.mjs';

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
const fakeSite = {
  search: async () => [],
  fetch: async () => ({ error: 'not_found' }),
  meta: (url) => ({
    source: 'dysonsphereblueprints',
    url,
    repo: 'https://www.dysonsphereblueprints.com',
    license: '未标注（见蓝图页面）',
    note: 'fake site',
  }),
};
const tools = createBlueprintTools(client, undefined, undefined, undefined, undefined, fakeSite);
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
  const dir = makeTempDir('icarus-bp-');
  const ws = new BlueprintWorkspace({ root: dir });
  const saveTools = createBlueprintTools(client, undefined, undefined, undefined, ws, fakeSite);
  const saveByName = Object.fromEntries(saveTools.map((tool) => [tool.name, tool]));
  const save = async (name, params) => JSON.parse((await saveByName[name].execute('save_1', params)).content[0].text);

  const saved = await save('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt' });
  check('saved=true', saved.saved === true, JSON.stringify(saved));
  check('默认保存到 IcarusII', saved.relPath.startsWith('IcarusII/'), saved.relPath);
  const file = join(dir, saved.relPath);
  check('文件已写入', existsSync(file));
  check('内容与仓库一致', readFileSync(file, 'utf8') === validText);
  check('默认文件名可用', saved.fileName.includes('卡西米尔') && saved.fileName.endsWith('.txt'), saved.fileName);
  check('保留许可', saved.license === 'CC BY-NC-SA 4.0');

  const second = await save('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt' });
  check('同名自动加序号', second.fileName.endsWith('(2).txt'), second.fileName);

  const overwritten = await save('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt', overwrite: true });
  check('overwrite 覆盖原文件', overwritten.fileName === saved.fileName);

  const nested = await save('blueprint_save', {
    path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt',
    dir: 'IcarusII/子目录',
    name: '嵌套',
  });
  check('可保存到任意子目录', nested.relPath === 'IcarusII/子目录/嵌套.txt', nested.relPath);

  const evil = await save('blueprint_save', { path: '产线/卡西米尔晶体/卡西米尔晶体 300/min.txt', name: '../evil/name' });
  check('非法文件名被清洗', !evil.fileName.includes('/') && !evil.fileName.includes('..'), evil.fileName);

  const bad = (await saveByName['blueprint_save'].execute('save_bad', { path: '产线/坏.txt' })).content[0].text;
  check('坏蓝图不保存', bad.includes('[blueprint_invalid]'), bad.slice(0, 120));

  rmSync(dir, { recursive: true, force: true });
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
check('五个蓝图工具', tools.length === 5 && BLUEPRINT_TOOL_NAMES.length === 5);
check('描述带署名', byName['blueprint_search'].description.includes('CC BY-NC-SA 4.0'));

console.log('\n── 6. Dyson Sphere Blueprints 解析 ──');
{
  const siteHtml = `
    <li class="o-blueprint-card factory" data-blueprint-id="1">
      <div class="o-blueprint-card__content"><div><h2><a href="/blueprints/factory-test">Test &amp; Blueprint</a></h2>
      <p>by <a href="/users/1/blueprints">Alice</a></p></div></div>
      <div class="o-blueprint-card__version"><span>Up to version <strong>0.10.35.29151</strong></span></div>
    </li>`;
  const site = new DysonSphereBlueprintsClient({
    baseUrl: 'https://example.test',
    fetchImpl: async () => ({ ok: true, status: 200, text: async () => siteHtml }),
  });
  const rows = await site.search('test', 5);
  check('站点搜索命中', rows.length === 1, JSON.stringify(rows));
  check('站点标题去实体', rows[0]?.title === 'Test & Blueprint', JSON.stringify(rows[0]));
  check('站点 URL 绝对化', rows[0]?.url === 'https://example.test/blueprints/factory-test');
  check('站点作者/版本', rows[0]?.author === 'Alice' && rows[0]?.gameVersion === '0.10.35.29151');

  const page = `<textarea data-preview-target="data" data-clipboard-target="true" readonly>${validText.replace(/"/g, '&quot;')}</textarea>`;
  const siteFetch = new DysonSphereBlueprintsClient({
    baseUrl: 'https://example.test',
    fetchImpl: async () => ({ ok: true, status: 200, text: async () => page }),
  });
  const fetched = await siteFetch.fetch('https://example.test/blueprints/factory-test');
  check('站点蓝图原文解码', fetched.text === validText, JSON.stringify(fetched));
  const blocked = await siteFetch.fetch('https://evil.test/blueprints/x');
  check('拒绝非站点 URL', blocked.error === 'bad_path', JSON.stringify(blocked));
}

console.log('\n── 7. 网站结果进入 search/info/save ──');
{
  const webUrl = 'https://example.test/blueprints/factory-web';
  const webSite = {
    search: async () => [{ title: 'Web Blueprint', url: webUrl, author: 'Bob', gameVersion: '0.10.35.29151' }],
    fetch: async () => ({ text: validText, title: 'Web Blueprint', url: webUrl }),
    meta: (url) => ({
      source: 'dysonsphereblueprints', url, repo: 'https://www.dysonsphereblueprints.com',
      license: '未标注（见蓝图页面）', note: '网站来源',
    }),
  };
  const webTools = createBlueprintTools(client, undefined, undefined, undefined, undefined, webSite);
  const webByName = Object.fromEntries(webTools.map((tool) => [tool.name, tool]));
  const web = JSON.parse((await webByName['blueprint_search'].execute('web_1', { query: '白糖' })).content[0].text);
  check('search 合并两个来源', web.results.some((row) => row.source === 'dysonsphereblueprints'), JSON.stringify(web.results));
  check('网站结果带 url', web.results.find((row) => row.source === 'dysonsphereblueprints')?.url === webUrl);

  const info = JSON.parse((await webByName['blueprint_info'].execute('web_2', { url: webUrl })).content[0].text);
  check('网站 info 成功', info.valid === true && info.source === 'dysonsphereblueprints', JSON.stringify(info));

  const dir = makeTempDir('icarus-bp-web-');
  const ws = new BlueprintWorkspace({ root: dir });
  const saveTools = createBlueprintTools(client, undefined, undefined, undefined, ws, webSite);
  const saveByName = Object.fromEntries(saveTools.map((tool) => [tool.name, tool]));
  const saved = JSON.parse((await saveByName['blueprint_save'].execute('web_3', { url: webUrl })).content[0].text);
  check('网站 save 成功', saved.saved === true && existsSync(join(dir, saved.relPath)), JSON.stringify(saved));
  check('网站 save 用标题作文件名', String(saved.fileName).includes('Web Blueprint'), saved.fileName);
  rmSync(dir, { recursive: true, force: true });
}

console.log(`\n${failed === 0 ? '✅' : '❌'} blueprint 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
server.close();
process.exit(failed === 0 ? 0 : 1);
