/**
 * 蓝图仓库检索（M4h）。
 *
 * 数据源：DSPBluePrints/FactoryBluePrints（CC BY-NC-SA 4.0）与 Dyson Sphere Blueprints 网站。
 * 本工具**不打包、不缓存蓝图内容到仓库**：只在线按需读取 GitHub 索引与原始文件，
 * 结果里始终带作者/仓库/许可证/链接，供玩家自行下载与署名。
 *
 * v1 只做检索与格式体检，不做粘贴/生成（写操作另立项）。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import type { KnowledgeStore } from '../kb/store.js';
import type { KnowledgeBase } from '../kb/knowledge.js';
import type { PlanResult } from '../kb/types.js';
import type { AliasResolver } from '../wiki/aliases.js';
import type { BlueprintTiers } from '../protocol.js';
import { BlueprintPathError, BlueprintWorkspace } from './workspace.js';
import { DysonSphereBlueprintsClient } from './site.js';
import type { PlanBlueprintMeta } from './planner.js';
import type { LocalWriteConfirmRequest } from '../tools/host.js';
import { gunzipSync } from 'node:zlib';
import { join } from 'node:path';
import { Type } from 'typebox';
// @ts-ignore vendored JS planner (no types)
import { planBlueprint } from './planner.mjs';

export const BLUEPRINT_TOOL_NAMES = ['blueprint_fs', 'blueprint_search', 'blueprint_info', 'blueprint_save', 'blueprint_generate'] as const;

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

function describeError(value: unknown): string {
  return value instanceof Error ? value.message : String(value);
}

/** 文件名只保留安全字符；不允许路径分隔符与 ..，避免越出蓝图目录。 */
export function sanitizeBlueprintName(raw: string): string {
  const base = raw.replace(/\.txt$/i, '').replace(/[<>:"/\\|?*\x00-\x1f]/g, ' ').replace(/\.+\./g, ' ');
  const cleaned = base.replace(/\s+/g, ' ').replace(/^[. ]+|[. ]+$/g, '').slice(0, 80);
  return cleaned.length > 0 ? cleaned : 'blueprint';
}

interface BuildTilesOptions {
  buildingTier: 'base' | 'mk2' | 'mk3';
  depth?: number;
  author: string;
  version: string;
  attributes: string;
  blueprintName: string;
  beltType: number;
  insertType: number;
  rows: number;
  stackSize: number;
  from?: 'ore' | 'smelted' | number;
  description?: (index: number, count: number, tileRate: number, totalRate: number) => string;
}

interface BuiltTile {
  text: string;
  meta: PlanBlueprintMeta;
  plan: PlanResult;
  rate: number;
  name: string;
}

const MAX_TILES = 256;
const GENERATION_BUDGET_MS = 8000;

/** 从容量错误里读出“至少要拆几张”：优先用结构化字段，其次解析消息里的两个数字。 */
function hintedTiles(err: unknown): number | null {
  const tiles = (err as { tiles?: unknown } | null)?.tiles;
  if (Number.isInteger(tiles)) return Math.max(2, Math.min(MAX_TILES, tiles as number));
  const message = err instanceof Error ? err.message : String(err);
  const split = /需要\s*([\d.]+)\/min，单条\s*([\d.]+)\/min/.exec(message);
  if (split) return Math.max(2, Math.ceil(Number(split[1]) / Number(split[2])));
  const pack = /超过传送带最大容量\s*[\d.]*\s*\*\s*[\d.]+\s*=\s*([\d.]+)/.exec(message);
  const need = /中间产物\s*([\d.]+)\s*份/.exec(message);
  if (pack && need) return Math.max(2, Math.ceil(Number(need[1]) / Number(pack[1])));
  return null;
}

function isBusCapacityError(err: unknown): boolean {
  const message = err instanceof Error ? err.message : String(err);
  return message.includes('传送带最大容量') || (message.includes('分配') && message.includes('超过传送带'));
}

/**
 * 一张混带蓝图的总线最多承载 3 条蓝带（vendor 的硬限制），高产量只能拆成多张。
 * 这里先试单张；命中总线容量上限时，按 2..8 张均分重试，每张独立生成。
 * 返回的是**文本**，落盘由调用方完成，避免生成一半留下半套文件。
 */
function buildBlueprintTiles(
  kb: KnowledgeBase,
  itemId: number,
  rate: number,
  options: BuildTilesOptions,
): { tiles: BuiltTile[]; ratePerTile: number } {
  const buildOne = (tileRate: number, name: string, index: number, count: number): BuiltTile => {
    const plan = kb.plan(itemId, {
      rate: tileRate,
      buildingTier: options.buildingTier,
      ...(options.depth !== undefined ? { depth: options.depth } : {}),
      ...(options.from !== undefined ? { from: options.from } : {}),
    });
    const built = planBlueprint(plan, {
      beltType: options.beltType,
      insertType: options.insertType,
      rows: options.rows,
      stackSize: options.stackSize,
      name,
      author: options.author,
      version: options.version,
      attributes: options.attributes,
      description: options.description
        ? options.description(index, count, tileRate, rate)
        : `全链路（从原矿投入）：${tileRate}/min`,
      gameVersion: kb.gameVersion,
    });
    return { text: built.text, meta: built.meta, plan, rate: tileRate, name };
  };

  const startedAt = performance.now();
  let lastError: unknown = null;
  try {
    return { tiles: [buildOne(rate, options.blueprintName, 1, 1)], ratePerTile: rate };
  } catch (err) {
    if (!isBusCapacityError(err)) throw err;
    lastError = err;
  }

  // 容量错误里带着“至少要拆几张”，直接从它开始试，别从 2 张一路试上去。
  const startCount = hintedTiles(lastError) ?? 2;
  for (let count = startCount; count <= MAX_TILES; count += 1) {
    if (performance.now() - startedAt > GENERATION_BUDGET_MS) {
      throw new Error(`蓝图生成超过 ${GENERATION_BUDGET_MS / 1000} 秒预算（已试到 ${count} 张）；请降低产量或改用多张蓝图。`);
    }
    const tileRate = rate / count;
    if (!(tileRate > 0)) break;
    try {
      const tiles: BuiltTile[] = [];
      for (let index = 0; index < count; index += 1) {
        tiles.push(buildOne(tileRate, `${options.blueprintName} ${index + 1}of${count}`, index + 1, count));
      }
      return { tiles, ratePerTile: tileRate };
    } catch (err) {
      if (!isBusCapacityError(err)) throw err;
      lastError = err;
      // 这一张的容量错误可能比之前更严重，直接跳到它要的张数再试。
      const need = hintedTiles(err);
      if (need && need - 1 > count) count = need - 1;
    }
  }
  throw lastError instanceof Error ? lastError : new Error('蓝图生成失败');
}


export function createBlueprintTools(
  client: BlueprintClient = new BlueprintClient(),
  store?: KnowledgeStore,
  aliases?: AliasResolver,
  getTiers?: () => BlueprintTiers | null,
  workspace?: BlueprintWorkspace,
  siteClient?: DysonSphereBlueprintsClient,
  confirmWrite?: (request: LocalWriteConfirmRequest) => Promise<boolean>,
): ToolDefinition[] {
  const ws = workspace ?? new BlueprintWorkspace();
  const site = siteClient ?? new DysonSphereBlueprintsClient();
  // 本地蓝图写操作不消耗 mod 令牌，但权限档位要求确认时仍要问玩家。
  const confirmFileWrite = async (tool: string, args: Record<string, unknown>, body: string): Promise<boolean> => {
    if (!confirmWrite) return true;
    return confirmWrite({ tool, args: args as never, risk: 1, title: '写入蓝图文件？', body });
  };
  return [
    {
      name: 'blueprint_fs',
      label: '蓝图文件',
      description:
        '在游戏的蓝图目录里查看和整理文件，类似受限的 ls/cd/pwd/cat/mkdir/rm/rmdir。' +
        '路径以蓝图根为 `/`；默认保存目录是 `/IcarusII`。不能访问蓝图目录之外。',
      promptGuidelines: [
        '玩家要查看蓝图目录、进入子目录、读取蓝图信息、建目录、删除或保存任意蓝图文本时使用。',
        '默认保存到 `/IcarusII`；用 op=write 保存模型从其他途径拿到的蓝图原文（必须是合法 .txt 蓝图）。',
        'op=rm/rmdir 是永久删除；必须先向玩家确认，只有玩家明确同意后才传 confirm=true。',
        'cat 默认返回蓝图元数据；只有确实需要原文时才传 raw=true。',
        '路径不能含 .. 或盘符；不能越出蓝图根。',
      ],
      parameters: Type.Object({
        op: Type.Union(
          [
            Type.Literal('pwd'),
            Type.Literal('cd'),
            Type.Literal('ls'),
            Type.Literal('cat'),
            Type.Literal('mkdir'),
            Type.Literal('write'),
            Type.Literal('rm'),
            Type.Literal('rmdir'),
          ],
          { description: '操作类型' },
        ),
        path: Type.Optional(Type.String({ description: '虚拟路径；相对当前目录，或以 / 开头' })),
        content: Type.Optional(Type.String({ description: 'op=write：蓝图 .txt 原文' })),
        recursive: Type.Optional(Type.Boolean({ description: 'ls 递归 / rmdir 递归删除，默认 false' })),
        confirm: Type.Optional(Type.Boolean({ description: 'rm/rmdir 必须为 true；永久删除' })),
        overwrite: Type.Optional(Type.Boolean({ description: 'write 是否覆盖同名文件，默认 false' })),
        raw: Type.Optional(Type.Boolean({ description: 'cat 是否返回蓝图原文，默认 false（只返回元数据）' })),
        details: Type.Optional(Type.Boolean({ description: 'ls 是否解析 .txt 蓝图元数据，默认 false' })),
        limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 1000, description: 'ls 最多返回条目，默认 200' })),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const op = typeof args['op'] === 'string' ? args['op'] : '';
        const path = typeof args['path'] === 'string' ? args['path'] : undefined;
        const writeOp = op === 'mkdir' || op === 'write' || op === 'rm' || op === 'rmdir';
        if (writeOp) {
          const ok = await confirmFileWrite(
            'blueprint_fs',
            { op, path },
            `操作：${op}\n路径：${path ?? '(默认目录)'}`,
          );
          if (!ok) {
            return reply({ error: '玩家拒绝了蓝图写操作' }, false, 'confirmation_denied');
          }
        }
        try {
          if (op === 'pwd') {
            return reply({ root: ws.root, cwd: ws.currentDir, defaultDir: ws.defaultDir }, true);
          }
          if (op === 'cd') {
            if (!path) return reply({ error: 'path 不能为空' }, false, 'bad_request');
            const cwd = ws.setCwd(path);
            return reply({ cwd }, true);
          }
          if (op === 'ls') {
            const listed = ws.list(path, {
              recursive: args['recursive'] === true,
              limit: typeof args['limit'] === 'number' ? args['limit'] : 200,
            });
            const details = args['details'] === true;
            const entries = listed.entries.map((entry) => {
              if (!details || entry.type !== 'file' || !entry.name.toLowerCase().endsWith('.txt')) return entry;
              try {
                const file = ws.read(`/${entry.relPath}`, 2 * 1024 * 1024);
                const info = inspectBlueprintText(file.text);
                return {
                  ...entry,
                  blueprint: {
                    valid: info.valid,
                    name: info.name,
                    author: info.author,
                    buildings: info.buildings,
                    width: info.width,
                    height: info.height,
                    iconItemId: info.iconItemId,
                  },
                };
              } catch {
                return entry;
              }
            });
            return reply(
              {
                cwd: ws.currentDir,
                path: listed.path.virtual,
                entries,
                truncated: listed.truncated,
                note: '路径以蓝图根 / 为根；文件大小单位 bytes。',
              },
              true,
            );
          }
          if (op === 'cat') {
            if (!path) return reply({ error: 'path 不能为空' }, false, 'bad_request');
            const file = ws.read(path, 2 * 1024 * 1024);
            const info = inspectBlueprintText(file.text);
            const base = {
              path: file.path.virtual,
              relPath: file.path.relPath,
              size: file.size,
              mtimeMs: file.mtimeMs,
              valid: info.valid,
              ...(info.reason ? { reason: info.reason } : {}),
              header: info.header,
              name: info.name,
              author: info.author,
              version: info.version,
              attributes: info.attributes,
              description: info.description,
              buildings: info.buildings,
              width: info.width,
              height: info.height,
              iconItemId: info.iconItemId,
            };
            if (args['raw'] === true) {
              const text = file.text.length > 256 * 1024 ? file.text.slice(0, 256 * 1024) : file.text;
              return reply(
                {
                  ...base,
                  raw: text,
                  truncated: file.text.length > text.length,
                },
                true,
              );
            }
            return reply(base, true);
          }
          if (op === 'mkdir') {
            if (!path) return reply({ error: 'path 不能为空' }, false, 'bad_request');
            const created = ws.mkdir(path, args['recursive'] !== false);
            return reply({ created: created.virtual, cwd: ws.currentDir }, true);
          }
          if (op === 'write') {
            const content = typeof args['content'] === 'string' ? args['content'] : '';
            if (!content) return reply({ error: 'content 不能为空' }, false, 'bad_request');
            const contentBytes = Buffer.byteLength(content, 'utf8');
            if (contentBytes > 2 * 1024 * 1024) {
              return reply(
                { error: `蓝图文本过大：${contentBytes} bytes（上限 2 MB）` },
                false,
                'too_large',
              );
            }
            const info = inspectBlueprintText(content);
            if (!info.valid) {
              return reply(
                { error: `蓝图格式无效：${info.reason ?? 'unknown'}`, valid: false },
                false,
                'blueprint_invalid',
              );
            }
            const fileName =
              path && path.trim()
                ? path.trim()
                : `${ws.defaultDir}/${info.name ?? `IcarusII-${Date.now()}`}.txt`;
            const target = ws.resolveVirtual(fileName.endsWith('.txt') ? fileName : `${fileName}.txt`);
            const written = ws.write(target.virtual, content, { overwrite: args['overwrite'] === true });
            const savedInfo = inspectBlueprintText(content);
            return reply(
              {
                saved: true,
                source: 'written',
                path: written.path.real,
                relPath: written.path.relPath,
                fileName: written.path.relPath.split('/').pop(),
                bytes: written.size,
                name: savedInfo.name,
                author: savedInfo.author,
                version: savedInfo.version,
                attributes: savedInfo.attributes,
                description: savedInfo.description,
                buildings: savedInfo.buildings,
                width: savedInfo.width,
                height: savedInfo.height,
                iconItemId: savedInfo.iconItemId,
                note: '已保存蓝图原文；打开游戏蓝图界面即可使用。',
              },
              true,
            );
          }
          if (op === 'rm' || op === 'rmdir') {
            if (!path) return reply({ error: 'path 不能为空' }, false, 'bad_request');
            const target = ws.resolveVirtual(path);
            if (args['confirm'] !== true) {
              return reply(
                {
                  requiresConfirmation: true,
                  confirm: false,
                  path: target.virtual,
                  relPath: target.relPath,
                  note: '永久删除；请先向玩家确认，再传 confirm=true。',
                },
                false,
                'confirmation_required',
              );
            }
            if (op === 'rm') {
              const removed = ws.remove(path);
              return reply({ removed: true, path: removed.virtual, relPath: removed.relPath }, true);
            }
            const removed = ws.removeDir(path, args['recursive'] === true);
            return reply(
              { removed: true, directory: true, path: removed.virtual, relPath: removed.relPath },
              true,
            );
          }
          return reply({ error: `不支持的操作：${op}` }, false, 'bad_request');
        } catch (err) {
          if (err instanceof BlueprintPathError) {
            return reply({ error: err.message, code: err.code }, false, err.code);
          }
          return reply({ error: err instanceof Error ? err.message : String(err) }, false, 'blueprint_fs_failed');
        }
      },
    },
    {
      name: 'blueprint_search',
      label: '蓝图搜索',
      description:
        '在 DSPBluePrints/FactoryBluePrints 仓库和 Dyson Sphere Blueprints 网站里按关键词搜索蓝图（在线按需，不打包内容）。' +
        `GitHub 结果带仓库路径与许可 ${LICENSE}；网站结果带详情页 URL。引用时必须署名来源与作者。`,
      promptGuidelines: [
        '玩家想要现成蓝图/产线设计时使用。',
        '路径多为中文目录+文件名，用“物品/产线/规模”这类关键词搜索；可多次换词。',
        'GitHub 结果用 path 继续 blueprint_info/blueprint_save；Dyson Sphere Blueprints 结果用 url。',
        'GitHub 的来源链接用 blobUrl（文件页），不是 repo；Dyson Sphere Blueprints 用 url。',
        `结果必须附来源链接与作者；GitHub 内容许可 ${LICENSE}，不要把蓝图内容说成自己生成的。`,
        'GitHub 搜索条目没有作者时不要编造；作者以 blueprint_info/blueprint_save 读出的 header 为准。',
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

        const [githubResult, siteResult] = await Promise.allSettled([
          client.search(query, limit),
          site.search(query, limit),
        ]);
        const githubRows = githubResult.status === 'fulfilled' ? githubResult.value : [];
        const siteRows = siteResult.status === 'fulfilled' ? siteResult.value : [];
        const warnings: string[] = [];
        if (githubResult.status === 'rejected') {
          warnings.push(`GitHub 检索失败：${describeError(githubResult.reason)}`);
        }
        if (siteResult.status === 'rejected') {
          warnings.push(`Dyson Sphere Blueprints 检索失败：${describeError(siteResult.reason)}`);
        }
        if (githubRows.length === 0 && siteRows.length === 0 && warnings.length > 0) {
          return reply(
            { error: warnings.join('；'), hint: 'GitHub 可能限流；Dyson Sphere Blueprints 可能暂时不可用' },
            false,
            'blueprint_unavailable',
          );
        }

        // 交替合并，确保 limit 较小时两个来源都有机会出现。
        const results: Record<string, unknown>[] = [];
        for (let i = 0; results.length < limit && (i < githubRows.length || i < siteRows.length); i++) {
          const github = githubRows[i];
          if (github) {
            results.push({
              ...client.meta(github.path),
              source: 'github',
              path: github.path,
              size: github.size,
            });
          }
          if (results.length >= limit) break;
          const web = siteRows[i];
          if (web) {
            results.push({
              ...site.meta(web.url),
              title: web.title,
              ...(web.author ? { author: web.author } : {}),
              ...(web.gameVersion ? { gameVersion: web.gameVersion } : {}),
            });
          }
        }

        return reply(
          {
            query,
            total: results.length,
            results,
            ...(warnings.length > 0 ? { warnings } : {}),
            note:
              `GitHub 结果许可 ${LICENSE}；` +
              'Dyson Sphere Blueprints 结果版权归原作者，使用前查看蓝图页面说明。',
          },
          true,
        );
      },
    },

    {
      name: 'blueprint_info',
      label: '蓝图详情',
      description:
        '读取一个蓝图文件并做格式体检（头部/base64+gzip/md5），返回大小与解码后字节数，不解析建筑。' +
        '支持 GitHub 仓库路径或 Dyson Sphere Blueprints 详情页 URL；内容版权归原作者，必须署名来源。',
      promptGuidelines: [
        '在 blueprint_search 选中结果后使用，确认文件有效并给出下载链接。',
        'GitHub 结果传 path；Dyson Sphere Blueprints 结果传 url。',
        'valid=false 时说明原因（format/gzip），不要猜里面的建筑。',
        '回答必须带来源链接和署名；GitHub 仓库内容许可为 ' + LICENSE + '。',
        'GitHub 的来源链接用返回的 blobUrl（文件页），不是 repo；Dyson Sphere Blueprints 用 url。',
        '没有 author 时不要编造，按 header 里实际内容或“作者见蓝图页”处理。',
      ],
      parameters: Type.Object({
        path: Type.Optional(Type.String({ description: 'GitHub 仓库路径（blueprint_search 的 path）' })),
        url: Type.Optional(Type.String({ description: 'Dyson Sphere Blueprints 详情页 URL' })),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const path = typeof args['path'] === 'string' ? args['path'] : '';
        const url = typeof args['url'] === 'string' ? args['url'] : '';
        if (!path.trim() && !url.trim()) {
          return reply({ error: 'path 或 url 至少提供一个' }, false, 'bad_request');
        }

        let fetched: { text?: string; error?: string; title?: string };
        let meta: Record<string, unknown>;
        try {
          if (url.trim()) {
            fetched = await site.fetch(url);
            meta = { ...site.meta(url) };
          } else {
            fetched = await client.fetch(path);
            meta = { ...client.meta(path) };
          }
        } catch (err) {
          return reply(
            { error: describeError(err), hint: '网络/站点可能暂时不可用' },
            false,
            'blueprint_unavailable',
          );
        }

        if (!fetched.text) {
          const code = fetched.error === 'not_found' ? 'blueprint_not_found' : 'blueprint_unavailable';
          return reply({ ...meta, error: fetched.error ?? 'unknown' }, false, code);
        }

        const info = inspectBlueprintText(fetched.text);
        return reply(
          {
            ...meta,
            ...(fetched.title ? { title: fetched.title } : {}),
            bytes: fetched.text.length,
            ...info,
            note: (meta as { note?: string }).note
              ?? `内容来自社区仓库，许可 ${LICENSE}；请遵守许可并署名。`,
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
        '把 blueprint_search 找到的蓝图保存到游戏蓝图目录（Documents/Dyson Sphere Program/Blueprint），' +
        '支持 GitHub 仓库路径或 Dyson Sphere Blueprints 详情页 URL；保存后打开游戏内蓝图界面即可使用。' +
        '内容版权归原作者，保存/再分发请遵守来源页面说明并署名。',
      promptGuidelines: [
        '玩家说“帮我保存/导入这个蓝图”时使用；GitHub 结果传 path，Dyson Sphere Blueprints 结果传 url。',
        '默认不覆盖同名文件，会自动加 (2)/(3)；玩家明确要求覆盖时才传 overwrite=true。',
        '保存不改变游戏状态，但回答要说明保存位置，并保留来源链接与作者署名。',
        'GitHub 的来源链接用 blobUrl（文件页），不是 repo；Dyson Sphere Blueprints 用 url。',
        '没有 author 时不要编造。',
      ],
      parameters: Type.Object({
        path: Type.Optional(Type.String({ description: 'GitHub 仓库路径（blueprint_search 的 path）' })),
        url: Type.Optional(Type.String({ description: 'Dyson Sphere Blueprints 详情页 URL' })),
        name: Type.Optional(Type.String({ description: '保存名（不含 .txt），默认用蓝图/文件名' })),
        dir: Type.Optional(
          Type.String({ description: '保存目录（相对蓝图根，默认 /IcarusII）；不存在会自动创建' }),
        ),
        overwrite: Type.Optional(Type.Boolean({ description: '同名时覆盖，默认 false' })),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const path = typeof args['path'] === 'string' ? args['path'] : '';
        const url = typeof args['url'] === 'string' ? args['url'] : '';
        if (!path.trim() && !url.trim()) {
          return reply({ error: 'path 或 url 至少提供一个' }, false, 'bad_request');
        }
        {
          const ok = await confirmFileWrite(
            'blueprint_save',
            { path, url, name: args['name'], dir: args['dir'] },
            `保存蓝图到：${typeof args['dir'] === 'string' && args['dir'] ? args['dir'] : '/IcarusII'}\n` +
              `来源：${url.trim() || path.trim()}`,
          );
          if (!ok) return reply({ error: '玩家拒绝了保存蓝图' }, false, 'confirmation_denied');
        }

        let fetched: { text?: string; error?: string; title?: string };
        let meta: Record<string, unknown>;
        try {
          if (url.trim()) {
            fetched = await site.fetch(url);
            meta = { ...site.meta(url) };
          } else {
            fetched = await client.fetch(path);
            meta = { ...client.meta(path) };
          }
        } catch (err) {
          return reply(
            { error: describeError(err), hint: '网络/站点可能暂时不可用' },
            false,
            'blueprint_unavailable',
          );
        }

        if (!fetched.text) {
          const code = fetched.error === 'not_found' ? 'blueprint_not_found' : 'blueprint_unavailable';
          return reply({ ...meta, error: fetched.error ?? 'unknown' }, false, code);
        }
        const info = inspectBlueprintText(fetched.text);
        if (!info.valid) {
          return reply({ ...meta, ...info, error: '蓝图格式无效，未保存' }, false, 'blueprint_invalid');
        }

        let dir;
        try {
          dir = ws.resolveTargetDir(typeof args['dir'] === 'string' ? args['dir'] : undefined);
        } catch (err) {
          return reply(
            { ...meta, error: describeError(err) },
            false,
            'blueprint_save_failed',
          );
        }
        let requested = typeof args['name'] === 'string' && args['name'].trim()
          ? args['name']
          : '';
        if (!requested && url.trim()) {
          requested = info.name ?? fetched.title ?? 'DysonSphereBlueprint';
        }
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

        let target;
        try {
          if (overwrite) {
            target = ws.resolveVirtual(`${dir.relPath}/${base}.txt`, '/');
            ws.write(target.virtual, fetched.text, { overwrite: true });
          } else {
            const unique = ws.uniqueFileName(dir, base);
            target = unique.virtual;
            ws.write(target.virtual, fetched.text);
          }
        } catch (err) {
          return reply(
            {
              ...meta,
              error: describeError(err),
              dir: dir.real,
            },
            false,
            'blueprint_save_failed',
          );
        }
        const fileName = target.relPath.split('/').pop() ?? `${base}.txt`;

        return reply(
          {
            ...meta,
            saved: true,
            source: 'downloaded',
            path: target.real,
            relPath: target.relPath,
            fileName,
            dir: dir.real,
            dirRel: dir.relPath,
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
              '已保存到游戏蓝图目录，打开游戏内蓝图界面即可使用（列表没刷新就关掉重开）。' +
              ((meta as { note?: string }).note
                ?? `内容来自社区仓库，许可 ${LICENSE}；请遵守许可并署名。`),
          },
          true,
        );
      },
    },

    {
      name: 'blueprint_generate',
      label: '生成产线蓝图',
      description:
        '按知识库里的真实配方，从原矿开始生成完整产线蓝图（各阶段机器 + 传送带 + 分拣器 + 连接槽位），' +
        '保存到游戏蓝图目录。需要先进入存档让知识库建立；输入物品与每分钟产量，可选机器/带子/分拣器级别与行数。',
      promptGuidelines: [
        '玩家要“完整产线蓝图/带带子和爪子的蓝图”时使用，不要回答只能下载。',
        'item 可以是物品名（如“电磁涡轮”）或物品 id；rate 是每分钟产量。',
        'buildingTier: base 对应最低速机器 / mk2 / mk3；beltType: 0 黄带 / 1 绿带 / 2 蓝带；insertType: 0 分拣器 / 1 高速 / 2 极速 / 3 集装。',
        '返回里的 stages 是每个阶段的机器数与产物，回答时先报最终产物与总机器数，再按阶段说明。',
        'warnings 非空时必须原样告诉玩家，不要声称“完整可用”。',
      ],
      parameters: Type.Object({
        item: Type.String({ description: '物品名或物品 id' }),
        rate: Type.Number({ description: '目标每分钟产量' }),
        buildingTier: Type.Optional(
          Type.Union([Type.Literal('base'), Type.Literal('mk2'), Type.Literal('mk3')], {
            description: '机器档位，默认 base（知识库里的最低速机器）',
          }),
        ),
        beltType: Type.Optional(Type.Integer({ minimum: 0, maximum: 2, description: '默认 2（蓝带）' })),
        insertType: Type.Optional(Type.Integer({ minimum: 0, maximum: 3, description: '默认 2（极速）' })),
        rows: Type.Optional(Type.Integer({ minimum: 1, maximum: 8, description: '产线行数，默认 1；超宽时会自动加行' })),
        stackSize: Type.Optional(Type.Integer({ minimum: 1, maximum: 4, description: '集装堆叠，默认 1' })),
        depth: Type.Optional(Type.Integer({ minimum: 1, maximum: 16, description: '配方展开深度，默认 8' })),
        from: Type.Optional(
          Type.String({
            description:
              '起点：ore（默认，从原矿投入）/ smelted（从冶炼产物，如铁块/钛块开始）/ 物品名或 id（从该中间产物开始）',
          }),
        ),
        dir: Type.Optional(
          Type.String({ description: '保存目录（相对蓝图根，默认 /IcarusII）；不存在会自动创建' }),
        ),
        name: Type.Optional(Type.String({ description: '蓝图文件名' })),
        author: Type.Optional(Type.String({ description: '作者，默认“自动生成”' })),
        version: Type.Optional(Type.String({ description: '版本号，默认当前游戏版本' })),
        attributes: Type.Optional(Type.String({ description: '蓝图属性' })),
        description: Type.Optional(Type.String({ description: '蓝图介绍' })),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const rawItem = typeof args['item'] === 'string' ? args['item'].trim() : '';
        const rate = typeof args['rate'] === 'number' ? args['rate'] : 0;
        if (!rawItem || !(rate > 0)) return reply({ error: 'item 与 rate 必填' }, false, 'bad_request');

        const kb = store?.snapshot;
        if (!kb) {
          return reply(
            {
              error:
                '知识库尚未建立，无法按真实配方生成。先进入存档，等 sidecar 完成原型数据导出（约 1 秒）后再试。',
            },
            false,
            'kb_unavailable',
          );
        }
        {
          const dirLabel =
            typeof args['dir'] === 'string' && args['dir'] ? args['dir'] : '/IcarusII';
          const ok = await confirmFileWrite(
            'blueprint_generate',
            { item: rawItem, rate, dir: args['dir'], name: args['name'] },
            `生成并保存蓝图：${rawItem} ${rate}/min\n保存目录：${dirLabel}`,
          );
          if (!ok) return reply({ error: '玩家拒绝了生成蓝图' }, false, 'confirmation_denied');
        }

        // 别名（蓝马达/绿马达/白糖…）先走 curated + wiki；精确名直接命中 KB。
        let resolution;
        if (aliases) {
          resolution = await aliases.resolve(kb, rawItem);
        } else {
          const item = /^\d+$/.test(rawItem) ? kb.resolveItem(Number(rawItem)) : kb.resolveItem(rawItem);
          resolution = {
            input: rawItem,
            ...(item ? { item, alias: item.name } : {}),
            source: item ? 'kb' : 'none',
            candidates: [],
          };
        }
        const resolved = resolution.item;
        if (!resolved) {
          if (resolution.candidates.length > 1) {
            return reply(
              {
                error: `「${rawItem}」对应多个物品，请让玩家确认要哪一个`,
                candidates: resolution.candidates,
              },
              false,
              'ambiguous_item',
            );
          }
          return reply({ error: `找不到物品：${rawItem}` }, false, 'bad_request');
        }
        const aliasInfo =
          resolution.source === 'kb'
            ? undefined
            : { input: rawItem, resolved: resolved.name, source: resolution.source };

        const tiers = getTiers?.() ?? null;
        const explicitBuildingTier =
          args['buildingTier'] === 'base' || args['buildingTier'] === 'mk2' || args['buildingTier'] === 'mk3';
        const buildingTier = explicitBuildingTier
          ? (args['buildingTier'] as 'base' | 'mk2' | 'mk3')
          : tiers?.assembler === 2
            ? 'mk3'
            : tiers?.assembler === 1
              ? 'mk2'
              : 'base';
        const explicitBelt = typeof args['beltType'] === 'number';
        const explicitInsert = typeof args['insertType'] === 'number';
        const explicitStack = typeof args['stackSize'] === 'number';
        const beltType = explicitBelt
          ? (args['beltType'] as number)
          : typeof tiers?.belt === 'number'
            ? Math.max(0, Math.min(2, Math.floor(tiers.belt)))
            : 2;
        const insertType = explicitInsert
          ? (args['insertType'] as number)
          : typeof tiers?.inserter === 'number'
            ? Math.max(0, Math.min(3, Math.floor(tiers.inserter)))
            : 2;
        const stackSize = explicitStack
          ? (args['stackSize'] as number)
          : tiers?.stacking
            ? 4
            : 1;
        const tierSource = explicitBuildingTier || explicitBelt || explicitInsert || explicitStack
          ? 'explicit'
          : tiers
            ? 'mod'
            : 'default';
        const depth = typeof args['depth'] === 'number' ? args['depth'] : undefined;
        const fromRaw = typeof args['from'] === 'string' ? args['from'].trim() : '';
        let from: 'ore' | 'smelted' | number | undefined;
        let fromLabel = '从原矿投入';
        if (fromRaw === 'ore' || fromRaw === 'smelted') {
          from = fromRaw;
          fromLabel = fromRaw === 'smelted' ? '从冶炼产物投入' : '从原矿投入';
        } else if (fromRaw) {
          const fromItem = aliases
            ? (await aliases.resolve(kb, fromRaw)).item
            : /^\d+$/.test(fromRaw)
              ? kb.resolveItem(Number(fromRaw))
              : kb.resolveItem(fromRaw);
          if (!fromItem) {
            return reply({ error: `找不到起点物品：${fromRaw}` }, false, 'bad_request');
          }
          from = fromItem.id;
          fromLabel = `从${fromItem.name}投入`;
        }
        const blueprintName =
          typeof args['name'] === 'string' && args['name'].trim()
            ? args['name'].trim()
            : `IcarusII-${resolved.name}-${rate}`;
        const author = typeof args['author'] === 'string' && args['author'] ? args['author'] : '自动生成';
        const version =
          typeof args['version'] === 'string' && args['version'] ? args['version'] : kb.gameVersion;
        const attributes = typeof args['attributes'] === 'string' ? args['attributes'] : '';
        const rows = typeof args['rows'] === 'number' ? args['rows'] : 1;

        let tileSet;
        try {
          tileSet = buildBlueprintTiles(kb, resolved.id, rate, {
            buildingTier,
            ...(depth !== undefined ? { depth } : {}),
            author,
            version,
            attributes,
            blueprintName,
            beltType,
            insertType,
            rows,
            stackSize,
            ...(from !== undefined ? { from } : {}),
            ...(typeof args['description'] === 'string' && args['description']
              ? { description: () => String(args['description']) }
              : {}),
            ...(typeof args['description'] !== 'string' || !args['description']
              ? {
                  description: (index: number, count: number, tileRate: number, totalRate: number) =>
                    count > 1
                      ? `全链路（${fromLabel}）：第 ${index}/${count} 张，每张约 ${tileRate}/min，合计 ${totalRate}/min`
                      : `全链路（${fromLabel}）：${tileRate}/min`,
                }
              : {}),
          });
        } catch (err) {
          return reply(
            { error: err instanceof Error ? err.message : String(err), item: resolved.name, rate },
            false,
            'blueprint_generate_failed',
          );
        }

        let targetDir;
        try {
          targetDir = ws.resolveTargetDir(typeof args['dir'] === 'string' ? args['dir'] : undefined);
        } catch (err) {
          return reply(
            { error: err instanceof Error ? err.message : String(err) },
            false,
            'blueprint_generate_failed',
          );
        }
        const tiles = [];
        for (const tile of tileSet.tiles) {
          const unique = ws.uniqueFileName(targetDir, sanitizeBlueprintName(tile.name));
          const filePath = unique.virtual.real;
          try {
            ws.write(unique.virtual.virtual, tile.text);
          } catch (err) {
            return reply(
              { error: err instanceof Error ? err.message : String(err), dir: targetDir.real },
              false,
              'blueprint_save_failed',
            );
          }
          tiles.push({
            fileName: unique.name,
            relPath: unique.virtual.relPath,
            path: filePath,
            bytes: tile.text.length,
            rate: tile.rate,
            width: tile.meta.width,
            height: tile.meta.height,
            rows: tile.meta.rows,
            buildings: tile.meta.buildings,
            machines: tile.meta.machines,
            stages: tile.meta.stages,
            warnings: tile.meta.warnings,
          });
        }

        const firstTile = tiles[0];
        if (!firstTile) {
          return reply({ error: '没有生成任何蓝图' }, false, 'blueprint_generate_failed');
        }
        const first = tileSet.tiles[0];
        const info = first ? inspectBlueprintText(first.text) : null;
        const tierWarnings: string[] = [];
        if (tiers && typeof tiers.inserter === 'number' && tiers.inserter < 3) {
          tierWarnings.push(
            '当前混带布局器使用集装分拣器（2014）；玩家未解锁集装分拣器时这张蓝图可能无法粘贴。',
          );
        }
        const warnings = [...new Set([...tierWarnings, ...tiles.flatMap((tile) => tile.warnings)])];
        const machines = tiles.reduce((sum, tile) => sum + tile.machines, 0);
        const totalBuildings = tiles.reduce((sum, tile) => sum + tile.buildings, 0);
        const totalBytes = tiles.reduce((sum, tile) => sum + tile.bytes, 0);
        const raw = first?.plan.raw ?? [];
        const byproducts = first?.plan.byproducts ?? [];
        const description =
          info?.description ??
          `全链路（${fromLabel}）：${rate}/min${tiles.length > 1 ? `，分 ${tiles.length} 张` : ''}`;
        const multi = tiles.length > 1;
        return reply(
          {
            saved: true,
            source: 'generated',
            path: firstTile.path,
            relPath: firstTile.relPath,
            fileName: firstTile.fileName,
            dir: targetDir.real,
            dirRel: targetDir.relPath,
            files: tiles.map((tile) => tile.path),
            bytes: totalBytes,
            item: resolved.name,
            itemId: resolved.id,
            rate,
            buildingTier,
            ...(aliasInfo ? { alias: aliasInfo } : {}),
            from: fromLabel,
            tierSource,
            ...(tiers ? { blueprintTiers: tiers } : {}),
            name: info?.name ?? blueprintName,
            author: info?.author ?? author,
            version: info?.version ?? version,
            ...(info?.attributes !== undefined ? { attributes: info.attributes } : {}),
            description,
            ...(info?.iconItemId !== undefined ? { iconItemId: info.iconItemId } : {}),
            buildings: firstTile.buildings,
            totalBuildings,
            width: Math.max(...tiles.map((tile) => tile.width)),
            height: Math.max(...tiles.map((tile) => tile.height)),
            rows: firstTile.rows,
            machines,
            tileCount: tiles.length,
            ratePerTile: tileSet.ratePerTile,
            tiles,
            stages: firstTile.stages,
            raw,
            byproducts,
            warnings,
            validation: { ok: warnings.length === 0, warnings },
            note: multi
              ? `单张混带蓝图的总线容量有限，已拆成 ${tiles.length} 张（每张约 ${tileSet.ratePerTile}/min，合计 ${rate}/min）。` +
                '全部铺下才达到目标产量；先打开第一张确认布局，再逐张粘贴。'
              : `已按知识库 ${kb.gameVersion} 的配方生成：${firstTile.stages.length} 个阶段、${machines} 台机器；` +
                `${fromLabel}（不含采矿机/矿脉），含各阶段机器、传送带与分拣器。粘贴后先检查进料/出料是否接通。`,
          },
          true,
        );
      },
    },
  ];
}
