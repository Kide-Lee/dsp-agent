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

import {
  appendFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import type {
  AgentSession,
  AgentSessionEvent,
  CreateAgentSessionOptions,
  ExtensionFactory,
  ModelRuntime,
  ResourceLoader,
  ToolDefinition,
} from '@earendil-works/pi-coding-agent';
import type { Api, Model } from '@earendil-works/pi-ai';
import { Value } from 'typebox/value';
import {
  defaultModelInput,
  type ConfigStore,
  maskSecret,
  providerDisplayName,
  redactSecrets,
} from './config.js';
import {
  ICARUS_II_PERSONA_NAME,
  ICARUS_II_RICH_TEXT_PROMPT,
  ICARUS_II_SYSTEM_PROMPT,
} from './persona.js';

/** mod 在 hello.capabilities 里声明：输出支持 Markdown + 小型 HTML 子集。 */
const CAPABILITY_RICH_TEXT_V2 = 'ui.richtext.v2';
import { errorFields, type Logger, type LogFields, log as rootLog } from './log.js';
import {
  canonicalBaseForChat,
  describeProbeFailure,
  parseModelList,
  probeModelEndpoint,
  safeEndpoint,
} from './provider-endpoint.js';
import {
  DEFAULT_PERMISSION_LEVEL,
  type AgentInfo,
  type ConfigView,
  type ErrorCode,
  type HistoryMessage,
  type HistoryStep,
  type HistoryToolStep,
  type JsonObject,
  type ModelCatalogEntry,
  type ModelCatalogResult,
  type ProviderCatalogEntry,
  type ProviderCatalogResult,
  type ProviderTestResult,
  type BlueprintTiers,
  type StatusState,
  type StreamChannel,
  type ToolCatalogEntry,
  type ToolDebugResultPayload,
  type ToolResultPayload,
} from './protocol.js';
import type {
  GameToolHost,
  LocalWriteConfirmRequest,
  ScriptConfirmRequest,
  ScriptConfirmResult,
} from './tools/host.js';
import { createKbTools } from './kb/tools.js';
import type { BlueprintCardPayload, ContextCompactedPayload, LinkCardPayload, ToolActivityPayload } from './protocol.js';
import {
  parseSessionLines,
  removeCompactionFromEntries,
  removeTurnFromEntries,
  serializeSessionEntries,
} from './history.js';
import { createScriptTool } from './tools/script.js';
import { CAPABILITY_UI_ASK, createAskTool } from './tools/ask.js';
import { CAPABILITY_GAME_SANDBOX } from './tools/sandbox.js';
import { isDebugToolAllowed } from './tools/debug.js';
import { createWikiTools } from './wiki/tools.js';
import { WikiClient } from './wiki/client.js';
import { AliasResolver } from './wiki/aliases.js';
import { createBlueprintTools } from './blueprint/tools.js';
import { BlueprintWorkspace } from './blueprint/workspace.js';
import {
  attachBlueprintCards,
  blueprintCardFileExists,
  compactBlueprintCard,
  dropDeletedBlueprintCards,
  extractBlueprintCard,
  type BlueprintCardRecord,
} from './blueprint/card.js';
import {
  attachLinkCards,
  compactLinkCard,
  extractLinkCard,
  isStoredLinkCard,
  type LinkCardRecord,
} from './link/card.js';
import { attachGameTimes, type GameTimeRecord } from './history/game-time.js';
import type { KnowledgeStore } from './kb/store.js';

/** 会话目录里的元数据日志；它们不是 pi 会话文件，不能被当成会话读取/删除。 */
const SESSION_METADATA_FILES = new Set(['blueprint-cards.jsonl', 'link-cards.jsonl', 'reply-times.jsonl']);

/** 判断一个文件名是否是可读的 pi 会话 JSONL。 */
function isSessionFile(name: string): boolean {
  return name.endsWith('.jsonl') && !SESSION_METADATA_FILES.has(name);
}

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
  /** run_script 包含写操作时的脚本级确认；null = 玩家拒绝。 */
  confirmScript?(request: ScriptConfirmRequest): Promise<ScriptConfirmResult | null>;
  /** 本地蓝图写操作确认；false = 玩家拒绝。 */
  confirmLocalWrite?(request: LocalWriteConfirmRequest): Promise<boolean>;
  /** 脚本结束/失败后释放写租约。 */
  releaseScript?(token: string): void;
  /** 本地工具（kb_* / run_script）的 UI 事件；协议附录 A.8。 */
  toolActivity?(payload: ToolActivityPayload): void;
  /** 蓝图卡片；协议附录 A.9。由 agent 在本轮收尾时统一发出。 */
  blueprintCard?(payload: BlueprintCardPayload): void;
  /** 实体链接卡片；本轮收尾时统一发出。 */
  linkCard?(payload: LinkCardPayload): void;
  /** 上下文压缩完成；协议附录 A.11。只给 UI，不进入模型上下文。 */
  compacted?(payload: ContextCompactedPayload): void;
}

