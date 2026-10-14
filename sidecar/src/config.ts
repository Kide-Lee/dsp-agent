/**
 * sidecar 配置存储（协议附录 A）。
 *
 * 落盘位置：`<data-dir>/config.json`，形如
 *
 *   { "provider": "deepseek", "model": "deepseek-v4-flash",
 *     "apiKey": "sk-…", "baseUrl": null }
 *
 * 硬性约束（附录 A.4）：
 *   1. `apiKey` **只允许**出现在 `config.set` 的入站载荷与本文件里；
 *      任何出站帧、日志、错误消息都必须脱敏。
 *   2. 日志里最多打印前 4 位与总长度，例如 `sk-a…(48)`（见 `maskSecret`）。
 *   3. Linux 下本文件权限必须是 `0600`。
 *
 * 读盘必须容错：文件不存在 / JSON 损坏 / 结构不对 ⇒ 视为「未配置」，绝不抛异常。
 */

import { chmodSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { errorFields, type Logger, log as rootLog } from './log.js';
import type { ConfigStateValue, ConfigUpdatePayload, ConfigView } from './protocol.js';

/** pi 内置目录里的权威取值（`pi-ai/dist/providers/data/deepseek.json`）。 */
export const DEFAULT_PROVIDER = 'deepseek';
export const DEFAULT_MODEL = 'deepseek-v4-flash';

export const CONFIG_FILE_NAME = 'config.json';

/** 内存中的完整配置。`apiKey` 为 null 表示未配置。 */
export interface ConfigData {
  provider: string;
  model: string;
  apiKey: string | null;
  baseUrl: string | null;
}

/** 只能由上游请求结果推导出来的状态（不落盘：重启后重新从 key 推导）。 */
export type RuntimeState = 'invalid_key' | 'unreachable';

// ---------------------------------------------------------------- 脱敏

/** 日志/错误消息里唯一允许出现的密钥形态：前 4 位 + 总长度。 */
export function maskSecret(value: string | null | undefined): string {
  if (typeof value !== 'string' || value.length === 0) return '(none)';
  return `${value.slice(0, 4)}…(${value.length})`;
}

/** 兜底：形如 `sk-…` 的长 token（即便不是当前配置的 key）也要打码。 */
const LOOSE_SECRET_PATTERN = /\bsk-[A-Za-z0-9_-]{12,}/g;

/**
 * 把文本里出现的密钥（以及形似密钥的 token）替换成脱敏形态。
 * 用于所有会被写出到 socket 或日志的字符串。
 */
export function redactSecrets(text: string, secrets: readonly (string | null | undefined)[]): string {
  let out = text;
  for (const secret of secrets) {
    // 太短的「密钥」会在正常文本里误伤，只处理足够长的。
    if (typeof secret !== 'string' || secret.length < 6) continue;
    out = out.split(secret).join(maskSecret(secret));
  }
  return out.replace(LOOSE_SECRET_PATTERN, (match) => maskSecret(match));
}

// ---------------------------------------------------------------- 存储

export interface ConfigStoreOptions {
  /** 配置文件绝对路径。 */
  path: string;
  defaults?: { provider?: string; model?: string };
  /**
   * true（pi agent）：没有 key ⇒ `missing_key`，prompt 回 `config_required`。
   * false（echo agent）：不需要凭证，状态恒为 `ready`。
   */
  requireApiKey?: boolean;
  logger?: Logger;
}

export class ConfigStore {
  private readonly path: string;
  private readonly logger: Logger;
  private readonly requireApiKey: boolean;
  private data: ConfigData;
  private runtime: { state: ConfigStateValue; message: string | null } | null = null;

  constructor(options: ConfigStoreOptions) {
    this.path = options.path;
    this.requireApiKey = options.requireApiKey ?? true;
    this.logger = (options.logger ?? rootLog).child({ component: 'config' });
    this.data = {
      provider: options.defaults?.provider ?? DEFAULT_PROVIDER,
      model: options.defaults?.model ?? DEFAULT_MODEL,
      apiKey: null,
      baseUrl: null,
    };
    this.load();
  }

  /** 容错读盘：任何异常都退化成「未配置」。 */
  private load(): void {
    let raw: string;
    try {
      raw = readFileSync(this.path, 'utf8');
    } catch (err) {
      const code = (err as NodeJS.ErrnoException).code;
      if (code !== 'ENOENT') {
        this.logger.warn('读取 config.json 失败，按未配置处理', {
          path: this.path,
          ...errorFields(err),
        });
      }
      return;
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      this.logger.warn('config.json 不是合法 JSON，按未配置处理', { path: this.path });
      return;
    }

    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      this.logger.warn('config.json 结构非法，按未配置处理', { path: this.path });
      return;
    }

    const obj = parsed as Record<string, unknown>;
    const provider = obj['provider'];
    if (typeof provider === 'string' && provider.length > 0) this.data.provider = provider;

    const model = obj['model'];
    if (typeof model === 'string' && model.length > 0) this.data.model = model;

    const apiKey = obj['apiKey'];
    if (typeof apiKey === 'string' && apiKey.length > 0) this.data.apiKey = apiKey;

    const baseUrl = obj['baseUrl'];
    if (typeof baseUrl === 'string' && baseUrl.length > 0) this.data.baseUrl = baseUrl;

    this.logger.debug('已加载 config.json', {
      path: this.path,
      provider: this.data.provider,
      model: this.data.model,
      apiKey: maskSecret(this.data.apiKey),
      baseUrl: this.data.baseUrl,
    });
  }

  /** 当前配置的快照（含明文 key，仅供内部构造请求使用）。 */
  get(): ConfigData {
    return { ...this.data };
  }

  hasKey(): boolean {
    return typeof this.data.apiKey === 'string' && this.data.apiKey.length > 0;
  }

  /** 只用于日志。 */
  get maskedApiKey(): string {
    return maskSecret(this.data.apiKey);
  }

  /**
   * 应用一次 `config.set` 增量更新并落盘。
   * 更新成功会清掉上游推导出来的临时状态（重新从 key 推导）。
   */
  update(patch: ConfigUpdatePayload): ConfigView {
    if (patch.provider !== undefined) this.data.provider = patch.provider;
    if (patch.model !== undefined) this.data.model = patch.model;
    if (patch.apiKey !== undefined) {
      this.data.apiKey = patch.apiKey.length > 0 ? patch.apiKey : null;
    }
    if (patch.baseUrl !== undefined) {
      this.data.baseUrl = patch.baseUrl && patch.baseUrl.length > 0 ? patch.baseUrl : null;
    }

    this.runtime = null;
    this.persist();

    this.logger.info('配置已更新', {
      provider: this.data.provider,
      model: this.data.model,
      apiKey: maskSecret(this.data.apiKey),
      baseUrl: this.data.baseUrl,
      path: this.path,
    });

    return this.view();
  }

  /** 记录上游请求推导出来的状态；返回最新视图。 */
  setRuntimeState(state: RuntimeState, message: string | null = null): ConfigView {
    this.runtime = { state, message };
    return this.view();
  }

  /** 清除上游推导出来的临时状态（下一次成功即回到 ready）。 */
  clearRuntimeState(): ConfigView {
    this.runtime = null;
    return this.view();
  }

  /** 当前对外可见的配置状态（**不含 apiKey**，附录 A.1）。 */
  view(): ConfigView {
    const base = { provider: this.data.provider, model: this.data.model };
    if (!this.requireApiKey) return { state: 'ready', ...base, message: null };
    if (!this.hasKey()) return { state: 'missing_key', ...base, message: null };
    const runtime = this.runtime;
    if (!runtime) return { state: 'ready', ...base, message: null };
    return { state: runtime.state, ...base, message: runtime.message };
  }

  /** 防呆：把 store 直接塞进日志/JSON 时也只有脱敏形态。 */
  toJSON(): Record<string, unknown> {
    return {
      provider: this.data.provider,
      model: this.data.model,
      apiKey: maskSecret(this.data.apiKey),
      baseUrl: this.data.baseUrl,
    };
  }

  private persist(): void {
    const dir = dirname(this.path);
    mkdirSync(dir, { recursive: true, mode: 0o700 });
    const tmp = `${this.path}.tmp-${process.pid}`;
    writeFileSync(tmp, `${JSON.stringify(this.data, null, 2)}\n`, {
      encoding: 'utf8',
      mode: 0o600,
    });
    renameSync(tmp, this.path);
    if (process.platform !== 'win32') {
      try {
        chmodSync(this.path, 0o600);
      } catch (err) {
        this.logger.warn('设置 config.json 权限失败', { path: this.path, ...errorFields(err) });
      }
    }
  }
}
