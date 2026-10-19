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
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { Type } from 'typebox';
// @ts-ignore vendored JS planner (no types)
import { planSingle, findItemIdByName } from './planner.mjs';

export const BLUEPRINT_TOOL_NAMES = ['blueprint_search', 'blueprint_info', 'blueprint_save', 'blueprint_generate'] as const;

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

/** 蓝图 header + 二进制里能稳定读出的元信息（卡片/工具结果共用）。 */
export interface BlueprintInspection {
  valid: boolean;
  header?: string;
  encodedBytes?: number;
  decodedBytes?: number;
  reason?: string;
  name?: string;
  author?: string;
  version?: string;
  attributes?: string;
  description?: string;
  iconItemId?: number;
  buildings?: number;
  width?: number;
  height?: number;
}

function decodeHeaderField(raw: string | undefined): string | undefined {
  if (raw === undefined || raw === '') return undefined;
  try {
    const decoded = decodeURIComponent(raw);
    return decoded.length > 0 ? decoded : undefined;
  } catch {
    // 第三方蓝图的 header 可能没有做 URI 转义；原样返回比丢掉更好。
    return raw;
  }
}

/**
 * header 格式（BlueprintData.headerStr）：
 *   BLUEPRINT:1,layout,icon0..icon4,0,ticks,gameVersion,shortDesc,author,customVersion,externalFields,desc
 * 只有 v1 header 才按这些位置解析；老格式/字段不足时只返回格式体检结果。
 */
function parseHeaderFields(header: string): Partial<BlueprintInspection> {
  if (!header.startsWith('BLUEPRINT:1,')) return {};
  const fields = header.split(',');
  if (fields.length < 15) return {};
  const icon = Number.parseInt(fields[2] ?? '', 10);
  return {
    iconItemId: Number.isFinite(icon) && icon > 0 ? icon : undefined,
    name: decodeHeaderField(fields[10]),
    author: decodeHeaderField(fields[11]),
    version: decodeHeaderField(fields[12]),
    attributes: decodeHeaderField(fields[13]),
    description: decodeHeaderField(fields[14]),
  };
}

/** 从解码后的二进制里读占地与建筑数；字段越界/损坏时返回空对象。 */
function parseBinaryFields(decoded: Buffer): Partial<BlueprintInspection> {
  // version/cursor/dragBox/primaryArea = 7 × int32，areaCount = 1 byte，
  // 每个 area 14 字节，之后才是 buildingCount。
  if (decoded.length < 29) return {};
  try {
    const width = decoded.readInt32LE(16);
    const height = decoded.readInt32LE(20);
    const areaCount = decoded[28] ?? 0;
    if (areaCount > 64) return {};
    const offset = 29 + areaCount * 14;
    if (offset + 4 > decoded.length) return {};
    const buildings = decoded.readInt32LE(offset);
    if (buildings < 0 || buildings > 1_048_576) return {};
    return {
      buildings,
      width: width > 0 ? width : undefined,
      height: height > 0 ? height : undefined,
    };
  } catch {
    return {};
  }
}

