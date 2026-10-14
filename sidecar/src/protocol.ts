/**
 * icarus-ii 通信协议 v1 —— 类型与校验。
 *
 * 唯一事实来源：`docs/protocol-v1.md`。改这里之前先改那份文档。
 * 本文件**不依赖 zod 或任何运行时依赖**（M0 只允许 `ws`），校验全部手写。
 *
 * 兼容性规则（协议 §7）：
 *   - 加字段允许 ⇒ 所有解析函数对未知字段**保持宽容**，只校验必需字段；
 *   - 加消息类型允许 ⇒ 未知 `type` 由上层忽略，绝不断开连接。
 */

/** 本实现声明的协议版本。 */
export const PROTOCOL_VERSION = 1;

/** 版本不匹配时回给对端的候选列表（协议 §3.3）。 */
export const SUPPORTED_PROTOCOL_VERSIONS: readonly number[] = [1];

/** 最大帧：8 MiB（协议 §1）；超出 ⇒ error{frame_too_large} + 关闭。 */
export const MAX_FRAME_BYTES = 8 * 1024 * 1024;

/** 默认端口（协议 §1）。 */
export const DEFAULT_PORT = 8765;

/** 收到 hello 的等待上限；超时按 unauthorized 处理。 */
export const HELLO_TIMEOUT_MS = 5000;

/** WebSocket 关闭码（协议 §6）。 */
export const CLOSE_NORMAL = 1000;
export const CLOSE_GOING_AWAY = 1001;
export const CLOSE_POLICY_VIOLATION = 1008; // 鉴权失败 / 协议不匹配
export const CLOSE_MESSAGE_TOO_BIG = 1009; // frame_too_large（RFC 6455 语义）

export const MessageType = {
  // mod → sidecar
  Hello: 'hello',
  Prompt: 'prompt',
  Ping: 'ping',
  ConfigSet: 'config.set',
  // sidecar → mod
  Ready: 'ready',
  Pong: 'pong',
  Status: 'status',
  Stream: 'stream',
  Message: 'message',
  Error: 'error',
  ConfigState: 'config.state',
} as const;

export type MessageTypeName = (typeof MessageType)[keyof typeof MessageType];

/** 服务端未实现、必须忽略的已预留类型（协议 §4.9）。 */
export const RESERVED_TYPES: readonly string[] = [
  'abort',
  'tool_call',
  'tool_result',
  'confirm_request',
  'confirm_result',
  'game_event',
  'ui_event',
  'session',
];

export type ErrorCode =
  | 'protocol_mismatch'
  | 'unauthorized'
  | 'frame_too_large'
  | 'bad_request'
  | 'agent_unavailable'
  | 'tool_failed'
  | 'internal'
  // 附录 A.3：M1 新增
  | 'invalid_key'
  | 'upstream_error'
  | 'rate_limited'
  | 'config_required';

export type StatusState =
  | 'idle'
  | 'thinking'
  | 'tools'
  | 'retrying'
  | 'compacting'
  | 'aborting';

export type StreamChannel = 'text' | 'reasoning';

/** 文本内容块（M0 唯一用到的形态）。 */
export interface TextContentBlock {
  type: 'text';
  text: string;
}

/** 工具卡片内容块（M1+，协议 §4.6）。 */
export interface ToolContentBlock {
  type: 'tool';
  callId: string;
  name: string;
  state: 'running' | 'ok' | 'error';
  summary?: string;
}

export type ContentBlock = TextContentBlock | ToolContentBlock;

export interface JsonObject {
  [key: string]: unknown;
}

// ---------------------------------------------------------------- 基础取值

function isPlainObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** 宽容取 string：非 string 返回 fallback。 */
export function asString(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

/** 宽容取整数：只接受 JSON 数字（且有限、整值）。 */
export function asInt(value: unknown): number | null {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null;
  if (!Number.isInteger(value)) return null;
  return value;
}

export function asBool(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback;
}

export function asStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  const out: string[] = [];
  for (const item of value) if (typeof item === 'string') out.push(item);
  return out;
}

// ---------------------------------------------------------------- 信封解析

export interface ParsedEnvelope {
  /** `v`（取不到时 null —— 只有 hello 需要它）。 */
  v: number | null;
  /** 发送方消息 id；缺失时为空串（宽容）。 */
  id: string;
  hasId: boolean;
  /** 消息类型（已校验为非空 string）。 */
  type: string;
  /** 应答目标；缺失时 null（协议 §2：无 replyTo 时不写该字段）。 */
  replyTo: string | null;
  /** 负载对象；缺失 / null 时为空对象。 */
  payload: JsonObject;
}

