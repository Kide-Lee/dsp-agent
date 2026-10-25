/**
 * 每连接会话：状态机 + agent 与配置的接线（协议 §3 / §6 / 附录 A）。
 *
 * 状态机（严格按 docs/protocol-v1.md §3 / §6）：
 *
 *   awaiting_hello ──hello(token,v 通过)──▶ ready ──(对端或服务端 close)──▶ closed
 *        │
 *        └──token 不匹配 / v 不支持 / 5s 超时──▶ error + close 1008
 *
 * agent 的帧输出走 `AgentEmitter`：本文件实现它，agent 只看到「发一帧」这件事，
 * 不接触 socket，也不碰握手状态机。
 *
 * 本文件刻意不 import `ws`：只依赖一个极小的 `SocketLike` 接口，
 * 这样会话逻辑可以脱离网络单独测试（server.ts 负责把 ws 包成 SocketLike）。
 */

import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import {
  CLOSE_MESSAGE_TOO_BIG,
  CLOSE_POLICY_VIOLATION,
  HELLO_TIMEOUT_MS,
  MAX_FRAME_BYTES,
  MessageType,
  PROTOCOL_VERSION,
  SUPPORTED_PROTOCOL_VERSIONS,
  buildConfigStatePayload,
  buildContextCompactedPayload,
  buildEnvelope,
  buildErrorPayload,
  buildHistoryCompactionUndonePayload,
  buildHistoryDeletedPayload,
  buildMessagePayload,
  buildModelCatalogPayload,
  buildProviderTestPayload,
  buildReadyPayload,
  buildSaveBoundPayload,
  buildStatusPayload,
  buildStreamPayload,
  buildToolActivityPayload,
  buildBlueprintCardPayload,
  buildLinkCardPayload,
  buildToolCallPayload,
  buildToolDebugResultPayload,
  buildConfirmRequestPayload,
  parseConfirmResultPayload,
  errorRetryable,
  parseConfigSetPayload,
  parseFrame,
  parseHelloPayload,
  parseHistoryDeletePayload,
  parseHistoryUndoCompactionPayload,
  parseModelCatalogPayload,
  parsePromptPayload,
  parseProviderTestPayload,
  parseReplyTimePayload,
  parseSaveBindPayload,
  parseToolResultPayload,
  parseToolDebugPayload,
  serializeEnvelope,
  type ConfigView,
  type ErrorCode,
  type JsonObject,
  type BlueprintCardPayload,
  type LinkCardPayload,
  type ContextCompactedPayload,
  type ModelCatalogResult,
  type ProviderTestResult,
  type ToolCatalogEntry,
  type ToolResultPayload,
  type ConfirmRequestPayload,
  type ConfirmResultPayload,
} from './protocol.js';
import { UI_ASK_TOOL_NAME, UI_ASK_TOOL_NAME_ALIASES, UI_ASK_TIMEOUT_MS } from './tools/ask.js';
import type { AgentEmitter, AgentRunner } from './agent.js';
import type { ScriptConfirmRequest, ScriptConfirmResult } from './tools/host.js';
import { maskSecret, redactSecrets, type ConfigStore } from './config.js';
import type { KbTransport, KnowledgeManager } from './kb/manager.js';
import { errorFields, type Logger, log as rootLog } from './log.js';

export type SessionState = 'awaiting_hello' | 'ready' | 'closed';

/** 会话只依赖这些能力；`server.ts` 用 ws 实现。 */
export interface SocketLike {
  readonly isOpen: boolean;
  send(text: string, cb?: (err?: Error) => void): void;
  close(code: number, reason: string): void;
  /** 传输层对端地址，仅用于日志。 */
  readonly peer: string;
}

export interface SessionOptions {
  /** 期望的握手 token（sidecar 启动时生成）。 */
  token: string;
  serverVersion: string;
  /** 本次进程的 agent（多连接共享；由 index.ts 装配）。 */
  agent: AgentRunner;
  /** 配置存储（多连接共享）。 */
  config: ConfigStore;
  /** 知识库编排（多连接共享）。 */
  knowledge?: KnowledgeManager;
  /** 本次连接注册的工具名；可按 hello 能力动态计算。 */
  tools: readonly string[] | ((capabilities: readonly string[]) => readonly string[]);
  /** M4m：是否允许 `tool_debug` 调试直调（默认 false）。 */
  debugTools?: boolean;
  helloTimeoutMs?: number;
  logger?: Logger;
  /** 便于测试注入。 */
  createSessionId?: () => string;
}

/** token 比较使用定时安全比较，避免长度/前缀时间侧信道（design.md §9.1）。 */
function tokenEquals(a: string, b: string): boolean {
  const ab = Buffer.from(a, 'utf8');
  const bb = Buffer.from(b, 'utf8');
  if (ab.length !== bb.length) return false;
  return timingSafeEqual(ab, bb);
}

export function defaultSessionId(): string {
  return `sess_${randomBytes(8).toString('hex')}`;
}

interface PendingToolCall {
  resolve: (result: ToolResultPayload) => void;
  timer: NodeJS.Timeout;
  cleanup: () => void;
}

interface PendingConfirm {
  resolve: (result: { token: string; argsHash: string } | null) => void;
  timer: NodeJS.Timeout;
  token: string;
  argsHash: string;
  callId: string;
  tool: string;
  cleanup: () => void;
}

