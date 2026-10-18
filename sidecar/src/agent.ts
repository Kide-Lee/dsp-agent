/**
 * agent 抽象与两种实现（协议 §4.5 / §4.6 / 附录 A.3）。
 *
 * - `EchoAgent`：M0 的 echo agent。**帧顺序与文本不变量与 M0 完全一致**
 *   （`C#` 端到端测试与 `test/negative.mjs` 都依赖它）：
 *     status{thinking} → stream{"echo: "} → stream{原文} → message{final} → status{idle}
 *     delta 拼接 == message 文本 == "echo: " + 原文
 * - `PiAgent`：真正由 LLM 驱动的 agent（pi SDK）。
 *   M1 是**纯对话**：不注册任何工具（`noTools: "builtin"` + `customTools: []`），
 *   并且把 `cwd` / `agentDir` 全部限制在数据目录内，绝不读写用户的 `~/.pi`
 *   或游戏安装目录。
 *
 * 本文件只通过 `import type` 引用 pi 的类型；pi 运行时是**懒加载**的
 * （`await import(...)`），因此 `--agent echo`、`--selfcheck` 以及
 * 「未配置 key」的启动路径都不会加载 pi、不会联网。
 */

import { existsSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type {
  AgentSession,
  AgentSessionEvent,
  CreateAgentSessionOptions,
  ExtensionFactory,
  ModelRuntime,
  ResourceLoader,
} from '@earendil-works/pi-coding-agent';
import type { Api, Model } from '@earendil-works/pi-ai';
import {
  defaultModelInput,
  type ConfigStore,
  maskSecret,
  providerDisplayName,
  redactSecrets,
} from './config.js';
import { ICARUS_II_PERSONA_NAME, ICARUS_II_SYSTEM_PROMPT } from './persona.js';
import { errorFields, type Logger, type LogFields, log as rootLog } from './log.js';
import type {
  AgentInfo,
  ConfigView,
  ErrorCode,
  HistoryMessage,
  JsonObject,
  ModelCatalogEntry,
  ModelCatalogResult,
  ProviderTestResult,
  StatusState,
  StreamChannel,
  ToolResultPayload,
} from './protocol.js';
import type { GameToolHost } from './tools/host.js';
import { createKbTools } from './kb/tools.js';
import type { ToolActivityPayload } from './protocol.js';
import { parseSessionLines, removeTurnFromEntries, serializeSessionEntries } from './history.js';
import { createScriptTool } from './tools/script.js';
import type { KnowledgeStore } from './kb/store.js';

/** pi SDK 的完整类型（仅类型层面；运行时不静态引用）。 */
type PiSdk = typeof import('@earendil-works/pi-coding-agent');

// ---------------------------------------------------------------- 抽象

/**
 * agent 写帧的唯一出口。`session.ts` 实现它，agent 不接触 socket。
 */
export interface AgentEmitter {
  /** 会话是否仍可写（socket 打开且握手完成）。 */
  isActive(): boolean;
  /** `status` 帧（与 M0 一致：不带 replyTo）。 */
  status(state: StatusState, detail?: string): void;
  /** `stream` 帧；delta 为空时不发送。 */
  stream(channel: StreamChannel, delta: string, replyTo: string | null): void;
  /** `message` 帧。 */
  message(text: string, replyTo: string | null, final?: boolean): void;
  /** `error` 帧；`message` 必须已脱敏（不含 apiKey）。 */
  error(code: ErrorCode, message: string, replyTo: string | null): void;
  /** 主动推送一次 `config.state`（上游推导出的状态变化，附录 A.1）。 */
  configState(view: ConfigView): void;
  /**
   * 向发起本轮 prompt 的 mod 连接发一次 tool_call 并等待 tool_result。
   * 失败/超时/断线都必须 resolve 成结构化错误，不能把异常抛给模型工具层。
   */
  callTool(
    name: string,
    args: JsonObject,
    options: { timeoutMs: number; signal?: AbortSignal },
  ): Promise<ToolResultPayload>;
  /** 本地工具（kb_* / run_script）的 UI 事件；协议附录 A.8。 */
  toolActivity?(payload: ToolActivityPayload): void;
}

export interface AgentRunner {
  /** 对外报告的 agent 身份（`ready.payload.agent`）。 */
  readonly info: AgentInfo;
  /** 处理一次 prompt；同一 runner 上的调用由上层串行化。 */
  run(text: string, promptId: string | null, emit: AgentEmitter): Promise<void>;
  /** `config.set` 落地后调用：让下一次 run 使用新配置。 */
  onConfigChanged(): void;
  /** 握手完成后同步 mod 能力；能力变化时 agent 应重建会话。 */
  onClientCapabilities?(capabilities: readonly string[]): void;
  /** 切换存档；null = 回到主菜单。agent 应在回合边界重建会话。 */
  onSaveChanged?(saveKey: string | null): void;
  /**
   * 读取某个存档最近的对话历史，供 UI 进入存档后回放。
   * 只读会话文件，不建模型会话、不联网、不改变当前 saveKey。
   */
  getHistory?(saveKey: string | null): Promise<HistoryMessage[]>;
  /**
   * 删除某个存档里的一个问答回合（M4e）。成功表示会话文件已被改写、
   * 内存会话已丢弃；失败用 `error` 说明原因（busy / turn_not_found / ...）。
   */
  deleteHistory?(
    saveKey: string | null,
    text: string,
    timestamp?: number,
  ): Promise<{ ok: boolean; removed: number; error?: string }>;
  /**
   * 拉取某个供应商的可用模型列表（M3）。
   * 已知供应商走 pi 目录；显式给了 baseUrl 的走 OpenAI 兼容 `/models`。
   * 失败时返回 `error`，不抛异常、不泄露 key。
   */
  listModels?(
    provider: string,
    options: { baseUrl: string | null; apiKey: string | null },
  ): Promise<ModelCatalogResult>;
  /**
   * 检测某个供应商的 API Key/端点是否可用（M3）。只做轻量 `/models` 探测，
   * 不发聊天请求、不产生 token 费用；失败时返回 ok=false，不抛异常。
   */
  testProvider?(
    provider: string,
    options: { baseUrl: string | null; apiKey: string | null },
  ): Promise<ProviderTestResult>;
  dispose(): void;
}

/** 让出一轮事件循环：让 status{thinking} 先落到 socket 上，再开始吐字。 */
export function tick(): Promise<void> {
  return new Promise<void>((resolve) => setImmediate(resolve));
}

// ---------------------------------------------------------------- 历史

/** 从 pi 的消息内容里只取可见文本；thinking / toolCall / 图片都忽略。 */
function contentText(content: unknown): string {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  const parts: string[] = [];
  for (const block of content) {
    if (!block || typeof block !== 'object') continue;
    const item = block as { type?: unknown; text?: unknown };
    if (item.type === 'text' && typeof item.text === 'string') parts.push(item.text);
  }
  return parts.join('\n');
}

/**
 * 把 pi 的 SessionEntry[] 投影成 UI 可回放的消息。
 *
 * 只保留 user/assistant 的文本；工具调用、工具结果、思考块、压缩摘要都不回放，
 * 避免把内部上下文暴露到聊天窗。只取最近 `maxMessages` 条，单条 `maxText` 字符。
 */
export function extractHistoryMessages(
  entries: readonly unknown[],
  maxMessages = 40,
  maxText = 4000,
): HistoryMessage[] {
  const out: HistoryMessage[] = [];
  for (const raw of entries) {
    if (!raw || typeof raw !== 'object') continue;
    const entry = raw as {
      type?: unknown;
      timestamp?: unknown;
      message?: {
        role?: unknown;
        content?: unknown;
        timestamp?: unknown;
      };
    };
    if (entry.type !== 'message' || !entry.message) continue;
    const role = entry.message.role;
    if (role !== 'user' && role !== 'assistant') continue;

    const text = contentText(entry.message.content).trim();
    if (!text) continue;

    const clipped = text.length > maxText ? `${text.slice(0, maxText)}…` : text;
    const rawTs =
      typeof entry.message.timestamp === 'number'
        ? entry.message.timestamp
        : typeof entry.timestamp === 'string'
          ? Date.parse(entry.timestamp)
          : NaN;
    const timestamp = Number.isFinite(rawTs) ? rawTs : undefined;
    out.push({ role, text: clipped, ...(timestamp !== undefined ? { timestamp } : {}) });
  }
  return out.slice(-maxMessages);
}

// ---------------------------------------------------------------- echo

/**
 * M0 的 echo agent。帧顺序与文本不变量是**冻结**的，重构时不要动。
 */
export class EchoAgent implements AgentRunner {
  readonly info: AgentInfo = { provider: 'echo', model: 'm0-echo', available: true };

  async listModels(
    provider: string,
    _options: { baseUrl: string | null; apiKey: string | null },
  ): Promise<ModelCatalogResult> {
    return {
      provider,
      models: [{ id: this.info.model, name: this.info.model }],
      source: 'catalog',
      error: null,
    };
  }

  async testProvider(
    provider: string,
    _options: { baseUrl: string | null; apiKey: string | null },
  ): Promise<ProviderTestResult> {
    return {
      provider,
      ok: true,
      status: 200,
      latencyMs: 0,
      message: 'echo agent',
      source: 'none',
    };
  }

  async run(text: string, promptId: string | null, emit: AgentEmitter): Promise<void> {
    if (!emit.isActive()) return;

    const full = `echo: ${text}`;

    emit.status('thinking', 'M0 echo agent');

    await tick();
    if (!emit.isActive()) return;

    emit.stream('text', 'echo: ', promptId);

    if (text.length > 0) {
      emit.stream('text', text, promptId);
    }

    emit.message(full, promptId, true);
    emit.status('idle');
  }

  onConfigChanged(): void {
    /* echo 与配置无关 */
  }

  dispose(): void {
    /* 无资源 */
  }
}

// ---------------------------------------------------------------- pi

export interface PiAgentOptions {
  dataDir: string;
  store: ConfigStore;
  /** 工具宿主：按本轮 prompt 绑定的 mod 连接执行工具。 */
  host: GameToolHost;
  /** M3 知识库；kb_* 工具始终注册，未建立时返回 kb_unavailable。 */
  kb?: KnowledgeStore;
  logger?: Logger;
  /** 便于测试注入 pi SDK；缺省时懒加载真实 SDK。 */
  loadSdk?: () => Promise<PiSdk>;
}

/** 一次 prompt 的运行状态；事件回调按此聚合。 */
interface RunState {
  emit: AgentEmitter;
  promptId: string | null;
  text: string;
  thinking: string;
  error: string | null;
  stopReason: string | null;
}

/**
 * 上游错误的分类（附录 A.3）。
 * 返回协议 error code，以及是否应该把配置状态改成 invalid_key / unreachable。
 */
export interface UpstreamFailure {
  code: ErrorCode;
  state: 'invalid_key' | 'unreachable' | null;
}

const RATE_LIMIT_HINTS = ['rate limit', 'rate_limit', 'too many requests', '429'];
/** 注意：不要放裸的 "401"/"403"（`includes` 会误伤 "9401999" 这类长数字），交给下面的 `\b40[13]\b`。 */
const AUTH_HINTS = [
  'unauthorized',
  'forbidden',
  'authentication',
  'invalid api key',
  'invalid_api_key',
  'invalid key',
  'api key is invalid',
];
const NETWORK_HINTS = [
  'fetch failed',
  'enotfound',
  'eai_again',
  'econnrefused',
  'econnreset',
  'etimedout',
  'getaddrinfo',
  'socket hang up',
  'network',
  'connection',
  'upstream connect',
  'timeout',
  'timed out',
];

/**
 * 把上游错误文本映射成协议错误码。
 * 上游文本可能很长且含 JSON body，这里只做保守的关键词判定。
 */
export function classifyUpstreamError(rawMessage: string): UpstreamFailure {
  const text = rawMessage.toLowerCase();

  if (RATE_LIMIT_HINTS.some((hint) => text.includes(hint))) {
    return { code: 'rate_limited', state: null };
  }
  if (AUTH_HINTS.some((hint) => text.includes(hint))) {
    return { code: 'invalid_key', state: 'invalid_key' };
  }
  // `\b40[13]\b`：避免把长数字里的 401/403 当状态码。
  if (/\b40[13]\b/.test(text)) return { code: 'invalid_key', state: 'invalid_key' };
  if (NETWORK_HINTS.some((hint) => text.includes(hint))) {
    return { code: 'upstream_error', state: 'unreachable' };
  }
  return { code: 'upstream_error', state: null };
}

/** 把任意异常归一化成一句可读文本（随后还要经过脱敏）。 */
function describeError(err: unknown): string {
  if (err instanceof Error) return err.message.length > 0 ? err.message : err.name;
  return String(err);
}

/**
 * `errorFields()` 会把异常原文（含 stack）放进日志字段里；
 * 上游 SDK 的异常文本可能夹带密钥，所以这里必须先脱敏再落日志。
 */
function safeErrorFields(err: unknown, apiKey: string | null): LogFields {
  const fields = errorFields(err);
  for (const key of ['errorMessage', 'errorStack'] as const) {
    const value = fields[key];
    if (typeof value === 'string') fields[key] = redactSecrets(value, [apiKey]);
  }
  return fields;
}

/** 取出助手消息里的纯文本（不流式时的兜底来源）。 */
function clipText(text: string, max: number): string {
  if (text.length <= max) return text;
  return `${text.slice(0, max)}…`;
}

function toolResultText(result: unknown): string {
  if (!result || typeof result !== 'object') return typeof result === 'string' ? result : '';
  const content = (result as { content?: unknown }).content;
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  const parts: string[] = [];
  for (const block of content) {
    if (!block || typeof block !== 'object') continue;
    const b = block as { type?: unknown; text?: unknown };
    if (b.type === 'text' && typeof b.text === 'string') parts.push(b.text);
  }
  return parts.join('\n');
}

function assistantText(message: { content?: unknown }): string {
  const content = message.content;
  if (!Array.isArray(content)) return '';
  const parts: string[] = [];
  for (const block of content) {
    if (
      block !== null &&
      typeof block === 'object' &&
      (block as { type?: unknown }).type === 'text' &&
      typeof (block as { text?: unknown }).text === 'string'
    ) {
      parts.push((block as { text: string }).text);
    }
  }
  return parts.join('');
}

// ---------------------------------------------------------------- 人格注入

/** `DefaultResourceLoader.extendResources` 的入参类型（pi 根导出没带出来）。 */
type LoaderExtendPaths = Parameters<ResourceLoader['extendResources']>[0];
/** `DefaultResourceLoader.reload` 的入参类型（同上）。 */
type LoaderReloadOptions = Parameters<ResourceLoader['reload']>[0];

/**
 * 内联扩展：每一轮 prompt 开始时把 `agent.state.systemPrompt` 换成我们的人格。
 *
 * 为什么 ResourceLoader 之外还需要它：pi 的 `buildSystemPrompt()` 在 customPrompt
 * 分支里**无条件**追加 `\nCurrent working directory: ${cwd}`（见 pi 的
 * core/system-prompt.js），光靠 `systemPrompt(Override)` 去不掉这条尾巴。
 * 而 `AgentSession.prompt()` 会调用扩展的 `before_agent_start`，返回值里的
 * `systemPrompt` 会**整轮**替换系统提示（pi 的 core/agent-session.js:898），
 * 于是 cwd 尾巴也不会进入请求体。
 *
 * 它是内联函数（`extensionFactories`），不落任何外部文件。
 */
const registerPersonaSystemPrompt: ExtensionFactory = (api) => {
  api.on('before_agent_start', () => ({ systemPrompt: ICARUS_II_SYSTEM_PROMPT }));
};

/**
 * 自定义 ResourceLoader：**装饰** `DefaultResourceLoader`，把「系统提示」与
 * 「项目上下文文件」两个出口钉死。
 *
 *  - `getSystemPrompt()` 恒返回游戏内助手人格 —— pi 的默认 coding prompt 不再可能
 *    被拼进去（默认 prompt 只在 customPrompt 为空时才会走）。
 *  - `getAgentsFiles()` 恒返回空 —— 不注入 `<cwd>/AGENTS.md`、`~/.pi/AGENTS.md`
 *    之类的项目上下文。
 *  - `getAppendSystemPrompt()` 恒返回空 —— 不追加任何外部 append prompt。
 *  - 其余（extensions/skills/prompts/themes）委托给 base；base 用 no* 选项构造，
 *    实际只加载我们自己的内联扩展。
 *
 * 装饰而不是直接 `new DefaultResourceLoader({systemPrompt: …})` 的原因：
 * `getSystemPrompt()` 只有在 `reload()` 成功之后才非空，而这里**永远**非空，
 * 所以即使资源加载出问题，人格也不会退化成 pi 默认的 coding prompt。
 */
class PersonaResourceLoader implements ResourceLoader {
  private readonly base: ResourceLoader;

  constructor(base: ResourceLoader) {
    this.base = base;
  }

  getExtensions(): ReturnType<ResourceLoader['getExtensions']> {
    return this.base.getExtensions();
  }

  getSkills(): ReturnType<ResourceLoader['getSkills']> {
    return this.base.getSkills();
  }

  getPrompts(): ReturnType<ResourceLoader['getPrompts']> {
    return this.base.getPrompts();
  }

  getThemes(): ReturnType<ResourceLoader['getThemes']> {
    return this.base.getThemes();
  }

  /** 项目上下文文件（AGENTS.md 等）永不注入。 */
  getAgentsFiles(): { agentsFiles: Array<{ path: string; content: string }> } {
    return { agentsFiles: [] };
  }

  /** pi 的 coding prompt 永不出现：恒返回游戏内助手人格。 */
  getSystemPrompt(): string {
    return ICARUS_II_SYSTEM_PROMPT;
  }

  getAppendSystemPrompt(): string[] {
    return [];
  }

  extendResources(paths: LoaderExtendPaths): void {
    this.base.extendResources(paths);
  }

  reload(options?: LoaderReloadOptions): Promise<void> {
    return this.base.reload(options);
  }
}

/**
 * 组装人格化的 ResourceLoader。
 *
 * `noExtensions/noSkills/noPromptTemplates/noThemes/noContextFiles` 全开：
 * sidecar 不该因为玩家机器上碰巧存在的 pi 配置而改变行为。
 */
function createPersonaResourceLoader(
  pi: PiSdk,
  options: { cwd: string; agentDir: string },
): ResourceLoader {
  const base = new pi.DefaultResourceLoader({
    cwd: options.cwd,
    agentDir: options.agentDir,
    noExtensions: true,
    noSkills: true,
    noPromptTemplates: true,
    noThemes: true,
    noContextFiles: true,
    // 替换（不是追加）pi 的 system prompt；override 是双保险。
    systemPrompt: ICARUS_II_SYSTEM_PROMPT,
    systemPromptOverride: () => ICARUS_II_SYSTEM_PROMPT,
    appendSystemPromptOverride: () => [],
    agentsFilesOverride: () => ({ agentsFiles: [] }),
    extensionFactories: [registerPersonaSystemPrompt],
  });
  return new PersonaResourceLoader(base);
}

/**
 * 用 `GET /models` 做一次轻量的 Key/连通性探测。
 * 不发聊天请求，不产生 token 费用；返回人类可读结果，绝不回传 key。
 */
async function probeProviderKey(
  provider: string,
  baseUrl: string,
  apiKey: string,
  api: string | null,
): Promise<ProviderTestResult> {
  const anthropic =
    provider === 'anthropic' || /anthropic/i.test(baseUrl) || Boolean(api && /anthropic/i.test(api));
  const google =
    provider === 'google' ||
    provider.startsWith('google-') ||
    /generativelanguage|googleapis/i.test(baseUrl);

  const headers: Record<string, string> = { accept: 'application/json' };
  let url: string;
  let rawBase = baseUrl.replace(/\/+$/, '');

  if (anthropic) {
    if (!/\/v1$/.test(rawBase)) rawBase += '/v1';
    url = `${rawBase}/models`;
    headers['x-api-key'] = apiKey;
    headers['anthropic-version'] = '2023-06-01';
  } else if (google) {
    if (!/\/(v1beta|v1)$/.test(rawBase)) rawBase += '/v1beta';
    url = `${rawBase}/models?key=${encodeURIComponent(apiKey)}`;
  } else {
    url = rawBase.endsWith('/models') ? rawBase : `${rawBase}/models`;
    headers.authorization = `Bearer ${apiKey}`;
  }

  const started = Date.now();
  try {
    const response = await fetch(url, { headers, signal: AbortSignal.timeout(10000) });
    const latencyMs = Date.now() - started;
    if (response.ok) {
      return { provider, ok: true, status: response.status, latencyMs, message: 'Key 可用', source: 'http' };
    }
    const message =
      response.status === 401 || response.status === 403
        ? `API Key 被拒绝（HTTP ${response.status}）`
        : response.status === 404
          ? '端点没有 /models 接口（HTTP 404）'
          : response.status === 429
            ? '请求被限流（HTTP 429）'
            : `端点返回 HTTP ${response.status}`;
    return { provider, ok: false, status: response.status, latencyMs, message, source: 'http' };
  } catch (err) {
    const raw = describeError(err);
    const safe = redactSecrets(raw, [apiKey]);
    const message = /timeout|aborted/i.test(raw) ? '检测超时' : `网络错误：${safe}`;
    return { provider, ok: false, status: 0, latencyMs: Date.now() - started, message, source: 'http' };
  }
}

/**
 * 调 OpenAI 兼容的 `GET {baseUrl}/models`。
 * 返回 `models` + 可选错误；调用方仍可退回 pi 目录。
 */
async function fetchOpenAICompatibleModels(
  baseUrl: string,
  apiKey: string | null,
): Promise<{ models: ModelCatalogEntry[]; source: 'openai-compatible' | 'none'; error: string | null }> {
  const base = baseUrl.replace(/\/+$/, '');
  const url = base.endsWith('/models') ? base : `${base}/models`;
  const headers: Record<string, string> = { accept: 'application/json' };
  if (apiKey) headers.authorization = `Bearer ${apiKey}`;

  try {
    const response = await fetch(url, { headers, signal: AbortSignal.timeout(12000) });
    if (!response.ok) {
      const hint =
        response.status === 401 || response.status === 403
          ? 'API Key 被拒绝'
          : `HTTP ${response.status}`;
      return { models: [], source: 'none', error: `获取模型列表失败：${hint}` };
    }
    const body = (await response.json()) as unknown;
    const list =
      body && typeof body === 'object' && Array.isArray((body as Record<string, unknown>)['data'])
        ? ((body as Record<string, unknown>)['data'] as unknown[])
        : body && typeof body === 'object' && Array.isArray((body as Record<string, unknown>)['models'])
          ? ((body as Record<string, unknown>)['models'] as unknown[])
          : [];

    const seen = new Set<string>();
    const models: ModelCatalogEntry[] = [];
    for (const item of list) {
      let id = '';
      let name = '';
      if (typeof item === 'string') {
        id = item;
        name = item;
      } else if (item && typeof item === 'object') {
        const obj = item as Record<string, unknown>;
        if (typeof obj['id'] === 'string') id = obj['id'];
        else if (typeof obj['name'] === 'string') id = obj['name'];
        if (typeof obj['name'] === 'string') name = obj['name'];
        else if (typeof obj['id'] === 'string') name = obj['id'];
      }
      id = id.trim();
      if (!id || seen.has(id)) continue;
      seen.add(id);
      models.push({ id, name: name.trim() || id });
    }
    return { models, source: 'openai-compatible', error: null };
  } catch (err) {
    const raw = describeError(err);
    const safe = redactSecrets(raw, apiKey ? [apiKey] : []);
    const message = /timeout|aborted/i.test(raw) ? '获取模型列表超时' : `获取模型列表失败：${safe}`;
    return { models: [], source: 'none', error: message };
  }
}

/**
 * pi 驱动的对话 agent。
 *
 * 隔离约定（不与用户环境互相污染）：
 *   - `cwd` = `<data-dir>`
 *   - `agentDir` = `<data-dir>/agent`
 *   - `ModelRuntime` 的 `authPath` / `modelsPath` 都在 `agentDir` 内，
 *     凭证由内存中的 CredentialStore 提供（不落地 auth.json）
 *   - `allowModelNetwork: false`：创建时不做任何模型目录网络刷新
 *   - 思考档位来自配置（默认 `high`）：默认请求思考；玩家可在聊天窗
 *     下拉框里选 `off` 关闭，或选 `max` 提高强度
 *   - system prompt 被**整个替换**成游戏内助手人格（见 persona.ts），
 *     pi 的 coding agent 身份不会出现在上游请求里
 */
export class PiAgent implements AgentRunner {
  private readonly dataDir: string;
  private readonly agentDir: string;
  private readonly store: ConfigStore;
  private readonly host: GameToolHost;
  private readonly kb: KnowledgeStore | null;
  private readonly logger: Logger;
  private readonly loadSdk: () => Promise<PiSdk>;

  private session: AgentSession | null = null;
  private runtime: ModelRuntime | null = null;
  private unsubscribe: (() => void) | null = null;
  private active: RunState | null = null;
  /** 回合运行中收到 config.set 时，延后到回合结束再重建会话。 */
  private pendingTeardown = false;
  /** 当前绑定的存档；每个存档使用独立的 session 目录。 */
  private saveKey: string | null = null;
  /** 进程内全局串行：多连接共用同一个 pi 会话。 */
  private chain: Promise<unknown> = Promise.resolve();

  constructor(options: PiAgentOptions) {
    this.dataDir = options.dataDir;
    this.agentDir = join(options.dataDir, 'agent');
    this.store = options.store;
    this.host = options.host;
    this.kb = options.kb ?? null;
    this.logger = (options.logger ?? rootLog).child({ component: 'pi-agent' });
    this.loadSdk =
      options.loadSdk ?? (() => import('@earendil-works/pi-coding-agent') as Promise<PiSdk>);
  }

  get info(): AgentInfo {
    const data = this.store.get();
    return { provider: data.provider, model: data.model, available: this.store.hasKey() };
  }

  onConfigChanged(): void {
    // 正在跑回合时不要拆会话：否则流式输出会被拦腰截断。
    // 记下来，等这一轮结束后再重建。
    if (this.active) {
      this.pendingTeardown = true;
      this.logger.info('配置变化：当前回合结束后重建 pi 会话', {
        apiKey: this.store.maskedApiKey,
        thinkingLevel: this.store.get().thinkingLevel,
      });
      return;
    }
    this.logger.info('配置变化，丢弃 pi 会话，下一次 prompt 重新建立', {
      apiKey: this.store.maskedApiKey,
      thinkingLevel: this.store.get().thinkingLevel,
    });
    this.teardown();
  }

  onClientCapabilities(capabilities: readonly string[]): void {
    const changed = this.host.setCapabilities(capabilities);
    if (!changed) return;

    this.logger.info('mod 能力变化，工具集将按新能力重建', {
      capabilities: [...capabilities],
    });
    if (this.active) {
      this.pendingTeardown = true;
      return;
    }
    this.teardown();
  }

  onSaveChanged(saveKey: string | null): void {
    if (this.saveKey === saveKey) return;
    this.saveKey = saveKey;
    this.logger.info('存档变化，切换对话会话', { saveKey });

    if (this.active) {
      this.pendingTeardown = true;
      return;
    }
    this.teardown();
  }

  async getHistory(saveKey: string | null): Promise<HistoryMessage[]> {
    if (!saveKey) return [];
    const sessionDir = join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey));
    // 没有会话文件就不要让 continueRecent 先建一个空会话（每次 bind 都会留垃圾）。
    if (!existsSync(sessionDir)) return [];
    if (!readdirSync(sessionDir).some((name) => name.endsWith('.jsonl'))) return [];

    const pi = await this.loadSdk();
    const manager = pi.SessionManager.continueRecent(this.dataDir, sessionDir);
    const messages = extractHistoryMessages(manager.buildContextEntries());
    this.logger.info('读取存档历史', { saveKey, messages: messages.length });
    return messages;
  }

  /**
   * 删除某个存档里的一个问答回合（M4e）。
   *
   * 只有删掉会话文件才算"真删"：`save.bound.history` 是从文件重放的。
   * 成功后丢弃内存会话，下一次 prompt 从改写后的文件重建上下文。
   */
  async deleteHistory(
    saveKey: string | null,
    text: string,
    timestamp?: number,
  ): Promise<{ ok: boolean; removed: number; error?: string }> {
    if (!saveKey) return { ok: false, removed: 0, error: 'no_save' };
    if (this.active) return { ok: false, removed: 0, error: 'busy' };

    const dir = join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey));
    if (!existsSync(dir)) return { ok: false, removed: 0, error: 'turn_not_found' };

    const files = readdirSync(dir).filter((name) => name.endsWith('.jsonl'));
    if (files.length === 0) return { ok: false, removed: 0, error: 'turn_not_found' };

    // 取最近写入的会话文件（与 continueRecent 的选择一致）。
    let newest = files[0] as string;
    let newestMs = -1;
    for (const name of files) {
      try {
        const ms = statSync(join(dir, name)).mtimeMs;
        if (ms > newestMs) {
          newestMs = ms;
          newest = name;
        }
      } catch {
        // 单个文件读不到就跳过。
      }
    }

    const file = join(dir, newest);
    let raw: string;
    try {
      raw = readFileSync(file, 'utf8');
    } catch {
      return { ok: false, removed: 0, error: 'io_error' };
    }

    const entries = parseSessionLines(raw);
    if (!entries) return { ok: false, removed: 0, error: 'unsupported_session' };

    const { outcome, entries: next } = removeTurnFromEntries(entries, text, timestamp);
    if (!outcome.ok || !next) return outcome;

    const tmp = `${file}.tmp-${process.pid}`;
    try {
      writeFileSync(tmp, serializeSessionEntries(next), { encoding: 'utf8', mode: 0o600 });
      renameSync(tmp, file);
    } catch (err) {
      try {
        rmSync(tmp, { force: true });
      } catch {
        // ignore
      }
      this.logger.warn('删除对话回合并写会话文件失败', errorFields(err));
      return { ok: false, removed: 0, error: 'io_error' };
    }

    // 上下文必须重建，否则被删的回合还在内存会话里继续发给模型。
    if (this.saveKey === saveKey) this.teardown();
    this.logger.info('已删除对话回合', { saveKey, removed: outcome.removed, file: newest });
    return outcome;
  }

  dispose(): void {
    this.teardown();
  }

  private applyPendingTeardown(): void {
    if (!this.pendingTeardown) return;
    this.pendingTeardown = false;
    this.teardown();
  }

  private teardown(): void {
    try {
      this.unsubscribe?.();
    } catch (err) {
      this.logger.debug('取消事件订阅失败', errorFields(err));
    }
    this.unsubscribe = null;
    try {
      this.session?.dispose();
    } catch (err) {
      this.logger.debug('释放 pi 会话失败', errorFields(err));
    }
    this.session = null;
    this.runtime = null;
    this.active = null;
  }

  run(text: string, promptId: string | null, emit: AgentEmitter): Promise<void> {
    const task = async (): Promise<void> => {
      try {
        await this.runOnce(text, promptId, emit);
      } finally {
        this.active = null;
        this.applyPendingTeardown();
      }
    };
    const previous = this.chain;
    const next = previous.then(task, task);
    this.chain = next.catch(() => undefined);
    return next;
  }

  private async runOnce(text: string, promptId: string | null, emit: AgentEmitter): Promise<void> {
    const data = this.store.get();

    if (!data.apiKey) {
      // 未配置：不建会话、不联网（协议附录 A.3 的 config_required）。
      const name = providerDisplayName(data.provider);
      emit.error('config_required', `尚未配置 API Key（${name}）`, promptId);
      return;
    }
    if (!emit.isActive()) return;

    const run: RunState = { emit, promptId, text: '', thinking: '', error: null, stopReason: null };
    this.active = run;

    emit.status('thinking', `${data.provider}/${data.model}`);

    let session: AgentSession;
    try {
      session = await this.ensureSession(
        data.provider,
        data.model,
        data.apiKey,
        data.baseUrl,
        data.thinkingLevel,
        data.providers[data.provider]?.models.find((m) => m.id === data.model)?.name,
      );
    } catch (err) {
      // 建会话失败 = LLM/模型不可用（协议 §4.8），与上游请求错误区分开。
      const safe = redactSecrets(describeError(err), [data.apiKey]);
      this.logger.error('无法建立 pi 会话', {
        promptId,
        message: safe,
        ...safeErrorFields(err, data.apiKey),
        apiKey: this.store.maskedApiKey,
      });
      if (emit.isActive()) {
        emit.error('agent_unavailable', safe, promptId);
        emit.status('idle');
      }
      return;
    }

    // 工具只发往发起本轮 prompt 的连接；prompt 结束立即解绑。
    this.host.bind(emit);
    try {
      if (!emit.isActive()) return;
      // 不解析斜杠命令/模板。
      await session.prompt(text, { expandPromptTemplates: false });
    } catch (err) {
      run.error = run.error ?? describeError(err);
      run.stopReason = 'error';
      this.logger.error('pi 会话执行失败', {
        promptId,
        ...safeErrorFields(err, data.apiKey),
        apiKey: this.store.maskedApiKey,
      });
    } finally {
      this.host.unbind(emit);
      this.active = null;
    }

    if (!emit.isActive()) return;

    const failed = run.stopReason === 'error' || run.stopReason === 'aborted';
    if (failed) {
      const raw = run.error ?? '未知上游错误';
      const failure = classifyUpstreamError(raw);
      const safe = redactSecrets(raw, [data.apiKey]);
      this.logger.warn('prompt 失败', { promptId, code: failure.code, message: safe });
      if (failure.state) {
        const view = this.store.setRuntimeState(failure.state, safe);
        emit.configState(view);
      }
      emit.error(failure.code, safe, promptId);
    } else {
      if (this.store.view().state !== 'ready') {
        // 上一次失败留下的 invalid_key / unreachable 已被成功请求证伪。
        emit.configState(this.store.clearRuntimeState());
      }
      emit.message(run.text, promptId, true);
    }

    emit.status('idle');
  }

  // ---------------------------------------------------------------- pi 生命周期

  /** 存档目录名：mod 传的是十六进制串，仍做一次白名单清洗防御异常输入。 */
  private sessionDirName(): string {
    return this.sessionDirNameFor(this.saveKey);
  }

  private sessionDirNameFor(saveKey: string | null): string {
    const raw = saveKey ?? 'menu';
    const safe = raw.replace(/[^a-zA-Z0-9_-]/g, '_').slice(0, 64);
    return safe.length > 0 ? safe : 'menu';
  }

  /**
   * 把配置里的 provider/model 解析成 pi 的 `Model` 对象。
   *
   * - 目录里有的模型：直接返回；
   * - 目录里没有、但该 provider 有其它模型：借第一条的 api/baseUrl/compat，
   *   只替换 id/name（新模型或第三方中转常常这样用）；
   * - 目录里也没有、但玩家填了 baseUrl：按 OpenAI 兼容协议现造一条；
   * - 都没有：返回 undefined，由调用方报「未知模型」。
   */
  private resolveModel(
    runtime: ModelRuntime,
    provider: string,
    modelId: string,
    apiKey: string,
    baseUrl: string | null,
    displayName?: string,
  ): Model<Api> | undefined {
    const exact = runtime.getModel(provider, modelId);
    const template = exact ?? runtime.getModels(provider)[0];

    // 配置里的 `input` 是对目录的覆盖：pi 目录可能滞后（模型已支持读图但标成 text）。
    // 不覆盖的话，provider 适配层会按 `model.input.includes("image")` 把截图丢掉。
    const configured = this.store
      .get()
      .providers[provider]?.models.find((entry) => entry.id === modelId);
    // 老配置里没有 `input`，用内置默认值兜底，否则升级后截图会被静默丢弃。
    const configuredInput = configured?.input ?? defaultModelInput(provider, modelId);

    if (template) {
      const model = {
        ...template,
        id: modelId,
        name: displayName || exact?.name || modelId,
        ...(configuredInput ? { input: [...configuredInput] } : {}),
      } as Model<Api>;
      return baseUrl ? { ...model, baseUrl } : model;
    }

    if (!baseUrl) return undefined;

    // 自定义 OpenAI 兼容端点：没有目录元数据，给一组保守默认值。
    const custom: Model<Api> = {
      id: modelId,
      name: displayName || modelId,
      api: 'openai-completions',
      provider,
      baseUrl,
      reasoning: false,
      input: configuredInput ? [...configuredInput] : ['text'],
      cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
      contextWindow: 128000,
      maxTokens: 8192,
    };
    // 直接传 model 对象还不足以让 ModelRuntime 找到凭证：把整条供应商注册进
    // runtime（含 key / baseUrl），pi 的 getAuth 才会为它返回 api_key。
    try {
      runtime.registerProvider(provider, {
        name: providerDisplayName(provider),
        baseUrl,
        apiKey,
        api: 'openai-completions',
        models: [{
          id: modelId,
          name: custom.name,
          api: 'openai-completions',
          baseUrl,
          reasoning: false,
          input: configuredInput ? [...configuredInput] : ['text'],
          cost: custom.cost,
          contextWindow: custom.contextWindow,
          maxTokens: custom.maxTokens,
        }],
      });
      return runtime.getModel(provider, modelId) ?? custom;
    } catch (err) {
      this.logger.warn('注册自定义供应商失败，改用裸模型对象', {
        provider,
        model: modelId,
        ...errorFields(err),
      });
      return custom;
    }
  }

  async testProvider(
    provider: string,
    options: { baseUrl: string | null; apiKey: string | null },
  ): Promise<ProviderTestResult> {
    const data = this.store.get();
    const stored = data.providers[provider];
    const baseUrl =
      options.baseUrl ?? stored?.baseUrl ?? (provider === data.provider ? data.baseUrl : null);
    const apiKey =
      options.apiKey ?? stored?.apiKey ?? (provider === data.provider ? data.apiKey : null);

    if (!apiKey) {
      return {
        provider,
        ok: false,
        status: 0,
        latencyMs: 0,
        message: '没有 API Key',
        source: 'none',
      };
    }

    let pi: PiSdk;
    try {
      pi = await this.loadSdk();
    } catch (err) {
      return {
        provider,
        ok: false,
        status: 0,
        latencyMs: 0,
        message: redactSecrets(describeError(err), [apiKey]),
        source: 'none',
      };
    }

    // 没显式 baseUrl 时从 pi 目录里借第一条模型的端点（DeepSeek/OpenAI/Anthropic 都有）。
    let resolvedBase = baseUrl;
    let api: string | null = null;
    if (!resolvedBase) {
      try {
        const runtime = await this.createRuntime(pi, provider, apiKey);
        const first = runtime.getModels(provider)[0];
        resolvedBase = first?.baseUrl ?? null;
        api = first?.api ?? null;
      } catch (err) {
        this.logger.debug('读取供应商端点失败', { provider, ...errorFields(err) });
      }
    }
    if (!resolvedBase) {
      return {
        provider,
        ok: false,
        status: 0,
        latencyMs: 0,
        message: '没有可用的 API 地址',
        source: 'none',
      };
    }

    const result = await probeProviderKey(provider, resolvedBase, apiKey, api);
    this.logger.info('检测供应商 Key', {
      provider,
      status: result.status,
      ok: result.ok,
      source: result.source,
    });
    return result;
  }

  async listModels(
    provider: string,
    options: { baseUrl: string | null; apiKey: string | null },
  ): Promise<ModelCatalogResult> {
    const data = this.store.get();
    const stored = data.providers[provider];
    const baseUrl = options.baseUrl ?? stored?.baseUrl ?? (provider === data.provider ? data.baseUrl : null);
    const apiKey = options.apiKey ?? stored?.apiKey ?? (provider === data.provider ? data.apiKey : null);

    let pi: PiSdk;
    try {
      pi = await this.loadSdk();
    } catch (err) {
      const message = redactSecrets(describeError(err), apiKey ? [apiKey] : []);
      return { provider, models: [], source: 'none', error: message };
    }

    // 显式 baseUrl 只问端点：中转站/本地推理的模型 id 往往不在 pi 目录里，
    // 这时拿官方目录来“兜底”反而会列出端点根本没有的模型。
    if (baseUrl) {
      const fetched = await fetchOpenAICompatibleModels(baseUrl, apiKey);
      if (fetched.models.length > 0) {
        this.logger.info('获取模型目录', { provider, source: fetched.source, count: fetched.models.length });
        return { provider, models: fetched.models, source: fetched.source, error: null };
      }
      return {
        provider,
        models: [],
        source: 'none',
        error: fetched.error ?? '端点没有返回模型列表',
      };
    }

    const catalog = await this.catalogModels(pi, provider, apiKey);
    if (catalog.length > 0) {
      this.logger.info('获取模型目录', { provider, source: 'catalog', count: catalog.length });
      return { provider, models: catalog, source: 'catalog', error: null };
    }

    return {
      provider,
      models: [],
      source: 'none',
      error: `没有 ${providerDisplayName(provider)} 的内置模型目录；请填写 API 地址后重试`,
    };
  }

  private async catalogModels(pi: PiSdk, provider: string, apiKey: string | null): Promise<ModelCatalogEntry[]> {
    try {
      const runtime = await this.createRuntime(pi, provider, apiKey ?? '');
      const seen = new Set<string>();
      const out: ModelCatalogEntry[] = [];
      for (const model of runtime.getModels(provider)) {
        if (!model || !model.id || seen.has(model.id)) continue;
        seen.add(model.id);
        out.push({ id: model.id, name: model.name || model.id });
      }
      return out;
    } catch (err) {
      this.logger.debug('读取 pi 模型目录失败', { provider, ...errorFields(err) });
      return [];
    }
  }

  /** 建一个只带单个供应商凭证的 pi runtime（不挂会话、不联网）。 */
  private async createRuntime(pi: PiSdk, provider: string, apiKey: string): Promise<ModelRuntime> {
    return pi.ModelRuntime.create({
      // 凭证只存在于内存里：不读也不写用户的 ~/.pi/agent/auth.json。
      credentials: {
        read: async (id: string) =>
          id === provider ? { type: 'api_key' as const, key: apiKey } : undefined,
        list: async () => [{ providerId: provider, type: 'api_key' as const }],
        modify: async (
          id: string,
          fn: (current: { type: 'api_key'; key?: string } | undefined) => Promise<
            { type: 'api_key'; key?: string } | undefined
          >,
        ) => {
          const current =
            id === provider ? ({ type: 'api_key', key: apiKey } as const) : undefined;
          const next = await fn(current);
          return next ?? current;
        },
        delete: async () => undefined,
      },
      // 两个路径都必须显式给出：缺省会指向 ~/.pi/agent（用户的全局目录）。
      authPath: join(this.agentDir, 'auth.json'),
      modelsPath: join(this.agentDir, 'models.json'),
      allowModelNetwork: false,
    });
  }

  private async ensureSession(
    provider: string,
    modelId: string,
    apiKey: string,
    baseUrl: string | null,
    thinkingLevel: string,
    displayName?: string,
  ): Promise<AgentSession> {
    if (this.session) return this.session;

    const pi = await this.loadSdk();

    const runtime = await this.createRuntime(pi, provider, apiKey);

    const model = this.resolveModel(runtime, provider, modelId, apiKey, baseUrl, displayName);
    if (!model) {
      const known = runtime
        .getModels(provider)
        .map((m) => m.id)
        .slice(0, 8);
      throw new Error(
        `未知模型 ${provider}/${modelId}（该 provider 可用：${known.length > 0 ? known.join(', ') : '无'}；` +
          `自定义供应商请填写 API 地址）`,
      );
    }

    const resourceLoader = createPersonaResourceLoader(pi, {
      cwd: this.dataDir,
      agentDir: this.agentDir,
    });
    // pi 只有在「自己创建 loader」时才自动 reload；外部传入的 loader 必须我们自己 reload，
    // 否则 getExtensions() 还是空的（内联人格扩展就不会被注册）。
    await resourceLoader.reload();

    // 每个存档独立的 session 目录：换档 = 换会话；同一存档重连/重启会恢复最近一次会话。
    const sessionDir = join(this.dataDir, 'sessions', this.sessionDirName());
    const sessionManager = pi.SessionManager.continueRecent(this.dataDir, sessionDir);

    const options: CreateAgentSessionOptions = {
      cwd: this.dataDir,
      agentDir: this.agentDir,
      modelRuntime: runtime,
      model,
      sessionManager,
      // 玩家选择的思考档位。DeepSeek 的 thinkingLevelMap 只映射 high/max，
      // 其余档位由 pi 自行 clamp；`off` 表示完全不请求思考。
      // 类型上 CreateAgentSessionOptions.thinkingLevel 是 pi 的 ThinkingLevel 联合类型，
      // 这里的字符串已在 protocol.parseConfigSetPayload 里做过白名单校验。
      thinkingLevel: thinkingLevel as NonNullable<CreateAgentSessionOptions['thinkingLevel']>,
      // 关闭全部内置工具，只注册按当前 mod 能力生成的游戏工具。
      noTools: 'builtin',
      customTools: [
        ...this.host.tools(),
        ...(this.kb ? createKbTools(this.kb) : []),
        createScriptTool(this.host),
      ],
      // 替换 pi 默认的 coding agent system prompt 为游戏内助手人格。
      resourceLoader,
    };

    const created = await pi.createAgentSession(options);
    this.runtime = runtime;
    this.session = created.session;
    this.unsubscribe = created.session.subscribe((event) => this.handleEvent(event));

    // 保险：确认 pi 真的用上了我们的人格（没生效就明确记一条 warn，别静默退化）。
    const effectivePrompt = created.session.systemPrompt ?? '';
    if (!effectivePrompt.startsWith(ICARUS_II_SYSTEM_PROMPT)) {
      this.logger.warn('pi 没有采用自定义 system prompt，助手人格可能未生效', {
        personaName: ICARUS_II_PERSONA_NAME,
        promptChars: effectivePrompt.length,
      });
    }

    this.logger.info('pi 会话已建立', {
      provider,
      model: model.id,
      cwd: this.dataDir,
      agentDir: this.agentDir,
      thinkingLevel: created.session.thinkingLevel,
      tools: created.session.getActiveToolNames(),
      persona: ICARUS_II_PERSONA_NAME,
      systemPromptChars: ICARUS_II_SYSTEM_PROMPT.length,
      extensions: created.extensionsResult.extensions.length,
      extensionErrors: created.extensionsResult.errors.length,
      apiKey: maskSecret(apiKey),
    });
    if (created.modelFallbackMessage) {
      this.logger.warn('pi 会话回退到了其它模型', { detail: created.modelFallbackMessage });
    }

    return created.session;
  }

  // ---------------------------------------------------------------- 事件映射

  /** 工具名是否由 mod 执行（那几个已经由 mod 自己出卡片，不能重复）。 */
  private isHostTool(name: string): boolean {
    return this.host.tools().some((tool) => tool.name === name);
  }

  private handleEvent(event: AgentSessionEvent): void {
    const run = this.active;
    if (!run) return;

    switch (event.type) {
      case 'message_start': {
        // 新一轮助手消息（例如自动重试）：重置累积文本，避免重复。
        if (event.message.role === 'assistant') {
          run.text = '';
          run.thinking = '';
          run.error = null;
          run.stopReason = null;
        }
        return;
      }

      case 'message_update': {
        const inner = event.assistantMessageEvent;
        switch (inner.type) {
          case 'text_delta':
            if (inner.delta.length > 0) {
              run.text += inner.delta;
              run.emit.stream('text', inner.delta, run.promptId);
            }
            return;
          case 'thinking_delta':
            if (inner.delta.length > 0) {
              run.thinking += inner.delta;
              run.emit.stream('reasoning', inner.delta, run.promptId);
            }
            return;
          case 'error':
            run.stopReason = inner.error.stopReason;
            run.error = inner.error.errorMessage ?? run.error ?? '上游返回错误';
            return;
          default:
            return;
        }
      }

      case 'message_end': {
        if (event.message.role !== 'assistant') return;
        const message = event.message;
        // 助手消息结束 ⇒ 以最终消息为准（流式增量可能被重试截断过）。
        const finalText = assistantText(message);
        if (finalText.length > 0) run.text = finalText;
        if (message.stopReason === 'error' || message.stopReason === 'aborted') {
          run.stopReason = message.stopReason;
          run.error = message.errorMessage ?? run.error ?? '上游返回错误';
        }
        return;
      }

      case 'tool_execution_start': {
        run.emit.status('tools', event.toolName);
        if (!this.isHostTool(event.toolName)) {
          run.emit.toolActivity?.({
            callId: `local:${event.toolCallId}`,
            phase: 'start',
            name: event.toolName,
            args: clipText(JSON.stringify(event.args ?? {}), 300),
          });
        }
        return;
      }

      case 'tool_execution_end': {
        // 工具结束后模型还要继续生成；回到 thinking，避免状态栏停在“工具”。
        run.emit.status('thinking');
        if (!this.isHostTool(event.toolName)) {
          const text = toolResultText(event.result);
          run.emit.toolActivity?.({
            callId: `local:${event.toolCallId}`,
            phase: 'end',
            name: event.toolName,
            ok: !event.isError,
            summary: clipText(text.replace(/\s+/g, ' ').trim(), 160),
            detail: clipText(text, 4000),
          });
        }
        return;
      }

      case 'auto_retry_start':
        run.emit.status('retrying', `第 ${event.attempt}/${event.maxAttempts} 次重试`);
        return;

      case 'auto_retry_end':
        if (event.success) run.emit.status('thinking');
        return;

      default:
        return;
    }
  }
}
