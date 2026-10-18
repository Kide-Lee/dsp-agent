#!/usr/bin/env node
/**
 * M4g 中英 Wiki 工具测试（离线，本地 MediaWiki 模拟）。
 *
 *   node test/wiki.mjs
 *
 * 覆盖：双语搜索、HTML 摘要清洗、页面正文、重定向标题、缺失页面、
 * 站点 500、缓存命中、空参数。
 */

import { createServer } from 'node:http';
import { WikiClient } from '../dist/wiki/client.js';
import { createWikiTools, WIKI_TOOL_NAMES } from '../dist/wiki/tools.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const requests = [];
const server = createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  requests.push(`${url.pathname}?${url.searchParams.toString()}`);

  if (!url.pathname.endsWith('/api.php')) {
    res.writeHead(404);
    res.end('not found');
    return;
  }

  const lang = url.pathname.startsWith('/zh') ? 'zh' : 'en';
  const action = url.searchParams.get('action');
  const list = url.searchParams.get('list');
  const query = url.searchParams.get('srsearch') ?? '';

  res.setHeader('content-type', 'application/json; charset=utf-8');

  if (query.includes('失败')) {
    res.writeHead(500);
    res.end(JSON.stringify({ error: 'boom' }));
    return;
  }

  if (action === 'query' && list === 'search') {
    if (lang === 'zh') {
      res.end(JSON.stringify({
        query: {
          search: [
            { title: '增产剂', snippet: '<span class="searchmatch">增产剂</span>可以提升产出' },
            { title: '黑雾', snippet: '黑雾是敌对势力' },
          ],
        },
      }));
    } else {
      res.end(JSON.stringify({
        query: { search: [{ title: 'Proliferator', snippet: 'Increases <b>output</b>' }] },
      }));
    }
    return;
  }

  if (action === 'query' && url.searchParams.get('prop') === 'extracts') {
    const title = url.searchParams.get('titles') ?? '';
    if (title === '不存在') {
      res.end(JSON.stringify({ query: { pages: [{ title, missing: true }] } }));
      return;
    }
    if (title.toLowerCase() === 'proliferator') {
      res.end(JSON.stringify({
        query: { pages: [{ title: 'Proliferator', extract: 'Proliferator is an item.' }] },
      }));
      return;
    }
    res.end(JSON.stringify({
      query: {
        pages: [{
          title: '增产剂',
          extract: `${'增产剂是一种消耗品。'.repeat(1000)}`,
        }],
      },
    }));
    return;
  }

  res.writeHead(400);
  res.end('{}');
});

await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port;
const base = `http://127.0.0.1:${port}`;

const client = new WikiClient({
  zhApi: `${base}/zh/api.php`,
  enApi: `${base}/en/api.php`,
  timeoutMs: 3000,
  cacheTtlMs: 60000,
});
const tools = createWikiTools(client);
const byName = Object.fromEntries(tools.map((tool) => [tool.name, tool]));

async function run(name, params) {
  const result = await byName[name].execute('call_1', params, undefined);
  return result.content[0].text;
}

// ---------------------------------------------------------------- 1. 双语搜索

console.log('── 1. wiki_search ──');
{
  const before = requests.length;
  const text = await run('wiki_search', { query: '增产剂', lang: 'both', limit: 5 });
  const payload = JSON.parse(text);
  check('ok', payload.ok !== false && Array.isArray(payload.hits), text.slice(0, 160));
  check('中英合并 3 条', payload.hits.length === 3, String(payload.hits.length));
  check('中文标题', payload.hits[0].title === '增产剂');
  check('HTML 被清洗', !payload.hits[0].snippet.includes('<span'), payload.hits[0].snippet);
  check('中文链接', payload.hits[0].url.includes('/zh/wiki/'), payload.hits[0].url);
  check('英文链接', payload.hits[2].url.includes('/en/wiki/'), payload.hits[2].url);
  check('带来源说明', String(payload.note).includes('url'));
  const calls = requests.length - before;

  const again = JSON.parse(await run('wiki_search', { query: '增产剂', lang: 'both', limit: 5 }));
  check('二次搜索命中缓存', again.hits.length === 3 && requests.length - before === calls);
}

{
  const payload = JSON.parse(await run('wiki_search', { query: '增产剂', lang: 'zh' }));
  check('lang=zh 只查中文', payload.hits.length === 2 && payload.hits.every((hit) => hit.lang === 'zh'));
}

// ---------------------------------------------------------------- 2. 页面

console.log('\n── 2. wiki_page ──');
{
  const payload = JSON.parse(await run('wiki_page', { title: '增产剂' }));
  check('正文非空', typeof payload.extract === 'string' && payload.extract.length > 100);
  check('长正文被截断', payload.truncated === true && payload.extract.length <= 8100, String(payload.extract.length));
  check('附来源链接', payload.url.includes('/zh/wiki/'));
  check('标题保留', payload.title === '增产剂');

  const en = JSON.parse(await run('wiki_page', { title: 'Proliferator', lang: 'en' }));
  check('英文页面', en.extract === 'Proliferator is an item.' && en.lang === 'en');
}

{
  const text = await run('wiki_page', { title: '不存在' });
  check('缺页报 wiki_not_found', text.includes('[wiki_not_found]'), text.slice(0, 120));
}

// ---------------------------------------------------------------- 3. 失败与参数

console.log('\n── 3. 失败与参数 ──');
{
  const text = await run('wiki_search', { query: '失败' });
  check('站点 500 报 wiki_unavailable', text.includes('[wiki_unavailable]'), text.slice(0, 120));
}

{
  const text = await run('wiki_search', { query: '   ' });
  check('空 query 报 bad_request', text.includes('[bad_request]'));
  const page = await run('wiki_page', { title: '' });
  check('空 title 报 bad_request', page.includes('[bad_request]'));
}

// ---------------------------------------------------------------- 4. 元数据

console.log('\n── 4. 工具元数据 ──');
check('两个 wiki 工具', tools.length === 2 && WIKI_TOOL_NAMES.length === 2);
check('wiki_search 描述提到 Wiki', byName['wiki_search'].description.includes('Wiki'));

console.log(`\n${failed === 0 ? '✅' : '❌'} wiki 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
server.close();
process.exit(failed === 0 ? 0 : 1);
