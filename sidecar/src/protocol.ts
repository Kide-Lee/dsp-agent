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
  /** B.1.9：能力/扩展列表变化后补发；旧端按未知类型忽略。 */
  Extensions: 'extensions',
  Prompt: 'prompt',
  Ping: 'ping',
  ConfigSet: 'config.set',
  SaveBind: 'save.bind',
  // M4m：调试直调（不经过 AI）
  ToolDebug: 'tool_debug',
  // sidecar → mod
  Ready: 'ready',
  Pong: 'pong',
  Status: 'status',
  Stream: 'stream',
  Message: 'message',
  Error: 'error',
  ConfigState: 'config.state',
  SaveBound: 'save.bound',
  // A.13：一个存档多个会话
  ConversationCreate: 'conversation.create',
  ConversationCreated: 'conversation.created',
  ConversationSwitch: 'conversation.switch',
  ConversationSwitched: 'conversation.switched',
  ConversationDelete: 'conversation.delete',
  ConversationDeleted: 'conversation.deleted',
  // A.13：标题由模型异步总结后更新（不重建正文）
  ConversationUpdated: 'conversation.updated',
  // A.13.1：会话状态图标（后台运行/需要授权/已完成）
  ConversationActivity: 'conversation.activity',
  ConversationRename: 'conversation.rename',
  ConversationRenamed: 'conversation.renamed',
  // A.13：会话置顶（只影响左侧列表排序）
  ConversationPin: 'conversation.pin',
  ConversationPinned: 'conversation.pinned',
  // M4e：删除一个问答回合（附录 A.7）
  HistoryDelete: 'history.delete',
  HistoryDeleted: 'history.deleted',
  // 撤销一次上下文压缩（附录 A.11）
  HistoryUndoCompaction: 'history.undo_compaction',
  HistoryCompactionUndone: 'history.compaction_undone',
  // 压缩完成事件（附录 A.11）；只给 UI，不进入模型上下文
  ContextCompacted: 'context.compacted',
  // M4f：sidecar 本地工具的 UI 事件（附录 A.8）
  ToolActivity: 'tool_activity',
  // M4i：蓝图卡片（附录 A.9）
  BlueprintCard: 'blueprint_card',
  // 实体链接卡片（建筑/星球）
  LinkCard: 'link_card',
  // 回存回答完成时的游戏时间（附录 A.10）
  ReplyTime: 'reply.time',
  // M3：拉取供应商的可用模型目录 / 检测 Key
  ModelCatalog: 'model.catalog',
  // 设置页预设供应商下拉框：pi 内置目录的全部供应商
  ProviderCatalog: 'provider.catalog',
  ProviderTest: 'provider.test',
  // A.14：设定页——工具开关、跨存档会话索引与导出
  ToolsSet: 'tools.set',
  ToolsState: 'tools.state',
  ConversationsIndex: 'conversations.index',
  ConversationsIndexed: 'conversations.indexed',
  ConversationsExport: 'conversations.export',
  ConversationsExported: 'conversations.exported',
  // M2：只读游戏工具（协议附录 B）
  ToolCall: 'tool_call',
  ToolResult: 'tool_result',
  // M4m：调试直调结果
  ToolDebugResult: 'tool_debug_result',
  // M4：写操作确认（附录 A.12）
  ConfirmRequest: 'confirm_request',
  ConfirmResult: 'confirm_result',
  ConfirmRelease: 'confirm_release',
  // A.15：玩家中断当前回合（mod → sidecar）
  Abort: 'abort',
} as const;

export type MessageTypeName = (typeof MessageType)[keyof typeof MessageType];

