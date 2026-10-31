/**
 * sidecar 配置存储（协议附录 A）。
 *
 * 落盘位置：`<data-dir>/config.json`。M3 起支持**多供应商 / 多模型**：
 *
 *   {
 *     "providers": {
 *       "deepseek": {
 *         "apiKey": "sk-…",
 *         "baseUrl": null,
 *         "models": [
 *           { "id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash" },
 *           { "id": "deepseek-v4-pro", "name": "DeepSeek V4 Pro" }
 *         ]
 *       }
 *     },
 *     "active": { "provider": "deepseek", "model": "deepseek-v4-flash" },
 *     "thinkingLevel": "high",
 *     "permissionLevel": "limited"
 *   }
 *
 * 兼容旧格式（单条 provider/model/apiKey/baseUrl）：读盘时自动迁移成一条
 * provider；写盘一律用新格式。
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
import {
  DEFAULT_PERMISSION_LEVEL,
  DEFAULT_THINKING_LEVEL,
  isPermissionLevel,
  isThinkingLevel,
  normalizeDisabledTools,
  type ConfigStateValue,
  type ConfigUpdatePayload,
  type ConfigView,
  type ModelEntry,
  type ModelInputModality,
  type PermissionLevel,
  type ProviderPatch,
  type ProviderView,
  type ThinkingLevelName,
} from './protocol.js';

/** pi 内置目录里的权威取值（`pi-ai/dist/providers/data/deepseek.json`）。 */
export const DEFAULT_PROVIDER = 'deepseek';
export const DEFAULT_MODEL = 'deepseek-v4-flash';

/**
 * 首次启动时给 DeepSeek 预置的模型。
 *
 * `input` 是对 pi 目录的**覆盖**：目录里 DeepSeek 仍被标成只收文本，
 * 但实际模型能读图。不写这一行，pi 的 provider 适配层会把工具结果里的截图丢掉
 * （判据是 `model.input.includes("image")`）。
 */
export const DEFAULT_DEEPSEEK_MODELS: readonly ModelEntry[] = [
  { id: 'deepseek-v4-flash', name: 'DeepSeek V4 Flash', input: ['text', 'image'] },
  { id: 'deepseek-v4-pro', name: 'DeepSeek V4 Pro', input: ['text', 'image'] },
];

/**
 * 老配置文件的读时兜底：`input` 是后加的字段，已有 `config.json` 里没有它。
 * 不兜底的话，升级后截图会被静默丢弃，而玩家看不出原因。
 */
export function defaultModelInput(
  provider: string,
  modelId: string,
): ModelInputModality[] | null {
  if (provider !== DEFAULT_PROVIDER) return null;
  const preset = DEFAULT_DEEPSEEK_MODELS.find((entry) => entry.id === modelId);
  return preset?.input ? [...preset.input] : null;
}

const PROVIDER_DISPLAY_NAMES: Record<string, string> = {
  deepseek: 'DeepSeek',
  openai: 'OpenAI',
  anthropic: 'Anthropic',
  google: 'Google',
  'google-vertex': 'Google Vertex',
  'amazon-bedrock': 'Amazon Bedrock',
  openrouter: 'OpenRouter',
  xai: 'xAI',
  groq: 'Groq',
  mistral: 'Mistral',
  cerebras: 'Cerebras',
  nvidia: 'NVIDIA',
  together: 'Together',
  fireworks: 'Fireworks',
  moonshotai: 'Moonshot',
  'moonshotai-cn': 'Moonshot (CN)',
  minimax: 'MiniMax',
  'minimax-cn': 'MiniMax (CN)',
  zai: 'Z.ai',
  'zai-coding-cn': 'Z.ai Coding (CN)',
  'github-copilot': 'GitHub Copilot',
  'vercel-ai-gateway': 'Vercel AI Gateway',
  'cloudflare-workers-ai': 'Cloudflare Workers AI',
  'cloudflare-ai-gateway': 'Cloudflare AI Gateway',
  'openai-codex': 'OpenAI Codex',
  'azure-openai-responses': 'Azure OpenAI',
  'radius': 'Radius',
  'ant-ling': 'Ant Ling',
  qwen: 'Qwen',
  'qwen-token-plan': 'Qwen Token Plan',
  huggingface: 'HuggingFace',
  opencode: 'OpenCode',
  kimi: 'Kimi',
  xiaomi: 'Xiaomi',
};