export interface AgentRunner {
  /** 对外报告的 agent 身份（`ready.payload.agent`）。 */
  readonly info: AgentInfo;
  /** 处理一次 prompt；同一 runner 上的调用由上层串行化。 */
  run(
    text: string,
    promptId: string | null,
    emit: AgentEmitter,
    requiredTool?: string | null,
  ): Promise<void>;
  /**
   * 本次连接实际可用的工具目录（M4l，供 ready.payload.toolCatalog / 聊天窗工具列表）。
   * 老实现可以不提供。
   */
  toolCatalog?(capabilities: readonly string[]): ToolCatalogEntry[];
  /**
   * M4m：调试直调。不创建/使用 pi 会话，不调用 LLM；只执行一个只读工具。
   * 老实现可以不提供。
   */
  debugTool?(name: string, args: JsonObject, emit: AgentEmitter): Promise<ToolDebugResultPayload>;
  /** `config.set` 落地后调用：让下一次 run 使用新配置。 */
  onConfigChanged(): void;
  /** 握手完成后同步 mod 能力；能力变化时 agent 应重建会话。 */
  onClientCapabilities?(capabilities: readonly string[]): void;
  /** 切换存档；null = 回到主菜单。agent 应在回合边界重建会话。 */
  onSaveChanged?(saveKey: string | null): void;
  /** 当前载入是否启用游戏沙盒工具；变化时 agent 应在回合边界重建会话。 */
  onSandboxChanged?(enabled: boolean): void;
  /** save.bind 带来的玩家已解锁蓝图档位；null = 未知/解绑。 */
  onBlueprintTiers?(tiers: BlueprintTiers | null): void;
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
   * 撤销最近一次上下文压缩（附录 A.11）：删掉 compaction entry，旧消息重新进入
   * 模型上下文；下一次提问如果仍超预算，可能立刻再次压缩。
   */
  undoCompaction?(
    saveKey: string | null,
    compactionId?: string,
  ): Promise<{
    ok: boolean;
    removed: number;
    compactionId?: string;
    error?: string;
    mayRecompact?: boolean;
  }>;
  /** 回存一轮回答完成时的游戏时间（附录 A.10）；写盘失败不影响回答。 */
  recordReplyTime?(saveKey: string | null, prompt: string, reply: string, gameTime: string): void;
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
   * 列出 pi 内置目录的全部供应商，供设置页预设下拉框使用。
   * 只读本地目录：不联网、不需要 Key；失败时返回 `error`，不抛异常。
   */
  listProviders?(): Promise<ProviderCatalogResult>;
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
 * - user / assistant 的可见文本：最多 `maxMessages` 条、单条 `maxText` 字符；
 * - assistant 顺带带上它之前的思考块和工具调用（`steps`，附录 A.6）：
 *   一次提问里会有多个 assistant 步骤（思考+工具、再思考+工具…，最后一段带正文），
 *   中间那些没有正文的步骤先攒着，等最终回答落地时按发生顺序一起挂上去；
 * - 工具结果按 toolCallId 回填到对应的 `steps[]`；没有结果的（回答被中断）不填 ok；
 * - `compaction` 投影成一条 `kind:"compaction"` 的 assistant 记录（摘要只给 UI）；
 * - 分支摘要仍不回放。
 *
 * 数据全部来自会话记录，不再额外落盘；旧会话（没有工具记录字段）也能读。
 */
export function extractHistoryMessages(
  entries: readonly unknown[],
  maxMessages = 40,
  maxText = 4000,
): HistoryMessage[] {
  const out: HistoryMessage[] = [];
  // 当前提问里还没落地的思考/工具步骤；遇到下一条 user 消息时丢弃（那一轮没有正文）。
  let pending: HistoryStep[] = [];
  const toolSteps = new Map<string, HistoryToolStep>();

  for (const raw of entries) {
    if (!raw || typeof raw !== 'object') continue;
    const entry = raw as {
      type?: unknown;
      id?: unknown;
      timestamp?: unknown;
      summary?: unknown;
      tokensBefore?: unknown;
      estimatedTokensAfter?: unknown;
      message?: {
        role?: unknown;
        content?: unknown;
        timestamp?: unknown;
        toolCallId?: unknown;
        isError?: unknown;
      };
    };

    if (entry.type === 'compaction') {
      const summary = typeof entry.summary === 'string' ? entry.summary.trim() : '';
      if (summary) {
        const compaction: HistoryMessage = {
          role: 'assistant',
          kind: 'compaction',
          text: clipHistoryText(summary, maxText),
          ...timestampOf(entry),
        };
        if (typeof entry.id === 'string' && entry.id) compaction.compactionId = entry.id;
        if (typeof entry.tokensBefore === 'number') compaction.tokensBefore = entry.tokensBefore;
        if (typeof entry.estimatedTokensAfter === 'number') {
          compaction.estimatedTokensAfter = entry.estimatedTokensAfter;
        }
        out.push(compaction);
      }
      // 压缩边界之前的中间步骤不属于下一条回答。
      pending = [];
      toolSteps.clear();
      continue;
    }

    if (entry.type !== 'message' || !entry.message) continue;
    const message = entry.message;
    const role = message.role;

    if (role === 'user') {
      pending = [];
      toolSteps.clear();
      const text = contentText(message.content).trim();
      if (!text) continue;
      out.push({ role: 'user', text: clipHistoryText(text, maxText), ...timestampOf(entry) });
      continue;
    }

    if (role === 'assistant') {
      const blocks = Array.isArray(message.content) ? message.content : [];
      const textParts: string[] = [];
      for (const block of blocks) {
        if (!block || typeof block !== 'object') continue;
        const item = block as {
          type?: unknown;
          text?: unknown;
          thinking?: unknown;
          id?: unknown;
          name?: unknown;
          arguments?: unknown;
        };
        if (item.type === 'text' && typeof item.text === 'string') {
          textParts.push(item.text);
        } else if (item.type === 'thinking' && typeof item.thinking === 'string') {
          const thinking = item.thinking.trim();
          if (thinking) {
            pending.push({ kind: 'thinking', text: clipHistoryText(thinking, HISTORY_THINKING_MAX) });
          }
        } else if (item.type === 'toolCall') {
          const step: HistoryToolStep = {
            kind: 'tool',
            name: typeof item.name === 'string' && item.name.trim() ? item.name.trim() : 'tool',
          };
          const args = toolArgsText(item.arguments);
          if (args) step.args = args;
          pending.push(step);
          if (typeof item.id === 'string' && item.id) toolSteps.set(item.id, step);
        }
      }

      const finalText = textParts.join('\n').trim();
      if (!finalText) continue; // 中间步骤：等最后一段带正文的回答一起回放

      const history: HistoryMessage = {
        role: 'assistant',
        text: clipHistoryText(finalText, maxText),
        ...timestampOf(entry),
      };
      if (pending.length > 0) {
        history.steps = pending;
        pending = [];
      }
      out.push(history);
      // toolSteps 保留到下一条 user 消息：结果可能在回答之后才写进会话。
      continue;
    }

    if (role === 'toolResult') {
      const callId = typeof message.toolCallId === 'string' ? message.toolCallId : '';
      const step = callId ? toolSteps.get(callId) : undefined;
      if (!step) continue;
      toolSteps.delete(callId);
      step.ok = message.isError !== true;
      const result = contentText(message.content).trim();
      if (result) {
        step.summary = clipHistoryText(result.replace(/\s+/g, ' ').trim(), HISTORY_TOOL_SUMMARY_MAX);
        step.detail = clipHistoryText(result, HISTORY_TOOL_DETAIL_MAX);
      }
    }
  }

  const recent = out.slice(-maxMessages);
  trimHistorySteps(recent);
  return recent;
}

/** 单条回答最多回放的步骤数（正常一轮 1~30 个，超出的只保留最近的）。 */
const HISTORY_MAX_STEPS = 60;
const HISTORY_THINKING_MAX = 4000;
const HISTORY_TOOL_ARGS_MAX = 600;
const HISTORY_TOOL_SUMMARY_MAX = 160;
const HISTORY_TOOL_DETAIL_MAX = 2000;
/** 所有历史消息 steps 的总字符预算，避免一帧超过协议 8 MiB 上限。 */
const HISTORY_STEPS_BUDGET = 2 * 1024 * 1024;

function clipHistoryText(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

function toolArgsText(args: unknown): string | undefined {
  if (args === undefined || args === null) return undefined;
  let text: string;
  try {
    text = JSON.stringify(args);
  } catch {
    return undefined;
  }
  if (!text || text === '{}' || text === 'null') return undefined;
  return clipHistoryText(text, HISTORY_TOOL_ARGS_MAX);
}

function timestampOf(entry: {
  timestamp?: unknown;
  message?: { timestamp?: unknown };
}): { timestamp?: number } {
  const rawTs =
    typeof entry.message?.timestamp === 'number'
      ? entry.message.timestamp
      : typeof entry.timestamp === 'string'
        ? Date.parse(entry.timestamp)
        : NaN;
  return Number.isFinite(rawTs) ? { timestamp: rawTs } : {};
}

/** 步骤太多/太大时从**最旧**的回答开始丢，保住最近几轮。 */
function trimHistorySteps(messages: HistoryMessage[]): void {
  for (const message of messages) {
    if (message.steps && message.steps.length > HISTORY_MAX_STEPS) {
      message.steps = message.steps.slice(-HISTORY_MAX_STEPS);
    }
  }
  let total = 0;
  for (const message of messages) total += stepsChars(message);
  for (let i = 0; i < messages.length && total > HISTORY_STEPS_BUDGET; i++) {
    const message = messages[i]!;
    if (!message.steps || message.steps.length === 0) continue;
    total -= stepsChars(message);
    delete message.steps;
  }
}

function stepsChars(message: HistoryMessage): number {
  if (!message.steps) return 0;
  let total = 0;
  for (const step of message.steps) {
    if (step.kind === 'thinking') total += step.text.length;
    else total += (step.args?.length ?? 0) + (step.detail?.length ?? 0) + (step.summary?.length ?? 0);
  }
  return total;
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

  async listProviders(): Promise<ProviderCatalogResult> {
    return {
      providers: [
        { id: 'echo', name: 'Echo' },
        { id: 'deepseek', name: 'DeepSeek' },
      ],
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

  async run(
    text: string,
    promptId: string | null,
    emit: AgentEmitter,
    _requiredTool?: string | null,
  ): Promise<void> {
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
  /** 本轮开始时的存档；中途换档时卡片仍记在发起回合的存档里。 */
  saveKey: string | null;
  /** 本轮用户提问原文；用于把卡片记录挂回历史回合。 */
  userText: string;
  text: string;
  thinking: string;
  error: string | null;
  stopReason: string | null;
  /**
   * 本轮成功产出的蓝图卡片。
   *
   * 不在 tool_end 立刻发：模型通常还会再写一段总结，卡片要排在**整轮最末尾**。
   * 因此先入队，等 final message（或失败 error）之后再 flush。
   */
  cards: BlueprintCardPayload[];
  /** 本轮成功建立的实体链接卡片。 */
  linkCards: LinkCardPayload[];

  /**
   * M4l：玩家用 /<工具名> 指定本轮必须调用的工具。
   * 未指定为 null；指定时 before_agent_start 会把要求写进本轮 system prompt。
   */
  requiredTool: string | null;
  /** 本轮是否已经看到 requiredTool 的 tool_call；看到后不再追加要求。 */
  requiredToolSatisfied: boolean;
  /** 已尝试的轮数（首次 + 一次纠正）；用于防止无限重试。 */
  requiredToolAttempts: number;
  /** 进入强制模式前的 active tools；满足要求后恢复，避免影响后续工具调用。 */
  savedTools: string[] | null;
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

// ---------------------------------------------------------------- 强制工具（M4l）

/**
 * 从用户原文里解析开头的 `/<工具名>`。
 *
 * 只在名字确实在当前工具集里时返回——否则 `/foo` 应当按普通文本发送，
 * 不能因为拼错一个工具就把整条消息判成“工具不存在”。
 * 显式 `requiredTool` 优先；这个函数只用于兼容老 mod / 直接发 prompt 的测试。
 */
export function extractSlashTool(text: string, available: ReadonlySet<string>): string | null {
  const match = /^\s*\/([A-Za-z0-9_-]+)(?=\s|$)/.exec(text ?? '');
  if (!match) return null;
  const name = match[1] ?? '';
  return available.has(name) ? name : null;
}

/** 本轮强制工具的 system prompt 片段。 */
export function forcedToolInstruction(tool: string): string {
  return `【本轮强制工具】玩家在输入里用 /${tool} 明确要求必须调用工具 ${tool}。
- 这是硬性要求：本轮至少调用一次 ${tool}，不要跳过它直接回答。
- **本轮工具列表是唯一权威**：即使历史对话说过“没有这个工具/做不到”，也以本轮提供的函数为准。
- 不要因为工具名字陌生、或历史回答里否定过它，就拒绝调用；先直接发起调用。
- 如果 ${tool} 返回错误，如实说明错误，不要编造结果。
- 调用 ${tool} 之后，可以继续调用其他工具或直接回答。`;
}

interface AgentMessageLike {
  role?: unknown;
  content?: unknown;
}

/** 从 `startIndex` 之后的消息里找一次指定工具的 toolCall。 */
export function hasToolCallSince(
  messages: readonly AgentMessageLike[],
  startIndex: number,
  toolName: string,
): boolean {
  for (let i = Math.max(0, startIndex); i < messages.length; i += 1) {
    const message = messages[i];
    if (!message || message.role !== 'assistant' || !Array.isArray(message.content)) continue;
    for (const block of message.content) {
      if (
        block !== null &&
        typeof block === 'object' &&
        (block as { type?: unknown }).type === 'toolCall' &&
        (block as { name?: unknown }).name === toolName
      ) {
        return true;
      }
    }
  }
  return false;
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
 *
 * M4l：`getExtraPrompt` 在强制工具回合返回额外要求，拼在人格后面；
 * 它按**当前 run** 实时读取，因此一次会话里有的回合强制、有的回合不强制。
 */
function createPersonaSystemPromptExtension(
  getExtraPrompt: () => string | null,
  getUseRichText: () => boolean,
): ExtensionFactory {
  return (api) => {
    api.on('before_agent_start', () => {
      const extra = getExtraPrompt();
      const persona = getUseRichText() ? ICARUS_II_RICH_TEXT_PROMPT : ICARUS_II_SYSTEM_PROMPT;
      return {
        systemPrompt: extra ? `${persona}\n\n${extra}` : persona,
      };
    });
  };
}

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
  private readonly systemPrompt: string;

  constructor(base: ResourceLoader, systemPrompt: string) {
    this.base = base;
    this.systemPrompt = systemPrompt;
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
    return this.systemPrompt;
  }

  getAppendSystemPrompt(): string[] {
    return [];
  }

  /**
   * pi 1.4 的 ResourceLoader 新增来源查询；我们的系统提示全部来自内联人格，
   * 没有文件来源，返回空即可（返回假路径会让导出/诊断误导）。
   */
  getSystemPromptSource(): { path: string } | undefined {
    return undefined;
  }

  getAppendSystemPromptSources(): Array<{ path: string }> {
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
  getExtraPrompt: () => string | null,
  getUseRichText: () => boolean,
): ResourceLoader {
  const getPrompt = () =>
    getUseRichText() ? ICARUS_II_RICH_TEXT_PROMPT : ICARUS_II_SYSTEM_PROMPT;
  const base = new pi.DefaultResourceLoader({
    cwd: options.cwd,
    agentDir: options.agentDir,
    noExtensions: true,
    noSkills: true,
    noPromptTemplates: true,
    noThemes: true,
    noContextFiles: true,
    // 替换（不是追加）pi 的 system prompt；override 是双保险。
    systemPrompt: getPrompt(),
    systemPromptOverride: getPrompt,
    appendSystemPromptOverride: () => [],
    agentsFilesOverride: () => ({ agentsFiles: [] }),
    extensionFactories: [createPersonaSystemPromptExtension(getExtraPrompt, getUseRichText)],
  });
  return new PersonaResourceLoader(base, getPrompt());
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
  const result = await probeModelEndpoint({ provider, baseUrl, apiKey, api });
  return {
    provider,
    ok: result.ok,
    status: result.status,
    latencyMs: result.latencyMs,
    message: result.ok ? 'Key 可用' : describeProbeFailure(result),
    source: result.attempts > 0 ? 'http' : 'none',
    resolvedBaseUrl: result.workingBaseUrl || null,
  };
}

/**
 * 调端点的 `GET /models` 拿模型列表。
 *
 * 与 Key 检测共用同一套地址清洗/鉴权/回退逻辑，避免出现「检测通过但拿不到模型」
 * 或反过来的情况；解析同时覆盖 OpenAI/Anthropic 的 `data[].id` 与 Gemini 的
 * `models[].name`（Gemini 的 `models/` 前缀会被去掉）。
 */
async function fetchEndpointModels(
  provider: string,
  baseUrl: string,
  apiKey: string | null,
  api: string | null,
): Promise<{
  models: ModelCatalogEntry[];
  source: 'openai-compatible' | 'none';
  error: string | null;
  endpoint: string;
  resolvedBaseUrl: string | null;
}> {
  const result = await probeModelEndpoint({ provider, baseUrl, apiKey, api });
  if (!result.ok) {
    return {
      models: [],
      source: 'none',
      error: describeProbeFailure(result),
      endpoint: result.endpoint,
      resolvedBaseUrl: null,
    };
  }

  const models = parseModelList(result.body);
  if (models.length === 0) {
    return {
      models: [],
      source: 'none',
      error: '端点没有返回模型列表',
      endpoint: result.endpoint,
      resolvedBaseUrl: result.workingBaseUrl || null,
    };
  }
  return {
    models,
    source: 'openai-compatible',
    error: null,
    endpoint: result.endpoint,
    resolvedBaseUrl: result.workingBaseUrl || null,
  };
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
// ---------------------------------------------------------------- 调试直调

const DEBUG_TIMEOUT_MS = 30_000;
const DEBUG_CONTENT_MAX = 64 * 1024;
const DEBUG_DETAILS_MAX = 256 * 1024;

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function debugJson(value: unknown): string {
  try {
    const text = JSON.stringify(value);
    return text === undefined ? 'null' : text;
  } catch {
    return JSON.stringify(String(value));
  }
}

/** base64 解码后的大致字节数；只用于 debug UI 显示，不参与校验。 */
function base64Bytes(data: string): number {
  if (!data) return 0;
  const padding = data.endsWith('==') ? 2 : data.endsWith('=') ? 1 : 0;
  return Math.max(0, Math.floor((data.length * 3) / 4) - padding);
}

export class PiAgent implements AgentRunner {
  private readonly dataDir: string;
  private readonly agentDir: string;
  private readonly store: ConfigStore;
  private readonly host: GameToolHost;
  private readonly kb: KnowledgeStore | null;
  private readonly wiki: WikiClient;
  private readonly aliases: AliasResolver;
  private readonly blueprintWorkspace: BlueprintWorkspace;
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
  /** 当前载入是否启用游戏沙盒工具（由 save.bind 上报）。 */
  private sandboxEnabled = false;
  /** mod 在 save.bind 里被动上报的蓝图档位；模型不需要调用查询工具。 */
  private blueprintTiers: BlueprintTiers | null = null;
  /** 进程内全局串行：多连接共用同一个 pi 会话。 */
  private chain: Promise<unknown> = Promise.resolve();

  onBlueprintTiers(tiers: BlueprintTiers | null): void {
    this.blueprintTiers = tiers;
  }

  constructor(options: PiAgentOptions) {
    this.dataDir = options.dataDir;
    this.agentDir = join(options.dataDir, 'agent');
    this.store = options.store;
    this.host = options.host;
    this.kb = options.kb ?? null;
    this.wiki = new WikiClient();
    this.aliases = new AliasResolver({
      wiki: this.wiki,
      cacheFile: join(this.dataDir, 'wiki-aliases.json'),
    });
    this.blueprintWorkspace = new BlueprintWorkspace();
    this.logger = (options.logger ?? rootLog).child({ component: 'pi-agent' });
    this.loadSdk =
      options.loadSdk ?? (() => import('@earendil-works/pi-coding-agent') as Promise<PiSdk>);
  }

  get info(): AgentInfo {
    const data = this.store.get();
    return { provider: data.provider, model: data.model, available: this.store.hasKey() };
  }

  onConfigChanged(): void {
    const data = this.store.get();

    // 正在跑回合时不要拆会话：否则流式输出会被拦腰截断。
    // 记下来，等这一轮结束后再重建。
    if (this.active) {
      this.pendingTeardown = true;
      this.logger.info('配置变化：当前回合结束后重建 pi 会话', {
        apiKey: this.store.maskedApiKey,
        thinkingLevel: data.thinkingLevel,
        permissionLevel: data.permissionLevel,
      });
      return;
    }
    this.logger.info('配置变化，丢弃 pi 会话，下一次 prompt 重新建立', {
      apiKey: this.store.maskedApiKey,
      thinkingLevel: data.thinkingLevel,
      permissionLevel: data.permissionLevel,
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

  onSandboxChanged(enabled: boolean): void {
    this.sandboxEnabled = enabled;
    const changed = this.host.setSandbox(enabled);
    if (!changed) return;

    this.logger.info('沙盒状态变化，工具集将按新状态重建', { sandbox: enabled });
    if (this.active) {
      this.pendingTeardown = true;
      return;
    }
    this.teardown();
  }

  onSaveChanged(saveKey: string | null): void {
    this.blueprintTiers = null;
    this.blueprintWorkspace.resetCwd();
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
    if (!readdirSync(sessionDir).some((name) => isSessionFile(name))) return [];

    const pi = await this.loadSdk();
    const manager = pi.SessionManager.continueRecent(this.dataDir, sessionDir);
    // 聊天记录回放必须用全量 branch：buildContextEntries() 会按 compaction 丢掉
    // 被摘要的旧消息，导致压缩后重进存档旧对话凭空消失。模型上下文由 pi 自己管，
    // 这里只负责 UI 回放。
    const messages = extractHistoryMessages(manager.getBranch());
    const records = this.readBlueprintCards(saveKey);
    const withCards = attachBlueprintCards(messages, records);
    const linkRecords = this.readLinkCards(saveKey);
    const withLinks = attachLinkCards(withCards, linkRecords);
    const timeRecords = this.readReplyTimes(saveKey);
    const withTimes = attachGameTimes(withLinks, timeRecords);
    this.logger.info('读取存档历史', {
      saveKey,
      messages: withTimes.length,
      blueprintCards: records.length,
      linkCards: linkRecords.length,
      replyTimes: timeRecords.length,
    });
    return withTimes;
  }

  // ---------------------------------------------------------------- 蓝图卡片持久化

  private blueprintCardLogPath(saveKey: string | null): string | null {
    if (!saveKey) return null;
    return join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey), 'blueprint-cards.jsonl');
  }

  /**
   * 追加卡片记录。持久化是为了 `save.bound.history` 回放：
   * 会话文件只存文本，卡片必须单独记一份，按「用户提问原文」挂回对应回答。
   */
  private appendBlueprintCards(
    saveKey: string | null,
    prompt: string,
    cards: readonly BlueprintCardPayload[],
  ): void {
    const path = this.blueprintCardLogPath(saveKey);
    if (!path || cards.length === 0) return;
    const text = prompt.trim();
    if (!text) return;

    try {
      mkdirSync(dirname(path), { recursive: true });
      const now = Date.now();
      const lines = cards
        .map((card) =>
          JSON.stringify({
            prompt: text,
            timestamp: now,
            card: compactBlueprintCard(card),
          } satisfies BlueprintCardRecord),
        )
        .join('\n');
      appendFileSync(path, `${lines}\n`, { encoding: 'utf8', mode: 0o600 });
    } catch (err) {
      // 卡片已经发到 UI 了；落盘失败只影响下次历史回放，不打断本轮。
      this.logger.warn('写蓝图卡片记录失败', { saveKey, ...errorFields(err) });
    }
  }

  private readBlueprintCards(saveKey: string | null): BlueprintCardRecord[] {
    const path = this.blueprintCardLogPath(saveKey);
    if (!path || !existsSync(path)) return [];
    let raw: string;
    try {
      raw = readFileSync(path, 'utf8');
    } catch (err) {
      this.logger.warn('读蓝图卡片记录失败', { saveKey, ...errorFields(err) });
      return [];
    }

    const records: BlueprintCardRecord[] = [];
    for (const line of raw.split('\n')) {
      if (!line.trim()) continue;
      try {
        const parsed = JSON.parse(line) as Partial<BlueprintCardRecord>;
        if (typeof parsed.prompt !== 'string' || !parsed.card || typeof parsed.card.fileName !== 'string') {
          continue;
        }
        records.push({
          prompt: parsed.prompt,
          ...(typeof parsed.timestamp === 'number' ? { timestamp: parsed.timestamp } : {}),
          card: parsed.card,
        });
      } catch {
        // 坏行跳过，不能因为一条记录损坏就丢掉整段历史。
      }
    }
    return records;
  }

  // ---------------------------------------------------------------- 实体链接卡片持久化

  private linkCardLogPath(saveKey: string | null): string | null {
    if (!saveKey) return null;
    return join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey), 'link-cards.jsonl');
  }

  private appendLinkCards(
    saveKey: string | null,
    prompt: string,
    reply: string,
    cards: readonly LinkCardPayload[],
  ): void {
    const path = this.linkCardLogPath(saveKey);
    if (!path || cards.length === 0) return;
    const text = prompt.trim();
    if (!text) return;
    const answer = reply.trim().slice(0, 4000);
    try {
      mkdirSync(dirname(path), { recursive: true });
      const now = Date.now();
      const lines = cards
        .map((card) =>
          JSON.stringify({
            prompt: text,
            ...(answer ? { reply: answer } : {}),
            timestamp: now,
            card: compactLinkCard(card),
          } satisfies LinkCardRecord),
        )
        .join('\n');
      appendFileSync(path, `${lines}\n`, { encoding: 'utf8', mode: 0o600 });
    } catch (err) {
      this.logger.warn('写实体链接卡片记录失败', { saveKey, ...errorFields(err) });
    }
  }

  private readLinkCards(saveKey: string | null): LinkCardRecord[] {
    const path = this.linkCardLogPath(saveKey);
    if (!path || !existsSync(path)) return [];
    let raw: string;
    try {
      raw = readFileSync(path, 'utf8');
    } catch (err) {
      this.logger.warn('读实体链接卡片记录失败', { saveKey, ...errorFields(err) });
      return [];
    }
    const records: LinkCardRecord[] = [];
    for (const line of raw.split('\n')) {
      if (!line.trim()) continue;
      try {
        const parsed = JSON.parse(line) as Partial<LinkCardRecord>;
        if (typeof parsed.prompt !== 'string' || !isStoredLinkCard(parsed.card)) continue;
        const card = parsed.card;
        records.push({
          prompt: parsed.prompt,
          ...(typeof parsed.reply === 'string' && parsed.reply.trim()
            ? { reply: parsed.reply }
            : {}),
          ...(typeof parsed.timestamp === 'number' ? { timestamp: parsed.timestamp } : {}),
          card,
        });
      } catch {
        // 坏行跳过，不能因为一条记录损坏就丢掉整段历史。
      }
    }
    return records;
  }

  /** 删除某个提问对应的实体链接卡片记录（回合被删时调用）。 */
  private removeLinkCardRecords(saveKey: string, prompt: string): void {
    const path = this.linkCardLogPath(saveKey);
    const text = prompt.trim();
    if (!path || !text || !existsSync(path)) return;
    const records = this.readLinkCards(saveKey);
    const kept = records.filter((record) => record.prompt.trim() !== text);
    if (kept.length === records.length) return;
    const tmp = `${path}.tmp-${process.pid}`;
    try {
      const body = kept.map((record) => JSON.stringify(record)).join('\n');
      writeFileSync(tmp, kept.length > 0 ? `${body}\n` : '', { encoding: 'utf8', mode: 0o600 });
      renameSync(tmp, path);
    } catch (err) {
      try { unlinkSync(tmp); } catch { /* ignore */ }
      this.logger.warn('删除实体链接卡片记录失败', { saveKey, ...errorFields(err) });
    }
  }

  // ---------------------------------------------------------------- 回答游戏时间持久化

  private replyTimeLogPath(saveKey: string | null): string | null {
    if (!saveKey) return null;
    return join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey), 'reply-times.jsonl');
  }

  /** 按提问原文回存每轮回答完成时的游戏时间，供历史脚注回放。 */
  recordReplyTime(saveKey: string | null, prompt: string, reply: string, gameTime: string): void {
    const path = this.replyTimeLogPath(saveKey);
    const text = prompt.trim();
    const answer = (reply ?? '').trim();
    const time = gameTime.trim();
    if (!path || !text || !time) return;

    try {
      mkdirSync(dirname(path), { recursive: true });
      const record: GameTimeRecord = {
        prompt: text.slice(0, 4000),
        ...(answer ? { reply: answer.slice(0, 4000) } : {}),
        gameTime: time.slice(0, 16),
        timestamp: Date.now(),
      };
      appendFileSync(path, `${JSON.stringify(record)}\n`, { encoding: 'utf8', mode: 0o600 });
    } catch (err) {
      // 时间只是 UI 元数据；写盘失败只影响下次回放，不打断本轮。
      this.logger.warn('写回答游戏时间失败', { saveKey, ...errorFields(err) });
    }
  }

  private readReplyTimes(saveKey: string | null): GameTimeRecord[] {
    const path = this.replyTimeLogPath(saveKey);
    if (!path || !existsSync(path)) return [];
    let raw: string;
    try {
      raw = readFileSync(path, 'utf8');
    } catch (err) {
      this.logger.warn('读回答游戏时间失败', { saveKey, ...errorFields(err) });
      return [];
    }

    const records: GameTimeRecord[] = [];
    for (const line of raw.split('\n')) {
      if (!line.trim()) continue;
      try {
        const parsed = JSON.parse(line) as Partial<GameTimeRecord>;
        if (typeof parsed.prompt !== 'string' || typeof parsed.gameTime !== 'string') continue;
        if (!parsed.prompt.trim() || !parsed.gameTime.trim()) continue;
        records.push({
          prompt: parsed.prompt,
          gameTime: parsed.gameTime,
          ...(typeof parsed.timestamp === 'number' ? { timestamp: parsed.timestamp } : {}),
        });
      } catch {
        // 坏行跳过，不因一条记录损坏丢掉整段历史。
      }
    }
    return records;
  }

  private removeReplyTimeRecords(saveKey: string, prompt: string): void {
    const path = this.replyTimeLogPath(saveKey);
    const text = prompt.trim();
    if (!path || !text || !existsSync(path)) return;

    const records = this.readReplyTimes(saveKey);
    const kept = records.filter((record) => record.prompt.trim() !== text);
    if (kept.length === records.length) return;

    const tmp = `${path}.tmp-${process.pid}`;
    try {
      const body = kept.map((record) => JSON.stringify(record)).join('\n');
      writeFileSync(tmp, kept.length > 0 ? `${body}\n` : '', { encoding: 'utf8', mode: 0o600 });
      renameSync(tmp, path);
    } catch (err) {
      try {
        rmSync(tmp, { force: true });
      } catch {
        // ignore
      }
      this.logger.warn('删除回答游戏时间失败', { saveKey, ...errorFields(err) });
    }
  }

  /** 删除某个提问对应的卡片记录（回合被删时调用）。 */
  private removeBlueprintCardRecords(saveKey: string, prompt: string): void {
    const path = this.blueprintCardLogPath(saveKey);
    const text = prompt.trim();
    if (!path || !text || !existsSync(path)) return;

    const records = this.readBlueprintCards(saveKey);
    const kept = records.filter((record) => record.prompt.trim() !== text);
    if (kept.length === records.length) return;

    const tmp = `${path}.tmp-${process.pid}`;
    try {
      const body = kept.map((record) => JSON.stringify(record)).join('\n');
      writeFileSync(tmp, kept.length > 0 ? `${body}\n` : '', { encoding: 'utf8', mode: 0o600 });
      renameSync(tmp, path);
    } catch (err) {
      try {
        rmSync(tmp, { force: true });
      } catch {
        // ignore
      }
      this.logger.warn('删除蓝图卡片记录失败', { saveKey, ...errorFields(err) });
    }
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

    const files = readdirSync(dir).filter((name) => isSessionFile(name));
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

    // 卡片/时间记录跟着回合一起删；否则重进存档时旧记录可能挂到同一句提问的新回答上。
    this.removeBlueprintCardRecords(saveKey, text);
    this.removeLinkCardRecords(saveKey, text);
    this.removeReplyTimeRecords(saveKey, text);

    // 上下文必须重建，否则被删的回合还在内存会话里继续发给模型。
    if (this.saveKey === saveKey) this.teardown();
    this.logger.info('已删除对话回合', { saveKey, removed: outcome.removed, file: newest });
    return outcome;
  }

  /**
   * 撤销最近一次上下文压缩（附录 A.11）。
   *
   * 从会话文件里删掉 compaction entry，并把它的子条目接回原 parent；旧消息重新
   * 成为模型上下文。只允许撤销最后一次压缩（LIFO）。成功后下一次 prompt 会按
   * 原始历史重建会话；如果仍超预算，pi 可能立刻再次压缩。
   */
  async undoCompaction(
    saveKey: string | null,
    compactionId?: string,
  ): Promise<{
    ok: boolean;
    removed: number;
    compactionId?: string;
    error?: string;
    mayRecompact?: boolean;
  }> {
    if (!saveKey) return { ok: false, removed: 0, error: 'no_save' };
    if (this.active) return { ok: false, removed: 0, error: 'busy' };

    const dir = join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey));
    if (!existsSync(dir)) return { ok: false, removed: 0, error: 'not_found' };

    const files = readdirSync(dir).filter((name) => isSessionFile(name));
    if (files.length === 0) return { ok: false, removed: 0, error: 'not_found' };

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

    const { outcome, entries: next } = removeCompactionFromEntries(entries, compactionId);
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
      this.logger.warn('撤销压缩并写会话文件失败', errorFields(err));
      return { ok: false, removed: 0, error: 'io_error' };
    }

    if (this.saveKey === saveKey) this.teardown();
    this.logger.info('已撤销上下文压缩', {
      saveKey,
      compactionId: outcome.compactionId ?? null,
      file: newest,
    });
    return { ...outcome, mayRecompact: true };
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

  run(
    text: string,
    promptId: string | null,
    emit: AgentEmitter,
    requiredTool: string | null = null,
  ): Promise<void> {
    const task = async (): Promise<void> => {
      try {
        await this.runOnce(text, promptId, emit, requiredTool);
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

  private async runOnce(
    text: string,
    promptId: string | null,
    emit: AgentEmitter,
    requiredTool: string | null,
  ): Promise<void> {
    const data = this.store.get();

    if (!data.apiKey) {
      // 未配置：不建会话、不联网（协议附录 A.3 的 config_required）。
      const name = providerDisplayName(data.provider);
      emit.error('config_required', `尚未配置 API Key（${name}）`, promptId);
      return;
    }
    if (!emit.isActive()) return;

    const run: RunState = {
      emit,
      promptId,
      saveKey: this.saveKey,
      userText: text,
      text: '',
      thinking: '',
      error: null,
      stopReason: null,
      cards: [],
      linkCards: [],
      requiredTool,
      requiredToolSatisfied: false,
      requiredToolAttempts: 0,
      savedTools: null,
    };
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

    // 强制工具（M4l）：显式 requiredTool 优先；老 mod 只传 /<工具名> 时从原文兜底解析。
    const activeNames = session.getActiveToolNames();
    const available = new Set(activeNames);
    let forced = run.requiredTool;
    if (forced && !available.has(forced)) {
      const message = `工具 ${forced} 当前不可用`;
      this.logger.warn('requiredTool 不在当前工具集', { promptId, tool: forced });
      if (emit.isActive()) {
        emit.error('tool_unavailable', message, promptId);
        emit.status('idle');
      }
      this.active = null;
      return;
    }
    if (!forced) forced = extractSlashTool(text, available);
    run.requiredTool = forced;

    // 只把强制工具暴露给模型：这样它不可能“先调用别的工具再绕过要求”。
    // 工具一被调用就在 handleEvent 里恢复完整工具集，后续轮次不受影响。
    if (forced) {
      run.savedTools = activeNames;
      try {
        session.setActiveToolsByName([forced]);
      } catch (err) {
        this.logger.warn('限制强制工具集失败，退回普通模式', {
          promptId,
          tool: forced,
          ...errorFields(err),
        });
        run.requiredTool = null;
        run.savedTools = null;
      }
    }

    // 工具只发往发起本轮 prompt 的连接；prompt 结束立即解绑。
    this.host.bind(emit);
    try {
      if (!emit.isActive()) return;
      const start = session.messages.length;
      // 不解析斜杠命令/模板。
      await session.prompt(text, { expandPromptTemplates: false });

      if (
        run.requiredTool &&
        !run.requiredToolSatisfied &&
        hasToolCallSince(
          session.messages as readonly AgentMessageLike[],
          start,
          run.requiredTool,
        )
      ) {
        run.requiredToolSatisfied = true;
      }

      // 模型没按要求调用：明确纠正一次。第一次的文本没有流给 UI（见 handleEvent），
      // 因此这里重置累积文本不会留下半截回答。
      if (
        run.requiredTool &&
        !run.requiredToolSatisfied &&
        run.requiredToolAttempts < 1 &&
        run.stopReason !== 'error' &&
        run.stopReason !== 'aborted' &&
        emit.isActive()
      ) {
        run.requiredToolAttempts += 1;
        run.text = '';
        run.thinking = '';
        run.error = null;
        run.stopReason = null;
        emit.status('thinking', `必须调用 ${run.requiredTool}`);
        const retryStart = session.messages.length;
        await session.prompt(
          `请现在调用工具 ${run.requiredTool}，然后再回答。`,
          { expandPromptTemplates: false },
        );
        if (
          hasToolCallSince(
            session.messages as readonly AgentMessageLike[],
            retryStart,
            run.requiredTool,
          )
        ) {
          run.requiredToolSatisfied = true;
        }
      }

      if (run.requiredTool && !run.requiredToolSatisfied) {
        run.stopReason = 'error';
        run.error = `本轮要求调用工具 ${run.requiredTool}，但模型没有调用。`;
      }
    } catch (err) {
      run.error = run.error ?? describeError(err);
      run.stopReason = 'error';
      this.logger.error('pi 会话执行失败', {
        promptId,
        ...safeErrorFields(err, data.apiKey),
        apiKey: this.store.maskedApiKey,
      });
    } finally {
      if (run.savedTools) {
        try {
          session.setActiveToolsByName(run.savedTools);
        } catch (err) {
          this.logger.debug('恢复工具集失败', errorFields(err));
        }
        run.savedTools = null;
      }
      this.host.unbind(emit);
      this.active = null;
    }

    if (!emit.isActive()) return;

    const failed = run.stopReason === 'error' || run.stopReason === 'aborted';
    // 强制工具没被调用是“本轮要求未被满足”，不是上游故障：不能把配置状态改成 unreachable。
    const requiredMissing =
      !!run.requiredTool &&
      !run.requiredToolSatisfied &&
      run.error?.startsWith('本轮要求调用工具') === true;

    if (requiredMissing) {
      const safe = redactSecrets(run.error ?? '强制工具未被调用', [data.apiKey]);
      this.logger.warn('prompt 未满足 requiredTool', {
        promptId,
        tool: run.requiredTool,
        message: safe,
      });
      emit.error('tool_required', safe, promptId);
    } else if (failed) {
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

    // 蓝图卡片排在整轮最末尾：final message（或失败 error）之后、idle 之前。
    // 一次工具调用成功一张；顺序与工具完成顺序一致。
    // 同一 fileName 去重：上游自动重试可能把同一张蓝图再执行一遍。
    const uniqueCards: BlueprintCardPayload[] = [];
    const seenCards = new Set<string>();
    for (const card of run.cards) {
      // 同一轮里生成后又被 blueprint_fs rm/rmdir 删掉的文件不再发卡。
      if (!blueprintCardFileExists(card)) continue;
      if (seenCards.has(card.fileName)) continue;
      seenCards.add(card.fileName);
      uniqueCards.push(card);
    }
    if (uniqueCards.length > 0) {
      // 先落盘再发帧：即使这一帧没送到，重进存档仍能从历史里回放。
      this.appendBlueprintCards(run.saveKey, run.userText, uniqueCards);
      for (const card of uniqueCards) emit.blueprintCard?.(card);
    }

    const uniqueLinks: LinkCardPayload[] = [];
    const seenLinks = new Set<string>();
    for (const card of run.linkCards) {
      const key = `${card.kind}:${card.planetId}:${card.kind === 'vein' ? card.veinId ?? 0 : card.buildingId ?? 0}`;
      if (seenLinks.has(key)) continue;
      seenLinks.add(key);
      uniqueLinks.push(card);
    }
    if (uniqueLinks.length > 0) {
      this.appendLinkCards(run.saveKey, run.userText, run.text, uniqueLinks);
      for (const card of uniqueLinks) emit.linkCard?.(card);
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

    // 玩家经常把文档里的「完整端点」整段粘进 API 地址（.../v1/chat/completions）。
    // 探测侧会清洗，聊天侧也必须用同一个地址，否则会出现「检测通过、对话 404」。
    // `api` 以聊天实际使用的适配器为准；自定义供应商固定 openai-completions。
    const chatApi = template?.api ?? 'openai-completions';
    const normalizedBase = baseUrl ? canonicalBaseForChat(provider, baseUrl, chatApi) : null;

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
      return normalizedBase ? { ...model, baseUrl: normalizedBase } : model;
    }

    if (!normalizedBase) return undefined;

    // 自定义 OpenAI 兼容端点：没有目录元数据，给一组保守默认值。
    const custom: Model<Api> = {
      id: modelId,
      name: displayName || modelId,
      api: 'openai-completions',
      provider,
      baseUrl: normalizedBase,
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
        baseUrl: normalizedBase,
        apiKey,
        api: 'openai-completions',
        models: [{
          id: modelId,
          name: custom.name,
          api: 'openai-completions',
          baseUrl: normalizedBase,
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
      endpoint: safeEndpoint(resolvedBase),
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
      const fetched = await fetchEndpointModels(provider, baseUrl, apiKey, null);
      if (fetched.models.length > 0) {
        this.logger.info('获取模型目录', {
          provider,
          source: fetched.source,
          count: fetched.models.length,
          endpoint: fetched.endpoint,
        });
        return {
          provider,
          models: fetched.models,
          source: fetched.source,
          error: null,
          resolvedBaseUrl: fetched.resolvedBaseUrl,
        };
      }
      this.logger.warn('获取模型目录失败', {
        provider,
        endpoint: fetched.endpoint,
        error: fetched.error ?? '端点没有返回模型列表',
      });
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

  /**
   * 只读 pi 内置目录的供应商列表（不联网、不需要 Key）。
   * 用空凭证 runtime，避免把用户已保存的 Key 带进这次只读查询。
   */
  async listProviders(): Promise<ProviderCatalogResult> {
    let pi: PiSdk;
    try {
      pi = await this.loadSdk();
    } catch (err) {
      const message = redactSecrets(describeError(err), []);
      return { providers: [], source: 'none', error: message };
    }

    try {
      const runtime = await this.createRuntime(pi, null, null);
      const seen = new Set<string>();
      const providers: ProviderCatalogEntry[] = [];
      for (const provider of runtime.getProviders()) {
        const id = provider?.id;
        if (!id || seen.has(id)) continue;
        seen.add(id);
        providers.push({ id, name: provider.name || id });
      }
      this.logger.info('读取供应商目录', { count: providers.length });
      return { providers, source: 'catalog', error: null };
    } catch (err) {
      const message = redactSecrets(describeError(err), []);
      this.logger.warn('读取供应商目录失败', { message });
      return { providers: [], source: 'none', error: message };
    }
  }

  private async catalogModels(pi: PiSdk, provider: string, apiKey: string | null): Promise<ModelCatalogEntry[]> {
    try {
      const runtime = await this.createRuntime(pi, provider, apiKey);
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

  /**
   * 建一个只带单个供应商凭证的 pi runtime（不挂会话、不联网）。
   * `provider` / `apiKey` 为 null 时是空凭证 runtime，只用于读目录。
   */
  private async createRuntime(
    pi: PiSdk,
    provider: string | null,
    apiKey: string | null,
  ): Promise<ModelRuntime> {
    return pi.ModelRuntime.create({
      // 凭证只存在于内存里：不读也不写用户的 ~/.pi/agent/auth.json。
      credentials: {
        read: async (id: string) =>
          provider !== null && apiKey !== null && id === provider
            ? { type: 'api_key' as const, key: apiKey }
            : undefined,
        list: async () =>
          provider !== null && apiKey !== null
            ? [{ providerId: provider, type: 'api_key' as const }]
            : [],
        modify: async (
          id: string,
          fn: (current: { type: 'api_key'; key?: string } | undefined) => Promise<
            { type: 'api_key'; key?: string } | undefined
          >,
        ) => {
          const current =
            provider !== null && apiKey !== null && id === provider
              ? ({ type: 'api_key', key: apiKey } as const)
              : undefined;
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

  /**
   * 当前回合的强制工具说明；pi 在每轮 `before_agent_start` 时调用。
   * 工具一旦被调用就清空，避免后续模型轮次继续被要求“必须调用”。
   */
  private currentForcedToolPrompt(): string | null {
    const run = this.active;
    if (!run || !run.requiredTool || run.requiredToolSatisfied) return null;
    return forcedToolInstruction(run.requiredTool);
  }

  /**
   * 每轮 system prompt 的附加段：强制工具 + 当前沙盒状态 + 权限档位。
   * 沙盒/权限都按当前状态实时读取，不写进人格常量，避免旧 mod 被误导。
   */
  private currentExtraPrompt(): string | null {
    const parts: string[] = [];
    const forced = this.currentForcedToolPrompt();
    if (forced) parts.push(forced);

    const permission = this.store.get().permissionLevel ?? DEFAULT_PERMISSION_LEVEL;
    if (permission === 'readonly') {
      parts.push(
        '当前权限为「只读」：所有写操作执行前都会请求玩家确认。读工具可以直接用；' +
          '玩家拒绝时不要重试，改成询问意图。',
      );
    } else if (permission === 'takeover') {
      parts.push(
        '当前权限为「接管」：所有写操作直接执行，不再请求确认。' +
          '执行前仍要确认目标、范围和参数明确；有歧义时先用 ask_user 问清楚，不要猜。',
      );
    } else {
      parts.push(
        '当前权限为「受限」：镜头/标记/面板/方位/撤销等低风险写操作直接执行；' +
          '改变存档状态的写操作（改配方、研究、存储、投料等）仍会请求玩家确认。',
      );
    }

    if (this.host.has(CAPABILITY_GAME_SANDBOX)) {
      parts.push(
        this.sandboxEnabled
          ? '当前存档启用了游戏沙盒工具。工具列表中若有沙盒专属工具，执行前仍需玩家确认。'
          : '当前存档未启用游戏沙盒工具。沙盒专属工具不会出现在工具列表中，不要尝试调用。',
      );
    }

    return parts.length > 0 ? parts.join('\n\n') : null;
  }

  /**
   * 构造本次能力下实际注册的全部工具定义。
   * ensureSession 与 ready.payload.toolCatalog 共用，避免两处工具集漂移。
   */
  private buildToolDefinitions(): ToolDefinition[] {
    return [
      ...this.host.tools(),
      ...(this.host.has(CAPABILITY_UI_ASK) ? [createAskTool(this.host)] : []),
      ...(this.kb ? createKbTools(this.kb) : []),
      ...createWikiTools(this.wiki),
      ...createBlueprintTools(
        undefined,
        this.kb ?? undefined,
        this.aliases,
        () => this.blueprintTiers,
        this.blueprintWorkspace,
        undefined,
        (request) => this.host.confirmLocalWrite(request),
      ),
      createScriptTool(this.host),
    ];
  }

  /** M4l：ready.payload.toolCatalog；只暴露显示需要的短字段。 */
  toolCatalog(capabilities: readonly string[]): ToolCatalogEntry[] {
    this.host.setCapabilities(capabilities);
    return this.buildToolDefinitions().map((tool) => ({
      name: tool.name,
      label: tool.label || tool.name,
      description: clipText(tool.description ?? '', 160),
      debuggable: isDebugToolAllowed(tool.name),
    }));
  }

  /**
   * M4m：调试直调。完全绕开 pi 会话/LLM，只执行一个工具。
   * 与 `run` 共用 `chain`，避免 `GameToolHost.bind` 和正在跑的回合抢 transport。
   */
  debugTool(
    name: string,
    args: JsonObject,
    emit: AgentEmitter,
  ): Promise<ToolDebugResultPayload> {
    const task = async (): Promise<ToolDebugResultPayload> => this.debugToolOnce(name, args, emit);
    const previous = this.chain;
    const next = previous.then(task, task);
    this.chain = next.catch(() => undefined);
    return next;
  }

  private debugFailure(
    name: string,
    source: 'game' | 'local' | 'unknown',
    startedAt: number,
    code: string,
    message: string,
  ): ToolDebugResultPayload {
    return {
      ok: false,
      tool: name,
      source,
      durationMs: Math.max(0, Date.now() - startedAt),
      content: `[${code}] ${message}`,
      details: { ok: false, error: { code, message } },
      error: { code, message },
      truncated: false,
      image: null,
    };
  }

  private async debugToolOnce(
    name: string,
    args: JsonObject,
    emit: AgentEmitter,
  ): Promise<ToolDebugResultPayload> {
    const startedAt = Date.now();
    const source: 'game' | 'local' | 'unknown' = this.host.tools().some((tool) => tool.name === name)
      ? 'game'
      : 'local';

    if (!isDebugToolAllowed(name)) {
      return this.debugFailure(
        name,
        source,
        startedAt,
        'debug_not_allowed',
        `工具 ${name} 不在调试只读白名单里；写文件/改游戏状态的工具只能走正常 AI 路径`,
      );
    }

    const definition = this.buildToolDefinitions().find((tool) => tool.name === name);
    if (!definition) {
      return this.debugFailure(name, source, startedAt, 'tool_not_found', `没有注册工具 ${name}`);
    }

    try {
      if (!Value.Check(definition.parameters, args)) {
        const errors = [...Value.Errors(definition.parameters, args)]
          .slice(0, 4)
          .map((error) => {
            const item = error as { instancePath?: unknown; message?: unknown };
            const path = typeof item.instancePath === 'string' ? item.instancePath : '/';
            const message = typeof item.message === 'string' ? item.message : '参数不匹配';
            return `${path} ${message}`;
          });
        return this.debugFailure(
          name,
          source,
          startedAt,
          'invalid_args',
          errors.length > 0 ? errors.join('; ') : '参数不匹配工具的 schema',
        );
      }

      // 与 prompt 相同：工具只发往当前 session 绑定的连接，结束后立即解绑。
      // debugMode 让 host 调用标记 internal（mod 不显示普通工具卡，由 tool_debug_result 统一展示）。
      this.host.bind(emit);
      this.host.setDebugMode(true);
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), DEBUG_TIMEOUT_MS);
      timer.unref?.();
      try {
        const result = await definition.execute(
          `debug_${startedAt}`,
          args as never,
          controller.signal,
          undefined,
          undefined as never,
        );

        let details: unknown = result.details;
        let image: ToolDebugResultPayload['image'] = null;
        const detailsObject = asRecord(details);
        const rawImage = detailsObject ? asRecord(detailsObject['image']) : null;
        if (rawImage && typeof rawImage['data'] === 'string') {
          const mimeType = typeof rawImage['mimeType'] === 'string' ? rawImage['mimeType'] : 'image/jpeg';
          const width = typeof rawImage['width'] === 'number' ? rawImage['width'] : 0;
          const height = typeof rawImage['height'] === 'number' ? rawImage['height'] : 0;
          image = { mimeType, width, height, bytes: base64Bytes(rawImage['data']) };
          // 图片 base64 可能有 2MiB；调试结果只保留元数据，避免把 frame 撑爆。
          details = {
            ...detailsObject,
            image: { mimeType, width, height, bytes: image.bytes },
          };
        }
        if (!image && Array.isArray(result.content)) {
          for (const block of result.content) {
            const item = asRecord(block);
            if (item && item['type'] === 'image' && typeof item['data'] === 'string') {
              const mimeType = typeof item['mimeType'] === 'string' ? item['mimeType'] : 'image/jpeg';
              image = { mimeType, width: 0, height: 0, bytes: base64Bytes(item['data']) };
              break;
            }
          }
        }

        const detailsObject2 = asRecord(details);
        const rawContent = contentText(result.content);
        const errorPrefix = /^\[([a-z_]+)\]\s*/.exec(rawContent);
        const explicitOk =
          typeof detailsObject2?.['ok'] === 'boolean' ? detailsObject2['ok'] : null;
        const hasErrorField = detailsObject2 != null && detailsObject2['error'] != null;
        const ok = explicitOk ?? !(hasErrorField || !!errorPrefix);
        let error: { code: string; message: string } | null = null;
        if (!ok) {
          const rawError = detailsObject2 ? detailsObject2['error'] : null;
          const errorObject = asRecord(rawError);
          const fallbackMessage = errorPrefix
            ? (rawContent.slice(errorPrefix[0].length).split('\n')[0] ?? '')
            : '工具返回失败';
          error = {
            code:
              typeof errorObject?.['code'] === 'string'
                ? errorObject['code']
                : (errorPrefix?.[1] ?? 'tool_failed'),
            message:
              typeof errorObject?.['message'] === 'string'
                ? errorObject['message']
                : (typeof rawError === 'string' ? rawError : fallbackMessage),
          };
        }

        let content = rawContent;
        let truncated = false;
        if (content.length > DEBUG_CONTENT_MAX) {
          content = `${content.slice(0, DEBUG_CONTENT_MAX)}\n…（内容截断）`;
          truncated = true;
        }
        const serialized = debugJson(details);
        if (serialized.length > DEBUG_DETAILS_MAX) {
          details = `${serialized.slice(0, DEBUG_DETAILS_MAX)}\n…（返回值截断）`;
          truncated = true;
        }

        return {
          ok,
          tool: name,
          source,
          durationMs: Math.max(0, Date.now() - startedAt),
          content,
          details,
          error,
          truncated,
          image,
        };
      } finally {
        clearTimeout(timer);
        this.host.setDebugMode(false);
        this.host.unbind(emit);
      }
    } catch (err) {
      const aborted = /abort/i.test(describeError(err));
      return this.debugFailure(
        name,
        source,
        startedAt,
        aborted ? 'timeout' : 'tool_failed',
        aborted ? `工具在 ${DEBUG_TIMEOUT_MS}ms 内没有完成` : describeError(err),
      );
    }
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

    const resourceLoader = createPersonaResourceLoader(
      pi,
      {
        cwd: this.dataDir,
        agentDir: this.agentDir,
      },
      () => this.currentExtraPrompt(),
      () => this.host.has(CAPABILITY_RICH_TEXT_V2),
    );
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
      customTools: this.buildToolDefinitions(),
      // 替换 pi 默认的 coding agent system prompt 为游戏内助手人格。
      resourceLoader,
    };

    const created = await pi.createAgentSession(options);
    this.runtime = runtime;
    this.session = created.session;
    this.unsubscribe = created.session.subscribe((event) => this.handleEvent(event));

    // 保险：确认 pi 真的用上了我们的人格（没生效就明确记一条 warn，别静默退化）。
    const effectivePrompt = created.session.systemPrompt ?? '';
    if (
      !effectivePrompt.startsWith(ICARUS_II_SYSTEM_PROMPT) &&
      !effectivePrompt.startsWith(ICARUS_II_RICH_TEXT_PROMPT)
    ) {
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
      systemPromptChars: effectivePrompt.length,
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
              // 强制工具尚未调用时先不流给 UI：如果模型直接作答，这次输出会被丢弃重试，
              // 提前显示会在界面上留下半截回答。
              if (!run.requiredTool || run.requiredToolSatisfied) {
                run.emit.stream('text', inner.delta, run.promptId);
              }
            }
            return;
          case 'thinking_delta':
            if (inner.delta.length > 0) {
              run.thinking += inner.delta;
              if (!run.requiredTool || run.requiredToolSatisfied) {
                run.emit.stream('reasoning', inner.delta, run.promptId);
              }
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
        // 强制工具已被调用：恢复完整工具集，并允许后续文本正常流式输出。
        if (run.requiredTool && event.toolName === run.requiredTool && !run.requiredToolSatisfied) {
          run.requiredToolSatisfied = true;
          if (run.savedTools && this.session) {
            try {
              this.session.setActiveToolsByName(run.savedTools);
            } catch (err) {
              this.logger.debug('恢复工具集失败', errorFields(err));
            }
            run.savedTools = null;
          }
        }
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
        // 同一轮里 AI 用 blueprint_fs 删掉刚生成的蓝图时，先把待发卡片撤掉。
        if (!event.isError && event.toolName === 'blueprint_fs') {
          run.cards = dropDeletedBlueprintCards(run.cards, event.result);
        }
        // 蓝图工具成功 ⇒ 记一张卡片，等 final message 后统一发，保证排在对话末尾。
        const card = extractBlueprintCard(event.toolName, event.result, event.isError);
        if (card) run.cards.push(card);
        const linkCard = extractLinkCard(event.toolName, event.result, event.isError);
        if (linkCard) run.linkCards.push(linkCard);
        return;
      }

      case 'compaction_start': {
        this.logger.info('开始整理上下文', { promptId: run.promptId, reason: event.reason });
        run.emit.status('compacting', event.reason);
        return;
      }

      case 'compaction_end': {
        if (event.result && !event.aborted) {
          // compaction entry 是刚追加的；取当前 branch 上最后一条，供 UI 撤销。
          let compactionId: string | undefined;
          try {
            const branch = this.session?.sessionManager.getBranch() ?? [];
            for (const entry of branch) {
              const item = entry as { type?: unknown; id?: unknown };
              if (item.type === 'compaction' && typeof item.id === 'string') compactionId = item.id;
            }
          } catch (err) {
            this.logger.debug('读取 compaction entry id 失败', errorFields(err));
          }
          run.emit.compacted?.({
            summary: clipHistoryText(event.result.summary, 4000),
            ...(compactionId ? { compactionId } : {}),
            timestamp: Date.now(),
            tokensBefore: event.result.tokensBefore,
            ...(typeof event.result.estimatedTokensAfter === 'number'
              ? { estimatedTokensAfter: event.result.estimatedTokensAfter }
              : {}),
            reason: event.reason,
          });
          this.logger.info('上下文压缩完成', {
            promptId: run.promptId,
            reason: event.reason,
            tokensBefore: event.result.tokensBefore,
            estimatedTokensAfter: event.result.estimatedTokensAfter ?? null,
            willRetry: event.willRetry,
          });
        } else if (event.errorMessage) {
          this.logger.warn('上下文压缩失败', {
            promptId: run.promptId,
            reason: event.reason,
            message: event.errorMessage,
            aborted: event.aborted,
          });
        }
        // 压缩后 pi 可能继续重试本轮；最终 idle 会由正常流程发出。
        run.emit.status('thinking');
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
