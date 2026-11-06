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
  buildProviderCatalogPayload,
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
  buildConversationCreatedPayload,
  buildConversationDeletedPayload,
  buildConversationSwitchedPayload,
  buildConversationActivityPayload,
  buildConversationUpdatedPayload,
  buildConversationRenamedPayload,
  buildConversationPinnedPayload,
  buildToolsStatePayload,
  buildConversationsIndexedPayload,
  buildConversationsExportedPayload,
  parseConversationRenamePayload,
  parseConversationPinPayload,
  parseToolsSetPayload,
  parseConversationsExportPayload,
  parseConversationTargetPayload,
  parseClientStatePayload,
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
  parseContextCompactedTimePayload,
  parseReplyTimePayload,
  parseSaveBindPayload,
  parseToolResultPayload,
  parseToolDebugPayload,
  serializeEnvelope,
  type ConfigView,
  type ErrorCode,
  type ExtensionSummary,
  type JsonObject,
  type BlueprintCardPayload,
  type LinkCardPayload,
  type ContextCompactedPayload,
  type ModelCatalogResult,
  type ProviderCatalogResult,
  type ProviderTestResult,
  type ToolCatalogEntry,
  type ToolResultPayload,
  type ConfirmRequestPayload,
  type ConfirmResultPayload,
  type ConversationInfo,
  type ConversationActivityState,
  type PermissionLevel,
} from './protocol.js';
import { UI_ASK_TOOL_NAME, UI_ASK_TOOL_NAME_ALIASES, UI_ASK_TIMEOUT_MS } from './tools/ask.js';
import { GameLockManager, lockKeysForTool } from './tools/locks.js';
import type { AgentEmitter, AgentRunner } from './agent.js';
import type {
  LocalWriteConfirmRequest,
  ScriptConfirmRequest,
  ScriptConfirmResult,
} from './tools/host.js';
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
  /** 本次连接注册的工具名；可按 hello 能力与第三方插件列表动态计算。 */
  tools:
    | readonly string[]
    | ((
        capabilities: readonly string[],
        extensions: readonly ExtensionSummary[],
      ) => readonly string[]);
  /** M4m：是否允许 `tool_debug` 调试直调（默认 false）。 */
  debugTools?: boolean;
  helloTimeoutMs?: number;
  logger?: Logger;
  /** 便于测试注入。 */
  createSessionId?: () => string;
  /** 并行会话共享的游戏资源锁；缺省时每个 Session 自己一份。 */
  locks?: GameLockManager;
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

/** 适配器创建确认里给人看的摘要（最多列 5 条）。 */
function summarizeAdapterTools(value: unknown): string {
  if (!Array.isArray(value) || value.length === 0) return '（无）';
  const lines: string[] = [];
  for (const item of value.slice(0, 5)) {
    if (typeof item !== 'object' || item === null) continue;
    const row = item as Record<string, unknown>;
    const name = typeof row['name'] === 'string' ? row['name'] : '?';
    const invoke = row['invoke'];
    let target = '?';
    if (typeof invoke === 'object' && invoke !== null) {
      const call = invoke as Record<string, unknown>;
      const type = typeof call['type'] === 'string' ? call['type'] : '?';
      const method = typeof call['method'] === 'string' ? call['method'] : '?';
      target = `${type}.${method}`;
    }
    lines.push(`${name} → ${target}`);
  }
  if (value.length > 5) lines.push(`…共 ${value.length} 个`);
  return lines.length > 0 ? lines.join('；') : '（无）';
}