export function providerDisplayName(id: string): string {
  return PROVIDER_DISPLAY_NAMES[id] ?? id;
}

export const CONFIG_FILE_NAME = 'config.json';

/** 单个供应商的落盘数据。`apiKey` 为 null 表示未配置。 */
export interface ProviderData {
  apiKey: string | null;
  baseUrl: string | null;
  models: ModelEntry[];
}

/** 内存中的完整配置。 */
export interface ConfigData {
  providers: Record<string, ProviderData>;
  activeProvider: string;
  activeModel: string;
  /** 思考档位；`off` = 不请求思考，默认 `high`。 */
  thinkingLevel: ThinkingLevelName;
  /** 权限档位；`limited`（受限）= risk 1 放行、risk 2 确认，默认。 */
  permissionLevel: PermissionLevel;
  /** 蓝图根目录；null = 使用系统默认的 Documents/Dyson Sphere Program/Blueprint。 */
  blueprintDir: string | null;
  /** 中文 Wiki API 端点；null = 使用内置默认值。 */
  wikiZhApi: string | null;
  /** 英文 Wiki API 端点；null = 使用内置默认值。 */
  wikiEnApi: string | null;
  /** A.14.5：被玩家关闭的工具名；空数组 = 全部启用。 */
  disabledTools: string[];
}

/**
 * `get()` 的返回值：在完整配置之外，把**当前**供应商的 key/baseUrl 展开，
 * 保持 agent 侧原有 `data.apiKey` / `data.baseUrl` 的用法。
 */
export interface ResolvedConfig extends ConfigData {
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

// ---------------------------------------------------------------- 解析工具

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isSafeId(id: string): boolean {
  return id.length > 0 && id !== '__proto__' && id !== 'constructor' && id !== 'prototype';
}

/** 归一化 `input` 模态列表；非法取值返回 null（表示"沿用目录默认"）。 */
function normalizeModelInput(raw: unknown): ModelInputModality[] | null {
  if (raw === undefined || raw === null) return null;
  if (!Array.isArray(raw) || raw.length === 0) return null;
  const out: ModelInputModality[] = [];
  for (const item of raw) {
    if (item !== 'text' && item !== 'image') return null;
    if (!out.includes(item)) out.push(item);
  }
  return out;
}

/** 归一化模型列表：去空、去重（后出现的覆盖名字），name 缺省用 id。 */
function normalizeModels(raw: unknown): ModelEntry[] | null {
  if (!Array.isArray(raw)) return null;
  const models: ModelEntry[] = [];
  const seen = new Set<string>();
  for (const item of raw) {
    if (!isPlainObject(item)) return null;
    const id = item['id'];
    if (typeof id !== 'string' || id.length === 0) return null;
    const name = item['name'];
    if (name !== undefined && name !== null && typeof name !== 'string') return null;
    const entry: ModelEntry = {
      id,
      name: typeof name === 'string' && name.length > 0 ? name : id,
    };
    const input = normalizeModelInput(item['input']);
    if (input) entry.input = input;
    if (seen.has(id)) {
      const index = models.findIndex((m) => m.id === id);
      models[index] = entry;
    } else {
      seen.add(id);
      models.push(entry);
    }
  }
  return models;
}

function cloneProviders(providers: Record<string, ProviderData>): Record<string, ProviderData> {
  const out: Record<string, ProviderData> = Object.create(null) as Record<string, ProviderData>;
  for (const [id, provider] of Object.entries(providers)) {
    out[id] = {
      apiKey: provider.apiKey,
      baseUrl: provider.baseUrl,
      models: provider.models.map((model) => ({ ...model })),
    };
  }
  return out;
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

    const provider = options.defaults?.provider ?? DEFAULT_PROVIDER;
    const model = options.defaults?.model ?? DEFAULT_MODEL;

    // 没有 key 时不给任何预置模型：列表保持为空，等玩家配好 key 再拉取。
    this.data = {
      providers: {
        [provider]: { apiKey: null, baseUrl: null, models: [] as ModelEntry[] },
      },
      activeProvider: provider,
      activeModel: model,
      thinkingLevel: DEFAULT_THINKING_LEVEL,
      permissionLevel: DEFAULT_PERMISSION_LEVEL,
      blueprintDir: null,
      wikiZhApi: null,
      wikiEnApi: null,
      disabledTools: [],
    };
    this.load();
    this.ensureActive();
  }