/** 稳定 JSON：确认令牌的参数哈希不依赖对象键顺序。 */
function stableJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
  if (value && typeof value === 'object') {
    const obj = value as Record<string, unknown>;
    return `{${Object.keys(obj)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableJson(obj[key])}`)
      .join(',')}}`;
  }
  return JSON.stringify(value) ?? 'null';
}

function hashArgs(args: JsonObject): string {
  return createHash('sha256').update(stableJson(args)).digest('hex');
}

function describeConfirmation(
  name: string,
  args: JsonObject,
): { title: string; body: string } {
  const fmt = (value: unknown): string =>
    typeof value === 'object' && value !== null ? stableJson(value) : String(value ?? '');
  switch (name) {
    case 'game_focus_camera':
      return { title: '移动视角？', body: `目标：${fmt(args['target'])}` };
    case 'game_open_panel':
      return { title: '打开游戏面板？', body: `面板：${fmt(args['panel'])}` };
    case 'game_set_research':
      return {
        title: '切换研究目标？',
        body: `科技：${fmt(args['techId'])}\n可能浪费当前研究已投入的进度。`,
      };
    case 'game_drop_item':
      return {
        title: '丢弃物品？',
        body: `物品：${fmt(args['itemId'])}\n数量：${fmt(args['count'] ?? 1)}\n` +
          '会丢到地面，需要手动拾回；不进入撤销栈。',
      };
    case 'game_set_recipe':
      return {
        title: '更换配方？',
        body: `建筑：#${fmt(args['buildingId'])}\n配方：#${fmt(args['recipeId'])}（0 = 清空）\n` +
          '会先退回机器内原料/产物；撤销只恢复配方。',
      };
    case 'game_set_assembler_batch': {
      const rows = Array.isArray(args['selections']) ? args['selections'] : [];
      const preview = rows.slice(0, 6).map((row) => {
        const item = (row ?? {}) as Record<string, unknown>;
        return `#${fmt(item['buildingId'])} → #${fmt(item['recipeId'])}`;
      });
      const more = rows.length > preview.length ? `\n…还有 ${rows.length - preview.length} 台` : '';
      return {
        title: '批量更换配方？',
        body: `${rows.length} 台生产建筑：\n${preview.join('\n')}${more}\n` +
          '每台会先退回机器内原料/产物；整批只压一层撤销，撤销只恢复配方。',
      };
    }
    case 'game_storage_set_filter':
      return {
        title: '设置存储过滤器？',
        body: `建筑：#${fmt(args['buildingId'])}\n` +
          (Number(args['itemId'] ?? 0) === 0 ? '清除过滤器' : `只放物品 #${fmt(args['itemId'])}`) +
          '\n已占用且物品不同的格子会跳过。',
      };
    case 'game_storage_set_limit':
      return {
        title: '设置存储容量上限？',
        body: `建筑：#${fmt(args['buildingId'])}\n允许使用：${fmt(args['limit'])} 格\n` +
          '会限制该存储可用的格子数。',
      };
    case 'game_storage_move':
      return {
        title: '移动物品？',
        body: `从 #${fmt(args['sourceId'])} 到 #${fmt(args['targetId'])}\n` +
          `物品：#${fmt(args['itemId'])}，数量：${fmt(args['count'])}\n` +
          '目标空间不足会整笔失败；源不足只移动可用部分。',
      };
    case 'game_storage_deposit':
      return {
        title: '存入存储？',
        body: `存储：#${fmt(args['buildingId'])}\n物品：#${fmt(args['itemId'])}，数量：${fmt(args['count'])}\n` +
          '存储满时只放入能放下的部分，剩余退回背包。',
      };
    case 'game_storage_withdraw':
      return {
        title: '从存储取出？',
        body: `存储：#${fmt(args['buildingId'])}\n物品：#${fmt(args['itemId'])}，数量：${fmt(args['count'])}\n` +
          '背包满时只取能放下的部分，剩余留在存储。',
      };
    case 'game_tether_add':
      return {
        title: '建立方位指示？',
        body: args['kind'] === 'planet'
          ? `目标：行星 #${fmt(args['planetId'] ?? '当前')}\n` +
            '会设置机甲导航朝向；可同时建多个，但游戏一次只显示一个跨行星导航朝向。'
          : `目标：建筑 #${fmt(args['buildingId'])}（行星 #${fmt(args['planetId'] ?? '当前')}）\n` +
            '本星球会自绘机甲→建筑线；跨行星先指向目标行星，落地后切换。',
      };
    case 'game_tether_clear':
      return {
        title: '清除方位指示？',
        body: '会移除本 mod 建立的所有导航朝向和建筑追踪线。',
      };
    case 'game_feed_input':
      return {
        title: '直接投料？',
        body: `建筑：#${fmt(args['buildingId'])}\n物品：#${fmt(args['itemId'])}，数量：${fmt(args['count'])}\n` +
          '会直接写入机器输入缓冲，不经过分拣器/传送带；只接受当前配方需要的物品。',
      };
    case 'game_take_output':
      return {
        title: '取出成品？',
        body: `建筑：#${fmt(args['buildingId'])}\n` +
          '会把当前成品放进背包，保留配方；背包满时只取能放下的部分。',
      };
    case 'game_dismantle': {
      const ids = Array.isArray(args['buildingIds']) ? args['buildingIds'] : [];
      const preview = ids.slice(0, 8).map((id) => `#${fmt(id)}`).join('、');
      const more = ids.length > 8 ? ` 等 ${ids.length} 个` : '';
      return {
        title: '拆除建筑？',
        body: `将拆除：${preview}${more}\n` +
          '拆除不可撤销；游戏会按正常规则退料/清理，但建筑不会恢复。',
      };
    }
    default:
      return { title: '确认写操作？', body: `${name}\n${stableJson(args)}` };
  }
}

function toolFailure(code: string, message: string): ToolResultPayload {
  return {
    ok: false,
    data: null,
    error: { code, message, retryable: false },
    meta: {},
  };
}

/** 只在日志里出现、避免把 token 原文写进 stderr。 */
function fingerprint(value: string): string {
  return createHash('sha256').update(value).digest('hex').slice(0, 12);
}

export class Session {
  readonly sessionId: string;
  private readonly socket: SocketLike;
  private readonly options: SessionOptions;
  private readonly logger: Logger;
  private readonly onClosed: () => void;

  private _state: SessionState = 'awaiting_hello';
  private helloPayload: ReturnType<typeof parseHelloPayload> = null;
  /** 当前绑定的存档；history.delete 省略 saveKey 时用它兜底。 */
  private boundSaveKey: string | null = null;
  private helloRejected = false;
  private helloTimer: NodeJS.Timeout | null = null;

  /** prompt 串行队列：保证同一连接上不会出现交错的流式输出。 */
  private promptChain: Promise<void> = Promise.resolve();

  /** 已发出、尚未收到 tool_result 的调用。 */
  private readonly pendingTools = new Map<string, PendingToolCall>();
  /** 已发出、尚未收到 confirm_result 的写操作确认。 */
  private readonly pendingConfirms = new Map<string, PendingConfirm>();
  private toolSeq = 0;
  private confirmSeq = 0;

  /** 本进程见过哪些 saveKey（只用于 save.bound.existed 的显示，不参与会话隔离）。 */
  private readonly seenSaveKeys = new Set<string>();

  private promptCount = 0;
  private frameCount = 0;

