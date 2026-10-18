/**
 * 蓝图仓库检索（M4h）。
 *
 * 数据源：DSPBluePrints/FactoryBluePrints（CC BY-NC-SA 4.0）。
 * 本工具**不打包、不缓存蓝图内容到仓库**：只在线按需读取 GitHub 索引与原始文件，
 * 结果里始终带作者/仓库/许可证/链接，供玩家自行下载与署名。
 *
 * v1 只做检索与格式体检，不做粘贴/生成（写操作另立项）。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { gunzipSync } from 'node:zlib';
import { Type } from 'typebox';

export const BLUEPRINT_TOOL_NAMES = ['blueprint_search', 'blueprint_info'] as const;

const REPO = 'DSPBluePrints/FactoryBluePrints';
const LICENSE = 'CC BY-NC-SA 4.0';
const REPO_URL = `https://github.com/${REPO}`;
const MAX_TREE_BYTES = 4 * 1024 * 1024;
const MAX_FILE_BYTES = 2 * 1024 * 1024;
const TREE_TTL_MS = 6 * 60 * 60 * 1000;

type ToolReply = { content: { type: 'text'; text: string }[]; details: unknown };

interface TreeEntry {
  path: string;
  size: number;
}

export interface BlueprintClientOptions {
  fetchImpl?: typeof fetch;
  apiBase?: string;
  rawBase?: string;
  timeoutMs?: number;
}

interface CachedTree {
  at: number;
  entries: TreeEntry[];
}

export class BlueprintClient {
  private readonly fetchImpl: typeof fetch;
  private readonly apiBase: string;
  private readonly rawBase: string;
  private readonly timeoutMs: number;
  private cachedTree: CachedTree | null = null;

  constructor(options: BlueprintClientOptions = {}) {
    this.fetchImpl = options.fetchImpl ?? fetch;
    this.apiBase = options.apiBase ?? 'https://api.github.com';
    this.rawBase = options.rawBase ?? 'https://raw.githubusercontent.com';
    this.timeoutMs = options.timeoutMs ?? 10000;
  }

  private rawUrl(path: string): string {
    return `${this.rawBase}/${REPO}/HEAD/${path.split('/').map(encodeURIComponent).join('/')}`;
  }

  private blobUrl(path: string): string {
    return `${REPO_URL}/blob/HEAD/${path.split('/').map(encodeURIComponent).join('/')}`;
  }

  async tree(): Promise<TreeEntry[]> {
    if (this.cachedTree && Date.now() - this.cachedTree.at < TREE_TTL_MS) return this.cachedTree.entries;
    const response = await this.fetchImpl(`${this.apiBase}/repos/${REPO}/git/trees/HEAD?recursive=1`, {
      signal: AbortSignal.timeout(this.timeoutMs),
      headers: { accept: 'application/vnd.github+json', 'user-agent': 'icarus-ii/0.1' },
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const text = await response.text();
    if (text.length > MAX_TREE_BYTES) throw new Error('索引过大');
    const raw = JSON.parse(text) as { tree?: unknown; truncated?: unknown };
    const entries: TreeEntry[] = [];
    for (const item of Array.isArray(raw.tree) ? raw.tree : []) {
      if (!item || typeof item !== 'object') continue;
      const row = item as { path?: unknown; type?: unknown; size?: unknown };
      if (row.type !== 'blob' || typeof row.path !== 'string') continue;
      if (!row.path.toLowerCase().endsWith('.txt')) continue;
      entries.push({ path: row.path, size: typeof row.size === 'number' ? row.size : 0 });
    }
    this.cachedTree = { at: Date.now(), entries };
    return entries;
  }

  async search(query: string, limit: number): Promise<TreeEntry[]> {
    const tokens = query.toLowerCase().split(/\s+/).filter((t) => t.length > 0);
    const all = await this.tree();
    const matched = all.filter((entry) => {
      const path = entry.path.toLowerCase();
      return tokens.every((token) => path.includes(token));
    });
    matched.sort((a, b) => a.path.length - b.path.length || a.path.localeCompare(b.path));
    return matched.slice(0, limit);
  }

  async fetch(path: string): Promise<{ text?: string; error?: string }> {
    if (!path.toLowerCase().endsWith('.txt') || path.includes('..')) return { error: 'bad_path' };
    const response = await this.fetchImpl(this.rawUrl(path), {
      signal: AbortSignal.timeout(this.timeoutMs),
      headers: { 'user-agent': 'icarus-ii/0.1' },
    });
    if (response.status === 404) return { error: 'not_found' };
    if (!response.ok) return { error: `HTTP ${response.status}` };
    const text = await response.text();
    if (text.length > MAX_FILE_BYTES) return { error: 'too_large' };
    return { text };
  }

  meta(path: string): { path: string; blobUrl: string; rawUrl: string; license: string; repo: string } {
    return { path, blobUrl: this.blobUrl(path), rawUrl: this.rawUrl(path), license: LICENSE, repo: REPO_URL };
  }
}

const BLUEPRINT_PATTERN = /^([^"]*)"([A-Za-z0-9+/=]+)"([0-9a-fA-F]{32})\s*$/;

/** 解析蓝图文本：头部 + base64(gzip) + md5。只体检，不反序列化建筑。 */
export function inspectBlueprintText(text: string): {
  valid: boolean;
  header?: string;
  encodedBytes?: number;
  decodedBytes?: number;
  reason?: string;
} {
  const match = BLUEPRINT_PATTERN.exec(text.trim());
  if (!match) return { valid: false, reason: 'format' };
  const header = match[1] ?? '';
  const encoded = match[2] ?? '';
  try {
    const decoded = gunzipSync(Buffer.from(encoded, 'base64'));
    return { valid: true, header, encodedBytes: encoded.length, decodedBytes: decoded.length };
  } catch {
    return { valid: false, reason: 'gzip' };
  }
}