  /** 当前供应商；`ensureActive()` 保证它存在，但类型上仍可能 undefined。 */
  private active(): ProviderData | undefined {
    return this.data.providers[this.data.activeProvider];
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

    if (!isPlainObject(parsed)) {
      this.logger.warn('config.json 结构非法，按未配置处理', { path: this.path });
      return;
    }

    const providers: Record<string, ProviderData> = Object.create(null) as Record<string, ProviderData>;
    const rawProviders = parsed['providers'];
    if (isPlainObject(rawProviders)) {
      for (const [id, rawProvider] of Object.entries(rawProviders)) {
        if (!isSafeId(id) || !isPlainObject(rawProvider)) continue;
        const models = normalizeModels(rawProvider['models']) ?? [];
        const apiKey = rawProvider['apiKey'];
        const baseUrl = rawProvider['baseUrl'];
        providers[id] = {
          apiKey: typeof apiKey === 'string' && apiKey.length > 0 ? apiKey : null,
          baseUrl: typeof baseUrl === 'string' && baseUrl.length > 0 ? baseUrl : null,
          models,
        };
      }
    }

    // 旧格式迁移：没有 providers 时，把单条配置包成一个供应商。
    if (Object.keys(providers).length === 0) {
      const legacyProvider = parsed['provider'];
      const legacyModel = parsed['model'];
      const legacyKey = parsed['apiKey'];
      const legacyBase = parsed['baseUrl'];
      const provider = typeof legacyProvider === 'string' && legacyProvider.length > 0
        ? legacyProvider
        : this.data.activeProvider;
      const model = typeof legacyModel === 'string' && legacyModel.length > 0
        ? legacyModel
        : this.data.activeModel;
      const hasLegacyKey = typeof legacyKey === 'string' && legacyKey.length > 0;
      const models: ModelEntry[] = !hasLegacyKey
        ? []
        : provider === DEFAULT_PROVIDER
          ? DEFAULT_DEEPSEEK_MODELS.map((m) => ({ ...m }))
          : [{ id: model, name: model }];
      if (hasLegacyKey && !models.some((m) => m.id === model)) models.unshift({ id: model, name: model });
      providers[provider] = {
        apiKey: typeof legacyKey === 'string' && legacyKey.length > 0 ? legacyKey : null,
        baseUrl: typeof legacyBase === 'string' && legacyBase.length > 0 ? legacyBase : null,
        models,
      };
      this.data.activeProvider = provider;
      this.data.activeModel = model;
    }

    if (Object.keys(providers).length > 0) {
      this.data.providers = providers;
      // active 可能来自新格式或旧字段；都以「存在于 providers 里」为准。
      const active = parsed['active'];
      if (isPlainObject(active)) {
        const provider = active['provider'];
        const model = active['model'];
        if (typeof provider === 'string' && providers[provider]) this.data.activeProvider = provider;
        if (typeof model === 'string' && model.length > 0) this.data.activeModel = model;
      }
      const legacyProvider = parsed['provider'];
      const legacyModel = parsed['model'];
      if (typeof legacyProvider === 'string' && providers[legacyProvider]) {
        this.data.activeProvider = legacyProvider;
      }
      if (typeof legacyModel === 'string' && legacyModel.length > 0) {
        this.data.activeModel = legacyModel;
      }
    }

    const thinkingLevel = parsed['thinkingLevel'];
    if (isThinkingLevel(thinkingLevel)) this.data.thinkingLevel = thinkingLevel;

    const permissionLevel = parsed['permissionLevel'];
    if (isPermissionLevel(permissionLevel)) this.data.permissionLevel = permissionLevel;

    const blueprintDir = parsed['blueprintDir'];
    if (typeof blueprintDir === 'string' && blueprintDir.trim().length > 0) {
      this.data.blueprintDir = blueprintDir.trim();
    }
    const wiki = parsed['wiki'];
    if (isPlainObject(wiki)) {
      const zh = wiki['zhApi'];
      const en = wiki['enApi'];
      if (typeof zh === 'string' && zh.trim().length > 0) this.data.wikiZhApi = zh.trim();
      if (typeof en === 'string' && en.trim().length > 0) this.data.wikiEnApi = en.trim();
    }

    // A.14.5：工具开关。老配置没有该字段 = 全部启用；损坏的列表按空处理。
    const disabledTools = normalizeDisabledTools(parsed['disabledTools']);
    if (disabledTools) this.data.disabledTools = disabledTools;

    this.ensureActive();

    this.logger.debug('已加载 config.json', {
      path: this.path,
      provider: this.data.activeProvider,
      model: this.data.activeModel,
      providers: Object.keys(this.data.providers),
      apiKey: maskSecret(this.active()?.apiKey),
      thinkingLevel: this.data.thinkingLevel,
      permissionLevel: this.data.permissionLevel,
    });
  }