  constructor(socket: SocketLike, options: SessionOptions, onClosed: () => void = () => {}) {
    this.socket = socket;
    this.options = options;
    this.onClosed = onClosed;
    this.sessionId = (options.createSessionId ?? defaultSessionId)();
    this.logger = (options.logger ?? rootLog).child({
      sessionId: this.sessionId,
      peer: socket.peer,
    });

    const timeoutMs = options.helloTimeoutMs ?? HELLO_TIMEOUT_MS;
    this.helloTimer = setTimeout(() => {
      if (this._state !== 'awaiting_hello') return;
      this.logger.warn('hello 超时', { timeoutMs });
      this.rejectHandshake('unauthorized', `hello not received within ${timeoutMs}ms`, null);
    }, timeoutMs);
    // 握手计时器不该阻止进程退出。
    this.helloTimer.unref?.();

    this.logger.debug('会话已建立');
  }

  get state(): SessionState {
    return this._state;
  }

  get isReady(): boolean {
    return this._state === 'ready';
  }

  get helloInfo(): JsonObject | null {
    if (!this.helloPayload) return null;
    return {
      modVersion: this.helloPayload.modVersion,
      gameVersion: this.helloPayload.gameVersion,
      gameLanguage: this.helloPayload.gameLanguage,
      capabilities: this.helloPayload.capabilities,
      saveKey: this.helloPayload.saveKey,
    };
  }

  get stats(): JsonObject {
    return { frames: this.frameCount, prompts: this.promptCount };
  }

  // ---------------------------------------------------------------- 入帧

  /**
   * 处理一帧（server.ts 已完成 ws 层解包）。
   * 任何异常都会被吞掉并转成 `error{internal}`，绝不因为一帧把进程带崩。
   */
  handleFrame(data: Buffer, isBinary: boolean): void {
    if (this._state === 'closed') return;
    this.frameCount += 1;

    if (data.byteLength > MAX_FRAME_BYTES) {
      this.logger.warn('收到超限帧，关闭连接', {
        bytes: data.byteLength,
        limit: MAX_FRAME_BYTES,
      });
      this.sendErrorAndClose(
        'frame_too_large',
        `frame exceeds ${MAX_FRAME_BYTES} bytes (got ${data.byteLength})`,
        null,
        CLOSE_MESSAGE_TOO_BIG,
        'frame too large',
      );
      return;
    }

    if (isBinary) {
      // 协议只用文本帧；二进制帧是可恢复的协议误用，不因此断开。
      this.logger.warn('收到二进制帧，已忽略', { bytes: data.byteLength });
      this.sendError('bad_request', 'binary frames are not supported', null);
      return;
    }

    const parsed = parseFrame(data.toString('utf8'));
    if (!parsed.ok) {
      this.handleBadFrame(parsed.failure);
      return;
    }

    try {
      this.dispatch(parsed.envelope.type, parsed.envelope);
    } catch (err) {
      this.logger.error('处理帧时抛出异常', { type: parsed.envelope.type, ...errorFields(err) });
      this.sendError('internal', 'internal error while handling frame', parsed.envelope.id || null);
    }
  }

  private handleBadFrame(failure: string): void {
    // hello 之前收到非法 JSON / 非 object ⇒ 这次握手已经废了，直接关。
    // hello 之后 ⇒ 单帧错误可恢复，回 bad_request 但保持连接（协议 §4.8）。
    const fatalHandshake = this._state === 'awaiting_hello' && (failure === 'not_json' || failure === 'not_object');
    const message =
      failure === 'not_json'
        ? 'frame is not valid JSON'
        : failure === 'not_object'
          ? 'frame is not a JSON object'
          : failure === 'invalid_type'
            ? 'envelope.type must be a non-empty string'
            : 'envelope.payload must be an object';

    this.logger.warn('非法帧', { failure, fatalHandshake });

    if (fatalHandshake) {
      this.sendErrorAndClose('bad_request', message, null, CLOSE_POLICY_VIOLATION, 'bad handshake');
      return;
    }
    this.sendError('bad_request', message, null);
  }

  private dispatch(type: string, env: import('./protocol.js').ParsedEnvelope): void {
    switch (type) {
      case MessageType.Hello:
        this.handleHello(env);
        return;
      case MessageType.Ping:
        this.handlePing(env);
        return;
      case MessageType.Prompt:
        this.handlePrompt(env);
        return;
      case MessageType.ToolDebug:
        this.handleToolDebug(env);
        return;
      case MessageType.ConfigSet:
        this.handleConfigSet(env);
        return;
      case MessageType.SaveBind:
        void this.handleSaveBind(env);
        return;
      case MessageType.HistoryDelete:
        void this.handleHistoryDelete(env);
        return;
      case MessageType.HistoryUndoCompaction:
        void this.handleHistoryUndoCompaction(env);
        return;
      case MessageType.ReplyTime:
        this.handleReplyTime(env);
        return;
      case MessageType.ToolResult:
        this.handleToolResult(env);
        return;
      case MessageType.ConfirmResult:
        this.handleConfirmResult(env);
        return;
      case MessageType.ModelCatalog:
        this.handleModelCatalog(env);
        return;
      case MessageType.ProviderTest:
        this.handleProviderTest(env);
        return;
      default:
        // 未知 type 必须忽略且不断开（协议 §2）；已预留类型额外记一条 debug。
        this.logger.debug('忽略未知/预留消息类型', { type, id: env.id });
        return;
    }
  }

  // ---------------------------------------------------------------- hello

  private handleHello(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state === 'ready') {
      this.logger.debug('重复 hello，已忽略');
      return;
    }
    if (this._state === 'closed' || this.helloRejected) return;

    const payload = parseHelloPayload(env.payload);
    if (!payload) {
      this.logger.warn('hello 缺少 token 字段');
      this.rejectHandshake('unauthorized', 'hello.payload.token must be a string', env.id || null);
      return;
    }

    if (!tokenEquals(payload.token, this.options.token)) {
      this.logger.warn('token 不匹配', {
        gotFingerprint: fingerprint(payload.token),
        wantFingerprint: fingerprint(this.options.token),
      });
      this.rejectHandshake('unauthorized', 'token mismatch', env.id || null);
      return;
    }

    if (env.v !== PROTOCOL_VERSION) {
      this.logger.warn('协议版本不匹配', { got: env.v, supported: SUPPORTED_PROTOCOL_VERSIONS });
      this.rejectHandshake(
        'protocol_mismatch',
        `unsupported protocol version ${String(env.v)}`,
        env.id || null,
        { supported: [...SUPPORTED_PROTOCOL_VERSIONS] },
      );
      return;
    }

