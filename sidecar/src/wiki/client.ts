/**
 * 中英 DSP Wiki 客户端（M4g）。
 *
 * 两个站点都是 MediaWiki，用同一套 `api.php`：
 *  - 中文：wiki.biligame.com/dsp
 *  - 英文：dsp-wiki.com
 *
 * 约束：
 *  - 只读、只发 `action=query`；不登录、不写、不抓整站。
 *  - 超时 8s、单次响应上限 2MB、内存缓存 30 分钟（最多 200 条）。
 *  - 失败必须显式返回错误，绝不用模型记忆补内容（wiki 数据可能过时/翻译不同）。
 */

export type WikiLang = 'zh' | 'en';

export interface WikiSearchHit {
  lang: WikiLang;
  title: string;
  snippet: string;
  url: string;
}

export interface WikiPageResult {
  lang: WikiLang;
  title: string;
  url: string;
  extract: string;
  truncated: boolean;
}

/** 页面 wikitext；别名解析用它读 `|别称=`，比 extracts 可靠（物品页 extract 常为空）。 */
export interface WikiTextResult {
  lang: WikiLang;
  title: string;
  url: string;
  text: string;
  truncated: boolean;
}

export interface WikiClientOptions {
  fetchImpl?: typeof fetch;
  zhApi?: string;
  enApi?: string;
  timeoutMs?: number;
  cacheTtlMs?: number;
}

const DEFAULTS = {
  zhApi: 'https://wiki.biligame.com/dsp/api.php',
  enApi: 'https://dsp-wiki.com/api.php',
};

const MAX_RESPONSE_BYTES = 2 * 1024 * 1024;
const DEFAULT_TIMEOUT_MS = 8000;
const DEFAULT_TTL_MS = 30 * 60 * 1000;
const MAX_CACHE_ENTRIES = 200;
const MAX_EXTRACT_CHARS = 8000;

interface CacheEntry {
  at: number;
  value: unknown;
}