  /**
   * 保证 activeProvider 存在、activeModel 属于该供应商。
   * 数据可能来自旧配置/被手工改坏，这里统一兜底。
   */
  private ensureActive(): void {
    let providerId = this.data.activeProvider;
    if (!this.data.providers[providerId]) {
      const ids = Object.keys(this.data.providers);
      providerId = ids[0] ?? DEFAULT_PROVIDER;
      if (!this.data.providers[providerId]) {
        this.data.providers[providerId] = {
          apiKey: null,
          baseUrl: null,
          models: [] as ModelEntry[],
        };
      }
      this.data.activeProvider = providerId;
    }

    const provider = this.data.providers[providerId];
    if (!provider) return;
    // 空列表就是空列表：不能凭空补一个模型，否则没 key 的下拉框又会冒出不可用项。
    if (provider.models.length === 0) return;
    if (!provider.models.some((m) => m.id === this.data.activeModel)) {
      const fallback = provider.models[0]?.id ?? DEFAULT_MODEL;
      // 旧格式可能给出一个目录里没有的自定义模型：保留它，名字用 id。
      if (this.data.activeModel) {
        provider.models.push({ id: this.data.activeModel, name: this.data.activeModel });
      } else {
        this.data.activeModel = fallback;
      }
    }
  }

  /** 当前配置的快照（含明文 key，仅供内部构造请求使用）。 */
  get(): ResolvedConfig {
    this.ensureActive();
    const provider = this.active()!;
    return {
      provider: this.data.activeProvider,
      model: this.data.activeModel,
      apiKey: provider.apiKey,
      baseUrl: provider.baseUrl,
      thinkingLevel: this.data.thinkingLevel,
      permissionLevel: this.data.permissionLevel,
      providers: cloneProviders(this.data.providers),
      activeProvider: this.data.activeProvider,
      activeModel: this.data.activeModel,
      blueprintDir: this.data.blueprintDir,
      wikiZhApi: this.data.wikiZhApi,
      wikiEnApi: this.data.wikiEnApi,
      disabledTools: [...this.data.disabledTools],
    };
  }

  hasKey(): boolean {
    const provider = this.active();
    return !!provider && typeof provider.apiKey === 'string' && provider.apiKey.length > 0;
  }

  /** 只用于日志。 */
  get maskedApiKey(): string {
    return maskSecret(this.active()?.apiKey);
  }

  /** 查询某个 provider 的显示名（不落盘，纯展示）。 */
  private viewProviders(): ProviderView[] {
    return Object.entries(this.data.providers).map(([id, provider]) => {
      const hasKey = typeof provider.apiKey === 'string' && provider.apiKey.length > 0;
      return {
        id,
        name: providerDisplayName(id),
        hasKey,
        baseUrl: provider.baseUrl,
        // 没 key 时这些模型没法用，别让下拉框显示出来。
        models: hasKey ? provider.models.map((model) => ({ ...model })) : [],
        isActive: id === this.data.activeProvider,
      };
    });
  }

