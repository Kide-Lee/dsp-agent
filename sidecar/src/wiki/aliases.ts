/**
 * 社区别名解析（A3）。
 *
 * 玩家口中的“蓝马达/绿马达/白糖”不是游戏原型名，直接 `kb.resolveItem` 会失败。
 * 做法分三层：
 *   1. 本地 curated 表：覆盖最常见的社区叫法，离线可用；
 *   2. Wiki：用 `insource:"别名"` 搜到物品页，再读 `|别称=` 校验；
 *   3. 缓存：把命中的映射写到 `<dataDir>/wiki-aliases.json`，30 天内不再联网。
 *
 * 约束：
 *  - Wiki 是**外部不可信数据**，只用来做“别名 → 已有 KB 物品”的映射；
 *    映射结果必须能被 `kb.resolveItem` 精确命中，否则丢弃。
 *  - 多个候选时不猜，返回 candidates 让调用方问玩家。
 *  - Wiki 不可用/超时只影响别名，不阻断蓝图生成（curated 仍生效）。
 */

import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import type { KnowledgeBase } from '../kb/knowledge.js';
import type { KbItem } from '../kb/types.js';
import type { WikiClient } from './client.js';

export type AliasSource = 'kb' | 'curated' | 'wiki' | 'cache' | 'none';

export interface AliasCandidate {
  id: number;
  name: string;
  evidence: string;
}

export interface AliasResolution {
  input: string;
  item?: KbItem;
  /** 命中的规范名（通常等于 item.name）。 */
  alias?: string;
  source: AliasSource;
  candidates: AliasCandidate[];
  note?: string;
}

/** 社区常见叫法；键会在查询时做 normalize。 */
const CURATED_ALIASES: Record<string, string> = {
  蓝马达: '超级磁场环',
  绿马达: '电磁涡轮',
  白糖: '宇宙矩阵',
  红糖: '能量矩阵',
  蓝糖: '电磁矩阵',
  黄糖: '结构矩阵',
  紫糖: '信息矩阵',
  绿糖: '引力矩阵',
};

const DEFAULT_TTL_MS = 30 * 24 * 60 * 60 * 1000;
const MAX_CANDIDATES = 3;

interface CacheEntry {
  itemId: number;
  name: string;
  at: number;
}

interface CacheFile {
  version: 1;
  entries: Record<string, CacheEntry>;
}

export function normalizeAlias(value: string): string {
  return value
    .normalize('NFKC')
    .toLowerCase()
    .replace(/[\s\p{P}\p{S}]+/gu, '');
}

/** 解析物品模板里的 `|别称=` / `|别名=`；英文 wiki 的 aka/alias 也一并兼容。 */
export function parseWikiAliases(wikitext: string): string[] {
  const out = new Set<string>();
  for (const match of wikitext.matchAll(/\|\s*(?:别称|别名|aka|alias)\s*=\s*([^\n|]+)/gi)) {
    const raw = match[1] ?? '';
    const cleaned = raw
      .replace(/<[^>]*>/g, '')
      .replace(/\[\[([^\]|]+)(?:\|[^\]]*)?\]\]/g, '$1')
      .replace(/\{\{[^}]*\}\}/g, '');
    for (const part of cleaned.split(/[、,，/;；|]/)) {
      const value = part.trim();
      if (value.length > 0) out.add(value);
    }
  }
  return [...out];
}

export interface AliasResolverOptions {
  wiki?: WikiClient;
  /** 持久化缓存路径；不传则只用内存。 */
  cacheFile?: string;
  ttlMs?: number;
  /** 便于测试控制时间。 */
  now?: () => number;
}

export class AliasResolver {
  private readonly wiki: WikiClient | undefined;
  private readonly cacheFile: string | undefined;
  private readonly ttlMs: number;
  private readonly now: () => number;
  private readonly memory = new Map<string, CacheEntry>();

  constructor(options: AliasResolverOptions = {}) {
    this.wiki = options.wiki;
    this.cacheFile = options.cacheFile;
    this.ttlMs = options.ttlMs ?? DEFAULT_TTL_MS;
    this.now = options.now ?? Date.now;
    this.load();
  }

  private load(): void {
    if (!this.cacheFile || !existsSync(this.cacheFile)) return;
    try {
      const parsed = JSON.parse(readFileSync(this.cacheFile, 'utf8')) as Partial<CacheFile>;
      if (parsed.version !== 1 || !parsed.entries || typeof parsed.entries !== 'object') return;
      for (const [key, value] of Object.entries(parsed.entries)) {
        if (!value || typeof value !== 'object') continue;
        const entry = value as Partial<CacheEntry>;
        if (typeof entry.itemId !== 'number' || typeof entry.at !== 'number') continue;
        this.memory.set(key, {
          itemId: entry.itemId,
          name: typeof entry.name === 'string' ? entry.name : '',
          at: entry.at,
        });
      }
    } catch {
      // 缓存坏了就当没有；下次会重建。
    }
  }

