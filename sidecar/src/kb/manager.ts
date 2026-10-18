/**
 * 知识库导出编排（M3）。
 *
 * 时序：
 *   1. hello 到达 → onHello() 尝试读磁盘缓存（同版本/语言）；
 *   2. 进入存档（save.bind）→ ensure()：缓存命中就直接可用，否则向 mod
 *      发 game_export_prototypes 分块导出；
 *   3. 导出完成后写入 store（内存 + 磁盘），kb_* 工具立即可用。
 *
 * 导出调用是 internal tool_call：mod 不弹工具卡、不算模型可见工具。
 */

import type { JsonObject, ToolResultPayload } from '../protocol.js';
import type { Logger } from '../log.js';
import { errorFields, log as rootLog } from '../log.js';
import { KnowledgeStore } from './store.js';
import { KB_SCHEMA_VERSION, type KbData, type KbText } from './types.js';

export interface KbTransport {
  callTool(
    name: string,
    args: JsonObject,
    options: { timeoutMs: number; internal?: boolean },
  ): Promise<ToolResultPayload>;
}

export interface KnowledgeManagerOptions {
  store: KnowledgeStore;
  logger?: Logger;
  /** 每次向 mod 要多少行。 */
  chunkSize?: number;
  /** 单次工具调用超时。 */
  timeoutMs?: number;
}