/** 解析蓝图文本：头部 + base64(gzip) + md5。只体检，不反序列化建筑。 */
export function inspectBlueprintText(text: string): BlueprintInspection {
  const match = BLUEPRINT_PATTERN.exec(text.trim());
  if (!match) return { valid: false, reason: 'format' };
  const header = match[1] ?? '';
  const encoded = match[2] ?? '';
  try {
    const decoded = gunzipSync(Buffer.from(encoded, 'base64'));
    return {
      valid: true,
      header,
      encodedBytes: encoded.length,
      decodedBytes: decoded.length,
      ...parseHeaderFields(header),
      ...parseBinaryFields(decoded),
    };
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

/** 游戏读取蓝图的目录（Windows/Wine 下 Documents 被映射到前缀里）。 */
export function resolveBlueprintDir(): string {
  const override = process.env['ICARUS_BLUEPRINT_DIR'];
  if (override && override.trim()) return override.trim();
  const home = process.env['USERPROFILE'] || homedir();
  return join(home, 'Documents', 'Dyson Sphere Program', 'Blueprint');
}

/** 文件名只保留安全字符；不允许路径分隔符与 ..，避免越出蓝图目录。 */
export function sanitizeBlueprintName(raw: string): string {
  const base = raw.replace(/\.txt$/i, '').replace(/[<>:"/\\|?*\x00-\x1f]/g, ' ').replace(/\.+\./g, ' ');
  const cleaned = base.replace(/\s+/g, ' ').replace(/^[. ]+|[. ]+$/g, '').slice(0, 80);
  return cleaned.length > 0 ? cleaned : 'blueprint';
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

    {
      name: 'blueprint_save',
      label: '保存蓝图',
      description:
        '把 blueprint_search 找到的蓝图直接保存到游戏的蓝图目录（Documents/Dyson Sphere Program/Blueprint），' +
        '保存后打开游戏内蓝图界面即可使用，无需退出游戏下载。' +
        `内容许可 ${LICENSE}；保存的是仓库原文件，请遵守许可并署名。`,
      promptSnippet: '把社区蓝图保存到游戏蓝图目录',
      promptGuidelines: [
        'Use blueprint_save 当玩家说“帮我保存/导入这个蓝图”时；path 来自 blueprint_search。',
        '默认不覆盖同名文件，会自动加 (2)/(3)；玩家明确要求覆盖时才传 overwrite=true。',
        `保存不改变游戏状态，但回答要说明保存位置，并保留 repository 与 ${LICENSE} 署名。`,
      ],
      parameters: Type.Object({
        path: Type.String({ description: 'blueprint_search 返回的仓库路径' }),
        name: Type.Optional(Type.String({ description: '保存名（不含 .txt），默认用原文件名' })),
        overwrite: Type.Optional(Type.Boolean({ description: '同名时覆盖，默认 false' })),
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
        if (!info.valid) {
          return reply({ ...client.meta(path), ...info, error: '蓝图格式无效，未保存' }, false, 'blueprint_invalid');
        }

        const dir = resolveBlueprintDir();
        let requested = typeof args['name'] === 'string' && args['name'].trim()
          ? args['name']
          : '';
        if (!requested) {
          const parts = path.replace(/\.txt$/i, '').split('/');
          const stem = parts[parts.length - 1] ?? 'blueprint';
          const parent = parts.length > 1 ? (parts[parts.length - 2] ?? '') : '';
          // 有些仓库用 “xxx/min.txt” 这种命名，文件名会退化成 min/max/1，
          // 这时补上上级目录，避免保存出一堆同名文件。
          requested = stem.length < 4 && parent ? `${parent} ${stem}` : stem;
        }
        const base = sanitizeBlueprintName(requested);
        const overwrite = args['overwrite'] === true;

        let fileName = `${base}.txt`;
        if (!overwrite && existsSync(join(dir, fileName))) {
          let index = 2;
          while (index < 50 && existsSync(join(dir, `${base} (${index}).txt`))) index += 1;
          fileName = `${base} (${index}).txt`;
        }

        const target = join(dir, fileName);
        try {
          mkdirSync(dir, { recursive: true });
          writeFileSync(target, fetched.text, { encoding: 'utf8' });
        } catch (err) {
          return reply(
            { ...client.meta(path), error: err instanceof Error ? err.message : String(err), dir },
            false,
            'blueprint_save_failed',
          );
        }

        return reply(
          {
            ...client.meta(path),
            saved: true,
            source: 'downloaded',
            path: target,
            fileName,
            dir,
            bytes: fetched.text.length,
            header: info.header,
            decodedBytes: info.decodedBytes,
            ...(info.name !== undefined ? { name: info.name } : {}),
            ...(info.author !== undefined ? { author: info.author } : {}),
            ...(info.version !== undefined ? { version: info.version } : {}),
            ...(info.attributes !== undefined ? { attributes: info.attributes } : {}),
            ...(info.description !== undefined ? { description: info.description } : {}),
            ...(info.iconItemId !== undefined ? { iconItemId: info.iconItemId } : {}),
            ...(info.buildings !== undefined ? { buildings: info.buildings } : {}),
            ...(info.width !== undefined ? { width: info.width } : {}),
            ...(info.height !== undefined ? { height: info.height } : {}),
            note:
              `已保存到游戏蓝图目录，打开游戏内蓝图界面即可使用（列表没刷新就关掉重开）。` +
              `内容来自社区仓库，许可 ${LICENSE}，请遵守许可并署名。`,
          },
          true,
        );
      },
    },
    {
      name: 'blueprint_generate',
      label: '生成产线蓝图',
      description:
        '用内置的 dsp-calc 规划器生成完整产线蓝图（机器 + 传送带 + 分拣器 + 连接槽位），' +
        '保存到游戏蓝图目录。输入物品与每分钟产量，可选带子/分拣器级别、行数、堆叠。',
      promptSnippet: '生成带传送带和分拣器的完整产线蓝图',
      promptGuidelines: [
        'Use blueprint_generate 当玩家要“完整产线蓝图/带带子和爪子的蓝图”时，不要回答只能下载。',
        'item 可以是物品名（如“电磁涡轮”）或物品 id；rate 是每分钟产量。',
        'beltType: 0 黄带 / 1 绿带 / 2 蓝带；insertType: 0 分拣器 / 1 高速 / 2 极速 / 3 集装。',
        '生成后告诉玩家文件路径与大小；这是完整的混带产线，粘贴后应可运行。',
      ],
      parameters: Type.Object({
        item: Type.String({ description: '物品名或物品 id' }),
        rate: Type.Number({ description: '目标每分钟产量' }),
        beltType: Type.Optional(Type.Integer({ minimum: 0, maximum: 2, description: '默认 2（蓝带）' })),
        insertType: Type.Optional(Type.Integer({ minimum: 0, maximum: 3, description: '默认 2（极速）' })),
        rows: Type.Optional(Type.Integer({ minimum: 1, maximum: 8, description: '产线行数，默认 1' })),
        stackSize: Type.Optional(Type.Integer({ minimum: 1, maximum: 4, description: '集装堆叠，默认 1' })),
        name: Type.Optional(Type.String({ description: '蓝图文件名' })),
        author: Type.Optional(Type.String({ description: '作者，默认“自动生成”' })),
        version: Type.Optional(Type.String({ description: '版本号，默认游戏版本 0.10.35.29151' })),
        attributes: Type.Optional(Type.String({ description: '蓝图属性' })),
        description: Type.Optional(Type.String({ description: '蓝图介绍' })),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const rawItem = typeof args['item'] === 'string' ? args['item'].trim() : '';
        const rate = typeof args['rate'] === 'number' ? args['rate'] : 0;
        if (!rawItem || !(rate > 0)) return reply({ error: 'item 与 rate 必填' }, false, 'bad_request');
        const itemId = /^\d+$/.test(rawItem) ? Number(rawItem) : findItemIdByName(rawItem);
        if (!itemId) return reply({ error: `找不到物品：${rawItem}` }, false, 'bad_request');
        try {
          const blueprintName = typeof args['name'] === 'string' && args['name'].trim()
            ? args['name'].trim()
            : `IcarusII-${rawItem}-${rate}`;
          const author = typeof args['author'] === 'string' && args['author']
            ? args['author']
            : '自动生成';
          const version = typeof args['version'] === 'string' && args['version']
            ? args['version']
            : '0.10.35.29151';
          const attributes = typeof args['attributes'] === 'string' ? args['attributes'] : '';
          const description = typeof args['description'] === 'string' ? args['description'] : '';
          const text = planSingle(itemId, rate, {
            beltType: typeof args['beltType'] === 'number' ? args['beltType'] : 2,
            insertType: typeof args['insertType'] === 'number' ? args['insertType'] : 2,
            rows: typeof args['rows'] === 'number' ? args['rows'] : 1,
            stackSize: typeof args['stackSize'] === 'number' ? args['stackSize'] : 4,
            name: blueprintName,
            author,
            version,
            attributes,
            description,
          });
          const dir = resolveBlueprintDir();
          mkdirSync(dir, { recursive: true });
          const base = sanitizeBlueprintName(blueprintName);
          let fileName = `${base}.txt`;
          for (let i = 2; i < 50 && existsSync(join(dir, fileName)); i++) fileName = `${base} (${i}).txt`;
          const filePath = join(dir, fileName);
          writeFileSync(filePath, text, 'ascii');
          const info = inspectBlueprintText(text);
          return reply({
            saved: true,
            source: 'generated',
            path: filePath,
            fileName,
            bytes: text.length,
            item: rawItem,
            itemId,
            rate,
            name: info.name ?? blueprintName,
            author: info.author ?? author,
            version: info.version ?? version,
            ...(info.attributes !== undefined ? { attributes: info.attributes } : {}),
            description: info.description ?? description,
            ...(info.iconItemId !== undefined ? { iconItemId: info.iconItemId } : {}),
            ...(info.buildings !== undefined ? { buildings: info.buildings } : {}),
            ...(info.width !== undefined ? { width: info.width } : {}),
            ...(info.height !== undefined ? { height: info.height } : {}),
            note: '由 dsp-calc 规划器生成，含机器/传送带/分拣器与连接；粘贴后应可直接运行。',
          }, true);
        } catch (err) {
          return reply({ error: err instanceof Error ? err.message : String(err) }, false, 'blueprint_generate_failed');
        }
      },
    },
  ];
}
