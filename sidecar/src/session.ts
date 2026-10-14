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
  buildEnvelope,
  buildErrorPayload,
  buildMessagePayload,
  buildReadyPayload,
  buildStatusPayload,
  buildStreamPayload,
  errorRetryable,
  parseConfigSetPayload,
  parseFrame,
  parseHelloPayload,
  parsePromptPayload,
  serializeEnvelope,
  type ConfigView,
  type ErrorCode,
  type JsonObject,
} from './protocol.js';
import type { AgentEmitter, AgentRunner } from './agent.js';
import { maskSecret, type ConfigStore } from './config.js';
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
  /** 本次连接注册的工具名（M1 恒为空数组）。 */
  tools: readonly string[];
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
  private helloRejected = false;
  private helloTimer: NodeJS.Timeout | null = null;

  /** prompt 串行队列：保证同一连接上不会出现交错的流式输出。 */
  private promptChain: Promise<void> = Promise.resolve();

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
      case MessageType.ConfigSet:
        this.handleConfigSet(env);
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
    this._state = 'ready';
    this.logger.info('握手完成', {
      modVersion: payload.modVersion,
      gameVersion: payload.gameVersion,
      gameLanguage: payload.gameLanguage,
      capabilities: payload.capabilities,
      saveKey: payload.saveKey,
      configState: this.options.config.view().state,
    });

    this.sendEnvelope(
      buildEnvelope({
        type: MessageType.Ready,
        replyTo: env.id || null,
        payload: buildReadyPayload({
          serverVersion: this.options.serverVersion,
          sessionId: this.sessionId,
          agent: this.options.agent.info,
          tools: this.options.tools,
          // 附录 A.2：让 mod 在第一次握手就知道要不要进密钥引导态。
          config: this.options.config.view(),
        }),
      }),
    );
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
    });

    // 串行化：保证同一 prompt 的 status/stream/message 不被交错的下一轮切开。
    const emit = this.createEmitter();
    const run = this.promptChain.then(() => this.options.agent.run(prompt.text, promptId, emit));
    this.promptChain = run.catch((err: unknown) => {
      this.logger.error('agent 执行失败', errorFields(err));
      this.sendError('internal', 'agent failed', promptId);
    });
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