/** 服务端未实现、必须忽略的已预留类型（协议 §4.9）。 */
export const RESERVED_TYPES: readonly string[] = [
  'game_event',
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
  | 'config_required'
  | 'save_required'
  // M2：工具链路
  | 'tool_not_found'
  | 'tool_unavailable'
  | 'not_in_game'
  | 'invalid_args'
  | 'timeout'
  | 'busy'
  | 'cancelled'
  // M4l：prompt.requiredTool 未被调用
  | 'tool_required'
  // M4m：调试直调
  | 'debug_disabled'
  | 'confirmation_required'
  | 'confirmation_invalid'
  | 'confirmation_expired'
  | 'confirmation_denied'
  // M5：沙盒门禁
  | 'sandbox_required'
  // A.13：多会话
  | 'conversation_not_found'
  | 'conversation_mismatch'
  | 'conversation_busy'
  | 'invalid_title'
  // 并行会话：后台会话的资源/交互限制
  | 'not_focused'
  | 'resource_locked';

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

// ---------------------------------------------------------------- 工具（附录 B）

export interface JsonObject {
  [key: string]: unknown;
}

/** `tool_call` 的负载（sidecar → mod）。 */
export interface ToolCallPayload {
  callId: string;
  name: string;
  args: JsonObject;
  risk: 0 | 1 | 2;
  timeoutMs: number;
  /** true 表示 sidecar 内部调用（如知识库导出），mod 不显示工具卡。 */
  internal?: boolean;
  /** 写操作一次性令牌；risk>0 时由确认流程生成。 */
  confirmToken?: string;
  /** 确认时绑定的参数哈希。 */
  confirmArgsHash?: string;
  /** 发起本次调用的会话；并行会话下用于 UI 路由。 */
  conversationId?: string;
}

/** `confirm_request`（sidecar → mod，M4）。 */
export interface ConfirmRequestPayload {
  requestId: string;
  callId: string;
  tool: string;
  argsHash: string;
  token: string;
  title: string;
  body: string;
  risk: 1 | 2;
  timeoutMs: number;
  /** B.1.6：写操作属于哪个第三方插件；mod 用它决定该插件的权限档位。 */
  modGuid?: string;
  /** run_script 的脚本写租约：允许的写调用次数；0/缺省 = 普通单次确认。 */
  maxWrites?: number;
  /** 脚本确认时列出的写工具名；租约只允许这些工具。 */
  writeNames?: string[];
  /** 确认请求所属会话；并行会话下用于焦点门控/UI 路由。 */
  conversationId?: string;
}

/** `confirm_result`（mod → sidecar）。 */
export interface ConfirmResultPayload {
  requestId: string;
  decision: 'allow' | 'deny';
  token: string;
}

export function buildConfirmRequestPayload(args: ConfirmRequestPayload): JsonObject {
  return { ...args };
}

export function parseConfirmResultPayload(payload: JsonObject): ConfirmResultPayload | null {
  if (!isPlainObject(payload)) return null;
  const requestId = typeof payload['requestId'] === 'string' ? payload['requestId'] : '';
  const token = typeof payload['token'] === 'string' ? payload['token'] : '';
  const decision = payload['decision'];
  if (!requestId || !token || (decision !== 'allow' && decision !== 'deny')) return null;
  return { requestId, token, decision };
}

/** `tool_result.error`（mod → sidecar）。 */
export interface ToolErrorPayload {
  code: string;
  message: string;
  /** 可选；缺省按 false 处理。 */
  retryable?: boolean;
}

/** `tool_result.image`（mod → sidecar，M4a）：截图等二进制附件。 */
export interface ToolImagePayload {
  mimeType: string;
  /** base64，不含 data URL 前缀。 */
  data: string;
  width: number;
  height: number;
}

/** 图片 base64 上限（协议附录 B.3：解码后 ≤ 2 MiB）。 */
export const MAX_TOOL_IMAGE_BYTES = 2 * 1024 * 1024;

/**
 * `tool_result` 的负载（mod → sidecar）。
 * `data` 只要求是合法 JSON；具体形状由各工具自己的 schema 约束。
 */
export interface ToolResultPayload {
  ok: boolean;
  data: unknown;
  error: ToolErrorPayload | null;
  meta: JsonObject;
  /** 可选附件；只在 ok=true 时有效。 */
  image?: ToolImagePayload | null;
}

// ---------------------------------------------------------------- 调试直调（M4m）

/** `tool_debug` 的负载（mod → sidecar）。 */
export interface ToolDebugPayload {
  name: string;
  args: JsonObject;
}

const MAX_DEBUG_ARGS_BYTES = 64 * 1024;

export function parseToolDebugPayload(payload: JsonObject): ToolDebugPayload | null {
  if (!isPlainObject(payload)) return null;
  const name = typeof payload['name'] === 'string' ? payload['name'].trim() : '';
  if (!name || name.length > 128) return null;
  const rawArgs = payload['args'] === undefined || payload['args'] === null ? {} : payload['args'];
  if (!isPlainObject(rawArgs)) return null;
  try {
    if (Buffer.byteLength(JSON.stringify(rawArgs), 'utf8') > MAX_DEBUG_ARGS_BYTES) return null;
  } catch {
    return null;
  }
  return { name, args: rawArgs };
}

export interface ToolDebugImageInfo {
  mimeType: string;
  width: number;
  height: number;
  bytes: number;
}

/** `tool_debug_result` 的负载（sidecar → mod）。 */
export interface ToolDebugResultPayload {
  ok: boolean;
  tool: string;
  source: 'game' | 'local' | 'unknown';
  durationMs: number;
  /** 模型可见文本（不含图片 base64）。 */
  content: string;
  /** 原始 details；图片 data 已剥离，超限时可能退化为截断字符串。 */
  details: unknown;
  error: { code: string; message: string } | null;
  truncated: boolean;
  image: ToolDebugImageInfo | null;
}

export function buildToolDebugResultPayload(result: ToolDebugResultPayload): JsonObject {
  return {
    ok: result.ok,
    tool: result.tool,
    source: result.source,
    durationMs: result.durationMs,
    content: result.content,
    details: result.details === undefined ? null : result.details,
    error: result.error ? { code: result.error.code, message: result.error.message } : null,
    truncated: result.truncated,
    image: result.image
      ? {
          mimeType: result.image.mimeType,
          width: result.image.width,
          height: result.image.height,
          bytes: result.image.bytes,
        }
      : null,
  };
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

export interface ExtensionTool {
  /** 完整工具名（`mod_<id>_<name>`），mod 生成，sidecar 原样使用。 */
  name: string;
  label: string;
  description: string;
  risk: 0 | 1 | 2;
  timeoutMs: number;
  /** JSON Schema；直接作为工具参数 schema。 */
  parameters: Record<string, unknown>;
}

export interface ExtensionSummary {
  /** 工具名用的短 id（`[a-z0-9_]{1,24}`），由 mod 生成并保证唯一。 */
  id: string;
  guid: string;
  name: string;
  version: string;
  configCount: number;
  /** manifest 声明的工具；没有 manifest 时为空数组。 */
  tools: ExtensionTool[];
}

export interface HelloPayload {
  token: string;
  modVersion: string;
  gameVersion: string;
  gameLanguage: string;
  capabilities: string[];
  saveKey: string | null;
  /** 第三方 mod 桥（`mod.bridge.v1`）：当前已加载的插件摘要；老 mod 不发。 */
  extensions: ExtensionSummary[];
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
    extensions: parseExtensions(payload['extensions']),
  };
}

/** B.1.9：能力/扩展推送的负载；缺字段一律按空处理。 */
export function parseClientStatePayload(payload: JsonObject): {
  capabilities: string[];
  extensions: ExtensionSummary[];
} {
  return {
    capabilities: asStringArray(payload['capabilities']),
    extensions: parseExtensions(payload['extensions']),
  };
}

/** 扩展摘要只接受结构化字段；格式不对的条目直接忽略，不让它影响握手。 */
function parseExtensions(value: unknown): ExtensionSummary[] {
  if (!Array.isArray(value)) return [];
  const seen = new Set<string>();
  const out: ExtensionSummary[] = [];
  for (const item of value) {
    if (typeof item !== 'object' || item === null) continue;
    const row = item as Record<string, unknown>;
    const id = typeof row['id'] === 'string' ? row['id'].trim() : '';
    const guid = typeof row['guid'] === 'string' ? row['guid'].trim() : '';
    if (!/^[a-z0-9_]{1,24}$/.test(id) || guid.length === 0 || seen.has(id)) continue;
    seen.add(id);
    out.push({
      id,
      guid,
      name: typeof row['name'] === 'string' ? row['name'] : guid,
      version: typeof row['version'] === 'string' ? row['version'] : '',
      configCount: typeof row['configCount'] === 'number' ? row['configCount'] : 0,
      tools: parseExtensionTools(row['tools']),
    });
  }
  return out;
}

/** 扩展工具：name/label/description/risk/timeoutMs/parameters；格式不对的单条忽略。 */
function parseExtensionTools(value: unknown): ExtensionTool[] {
  if (!Array.isArray(value)) return [];
  const out: ExtensionTool[] = [];
  const seen = new Set<string>();
  for (const item of value) {
    if (typeof item !== 'object' || item === null) continue;
    const row = item as Record<string, unknown>;
    const name = typeof row['name'] === 'string' ? row['name'].trim() : '';
    if (!/^[a-z0-9_]{1,64}$/.test(name) || seen.has(name)) continue;
    const parameters = row['parameters'];
    if (typeof parameters !== 'object' || parameters === null || Array.isArray(parameters)) continue;
    seen.add(name);
    const risk = typeof row['risk'] === 'number' ? Math.min(2, Math.max(0, Math.trunc(row['risk']))) : 0;
    out.push({
      name,
      label: typeof row['label'] === 'string' ? row['label'] : name,
      description: typeof row['description'] === 'string' ? row['description'] : '',
      risk: risk as 0 | 1 | 2,
      timeoutMs: typeof row['timeoutMs'] === 'number' && row['timeoutMs'] > 0 ? row['timeoutMs'] : 8000,
      parameters: parameters as Record<string, unknown>,
    });
  }
  return out;
}

export interface PromptPayload {
  text: string;
  conversationId: string | null;
  /** M4l：本轮必须调用的工具名；null = 不强制。 */
  requiredTool: string | null;
  /** A.15：缺省/queue = 普通提问；steer = 插话到当前回合。 */
  mode: 'queue' | 'steer';
}

/**
 * 解析 prompt 负载（协议 §4.4）。
 * `text` 缺失按空串处理；存在但不是 string ⇒ 返回 null（bad_request）。
 * `requiredTool` 存在但不是 string ⇒ 返回 null；空串按未指定处理。
 * `mode` 只认 `"queue"` / `"steer"`；缺省按 `"queue"`。
 */
export function parsePromptPayload(payload: JsonObject): PromptPayload | null {
  const textValue = payload['text'];
  if (textValue !== undefined && textValue !== null && typeof textValue !== 'string') return null;

  const requiredToolValue = payload['requiredTool'];
  if (
    requiredToolValue !== undefined &&
    requiredToolValue !== null &&
    typeof requiredToolValue !== 'string'
  ) {
    return null;
  }

  const modeValue = payload['mode'];
  if (modeValue !== undefined && modeValue !== null && modeValue !== 'queue' && modeValue !== 'steer') {
    return null;
  }

  const conversationId = payload['conversationId'];
  const requiredTool =
    typeof requiredToolValue === 'string' && requiredToolValue.trim().length > 0
      ? requiredToolValue.trim()
      : null;

  return {
    text: typeof textValue === 'string' ? textValue : '',
    conversationId:
      typeof conversationId === 'string' && conversationId.length > 0 ? conversationId : null,
    requiredTool,
    mode: modeValue === 'steer' ? 'steer' : 'queue',
  };
}

// ---------------------------------------------------------------- 存档绑定（附录 A.6）

export interface BlueprintTiers {
  /** 0 = Mk.I / 1 = Mk.II / 2 = Mk.III */
  assembler?: number;
  /** 0 = 黄带 / 1 = 绿带 / 2 = 蓝带 */
  belt?: number;
  /** 0..3（分拣器 / 高速 / 极速 / 集装） */
  inserter?: number;
  piler?: boolean;
  stacking?: boolean;
}

export interface SaveBindPayload {
  saveKey: string | null;
  saveName: string | null;
  /** 可选：玩家已解锁的蓝图档位；缺失时 sidecar 用默认档。 */
  blueprintTiers?: BlueprintTiers | null;
  /** 可选：本次载入是否启用了游戏沙盒工具；缺失/非法按 false（安全默认）。 */
  sandbox: boolean;
}

function clampTier(value: unknown, min: number, max: number): number | undefined {
  if (typeof value !== 'number' || !Number.isFinite(value)) return undefined;
  return Math.max(min, Math.min(max, Math.floor(value)));
}

/** 解析可选的 `blueprintTiers`；非法/空对象返回 null，不阻断 save.bind。 */
export function parseBlueprintTiers(value: unknown): BlueprintTiers | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null;
  const row = value as Record<string, unknown>;
  const tiers: BlueprintTiers = {};
  const assembler = clampTier(row['assembler'], 0, 2);
  const belt = clampTier(row['belt'], 0, 2);
  const inserter = clampTier(row['inserter'], 0, 3);
  if (assembler !== undefined) tiers.assembler = assembler;
  if (belt !== undefined) tiers.belt = belt;
  if (inserter !== undefined) tiers.inserter = inserter;
  if (typeof row['piler'] === 'boolean') tiers.piler = row['piler'];
  if (typeof row['stacking'] === 'boolean') tiers.stacking = row['stacking'];
  return Object.keys(tiers).length > 0 ? tiers : null;
}