export type FrameFailure = 'not_json' | 'not_object' | 'invalid_type' | 'invalid_payload';

export type FrameParseResult =
  | { ok: true; envelope: ParsedEnvelope }
  | { ok: false; failure: FrameFailure };

/**
 * 解析一帧。
 *
 * - 非法 JSON ⇒ `not_json`
 * - JSON 但不是 object（数组 / 数字 / 字符串…）⇒ `not_object`
 * - `type` 缺失或非 string ⇒ `invalid_type`
 * - `payload` 存在但不是 object/null ⇒ `invalid_payload`
 *
 * 未知字段一律忽略（协议 §7.1）。
 */
export function parseFrame(text: string): FrameParseResult {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return { ok: false, failure: 'not_json' };
  }

  if (!isPlainObject(raw)) return { ok: false, failure: 'not_object' };

  const type = raw['type'];
  if (typeof type !== 'string' || type.length === 0) {
    return { ok: false, failure: 'invalid_type' };
  }

  const idValue = raw['id'];
  const hasId = typeof idValue === 'string' && idValue.length > 0;

  const payloadValue = raw['payload'];
  let payload: JsonObject = {};
  if (payloadValue !== undefined && payloadValue !== null) {
    if (!isPlainObject(payloadValue)) return { ok: false, failure: 'invalid_payload' };
    payload = payloadValue;
  }

  const replyToValue = raw['replyTo'];

  return {
    ok: true,
    envelope: {
      v: asInt(raw['v']),
      id: hasId ? (idValue as string) : '',
      hasId,
      type,
      replyTo: typeof replyToValue === 'string' && replyToValue.length > 0 ? replyToValue : null,
      payload,
    },
  };
}

// ---------------------------------------------------------------- 信封构造

let outboundSeq = 0;

/** 下一个 sidecar 侧消息 id（协议 §2 约定前缀 `s-`）。 */
export function nextOutboundId(): string {
  outboundSeq += 1;
  return `s-${outboundSeq}`;
}

export interface EnvelopeInit {
  type: string;
  payload?: JsonObject;
  id?: string;
  replyTo?: string | null;
}

/**
 * 构造信封。字段顺序固定为 v / id / type / [replyTo] / payload，
 * 便于人眼比对与抓包阅读。
 */
export function buildEnvelope(init: EnvelopeInit): JsonObject {
  const env: JsonObject = {
    v: PROTOCOL_VERSION,
    id: init.id ?? nextOutboundId(),
    type: init.type,
  };
  if (init.replyTo) env['replyTo'] = init.replyTo;
  env['payload'] = init.payload ?? {};
  return env;
}

export function serializeEnvelope(env: JsonObject): string {
  return JSON.stringify(env);
}

// ---------------------------------------------------------------- 负载解析

export interface HelloPayload {
  token: string;
  modVersion: string;
  gameVersion: string;
  gameLanguage: string;
  capabilities: string[];
  saveKey: string | null;
}

/**
 * 校验 hello 负载（协议 §4.1）。
 * 只有 `token` 是必需字段：拿不到 string token 时返回 null（⇒ unauthorized）。
 * 其余字段缺失一律取默认值（前向兼容）。
 */
export function parseHelloPayload(payload: JsonObject): HelloPayload | null {
  const token = payload['token'];
  if (typeof token !== 'string') return null;

  const saveKeyValue = payload['saveKey'];

  return {
    token,
    modVersion: asString(payload['modVersion'], 'unknown'),
    gameVersion: asString(payload['gameVersion'], 'unknown'),
    gameLanguage: asString(payload['gameLanguage'], 'unknown'),
    capabilities: asStringArray(payload['capabilities']),
    saveKey: typeof saveKeyValue === 'string' ? saveKeyValue : null,
  };
}

export interface PromptPayload {
  text: string;
  conversationId: string | null;
}

/**
 * 解析 prompt 负载（协议 §4.4）。
 * `text` 缺失按空串处理；存在但不是 string ⇒ 返回 null（bad_request）。
 */
export function parsePromptPayload(payload: JsonObject): PromptPayload | null {
  const textValue = payload['text'];
  if (textValue !== undefined && textValue !== null && typeof textValue !== 'string') return null;

  const conversationId = payload['conversationId'];

  return {
    text: typeof textValue === 'string' ? textValue : '',
    conversationId:
      typeof conversationId === 'string' && conversationId.length > 0 ? conversationId : null,
  };
}

// ---------------------------------------------------------------- 配置（附录 A）