function reply(payload: unknown, ok: boolean, code?: string): ToolReply {
  return {
    content: [{ type: 'text', text: ok ? JSON.stringify(payload, null, 2) : `[${code ?? 'blueprint_error'}] ${JSON.stringify(payload, null, 2)}` }],
    details: payload,
  };
}

export function createBlueprintTools(client: BlueprintClient = new BlueprintClient()): ToolDefinition[] {
  return [
    {
      name: 'blueprint_search',
      label: '蓝图搜索',
      description:
        '在 DSPBluePrints/FactoryBluePrints 仓库里按路径关键词搜索蓝图（在线按需，不打包内容）。' +
        `结果是文件路径 + 仓库链接，内容许可 ${LICENSE}；引用时必须署名仓库与作者。`,
      promptSnippet: '搜索社区蓝图仓库（在线，带署名）',
      promptGuidelines: [
        'Use blueprint_search 当玩家想要现成蓝图/产线设计时。',
        '路径多为中文目录+文件名，用“物品/产线/规模”这类关键词搜索；可多次换词。',
        `结果必须附 repository 与 ${LICENSE} 署名；不要把蓝图内容说成自己生成的。`,
      ],
      parameters: Type.Object({
        query: Type.String({ description: '路径关键词，例如“卡西米尔”“白糖”“300/min”' }),
        limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 20, description: '默认 10' })),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const query = typeof args['query'] === 'string' ? args['query'] : '';
        if (!query.trim()) return reply({ error: 'query 不能为空' }, false, 'bad_request');
        const limitRaw = typeof args['limit'] === 'number' ? args['limit'] : 10;
        const limit = Math.max(1, Math.min(20, Math.floor(limitRaw)));

        try {
          const rows = await client.search(query, limit);
          return reply(
            {
              query,
              total: rows.length,
              results: rows.map((row) => ({
                ...client.meta(row.path),
                size: row.size,
              })),
              note: `内容来自社区仓库，许可 ${LICENSE}；使用/再分发请遵守该许可并署名。`,
            },
            true,
          );
        } catch (err) {
          return reply(
            { error: err instanceof Error ? err.message : String(err), hint: 'GitHub 可能限流（未登录 60 次/小时）' },
            false,
            'blueprint_unavailable',
          );
        }
      },
    },

    {
      name: 'blueprint_info',
      label: '蓝图详情',
      description:
        '读取一个蓝图文件并做格式体检（头部/base64+gzip/md5），返回大小与解码后字节数，不解析建筑。' +
        `内容许可 ${LICENSE}，必须署名来源。`,
      promptSnippet: '读取蓝图文件并做格式体检',
      promptGuidelines: [
        'Use blueprint_info 在 blueprint_search 选中文件后，确认文件有效并给出下载链接。',
        'valid=false 时说明原因（format/gzip），不要猜里面的建筑。',
        `回答必须带 repository 与 ${LICENSE} 署名。`,
      ],
      parameters: Type.Object({
        path: Type.String({ description: 'blueprint_search 返回的 path' }),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const path = typeof args['path'] === 'string' ? args['path'] : '';
        if (!path.trim()) return reply({ error: 'path 不能为空' }, false, 'bad_request');

        const fetched = await client.fetch(path);
        if (!fetched.text) {
          const code = fetched.error === 'not_found' ? 'blueprint_not_found' : 'blueprint_unavailable';
          return reply({ ...client.meta(path), error: fetched.error ?? 'unknown' }, false, code);
        }

        const info = inspectBlueprintText(fetched.text);
        return reply(
          {
            ...client.meta(path),
            bytes: fetched.text.length,
            ...info,
            note: `内容来自社区仓库，许可 ${LICENSE}；请遵守许可并署名。`,
          },
          info.valid,
          'blueprint_invalid',
        );
      },
    },
  ];
}