/**
 * 解析 `save.bind`。`saveKey` 必须存在，但允许为 null（回到主菜单 = 解绑）。
 * 缺失字段 / 类型不符 ⇒ null（bad_request）。
 */
export function parseSaveBindPayload(payload: JsonObject): SaveBindPayload | null {
  const key = payload['saveKey'];
  if (key === undefined) return null;
  if (key !== null && typeof key !== 'string') return null;

  const name = payload['saveName'];
  if (name !== undefined && name !== null && typeof name !== 'string') return null;

  return {
    saveKey: key === null ? null : key,
    saveName: typeof name === 'string' && name.length > 0 ? name : null,
    blueprintTiers: parseBlueprintTiers(payload['blueprintTiers']),
    // 旧 mod 不发该字段；非法类型也按 false 处理，绝不能因为门禁字段把存档解绑掉。
    // 解绑（saveKey=null）时一律 false；字段只描述当前载入会话。
    sandbox: key !== null && payload['sandbox'] === true,
  };
}

/** 历史回答里的一条思考步骤（协议附录 A.6 的 `steps`）。 */
export interface HistoryThinkingStep {
  kind: 'thinking';
  text: string;
}

/**
 * 历史回答里的一段过程正文（协议附录 A.6 的 `steps`）。
 *
 * 模型在两次思考/工具调用之间说的话，按发生时刻与思考、工具步骤交错排列；
 * 它不是最终回答（最终回答是 `HistoryMessage.text`）。
 */
export interface HistoryTextStep {
  kind: 'text';
  text: string;
}

/** 历史回答里的一次工具调用（协议附录 A.6 的 `steps`）。 */
export interface HistoryToolStep {
  kind: 'tool';
  name: string;
  /** 工具参数（JSON 文本，已截断）。 */
  args?: string;
  /** 是否成功；缺省 = 会话里没有结果记录（回答被中断）。 */
  ok?: boolean;
  /** 结果摘要（单行、已截断）。 */
  summary?: string;
  /** 展开后的完整结果（已截断）。 */
  detail?: string;
}

export type HistoryStep = HistoryThinkingStep | HistoryTextStep | HistoryToolStep;

/** `save.bound.history` 的一条可回放消息（M2.7，UI 历史恢复用）。 */
export interface HistoryMessage {
  role: 'user' | 'assistant';
  text: string;
  timestamp?: number;
  /**
   * 该回答之前产生的思考/工具步骤，按发生顺序（附录 A.6，M4k+）。
   * 只在 assistant 消息上出现；老客户端忽略该字段。
   */
  steps?: HistoryStep[];
  /**
   * 该回答产出的蓝图卡片（附录 A.9）；只在 assistant 消息上出现。
   * 老客户端忽略该字段，行为不变。
   */
  cards?: BlueprintCardPayload[];
  /** 该回答产出的实体链接卡片（建筑/星球）；可选。 */
  linkCards?: LinkCardPayload[];
  /** 该回答完成时的游戏时间 HH:mm:ss（附录 A.6/A.10）。 */
  gameTime?: string;
  /**
   * `compaction` = 压缩记录（附录 A.11）。UI 用它插入“自动压缩上下文”记录；
   * 只用于回放，不进入模型上下文。老客户端会把它当普通 assistant 消息显示。
   */
  kind?: 'compaction';
  /** 压缩记录对应的 pi compaction entry id（撤销时用；可选）。 */
  compactionId?: string;
  /** 压缩前/后的估算 token（只用于展示）。 */
  tokensBefore?: number;
  estimatedTokensAfter?: number;
}

/** 实体链接卡片：只描述目标，图标由 mod 按 ID 解析。 */
export interface LinkCardPayload {
  kind: 'planet' | 'building' | 'vein';
  planetId: number;
  buildingId?: number;
  veinId?: number;
  name: string;
  subtitle?: string;
  description?: string;
  iconLabel?: string;
}

export function buildLinkCardPayload(args: LinkCardPayload): JsonObject {
  const payload: JsonObject = {
    kind: args.kind,
    planetId: args.planetId,
    name: args.name,
  };
  if (args.buildingId !== undefined) payload.buildingId = args.buildingId;
  if (args.veinId !== undefined) payload.veinId = args.veinId;
  for (const key of ['subtitle', 'description', 'iconLabel'] as const) {
    const value = args[key];
    if (value !== undefined && value !== '') payload[key] = value;
  }
  return payload;
}

/** `reply.time` 载荷（附录 A.10）：mod 回存回答完成时的游戏时间。 */
export interface ReplyTimePayload {
  /** 可选：把时间记录归属到指定会话；缺省时由调用方用当前活动会话兜底。 */
  conversationId?: string;
  prompt: string;
  reply?: string;
  gameTime: string;
}

export function parseReplyTimePayload(payload: JsonObject): ReplyTimePayload | null {
  const prompt = payload['prompt'];
  const reply = payload['reply'];
  const gameTime = payload['gameTime'];
  const conversationId = payload['conversationId'];
  if (typeof prompt !== 'string' || prompt.trim().length === 0) return null;
  if (reply !== undefined && reply !== null && typeof reply !== 'string') return null;
  if (typeof gameTime !== 'string') return null;
  if (
    conversationId !== undefined &&
    conversationId !== null &&
    typeof conversationId !== 'string'
  ) {
    return null;
  }
  const time = gameTime.trim();
  if (time.length === 0 || time.length > 16) return null;
  const trimmedReply = typeof reply === 'string' ? reply.trim() : '';
  return {
    ...(typeof conversationId === 'string' && conversationId.trim()
      ? { conversationId: conversationId.trim() }
      : {}),
    prompt: prompt.trim(),
    ...(trimmedReply ? { reply: trimmedReply } : {}),
    gameTime: time,
  };
}

export function buildSaveBoundPayload(args: {
  saveKey: string | null;
  saveName: string | null;
  existed: boolean;
  messageCount: number;
  history?: readonly HistoryMessage[];
  /** A.13：当前活动会话 id；老 sidecar 没有该字段。 */
  activeConversationId?: string | null;
  /** A.13：该存档的会话列表；老 sidecar 没有该字段。 */
  conversations?: readonly ConversationInfo[];
}): JsonObject {
  return {
    saveKey: args.saveKey,
    saveName: args.saveName,
    existed: args.existed,
    messageCount: args.messageCount,
    // 只带最近若干条；mod 只用于回放，不进入模型上下文。
    history: args.history ?? [],
    ...(args.activeConversationId ? { activeConversationId: args.activeConversationId } : {}),
    ...(args.conversations ? { conversations: args.conversations } : {}),
  };
}

