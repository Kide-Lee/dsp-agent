/**
 * Dyson Sphere Blueprints 网站检索（无官方 JSON API，解析 HTML）。
 *
 * 站点页面结构：搜索页每张卡片是 `li.o-blueprint-card`，详情页把蓝图原文放在
 * `textarea[data-clipboard-target="true"]` 里。解析失败时宁可返回空结果/错误，
 * 不猜测内容。
 */

export const DSB_BASE = 'https://www.dysonsphereblueprints.com';

const MAX_PAGE_BYTES = 4 * 1024 * 1024;
const MAX_BLUEPRINT_BYTES = 2 * 1024 * 1024;

export interface DsbSearchRow {
  title: string;
  url: string;
  author?: string;
  gameVersion?: string;
}

export interface DsbMeta {
  source: 'dysonsphereblueprints';
  url: string;
  repo: string;
  license: string;
  note: string;
}

export interface DsbFetchResult {
  text?: string;
  title?: string;
  url?: string;
  error?: string;
}

export class DysonSphereBlueprintsClient {
  private readonly fetchImpl: typeof fetch;
  private readonly baseUrl: string;
  private readonly timeoutMs: number;

  constructor(options: { fetchImpl?: typeof fetch; baseUrl?: string; timeoutMs?: number } = {}) {
    this.fetchImpl = options.fetchImpl ?? fetch;
    this.baseUrl = (options.baseUrl ?? DSB_BASE).replace(/\/$/, '');
    this.timeoutMs = options.timeoutMs ?? 10000;
  }

  private absolute(url: string): string {
    const parsed = new URL(url, `${this.baseUrl}/`);
    const host = parsed.hostname.toLowerCase();
    const baseHost = new URL(this.baseUrl).hostname.toLowerCase();
    if (
      parsed.protocol !== 'https:' ||
      (host !== baseHost && host !== 'dysonsphereblueprints.com' && host !== 'www.dysonsphereblueprints.com')
    ) {
      throw new Error('host_not_allowed');
    }
    return parsed.toString();
  }

  async search(query: string, limit: number): Promise<DsbSearchRow[]> {
    const url = `${this.baseUrl}/en/blueprints?search=${encodeURIComponent(query)}`;
    const response = await this.fetchImpl(url, {
      signal: AbortSignal.timeout(this.timeoutMs),
      headers: { accept: 'text/html', 'user-agent': 'icarus-ii/0.1' },
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const html = await response.text();
    if (html.length > MAX_PAGE_BYTES) throw new Error('页面过大');
    const chunks = html.split(/<li class="o-blueprint-card(?=\s|")/).slice(1);
    const rows: DsbSearchRow[] = [];

    for (const chunk of chunks) {
      const href = /<h2>\s*<a[^>]*href="([^"]+)"/i.exec(chunk)?.[1];
      const title = /<h2>\s*<a[^>]*>([\s\S]*?)<\/a>/i.exec(chunk)?.[1];
      if (!href || !title) continue;
      const author = /<p>\s*by\s*<a[^>]*>([\s\S]*?)<\/a>/i.exec(chunk)?.[1];
      const gameVersion = /Up to version\s*<strong>([^<]+)<\/strong>/i.exec(chunk)?.[1];
      const cleanTitle = textOf(title);
      if (!cleanTitle) continue;
      rows.push({
        title: cleanTitle,
        url: this.absolute(href),
        ...(author ? { author: textOf(author) } : {}),
        ...(gameVersion ? { gameVersion: gameVersion.trim() } : {}),
      });
      if (rows.length >= limit) break;
    }
    return rows;
  }

  async fetch(url: string): Promise<DsbFetchResult> {
    let target: string;
    try {
      target = this.absolute(url);
    } catch {
      return { error: 'bad_path' };
    }

    const response = await this.fetchImpl(target, {
      signal: AbortSignal.timeout(this.timeoutMs),
      headers: { accept: 'text/html', 'user-agent': 'icarus-ii/0.1' },
    });
    if (response.status === 404) return { error: 'not_found' };
    if (!response.ok) return { error: `HTTP ${response.status}` };

    const html = await response.text();
    if (html.length > MAX_PAGE_BYTES) return { error: 'too_large' };
    const match = /<textarea[^>]*data-clipboard-target="true"[^>]*>([\s\S]*?)<\/textarea>/i.exec(html);
    if (!match) return { error: 'no_blueprint' };

    const text = decodeEntities(match[1] ?? '').trim();
    if (!text.startsWith('BLUEPRINT:')) return { error: 'not_blueprint' };
    if (text.length > MAX_BLUEPRINT_BYTES) return { error: 'too_large' };

    const rawTitle = /<title>([\s\S]*?)<\/title>/i.exec(html)?.[1];
    const title = rawTitle ? textOf(rawTitle).replace(/^Dyson Sphere Blueprints\s*-\s*/i, '') : undefined;
    return { text, url: target, ...(title ? { title } : {}) };
  }

  meta(url: string): DsbMeta {
    return {
      source: 'dysonsphereblueprints',
      url: this.absolute(url),
      repo: this.baseUrl,
      license: '未标注（见蓝图页面）',
      note: '来源 Dyson Sphere Blueprints；蓝图版权归原作者，使用前查看蓝图页面说明。',
    };
  }
}

function textOf(value: string): string {
  return decodeEntities(value.replace(/<[^>]*>/g, ' ')).replace(/\s+/g, ' ').trim();
}

function decodeEntities(value: string): string {
  const named: Record<string, string> = {
    amp: '&',
    apos: "'",
    gt: '>',
    lt: '<',
    quot: '"',
  };
  return value.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (match, entity: string) => {
    if (entity[0] === '#') {
      const hex = entity[1]?.toLowerCase() === 'x';
      const code = Number.parseInt(entity.slice(hex ? 2 : 1), hex ? 16 : 10);
      return Number.isFinite(code) && code > 0 && code <= 0x10ffff
        ? String.fromCodePoint(code)
        : match;
    }
    return named[entity.toLowerCase()] ?? match;
  });
}