  /** 切换当前供应商；没有指定模型时优先保留原模型，否则用它自己的第一个。 */
  private switchActive(providerId: string, model?: string): void {
    if (!isSafeId(providerId)) return;
    const provider = this.data.providers[providerId] ?? {
      apiKey: null,
      baseUrl: null,
      models: [] as ModelEntry[],
    };
    this.data.providers[providerId] = provider;
    this.data.activeProvider = providerId;
    if (model !== undefined && model.length > 0) {
      this.data.activeModel = model;
    } else if (!provider.models.some((m) => m.id === this.data.activeModel)) {
      this.data.activeModel = provider.models[0]?.id ?? this.data.activeModel;
    }
  }

  /**
   * 应用一次 `config.set` 增量更新并落盘。
   * 更新成功会清掉上游推导出来的临时状态（重新从 key 推导）。
   */
  update(patch: ConfigUpdatePayload): ConfigView {
    // 1) 先切 active（后面的 legacy 字段要写到新的 active 上）。
    if (patch.provider !== undefined) {
      this.switchActive(patch.provider, patch.model);
    }
    if (patch.activeProvider !== undefined) {
      this.switchActive(patch.activeProvider, patch.activeModel);
    }

    // 2) 批量 upsert 供应商。
    if (patch.providerConfig) {
      for (const [id, providerPatch] of Object.entries(patch.providerConfig)) {
        if (!isSafeId(id)) continue;
        this.applyProviderPatch(id, providerPatch);
      }
    }

    // 3) 删除供应商。
    if (patch.removeProviders) {
      for (const id of patch.removeProviders) {
        if (!isSafeId(id)) continue;
        delete this.data.providers[id];
      }
    }

    // 4) 显式 active（providerConfig 之后）。
    if (patch.activeProvider !== undefined) {
      this.switchActive(patch.activeProvider, patch.activeModel);
      if (patch.activeModel === undefined) {
        const provider = this.data.providers[patch.activeProvider];
        if (provider && !provider.models.some((m) => m.id === this.data.activeModel)) {
          this.data.activeModel = provider.models[0]?.id ?? this.data.activeModel;
        }
      }
    } else if (patch.activeModel !== undefined) {
      this.data.activeModel = patch.activeModel;
    }

    // 5) 老字段：只作用在当前 active 供应商上。
    const active = this.active();
    if (active) {
      if (patch.model !== undefined) {
        this.data.activeModel = patch.model;
      }
      if (patch.apiKey !== undefined) {
        active.apiKey = patch.apiKey.length > 0 ? patch.apiKey : null;
      }
      if (patch.baseUrl !== undefined) {
        active.baseUrl = patch.baseUrl && patch.baseUrl.length > 0 ? patch.baseUrl : null;
      }
    }

    if (patch.thinkingLevel !== undefined) {
      this.data.thinkingLevel = patch.thinkingLevel;
    }
    if (patch.permissionLevel !== undefined) {
      this.data.permissionLevel = patch.permissionLevel;
    }
    if (patch.disabledTools !== undefined) {
      this.data.disabledTools = [...patch.disabledTools];
    }

    // 6) 切到新供应商后，若原模型不属于它，自动选它的第一个模型。
    const providerNow = this.active();
    if (
      providerNow &&
      providerNow.models.length > 0 &&
      !providerNow.models.some((m) => m.id === this.data.activeModel)
    ) {
      this.data.activeModel = providerNow.models[0]?.id ?? this.data.activeModel;
    }

    // 7) 删空 / 改坏后的兜底。
    if (Object.keys(this.data.providers).length === 0) {
      this.data.providers[DEFAULT_PROVIDER] = {
        apiKey: null,
        baseUrl: null,
        models: [] as ModelEntry[],
      };
      this.data.activeProvider = DEFAULT_PROVIDER;
      this.data.activeModel = DEFAULT_MODEL;
    }
    this.ensureActive();

    this.runtime = null;
    this.persist();

    this.logger.info('配置已更新', {
      provider: this.data.activeProvider,
      disabledTools: this.data.disabledTools,
      model: this.data.activeModel,
      providers: Object.keys(this.data.providers),
      apiKey: maskSecret(this.active()?.apiKey),
      baseUrl: this.active()?.baseUrl ?? null,
      thinkingLevel: this.data.thinkingLevel,
      permissionLevel: this.data.permissionLevel,
      path: this.path,
    });

    return this.view();
  }