// ---------------------------------------------------------------- 多会话（附录 A.13）

export interface ConversationRecord {
  text: string;
  timestamp: number;
  gameTime?: string;
}

export interface ConversationInfo {
  id: string;
  title: string;
  createdAt: number;
  updatedAt: number;
  recordCount: number;
  records: ConversationRecord[];
  /** 是否置顶（缺省 false）；置顶会话排在列表最前面。 */
  pinned?: boolean;
}

export interface ConversationTargetPayload {
  conversationId: string;
  /** A.14.4：缺省 = 当前存档。 */
  saveKey?: string;
}

export interface ConversationRenamePayload {
  conversationId: string;
  title: string;
  /** A.14.4：缺省 = 当前存档。 */
  saveKey?: string;
}

/** A.14.4：可选 saveKey；空串/缺省都按“当前存档”处理。 */
function parseOptionalSaveKey(payload: JsonObject): { ok: boolean; value?: string } {
  const raw = payload['saveKey'];
  if (raw === undefined || raw === null) return { ok: true };
  if (typeof raw !== 'string') return { ok: false };
  const trimmed = raw.trim();
  return trimmed.length > 0 ? { ok: true, value: trimmed } : { ok: true };
}

/** 解析 conversation.rename：title 可以是空串，由 agent 统一校验。 */
export function parseConversationRenamePayload(payload: JsonObject): ConversationRenamePayload | null {
  const id = payload['conversationId'];
  const title = payload['title'];
  if (typeof id !== 'string' || id.trim().length === 0) return null;
  if (typeof title !== 'string') return null;
  const saveKey = parseOptionalSaveKey(payload);
  if (!saveKey.ok) return null;
  return { conversationId: id.trim(), title, ...(saveKey.value ? { saveKey: saveKey.value } : {}) };
}

export function buildConversationRenamePayload(args: {
  conversationId: string;
  title: string;
}): JsonObject {
  return { conversationId: args.conversationId, title: args.title };
}

export function buildConversationRenamedPayload(args: {
  saveKey: string | null;
  conversationId: string;
  title: string;
  ok: boolean;
  error?: string | null;
}): JsonObject {
  return {
    saveKey: args.saveKey,
    conversationId: args.conversationId,
    title: args.title,
    ok: args.ok,
    error: args.error ?? null,
  };
}

/** 解析 conversation.switch / conversation.delete 的目标。 */
export function parseConversationTargetPayload(payload: JsonObject): ConversationTargetPayload | null {
  const value = payload['conversationId'];
  if (typeof value !== 'string' || value.trim().length === 0) return null;
  const saveKey = parseOptionalSaveKey(payload);
  if (!saveKey.ok) return null;
  return { conversationId: value.trim(), ...(saveKey.value ? { saveKey: saveKey.value } : {}) };
}

export function buildConversationCreatedPayload(args: {
  saveKey: string | null;
  conversation: ConversationInfo;
  history?: readonly HistoryMessage[];
  running?: boolean;
  state?: StatusState;
}): JsonObject {
  return {
    saveKey: args.saveKey,
    conversation: args.conversation,
    running: args.running === true,
    state: args.state ?? 'idle',
    history: args.history ?? [],
  };
}

/** A.13：会话标题被模型总结/重命名后推送；mod 只更新左侧标题，不重放历史。 */
/** 会话状态图标：normal 由前端直接显示 chevron，不通过协议发送。 */
export type ConversationActivityState = 'running' | 'attention' | 'completed' | 'idle';

export function buildConversationActivityPayload(args: {
  conversationId: string;
  state: ConversationActivityState;
  reason?: string | null;
}): JsonObject {
  return {
    conversationId: args.conversationId,
    state: args.state,
    ...(args.reason ? { reason: args.reason } : {}),
  };
}

export function buildConversationUpdatedPayload(args: {
  conversationId: string;
  title: string;
  saveKey?: string | null;
}): JsonObject {
  return {
    ...(args.saveKey ? { saveKey: args.saveKey } : {}),
    conversationId: args.conversationId,
    title: args.title,
  };
}

export function buildConversationSwitchedPayload(args: {
  saveKey: string | null;
  conversation: ConversationInfo;
  existed: boolean;
  messageCount: number;
  history?: readonly HistoryMessage[];
  /** A.13：切换前那个没落盘的空会话被丢弃时，回传它，mod 据此删掉列表里的行。 */
  discardedConversationId?: string | null;
  running?: boolean;
  state?: StatusState;
}): JsonObject {
  return {
    saveKey: args.saveKey,
    conversationId: args.conversation.id,
    conversation: args.conversation,
    existed: args.existed,
    messageCount: args.messageCount,
    running: args.running === true,
    state: args.state ?? 'idle',
    ...(args.discardedConversationId ? { discardedConversationId: args.discardedConversationId } : {}),
    history: args.history ?? [],
  };
}

export function buildConversationDeletedPayload(args: {
  saveKey: string | null;
  conversationId: string;
  ok: boolean;
  error?: string | null;
  activeConversationId?: string | null;
  conversation?: ConversationInfo | null;
  messageCount?: number;
  history?: readonly HistoryMessage[];
  conversations?: readonly ConversationInfo[];
}): JsonObject {
  return {
    saveKey: args.saveKey,
    conversationId: args.conversationId,
    ok: args.ok,
    error: args.error ?? null,
    ...(args.activeConversationId ? { activeConversationId: args.activeConversationId } : {}),
    ...(args.conversation ? { conversation: args.conversation } : {}),
    ...(args.messageCount !== undefined ? { messageCount: args.messageCount } : {}),
    history: args.history ?? [],
    ...(args.conversations ? { conversations: args.conversations } : {}),
  };
}

/** `conversation.pin`：玩家点置顶按钮；pinned 是点完之后的目标状态。 */
export interface ConversationPinPayload {
  conversationId: string;
  pinned: boolean;
  /** A.14.4：缺省 = 当前存档。 */
  saveKey?: string;
}

export function parseConversationPinPayload(payload: JsonObject): ConversationPinPayload | null {
  const id = payload['conversationId'];
  const pinned = payload['pinned'];
  if (typeof id !== 'string' || id.trim().length === 0) return null;
  if (typeof pinned !== 'boolean') return null;
  const saveKey = parseOptionalSaveKey(payload);
  if (!saveKey.ok) return null;
  return {
    conversationId: id.trim(),
    pinned,
    ...(saveKey.value ? { saveKey: saveKey.value } : {}),
  };
}

export function buildConversationPinnedPayload(args: {
  saveKey: string | null;
  conversationId: string;
  pinned: boolean;
  ok: boolean;
  error?: string | null;
}): JsonObject {
  return {
    saveKey: args.saveKey,
    conversationId: args.conversationId,
    pinned: args.pinned,
    ok: args.ok,
    error: args.error ?? null,
  };
}

// ---------------------------------------------------------------- 设定页（附录 A.14）

/** `tools.set`：整份替换被关闭的工具名。 */
export interface ToolsSetPayload {
  disabled: string[];
}

export function parseToolsSetPayload(payload: JsonObject): ToolsSetPayload | null {
  const disabled = normalizeDisabledTools(payload['disabled']);
  return disabled === null ? null : { disabled };
}

export function buildToolsSetPayload(disabled: readonly string[]): JsonObject {
  return { disabled: [...disabled] };
}

/** `tools.state`：开关回执 + 完整工具目录（含 enabled=false 的项）。 */
export function buildToolsStatePayload(args: {
  ok: boolean;
  disabled: readonly string[];
  rebuilt: boolean;
  toolCatalog: readonly ToolCatalogEntry[];
  error?: string | null;
}): JsonObject {
  return {
    ok: args.ok,
    disabled: [...args.disabled],
    rebuilt: args.rebuilt,
    toolCatalog: args.toolCatalog.map((entry) => ({
      name: entry.name,
      label: entry.label,
      description: entry.description,
      ...(entry.group === undefined ? {} : { group: entry.group }),
      ...(entry.risk === undefined ? {} : { risk: entry.risk }),
      ...(entry.source === undefined ? {} : { source: entry.source }),
      ...(entry.sourceName === undefined ? {} : { sourceName: entry.sourceName }),
      ...(entry.enabled === undefined ? {} : { enabled: entry.enabled }),
    })),
    error: args.error ?? null,
  };
}

