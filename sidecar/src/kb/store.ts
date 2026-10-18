/**
 * 知识库落盘/内存状态（M3）。
 *
 * 缓存路径：`<dataDir>/kb/ldb-v1-<gameVersion>-<locale>.json`。
 * 写入用临时文件 + rename，避免导出中断留下半个 JSON。
 */

import { existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { KnowledgeBase, parseKbData } from './knowledge.js';
import { KB_SCHEMA_VERSION, type KbData } from './types.js';

export type KnowledgeState = 'empty' | 'loading' | 'ready' | 'error';

function safePart(value: string): string {
  const cleaned = value.replace(/[^a-zA-Z0-9._-]/g, '_').slice(0, 64);
  return cleaned.length > 0 ? cleaned : 'unknown';
}

export class KnowledgeStore {
  private readonly dataDir: string;
  private kb: KnowledgeBase | null = null;
  private state: KnowledgeState = 'empty';
  private error: string | null = null;
  private key: string | null = null;

  constructor(dataDir: string) {
    this.dataDir = dataDir;
  }

  get isReady(): boolean {
    return this.kb !== null;
  }

  get snapshot(): KnowledgeBase | null {
    return this.kb;
  }

  get currentKey(): string | null {
    return this.key;
  }

  meta(): Record<string, unknown> {
    return {
      state: this.state,
      gameVersion: this.kb?.gameVersion ?? null,
      locale: this.kb?.locale ?? null,
      error: this.error,
      counts: this.kb?.stats ?? null,
    };
  }

  setLoading(): void {
    if (this.kb) return;
    this.state = 'loading';
    this.error = null;
  }

  setError(message: string): void {
    this.error = message;
    this.state = this.kb ? 'ready' : 'error';
  }

  /** 用已经组装好的数据替换内存库，并落盘。 */
  setData(data: KbData): KnowledgeBase {
    const validated = parseKbData(data);
    const kb = new KnowledgeBase(validated);
    this.kb = kb;
    this.key = `${kb.gameVersion}\u0000${kb.locale}`;
    this.state = 'ready';
    this.error = null;
    try {
      this.saveToDisk(validated);
    } catch (err) {
      // 落盘失败不影响本次会话使用；下一次会重新导出。
      this.error = `知识库落盘失败：${err instanceof Error ? err.message : String(err)}`;
    }
    return kb;
  }

  fileFor(gameVersion: string, locale: string): string {
    return join(this.dataDir, 'kb', `ldb-v${KB_SCHEMA_VERSION}-${safePart(gameVersion)}-${safePart(locale)}.json`);
  }

  /** 尝试从磁盘恢复；版本/语言不匹配或文件损坏时返回 null。 */
  loadFromDisk(gameVersion: string, locale: string): KnowledgeBase | null {
    const file = this.fileFor(gameVersion, locale);
    if (!existsSync(file)) return null;
    try {
      const parsed: unknown = JSON.parse(readFileSync(file, 'utf8'));
      const data = parseKbData(parsed);
      if (data.gameVersion !== gameVersion || data.locale !== locale) return null;
      const kb = new KnowledgeBase(data);
      this.kb = kb;
      this.key = `${gameVersion}\u0000${locale}`;
      this.state = 'ready';
      this.error = null;
      return kb;
    } catch (err) {
      this.error = `知识库缓存损坏：${err instanceof Error ? err.message : String(err)}`;
      return null;
    }
  }

  private saveToDisk(data: KbData): void {
    const dir = join(this.dataDir, 'kb');
    mkdirSync(dir, { recursive: true });
    const target = this.fileFor(data.gameVersion, data.locale);
    const tmp = `${target}.tmp-${process.pid}`;
    try {
      writeFileSync(tmp, `${JSON.stringify(data)}\n`, { encoding: 'utf8', mode: 0o600 });
      renameSync(tmp, target);
    } catch (err) {
      rmSync(tmp, { force: true });
      throw err;
    }
  }
}