  private applyProviderPatch(id: string, patch: ProviderPatch): void {
    const provider = this.data.providers[id] ?? {
      apiKey: null,
      baseUrl: null,
      models: [] as ModelEntry[],
    };
    if (patch.apiKey !== undefined) {
      provider.apiKey = patch.apiKey.length > 0 ? patch.apiKey : null;
    }
    if (patch.baseUrl !== undefined) {
      provider.baseUrl = patch.baseUrl && patch.baseUrl.length > 0 ? patch.baseUrl : null;
    }
    if (patch.models !== undefined) {
      provider.models = patch.models.map((m) => ({ id: m.id, name: m.name || m.id }));
    }
    this.data.providers[id] = provider;
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

  /** A.14.5：被关闭的工具名（只读快照）。 */
  disabledToolNames(): readonly string[] {
    return this.data.disabledTools;
  }

  /** 蓝图根目录；null 表示用系统默认目录。 */
  blueprintRoot(): string | null {
    return this.data.blueprintDir;
  }

  /** Wiki API 端点；null 表示用内置默认值。 */
  wikiEndpoint(lang: 'zh' | 'en'): string | null {
    return lang === 'zh' ? this.data.wikiZhApi : this.data.wikiEnApi;
  }

  /** 当前对外可见的配置状态（**不含 apiKey**，附录 A.1）。 */
  view(): ConfigView {
    this.ensureActive();
    const providers = this.viewProviders();
    const base = {
      provider: this.data.activeProvider,
      model: this.data.activeModel,
      thinkingLevel: this.data.thinkingLevel,
      permissionLevel: this.data.permissionLevel,
      disabledTools: [...this.data.disabledTools],
      providers,
      active: { provider: this.data.activeProvider, model: this.data.activeModel },
    };
    if (!this.requireApiKey) return { state: 'ready', ...base, message: null };
    if (!this.hasKey()) return { state: 'missing_key', ...base, message: null };
    const runtime = this.runtime;
    if (!runtime) return { state: 'ready', ...base, message: null };
    return { state: runtime.state, ...base, message: runtime.message };
  }

  /** 防呆：把 store 直接塞进日志/JSON 时也只有脱敏形态。 */
  toJSON(): Record<string, unknown> {
    const providers: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    for (const [id, provider] of Object.entries(this.data.providers)) {
      providers[id] = {
        apiKey: maskSecret(provider.apiKey),
        baseUrl: provider.baseUrl,
        models: provider.models.map((model) => ({ ...model })),
      };
    }
    return {
      providers,
      active: { provider: this.data.activeProvider, model: this.data.activeModel },
      provider: this.data.activeProvider,
      model: this.data.activeModel,
      apiKey: maskSecret(this.active()?.apiKey),
      baseUrl: this.active()?.baseUrl ?? null,
      thinkingLevel: this.data.thinkingLevel,
      permissionLevel: this.data.permissionLevel,
      blueprintDir: this.data.blueprintDir,
      wiki: { zhApi: this.data.wikiZhApi, enApi: this.data.wikiEnApi },
      disabledTools: [...this.data.disabledTools],
    };
  }

  private persist(): void {
    const dir = dirname(this.path);
    mkdirSync(dir, { recursive: true, mode: 0o700 });
    const tmp = `${this.path}.tmp-${process.pid}`;
    const onDisk = {
      providers: this.data.providers,
      active: { provider: this.data.activeProvider, model: this.data.activeModel },
      thinkingLevel: this.data.thinkingLevel,
      permissionLevel: this.data.permissionLevel,
      blueprintDir: this.data.blueprintDir,
      wiki: { zhApi: this.data.wikiZhApi, enApi: this.data.wikiEnApi },
      disabledTools: this.data.disabledTools,
    };
    writeFileSync(tmp, `${JSON.stringify(onDisk, null, 2)}\n`, {
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