/** `config.state.payload.state` / `ready.payload.config.state` 的取值（附录 A.1）。 */
export type ConfigStateValue = 'ready' | 'missing_key' | 'invalid_key' | 'unreachable';

/**
 * `config.state` 与 `ready.payload.config` 共用的视图。
 * **任何字段都不得包含 apiKey**（附录 A.4）。
 */
export interface ConfigView {
  state: ConfigStateValue;
  provider: string;
  model: string;
  message: string | null;
}

/**
 * `config.set` 的增量更新（附录 A.1）。
 * 只包含**给出**的字段；未给出的保持不变。
 * `baseUrl: null` 显式表示「清除覆盖」。
 */
export interface ConfigUpdatePayload {
  provider?: string;
  model?: string;
  apiKey?: string;
  baseUrl?: string | null;
}

/**
 * 解析 `config.set` 负载（附录 A.1）。
 *
 * - 字段缺失 ⇒ 不改动。
 * - `baseUrl: null` ⇒ 清除 baseUrl 覆盖；其它字段 `null` 视为「未给出」。
 * - 字段存在但不是 string（且不是 baseUrl 的 null）⇒ 返回 null（bad_request）。
 *
 * 注意：`apiKey` 是本协议中**唯一**允许承载密钥的字段（附录 A.4）。
 */
export function parseConfigSetPayload(payload: JsonObject): ConfigUpdatePayload | null {
  const out: ConfigUpdatePayload = {};

  const provider = payload['provider'];
  if (provider !== undefined && provider !== null) {
    if (typeof provider !== 'string') return null;
    out.provider = provider;
  }

  const model = payload['model'];
  if (model !== undefined && model !== null) {
    if (typeof model !== 'string') return null;
    out.model = model;
  }

  const apiKey = payload['apiKey'];
  if (apiKey !== undefined && apiKey !== null) {
    if (typeof apiKey !== 'string') return null;
    out.apiKey = apiKey;
  }

  const baseUrl = payload['baseUrl'];
  if (baseUrl !== undefined) {
    if (baseUrl === null) out.baseUrl = null;
    else if (typeof baseUrl === 'string') out.baseUrl = baseUrl;
    else return null;
  }

  return out;
}

export interface AgentInfo {
  provider: string;
  model: string;
  available: boolean;
}

// ---------------------------------------------------------------- 负载构造

export function buildReadyPayload(args: {
  serverVersion: string;
  sessionId: string;
  agent: AgentInfo;
  tools: readonly string[];
  /** 附录 A.2 新增的可选字段；给出时才会出现在 `ready.payload.config` 里。 */
  config?: ConfigView;
}): JsonObject {
  const payload: JsonObject = {
    serverVersion: args.serverVersion,
    protocol: PROTOCOL_VERSION,
    sessionId: args.sessionId,
    agent: {
      provider: args.agent.provider,
      model: args.agent.model,
      available: args.agent.available,
    },
    tools: [...args.tools],
  };
  if (args.config) payload['config'] = buildConfigStatePayload(args.config);
  return payload;
}

/** `config.state` / `ready.payload.config` 的载荷（附录 A.1 / A.2）。绝不含 apiKey。 */
export function buildConfigStatePayload(view: ConfigView): JsonObject {
  return {
    state: view.state,
    provider: view.provider,
    model: view.model,
    message: view.message,
  };
}

export function buildStatusPayload(state: StatusState, detail?: string): JsonObject {
  const payload: JsonObject = { state };
  if (detail) payload['detail'] = detail;
  return payload;
}

export function buildStreamPayload(channel: StreamChannel, delta: string): JsonObject {
  return { channel, delta };
}

export function buildMessagePayload(
  content: readonly ContentBlock[],
  options: { role?: 'assistant'; final?: boolean } = {},
): JsonObject {
  return {
    role: options.role ?? 'assistant',
    final: options.final ?? true,
    content: content.map((block) => ({ ...block })),
  };
}

export function buildErrorPayload(
  code: ErrorCode,
  message: string,
  extra: JsonObject = {},
): JsonObject {
  return { code, message, ...extra };
}

/** `error` 常用的 `retryable` 字段（协议 §4.8 示例里有它）。 */
export function errorRetryable(code: ErrorCode): boolean {
  return (
    code === 'agent_unavailable' ||
    code === 'internal' ||
    code === 'upstream_error' ||
    code === 'rate_limited'
  );
}

/** 是否是回环地址（非回环时 server.ts 会打警告）。 */
export function isLoopbackHost(host: string): boolean {
  return (
    host === '127.0.0.1' ||
    host === '::1' ||
    host === 'localhost' ||
    host.startsWith('127.')
  );
}