const EXPORT_TOOL = 'game_export_prototypes';
const EXPORT_TEXT_TOOL = 'game_export_text';
type KbExportSet = 'items' | 'recipes' | 'techs' | 'models' | 'veins' | 'signals';
const EXPORT_SETS: readonly KbExportSet[] = ['items', 'recipes', 'techs', 'models', 'veins', 'signals'];

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export class KnowledgeManager {
  private readonly store: KnowledgeStore;
  private readonly logger: Logger;
  private readonly chunkSize: number;
  private readonly timeoutMs: number;
  private chain: Promise<boolean> = Promise.resolve(false);
  private inflight: Promise<boolean> | null = null;

  constructor(options: KnowledgeManagerOptions) {
    this.store = options.store;
    this.logger = (options.logger ?? rootLog).child({ component: 'kb' });
    this.chunkSize = Math.max(1, Math.min(options.chunkSize ?? 200, 1000));
    this.timeoutMs = Math.max(1000, options.timeoutMs ?? 12000);
  }

  get knowledge(): KnowledgeStore {
    return this.store;
  }

  /** hello 阶段只读缓存，不发起导出（此时 mod 可能还在主菜单）。 */
  onHello(gameVersion: string, locale: string): void {
    if (!gameVersion || gameVersion === 'unknown') return;
    if (this.store.isReady && this.store.currentKey === `${gameVersion}\u0000${locale}`) return;
    const loaded = this.store.loadFromDisk(gameVersion, locale);
    if (loaded) {
      this.logger.info('知识库缓存已加载', { ...this.store.meta() });
    }
  }

  /**
   * 确保当前版本/语言的知识库可用。多次调用串行化；失败不会抛出，
   * 由 kb_* 工具返回 kb_unavailable。
   */
  ensure(transport: KbTransport, gameVersion: string, locale: string): Promise<boolean> {
    const task = async (): Promise<boolean> => {
      if (!gameVersion || gameVersion === 'unknown') {
        this.logger.warn('知识库导出跳过：gameVersion 未知');
        return false;
      }
      const key = `${gameVersion}\u0000${locale}`;
      if (this.store.isReady && this.store.currentKey === key) return true;

      const cached = this.store.loadFromDisk(gameVersion, locale);
      if (cached) {
        this.logger.info('知识库缓存已加载', { ...this.store.meta() });
        return true;
      }

      this.store.setLoading();
      this.logger.info('开始导出知识库', { gameVersion, locale, chunkSize: this.chunkSize });
      try {
        const data = await this.exportAll(transport, gameVersion, locale);
        this.store.setData(data);
        this.logger.info('知识库已建立', { ...this.store.meta() });
        return true;
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        this.store.setError(message);
        this.logger.warn('知识库导出失败', { gameVersion, locale, message, ...errorFields(err) });
        return false;
      }
    };

    const next = this.chain.then(task, task);
    this.chain = next.catch(() => false);
    this.inflight = next;
    void next.finally(() => {
      if (this.inflight === next) this.inflight = null;
    });
    return next;
  }

  /** 第一次 prompt 前等知识库一小会儿，避免玩家首问拿到旧版本/ kb_unavailable。 */
  async whenReady(timeoutMs = 2500): Promise<boolean> {
    const pending = this.inflight;
    if (!pending) return this.store.isReady;
    const result = await Promise.race([
      pending,
      new Promise<boolean>((resolve) => {
        const timer = setTimeout(() => resolve(false), Math.max(100, timeoutMs));
        timer.unref?.();
      }),
    ]);
    return result || this.store.isReady;
  }

  private async exportAll(
    transport: KbTransport,
    gameVersion: string,
    locale: string,
  ): Promise<KbData> {
    const collected: Record<KbExportSet, unknown[]> = {
      items: [],
      recipes: [],
      techs: [],
      models: [],
      veins: [],
      signals: [],
    };

    for (const set of EXPORT_SETS) {
      let offset = 0;
      let guard = 0;
      for (;;) {
        if (guard++ > 200) throw new Error(`${set} 导出分块过多，已中止`);
        const result = await transport.callTool(
          EXPORT_TOOL,
          { set, offset, limit: this.chunkSize, locale },
          { timeoutMs: this.timeoutMs, internal: true },
        );
        if (!result.ok) {
          const message = result.error?.message ?? '未返回错误说明';
          throw new Error(`${set} 导出失败（offset=${offset}）：${message}`);
        }
        if (!isObject(result.data)) throw new Error(`${set} 导出结果不是对象`);
        const data = result.data;
        const rows = data['rows'];
        if (!Array.isArray(rows)) throw new Error(`${set} 导出结果缺少 rows[]`);
        collected[set].push(...rows);
        if (data['done'] === true || rows.length === 0) break;
        offset += rows.length;
      }
      this.logger.debug('原型集导出完成', { set, rows: collected[set].length });
    }

    if (collected.items.length === 0) throw new Error('导出结果没有物品（items）');
    if (collected.recipes.length === 0) throw new Error('导出结果没有配方（recipes）');
    if (collected.techs.length === 0) throw new Error('导出结果没有科技（techs）');

    // M3b 文本是增强项：失败时保留结构化知识库，不阻断 M3a。
    let texts: KbText[] = [];
    try {
      texts = await this.exportTexts(transport);
    } catch (err) {
      this.logger.warn('知识库文本导出失败（结构化数据仍可用）', {
        message: err instanceof Error ? err.message : String(err),
        ...errorFields(err),
      });
    }

    return {
      schemaVersion: KB_SCHEMA_VERSION,
      gameVersion,
      locale,
      builtAt: new Date().toISOString(),
      source: 'runtime-export',
      items: collected.items as KbData['items'],
      recipes: collected.recipes as KbData['recipes'],
      techs: collected.techs as KbData['techs'],
      models: collected.models as KbData['models'],
      veins: collected.veins as KbData['veins'],
      signals: collected.signals as KbData['signals'],
      texts,
    };
  }

  private async exportTexts(transport: KbTransport): Promise<KbText[]> {
    const out: KbText[] = [];
    let offset = 0;
    let guard = 0;
    for (;;) {
      if (guard++ > 200) throw new Error('文本导出分块过多，已中止');
      const result = await transport.callTool(
        EXPORT_TEXT_TOOL,
        { offset, limit: this.chunkSize },
        { timeoutMs: this.timeoutMs, internal: true },
      );
      if (!result.ok) {
        const message = result.error?.message ?? '未返回错误说明';
        throw new Error(`文本导出失败（offset=${offset}）：${message}`);
      }
      if (!isObject(result.data)) throw new Error('文本导出结果不是对象');
      const data = result.data;
      const rows = data['rows'];
      if (!Array.isArray(rows)) throw new Error('文本导出结果缺少 rows[]');
      out.push(...(rows as KbText[]));
      if (data['done'] === true || rows.length === 0) break;
      offset += rows.length;
    }
    this.logger.debug('文本导出完成', { rows: out.length });
    return out;
  }
}