/** `conversations.index` 的一条会话。 */
export interface ConversationIndexEntry {
  id: string;
  title: string;
  updatedAt: number;
  messageCount: number;
  isActive: boolean;
}

/** `conversations.index` 的一个存档分组。 */
export interface ConversationSaveGroup {
  saveKey: string;
  saveName: string;
  lastSeen: number;
  current: boolean;
  conversations: ConversationIndexEntry[];
}

export function buildConversationsIndexedPayload(args: {
  currentSaveKey: string | null;
  saves: readonly ConversationSaveGroup[];
}): JsonObject {
  return {
    currentSaveKey: args.currentSaveKey,
    saves: args.saves.map((group) => ({
      saveKey: group.saveKey,
      saveName: group.saveName,
      lastSeen: group.lastSeen,
      current: group.current,
      conversations: group.conversations.map((entry) => ({
        id: entry.id,
        title: entry.title,
        updatedAt: entry.updatedAt,
        messageCount: entry.messageCount,
        isActive: entry.isActive,
      })),
    })),
  };
}

/** `conversations.export`：目前只支持 Markdown。 */
export interface ConversationsExportRequest {
  /** null = 当前存档。 */
  saveKey: string | null;
  conversationId: string;
  format: 'md';
}

export function parseConversationsExportPayload(
  payload: JsonObject,
): ConversationsExportRequest | null {
  const id = payload['conversationId'];
  if (typeof id !== 'string' || id.trim().length === 0) return null;
  const rawFormat = payload['format'];
  if (rawFormat !== undefined && rawFormat !== null && rawFormat !== 'md') return null;
  const saveKey = parseOptionalSaveKey(payload);
  if (!saveKey.ok) return null;
  return {
    saveKey: saveKey.value ?? null,
    conversationId: id.trim(),
    format: 'md',
  };
}

export function buildConversationsExportRequest(args: {
  saveKey?: string | null;
  conversationId: string;
  format?: 'md';
}): JsonObject {
  return {
    conversationId: args.conversationId,
    format: args.format ?? 'md',
    ...(args.saveKey ? { saveKey: args.saveKey } : {}),
  };
}

export function buildConversationsExportedPayload(args: {
  ok: boolean;
  path?: string | null;
  bytes?: number;
  messageCount?: number;
  error?: string | null;
}): JsonObject {
  return {
    ok: args.ok,
    ...(args.path ? { path: args.path } : {}),
    ...(typeof args.bytes === 'number' ? { bytes: args.bytes } : {}),
    ...(typeof args.messageCount === 'number' ? { messageCount: args.messageCount } : {}),
    error: args.error ?? null,
  };
}

// ---------------------------------------------------------------- 删除回合（附录 A.7）

export interface HistoryDeletePayload {
  saveKey: string | null;
  /** 可选：要改写的会话；缺省时由调用方用当前活动会话/旧版最新文件兜底。 */
  conversationId?: string;
  text: string;
  timestamp?: number;
}

/** 解析 `history.delete`：`text` 必填；`saveKey` 可为 null（由当前绑定兜底）。 */
export function parseHistoryDeletePayload(payload: JsonObject): HistoryDeletePayload | null {
  const text = payload['text'];
  if (typeof text !== 'string' || text.trim().length === 0) return null;

  const key = payload['saveKey'];
  if (key !== undefined && key !== null && typeof key !== 'string') return null;

  const conversationId = payload['conversationId'];
  if (
    conversationId !== undefined &&
    conversationId !== null &&
    typeof conversationId !== 'string'
  ) {
    return null;
  }

  const rawTs = payload['timestamp'];
  if (rawTs !== undefined && rawTs !== null && typeof rawTs !== 'number') return null;

  return {
    saveKey: typeof key === 'string' && key.length > 0 ? key : null,
    ...(typeof conversationId === 'string' && conversationId.trim()
      ? { conversationId: conversationId.trim() }
      : {}),
    text,
    ...(typeof rawTs === 'number' && Number.isFinite(rawTs) ? { timestamp: rawTs } : {}),
  };
}

export function buildHistoryDeletedPayload(args: {
  saveKey: string | null;
  text: string;
  ok: boolean;
  removed: number;
  error?: string | null;
}): JsonObject {
  return {
    saveKey: args.saveKey,
    text: args.text,
    ok: args.ok,
    removed: args.removed,
    error: args.error ?? null,
  };
}

// ---------------------------------------------------------------- 上下文压缩（附录 A.11）

export interface ContextCompactedPayload {
  /** 压缩摘要（给玩家看；同时用于回放）。 */
  summary: string;
  /** pi compaction entry id；事件刚发生时可能拿不到，撤销时可省略按“最近一次”处理。 */
  compactionId?: string;
  /** 毫秒时间戳（压缩完成时）。 */
  timestamp: number;
  /** 压缩前/后的估算 token。 */
  tokensBefore?: number;
  estimatedTokensAfter?: number;
  reason?: 'manual' | 'threshold' | 'overflow';
}

export function buildContextCompactedPayload(args: ContextCompactedPayload): JsonObject {
  const payload: JsonObject = {
    summary: args.summary,
    timestamp: args.timestamp,
  };
  if (args.compactionId) payload['compactionId'] = args.compactionId;
  if (typeof args.tokensBefore === 'number') payload['tokensBefore'] = args.tokensBefore;
  if (typeof args.estimatedTokensAfter === 'number') {
    payload['estimatedTokensAfter'] = args.estimatedTokensAfter;
  }
  if (args.reason) payload['reason'] = args.reason;
  return payload;
}

export interface HistoryUndoCompactionPayload {
  saveKey: string | null;
  /** 可选：要改写的会话；缺省时由调用方用当前活动会话/旧版最新文件兜底。 */
  conversationId?: string;
  /** 省略 = 撤销最近一次压缩。 */
  compactionId?: string;
}

export function parseHistoryUndoCompactionPayload(
  payload: JsonObject,
): HistoryUndoCompactionPayload | null {
  const key = payload['saveKey'];
  if (key !== undefined && key !== null && typeof key !== 'string') return null;
  const conversationId = payload['conversationId'];
  if (
    conversationId !== undefined &&
    conversationId !== null &&
    typeof conversationId !== 'string'
  ) {
    return null;
  }
  const id = payload['compactionId'];
  if (id !== undefined && id !== null && typeof id !== 'string') return null;
  return {
    saveKey: typeof key === 'string' && key.length > 0 ? key : null,
    ...(typeof conversationId === 'string' && conversationId.trim()
      ? { conversationId: conversationId.trim() }
      : {}),
    ...(typeof id === 'string' && id.length > 0 ? { compactionId: id } : {}),
  };
}

export function buildHistoryCompactionUndonePayload(args: {
  saveKey: string | null;
  compactionId: string | null;
  ok: boolean;
  removed: number;
  error?: string | null;
  /** 撤销后如果上下文仍超预算，下一次提问可能立刻重新压缩。 */
  mayRecompact?: boolean;
}): JsonObject {
  return {
    saveKey: args.saveKey,
    compactionId: args.compactionId,
    ok: args.ok,
    removed: args.removed,
    error: args.error ?? null,
    mayRecompact: args.mayRecompact === true,
  };
}

// ---------------------------------------------------------------- 本地工具活动（附录 A.8）

export interface ToolActivityPayload {
  callId: string;
  phase: 'start' | 'end';
  name: string;
  args?: string;
  ok?: boolean;
  summary?: string;
  detail?: string;
}

