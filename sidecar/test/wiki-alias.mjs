#!/usr/bin/env node
/**
 * 社区别名解析测试（离线，假 Wiki）。
 *
 * 覆盖：
 *   - 精确 KB 名直接命中
 *   - curated 表：蓝马达/绿马达/白糖
 *   - Wiki insource + `|别称=` 解析
 *   - 命中后写缓存；第二次不再联网
 *   - 多候选不猜，返回 candidates
 *   - Wiki 不可用时不阻断（返回 none）
 */

import { readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { KnowledgeBase, parseKbData } from '../dist/kb/knowledge.js';
import { AliasResolver, normalizeAlias, parseWikiAliases } from '../dist/wiki/aliases.js';
import { makeTempDir } from './helpers.mjs';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const fixture = {
  schemaVersion: 1,
  gameVersion: 'test',
  locale: 'zh-CN',
  items: [
    { id: 1204, name: '电磁涡轮' },
    { id: 1205, name: '超级磁场环' },
    { id: 6002, name: '能量矩阵' },
    { id: 6006, name: '宇宙矩阵' },
    { id: 6003, name: '结构矩阵' },
  ],
  recipes: [],
  techs: [],
  models: [],
  veins: [],
  signals: [],
  texts: [],
};
const kb = new KnowledgeBase(parseKbData(fixture));

class FakeWiki {
  constructor() {
    this.searchCalls = 0;
    this.textCalls = 0;
    this.fail = false;
    this.aliases = {
      电磁涡轮: '绿电机',
      超级磁场环: '蓝马达',
      宇宙矩阵: '白糖',
      结构矩阵: '黄糖',
    };
  }

  async search(query) {
    this.searchCalls += 1;
    if (this.fail) throw new Error('offline');
    if (!query.startsWith('insource:')) return { hits: [], errors: [] };
    const term = query.replace(/^insource:"?|"?$/g, '');
    const hits = Object.entries(this.aliases)
      .filter(([, alias]) => alias === term)
      .map(([title, alias]) => ({ lang: 'zh', title, snippet: `别称 ${alias}`, url: '' }));
    return { hits, errors: [] };
  }

  async wikitext(title) {
    this.textCalls += 1;
    if (this.fail) return { error: 'offline' };
    const alias = this.aliases[title];
    if (!alias) return { error: 'not_found' };
    return {
      page: {
        lang: 'zh',
        title,
        url: '',
        text: `{{物品\n|名称=${title}\n|别称=${alias}\n}}`,
        truncated: false,
      },
    };
  }
}

const root = makeTempDir('icarus-ii-alias-');
const cacheFile = join(root, 'wiki-aliases.json');

console.log('── 1. 解析工具函数 ──');
{
  check('normalize 去空格/标点', normalizeAlias('  蓝 马-达 ') === '蓝马达');
  const aliases = parseWikiAliases('{{物品\n|名称=电磁涡轮\n|别称=绿马达、绿电机\n}}');
  check('解析别称列表', aliases.includes('绿马达') && aliases.includes('绿电机'), JSON.stringify(aliases));
  check('解析别名/aka', parseWikiAliases('|别名=小绿|aka=green motor').includes('green motor'));
}

console.log('\n── 2. 精确名与 curated ──');
{
  const wiki = new FakeWiki();
  const resolver = new AliasResolver({ wiki, cacheFile });
  const exact = await resolver.resolve(kb, '电磁涡轮');
  check('精确名 source=kb', exact.source === 'kb' && exact.item?.id === 1204, JSON.stringify(exact));
  const blue = await resolver.resolve(kb, '蓝马达');
  check('蓝马达 → 超级磁场环', blue.item?.id === 1205 && blue.source === 'curated', JSON.stringify(blue));
  check('curated 不联网', wiki.searchCalls === 0 && wiki.textCalls === 0);
}

console.log('\n── 3. Wiki insource + 别称 ──');
{
  const wiki = new FakeWiki();
  const resolver = new AliasResolver({ wiki, cacheFile });
  const curatedGreen = await resolver.resolve(kb, '绿马达');
  check('curated 绿马达 → 电磁涡轮', curatedGreen.item?.id === 1204 && curatedGreen.source === 'curated', JSON.stringify(curatedGreen));

  const green = await resolver.resolve(kb, '绿电机');
  check('Wiki 绿电机 → 电磁涡轮', green.item?.id === 1204 && green.source === 'wiki', JSON.stringify(green));
  check('读了 wikitext', wiki.textCalls >= 1);
  check('缓存已落盘', existsSync(cacheFile));

  const wikiOffline = new FakeWiki();
  wikiOffline.fail = true;
  const cached = new AliasResolver({ wiki: wikiOffline, cacheFile, now: () => Date.now() });
  const again = await cached.resolve(kb, '绿电机');
  check('第二次走缓存', again.source === 'cache' && again.item?.id === 1204, JSON.stringify(again));
  check('缓存命中不联网', wikiOffline.searchCalls === 0);
}

console.log('\n── 4. 多候选 / 不可用 ──');
{
  const wiki = new FakeWiki();
  wiki.aliases = { 电磁涡轮: '马达', 超级磁场环: '马达' };
  const resolver = new AliasResolver({ wiki });
  const ambiguous = await resolver.resolve(kb, '马达');
  check('多候选不猜', ambiguous.item === undefined && ambiguous.candidates.length === 2, JSON.stringify(ambiguous));
  check('候选带证据', ambiguous.candidates.every((row) => typeof row.evidence === 'string'));

  const offline = new FakeWiki();
  offline.fail = true;
  const none = new AliasResolver({ wiki: offline }).resolve(kb, '不存在的叫法');
  check('Wiki 不可用时返回 none', (await none).source === 'none');
}

rmSync(root, { recursive: true, force: true });

if (failed > 0) {
  console.error(`\n❌ wiki-alias 测试失败：${failed} 项`);
  process.exit(1);
}
console.log(`\n✅ wiki-alias 测试全部通过（${passed} 项）`);
