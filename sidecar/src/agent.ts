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

import { join } from 'node:path';
import type {
  AgentSession,
  AgentSessionEvent,
  CreateAgentSessionOptions,
  ExtensionFactory,
  ModelRuntime,
  ResourceLoader,
} from '@earendil-works/pi-coding-agent';
import { type ConfigStore, maskSecret, redactSecrets } from './config.js';
import { ICARUS_II_PERSONA_NAME, ICARUS_II_SYSTEM_PROMPT } from './persona.js';
import { errorFields, type Logger, type LogFields, log as rootLog } from './log.js';
import type {
  AgentInfo,
  ConfigView,
  ErrorCode,
  StatusState,
  StreamChannel,
} from './protocol.js';

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
}

export interface AgentRunner {
  /** 对外报告的 agent 身份（`ready.payload.agent`）。 */
  readonly info: AgentInfo;
  /** 处理一次 prompt；同一 runner 上的调用由上层串行化。 */
  run(text: string, promptId: string | null, emit: AgentEmitter): Promise<void>;
  /** `config.set` 落地后调用：让下一次 run 使用新配置。 */
  onConfigChanged(): void;
  dispose(): void;
}

/** 让出一轮事件循环：让 status{thinking} 先落到 socket 上，再开始吐字。 */
export function tick(): Promise<void> {
  return new Promise<void>((resolve) => setImmediate(resolve));
}

// ---------------------------------------------------------------- echo

/**
 * M0 的 echo agent。帧顺序与文本不变量是**冻结**的，重构时不要动。
 */
export class EchoAgent implements AgentRunner {
  readonly info: AgentInfo = { provider: 'echo', model: 'm0-echo', available: true };

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
 * pi 驱动的对话 agent。
 *
 * 隔离约定（不与用户环境互相污染）：
 *   - `cwd` = `<data-dir>`
 *   - `agentDir` = `<data-dir>/agent`
 *   - `ModelRuntime` 的 `authPath` / `modelsPath` 都在 `agentDir` 内，
 *     凭证由内存中的 CredentialStore 提供（不落地 auth.json）
 *   - `allowModelNetwork: false`：创建时不做任何模型目录网络刷新
 *   - `thinkingLevel: "off"`：M1 默认不请求思考（deepseek 的 thinkingLevelMap
 *     把 medium 映射为 null，pi 的 clamp 会把默认的 medium 提升成 high）
 *   - system prompt 被**整个替换**成游戏内助手人格（见 persona.ts），
 *     pi 的 coding agent 身份不会出现在上游请求里
 */
export class PiAgent implements AgentRunner {
  private readonly dataDir: string;
  private readonly agentDir: string;
  private readonly store: ConfigStore;
  private readonly logger: Logger;
  private readonly loadSdk: () => Promise<PiSdk>;

  private session: AgentSession | null = null;
  private runtime: ModelRuntime | null = null;
  private unsubscribe: (() => void) | null = null;
  private active: RunState | null = null;
  /** 回合运行中收到 config.set 时，延后到回合结束再重建会话。 */
  private pendingTeardown = false;
  /** 进程内全局串行：多连接共用同一个 pi 会话。 */
  private chain: Promise<unknown> = Promise.resolve();

  constructor(options: PiAgentOptions) {
    this.dataDir = options.dataDir;
    this.agentDir = join(options.dataDir, 'agent');
    this.store = options.store;
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
      });
      return;
    }
    this.logger.info('配置变化，丢弃 pi 会话，下一次 prompt 重新建立', {
      apiKey: this.store.maskedApiKey,
    });
    this.teardown();
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
      emit.error('config_required', '尚未配置 API Key', promptId);
      return;
    }
    if (!emit.isActive()) return;

    const run: RunState = { emit, promptId, text: '', thinking: '', error: null, stopReason: null };
    this.active = run;

    emit.status('thinking', `${data.provider}/${data.model}`);

    let session: AgentSession;
    try {
      session = await this.ensureSession(data.provider, data.model, data.apiKey, data.baseUrl);
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

    try {
      if (!emit.isActive()) return;
      // 纯对话：不解析斜杠命令/模板。
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

  private async ensureSession(
    provider: string,
    modelId: string,
    apiKey: string,
    baseUrl: string | null,
  ): Promise<AgentSession> {
    if (this.session) return this.session;

    const pi = await this.loadSdk();

    const runtime = await pi.ModelRuntime.create({
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

    const base = runtime.getModel(provider, modelId);
    if (!base) {
      const known = runtime
        .getModels(provider)
        .map((m) => m.id)
        .slice(0, 8);
      throw new Error(
        `未知模型 ${provider}/${modelId}（该 provider 可用：${known.length > 0 ? known.join(', ') : '无'}）`,
      );
    }
    // baseUrl 覆盖：配置里给了就用它，否则用 pi 目录里的官方地址。
    const model = baseUrl ? { ...base, baseUrl } : base;

    const resourceLoader = createPersonaResourceLoader(pi, {
      cwd: this.dataDir,
      agentDir: this.agentDir,
    });
    // pi 只有在「自己创建 loader」时才自动 reload；外部传入的 loader 必须我们自己 reload，
    // 否则 getExtensions() 还是空的（内联人格扩展就不会被注册）。
    await resourceLoader.reload();

    const options: CreateAgentSessionOptions = {
      cwd: this.dataDir,
      agentDir: this.agentDir,
      modelRuntime: runtime,
      model,
      // deepseek 的 thinkingLevelMap 只承认 high/max；pi 会把默认的 medium
      // clamp 成 high。M1 默认不请求思考，因此必须显式 off。
      thinkingLevel: 'off',
      // M1 纯对话：不启用任何工具。
      noTools: 'builtin',
      customTools: [],
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