function describeConfirmation(
  name: string,
  args: JsonObject,
): { title: string; body: string } {
  const fmt = (value: unknown): string =>
    typeof value === 'object' && value !== null ? stableJson(value) : String(value ?? '');
  switch (name) {
    case 'game_place_marker':
      return {
        title: '放置定位标记？',
        body: `当前行星\n坐标：${fmt(args['latitude'])} / ${fmt(args['longitude'])}\n` +
          '抵达附近或离开行星后标记会自动消失。',
      };
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
          : args['kind'] === 'vein'
            ? `目标：矿脉 #${fmt(args['veinId'])}（行星 #${fmt(args['planetId'] ?? '当前')}）\n` +
              '本星球会自绘机甲→矿脉线；跨行星先指向目标行星，落地后切换。'
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
    case 'game_undo_last_write':
      return {
        title: '撤销上一步？',
        body: '会撤销最近一次写操作；如果最近一次是不可撤销操作（丢物品/取成品/方位指示），会返回错误。',
      };
    case 'ui_click':
      return {
        title: '点击界面控件？',
        body: `路径：${fmt(args['path'])}\n` +
          '会派发一次合成点击；语义未知、不可撤销，点完请用 ui_snapshot 复核。',
      };
    case 'mods_config_set':
      return {
        title: '修改其他 mod 的配置？',
        body: `mod：${fmt(args['guid'])}\n` +
          `配置：${fmt(args['section'])}/${fmt(args['key'])}` +
          `\n新值：${fmt(args['value'])}\n` +
          '会写入该 mod 的配置文件，可能立即改变游戏行为；可撤销，部分 mod 需要重启生效。',
      };
    case 'mods_adapter_create':
      return {
        title: '为 mod 创建适配器？',
        body: `mod：${fmt(args['guid'])}\n` +
          `工具：${summarizeAdapterTools(args['tools'])}\n` +
          '会把绑定写入 config/icarus-ii/bridge 并热载；创建后下一轮才可用。' +
          'AI 只能按方法名推断功能，生成的能力每次调用都会请求确认；想放行可把该 mod 的权限档位调成接管。',
      };
    case 'mods_adapter_delete':
      return {
        title: '删除 mod 适配器？',
        body: `mod：${fmt(args['guid'])}\n` +
          '只删除 config/icarus-ii/bridge 下的适配器；随包与内置适配器不受影响。撤销可恢复。',
      };
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

/** 与 mod 的权限档位保持一致：sidecar 用它判断后台写操作是否还需要等人点确认。 */
function autoApprovesWrite(permission: PermissionLevel, risk: number): boolean {
  if (risk <= 0) return true;
  if (permission === 'takeover') return true;
  if (permission === 'limited') return risk <= 1;
  return false;
}

/** 这些工具会直接改变玩家眼前的界面，后台会话调用时必须等切到该会话。 */
const FOREGROUND_ONLY_TOOLS = new Set([
  UI_ASK_TOOL_NAME,
  ...UI_ASK_TOOL_NAME_ALIASES,
  'game_open_panel',
  'ui_click',
]);

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

  /** 调试直调的串行队列；正常 prompt 由 agent 按会话排队。 */
  private promptChain: Promise<void> = Promise.resolve();
  /** 当前焦点会话；只影响 UI 帧、确认/提问和按钮，不影响后台运行。 */
  private focusedConversationId: string | null = null;
  /** 跨会话共享的写锁。 */
  private readonly locks: GameLockManager;
  /** 每个会话当前要显示的列表图标；普通状态不存，mod 显示 chevron。 */
  private readonly conversationActivity = new Map<string, ConversationActivityState>();

  /**
   * 存档切换/历史删除的串行队列。
   * 这两个操作都要 await 会话文件，如果并发执行，后到的存档可能先回执，
   * 让 A 存档的历史挂到 B 的会话上。
   */
  private bindChain: Promise<void> = Promise.resolve();

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
    this.locks = options.locks ?? new GameLockManager();
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

  /** 设置焦点会话；切到后台会话时只影响显示，不打断它。 */
  setFocusedConversation(conversationId: string | null): void {
    this.focusedConversationId = conversationId;
  }

  get focusedConversation(): string | null {
    return this.focusedConversationId;
  }

  private isFocused(conversationId?: string | null): boolean {
    return !conversationId || this.focusedConversationId === conversationId;
  }

  private async waitForFocus(
    conversationId: string,
    signal?: AbortSignal,
    timeoutMs = 10 * 60 * 1000,
  ): Promise<boolean> {
    if (this.isFocused(conversationId)) return true;
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (this._state !== 'ready') return false;
      if (signal?.aborted) return false;
      if (this.focusedConversationId === conversationId) return true;
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    return false;
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
      case MessageType.Extensions:
        this.handleExtensions(env);
        return;
      case MessageType.Ping:
        this.handlePing(env);
        return;
      case MessageType.Prompt:
        this.handlePrompt(env);
        return;
      case MessageType.Abort:
        this.handleAbort(env);
        return;
      case MessageType.ToolDebug:
        this.handleToolDebug(env);
        return;
      case MessageType.ConfigSet:
        this.handleConfigSet(env);
        return;
      case MessageType.SaveBind:
        this.queueBind(() => this.handleSaveBind(env));
        return;
      case MessageType.HistoryDelete:
        this.queueBind(() => this.handleHistoryDelete(env));
        return;
      case MessageType.ConversationCreate:
        this.queueBind(() => this.handleConversationCreate(env));
        return;
      case MessageType.ConversationSwitch:
        this.queueBind(() => this.handleConversationSwitch(env));
        return;
      case MessageType.ConversationRename:
        this.queueBind(() => this.handleConversationRename(env));
        return;
      case MessageType.ConversationDelete:
        this.queueBind(() => this.handleConversationDelete(env));
        return;
      case MessageType.ConversationPin:
        this.queueBind(() => this.handleConversationPin(env));
        return;
      case MessageType.HistoryUndoCompaction:
        this.queueBind(() => this.handleHistoryUndoCompaction(env));
        return;
      case MessageType.ReplyTime:
        this.handleReplyTime(env);
        return;
      case MessageType.ContextCompactedTime:
        this.handleContextCompactedTime(env);
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
      case MessageType.ProviderCatalog:
        this.handleProviderCatalog(env);
        return;
      case MessageType.ProviderTest:
        this.handleProviderTest(env);
        return;
      case MessageType.ToolsSet:
        this.queueBind(() => this.handleToolsSet(env));
        return;
      case MessageType.ConversationsIndex:
        this.queueBind(() => this.handleConversationsIndex(env));
        return;
      case MessageType.ConversationsExport:
        this.queueBind(() => this.handleConversationsExport(env));
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

    // 能力与扩展先同步给 agent：插件列表变化时它会重建工具集/会话。
    try {
      this.options.agent.onClientCapabilities?.(payload.capabilities, payload.extensions);
    } catch (err) {
      this.logger.warn('同步 mod 能力失败', errorFields(err));
    }

    const toolNames =
      this.options.agent.toolNames?.(payload.capabilities, payload.extensions) ??
      (typeof this.options.tools === 'function'
        ? this.options.tools(payload.capabilities, payload.extensions)
        : this.options.tools);

    // M4l：工具目录是可选字段；agent 没实现时老 mod 继续用 tools 名 + 本地短名。
    let toolCatalog: ToolCatalogEntry[] = [];
    try {
      toolCatalog = this.options.agent.toolCatalog?.(payload.capabilities, payload.extensions) ?? [];
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

  /**
   * B.1.9：mod 在设置里开关插件、或创建/删除适配器后推送新的能力/扩展列表。
   * 与 hello 走同一条同步路径：插件列表变化时在回合边界重建工具集。
   */
  private handleExtensions(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready' || !this.helloPayload) {
      this.logger.debug('忽略未握手时的 extensions 推送');
      return;
    }
    const state = parseClientStatePayload(env.payload);
    this.helloPayload = {
      ...this.helloPayload,
      capabilities: state.capabilities,
      extensions: state.extensions,
    };
    try {
      this.options.agent.onClientCapabilities?.(state.capabilities, state.extensions);
    } catch (err) {
      this.logger.warn('同步扩展推送失败', errorFields(err));
      return;
    }
    this.logger.info('收到扩展列表推送', {
      capabilities: state.capabilities.length,
      extensions: state.extensions.map((extension) => extension.id),
    });
  }

  /** 写操作属于哪个第三方插件；非第三方工具返回 undefined。 */
  private modGuidForTool(name: string, args: JsonObject): string | undefined {
    if (
      name === 'mods_config_list' ||
      name === 'mods_config_get' ||
      name === 'mods_config_set' ||
      name === 'mods_adapter_create' ||
      name === 'mods_adapter_delete'
    ) {
      const guid = args['guid'];
      return typeof guid === 'string' && guid.length > 0 ? guid : undefined;
    }
    if (!name.startsWith('mod_')) return undefined;
    for (const extension of this.helloPayload?.extensions ?? []) {
      if (name.startsWith(`mod_${extension.id}_`)) return extension.guid;
    }
    return undefined;
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
   * `provider.catalog`：只读 pi 内置目录的供应商列表，供设置页预设下拉框使用。
   * 不联网、不需要 Key；失败时回空列表 + error，mod 会回退到内置短名单。
   */
  private handleProviderCatalog(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.logger.warn('握手未完成就收到 provider.catalog');
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }

    const listProviders = this.options.agent.listProviders;
    if (!listProviders) {
      const payload = buildProviderCatalogPayload({
        providers: [],
        source: 'none',
        error: '当前 agent 不支持供应商目录',
      });
      this.sendEnvelope(buildEnvelope({ type: MessageType.ProviderCatalog, replyTo: env.id || null, payload }));
      return;
    }

    void listProviders
      .call(this.options.agent)
      .then((result: ProviderCatalogResult) => {
        this.logger.info('列出供应商目录', {
          source: result.source,
          count: result.providers.length,
        });
        this.sendEnvelope(
          buildEnvelope({
            type: MessageType.ProviderCatalog,
            replyTo: env.id || null,
            payload: buildProviderCatalogPayload(result),
          }),
        );
      })
      .catch((err: unknown) => {
        const raw = err instanceof Error ? err.message : String(err);
        const safe = redactSecrets(raw, []);
        this.logger.error('列出供应商目录失败', { message: safe });
        const payload = buildProviderCatalogPayload({
          providers: [],
          source: 'none',
          error: safe,
        });
        this.sendEnvelope(buildEnvelope({ type: MessageType.ProviderCatalog, replyTo: env.id || null, payload }));
      });
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

  /** A.14.1：设定页保存工具开关；回执带回完整目录，避免两帧不同步。 */
  private async handleToolsSet(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    const request = parseToolsSetPayload(env.payload);
    if (!request) {
      this.sendError('bad_request', 'tools.set.payload.disabled must be a string array', env.id || null);
      return;
    }

    let disabled = request.disabled;
    let rebuilt = false;
    try {
      if (this.options.agent.applyDisabledTools) {
        const result = this.options.agent.applyDisabledTools(request.disabled);
        disabled = result.disabled;
        rebuilt = result.rebuilt;
      } else {
        const view = this.options.config.update({ disabledTools: request.disabled });
        disabled = view.disabledTools ?? request.disabled;
      }
    } catch (err) {
      this.logger.error('保存工具开关失败', errorFields(err));
      this.sendError('internal', 'failed to persist tool settings', env.id || null);
      return;
    }

    const capabilities = this.helloPayload?.capabilities ?? [];
    const extensions = this.helloPayload?.extensions ?? [];
    let toolCatalog: ToolCatalogEntry[] = [];
    try {
      toolCatalog = this.options.agent.toolCatalog?.(capabilities, extensions) ?? [];
    } catch (err) {
      this.logger.warn('生成工具目录失败', errorFields(err));
    }

    this.logger.info('工具开关已更新', { disabled, rebuilt, tools: toolCatalog.length });
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ToolsState,
        replyTo: env.id || null,
        payload: buildToolsStatePayload({ ok: true, disabled, rebuilt, toolCatalog }),
      }),
    );
  }

  /** A.14.2：跨存档会话索引；只读，不打扰当前活动会话。 */
  private async handleConversationsIndex(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    let result: Awaited<ReturnType<NonNullable<AgentRunner['conversationIndex']>>> = {
      currentSaveKey: this.boundSaveKey,
      saves: [],
    };
    if (this.options.agent.conversationIndex) {
      try {
        result = await this.options.agent.conversationIndex();
      } catch (err) {
        this.logger.warn('生成会话索引失败', errorFields(err));
      }
    }
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConversationsIndexed,
        replyTo: env.id || null,
        payload: buildConversationsIndexedPayload(result),
      }),
    );
  }

  /** A.14.3：导出单个会话；路径与字节数由 sidecar 回传。 */
  private async handleConversationsExport(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    const request = parseConversationsExportPayload(env.payload);
    if (!request) {
      this.sendError('bad_request', 'conversations.export.payload 非法', env.id || null);
      return;
    }
    const saveKey = request.saveKey ?? this.boundSaveKey;
    if (!saveKey) {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.ConversationsExported,
          replyTo: env.id || null,
          payload: buildConversationsExportedPayload({ ok: false, error: 'no_save' }),
        }),
      );
      return;
    }
    if (!this.options.agent.exportConversation) {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.ConversationsExported,
          replyTo: env.id || null,
          payload: buildConversationsExportedPayload({ ok: false, error: 'unsupported' }),
        }),
      );
      return;
    }
    let result: Awaited<ReturnType<NonNullable<AgentRunner['exportConversation']>>>;
    try {
      result = await this.options.agent.exportConversation(saveKey, request.conversationId);
    } catch (err) {
      this.logger.warn('导出会话异常', errorFields(err));
      result = { ok: false, error: 'internal' };
    }
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConversationsExported,
        replyTo: env.id || null,
        payload: buildConversationsExportedPayload(result),
      }),
    );
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

  /** 把存档切换/历史删除排到同一队列，避免异步回执乱序。 */
  private queueBind(work: () => Promise<void>): void {
    const run = this.bindChain.then(work);
    this.bindChain = run.catch((err: unknown) => {
      this.logger.error('存档/历史操作失败', errorFields(err));
      this.sendError('internal', 'save operation failed', null);
    });
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
      this.options.agent.noteSaveBinding?.(bind.saveKey, bind.saveName);
      this.options.agent.onSaveChanged?.(bind.saveKey);
      this.options.agent.onSandboxChanged?.(bind.sandbox);
      this.options.agent.onBlueprintTiers?.(bind.blueprintTiers ?? null);
    } catch (err) {
      this.logger.warn('切换存档会话失败', errorFields(err));
    }
    this.boundSaveKey = bind.saveKey;
    this.conversationActivity.clear();

    // A.13：先得到该存档的会话列表（同时会解析 activeConversationId），
    // 再读当前活动会话的历史。读文件是纯本地操作，不建模型会话、不联网。
    let conversations: ConversationInfo[] = [];
    if (bind.saveKey && this.options.agent.getConversations) {
      try {
        conversations = await this.options.agent.getConversations(bind.saveKey);
      } catch (err) {
        this.logger.warn('读取会话列表失败，按单会话处理', {
          saveKey: bind.saveKey,
          ...errorFields(err),
        });
      }
    }
    const activeConversationId =
      this.options.agent.getActiveConversationId?.() ??
      conversations[0]?.id ??
      null;
    this.setFocusedConversation(activeConversationId);

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
          existed: known || conversations.length > 0 || history.length > 0,
          messageCount: history.length,
          history,
          activeConversationId,
          conversations,
        }),
      }),
    );

    if (bind.saveKey) this.ensureKnowledge();
  }

  // ---------------------------------------------------------------- 多会话（附录 A.13）

  private async handleConversationCreate(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    if (!this.boundSaveKey) {
      this.sendError('save_required', 'save.bind is required before conversation.create', env.id || null);
      return;
    }
    if (!this.options.agent.createConversation) {
      this.sendError('internal', '当前 agent 不支持多会话', env.id || null);
      return;
    }
    const result = await this.options.agent.createConversation(this.boundSaveKey);
    if (!result.ok || !result.conversation) {
      this.sendError(
        (result.error as ErrorCode) ?? 'internal',
        '创建会话失败',
        env.id || null,
      );
      return;
    }
    this.setFocusedConversation(result.conversation.id);
    this.setConversationActivity(result.conversation.id, 'idle');
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConversationCreated,
        replyTo: env.id || null,
        payload: buildConversationCreatedPayload({
          saveKey: this.boundSaveKey,
          conversation: result.conversation,
          history: result.history ?? [],
          running: false,
          state: 'idle',
        }),
      }),
    );
  }

  private async handleConversationSwitch(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    if (!this.boundSaveKey) {
      this.sendError('save_required', 'save.bind is required before conversation.switch', env.id || null);
      return;
    }
    const target = parseConversationTargetPayload(env.payload);
    if (!target) {
      this.sendError('bad_request', 'conversation.switch.payload.conversationId 非法', env.id || null);
      return;
    }
    if (!this.options.agent.switchConversation) {
      this.sendError('internal', '当前 agent 不支持多会话', env.id || null);
      return;
    }
    const result = await this.options.agent.switchConversation(this.boundSaveKey, target.conversationId);
    if (!result.ok || !result.conversation) {
      this.sendError(
        (result.error as ErrorCode) ?? 'internal',
        '切换会话失败',
        env.id || null,
      );
      return;
    }
    const history = result.history ?? [];
    const running = this.options.agent.isRunning?.(target.conversationId) === true;
    // 先读进度再切焦点：切焦点后新增量会立刻开始发送，若快照晚读会重复一段正文。
    const progress = this.options.agent.getProgress?.(target.conversationId);
    this.setFocusedConversation(target.conversationId);
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConversationSwitched,
        replyTo: env.id || null,
        payload: buildConversationSwitchedPayload({
          saveKey: this.boundSaveKey,
          conversation: result.conversation,
          existed: history.length > 0,
          messageCount: history.length,
          running,
          state: running ? 'thinking' : 'idle',
          history,
          ...(result.discardedConversationId
            ? { discardedConversationId: result.discardedConversationId }
            : {}),
        }),
      }),
    );

    // 切回一个仍在回答的会话：把切走期间已经生成的正文补成一条 stream，
    // 之后的新增量继续按正常流式发送。工具/思考块不重放，最终正文以 message 为准。
    const activity = this.conversationActivity.get(target.conversationId);
    if (activity === 'completed') {
      this.setConversationActivity(target.conversationId, 'idle');
    } else if (running && activity !== 'attention') {
      this.setConversationActivity(target.conversationId, 'running');
    } else if (!running && activity === 'running') {
      this.setConversationActivity(target.conversationId, 'idle');
    }

    if (progress?.running && progress.text.length > 0) {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.Stream,
          replyTo: env.id ?? null,
          payload: {
            ...buildStreamPayload('text', progress.text),
            conversationId: target.conversationId,
          },
        }),
      );
    }
  }

  private async handleConversationRename(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    const payload = parseConversationRenamePayload(env.payload ?? {});
    if (!payload) {
      this.sendError('bad_request', 'conversation.rename.payload 非法', env.id || null);
      return;
    }
    // A.14.4：可选 saveKey = 跨存档重命名；缺省仍走当前存档。
    const saveKey = payload.saveKey ?? this.boundSaveKey;
    const reply = (ok: boolean, title: string, error?: string | null) => {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.ConversationRenamed,
          replyTo: env.id || null,
          payload: buildConversationRenamedPayload({
            saveKey,
            conversationId: payload.conversationId,
            title,
            ok,
            error: error ?? null,
          }),
        }),
      );
    };
    if (!saveKey) {
      reply(false, payload.title, 'save_required');
      return;
    }
    if (!this.options.agent.renameConversation) {
      reply(false, payload.title, 'internal');
      return;
    }
    try {
      const result = await this.options.agent.renameConversation(
        saveKey,
        payload.conversationId,
        payload.title,
      );
      reply(result.ok, result.title ?? payload.title, result.error);
    } catch (err) {
      this.logger.warn('重命名会话异常', { error: err instanceof Error ? err.message : String(err) });
      reply(false, payload.title, 'internal');
    }
  }

  private async handleConversationDelete(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    const target = parseConversationTargetPayload(env.payload);
    if (!target) {
      this.sendError('bad_request', 'conversation.delete.payload.conversationId 非法', env.id || null);
      return;
    }
    // A.14.4：可选 saveKey = 跨存档删除；缺省仍走当前存档。
    const saveKey = target.saveKey ?? this.boundSaveKey;
    if (!saveKey) {
      this.sendError('save_required', 'save.bind is required before conversation.delete', env.id || null);
      return;
    }
    if (!this.options.agent.deleteConversation) {
      this.sendError('internal', '当前 agent 不支持多会话', env.id || null);
      return;
    }
    const result = await this.options.agent.deleteConversation(saveKey, target.conversationId);
    if (!result.ok) {
      this.sendError(
        (result.error as ErrorCode) ?? 'internal',
        '删除会话失败',
        env.id || null,
      );
      return;
    }

    // 非当前存档：不读取/不改动当前活动会话，回执只带目标存档。
    const isCurrentSave = saveKey === this.boundSaveKey;
    const conversations = isCurrentSave && this.options.agent.getConversations
      ? await this.options.agent.getConversations(saveKey)
      : [];
    const activeConversationId = isCurrentSave
      ? this.options.agent.getActiveConversationId?.() ?? result.activeConversationId ?? null
      : result.activeConversationId ?? null;
    this.clearConversationActivity(target.conversationId);
    if (isCurrentSave && activeConversationId) this.setFocusedConversation(activeConversationId);
    const conversation = conversations.find((item) => item.id === activeConversationId) ?? null;
    const history = isCurrentSave && this.options.agent.getHistory
      ? await this.options.agent.getHistory(saveKey)
      : [];
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConversationDeleted,
        replyTo: env.id || null,
        payload: buildConversationDeletedPayload({
          saveKey,
          conversationId: target.conversationId,
          ok: true,
          activeConversationId,
          conversation,
          messageCount: history.length,
          history,
          conversations,
        }),
      }),
    );

    // 删掉焦点会话后，新焦点可能是一个仍在后台回答的会话；补一条状态把按钮置回中断。
    if (
      isCurrentSave &&
      activeConversationId &&
      this.options.agent.isRunning?.(activeConversationId)
    ) {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.Status,
          payload: {
            ...buildStatusPayload('thinking'),
            conversationId: activeConversationId,
          },
        }),
      );
    }
  }

  /**
   * A.13：置顶/取消置顶一个会话（附录 A.13）。
   *
   * 置顶只影响左栏排序：不改会话内容、不切换活动会话、不算"已读"。
   * mod 会先本地乐观置顶，回执只核对结果；失败时 mod 回滚。
   */
  private async handleConversationPin(env: import('./protocol.js').ParsedEnvelope): Promise<void> {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    const payload = parseConversationPinPayload(env.payload ?? {});
    if (!payload) {
      this.sendError('bad_request', 'conversation.pin.payload 非法', env.id || null);
      return;
    }
    // A.14.4：可选 saveKey = 跨存档置顶；缺省仍走当前存档。
    const saveKey = payload.saveKey ?? this.boundSaveKey;
    const reply = (ok: boolean, error?: string | null) => {
      this.sendEnvelope(
        buildEnvelope({
          type: MessageType.ConversationPinned,
          replyTo: env.id || null,
          payload: buildConversationPinnedPayload({
            saveKey,
            conversationId: payload.conversationId,
            pinned: payload.pinned,
            ok,
            error: error ?? null,
          }),
        }),
      );
    };
    if (!saveKey) {
      reply(false, 'save_required');
      return;
    }
    if (!this.options.agent.pinConversation) {
      reply(false, 'internal');
      return;
    }
    try {
      const result = await this.options.agent.pinConversation(
        saveKey,
        payload.conversationId,
        payload.pinned,
      );
      reply(result.ok, result.error);
    } catch (err) {
      this.logger.warn('置顶会话异常', { error: err instanceof Error ? err.message : String(err) });
      reply(false, 'internal');
    }
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
        const conversationId =
          payload.conversationId ??
          this.focusedConversationId ??
          this.options.agent.getActiveConversationId?.() ??
          null;
        result = await this.options.agent.deleteHistory(
          saveKey,
          payload.text,
          payload.timestamp,
          conversationId,
        );
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
        const conversationId =
          payload.conversationId ??
          this.focusedConversationId ??
          this.options.agent.getActiveConversationId?.() ??
          null;
        result = await this.options.agent.undoCompaction(
          saveKey,
          payload.compactionId,
          conversationId,
        );
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

    // A.13：显式 conversationId 决定本轮路由；缺省用焦点会话，再缺省用 agent 的活动会话。
    const conversationId =
      prompt.conversationId ??
      this.focusedConversationId ??
      this.options.agent.getActiveConversationId?.() ??
      null;
    // 不因为收到 prompt 就抢焦点：焦点由 conversation.switch / create 决定。
    // 只有连接还没有焦点（老 mod）时才用本轮会话兜底。
    if (conversationId && !this.focusedConversationId) this.setFocusedConversation(conversationId);

    const promptId = env.id || null;
    this.promptCount += 1;
    this.logger.info('收到 prompt', {
      promptId,
      chars: prompt.text.length,
      conversationId,
      requiredTool: prompt.requiredTool,
      mode: prompt.mode,
    });

    // A.15：插话必须立刻投递到运行中的回合，不能进串行链（那会等当前回合结束）。
    if (prompt.mode === 'steer' && this.options.agent.steer) {
      void this.steerOrPrompt(prompt, promptId, conversationId);
      return;
    }
    this.startPromptRun(prompt, promptId, conversationId);
  }

  /** A.15：先尝试插话；没有运行中的回合就退回普通提问，不能把玩家的话丢掉。 */
  private async steerOrPrompt(
    prompt: import('./protocol.js').PromptPayload,
    promptId: string | null,
    conversationId: string | null,
  ): Promise<void> {
    try {
      const steered = await this.options.agent.steer!(prompt.text, conversationId);
      if (steered) {
        this.logger.info('插话已投递给运行中的回合', { promptId, chars: prompt.text.length });
        return;
      }
    } catch (err) {
      this.logger.warn('插话失败，按普通提问处理', { promptId, ...errorFields(err) });
    }
    this.logger.info('插话时没有运行中的回合，按普通提问处理', { promptId });
    this.startPromptRun(prompt, promptId, conversationId);
  }

  /** 启动一轮 prompt：知识库就绪 → agent.run；不同会话由 agent 并行调度。 */
  private startPromptRun(
    prompt: import('./protocol.js').PromptPayload,
    promptId: string | null,
    conversationId: string | null,
  ): void {
    const emit = this.createEmitter(conversationId);
    this.setConversationActivity(conversationId, 'running');
    void (async () => {
      try {
        const knowledge = this.options.knowledge;
        if (knowledge) {
          // 知识库导出通常 <1s；首问等一小会儿，避免模型在旧版本/ kb_unavailable 下回答。
          // 无导出在途时立即返回，不额外发 status 帧（echo/无知识库路径的帧序必须保持 M0 契约）。
          await knowledge.whenReady(2500);
        }
        await this.options.agent.run(
          prompt.text,
          promptId,
          emit,
          prompt.requiredTool,
          conversationId,
        );
      } catch (err) {
        this.logger.error('agent 执行失败', errorFields(err));
        emit.error('internal', 'agent failed', promptId);
      }
    })();
  }

  /** A.15：玩家中断当前回合。没有回合在跑时静默忽略。 */
  private handleAbort(env: import('./protocol.js').ParsedEnvelope): void {
    if (this._state !== 'ready') {
      this.sendError('unauthorized', 'handshake not completed', env.id || null);
      return;
    }
    const agent = this.options.agent;
    const abort = agent.abort;
    if (!abort) {
      this.sendError('internal', '当前 agent 不支持中断', env.id || null);
      return;
    }
    const rawTarget = env.payload?.['conversationId'];
    const conversationId =
      typeof rawTarget === 'string' && rawTarget.trim()
        ? rawTarget.trim()
        : this.focusedConversationId ??
          this.options.agent.getActiveConversationId?.() ??
          null;
    if (agent.isRunning && !agent.isRunning(conversationId)) {
      this.logger.info('收到 abort，但目标会话没有运行中的回合，忽略', { conversationId });
      return;
    }
    this.logger.info('收到 abort', { conversationId });
    // abort() 要等 agent 空闲才返回，那时 interrupted 终稿和 idle 已经发出去了；
    // aborting 必须在中断动作之前发，否则会排在 idle 后面，把界面重新置成「回答中」。
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.Status,
        payload: {
          ...buildStatusPayload('aborting'),
          ...(conversationId ? { conversationId } : {}),
        },
      }),
    );
    // 不排队：中断必须立刻作用在当前回合上（排队会等回合结束，等于没断）。
    void abort.call(agent, conversationId).catch((err: unknown) => {
      this.logger.warn('中断失败', errorFields(err));
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
      parsed.conversationId ??
        this.focusedConversationId ??
        this.options.agent.getActiveConversationId?.() ??
        null,
      parsed.gameTick ?? null,
    );
  }

  private handleContextCompactedTime(env: import('./protocol.js').ParsedEnvelope): void {
    const parsed = parseContextCompactedTimePayload(env.payload);
    if (!parsed) {
      this.logger.warn('context.compacted.time 载荷无效，已忽略', { promptId: env.id ?? null });
      return;
    }
    this.options.agent.recordCompactionTime?.(this.boundSaveKey, {
      ...(parsed.compactionId ? { compactionId: parsed.compactionId } : {}),
      conversationId:
        parsed.conversationId ??
        this.focusedConversationId ??
        this.options.agent.getActiveConversationId?.() ??
        undefined,
      gameTime: parsed.gameTime,
      ...(typeof parsed.gameTick === 'number' ? { gameTick: parsed.gameTick } : {}),
    });
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
      conversationId?: string;
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
    const conversationId = options.conversationId ?? this.focusedConversationId ?? null;
    const permission = this.options.config.view().permissionLevel;

    // 后台会话不抢玩家的确认页/提问面板/界面；等玩家切到该会话再发。
    // limited 下 risk 1 会被 mod 自动放行，不需要 focus；risk 2 仍要等。
    const needsAttention =
      isAsk || (risk > 0 && !autoApprovesWrite(permission, risk));
    const needsFocus = needsAttention || FOREGROUND_ONLY_TOOLS.has(name);
    if (conversationId && needsAttention) {
      // 确认页/提问面板展示期间保持 warning；玩家作答后再回到 loading。
      this.setConversationActivity(conversationId, 'attention', isAsk ? 'ask' : 'confirm');
    }
    if (conversationId && needsFocus && !this.isFocused(conversationId)) {
      const focused = await this.waitForFocus(conversationId, options.signal);
      if (!focused) {
        // 回合本身可能还在跑（例如玩家一直不切过来），不要把图标误清成 completed。
        this.setConversationActivity(
          conversationId,
          options.signal?.aborted ? 'idle' : 'running',
        );
        return toolFailure(
          options.signal?.aborted ? 'cancelled' : 'not_focused',
          options.signal?.aborted
            ? `工具 ${name} 已取消`
            : `会话 ${conversationId} 不在前台，玩家切换后再重试`,
        );
      }
    }

    let token: string | undefined = options.confirmToken;
    let argsHash: string | undefined = options.confirmArgsHash;
    if (!token && risk > 0) {
      const approved = await this.requestConfirmation(
        callId,
        name,
        args,
        risk,
        options.signal,
        undefined,
        conversationId,
      );
      if (conversationId && needsAttention) {
        this.setConversationActivity(conversationId, 'running');
      }
      if (!approved) {
        return toolFailure('confirmation_denied', '玩家拒绝了这次写操作，未执行');
      }
      token = approved.token;
      argsHash = approved.argsHash;
    }

    const lockKeys = lockKeysForTool(name, args, risk);
    if (lockKeys.length > 0) {
      const locked = await this.locks.acquire(
        conversationId ?? 'legacy',
        callId,
        lockKeys,
        { signal: options.signal, waitMs: 3000 },
      );
      if (!locked.ok) {
        const owner = locked.conflicts?.[0]?.conversationId ?? '另一个会话';
        const path = locked.conflicts?.[0]?.heldPath ?? '资源';
        return toolFailure(
          'resource_locked',
          `${path} 正被 ${owner} 使用；等它释放或换一个建筑/工厂再试`,
        );
      }
      if (options.signal?.aborted) {
        this.locks.release(callId);
        return toolFailure('cancelled', `工具 ${name} 已取消`);
      }
    }

    const result = await new Promise<ToolResultPayload>((resolve) => {
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
          this.locks.release(callId);
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
            ...(conversationId ? { conversationId } : {}),
          }),
        }),
        (err) => {
          if (err) {
            this.settleTool(callId, toolFailure('tool_unavailable', `发送 tool_call 失败：${err.message}`));
          }
        },
      );
    });
    if (conversationId && needsAttention) {
      this.setConversationActivity(conversationId, 'running');
    }
    return result;
  }

  /**
   * run_script 的脚本级确认：一次确认授权脚本最多 maxWrites 次写调用。
   * token 绑定脚本哈希；mod 只接受该 token + 同一哈希的写 tool_call。
   */
  private async confirmScript(request: ScriptConfirmRequest): Promise<ScriptConfirmResult | null> {
    const conversationId = request.conversationId ?? null;
    if (conversationId) {
      this.setConversationActivity(conversationId, 'attention', 'confirm');
      if (!this.isFocused(conversationId)) {
        const focused = await this.waitForFocus(conversationId);
        if (!focused) {
          this.setConversationActivity(conversationId, 'running');
          return null;
        }
      }
    }
    const writeNames = request.writeNames.slice(0, 12);
    const more = request.writeNames.length > writeNames.length ? '…' : '';
    const approved = await this.requestConfirmation(
      'script',
      'run_script',
      {},
      request.risk,
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
      conversationId,
    );
    if (conversationId) this.setConversationActivity(conversationId, 'running');
    if (!approved) return null;
    return { token: approved.token, scriptHash: request.scriptHash };
  }

  /**
   * 本地蓝图写确认。确认令牌由 mod 签发后不需要回收：它不会进入 tool_call，
   * 60 秒后自动过期；这里只关心玩家是否同意。
   */
  private async confirmLocalWrite(request: LocalWriteConfirmRequest): Promise<boolean> {
    const conversationId = request.conversationId ?? null;
    if (conversationId) {
      this.setConversationActivity(conversationId, 'attention', 'confirm');
      if (!this.isFocused(conversationId)) {
        const focused = await this.waitForFocus(conversationId);
        if (!focused) {
          this.setConversationActivity(conversationId, 'running');
          return false;
        }
      }
    }
    const approved = await this.requestConfirmation(
      `local_${request.tool}`,
      request.tool,
      request.args,
      request.risk,
      undefined,
      { title: request.title, body: request.body },
      conversationId,
    );
    if (conversationId) this.setConversationActivity(conversationId, 'running');
    return approved != null;
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
    conversationId?: string | null,
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
            ...(this.modGuidForTool(name, args)
              ? { modGuid: this.modGuidForTool(name, args) as string }
              : {}),
            ...(override?.maxWrites ? { maxWrites: override.maxWrites } : {}),
            ...(override?.writeNames ? { writeNames: override.writeNames } : {}),
            ...(conversationId ? { conversationId } : {}),
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
    this.locks.release(callId);
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
    this.locks.releaseAll();
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
  private createEmitter(conversationId: string | null = null): AgentEmitter {
    const session = this;
    const focused = (): boolean => session.isFocused(conversationId);
    const withId = (payload: JsonObject): JsonObject =>
      conversationId ? { ...payload, conversationId } : payload;
    return {
      isActive: () => session._state === 'ready' && session.socket.isOpen,

      status(state, detail) {
        if (!focused()) return;
        if (conversationId) {
          if (state === 'idle') session.setConversationActivity(conversationId, 'idle');
          else session.setConversationActivity(conversationId, 'running');
        }
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.Status,
            payload: withId(buildStatusPayload(state, detail)),
          }),
        );
      },

      stream(channel, delta, replyTo) {
        if (!focused() || delta.length === 0) return;
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.Stream,
            replyTo,
            payload: withId(buildStreamPayload(channel, delta)),
          }),
        );
      },

      message(text, replyTo, final = true, options = {}) {
        if (final && conversationId) {
          if (options.interrupted) session.setConversationActivity(conversationId, 'idle');
          else if (focused()) session.setConversationActivity(conversationId, 'idle');
          else session.setConversationActivity(conversationId, 'completed');
        }
        // 终稿即使属于后台会话也要送到 mod：mod 不显示它，但要取游戏时间回存
        // reply.time，否则切回去的历史脚注会缺时间。非终稿仍然只发焦点会话。
        if (!focused() && !final) return;
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.Message,
            replyTo,
            payload: withId(
              buildMessagePayload([{ type: 'text', text }], {
                role: 'assistant',
                final,
                ...(options.interrupted ? { interrupted: true } : {}),
              }),
            ),
          }),
        );
      },

      error(code, message, replyTo) {
        if (conversationId) session.setConversationActivity(conversationId, 'idle');
        if (!focused()) return;
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
        return session.callTool(name, args, {
          ...options,
          ...(conversationId ? { conversationId } : {}),
        });
      },

      confirmScript(request) {
        return session.confirmScript({
          ...request,
          ...(conversationId ? { conversationId } : {}),
        });
      },

      confirmLocalWrite(request) {
        return session.confirmLocalWrite({
          ...request,
          ...(conversationId ? { conversationId } : {}),
        });
      },

      releaseScript(token) {
        session.releaseScript(token);
      },

      toolActivity(payload) {
        if (!focused()) return;
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.ToolActivity,
            payload: withId(buildToolActivityPayload(payload)),
          }),
        );
      },

      blueprintCard(payload: BlueprintCardPayload) {
        if (!focused()) return;
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.BlueprintCard,
            payload: withId(buildBlueprintCardPayload(payload)),
          }),
        );
      },

      linkCard(payload: LinkCardPayload) {
        if (!focused()) return;
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.LinkCard,
            payload: withId(buildLinkCardPayload(payload)),
          }),
        );
      },

      compacted(payload: ContextCompactedPayload) {
        if (!focused()) return;
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.ContextCompacted,
            payload: withId(buildContextCompactedPayload(payload)),
          }),
        );
      },

      conversationUpdated(payload: { conversationId: string; title: string }) {
        session.sendEnvelope(
          buildEnvelope({
            type: MessageType.ConversationUpdated,
            payload: buildConversationUpdatedPayload(payload),
          }),
        );
      },
    };
  }

  /**
   * 更新并广播会话状态图标。同一状态不重复发；idle 只清已有状态。
   * 这是 UI 旁路，不受焦点过滤影响。
   */
  private setConversationActivity(
    conversationId: string | null | undefined,
    state: ConversationActivityState,
    reason?: 'confirm' | 'ask',
  ): void {
    if (!conversationId) return;
    const previous = this.conversationActivity.get(conversationId);
    if (state === 'idle') {
      if (previous === undefined) return;
      this.conversationActivity.delete(conversationId);
    } else {
      if (previous === state) return;
      this.conversationActivity.set(conversationId, state);
    }
    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.ConversationActivity,
        payload: buildConversationActivityPayload({
          conversationId,
          state,
          ...(reason ? { reason } : {}),
        }),
      }),
    );
  }

  private clearConversationActivity(conversationId: string | null | undefined): void {
    if (conversationId) this.conversationActivity.delete(conversationId);
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
    this.options.agent.abortAll?.();
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
    this.options.agent.abortAll?.();
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