export function buildToolActivityPayload(args: ToolActivityPayload): JsonObject {
  const payload: JsonObject = {
    callId: args.callId,
    phase: args.phase,
    name: args.name,
  };
  if (args.args !== undefined) payload['args'] = args.args;
  if (args.ok !== undefined) payload['ok'] = args.ok;
  if (args.summary !== undefined) payload['summary'] = args.summary;
  if (args.detail !== undefined) payload['detail'] = args.detail;
  return payload;
}

// ---------------------------------------------------------------- 蓝图卡片（附录 A.9）

export type BlueprintCardSource = 'generated' | 'downloaded' | 'copied' | 'written';

/** 蓝图卡片：所有字段都由工具结果推导，缺失就不发。 */
export interface BlueprintCardPayload {
  fileName: string;
  /** 相对蓝图根的路径（POSIX），例如 `IcarusII/a.txt`。 */
  relPath?: string;
  path?: string;
  name?: string;
  item?: string;
  itemId?: number;
  rate?: number;
  buildings?: number;
  width?: number;
  height?: number;
  author?: string;
  version?: string;
  attributes?: string;
  description?: string;
  source?: BlueprintCardSource;
  license?: string;
  repository?: string;
}

export function buildBlueprintCardPayload(args: BlueprintCardPayload): JsonObject {
  const payload: JsonObject = { fileName: args.fileName };
  const optional: (keyof BlueprintCardPayload)[] = [
    'path',
    'relPath',
    'name',
    'item',
    'itemId',
    'rate',
    'buildings',
    'width',
    'height',
    'author',
    'version',
    'attributes',
    'description',
    'source',
    'license',
    'repository',
  ];
  for (const key of optional) {
    const value = args[key];
    if (value !== undefined && value !== null && value !== '') payload[key] = value;
  }
  return payload;
}

// ---------------------------------------------------------------- 配置（附录 A）

/**
 * 思考档位，取值与 pi 的 `ThinkingLevel` 完全一致。
 *
 * DeepSeek 的 `thinkingLevelMap`：Flash 映射 `low` / `high` / `max`；
 * Pro 只映射 `high` / `max`（`low` 会被 pi clamp）。`off` 表示完全不请求思考。
 * 聊天窗实际只暴露 `off | low | high | max` 四档，其余取值仅为兼容老配置保留。
 */
export const THINKING_LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] as const;
export type ThinkingLevelName = (typeof THINKING_LEVELS)[number];
export const DEFAULT_THINKING_LEVEL: ThinkingLevelName = 'high';

export function isThinkingLevel(value: unknown): value is ThinkingLevelName {
  return typeof value === 'string' && (THINKING_LEVELS as readonly string[]).includes(value);
}

/**
 * 权限档位：AI 调用写工具时是否需要玩家同意。
 *
 * - `readonly`：只读。所有写操作（risk 1/2）都要先请求玩家确认。
 * - `limited`：受限（默认）。risk 1（可逆、无存档副作用）直接放行；
 *   risk 2（改变存档状态）仍需确认。
 * - `takeover`：接管。所有写操作直接放行，不再请求确认。
 *
 * 三种档位都保留 confirm_request + 一次性 token：需要自动放行时由 mod 回传 allow，
 * 不是绕过 token。权限只能由玩家在聊天窗/配置文件里改，模型无法通过工具修改。
 */
export const PERMISSION_LEVELS = ['readonly', 'limited', 'takeover'] as const;
export type PermissionLevel = (typeof PERMISSION_LEVELS)[number];
export const DEFAULT_PERMISSION_LEVEL: PermissionLevel = 'limited';

export function isPermissionLevel(value: unknown): value is PermissionLevel {
  return typeof value === 'string' && (PERMISSION_LEVELS as readonly string[]).includes(value);
}

/** `config.state.payload.state` / `ready.payload.config.state` 的取值（附录 A.1）。 */
export type ConfigStateValue = 'ready' | 'missing_key' | 'invalid_key' | 'unreachable';

/**
 * `config.state` 与 `ready.payload.config` 共用的视图。
 * **任何字段都不得包含 apiKey**（附录 A.4）。
 */
export interface ModelEntry {
  id: string;
  name: string;
  /**
   * 该模型能接受的输入模态。缺省时用 pi 目录里的取值。
   *
   * 目录数据可能滞后（例如 DeepSeek 已支持图片但目录仍写 `text`），
   * 因此允许在这里覆盖；写 `['text','image']` 后 pi 才会把工具结果里的
   * 图片转成真正的图像输入。
   */
  input?: ModelInputModality[];
}

export type ModelInputModality = 'text' | 'image';

/** 一个供应商的只读视图；`hasKey` 只表示是否配置过，绝不回传 key。 */
export interface ProviderView {
  id: string;
  name: string;
  hasKey: boolean;
  baseUrl: string | null;
  models: ModelEntry[];
  isActive: boolean;
}

export interface ConfigView {
  state: ConfigStateValue;
  provider: string;
  model: string;
  /** 思考档位；只影响下一次 prompt。 */
  thinkingLevel: ThinkingLevelName;
  /** 权限档位；决定写工具是否需要玩家确认。 */
  permissionLevel: PermissionLevel;
  message: string | null;
  /** 多供应商配置；老服务端不返回时 mod 回退到 provider/model 单条。 */
  providers: ProviderView[];
  /** 当前选中的 provider/model（与 provider/model 冗余，便于新客户端读取）。 */
  active: { provider: string; model: string };
  /** A.14.5：被玩家关闭的工具名；缺省 = 全部启用。 */
  disabledTools?: string[];
}

/** 对单个供应商的增量更新；未给出的字段保持不变，`apiKey: ""` 表示清除。 */
export interface ProviderPatch {
  apiKey?: string;
  baseUrl?: string | null;
  /** 整份替换该供应商的模型列表。 */
  models?: ModelEntry[];
}

/**
 * `config.set` 的增量更新（附录 A.1）。
 * 只包含**给出**的字段；未给出的保持不变。
 * `baseUrl: null` 显式表示「清除覆盖」。
 *
 * 多供应商有两种写法，可同时出现：
 *   * 老字段（provider/model/apiKey/baseUrl）→ 更新**当前**供应商；
 *   * `providerConfig` / `removeProviders` / `active*` → 批量维护供应商列表。
 */
export interface ConfigUpdatePayload {
  provider?: string;
  model?: string;
  apiKey?: string;
  baseUrl?: string | null;
  /** 思考档位；必须是 {@link THINKING_LEVELS} 之一。 */
  thinkingLevel?: ThinkingLevelName;
  /** 权限档位；必须是 {@link PERMISSION_LEVELS} 之一。 */
  permissionLevel?: PermissionLevel;
  /** 键 = provider id，值 = 该供应商的增量。 */
  providerConfig?: Record<string, ProviderPatch>;
  /** 要删除的 provider id。 */
  removeProviders?: string[];
  activeProvider?: string;
  activeModel?: string;
  /** A.14.5：整份替换被关闭的工具名；`[]` = 全部启用。 */
  disabledTools?: string[];
}

function parseModelEntries(value: unknown): ModelEntry[] | null {
  if (!Array.isArray(value)) return null;
  const models: ModelEntry[] = [];
  for (const item of value) {
    if (!isPlainObject(item)) return null;
    const id = item['id'];
    if (typeof id !== 'string' || id.length === 0) return null;
    const name = item['name'];
    if (name !== undefined && name !== null && typeof name !== 'string') return null;
    models.push({ id, name: typeof name === 'string' && name.length > 0 ? name : id });
  }
  return models;
}