  private persist(): void {
    if (!this.cacheFile) return;
    try {
      mkdirSync(dirname(this.cacheFile), { recursive: true });
      const entries: Record<string, CacheEntry> = {};
      for (const [key, value] of this.memory) entries[key] = value;
      const tmp = `${this.cacheFile}.tmp-${process.pid}`;
      writeFileSync(tmp, `${JSON.stringify({ version: 1, entries }, null, 2)}\n`, 'utf8');
      renameSync(tmp, this.cacheFile);
    } catch {
      // 缓存写失败不影响本次解析。
    }
  }

  async resolve(kb: KnowledgeBase, input: string): Promise<AliasResolution> {
    const term = input.trim();
    if (!term) return { input: term, source: 'kb', candidates: [] };

    const exact = kb.resolveItem(term);
    if (exact) return { input: term, item: exact, alias: exact.name, source: 'kb', candidates: [] };

    const norm = normalizeAlias(term);
    const curated = CURATED_ALIASES[norm];
    if (curated) {
      const item = kb.resolveItem(curated);
      if (item) {
        return { input: term, item, alias: item.name, source: 'curated', candidates: [] };
      }
    }

    const cached = this.memory.get(norm);
    if (cached && this.now() - cached.at <= this.ttlMs) {
      const item = kb.resolveItem(cached.itemId);
      if (item) {
        return { input: term, item, alias: cached.name || item.name, source: 'cache', candidates: [] };
      }
    }

    if (!this.wiki) return { input: term, source: 'none', candidates: [] };

    const candidates = new Map<number, AliasCandidate>();
    const add = (item: KbItem, evidence: string): void => {
      if (!candidates.has(item.id)) {
        candidates.set(item.id, { id: item.id, name: item.name, evidence: evidence.slice(0, 160) });
      }
    };

    // 1) insource 精确搜：只有 wikitext 里真的写了这个别名才算数。
    try {
      const { hits } = await this.wiki.search(`insource:"${term.replace(/"/g, '')}"`, ['zh'], MAX_CANDIDATES);
      for (const hit of hits.slice(0, MAX_CANDIDATES)) {
        const item = kb.resolveItem(stripTitleSuffix(hit.title));
        if (!item) continue;
        const page = await this.wiki.wikitext(hit.title, 'zh');
        const aliases = page.page ? parseWikiAliases(page.page.text) : [];
        if (aliases.some((alias) => normalizeAlias(alias) === norm)) {
          add(item, `别称 ${aliases.join('、')}`);
        }
      }
    } catch {
      // wiki 不可用时继续走正常搜索/curated；不抛。
    }

    // 2) 普通搜索：摘要里直接出现别名时也算证据；否则读一次 wikitext。
    if (candidates.size === 0) {
      try {
        const { hits } = await this.wiki.search(term, ['zh'], MAX_CANDIDATES);
        for (const hit of hits.slice(0, MAX_CANDIDATES)) {
          const item = kb.resolveItem(stripTitleSuffix(hit.title));
          if (!item) continue;
          if (normalizeAlias(hit.snippet).includes(norm)) {
            add(item, hit.snippet);
            continue;
          }
          const page = await this.wiki.wikitext(hit.title, 'zh');
          const aliases = page.page ? parseWikiAliases(page.page.text) : [];
          if (aliases.some((alias) => normalizeAlias(alias) === norm)) {
            add(item, `别称 ${aliases.join('、')}`);
          }
        }
      } catch {
        // 同上。
      }
    }

    if (candidates.size === 1) {
      const found = [...candidates.values()][0];
      if (!found) return { input: term, source: 'none', candidates: [] };
      const item = kb.resolveItem(found.id);
      if (!item) return { input: term, source: 'none', candidates: [] };
      this.memory.set(norm, { itemId: item.id, name: item.name, at: this.now() });
      this.persist();
      return { input: term, item, alias: item.name, source: 'wiki', candidates: [] };
    }

    if (candidates.size > 1) {
      return {
        input: term,
        source: 'none',
        candidates: [...candidates.values()].sort((a, b) => a.name.localeCompare(b.name)),
        note: '别名对应多个物品，请让玩家确认',
      };
    }

    return { input: term, source: 'none', candidates: [] };
  }
}

function stripTitleSuffix(title: string): string {
  return title.replace(/\s*[（(][^）)]*[）)]\s*$/, '').trim();
}