    // 握手通过。
    this.clearHelloTimer();
    this.helloPayload = payload;
    this.boundSaveKey = payload.saveKey ?? null;
    this._state = 'ready';
    this.logger.info('握手完成', {
      modVersion: payload.modVersion,
      gameVersion: payload.gameVersion,
      gameLanguage: payload.gameLanguage,
      capabilities: payload.capabilities,
      saveKey: payload.saveKey,
      configState: this.options.config.view().state,
    });

    // 能力先同步给 agent：它会按能力重建工具集/会话。
    try {
      this.options.agent.onClientCapabilities?.(payload.capabilities);
    } catch (err) {
      this.logger.warn('同步 mod 能力失败', errorFields(err));
    }

    const toolNames =
      typeof this.options.tools === 'function'
        ? this.options.tools(payload.capabilities)
        : this.options.tools;

    // M4l：工具目录是可选字段；agent 没实现时老 mod 继续用 tools 名 + 本地短名。
    let toolCatalog: ToolCatalogEntry[] = [];
    try {
      toolCatalog = this.options.agent.toolCatalog?.(payload.capabilities) ?? [];
    } catch (err) {
      this.logger.warn('生成工具目录失败（ready 不带 toolCatalog）', errorFields(err));
    }

    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.Ready,
        replyTo: env.id || null,
        payload: buildReadyPayload({
          serverVersion: this.options.serverVersion,
          sessionId: this.sessionId,
          agent: this.options.agent.info,
          tools: toolNames,
          toolCatalog,
          // 附录 A.2：让 mod 在第一次握手就知道要不要进密钥引导态。
          config: this.options.config.view(),
        }),
      }),
    );

    // 知识库：先读缓存（不阻塞 ready）；如果握手时已经带了存档，立刻尝试导出。
    try {
      this.options.knowledge?.onHello(payload.gameVersion, payload.gameLanguage || 'unknown');
    } catch (err) {
      this.logger.warn('知识库缓存加载失败', errorFields(err));
    }
    if (payload.saveKey) this.ensureKnowledge();
  }

  private rejectHandshake(
    code: ErrorCode,
    message: string,
    replyTo: string | null,
    extra: JsonObject = {},
  ): void {
    if (this.helloRejected || this._state === 'closed') return;
    this.helloRejected = true;
    this.sendErrorAndClose(code, message, replyTo, CLOSE_POLICY_VIOLATION, code, extra);
  }

  // ---------------------------------------------------------------- ping / config / prompt

  private handlePing(env: import('./protocol.js').ParsedEnvelope): void {
    // 原样回传 t（协议 §4.7）；t 缺失/非法时回 null，仍然应答。
    const t = env.payload['t'];
    const payload: JsonObject = { t: t === undefined ? null : t };
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.Pong,
        replyTo: env.id || null,
        payload,
      }),
    );
  }

  /**
   * `model.catalog`（M3）：拉取某个供应商的可用模型，供设置页「获取可用模型」使用。
   * 请求里的 apiKey 只用于本次探测，不回传、不落盘、不记日志。
   */
  private handleModelCatalog(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.logger.warn('握手未完成就收到 model.catalog');
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }

    const request = parseModelCatalogPayload(env.payload);
    if (!request) {
      this.sendError('bad_request', 'model.catalog requires provider (and optional baseUrl/apiKey)', env.id || null);
      return;
    }

    const listModels = this.options.agent.listModels;
    if (!listModels) {
      const payload = buildModelCatalogPayload({
        provider: request.provider,
        models: [],
        source: 'none',
        error: '当前 agent 不支持模型目录',
      });
      this.sendEnvelope(buildEnvelope({ type: MessageType.ModelCatalog, replyTo: env.id || null, payload }));
      return;
    }

    this.logger.info('收到 model.catalog', {
      provider: request.provider,
      hasKey: Boolean(request.apiKey),
    });

    void listModels
      .call(this.options.agent, request.provider, {
        baseUrl: request.baseUrl,
        apiKey: request.apiKey,
      })
      .then((result: ModelCatalogResult) => {
        this.sendEnvelope(
          buildEnvelope({
            type: MessageType.ModelCatalog,
            replyTo: env.id || null,
            payload: buildModelCatalogPayload(result),
          }),
        );
      })
      .catch((err: unknown) => {
        const raw = err instanceof Error ? err.message : String(err);
        const safe = redactSecrets(raw, request.apiKey ? [request.apiKey] : []);
        this.logger.error('获取模型目录失败', { provider: request.provider, message: safe });
        const payload = buildModelCatalogPayload({
          provider: request.provider,
          models: [],
          source: 'none',
          error: safe,
        });
        this.sendEnvelope(buildEnvelope({ type: MessageType.ModelCatalog, replyTo: env.id || null, payload }));
      });
  }

  /**
   * `provider.test`（M3）：轻量检测供应商 Key/端点是否可用。
   * 请求里的 apiKey 只用于本次探测，不回传、不落盘、不记日志。
   */
  private handleProviderTest(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.logger.warn('握手未完成就收到 provider.test');
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }

    const request = parseProviderTestPayload(env.payload);
    if (!request) {
      this.sendError('bad_request', 'provider.test requires provider (and optional baseUrl/apiKey)', env.id || null);
      return;
    }

    const testProvider = this.options.agent.testProvider;
    if (!testProvider) {
      const payload = buildProviderTestPayload({
        provider: request.provider,
        ok: false,
        status: 0,
        latencyMs: 0,
        message: '当前 agent 不支持 Key 检测',
        source: 'none',
      });
      this.sendEnvelope(buildEnvelope({ type: MessageType.ProviderTest, replyTo: env.id || null, payload }));
      return;
    }

    this.logger.info('收到 provider.test', {
      provider: request.provider,
      hasKey: Boolean(request.apiKey),
    });

    void testProvider
      .call(this.options.agent, request.provider, {
        baseUrl: request.baseUrl,
        apiKey: request.apiKey,
      })
      .then((result: ProviderTestResult) => {
        this.sendEnvelope(
          buildEnvelope({
            type: MessageType.ProviderTest,
            replyTo: env.id || null,
            payload: buildProviderTestPayload(result),
          }),
        );
      })
      .catch((err: unknown) => {
        const raw = err instanceof Error ? err.message : String(err);
        const safe = redactSecrets(raw, request.apiKey ? [request.apiKey] : []);
        this.logger.error('检测供应商 Key 失败', { provider: request.provider, message: safe });
        const payload = buildProviderTestPayload({
          provider: request.provider,
          ok: false,
          status: 0,
          latencyMs: 0,
          message: safe,
          source: 'none',
        });
        this.sendEnvelope(buildEnvelope({ type: MessageType.ProviderTest, replyTo: env.id || null, payload }));
      });
  }

  /**
   * `config.set`（附录 A.1）。
   *
   * - 只更新给出的字段，然后**立即落盘**并回 `config.state`。
   * - 不做上游探测：配置状态由「有没有 key」决定，真正的可用性由
   *   第一次 prompt 的结果反馈（invalid_key / unreachable）。
   * - `apiKey` 只进不出：回帧与日志里只有脱敏形态。
   */
  private handleConfigSet(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.logger.warn('握手未完成就收到 config.set');
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }

    const patch = parseConfigSetPayload(env.payload);
    if (!patch) {
      this.sendError('bad_request', 'config.set fields must be strings', env.id || null);
      return;
    }

    const fields = Object.keys(patch);
    this.logger.info('收到 config.set', {
      fields,
      apiKey: maskSecret(typeof patch.apiKey === 'string' ? patch.apiKey : null),
    });

    let view: ConfigView;
    try {
      view = this.options.config.update(patch);
    } catch (err) {
      this.logger.error('写入 config.json 失败', errorFields(err));
      this.sendError('internal', 'failed to persist configuration', env.id || null);
      return;
    }

    // 配置变了：让 agent 丢弃旧会话（下一次 prompt 用新配置重建）。
    this.options.agent.onConfigChanged();

    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConfigState,
        replyTo: env.id || null,
        payload: buildConfigStatePayload(view),
      }),
    );
  }

  /**
   * `save.bind`（附录 A.6）：通知 agent 切换到对应存档的会话。
   * 这里只做路由与回执；真正的会话切换由 agent 负责（它会在回合边界重建 pi session）。
   */
  private async handleSaveBind(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.logger.warn('握手未完成就收到 save.bind');
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }

    const bind = parseSaveBindPayload(env.payload);
    if (!bind) {
      this.sendError('bad_request', 'save.bind.payload.saveKey must be a string or null', env.id || null);
      return;
    }

    const known = bind.saveKey !== null && this.seenSaveKeys.has(bind.saveKey);
    if (bind.saveKey !== null) this.seenSaveKeys.add(bind.saveKey);

    this.logger.info('收到 save.bind', {
      saveKey: bind.saveKey,
      saveName: bind.saveName,
      existed: known,
      blueprintTiers: bind.blueprintTiers ?? null,
      sandbox: bind.sandbox,
    });

    try {
      this.options.agent.onSaveChanged?.(bind.saveKey);
      this.options.agent.onSandboxChanged?.(bind.sandbox);
      this.options.agent.onBlueprintTiers?.(bind.blueprintTiers ?? null);
    } catch (err) {
      this.logger.warn('切换存档会话失败', errorFields(err));
    }
    this.boundSaveKey = bind.saveKey;

    // 先在服务端读历史，再把 save.bound 发给 mod；mod 用它回放上一次对话。
    // 读文件是纯本地操作，不建模型会话、不联网；失败时按“无历史”处理。
    let history: Awaited<ReturnType<NonNullable<AgentRunner['getHistory']>>> = [];
    if (bind.saveKey && this.options.agent.getHistory) {
      try {
        history = await this.options.agent.getHistory(bind.saveKey);
      } catch (err) {
        this.logger.warn('读取存档历史失败，按无历史处理', {
          saveKey: bind.saveKey,
          ...errorFields(err),
        });
      }
    }

    if (this._state !== 'ready') return;

    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.SaveBound,
        replyTo: env.id || null,
        payload: buildSaveBoundPayload({
          saveKey: bind.saveKey,
          saveName: bind.saveName,
          existed: known || history.length > 0,
          messageCount: history.length,
          history,
        }),
      }),
    );

    if (bind.saveKey) this.ensureKnowledge();
  }

  /**
   * 删除一个问答回合（附录 A.7）。
   *
   * 删除必须落到会话文件上，否则 `save.bound.history` 会把它又带回聊天窗。
   * agent 在成功后丢弃内存会话，下一次 prompt 从改写后的文件重建上下文。
   */
  private async handleHistoryDelete(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    const payload = parseHistoryDeletePayload(env.payload ?? {});
    if (!payload) {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.HistoryDeleted,
          replyTo: env.id ?? null,
          payload: buildHistoryDeletedPayload({
            saveKey: this.boundSaveKey,
            text: '',
            ok: false,
            removed: 0,
            error: 'bad_request',
          }),
        }),
      );
      return;
    }

    const saveKey = payload.saveKey ?? this.boundSaveKey;
    let result: { ok: boolean; removed: number; error?: string } = {
      ok: false,
      removed: 0,
      error: 'agent_unavailable',
    };

    if (this.options.agent.deleteHistory) {
      try {
        result = await this.options.agent.deleteHistory(saveKey, payload.text, payload.timestamp);
      } catch (err) {
        this.logger.warn('删除对话回合失败', errorFields(err));
        result = { ok: false, removed: 0, error: 'io_error' };
      }
    }

    this.logger.info('处理 history.delete', {
      saveKey,
      ok: result.ok,
      removed: result.removed,
      error: result.error ?? null,
    });

    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.HistoryDeleted,
        replyTo: env.id ?? null,
        payload: buildHistoryDeletedPayload({
          saveKey,
          text: payload.text,
          ok: result.ok,
          removed: result.removed,
          error: result.error ?? null,
        }),
      }),
    );
  }

  /**
   * 撤销最近一次上下文压缩。和删除回合一样，必须落到会话文件上；
   * 成功后丢弃内存会话，下一次 prompt 从展开后的历史重建上下文。
   */
  private async handleHistoryUndoCompaction(
    env: import('./protocol.js').ParsedEnvelope,
  ): Promise<void> {
    const payload = parseHistoryUndoCompactionPayload(env.payload ?? {});
    if (!payload) {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.HistoryCompactionUndone,
          replyTo: env.id ?? null,
          payload: buildHistoryCompactionUndonePayload({
            saveKey: this.boundSaveKey,
            compactionId: null,
            ok: false,
            removed: 0,
            error: 'bad_request',
          }),
        }),
      );
      return;
    }

    const saveKey = payload.saveKey ?? this.boundSaveKey;
    let result: {
      ok: boolean;
      removed: number;
      compactionId?: string;
      error?: string;
      mayRecompact?: boolean;
    } = { ok: false, removed: 0, error: 'agent_unavailable' };

    if (this.options.agent.undoCompaction) {
      try {
        result = await this.options.agent.undoCompaction(saveKey, payload.compactionId);
      } catch (err) {
        this.logger.warn('撤销上下文压缩失败', errorFields(err));
        result = { ok: false, removed: 0, error: 'io_error' };
      }
    }

    this.logger.info('处理 history.undo_compaction', {
      saveKey,
      requested: payload.compactionId ?? null,
      ok: result.ok,
      removed: result.removed,
      compactionId: result.compactionId ?? null,
      error: result.error ?? null,
    });

    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.HistoryCompactionUndone,
        replyTo: env.id ?? null,
        payload: buildHistoryCompactionUndonePayload({
          saveKey,
          compactionId: result.compactionId ?? payload.compactionId ?? null,
          ok: result.ok,
          removed: result.removed,
          error: result.error ?? null,
          mayRecompact: result.mayRecompact === true,
        }),
      }),
    );
  }

  private handlePrompt(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.logger.warn('握手未完成就收到 prompt');
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }

    const prompt = parsePromptPayload(env.payload);
    if (!prompt) {
      this.sendError('bad_request', 'prompt.payload.text must be a string', env.id || null);
      return;
    }

    const promptId = env.id || null;
    this.promptCount += 1;
    this.logger.info('收到 prompt', {
      promptId,
      chars: prompt.text.length,
      conversationId: prompt.conversationId,
      requiredTool: prompt.requiredTool,
    });

    // 串行化：保证同一 prompt 的 status/stream/message 不被交错的下一轮切开。
    const emit = this.createEmitter();
    const run = this.promptChain.then(async () => {
      const knowledge = this.options.knowledge;
      if (knowledge) {
        // 知识库导出通常 <1s；首问等一小会儿，避免模型在旧版本/ kb_unavailable 下回答。
        // 无导出在途时立即返回，不额外发 status 帧（echo/无知识库路径的帧序必须保持 M0 契约）。
        await knowledge.whenReady(2500);
      }
      await this.options.agent.run(prompt.text, promptId, emit, prompt.requiredTool);
    });
    this.promptChain = run.catch((err: unknown) => {
      this.logger.error('agent 执行失败', errorFields(err));
      this.sendError('internal', 'agent failed', promptId);
    });
  }

  /**
   * M4m：调试直调。不进入 agent 的 prompt 会话、不调用 LLM；
   * 与 prompt 共用串行链，避免 GameToolHost.bind 冲突。
   */
  private handleToolDebug(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    if (!this.options.debugTools) {
      this.sendError(
        'debug_disabled',
        '调试直调未开启：sidecar 需要用 --debug-tools 启动',
        env.id || null,
      );
      return;
    }

    const request = parseToolDebugPayload(env.payload);
    if (!request) {
      this.sendError('bad_request', 'tool_debug.payload.name/args 非法', env.id || null);
      return;
    }

    const debugId = env.id || null;
    this.logger.info('收到 tool_debug', { debugId, name: request.name, argsBytes: JSON.stringify(request.args).length });

    const emit = this.createEmitter();
    const run = this.promptChain.then(async () => {
      const debug = this.options.agent.debugTool;
      if (!debug) {
        this.sendError('tool_unavailable', '当前 agent 不支持调试直调', debugId);
        return;
      }
      try {
        const result = await debug.call(this.options.agent, request.name, request.args, emit);
        if (this._state !== 'ready' || !this.socket.isOpen) return;
        this.sendEnvelope(
          buildEnvelope({
            type: MessageType.ToolDebugResult,
            replyTo: debugId,
            payload: buildToolDebugResultPayload(result),
          }),
        );
        this.logger.info('tool_debug 完成', {
          debugId,
          name: request.name,
          ok: result.ok,
          durationMs: result.durationMs,
        });
      } catch (err) {
        this.logger.error('tool_debug 执行失败', { debugId, name: request.name, ...errorFields(err) });
        this.sendError('internal', '调试直调失败', debugId);
      }
    });
    this.promptChain = run.catch((err: unknown) => {
      this.logger.error('tool_debug 队列失败', errorFields(err));
      this.sendError('internal', '调试直调失败', debugId);
    });
  }

  private handleReplyTime(env: import('./protocol.js').ParsedEnvelope): void {
    const parsed = parseReplyTimePayload(env.payload);
    if (!parsed) {
      this.logger.warn('reply.time 载荷无效，已忽略', { promptId: env.id ?? null });
      return;
    }
    this.options.agent.recordReplyTime?.(
      this.boundSaveKey,
      parsed.prompt,
      parsed.reply ?? '',
      parsed.gameTime,
    );
  }

  // ---------------------------------------------------------------- 工具调用

  /**
   * 发一次 tool_call 并等待 mod 的 tool_result。
   * 超时、断线、取消都 resolve 成结构化错误；调用方（pi 工具层）不需要 try/catch。
   */
  private async callTool(
    name: string,
    args: JsonObject,
    options: {
      timeoutMs: number;
      signal?: AbortSignal;
      internal?: boolean;
      risk?: number;
      confirmToken?: string;
      confirmArgsHash?: string;
    },
  ): Promise<ToolResultPayload> {
    if (this._state !== 'ready' || !this.socket.isOpen) {
      return toolFailure('tool_unavailable', 'mod 连接未就绪');
    }

    const isAsk = name === UI_ASK_TOOL_NAME || (UI_ASK_TOOL_NAME_ALIASES as readonly string[]).includes(name);
    const maxTimeoutMs = isAsk ? UI_ASK_TIMEOUT_MS : 30000;
    const timeoutMs = Math.min(maxTimeoutMs, Math.max(500, options.timeoutMs || 8000));
    const callId = `call_${(this.toolSeq += 1)}`;
    const risk = options.risk ?? 0;

    let token: string | undefined = options.confirmToken;
    let argsHash: string | undefined = options.confirmArgsHash;
    if (!token && risk > 0 && options.internal !== true) {
      const approved = await this.requestConfirmation(callId, name, args, risk, options.signal);
      if (!approved) {
        return toolFailure('confirmation_denied', '玩家拒绝了这次写操作，未执行');
      }
      token = approved.token;
      argsHash = approved.argsHash;
    }

    return new Promise<ToolResultPayload>((resolve) => {
      let cleanup = (): void => {};

      const timer = setTimeout(() => {
        this.settleTool(callId, toolFailure('timeout', `工具 ${name} 在 ${timeoutMs}ms 内没有返回`));
      }, timeoutMs);
      timer.unref?.();

      const signal = options.signal;
      if (signal) {
        const onAbort = (): void => {
          this.settleTool(callId, toolFailure('cancelled', `工具 ${name} 已被取消`));
        };
        if (signal.aborted) {
          clearTimeout(timer);
          resolve(toolFailure('cancelled', `工具 ${name} 已被取消`));
          return;
        }
        signal.addEventListener('abort', onAbort, { once: true });
        cleanup = () => signal.removeEventListener('abort', onAbort);
      }

      this.pendingTools.set(callId, { resolve, timer, cleanup });

      this.logger.debug('发送 tool_call', { callId, name, timeoutMs, risk, confirmed: !!token });
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.ToolCall,
          payload: buildToolCallPayload({
            callId,
            name,
            args,
            risk: risk as 0 | 1 | 2,
            timeoutMs,
            internal: options.internal === true,
            confirmToken: token,
            confirmArgsHash: argsHash,
          }),
        }),
        (err) => {
          if (err) {
            this.settleTool(callId, toolFailure('tool_unavailable', `发送 tool_call 失败：${err.message}`));
          }
        },
      );
    });
  }

  /**
   * run_script 的脚本级确认：一次确认授权脚本最多 maxWrites 次写调用。
   * token 绑定脚本哈希；mod 只接受该 token + 同一哈希的写 tool_call。
   */
  private async confirmScript(request: ScriptConfirmRequest): Promise<ScriptConfirmResult | null> {
    const writeNames = request.writeNames.slice(0, 12);
    const more = request.writeNames.length > writeNames.length ? '…' : '';
    const approved = await this.requestConfirmation(
      'script',
      'run_script',
      {},
      2,
      undefined,
      {
        argsHash: request.scriptHash,
        title: '脚本包含写操作？',
        body:
          `脚本将调用写工具：${writeNames.join(', ')}${more}\n` +
          `本次最多允许 ${request.maxWrites} 次写调用；执行期间不再逐次确认。`,
        maxWrites: request.maxWrites,
        writeNames: request.writeNames,
      },
    );
    if (!approved) return null;
    return { token: approved.token, scriptHash: request.scriptHash };
  }

  private releaseScript(token: string): void {
    if (!token || this._state !== 'ready' || !this.socket.isOpen) return;
    const payload = { token };
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConfirmRelease,
        payload,
      }),
    );
  }

  private requestConfirmation(
    callId: string,
    name: string,
    args: JsonObject,
    risk: number,
    signal?: AbortSignal,
    override?: {
      argsHash?: string;
      title?: string;
      body?: string;
      maxWrites?: number;
      writeNames?: string[];
    },
  ): Promise<{ token: string; argsHash: string } | null> {
    const requestId = `confirm_${(this.confirmSeq += 1)}`;
    const token = randomBytes(32).toString('hex');
    const argsHash = override?.argsHash ?? hashArgs(args);
    const copy = override
      ? { title: override.title ?? '确认写操作？', body: override.body ?? '' }
      : describeConfirmation(name, args);

    return new Promise((resolve) => {
      let cleanup = (): void => {};
      const finish = (value: { token: string; argsHash: string } | null): void => {
        const pending = this.pendingConfirms.get(requestId);
        if (!pending) return;
        this.pendingConfirms.delete(requestId);
        clearTimeout(pending.timer);
        try {
          pending.cleanup();
        } catch {
          /* ignore */
        }
        resolve(value);
      };

      const timer = setTimeout(() => finish(null), 60000);
      timer.unref?.();
      if (signal) {
        const onAbort = (): void => finish(null);
        if (signal.aborted) {
          clearTimeout(timer);
          resolve(null);
          return;
        }
        signal.addEventListener('abort', onAbort, { once: true });
        cleanup = () => signal.removeEventListener('abort', onAbort);
      }

      this.pendingConfirms.set(requestId, {
        resolve: finish,
        timer,
        token,
        argsHash,
        callId,
        tool: name,
        cleanup,
      });
      this.logger.info('请求写操作确认', { requestId, callId, tool: name, risk });
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.ConfirmRequest,
          payload: buildConfirmRequestPayload({
            requestId,
            callId,
            tool: name,
            argsHash,
            token,
            title: copy.title,
            body: copy.body,
            risk: risk as 1 | 2,
            timeoutMs: 60000,
            ...(override?.maxWrites ? { maxWrites: override.maxWrites } : {}),
            ...(override?.writeNames ? { writeNames: override.writeNames } : {}),
          } satisfies ConfirmRequestPayload),
        }),
        (err) => {
          if (err) {
            this.logger.warn('发送 confirm_request 失败', { requestId, error: err.message });
            finish(null);
          }
        },
      );
    });
  }

  private handleConfirmResult(env: import('./protocol.js').ParsedEnvelope): void {
    const parsed = parseConfirmResultPayload(env.payload);
    if (!parsed) {
      this.logger.warn('confirm_result 载荷无效');
      return;
    }
    const pending = this.pendingConfirms.get(parsed.requestId);
    if (!pending) return;
    const ok = parsed.decision === 'allow' && parsed.token === pending.token;
    this.logger.info('收到确认结果', {
      requestId: parsed.requestId,
      decision: parsed.decision,
      ok,
    });
    pending.resolve(ok ? { token: pending.token, argsHash: pending.argsHash } : null);
  }

  /**
   * 知识库导出专用 transport：走同一条 tool_call 通道，但标记 internal，
   * mod 不显示工具卡；也不会出现在模型可见工具列表里。
   */
  private knowledgeTransport(): KbTransport {
    return {
      callTool: (name, args, options) =>
        this.callTool(name, args, {
          timeoutMs: options.timeoutMs,
          internal: true,
        }),
    };
  }

  /** 进入存档后确保当前版本/语言的知识库可用（缓存优先，缺失才导出）。 */
  private ensureKnowledge(): void {
    const knowledge = this.options.knowledge;
    const hello = this.helloPayload;
    if (!knowledge || !hello) return;
    if (!hello.gameVersion || hello.gameVersion === 'unknown') return;
    const locale = hello.gameLanguage || 'unknown';
    void knowledge.ensure(this.knowledgeTransport(), hello.gameVersion, locale).then((ok) => {
      if (ok) {
        this.logger.info('知识库可用', { ...knowledge.knowledge.meta() });
      }
    });
  }

  private handleToolResult(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }

    const callId =
      env.replyTo ??
      (typeof env.payload['callId'] === 'string' ? (env.payload['callId'] as string) : null);
    if (!callId) {
      this.logger.warn('tool_result 缺少 callId/replyTo');
      this.sendError('bad_request', 'tool_result requires replyTo or payload.callId', env.id || null);
      return;
    }

    const parsed = parseToolResultPayload(env.payload);
    if (!parsed) {
      this.settleTool(callId, toolFailure('bad_request', 'tool_result.payload.ok must be a boolean'));
      return;
    }

    this.logger.debug('收到 tool_result', {
      callId,
      ok: parsed.ok,
      code: parsed.error?.code ?? null,
    });
    this.settleTool(callId, parsed);
  }

  private settleTool(callId: string, result: ToolResultPayload): void {
    const pending = this.pendingTools.get(callId);
    if (!pending) return;
    this.pendingTools.delete(callId);
    clearTimeout(pending.timer);
    try {
      pending.cleanup();
    } catch {
      /* ignore */
    }
    pending.resolve(result);
  }

  private failAllTools(reason: string): void {
    for (const callId of [...this.pendingTools.keys()]) {
      this.settleTool(callId, toolFailure('tool_unavailable', reason));
    }
    for (const pending of [...this.pendingConfirms.values()]) {
      pending.resolve(null);
    }
    this.pendingConfirms.clear();
  }

  // ---------------------------------------------------------------- agent 写帧出口

  /** agent 唯一的写帧出口；所有 replyTo 由 agent 给出。 */
  private createEmitter(): AgentEmitter {
    const session = this;
    return {
      isActive: () => session._state === 'ready' && session.socket.isOpen,

      status(state, detail) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.Status,
            payload: buildStatusPayload(state, detail),
          }),
        );
      },

      stream(channel, delta, replyTo) {
        if (delta.length === 0) return;
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.Stream,
            replyTo,
            payload: buildStreamPayload(channel, delta),
          }),
        );
      },

      message(text, replyTo, final = true) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.Message,
            replyTo,
            payload: buildMessagePayload([{ type: 'text', text }], {
              role: 'assistant',
              final,
            }),
          }),
        );
      },

      error(code, message, replyTo) {
        session.sendError(code, message, replyTo);
      },

      configState(view) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.ConfigState,
            payload: buildConfigStatePayload(view),
          }),
        );
      },

      callTool(name, args, options) {
        return session.callTool(name, args, options);
      },

      confirmScript(request) {
        return session.confirmScript(request);
      },

      releaseScript(token) {
        session.releaseScript(token);
      },

      toolActivity(payload) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.ToolActivity,
            payload: buildToolActivityPayload(payload),
          }),
        );
      },

      blueprintCard(payload: BlueprintCardPayload) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.BlueprintCard,
            payload: buildBlueprintCardPayload(payload),
          }),
        );
      },

      linkCard(payload: LinkCardPayload) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.LinkCard,
            payload: buildLinkCardPayload(payload),
          }),
        );
      },

      compacted(payload: ContextCompactedPayload) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.ContextCompacted,
            payload: buildContextCompactedPayload(payload),
          }),
        );
      },
    };
  }

  // ---------------------------------------------------------------- 出帧

  private sendEnvelope(env: JsonObject, cb?: (err?: Error) => void): void {
    this.sendRaw(serializeEnvelope(env), cb);
  }

  private sendRaw(text: string, cb?: (err?: Error) => void): void {
    if (this._state === 'closed' || !this.socket.isOpen) {
      cb?.(new Error('socket is not open'));
      return;
    }
    try {
      this.socket.send(text, cb);
    } catch (err) {
      this.logger.debug('发送失败', errorFields(err));
      cb?.(err as Error);
    }
  }

  private sendError(
    code: ErrorCode,
    message: string,
    replyTo: string | null,
    extra: JsonObject = {},
  ): void {
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.Error,
        replyTo,
        payload: buildErrorPayload(code, message, {
          retryable: errorRetryable(code),
          ...extra,
        }),
      }),
    );
  }

  /** 先发 error，等它真正写出去后再关连接（顺序保证见 ws sender 队列）。 */
  private sendErrorAndClose(
    code: ErrorCode,
    message: string,
    replyTo: string | null,
    closeCode: number,
    closeReason: string,
    extra: JsonObject = {},
  ): void {
    const env = buildEnvelope({
      type: MessageType.Error,
      replyTo,
      payload: buildErrorPayload(code, message, {
        retryable: errorRetryable(code),
        ...extra,
      }),
    });

    this.logger.info('拒绝连接', { code, closeCode, reason: closeReason });

    this.sendEnvelope(env, () => {
      this.closeWith(closeCode, closeReason);
    });
  }

  // ---------------------------------------------------------------- 关闭

  /** 由 server.ts 在需要主动断开时调用（例如进程退出，code 1001）。 */
  closeWith(code: number, reason: string): void {
    if (this._state === 'closed') return;
    this._state = 'closed';
    this.clearHelloTimer();
    this.failAllTools(`连接关闭：${reason}`);
    try {
      this.socket.close(code, reason);
    } catch (err) {
      this.logger.debug('关闭连接失败', errorFields(err));
    }
    this.logger.info('会话关闭', { code, reason, ...this.stats });
    this.onClosed();
  }

  /** 对端已关闭 / 传输层销毁：只做清理，不再动 socket。 */
  dispose(reason = 'peer closed'): void {
    if (this._state === 'closed') return;
    this._state = 'closed';
    this.clearHelloTimer();
    this.failAllTools(`连接断开：${reason}`);
    this.logger.info('会话已断开', { reason, ...this.stats });
    this.onClosed();
  }

  private clearHelloTimer(): void {
    if (this.helloTimer) {
      clearTimeout(this.helloTimer);
      this.helloTimer = null;
    }
  }
}