function parseProviderConfig(value: unknown): Record<string, ProviderPatch> | null {
  if (!isPlainObject(value)) return null;
  const out: Record<string, ProviderPatch> = {};
  for (const [id, raw] of Object.entries(value)) {
    if (id.length === 0) return null;
    if (!isPlainObject(raw)) return null;
    const patch: ProviderPatch = {};

    const apiKey = raw['apiKey'];
    if (apiKey !== undefined && apiKey !== null) {
      if (typeof apiKey !== 'string') return null;
      patch.apiKey = apiKey;
    }

    const baseUrl = raw['baseUrl'];
    if (baseUrl !== undefined) {
      if (baseUrl === null) patch.baseUrl = null;
      else if (typeof baseUrl === 'string') patch.baseUrl = baseUrl;
      else return null;
    }

    const models = raw['models'];
    if (models !== undefined && models !== null) {
      const parsed = parseModelEntries(models);
      if (parsed === null) return null;
      patch.models = parsed;
    }

    out[id] = patch;
  }
  return out;
}

/**
 * 解析 `config.set` 负载（附录 A.1）。
 *
 * - 字段缺失 ⇒ 不改动。
 * - `baseUrl: null` ⇒ 清除 baseUrl 覆盖；其它字段 `null` 视为「未给出」。
 * - 字段存在但类型不对 ⇒ 返回 null（bad_request）。
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

  // 思考档位：只接受 pi 认识的取值，避免把任意字符串透传给 pi。
  const thinkingLevel = payload['thinkingLevel'];
  if (thinkingLevel !== undefined && thinkingLevel !== null) {
    if (!isThinkingLevel(thinkingLevel)) return null;
    out.thinkingLevel = thinkingLevel;
  }

  // 权限档位：只接受三个固定值，模型无法借此提权。
  const permissionLevel = payload['permissionLevel'];
  if (permissionLevel !== undefined && permissionLevel !== null) {
    if (!isPermissionLevel(permissionLevel)) return null;
    out.permissionLevel = permissionLevel;
  }

  // ---- 多供应商（M3）----
  const providerConfig = payload['providerConfig'];
  if (providerConfig !== undefined && providerConfig !== null) {
    const parsed = parseProviderConfig(providerConfig);
    if (parsed === null) return null;
    out.providerConfig = parsed;
  }

  const removeProviders = payload['removeProviders'];
  if (removeProviders !== undefined && removeProviders !== null) {
    if (!Array.isArray(removeProviders)) return null;
    const ids: string[] = [];
    for (const item of removeProviders) {
      if (typeof item !== 'string' || item.length === 0) return null;
      ids.push(item);
    }
    out.removeProviders = ids;
  }

  const activeProvider = payload['activeProvider'];
  if (activeProvider !== undefined && activeProvider !== null) {
    if (typeof activeProvider !== 'string' || activeProvider.length === 0) return null;
    out.activeProvider = activeProvider;
  }

  const activeModel = payload['activeModel'];
  if (activeModel !== undefined && activeModel !== null) {
    if (typeof activeModel !== 'string' || activeModel.length === 0) return null;
    out.activeModel = activeModel;
  }

  // ---- A.14.5：工具开关 ----
  const disabledTools = payload['disabledTools'];
  if (disabledTools !== undefined && disabledTools !== null) {
    const parsed = normalizeDisabledTools(disabledTools);
    if (parsed === null) return null;
    out.disabledTools = parsed;
  }

  return out;
}

/** 工具名列表的归一化：去空、去重、限长。非法输入返回 null。 */
export function normalizeDisabledTools(value: unknown): string[] | null {
  if (!Array.isArray(value)) return null;
  const out: string[] = [];
  const seen = new Set<string>();
  for (const item of value) {
    if (typeof item !== 'string') return null;
    const name = item.trim();
    if (name.length === 0) continue;
    if (name.length > 128) return null;
    if (seen.has(name)) continue;
    seen.add(name);
    out.push(name);
    if (out.length > 512) return null;
  }
  return out;
}

export interface ModelCatalogEntry {
  id: string;
  name: string;
}

export interface ModelCatalogRequest {
  provider: string;
  baseUrl: string | null;
  apiKey: string | null;
}

export interface ModelCatalogResult {
  provider: string;
  models: ModelCatalogEntry[];
  source: 'catalog' | 'openai-compatible' | 'none';
  error: string | null;
  /** 端点探测成功时，适配器期望的聊天 base（可选；mod 可用它补全地址）。 */
  resolvedBaseUrl?: string | null;
}

/** `model.catalog` 请求：provider 必填；baseUrl/apiKey 可选（草稿值优先）。 */
export function parseModelCatalogPayload(payload: JsonObject): ModelCatalogRequest | null {
  const provider = payload['provider'];
  if (typeof provider !== 'string' || provider.length === 0) return null;

  let baseUrl: string | null = null;
  const rawBase = payload['baseUrl'];
  if (rawBase !== undefined && rawBase !== null) {
    if (typeof rawBase !== 'string') return null;
    const trimmed = rawBase.trim();
    baseUrl = trimmed.length > 0 ? trimmed : null;
  }

  let apiKey: string | null = null;
  const rawKey = payload['apiKey'];
  if (rawKey !== undefined && rawKey !== null) {
    if (typeof rawKey !== 'string') return null;
    apiKey = rawKey.length > 0 ? rawKey : null;
  }

  return { provider, baseUrl, apiKey };
}

/** `provider.catalog` 里的一条供应商（只含展示所需的 id/name）。 */
export interface ProviderCatalogEntry {
  id: string;
  name: string;
}

/** `provider.catalog` 响应：pi 内置目录的全部供应商，顺序即目录顺序。 */
export interface ProviderCatalogResult {
  providers: ProviderCatalogEntry[];
  source: 'catalog' | 'none';
  error: string | null;
}

export interface ProviderTestRequest {
  provider: string;
  baseUrl: string | null;
  apiKey: string | null;
}

export interface ProviderTestResult {
  provider: string;
  ok: boolean;
  status: number;
  latencyMs: number;
  message: string;
  source: 'http' | 'none';
  /** 同 `model.catalog`：成功时给出聊天可用的 base（可选）。 */
  resolvedBaseUrl?: string | null;
}

/** `provider.test` 请求：provider 必填；baseUrl/apiKey 可选（草稿值优先）。 */
export function parseProviderTestPayload(payload: JsonObject): ProviderTestRequest | null {
  const provider = payload['provider'];
  if (typeof provider !== 'string' || provider.length === 0) return null;

  let baseUrl: string | null = null;
  const rawBase = payload['baseUrl'];
  if (rawBase !== undefined && rawBase !== null) {
    if (typeof rawBase !== 'string') return null;
    const trimmed = rawBase.trim();
    baseUrl = trimmed.length > 0 ? trimmed : null;
  }

  let apiKey: string | null = null;
  const rawKey = payload['apiKey'];
  if (rawKey !== undefined && rawKey !== null) {
    if (typeof rawKey !== 'string') return null;
    apiKey = rawKey.length > 0 ? rawKey : null;
  }

  return { provider, baseUrl, apiKey };
}

export function buildProviderTestPayload(result: ProviderTestResult): JsonObject {
  const payload: JsonObject = {
    provider: result.provider,
    ok: result.ok,
    status: result.status,
    latencyMs: result.latencyMs,
    message: result.message,
    source: result.source,
  };
  if (result.resolvedBaseUrl) payload['resolvedBaseUrl'] = result.resolvedBaseUrl;
  return payload;
}

export function buildModelCatalogPayload(result: ModelCatalogResult): JsonObject {
  const payload: JsonObject = {
    provider: result.provider,
    models: result.models.map((model) => ({ id: model.id, name: model.name })),
    source: result.source,
  };
  if (result.error) payload['error'] = result.error;
  if (result.resolvedBaseUrl) payload['resolvedBaseUrl'] = result.resolvedBaseUrl;
  return payload;
}

export function buildProviderCatalogPayload(result: ProviderCatalogResult): JsonObject {
  const payload: JsonObject = {
    providers: result.providers.map((provider) => ({ id: provider.id, name: provider.name })),
    source: result.source,
  };
  if (result.error) payload['error'] = result.error;
  return payload;
}

export interface AgentInfo {
  provider: string;
  model: string;
  available: boolean;
}