function stripHtml(text: string): string {
  return text
    .replace(/<[^>]*>/g, '')
    .replace(/&quot;/g, '"')
    .replace(/&#039;/g, "'")
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&nbsp;/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * 页面 URL。biligame 的 DSP wiki 使用 `/dsp/<页面名>`；
 * 如果拼成 `/dsp/wiki/<页面名>`，站点会 301 到 `/dsp/Wiki/<页面名>`，
 * 玩家看到的就是带 `/Wiki/` 的错误地址。英文 dsp-wiki 仍是标准 `/wiki/`。
 */
export function pageUrl(base: string, title: string): string {
  const slug = encodeURIComponent(title.replace(/ /g, '_'));
  if (base.toLowerCase().includes('wiki.biligame.com')) return `${base}/${slug}`;
  return `${base}/wiki/${slug}`;
}

export class WikiClient {
  private readonly fetchImpl: typeof fetch;
  private readonly apis: Record<WikiLang, string>;
  private readonly bases: Record<WikiLang, string>;
  private readonly timeoutMs: number;
  private readonly cacheTtlMs: number;
  private readonly cache = new Map<string, CacheEntry>();

  constructor(options: WikiClientOptions = {}) {
    this.fetchImpl = options.fetchImpl ?? fetch;
    this.apis = {
      zh: options.zhApi ?? DEFAULTS.zhApi,
      en: options.enApi ?? DEFAULTS.enApi,
    };
    this.bases = {
      zh: this.apis.zh.replace(/\/api\.php$/, ''),
      en: this.apis.en.replace(/\/api\.php$/, ''),
    };
    this.timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    this.cacheTtlMs = options.cacheTtlMs ?? DEFAULT_TTL_MS;
  }

  private cacheGet<T>(key: string): T | null {
    const hit = this.cache.get(key);
    if (!hit) return null;
    if (Date.now() - hit.at > this.cacheTtlMs) {
      this.cache.delete(key);
      return null;
    }
    return hit.value as T;
  }

  private cacheSet(key: string, value: unknown): void {
    if (this.cache.size >= MAX_CACHE_ENTRIES) {
      const oldest = this.cache.keys().next().value;
      if (oldest !== undefined) this.cache.delete(oldest);
    }
    this.cache.set(key, { at: Date.now(), value });
  }

  private async request(lang: WikiLang, params: Record<string, string>): Promise<unknown> {
    const url = new URL(this.apis[lang]);
    url.searchParams.set('format', 'json');
    url.searchParams.set('formatversion', '2');
    url.searchParams.set('utf8', '1');
    for (const [key, value] of Object.entries(params)) url.searchParams.set(key, value);

    const response = await this.fetchImpl(url.toString(), {
      signal: AbortSignal.timeout(this.timeoutMs),
      headers: { accept: 'application/json', 'user-agent': 'icarus-ii/0.1 (DSP in-game assistant)' },
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const length = Number(response.headers.get('content-length') ?? '0');
    if (length > MAX_RESPONSE_BYTES) throw new Error('响应过大');
    const text = await response.text();
    if (text.length > MAX_RESPONSE_BYTES) throw new Error('响应过大');
    return JSON.parse(text) as unknown;
  }

  async search(
    query: string,
    langs: readonly WikiLang[],
    limit: number,
  ): Promise<{ hits: WikiSearchHit[]; errors: { lang: WikiLang; error: string }[] }> {
    const trimmed = query.trim();
    const hits: WikiSearchHit[] = [];
    const errors: { lang: WikiLang; error: string }[] = [];
    if (!trimmed) return { hits, errors };

    for (const lang of langs) {
      const cacheKey = `s:${lang}:${limit}:${trimmed}`;
      const cached = this.cacheGet<WikiSearchHit[]>(cacheKey);
      if (cached) {
        hits.push(...cached);
        continue;
      }
      try {
        const raw = (await this.request(lang, {
          action: 'query',
          list: 'search',
          srsearch: trimmed,
          srlimit: String(limit),
        })) as { query?: { search?: unknown } };
        const list = Array.isArray(raw.query?.search) ? raw.query.search : [];
        const mapped: WikiSearchHit[] = [];
        for (const item of list) {
          if (!item || typeof item !== 'object') continue;
          const row = item as { title?: unknown; snippet?: unknown };
          if (typeof row.title !== 'string' || row.title.length === 0) continue;
          mapped.push({
            lang,
            title: row.title,
            snippet: typeof row.snippet === 'string' ? stripHtml(row.snippet) : '',
            url: pageUrl(this.bases[lang], row.title),
          });
        }
        this.cacheSet(cacheKey, mapped);
        hits.push(...mapped);
      } catch (err) {
        errors.push({ lang, error: err instanceof Error ? err.message : String(err) });
      }
    }
    return { hits, errors };
  }

  async page(
    title: string,
    lang: WikiLang,
  ): Promise<{ page?: WikiPageResult; error?: string }> {
    const trimmed = title.trim();
    if (!trimmed) return { error: 'empty_title' };
    const cacheKey = `p:${lang}:${trimmed}`;
    const cached = this.cacheGet<WikiPageResult>(cacheKey);
    if (cached) return { page: cached };

    try {
      const raw = (await this.request(lang, {
        action: 'query',
        prop: 'extracts',
        explaintext: '1',
        redirects: '1',
        titles: trimmed,
      })) as { query?: { pages?: unknown } };
      const pages = Array.isArray(raw.query?.pages) ? raw.query.pages : [];
      const first = pages[0] as { title?: unknown; extract?: unknown; missing?: unknown } | undefined;
      if (!first || first.missing === true) return { error: 'not_found' };
      const actualTitle = typeof first.title === 'string' ? first.title : trimmed;
      const extractRaw = typeof first.extract === 'string' ? first.extract : '';
      const truncated = extractRaw.length > MAX_EXTRACT_CHARS;
      const page: WikiPageResult = {
        lang,
        title: actualTitle,
        url: pageUrl(this.bases[lang], actualTitle),
        extract: truncated ? `${extractRaw.slice(0, MAX_EXTRACT_CHARS)}\n…（已截断）` : extractRaw,
        truncated,
      };
      this.cacheSet(cacheKey, page);
      return { page };
    } catch (err) {
      return { error: err instanceof Error ? err.message : String(err) };
    }
  }

  /**
   * 读取页面 wikitext（`action=parse&prop=wikitext`）。
   * 只用于解析物品模板里的 `|别称=`；不作为模型可见正文。
   */
  async wikitext(
    title: string,
    lang: WikiLang,
  ): Promise<{ page?: WikiTextResult; error?: string }> {
    const trimmed = title.trim();
    if (!trimmed) return { error: 'empty_title' };
    const cacheKey = `w:${lang}:${trimmed}`;
    const cached = this.cacheGet<WikiTextResult>(cacheKey);
    if (cached) return { page: cached };

    try {
      const raw = (await this.request(lang, {
        action: 'parse',
        page: trimmed,
        prop: 'wikitext',
        redirects: '1',
      })) as { parse?: { title?: unknown; wikitext?: unknown }; error?: { info?: unknown } };
      if (!raw.parse || typeof raw.parse.wikitext !== 'string') {
        return { error: typeof raw.error?.info === 'string' ? raw.error.info : 'not_found' };
      }
      const actualTitle = typeof raw.parse.title === 'string' ? raw.parse.title : trimmed;
      const text = raw.parse.wikitext;
      const truncated = text.length > MAX_EXTRACT_CHARS;
      const page: WikiTextResult = {
        lang,
        title: actualTitle,
        url: pageUrl(this.bases[lang], actualTitle),
        text: truncated ? text.slice(0, MAX_EXTRACT_CHARS) : text,
        truncated,
      };
      this.cacheSet(cacheKey, page);
      return { page };
    } catch (err) {
      return { error: err instanceof Error ? err.message : String(err) };
    }
  }
}

export const WIKI_DEFAULT_LANGS: readonly WikiLang[] = ['zh', 'en'];