/** ready.payload.toolCatalog 的一条：工具名 + 显示名 + 简介（M4l）。 */
/** A.14.1：工具分组，设定页用来筛选。 */
export const TOOL_GROUPS = [
  'game.read',
  'game.write',
  'kb',
  'blueprint',
  'ask',
  'thirdparty',
  'other',
] as const;

export type ToolGroup = (typeof TOOL_GROUPS)[number];

export function isToolGroup(value: unknown): value is ToolGroup {
  return typeof value === 'string' && (TOOL_GROUPS as readonly string[]).includes(value);
}

export interface ToolCatalogEntry {
  name: string;
  label: string;
  description: string;
  /** A.14.1：分组；老服务端不返回时 mod 按名字推断。 */
  group?: ToolGroup;
  /** A.14.1：0 = 只读，1 = 可逆写，2 = 改变存档。 */
  risk?: 0 | 1 | 2;
  /** A.14.1：`builtin` 或第三方插件 id。 */
  source?: string;
  /** A.14.1：来源显示名（内置 = 内置）。 */
  sourceName?: string;
  /** A.14.1：false = 被玩家关掉，不注册给模型。 */
  enabled?: boolean;
}

// ---------------------------------------------------------------- 负载构造

export function buildReadyPayload(args: {
  serverVersion: string;
  sessionId: string;
  agent: AgentInfo;
  tools: readonly string[];
  /** M4l：可选工具目录，供聊天窗的工具列表展示；老 mod 忽略。 */
  toolCatalog?: readonly ToolCatalogEntry[];
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
  if (args.toolCatalog && args.toolCatalog.length > 0) {
    payload['toolCatalog'] = args.toolCatalog.map((entry) => ({
      name: entry.name,
      label: entry.label,
      description: entry.description,
      ...(entry.group === undefined ? {} : { group: entry.group }),
      ...(entry.risk === undefined ? {} : { risk: entry.risk }),
      ...(entry.source === undefined ? {} : { source: entry.source }),
      ...(entry.sourceName === undefined ? {} : { sourceName: entry.sourceName }),
      ...(entry.enabled === undefined ? {} : { enabled: entry.enabled }),
    }));
  }
  if (args.config) payload['config'] = buildConfigStatePayload(args.config);
  return payload;
}

/** `config.state` / `ready.payload.config` 的载荷（附录 A.1 / A.2）。绝不含 apiKey。 */
export function buildConfigStatePayload(view: ConfigView): JsonObject {
  // 老测试夹具/老 agent 可能只给 provider/model：缺 providers 时合成一条，
  // 避免 ready 构造直接抛异常（真实 ConfigStore 永远会给全）。
  const providers = Array.isArray(view.providers) ? view.providers : [];
  const active = view.active ?? { provider: view.provider, model: view.model };
  return {
    state: view.state,
    provider: view.provider,
    model: view.model,
    thinkingLevel: view.thinkingLevel,
    // 老测试夹具没有该字段时按默认「受限」处理。
    permissionLevel: view.permissionLevel ?? DEFAULT_PERMISSION_LEVEL,
    message: view.message,
    providers: providers.map((provider) => ({
      id: provider.id,
      name: provider.name,
      hasKey: provider.hasKey,
      baseUrl: provider.baseUrl,
      models: (provider.models ?? []).map((model) => ({ id: model.id, name: model.name })),
      isActive: provider.isActive,
    })),
    active: { provider: active.provider, model: active.model },
    // A.14.5：老测试夹具/老 agent 缺该字段时按「全部启用」处理。
    disabledTools: Array.isArray(view.disabledTools) ? [...view.disabledTools] : [],
  };
}

export function buildStatusPayload(state: StatusState, detail?: string): JsonObject {
  const payload: JsonObject = { state };
  if (detail) payload['detail'] = detail;
  return payload;
}

export function buildToolCallPayload(args: ToolCallPayload): JsonObject {
  const payload: JsonObject = {
    callId: args.callId,
    name: args.name,
    args: args.args,
    risk: args.risk,
    timeoutMs: args.timeoutMs,
  };
  if (args.internal === true) payload['internal'] = true;
  if (args.confirmToken) payload['confirmToken'] = args.confirmToken;
  if (args.confirmArgsHash) payload['confirmArgsHash'] = args.confirmArgsHash;
  if (args.conversationId) payload['conversationId'] = args.conversationId;
  return payload;
}

/**
 * 解析 `tool_result`。`ok` 缺失或不是 boolean ⇒ null（协议违例）。
 * 其余字段宽容：缺 `error` / `meta` 时给出安全默认值。
 */
export function parseToolResultPayload(payload: JsonObject): ToolResultPayload | null {
  const ok = payload['ok'];
  if (typeof ok !== 'boolean') return null;

  const data = payload['data'] === undefined ? null : payload['data'];

  let error: ToolErrorPayload | null = null;
  const rawError = payload['error'];
  if (rawError !== undefined && rawError !== null) {
    if (!isPlainObject(rawError)) return null;
    const code = typeof rawError['code'] === 'string' ? rawError['code'] : 'tool_failed';
    error = {
      code,
      message: typeof rawError['message'] === 'string' ? rawError['message'] : '',
      retryable:
        typeof rawError['retryable'] === 'boolean' ? rawError['retryable'] : false,
    };
  }

  const rawMeta = payload['meta'];
  const meta: JsonObject = isPlainObject(rawMeta) ? rawMeta : {};

  // 附件只在成功时保留：失败结果带图会让模型对"成功了什么"产生错觉。
  const image = ok ? parseToolImage(payload['image']) : null;
  if (image === INVALID_IMAGE) return null;

  return image ? { ok, data, error, meta, image } : { ok, data, error, meta };
}

const INVALID_IMAGE = Symbol('invalid-image');

/**
 * 解析可选的 `image` 附件。
 *
 * - 缺省/null ⇒ 没有图片；
 * - 形状或大小不合法 ⇒ INVALID_IMAGE（协议违例，由调用方判 null）；
 * - 只在 ok=true 时才保留（失败结果不该带图）。
 */
function parseToolImage(raw: unknown): ToolImagePayload | null | typeof INVALID_IMAGE {
  if (raw === undefined || raw === null) return null;
  if (!isPlainObject(raw)) return INVALID_IMAGE;

  const mimeType = raw['mimeType'];
  const data = raw['data'];
  const width = raw['width'];
  const height = raw['height'];
  if (typeof mimeType !== 'string' || mimeType.length === 0) return INVALID_IMAGE;
  if (typeof data !== 'string' || data.length === 0) return INVALID_IMAGE;
  if (typeof width !== 'number' || !Number.isFinite(width) || width <= 0) return INVALID_IMAGE;
  if (typeof height !== 'number' || !Number.isFinite(height) || height <= 0) return INVALID_IMAGE;

  // base64 的字符数上限：4/3 × 字节数（向上取整），再加 padding 余量。
  const maxChars = Math.ceil((MAX_TOOL_IMAGE_BYTES * 4) / 3) + 4;
  if (data.length > maxChars) return INVALID_IMAGE;
  if (!BASE64_PATTERN.test(data)) return INVALID_IMAGE;

  return {
    mimeType,
    data,
    width: Math.round(width),
    height: Math.round(height),
  };
}

const BASE64_PATTERN = /^[A-Za-z0-9+/]+={0,2}$/;

export function buildStreamPayload(channel: StreamChannel, delta: string): JsonObject {
  return { channel, delta };
}

export function buildMessagePayload(
  content: readonly ContentBlock[],
  options: { role?: 'assistant'; final?: boolean; interrupted?: boolean } = {},
): JsonObject {
  return {
    role: options.role ?? 'assistant',
    final: options.final ?? true,
    ...(options.interrupted ? { interrupted: true } : {}),
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

/** 是否是回环地址（非回环时 server.ts 直接拒绝监听）。 */
export function isLoopbackHost(host: string): boolean {
  const value = host.trim().toLowerCase();
  return (
    value === '127.0.0.1' ||
    value === '::1' ||
    value === 'localhost' ||
    value.startsWith('127.')
  );
}
