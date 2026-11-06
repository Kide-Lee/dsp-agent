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
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readFileSync,
  readSync,
  readdirSync,
  renameSync,
  rmSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import { randomUUID } from 'node:crypto';
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
  ICARUS_II_SYSTEM_PROMPT,
} from './persona.js';
import { renderCapabilitySection, renderToolRules, toolsSignature } from './prompt/sections.js';
import { loadPromptOverride, type PromptOverride } from './prompt/override.js';

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
  type ConversationInfo,
  type ConversationRecord,
  type ConversationSaveGroup,
  type ConversationIndexEntry,
  type HistoryToolStep,
  type HistoryTextStep,
  type JsonObject,
  type ModelCatalogEntry,
  type ModelCatalogResult,
  type ProviderCatalogEntry,
  type ProviderCatalogResult,
  type ProviderTestResult,
  type BlueprintTiers,
  type StatusState,
  type StreamChannel,
  type ExtensionSummary,
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
import { createWikiTools } from './wiki/tools.js';
import { WikiClient } from './wiki/client.js';
import { AliasResolver } from './wiki/aliases.js';
import { createBlueprintTools } from './blueprint/tools.js';
import { toolGroupOf, toolRiskOf, toolSourceOf } from './tools/registry.js';
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
import {
  attachCompactionTimes,
  attachGameTimes,
  type CompactionTimeRecord,
  type GameTimeRecord,
} from './history/game-time.js';
import type { KnowledgeStore } from './kb/store.js';

/** 会话目录里的元数据日志；它们不是 pi 会话文件，不能被当成会话读取/删除。 */
const SESSION_METADATA_FILES = new Set([
  'blueprint-cards.jsonl',
  'link-cards.jsonl',
  'reply-times.jsonl',
  'compaction-times.jsonl',
]);

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
  /** `message` 帧。`interrupted` = 本轮被玩家中断（附录 A.15）。 */
  message(
    text: string,
    replyTo: string | null,
    final?: boolean,
    options?: { interrupted?: boolean },
  ): void;
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
  /** A.13：模型总结出会话标题；只更新左侧索引/标题栏，不重放历史。 */
  conversationUpdated?(payload: { conversationId: string; title: string }): void;
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
    conversationId?: string | null,
  ): Promise<void>;
  /** A.15：中断指定会话（缺省为焦点会话）；返回是否真的中断了一个回合。 */
  abort?(conversationId?: string | null): Promise<boolean>;
  /** 连接关闭/换档：中断所有会话的当前回合。 */
  abortAll?(): void;
  /**
   * A.15：是否有回合正在跑。
   * `abort()` 要等 agent 空闲才返回，那时收尾帧已经发出去了，
   * 所以 `status{aborting}` 必须靠这个同步判断先发，不能等 abort 的返回值。
   */
  isRunning?(conversationId?: string | null): boolean;
  /**
   * A.15：插话。当前回合在跑时把这句话投递进该回合并返回 true；
   * 没有回合在跑（或实现不支持）时返回 false，由调用方退回普通提问。
   */
  steer?(text: string, conversationId?: string | null): Promise<boolean>;
  /**
   * 并行会话：切回一个正在跑的会话时，取已生成的正文，补上切走期间丢失的显示。
   */
  getProgress?(conversationId: string): { text: string; thinking: string; running: boolean };
  /**
   * 本次连接实际可用的工具目录（M4l，供 ready.payload.toolCatalog / 聊天窗工具列表）。
   * 老实现可以不提供。
   */
  toolCatalog?(capabilities: readonly string[], extensions?: readonly ExtensionSummary[]): ToolCatalogEntry[];
  /**
   * A.14.1：整份替换被关闭的工具名。返回落盘后的列表与是否重建了会话。
   * 老实现可以不提供（设定页会显示全部启用）。
   */
  applyDisabledTools?(disabled: readonly string[]): { disabled: string[]; rebuilt: boolean };
  /**
   * A.14.2：跨存档会话索引；只读，不改变当前存档/活动会话。
   * 老实现可以不提供。
   */
  conversationIndex?(): Promise<{ currentSaveKey: string | null; saves: ConversationSaveGroup[] }>;
  /**
   * A.14.3：把一个会话导出成 Markdown；只读会话，不改活动会话。
   */
  exportConversation?(
    saveKey: string | null,
    conversationId: string,
  ): Promise<{ ok: boolean; path?: string; bytes?: number; messageCount?: number; error?: string }>;
  /** A.14.6：记录存档显示名，供会话索引分组使用。 */
  noteSaveBinding?(saveKey: string | null, saveName: string | null): void;
  /**
   * M4m：调试直调。不创建/使用 pi 会话，不调用 LLM；只执行一个已注册工具。
   * 老实现可以不提供。
   */
  debugTool?(name: string, args: JsonObject, emit: AgentEmitter): Promise<ToolDebugResultPayload>;
  /** `config.set` 落地后调用：让下一次 run 使用新配置。 */
  onConfigChanged(): void;
  /** 握手完成后同步 mod 能力与第三方插件列表；任一变化时 agent 应重建会话。 */
  onClientCapabilities?(capabilities: readonly string[], extensions?: readonly ExtensionSummary[]): void;
  /** 本次连接实际广告的工具名（含扩展启用状态）；老实现回退到 options.tools。 */
  toolNames?(capabilities: readonly string[], extensions?: readonly ExtensionSummary[]): readonly string[];
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
  /** A.13：当前活动会话 id；老实现可以不提供。 */
  getActiveConversationId?(): string | null;
  /** A.13：该存档的会话列表（含每个会话的用户提问记录）。 */
  getConversations?(saveKey: string | null): Promise<ConversationInfo[]>;
  /** A.13：按会话读取历史，供切换/删除后回放。 */
  getHistoryFor?(saveKey: string | null, conversationId: string): Promise<HistoryMessage[]>;
  /** A.13：新建空会话并设为活动会话。 */
  createConversation?(saveKey: string | null): Promise<{
    ok: boolean;
    conversation?: ConversationInfo;
    history?: HistoryMessage[];
    error?: string;
  }>;
  /** A.13：切换活动会话。 */
  switchConversation?(saveKey: string | null, conversationId: string): Promise<{
    ok: boolean;
    conversation?: ConversationInfo;
    history?: HistoryMessage[];
    discardedConversationId?: string;
    error?: string;
  }>;
  /** A.13：玩家重命名会话（写入 pi session_info）。 */
  renameConversation?(saveKey: string | null, conversationId: string, title: string): Promise<{
    ok: boolean;
    title?: string;
    error?: string;
  }>;
  /** A.13：删除会话；如果删的是活动会话，返回新的活动会话 id。 */
  deleteConversation?(saveKey: string | null, conversationId: string): Promise<{
    ok: boolean;
    error?: string;
    activeConversationId?: string | null;
  }>;
  /**
   * 删除某个存档里的一个问答回合（M4e）。成功表示会话文件已被改写、
   * 内存会话已丢弃；失败用 `error` 说明原因（busy / turn_not_found / ...）。
   *
   * `conversationId` 可选：指定要改写的会话；缺省时按旧版“最新 session 文件”定位，
   * 保证老 mod 不传该字段时行为不变。
   */
  deleteHistory?(
    saveKey: string | null,
    text: string,
    timestamp?: number,
    conversationId?: string | null,
  ): Promise<{ ok: boolean; removed: number; error?: string }>;
  /**
   * 撤销最近一次上下文压缩（附录 A.11）：删掉 compaction entry，旧消息重新进入
   * 模型上下文；下一次提问如果仍超预算，可能立刻再次压缩。
   *
   * `conversationId` 可选，语义同 `deleteHistory`。
   */
  undoCompaction?(
    saveKey: string | null,
    compactionId?: string,
    conversationId?: string | null,
  ): Promise<{
    ok: boolean;
    removed: number;
    compactionId?: string;
    error?: string;
    mayRecompact?: boolean;
  }>;
  /** 回存一轮回答完成时的游戏时间（附录 A.10）；写盘失败不影响回答。 */
  recordReplyTime?(
    saveKey: string | null,
    prompt: string,
    reply: string,
    gameTime: string,
    conversationId?: string | null,
    gameTick?: number | null,
  ): void;
  recordCompactionTime?(saveKey: string | null, record: CompactionTimeRecord): void;
  /** A.13：置顶/取消置顶一个会话；只影响左栏排序。 */
  pinConversation?(saveKey: string | null, conversationId: string, pinned: boolean): Promise<{
    ok: boolean;
    error?: string;
  }>;
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
 * - assistant 顺带带上它之前的思考块、过程正文和工具调用（`steps`，附录 A.6）：
 *   一次提问里会有多个 assistant 步骤（思考+工具、再思考+工具…，最后一段带正文），
 *   中间那些正文先按发生顺序攒进 `steps`，等最终回答落地时一起挂上去；
 * - 一段正文后面跟着工具执行（同一消息里的 toolCall，或下一条 assistant 的正文）
 *   就说明它只是过程正文，不能当回答；最后一条「没有被工具执行打断」的正文才是回答；
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
  // 当前提问里按发生顺序攒下的步骤（思考 / 过程正文 / 工具），最终挂到回答上。
  let pending: HistoryStep[] = [];
  // 本轮「还没被工具执行打断」的最后一段正文：它就是最终回答，其余正文留在 steps 里。
  let answer: {
    text: string;
    steps: HistoryTextStep[];
    entry: { timestamp?: unknown; message?: { timestamp?: unknown } };
  } | null = null;
  const toolSteps = new Map<string, HistoryToolStep>();

  /** 工具执行说明它之前的正文只是过程正文，不再是回答候选。 */
  const demoteAnswer = (): void => {
    answer = null;
  };

  /** 一轮结束：把回答和过程步骤分开落地；没有回答的轮次不生成消息。 */
  const flushTurn = (): void => {
    const current = answer;
    const steps = pending;
    pending = [];
    answer = null;
    if (!current) return;
    const text = clipHistoryText(current.text, maxText);
    if (!text) return;
    const history: HistoryMessage = { role: 'assistant', text, ...timestampOf(current.entry) };
    const answerSteps = new Set<HistoryStep>(current.steps);
    const rest = steps.filter((step) => !answerSteps.has(step));
    if (rest.length > 0) history.steps = rest;
    out.push(history);
  };

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
      flushTurn();
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
      toolSteps.clear();
      continue;
    }

    if (entry.type !== 'message' || !entry.message) continue;
    const message = entry.message;
    const role = message.role;

    if (role === 'user') {
      flushTurn();
      toolSteps.clear();
      const text = contentText(message.content).trim();
      if (!text) continue;
      out.push({ role: 'user', text: clipHistoryText(text, maxText), ...timestampOf(entry) });
      continue;
    }

    if (role === 'assistant') {
      const blocks = Array.isArray(message.content) ? message.content : [];
      const textParts: string[] = [];
      const textSteps: HistoryTextStep[] = [];
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
          const text = item.text.trim();
          if (!text) continue;
          textParts.push(item.text);
          const step: HistoryTextStep = {
            kind: 'text',
            text: clipHistoryText(text, HISTORY_TEXT_MAX),
          };
          pending.push(step);
          textSteps.push(step);
        } else if (item.type === 'thinking' && typeof item.thinking === 'string') {
          const thinking = item.thinking.trim();
          if (thinking) {
            pending.push({ kind: 'thinking', text: clipHistoryText(thinking, HISTORY_THINKING_MAX) });
          }
        } else if (item.type === 'toolCall') {
          demoteAnswer();
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

      if (textSteps.length > 0) {
        answer = { text: textParts.join('\n').trim(), steps: textSteps, entry };
      }
      // toolSteps 保留到下一条 user 消息：结果可能在回答之后才写进会话。
      continue;
    }

    if (role === 'toolResult') {
      const callId = typeof message.toolCallId === 'string' ? message.toolCallId : '';
      if (!callId) continue;
      const step = toolSteps.get(callId);
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

  flushTurn();

  const recent = out.slice(-maxMessages);
  trimHistorySteps(recent);
  return recent;
}

/** 单条回答最多回放的步骤数（正常一轮 1~30 个，超出的只保留最近的）。 */
const HISTORY_MAX_STEPS = 60;
/** A.13：save.bound 里最多带多少个会话 / 每个会话多少条提问索引。 */
const MAX_CONVERSATIONS = 50;
const MAX_CONVERSATION_RECORDS = 40;
/** A.14.2：设定页索引最多返回多少个存档。 */
const MAX_INDEX_SAVES = 50;
/** A.14.3：导出文件名与工具参数截断。 */
const EXPORT_NAME_MAX_CHARS = 60;
const EXPORT_STEP_CLIP_CHARS = 200;
/** 导出时间戳格式：yyyymmdd-HHmm（本地时区）。 */
function formatExportStamp(date: Date): string {
  const pad = (value: number): string => String(value).padStart(2, '0');
  return `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}-${pad(date.getHours())}${pad(date.getMinutes())}`;
}

/** 导出文件名清洗：去掉 Windows 非法字符与换行，压缩空白，按字符截断。 */
function sanitizeExportName(raw: string, maxChars: number): string {
  return raw
    .replace(/[\\/:*?"<>|]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, maxChars)
    .trim();
}

function clipExportText(text: string, maxChars: number): string {
  const oneLine = text.replace(/\s+/g, ' ').trim();
  return oneLine.length <= maxChars ? oneLine : `${oneLine.slice(0, maxChars)}…`;
}

function formatExportTime(timestamp: number | undefined): string {
  if (typeof timestamp !== 'number' || !Number.isFinite(timestamp) || timestamp <= 0) return '';
  const date = new Date(timestamp);
  const pad = (value: number): string => String(value).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** A.14.3：会话的 Markdown 导出；工具/思考只留引用块摘要，不写图片字节。 */
function renderConversationMarkdown(args: {
  title: string;
  saveName: string;
  saveKey: string;
  exportedAt: Date;
  history: readonly HistoryMessage[];
}): string {
  const lines: string[] = [];
  lines.push(`# ${args.title}`, '');
  lines.push(`- 存档：${args.saveName}\`（${args.saveKey}）\``);
  lines.push(`- 导出：${formatExportTime(args.exportedAt.getTime())}`);
  lines.push('', '---', '');
  for (const message of args.history) {
    if (message.kind === 'compaction') {
      lines.push('## 自动压缩上下文', '', message.text, '');
      continue;
    }
    const time = formatExportTime(message.timestamp);
    const who = message.role === 'user' ? '你' : '助手';
    lines.push(`## ${who}${time ? ` · ${time}` : ''}`, '');
    const steps = message.steps ?? [];
    if (steps.length > 0) {
      lines.push('> 思考与工具步骤：');
      for (const step of steps) {
        if (step.kind === 'tool') {
          const detail = step.args ? `：${clipExportText(step.args, EXPORT_STEP_CLIP_CHARS)}` : '';
          lines.push(`> - 工具 \`${step.name}\`${detail}`);
        } else {
          lines.push(`> - 思考：${clipExportText(step.text, EXPORT_STEP_CLIP_CHARS)}`);
        }
      }
      lines.push('');
    }
    lines.push(message.text.trim(), '');
  }
  return `${lines.join('\n')}\n`;
}
/** 模型总结标题的输入预算与输出长度（字符）。 */
const CONVERSATION_TITLE_INPUT_MAX = 2000;
const CONVERSATION_TITLE_MAX_CHARS = 24;
const CONVERSATION_RENAME_MAX_CHARS = 40;
const CONVERSATION_TITLE_SYSTEM_PROMPT = [
  '你为《戴森球计划》的 AI 助手会话生成一个短标题。',
  '根据玩家的提问和助手的回答，概括这次对话的主题或任务；不要照抄提问。',
  '只输出一行纯文本标题，不要引号、Markdown、编号、解释或控制字符。',
  '使用玩家提问的语言；中文 6-14 个字，英文 2-6 个词。',
  '如果内容太少，也给一个简短概括，例如“新手开局咨询”。',
].join('\n');
const HISTORY_THINKING_MAX = 4000;
/** 一段过程正文（模型在思考/工具之间说的话）的上限。 */
const HISTORY_TEXT_MAX = 2000;
const HISTORY_TOOL_ARGS_MAX = 600;
const HISTORY_TOOL_SUMMARY_MAX = 160;
const HISTORY_TOOL_DETAIL_MAX = 2000;
/** 所有历史消息 steps 的总字符预算，避免一帧超过协议 8 MiB 上限。 */
const HISTORY_STEPS_BUDGET = 2 * 1024 * 1024;

function clipHistoryText(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

/**
 * 清理模型输出的标题：只取第一行，去掉标题前缀/引号/Markdown 强调/结尾标点，
 * 并限制长度。与 DSH 的 session-title 归一化思路一致，但不引入其整套服务。
 */
export function normalizeConversationTitle(raw: string): string {
  const line = String(raw ?? '')
    .replace(/\r/g, '')
    .split('\n')
    .map((item) => item.trim())
    .find((item) => item.length > 0) ?? '';
  let title = line;
  // 允许模型套一层 Markdown/引号再写“标题：…”，剥两轮。
  for (let i = 0; i < 2; i += 1) {
    title = title
      .replace(/^(?:标题|会话标题|title)\s*[:：]\s*/i, '')
      .replace(/^[*_`"'“”‘’]+|[*_`"'“”‘’]+$/g, '')
      .trim();
  }
  title = title
    .replace(/[\u0000-\u001f\u007f]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/[。.!！?？；;，,、]+$/g, '')
    .trim();
  const chars = Array.from(title);
  if (chars.length > CONVERSATION_TITLE_MAX_CHARS)
    title = chars.slice(0, CONVERSATION_TITLE_MAX_CHARS).join('');
  return title;
}

/** 玩家重命名：去换行/控制字符、压缩空白，最多 40 个字符。 */
export function normalizeConversationRename(raw: string): string {
  const text = String(raw ?? '')
    .replace(/[\r\n]+/g, ' ')
    .replace(/[\u0000-\u001f\u007f]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  const chars = Array.from(text);
  return chars.length > CONVERSATION_RENAME_MAX_CHARS
    ? chars.slice(0, CONVERSATION_RENAME_MAX_CHARS).join('')
    : text;
}

/** 每个会话最后一条回答的 gameTick：同一会话取最大值；没有归属或没有 tick 的记录跳过。 */
function lastReplyTicks(records: readonly GameTimeRecord[]): Map<string, number> {
  const out = new Map<string, number>();
  for (const record of records) {
    if (!record.conversationId || typeof record.gameTick !== 'number') continue;
    const current = out.get(record.conversationId);
    if (current === undefined || record.gameTick > current) {
      out.set(record.conversationId, record.gameTick);
    }
  }
  return out;
}

/**
 * A.13 左侧会话排序：只由「是否置顶」和「最后回复时间」决定。
 * 最后回复时间用 updatedAt（会话文件的最后写入时间，也就是最后一条回答），
 * 切换会话不写它，所以点一下不会跳位；时间相同再按 createdAt。
 */
export function compareConversations(a: ConversationInfo, b: ConversationInfo): number {
  const pinnedA = a.pinned === true ? 1 : 0;
  const pinnedB = b.pinned === true ? 1 : 0;
  if (pinnedA !== pinnedB) return pinnedB - pinnedA;
  const byUpdated = b.updatedAt - a.updatedAt;
  if (byUpdated !== 0) return byUpdated;
  const byCreated = b.createdAt - a.createdAt;
  if (byCreated !== 0) return byCreated;
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

/** 标题模型只看到这次问答的裁剪版，避免长回答把请求撑大。 */
function conversationTitleInput(userText: string, assistantText: string): string {
  const clip = (value: string) => {
    const text = (value ?? '').trim();
    return text.length > CONVERSATION_TITLE_INPUT_MAX
      ? `${text.slice(0, CONVERSATION_TITLE_INPUT_MAX)}…`
      : text;
  };
  return JSON.stringify({ user: clip(userText), assistant: clip(assistantText) });
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
    if (step.kind === 'thinking' || step.kind === 'text') total += step.text.length;
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

interface ConversationSlot {
  conversationId: string;
  session: AgentSession;
  runtime: ModelRuntime;
  unsubscribe: () => void;
  run: RunState | null;
  abortRequested: boolean;
  draft: boolean;
  configKey: string;
}

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
  /**
   * 开发/评测用：整份替换内置人格（启动读一次，失败直接抛错）。
   * 游戏内 UI 不暴露这个开关，避免玩家把线上行为改成不可复现的状态。
   */
  systemPromptFile?: string;
}

/** 记录没有会话归属（老数据）或归属相同，才允许被这次会话级删除命中。 */
function recordTargetsConversation(
  record: { conversationId?: string },
  conversationId?: string | null,
): boolean {
  return !conversationId || !record.conversationId || record.conversationId === conversationId;
}

/** 一次 prompt 的运行状态；事件回调按此聚合。 */
interface RunState {
  emit: AgentEmitter;
  promptId: string | null;
  /** 本轮开始时的存档；中途换档时卡片仍记在发起回合的存档里。 */
  saveKey: string | null;
  /** 本轮用户提问原文；用于把卡片记录挂回历史回合。 */
  userText: string;
  /** 本轮所属会话；用于把卡片/时间记录按会话归属，避免同文提问串会话。 */
  conversationId: string | null;
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

  /** 本轮是否写过待办；写过才在收尾时清空原生目标面板里的待办组。 */
  todoWritten: boolean;
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
  persona: string,
): ExtensionFactory {
  return (api) => {
    api.on('before_agent_start', () => {
      const extra = getExtraPrompt();
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
  persona: string,
): ResourceLoader {
  const getPrompt = () => persona;
  const base = new pi.DefaultResourceLoader({
    cwd: options.cwd,
    agentDir: options.agentDir,
    noExtensions: true,
    noSkills: true,
    noPromptTemplates: true,
    noThemes: true,
    noContextFiles: true,
    // 替换（不是追加）pi 的 system prompt；override 是双保险。
    systemPrompt: persona,
    systemPromptOverride: getPrompt,
    appendSystemPromptOverride: () => [],
    agentsFilesOverride: () => ({ agentsFiles: [] }),
    extensionFactories: [createPersonaSystemPromptExtension(getExtraPrompt, persona)],
  });
  return new PersonaResourceLoader(base, persona);
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

// 覆盖 run_script 的运行时间上限（最大 120 秒），否则脚本类工具无法用满自己的预算。
const DEBUG_TIMEOUT_MS = 130_000;
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
  /** 非 null 时整份替换内置人格（--system-prompt-file）。 */
  private readonly promptOverride: PromptOverride | null;
  /** 本次会话实际使用的人格文本（覆盖优先，否则内置常量）。 */
  private readonly systemPromptText: string;

  /** 每个会话一个独立 pi session；同一进程内可并行跑多个。 */
  private readonly slots = new Map<string, ConversationSlot>();
  /** 每会话一条 prompt 队列：同一会话串行，不同会话并行。 */
  private readonly runQueues = new Map<string, Promise<void>>();
  /** 进程级并行上限，避免一次创建过多模型请求/工具调用。 */
  private readonly maxParallelRuns = 3;
  private activeRuns = 0;
  private readonly runWaiters: Array<() => void> = [];
  /** 正在建立 pi session、还没挂上 slot.run 的回合；用于让 abort 不落空。 */
  private readonly startingRuns = new Set<string>();
  private readonly pendingAborts = new Set<string>();
  /** 回合运行中收到 config.set 时，延后到所有回合结束再重建会话。 */
  private pendingTeardown = false;
  /** 当前绑定的存档；每个存档使用独立的 session 目录。 */
  private saveKey: string | null = null;
  /** A.13：当前活动会话 id；同一存档的多个 pi session 靠它切换。 */
  private activeConversationId: string | null = null;
  /** A.13：活动会话还没有 session 文件（新建后尚未发过消息）。 */
  private activeConversationDraft = false;
  /** A.13：未落盘 draft 的标题；第一次提问建会话时写入 session_info。 */
  private activeConversationDraftTitle: string | null = null;
  /** A.13：activeConversationId 是针对哪个 saveKey 初始化的。 */
  private conversationSaveKey: string | null = null;
  /** A.13：正在异步生成标题的会话，避免重复调用模型。 */
  private readonly titlePending = new Set<string>();
  /** 当前载入是否启用游戏沙盒工具（由 save.bind 上报）。 */
  private sandboxEnabled = false;
  /** mod 在 save.bind 里被动上报的蓝图档位；模型不需要调用查询工具。 */
  private blueprintTiers: BlueprintTiers | null = null;
  /** 进程内全局串行：多连接共用同一个 pi 会话。 */
  private chain: Promise<unknown> = Promise.resolve();
  /** 工具能力/规则段落的缓存（能力集不变时复用同一段字节）。 */
  private toolPromptCache: { signature: string; text: string } | null = null;

  onBlueprintTiers(tiers: BlueprintTiers | null): void {
    this.blueprintTiers = tiers;
  }

  constructor(options: PiAgentOptions) {
    this.dataDir = options.dataDir;
    this.agentDir = join(options.dataDir, 'agent');
    this.store = options.store;
    this.host = options.host;
    // 启用/禁用扩展工具后，下一轮重建会话；回合中则延后。
    this.host.onToolsetChanged = () => {
      if (this.isRunning()) {
        this.pendingTeardown = true;
        return;
      }
      this.teardown();
    };
    // A.14.1：配置里的工具开关在启动时就生效，脚本白名单/预算都读同一份。
    this.host.setDisabledTools?.(this.store.disabledToolNames?.() ?? []);
    this.kb = options.kb ?? null;
    this.wiki = new WikiClient({
      zhApi: this.store.wikiEndpoint('zh') ?? undefined,
      enApi: this.store.wikiEndpoint('en') ?? undefined,
    });
    this.aliases = new AliasResolver({
      wiki: this.wiki,
      cacheFile: join(this.dataDir, 'wiki-aliases.json'),
    });
    this.blueprintWorkspace = new BlueprintWorkspace({
      root: this.store.blueprintRoot() ?? undefined,
    });
    this.logger = (options.logger ?? rootLog).child({ component: 'pi-agent' });
    this.loadSdk =
      options.loadSdk ?? (() => import('@earendil-works/pi-coding-agent') as Promise<PiSdk>);
    this.promptOverride = options.systemPromptFile
      ? loadPromptOverride(options.systemPromptFile)
      : null;
    this.systemPromptText = this.promptOverride?.text ?? ICARUS_II_SYSTEM_PROMPT;
    if (this.promptOverride) {
      // 覆盖是“启动即生效、运行期不变”的调试口子：把路径和哈希打进日志，
      // bug report 才能对上到底用了哪份提示词。
      this.logger.warn('使用外部 system prompt 覆盖（仅调试/评测）', {
        path: this.promptOverride.path,
        sha256: this.promptOverride.sha256,
        chars: this.promptOverride.text.length,
      });
    }
  }

  get info(): AgentInfo {
    const data = this.store.get();
    return { provider: data.provider, model: data.model, available: this.store.hasKey() };
  }

  onConfigChanged(): void {
    // A.14.5：config.set 也可能带 disabledTools；先同步 host 的过滤集合。
    this.host.setDisabledTools?.(this.store.disabledToolNames?.() ?? []);
    const data = this.store.get();

    // 正在跑回合时不要拆会话：否则流式输出会被拦腰截断。
    // 记下来，等这一轮结束后再重建。
    if (this.isRunning()) {
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

  onClientCapabilities(
    capabilities: readonly string[],
    extensions: readonly ExtensionSummary[] = [],
  ): void {
    const capsChanged = this.host.setCapabilities(capabilities);
    const extensionsChanged = this.host.setExtensions(extensions);
    if (!capsChanged && !extensionsChanged) return;

    this.logger.info('mod 能力/插件列表变化，工具集将重建', {
      capabilities: [...capabilities],
      extensions: extensions.map((extension) => extension.id),
    });
    if (this.isRunning()) {
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
    if (this.isRunning()) {
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
    // 换存档必须重新解析 activeConversationId；新存档的会话在第一次
    // getConversations/getHistory 时惰性加载。
    this.activeConversationId = null;
    this.activeConversationDraft = false;
    this.activeConversationDraftTitle = null;
    this.conversationSaveKey = null;
    this.logger.info('存档变化，切换对话会话', { saveKey });

    if (this.isRunning()) {
      this.pendingTeardown = true;
      this.abortAll();
      return;
    }
    this.teardown();
  }

  /** 换档/关闭：所有会话的当前回合都结束；收尾由各自的 runOnce 负责。 */
  abortAll(): void {
    for (const conversationId of this.startingRuns) this.pendingAborts.add(conversationId);
    for (const slot of this.slots.values()) {
      if (!slot.run || slot.abortRequested) continue;
      slot.abortRequested = true;
      slot.run.stopReason = 'aborted';
      void slot.session.abort().catch((err: unknown) => {
        this.logger.debug('批量中断会话失败', {
          conversationId: slot.conversationId,
          ...errorFields(err),
        });
      });
    }
  }

  getActiveConversationId(): string | null {
    return this.activeConversationId;
  }

  /** A.13：生成一个新的会话 id（同时作为 pi session id）。 */
  private newConversationId(): string {
    return `conv_${randomUUID().replace(/-/g, '').slice(0, 16)}`;
  }

  private activeFilePath(saveKey: string | null): string | null {
    if (!saveKey) return null;
    return join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey), 'active.json');
  }

  private readActiveConversationId(saveKey: string | null): string | null {
    const path = this.activeFilePath(saveKey);
    if (!path) return null;
    try {
      const parsed = JSON.parse(readFileSync(path, 'utf8')) as { activeConversationId?: unknown };
      const id = parsed.activeConversationId;
      return typeof id === 'string' && id.length > 0 ? id : null;
    } catch {
      return null;
    }
  }

  private readActiveConversationTitle(saveKey: string | null): string | null {
    const path = this.activeFilePath(saveKey);
    if (!path) return null;
    try {
      const parsed = JSON.parse(readFileSync(path, 'utf8')) as { title?: unknown };
      return typeof parsed.title === 'string' && parsed.title.trim().length > 0
        ? parsed.title.trim()
        : null;
    } catch {
      return null;
    }
  }

  private writeActiveConversationId(
    saveKey: string | null,
    id: string,
    title: string | null = null,
  ): void {
    const path = this.activeFilePath(saveKey);
    if (!path) return;
    try {
      mkdirSync(dirname(path), { recursive: true });
      const tmp = `${path}.tmp-${process.pid}`;
      const data = title ? { activeConversationId: id, title } : { activeConversationId: id };
      writeFileSync(tmp, JSON.stringify(data), {
        encoding: 'utf8',
        mode: 0o600,
      });
      renameSync(tmp, path);
    } catch (err) {
      this.logger.warn('写活动会话记录失败', { saveKey, ...errorFields(err) });
    }
  }

  /**
   * 解析当前存档的活动会话。优先 active.json；没有时取最近修改的会话；
   * 一个会话都没有就生成一个空 draft（尚未落盘，第一次提问时才创建文件）。
   */
  private async ensureActiveConversation(saveKey: string | null, pi: PiSdk): Promise<string | null> {
    if (!saveKey) {
      this.activeConversationId = null;
      this.activeConversationDraft = false;
      this.conversationSaveKey = null;
      return null;
    }
    if (this.conversationSaveKey === saveKey && this.activeConversationId) {
      return this.activeConversationId;
    }

    this.conversationSaveKey = saveKey;
    const sessionDir = join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey));
    const stored = this.readActiveConversationId(saveKey);
    if (stored) {
      const file = pi.SessionManager.findById(this.dataDir, stored, sessionDir);
      if (file) {
        this.activeConversationId = stored;
        this.activeConversationDraft = false;
        this.activeConversationDraftTitle = null;
        return stored;
      }
      // active.json 可能指向一个还没落盘的 draft（只改过标题、没发过消息）。
      // 保留它和标题，而不是丢掉玩家刚改的名字。
      this.activeConversationId = stored;
      this.activeConversationDraft = true;
      this.activeConversationDraftTitle = this.readActiveConversationTitle(saveKey);
      this.writeActiveConversationId(saveKey, stored, this.activeConversationDraftTitle);
      return stored;
    }

    let list: Awaited<ReturnType<typeof pi.SessionManager.list>> = [];
    try {
      list = await pi.SessionManager.list(this.dataDir, sessionDir);
    } catch (err) {
      this.logger.warn('列出存档会话失败，按空存档处理', { saveKey, ...errorFields(err) });
    }
    if (list.length > 0) {
      this.activeConversationId = list[0]!.id;
      this.activeConversationDraft = false;
      this.activeConversationDraftTitle = null;
    } else {
      this.activeConversationId = this.newConversationId();
      this.activeConversationDraft = true;
      this.activeConversationDraftTitle = null;
    }
    this.writeActiveConversationId(saveKey, this.activeConversationId);
    return this.activeConversationId;
  }

  private conversationTitle(info: { name?: string; firstMessage: string }): string {
    const explicit = (info.name ?? '').trim();
    if (explicit.length > 0) return explicit.slice(0, 40);
    const first = (info.firstMessage ?? '').replace(/\s+/g, ' ').trim();
    if (first.length === 0) return '新会话';
    return first.length <= 24 ? first : `${first.slice(0, 24)}…`;
  }

  /**
   * 异步生成会话标题。只在第一次成功问答后调用；不 await，避免给主回答增加延迟。
   * 失败静默退回“第一条提问截断”，不影响聊天。
   */
  private scheduleConversationTitle(
    saveKey: string,
    conversationId: string,
    userText: string,
    assistantText: string,
    runtime: ModelRuntime,
    model: Model<Api>,
    emit: AgentEmitter,
  ): void {
    if (this.titlePending.has(conversationId)) return;
    this.titlePending.add(conversationId);
    void this.generateConversationTitle(
      saveKey,
      conversationId,
      userText,
      assistantText,
      runtime,
      model,
      emit,
    )
      .catch((err) => {
        this.logger.warn('生成会话标题失败，保留第一条提问作为标题', {
          conversationId,
          ...errorFields(err),
        });
      })
      .finally(() => {
        this.titlePending.delete(conversationId);
      });
  }

  private async generateConversationTitle(
    saveKey: string,
    conversationId: string,
    userText: string,
    assistantText: string,
    runtime: ModelRuntime,
    model: Model<Api>,
    emit: AgentEmitter,
  ): Promise<void> {
    const response = await runtime.complete(
      model,
      {
        systemPrompt: CONVERSATION_TITLE_SYSTEM_PROMPT,
        messages: [{
          role: 'user',
          content: conversationTitleInput(userText, assistantText),
          timestamp: Date.now(),
        }],
      },
      { maxTokens: 64, temperature: 0.3 },
    );
    if (response.stopReason !== 'stop') return;
    let raw = '';
    for (const block of response.content) {
      if (block.type === 'text') raw += block.text;
    }
    const title = normalizeConversationTitle(raw);
    if (title.length === 0) return;

    const persisted = await this.persistConversationTitle(saveKey, conversationId, title);
    if (!persisted) return;
    this.logger.info('会话标题已生成', { conversationId, title });
    if (this.saveKey === saveKey && emit.isActive()) {
      emit.conversationUpdated?.({ conversationId, title });
    }
  }

  /** 优先走活着的 session（保证 leaf 一致）；已切走则直接打开目标文件追加 session_info。 */
  private async persistConversationTitle(
    saveKey: string,
    conversationId: string,
    title: string,
  ): Promise<boolean> {
    const live = this.slots.get(conversationId)?.session ?? null;
    if (live && live.sessionId === conversationId) {
      // 生成期间玩家可能刚改过名；已有标题时绝不能用模型结果覆盖。
      if ((live.sessionName ?? '').trim().length > 0) return false;
      try {
        live.setSessionName(title);
        return true;
      } catch (err) {
        this.logger.debug('活会话写入标题失败，改走文件', {
          conversationId,
          ...errorFields(err),
        });
      }
    }

    try {
      const pi = await this.loadSdk();
      const sessionDir = this.conversationSessionDir(saveKey);
      const file = pi.SessionManager.findById(this.dataDir, conversationId, sessionDir);
      if (!file) return false;
      const manager = pi.SessionManager.open(file, sessionDir, this.dataDir);
      if ((manager.getSessionName() ?? '').trim().length > 0) return false;
      manager.appendSessionInfo(title);
      return true;
    } catch (err) {
      this.logger.warn('写入会话标题失败', { conversationId, ...errorFields(err) });
      return false;
    }
  }

  private conversationSessionDir(saveKey: string): string {
    return join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey));
  }

  /**
   * 读一个会话的回放记录与文件里保存的标题。只读会话文件，不建模型会话、不联网。
   * 找不到会话时返回空历史（例如新建但还没发过消息的 draft）。
   * 标题只认 session_info（玩家改名和模型总结都写这里）；用第一条提问重新推导，
   * 会让玩家改名在切走再切回时丢失。
   */
  private readConversation(
    pi: PiSdk,
    saveKey: string,
    conversationId: string,
  ): { history: HistoryMessage[]; name: string | null } {
    const sessionDir = this.conversationSessionDir(saveKey);
    const file = pi.SessionManager.findById(this.dataDir, conversationId, sessionDir);
    if (!file) return { history: [], name: null };
    let manager;
    try {
      manager = pi.SessionManager.open(file, sessionDir, this.dataDir);
    } catch (err) {
      this.logger.warn('打开会话文件失败', { saveKey, conversationId, ...errorFields(err) });
      return { history: [], name: null };
    }
    // 聊天记录回放必须用全量 branch：buildContextEntries() 会按 compaction 丢掉
    // 被摘要的旧消息，导致压缩后重进存档旧对话凭空消失。模型上下文由 pi 自己管，
    // 这里只负责 UI 回放。
    const messages = extractHistoryMessages(manager.getBranch());
    const records = this.readBlueprintCards(saveKey);
    const withCards = attachBlueprintCards(messages, records, conversationId);
    const linkRecords = this.readLinkCards(saveKey);
    const withLinks = attachLinkCards(withCards, linkRecords, conversationId);
    const timeRecords = this.readReplyTimes(saveKey);
    const compactionRecords = this.readCompactionTimes(saveKey);
    const withTimes = attachCompactionTimes(
      attachGameTimes(withLinks, timeRecords, conversationId),
      compactionRecords,
      conversationId,
    );
    this.logger.info('读取会话历史', {
      saveKey,
      conversationId,
      messages: withTimes.length,
      blueprintCards: records.length,
      linkCards: linkRecords.length,
      replyTimes: timeRecords.length,
    });
    return { history: withTimes, name: manager.getSessionName() ?? null };
  }

  /** 只取 UI 回放历史；标题走 readConversation。 */
  private readConversationHistory(
    pi: PiSdk,
    saveKey: string,
    conversationId: string,
  ): HistoryMessage[] {
    return this.readConversation(pi, saveKey, conversationId).history;
  }

  private readConversationRecords(
    pi: PiSdk,
    saveKey: string,
    conversationId: string,
    knownHistory?: HistoryMessage[],
  ): ConversationRecord[] {
    const history = knownHistory ?? this.readConversationHistory(pi, saveKey, conversationId);
    const out: ConversationRecord[] = [];
    for (const message of history) {
      if (message.role !== 'user' || message.text.trim().length === 0) continue;
      out.push({
        text: message.text,
        timestamp: message.timestamp ?? 0,
        ...(message.gameTime ? { gameTime: message.gameTime } : {}),
      });
      if (out.length >= MAX_CONVERSATION_RECORDS) break;
    }
    return out;
  }

  private conversationInfoFrom(
    pi: PiSdk,
    saveKey: string,
    info: {
      id: string;
      name?: string;
      firstMessage: string;
      created: Date;
      modified: Date;
      history?: HistoryMessage[];
    },
    meta?: { pins?: Record<string, number>; replyTicks?: Map<string, number> },
  ): ConversationInfo {
    const records = this.readConversationRecords(pi, saveKey, info.id, info.history);
    // 缺省就从盘上读一次：切换/新建会话的回执也要带上 pinned / lastReplyTick，
    // 否则 mod 拿这条回执覆盖列表项时会把置顶和时间标记丢掉。
    const pins = meta ? meta.pins : this.readConversationPins(saveKey);
    const replyTicks = meta ? meta.replyTicks : lastReplyTicks(this.readReplyTimes(saveKey));
    const replyTick = replyTicks?.get(info.id);
    return {
      id: info.id,
      title: this.conversationTitle(info),
      createdAt: info.created.getTime(),
      updatedAt: info.modified.getTime(),
      recordCount: records.length,
      records,
      ...(pins?.[info.id] !== undefined ? { pinned: true } : {}),
      ...(typeof replyTick === 'number' ? { lastReplyTick: replyTick } : {}),
    };
  }

  private draftConversationInfo(conversationId: string, title: string | null = null): ConversationInfo {
    const now = Date.now();
    return {
      id: conversationId,
      title: title && title.trim().length > 0 ? title.trim() : '新会话',
      createdAt: now,
      updatedAt: now,
      recordCount: 0,
      records: [],
    };
  }

  // ---------------------------------------------------------------- A.14 设定页

  private savesIndexPath(): string {
    return join(this.dataDir, 'saves.json');
  }

  /** 读存档显示名索引；损坏/缺条目一律按无索引处理。 */
  private readSavesIndex(): Record<string, { name: string; lastSeen: number }> {
    const out: Record<string, { name: string; lastSeen: number }> = Object.create(null) as Record<
      string,
      { name: string; lastSeen: number }
    >;
    try {
      const parsed = JSON.parse(readFileSync(this.savesIndexPath(), 'utf8')) as unknown;
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return out;
      for (const [key, raw] of Object.entries(parsed as Record<string, unknown>)) {
        if (typeof raw !== 'object' || raw === null) continue;
        const row = raw as Record<string, unknown>;
        if (typeof row['name'] !== 'string' || row['name'].trim().length === 0) continue;
        const lastSeen = typeof row['lastSeen'] === 'number' && Number.isFinite(row['lastSeen'])
          ? row['lastSeen']
          : 0;
        out[key] = { name: row['name'].trim(), lastSeen };
      }
    } catch {
      // 不存在或损坏：无索引即可，不影响读取会话。
    }
    return out;
  }

  /** A.14.6：只在 save.bind 且有显示名时写；读-改-写，保留其它存档。 */
  noteSaveBinding(saveKey: string | null, saveName: string | null): void {
    if (!saveKey || !saveName || saveName.trim().length === 0) return;
    const dirKey = this.sessionDirNameFor(saveKey);
    const index = this.readSavesIndex();
    index[dirKey] = { name: saveName.trim().slice(0, 64), lastSeen: Date.now() };
    try {
      mkdirSync(this.dataDir, { recursive: true, mode: 0o700 });
      const path = this.savesIndexPath();
      const tmp = `${path}.tmp-${process.pid}`;
      writeFileSync(tmp, `${JSON.stringify(index, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 });
      renameSync(tmp, path);
    } catch (err) {
      this.logger.warn('写入 saves.json 失败', errorFields(err));
    }
  }

  /**
   * 会话的创建/修改时间：创建时间取 session 文件头里的 timestamp（首行 JSON），
   * 修改时间取文件 mtime。切换会话不能用 `new Date()` 伪造——这两个值参与左栏排序，
   * 伪造会让“点一下会话”改变列表顺序。
   */
  private sessionTimes(filePath: string | null | undefined): { created: Date; modified: Date } {
    const now = new Date();
    if (!filePath) return { created: now, modified: now };
    let created = now;
    try {
      const fd = openSync(filePath, 'r');
      try {
        const buffer = Buffer.alloc(1024);
        const read = readSync(fd, buffer, 0, buffer.length, 0);
        const line = buffer.subarray(0, read).toString('utf8').split('\n', 1)[0] ?? '';
        const header = JSON.parse(line) as { timestamp?: unknown };
        if (typeof header.timestamp === 'string') {
          const ms = Date.parse(header.timestamp);
          if (Number.isFinite(ms)) created = new Date(ms);
        }
      } finally {
        closeSync(fd);
      }
    } catch {
      // 读不到就退回 now；排序仍可用（同分按稳定顺序）。
    }
    let modified = now;
    try {
      modified = statSync(filePath).mtime;
    } catch {
      // 同上。
    }
    return { created, modified };
  }

  private conversationPinsPath(saveKey: string | null): string | null {
    if (!saveKey) return null;
    return join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey), 'conversation-pins.json');
  }

  /** 读置顶记录：会话 id → 置顶时间（毫秒）。损坏/缺失都按“没有置顶”处理。 */
  private readConversationPins(saveKey: string | null): Record<string, number> {
    const path = this.conversationPinsPath(saveKey);
    if (!path || !existsSync(path)) return {};
    let parsed: unknown;
    try {
      parsed = JSON.parse(readFileSync(path, 'utf8'));
    } catch {
      return {};
    }
    if (typeof parsed !== 'object' || parsed === null) return {};
    const pins = (parsed as { pins?: unknown }).pins;
    if (typeof pins !== 'object' || pins === null || Array.isArray(pins)) return {};
    const out: Record<string, number> = Object.create(null) as Record<string, number>;
    for (const [id, value] of Object.entries(pins as Record<string, unknown>)) {
      if (!id) continue;
      out[id] = typeof value === 'number' && Number.isFinite(value) ? value : 0;
    }
    return out;
  }

  private writeConversationPins(saveKey: string, pins: Record<string, number>): boolean {
    const path = this.conversationPinsPath(saveKey);
    if (!path) return false;
    const tmp = `${path}.tmp-${process.pid}`;
    try {
      mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
      writeFileSync(tmp, `${JSON.stringify({ pins }, null, 2)}\n`, {
        encoding: 'utf8',
        mode: 0o600,
      });
      renameSync(tmp, path);
      return true;
    } catch (err) {
      try { unlinkSync(tmp); } catch { /* ignore */ }
      this.logger.warn('写会话置顶记录失败', { saveKey, ...errorFields(err) });
      return false;
    }
  }

  /** 删除会话时顺手清掉置顶记录，避免会话 id 复用时冒出一个置顶的陌生会话。 */
  private removeConversationPin(saveKey: string, conversationId: string): void {
    const pins = this.readConversationPins(saveKey);
    if (pins[conversationId] === undefined) return;
    delete pins[conversationId];
    this.writeConversationPins(saveKey, pins);
  }

  /** A.13：置顶只影响排序；会话不存在时返回 conversation_not_found。 */
  async pinConversation(
    saveKey: string | null,
    conversationId: string,
    pinned: boolean,
  ): Promise<{ ok: boolean; error?: string }> {
    if (!saveKey) return { ok: false, error: 'no_save' };
    if (!conversationId) return { ok: false, error: 'conversation_not_found' };
    const pi = await this.loadSdk();
    const sessionDir = this.conversationSessionDir(saveKey);
    // findById 找不到时返回 undefined（不是 null），两者都要按“不存在”处理。
    const file = pi.SessionManager.findById(this.dataDir, conversationId, sessionDir);
    const exists = Boolean(file) || this.readActiveConversationId(saveKey) === conversationId;
    if (!exists) return { ok: false, error: 'conversation_not_found' };

    const pins = this.readConversationPins(saveKey);
    if (pinned) pins[conversationId] = Date.now();
    else delete pins[conversationId];
    if (!this.writeConversationPins(saveKey, pins)) return { ok: false, error: 'io_error' };
    return { ok: true };
  }

  /** 只读 active.json；索引不能用 ensureActiveConversation（它会写盘）。 */
  private readActiveConversationRaw(sessionDir: string): { id: string | null; title: string | null } {
    try {
      const parsed = JSON.parse(readFileSync(join(sessionDir, 'active.json'), 'utf8')) as Record<
        string,
        unknown
      >;
      const id = typeof parsed['activeConversationId'] === 'string'
        ? parsed['activeConversationId']
        : null;
      const title = typeof parsed['title'] === 'string' && parsed['title'].trim().length > 0
        ? parsed['title'].trim()
        : null;
      return { id, title };
    } catch {
      return { id: null, title: null };
    }
  }

  /**
   * A.14.2：扫描 `sessions/` 下的全部存档目录，返回只读索引。
   * 不调用 ensureActiveConversation，不写 active.json，不切换当前状态。
   */
  async conversationIndex(): Promise<{
    currentSaveKey: string | null;
    saves: ConversationSaveGroup[];
  }> {
    const pi = await this.loadSdk();
    const savesIndex = this.readSavesIndex();
    const root = join(this.dataDir, 'sessions');
    const currentDir = this.saveKey ? this.sessionDirNameFor(this.saveKey) : null;
    const out: ConversationSaveGroup[] = [];

    let dirs: string[] = [];
    try {
      dirs = readdirSync(root, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name);
    } catch {
      return { currentSaveKey: this.saveKey, saves: [] };
    }

    // 最近活动的存档排前面；按目录 mtime 粗排，避免每个都先读一遍。
    const ranked = dirs
      .filter((name) => name !== 'menu')
      .map((name) => {
        try {
          return { name, mtime: statSync(join(root, name)).mtimeMs };
        } catch {
          return { name, mtime: 0 };
        }
      })
      .sort((a, b) => b.mtime - a.mtime)
      .slice(0, MAX_INDEX_SAVES);

    for (const { name: dirKey } of ranked) {
      const sessionDir = join(root, dirKey);
      const active = this.readActiveConversationRaw(sessionDir);
      let list: Awaited<ReturnType<typeof pi.SessionManager.list>> = [];
      try {
        list = await pi.SessionManager.list(this.dataDir, sessionDir);
      } catch (err) {
        this.logger.warn('索引会话失败，按空存档处理', { saveKey: dirKey, ...errorFields(err) });
      }

      const conversations: ConversationIndexEntry[] = [];
      const seen = new Set<string>();
      let newest = 0;
      for (const info of list) {
        if (conversations.length >= MAX_CONVERSATIONS) break;
        // 索引只用 id/标题/时间/条数，不需要置顶与最后回复时刻，传空 meta 省掉两遍读盘。
        const built = this.conversationInfoFrom(pi, dirKey, info, {});
        conversations.push({
          id: built.id,
          title: built.title,
          updatedAt: built.updatedAt,
          messageCount: built.recordCount,
          isActive: built.id === active.id,
        });
        seen.add(built.id);
        newest = Math.max(newest, built.updatedAt);
      }
      if (active.id && !seen.has(active.id)) {
        conversations.unshift({
          id: active.id,
          title: active.title ?? '新会话',
          updatedAt: Date.now(),
          messageCount: 0,
          isActive: true,
        });
        newest = Math.max(newest, Date.now());
      }
      conversations.sort((a, b) => b.updatedAt - a.updatedAt);

      const known = savesIndex[dirKey];
      out.push({
        saveKey: dirKey,
        saveName: known?.name ?? `存档 ${dirKey.slice(0, 8)}`,
        lastSeen: known?.lastSeen ?? newest,
        current: dirKey === currentDir,
        conversations,
      });
    }

    out.sort((a, b) => b.lastSeen - a.lastSeen);
    return { currentSaveKey: this.saveKey, saves: out };
  }

  /**
   * A.14.3：导出单个会话为 Markdown。只读会话文件，不改变活动会话。
   */
  async exportConversation(
    saveKey: string | null,
    conversationId: string,
  ): Promise<{ ok: boolean; path?: string; bytes?: number; messageCount?: number; error?: string }> {
    if (!saveKey) return { ok: false, error: 'no_save' };
    if (!conversationId) return { ok: false, error: 'conversation_not_found' };
    const pi = await this.loadSdk();
    const sessionDir = this.conversationSessionDir(saveKey);
    if (!pi.SessionManager.findById(this.dataDir, conversationId, sessionDir)) {
      return { ok: false, error: 'conversation_not_found' };
    }
    const { history, name } = this.readConversation(pi, saveKey, conversationId);
    if (history.length === 0) return { ok: false, error: 'empty' };

    const saveName =
      this.readSavesIndex()[this.sessionDirNameFor(saveKey)]?.name ??
      `存档 ${this.sessionDirNameFor(saveKey).slice(0, 8)}`;
    const firstUser = history.find((message) => message.role === 'user' && message.text.trim());
    const title = (name ?? '').trim() || firstUser?.text.trim() || '会话';
    const targetDir = join(this.dataDir, 'exports', this.sessionDirNameFor(saveKey));
    try {
      mkdirSync(targetDir, { recursive: true, mode: 0o700 });
    } catch (err) {
      this.logger.warn('创建导出目录失败', errorFields(err));
      return { ok: false, error: 'io_error' };
    }

    const stamp = formatExportStamp(new Date());
    const base = sanitizeExportName(title, 60) || 'conversation';
    let path = join(targetDir, `${base}-${stamp}.md`);
    for (let index = 2; existsSync(path) && index < 1000; index += 1) {
      path = join(targetDir, `${base}-${stamp}-${index}.md`);
    }

    const markdown = renderConversationMarkdown({
      title: title.slice(0, 120),
      saveName,
      saveKey,
      exportedAt: new Date(),
      history,
    });
    try {
      writeFileSync(path, markdown, { encoding: 'utf8', mode: 0o600 });
    } catch (err) {
      this.logger.warn('导出会话失败', { saveKey, conversationId, ...errorFields(err) });
      return { ok: false, error: 'io_error' };
    }
    this.logger.info('会话已导出', { saveKey, conversationId, path, bytes: Buffer.byteLength(markdown) });
    return {
      ok: true,
      path,
      bytes: Buffer.byteLength(markdown, 'utf8'),
      messageCount: history.length,
    };
  }

  async getConversations(saveKey: string | null): Promise<ConversationInfo[]> {
    if (!saveKey) return [];
    const pi = await this.loadSdk();
    const activeId = await this.ensureActiveConversation(saveKey, pi);
    const sessionDir = this.conversationSessionDir(saveKey);
    let list: Awaited<ReturnType<typeof pi.SessionManager.list>> = [];
    try {
      list = await pi.SessionManager.list(this.dataDir, sessionDir);
    } catch (err) {
      this.logger.warn('列出存档会话失败', { saveKey, ...errorFields(err) });
    }

    const meta = {
      pins: this.readConversationPins(saveKey),
      replyTicks: lastReplyTicks(this.readReplyTimes(saveKey)),
    };
    const out: ConversationInfo[] = [];
    const seen = new Set<string>();
    for (const info of list) {
      if (out.length >= MAX_CONVERSATIONS) break;
      out.push(this.conversationInfoFrom(pi, saveKey, info, meta));
      seen.add(info.id);
    }
    if (activeId && !seen.has(activeId)) {
      out.unshift(this.draftConversationInfo(activeId, this.activeConversationDraftTitle));
    }
    out.sort(compareConversations);
    return out;
  }

  async getHistory(saveKey: string | null): Promise<HistoryMessage[]> {
    if (!saveKey) return [];
    const pi = await this.loadSdk();
    const conversationId = await this.ensureActiveConversation(saveKey, pi);
    if (!conversationId) return [];
    return this.readConversationHistory(pi, saveKey, conversationId);
  }

  async getHistoryFor(saveKey: string | null, conversationId: string): Promise<HistoryMessage[]> {
    if (!saveKey || !conversationId) return [];
    const pi = await this.loadSdk();
    return this.readConversationHistory(pi, saveKey, conversationId);
  }

  async createConversation(saveKey: string | null): Promise<{
    ok: boolean;
    conversation?: ConversationInfo;
    history?: HistoryMessage[];
    error?: string;
  }> {
    if (!saveKey) return { ok: false, error: 'no_save' };
    const pi = await this.loadSdk();
    const activeId = await this.ensureActiveConversation(saveKey, pi);

    // 当前会话本来就是空的：复用它，避免每次点“新会话”都留下一个空 draft。
    if (activeId && this.activeConversationDraft) {
      return {
        ok: true,
        conversation: this.draftConversationInfo(activeId, this.activeConversationDraftTitle),
        history: [],
      };
    }

    const id = this.newConversationId();
    this.activeConversationId = id;
    this.activeConversationDraft = true;
    this.activeConversationDraftTitle = null;
    this.writeActiveConversationId(saveKey, id);
    return { ok: true, conversation: this.draftConversationInfo(id), history: [] };
  }

  async switchConversation(saveKey: string | null, conversationId: string): Promise<{
    ok: boolean;
    conversation?: ConversationInfo;
    history?: HistoryMessage[];
    /** 切换前那个没落盘的空会话已被丢弃时，回传它的 id（A.13）。 */
    discardedConversationId?: string;
    error?: string;
  }> {
    if (!saveKey) return { ok: false, error: 'no_save' };
    const pi = await this.loadSdk();
    const activeId = await this.ensureActiveConversation(saveKey, pi);
    if (conversationId === activeId) {
      const { history, name } = this.readConversation(pi, saveKey, conversationId);
      const sameFile = pi.SessionManager.findById(this.dataDir, conversationId,
        this.conversationSessionDir(saveKey));
      const times = this.sessionTimes(sameFile);
      const conversation = this.activeConversationDraft
        ? this.draftConversationInfo(conversationId, this.activeConversationDraftTitle)
        : this.conversationInfoFrom(pi, saveKey, {
            id: conversationId,
            name: name ?? undefined,
            firstMessage: history.find((m) => m.role === 'user')?.text ?? '',
            created: times.created,
            modified: times.modified,
            history,
          });
      return { ok: true, conversation, history };
    }

    const sessionDir = this.conversationSessionDir(saveKey);
    const file = pi.SessionManager.findById(this.dataDir, conversationId, sessionDir);
    if (!file) return { ok: false, error: 'conversation_not_found' };

    // 空会话只在它是活动会话时存在：切走时它没有 session 文件可回，直接丢弃。
    // 不丢的话左侧会留下一个点不开的行（切回去只会得到 conversation_not_found）。
    // 判据是「文件不存在」而不是只看 draft 标记：首轮 prompt 抛错时标记可能还是 true，
    // 但文件已经写出来了，那种会话切得回去，不能删它的行。
    const discardable =
      this.activeConversationDraft &&
      activeId !== null &&
      !pi.SessionManager.findById(this.dataDir, activeId, sessionDir);
    const discardedConversationId = discardable ? activeId : null;
    this.activeConversationId = conversationId;
    this.activeConversationDraft = false;
    this.activeConversationDraftTitle = null;
    this.writeActiveConversationId(saveKey, conversationId);
    const { history, name } = this.readConversation(pi, saveKey, conversationId);
    const times = this.sessionTimes(file);
    const conversation = this.conversationInfoFrom(pi, saveKey, {
      id: conversationId,
      name: name ?? undefined,
      firstMessage: history.find((m) => m.role === 'user')?.text ?? '',
      created: times.created,
      modified: times.modified,
      history,
    });
    return {
      ok: true,
      conversation,
      history,
      ...(discardedConversationId ? { discardedConversationId } : {}),
    };
  }

  async renameConversation(saveKey: string | null, conversationId: string, rawTitle: string): Promise<{
    ok: boolean;
    title?: string;
    error?: string;
  }> {
    if (!saveKey) return { ok: false, error: 'no_save' };
    const title = normalizeConversationRename(rawTitle);
    if (!title) return { ok: false, error: 'invalid_title' };

    const pi = await this.loadSdk();
    // A.14.4：非当前存档只读解析活动会话，不能把别人的存档切成当前会话。
    const isCurrent = saveKey === this.saveKey;
    const activeId = isCurrent
      ? await this.ensureActiveConversation(saveKey, pi)
      : this.readActiveConversationId(saveKey);
    const sessionDir = this.conversationSessionDir(saveKey);
    const file = pi.SessionManager.findById(this.dataDir, conversationId, sessionDir);
    if (!file && conversationId !== activeId) return { ok: false, error: 'conversation_not_found' };

    // 空 draft 还没有 session 文件：标题先存在 active.json，等第一次提问时再写
    // 进 session_info（pi 在没有 user/assistant 消息前不会落盘）。
    if (!file && conversationId === activeId && (isCurrent ? this.activeConversationDraft : true)) {
      if (isCurrent) this.activeConversationDraftTitle = title;
      this.writeActiveConversationId(saveKey, conversationId, title);
      return { ok: true, title };
    }

    // 活动会话还活着：直接走 session manager，保证 leaf/parent 一致。
    const live = isCurrent ? this.slots.get(conversationId)?.session ?? null : null;
    if (live && live.sessionId === conversationId) {
      try {
        live.setSessionName(title);
        return { ok: true, title };
      } catch (err) {
        this.logger.debug('活会话重命名失败，改走文件', {
          conversationId,
          ...errorFields(err),
        });
      }
    }

    if (file) {
      try {
        const manager = pi.SessionManager.open(file, sessionDir, this.dataDir);
        manager.appendSessionInfo(title);
        return { ok: true, title };
      } catch (err) {
        this.logger.warn('重命名会话失败', { conversationId, ...errorFields(err) });
        return { ok: false, error: 'io_error' };
      }
    }

    // 理论到不了：既没有文件、又不是活动 draft，说明会话已被删除。
    return { ok: false, error: 'conversation_not_found' };
  }

  async deleteConversation(saveKey: string | null, conversationId: string): Promise<{
    ok: boolean;
    error?: string;
    activeConversationId?: string | null;
  }> {
    if (!saveKey) return { ok: false, error: 'no_save' };
    if (this.isRunning(conversationId)) {
      return { ok: false, error: 'conversation_busy' };
    }
    const pi = await this.loadSdk();
    const isCurrent = saveKey === this.saveKey;
    const activeId = isCurrent
      ? await this.ensureActiveConversation(saveKey, pi)
      : this.readActiveConversationId(saveKey);
    const sessionDir = this.conversationSessionDir(saveKey);
    const file = pi.SessionManager.findById(this.dataDir, conversationId, sessionDir);
    const isActive = conversationId === activeId;
    if (!file && !isActive) return { ok: false, error: 'conversation_not_found' };

    this.disposeSlot(conversationId);
    if (file) {
      try {
        unlinkSync(file);
      } catch (err) {
        this.logger.warn('删除会话文件失败', { saveKey, conversationId, ...errorFields(err) });
        return { ok: false, error: 'io_error' };
      }
    }
    this.removeConversationPin(saveKey, conversationId);

    if (!isActive) {
      return { ok: true, activeConversationId: activeId };
    }

    // 非当前存档：只改那个存档的 active.json，不动当前会话/内存状态。
    if (!isCurrent) {
      let list: Awaited<ReturnType<typeof pi.SessionManager.list>> = [];
      try {
        list = await pi.SessionManager.list(this.dataDir, sessionDir);
      } catch (err) {
        this.logger.warn('删除会话后列出剩余会话失败', { saveKey, ...errorFields(err) });
      }
      const next = list[0]?.id ?? this.newConversationId();
      this.writeActiveConversationId(saveKey, next);
      return { ok: true, activeConversationId: next };
    }

    // 删掉的是活动会话：优先切到最近修改的剩余会话；没有就新建空 draft。
    this.activeConversationId = null;
    this.activeConversationDraft = false;
    this.activeConversationDraftTitle = null;
    let list: Awaited<ReturnType<typeof pi.SessionManager.list>> = [];
    try {
      list = await pi.SessionManager.list(this.dataDir, sessionDir);
    } catch (err) {
      this.logger.warn('删除会话后列出剩余会话失败', { saveKey, ...errorFields(err) });
    }
    if (list.length > 0) {
      this.activeConversationId = list[0]!.id;
      this.activeConversationDraft = false;
      this.activeConversationDraftTitle = null;
    } else {
      this.activeConversationId = this.newConversationId();
      this.activeConversationDraft = true;
      this.activeConversationDraftTitle = null;
    }
    this.writeActiveConversationId(saveKey, this.activeConversationId);
    return { ok: true, activeConversationId: this.activeConversationId };
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
    conversationId?: string | null,
    gameTick?: number | null,
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
            ...(conversationId ? { conversationId } : {}),
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
          ...(typeof parsed.conversationId === 'string' && parsed.conversationId.trim()
            ? { conversationId: parsed.conversationId }
            : {}),
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
    conversationId?: string | null,
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
            ...(conversationId ? { conversationId } : {}),
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
          ...(typeof parsed.conversationId === 'string' && parsed.conversationId.trim()
            ? { conversationId: parsed.conversationId }
            : {}),
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
  private removeLinkCardRecords(
    saveKey: string,
    prompt: string,
    conversationId?: string | null,
  ): void {
    const path = this.linkCardLogPath(saveKey);
    const text = prompt.trim();
    if (!path || !text || !existsSync(path)) return;
    const records = this.readLinkCards(saveKey);
    const kept = records.filter(
      (record) => !(record.prompt.trim() === text && recordTargetsConversation(record, conversationId)),
    );
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
  recordReplyTime(
    saveKey: string | null,
    prompt: string,
    reply: string,
    gameTime: string,
    conversationId?: string | null,
    gameTick?: number | null,
  ): void {
    const path = this.replyTimeLogPath(saveKey);
    const text = prompt.trim();
    const answer = (reply ?? '').trim();
    const time = gameTime.trim();
    if (!path || !text || !time) return;

    try {
      mkdirSync(dirname(path), { recursive: true });
      const record: GameTimeRecord = {
        ...(conversationId ? { conversationId } : {}),
        prompt: text.slice(0, 4000),
        ...(answer ? { reply: answer.slice(0, 4000) } : {}),
        gameTime: time.slice(0, 16),
        ...(typeof gameTick === 'number' && Number.isFinite(gameTick) && gameTick >= 0
          ? { gameTick }
          : {}),
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
          ...(typeof parsed.conversationId === 'string' && parsed.conversationId.trim()
            ? { conversationId: parsed.conversationId }
            : {}),
          prompt: parsed.prompt,
          ...(typeof parsed.reply === 'string' && parsed.reply.trim()
            ? { reply: parsed.reply }
            : {}),
          gameTime: parsed.gameTime,
          ...(typeof parsed.gameTick === 'number' && Number.isFinite(parsed.gameTick)
            ? { gameTick: parsed.gameTick }
            : {}),
          ...(typeof parsed.timestamp === 'number' ? { timestamp: parsed.timestamp } : {}),
        });
      } catch {
        // 坏行跳过，不因一条记录损坏丢掉整段历史。
      }
    }
    return records;
  }

  private compactionTimeLogPath(saveKey: string | null): string | null {
    if (!saveKey) return null;
    return join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey), 'compaction-times.jsonl');
  }

  /** 回存压缩记录的游戏时间（协议 A.11）：压缩时间只用于回放显示，不写进模型上下文。 */
  recordCompactionTime(saveKey: string | null, record: CompactionTimeRecord): void {
    const path = this.compactionTimeLogPath(saveKey);
    const gameTime = (record.gameTime ?? '').trim();
    if (!path || !gameTime) return;

    try {
      mkdirSync(dirname(path), { recursive: true });
      const item: CompactionTimeRecord = {
        ...(record.compactionId ? { compactionId: record.compactionId } : {}),
        ...(record.conversationId ? { conversationId: record.conversationId } : {}),
        gameTime,
        ...(typeof record.gameTick === 'number' && Number.isFinite(record.gameTick)
          ? { gameTick: record.gameTick }
          : {}),
        timestamp: Date.now(),
      };
      appendFileSync(path, `${JSON.stringify(item)}\n`, { encoding: 'utf8', mode: 0o600 });
    } catch (err) {
      this.logger.warn('写压缩记录游戏时间失败', { saveKey, ...errorFields(err) });
    }
  }

  private readCompactionTimes(saveKey: string | null): CompactionTimeRecord[] {
    const path = this.compactionTimeLogPath(saveKey);
    if (!path || !existsSync(path)) return [];
    let raw: string;
    try {
      raw = readFileSync(path, 'utf8');
    } catch (err) {
      this.logger.warn('读压缩记录游戏时间失败', { saveKey, ...errorFields(err) });
      return [];
    }

    const records: CompactionTimeRecord[] = [];
    for (const line of raw.split('\n')) {
      if (!line.trim()) continue;
      try {
        const parsed = JSON.parse(line) as Partial<CompactionTimeRecord>;
        if (typeof parsed.gameTime !== 'string' || !parsed.gameTime.trim()) continue;
        records.push({
          ...(typeof parsed.compactionId === 'string' && parsed.compactionId
            ? { compactionId: parsed.compactionId }
            : {}),
          ...(typeof parsed.conversationId === 'string' && parsed.conversationId.trim()
            ? { conversationId: parsed.conversationId }
            : {}),
          gameTime: parsed.gameTime,
          ...(typeof parsed.gameTick === 'number' && Number.isFinite(parsed.gameTick)
            ? { gameTick: parsed.gameTick }
            : {}),
          ...(typeof parsed.timestamp === 'number' ? { timestamp: parsed.timestamp } : {}),
        });
      } catch {
        // 坏行跳过：时间只是展示信息，不因一条记录损坏丢掉整段历史。
      }
    }
    return records;
  }

  private removeReplyTimeRecords(
    saveKey: string,
    prompt: string,
    conversationId?: string | null,
  ): void {
    const path = this.replyTimeLogPath(saveKey);
    const text = prompt.trim();
    if (!path || !text || !existsSync(path)) return;

    const records = this.readReplyTimes(saveKey);
    const kept = records.filter(
      (record) => !(record.prompt.trim() === text && recordTargetsConversation(record, conversationId)),
    );
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
  private removeBlueprintCardRecords(
    saveKey: string,
    prompt: string,
    conversationId?: string | null,
  ): void {
    const path = this.blueprintCardLogPath(saveKey);
    const text = prompt.trim();
    if (!path || !text || !existsSync(path)) return;

    const records = this.readBlueprintCards(saveKey);
    const kept = records.filter(
      (record) => !(record.prompt.trim() === text && recordTargetsConversation(record, conversationId)),
    );
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
    conversationId?: string | null,
  ): Promise<{ ok: boolean; removed: number; error?: string }> {
    if (!saveKey) return { ok: false, removed: 0, error: 'no_save' };
    if (conversationId ? this.isRunning(conversationId) : this.isRunning()) {
      return { ok: false, removed: 0, error: 'busy' };
    }

    const file = await this.resolveSessionFileForWrite(saveKey, conversationId);
    if (!file) {
      return {
        ok: false,
        removed: 0,
        error: conversationId ? 'conversation_not_found' : 'turn_not_found',
      };
    }

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
    this.removeBlueprintCardRecords(saveKey, text, conversationId);
    this.removeLinkCardRecords(saveKey, text, conversationId);
    this.removeReplyTimeRecords(saveKey, text, conversationId);

    // 只丢弃被改写的会话；删别的会话不应影响当前内存上下文。
    const targetId = conversationId ?? this.activeConversationId;
    if (this.saveKey === saveKey && targetId) this.disposeSlot(targetId);
    this.logger.info('已删除对话回合', {
      saveKey,
      conversationId: conversationId ?? null,
      removed: outcome.removed,
      file,
    });
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
    conversationId?: string | null,
  ): Promise<{
    ok: boolean;
    removed: number;
    compactionId?: string;
    error?: string;
    mayRecompact?: boolean;
  }> {
    if (!saveKey) return { ok: false, removed: 0, error: 'no_save' };
    if (conversationId ? this.isRunning(conversationId) : this.isRunning()) {
      return { ok: false, removed: 0, error: 'busy' };
    }

    const file = await this.resolveSessionFileForWrite(saveKey, conversationId);
    if (!file) {
      return {
        ok: false,
        removed: 0,
        error: conversationId ? 'conversation_not_found' : 'not_found',
      };
    }

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

    const targetId = conversationId ?? this.activeConversationId;
    if (this.saveKey === saveKey && targetId) this.disposeSlot(targetId);
    this.logger.info('已撤销上下文压缩', {
      saveKey,
      conversationId: conversationId ?? null,
      compactionId: outcome.compactionId ?? null,
      file,
    });
    return { ...outcome, mayRecompact: true };
  }

  /**
   * 解析要改写的 session 文件。
   *
   * 指定 conversationId 时只认该会话；找不到返回 null，调用方回
   * `conversation_not_found`。不指定时保持旧行为：取目录里最新修改的 session 文件，
   * 让没有 conversationId 的老 mod 继续工作。
   */
  private async resolveSessionFileForWrite(
    saveKey: string,
    conversationId?: string | null,
  ): Promise<string | null> {
    const dir = join(this.dataDir, 'sessions', this.sessionDirNameFor(saveKey));
    if (!existsSync(dir)) return null;

    if (conversationId) {
      const pi = await this.loadSdk();
      return pi.SessionManager.findById(this.dataDir, conversationId, dir) ?? null;
    }

    const files = readdirSync(dir).filter((name) => isSessionFile(name));
    if (files.length === 0) return null;

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
    return join(dir, newest);
  }

  dispose(): void {
    this.teardown();
  }

  private applyPendingTeardown(): void {
    if (!this.pendingTeardown || this.isRunning()) return;
    this.pendingTeardown = false;
    this.teardown();
  }

  private disposeSlot(conversationId: string): void {
    const slot = this.slots.get(conversationId);
    if (!slot) return;
    this.slots.delete(conversationId);
    this.runQueues.delete(conversationId);
    try {
      slot.unsubscribe();
    } catch (err) {
      this.logger.debug('取消事件订阅失败', errorFields(err));
    }
    try {
      slot.session.dispose();
    } catch (err) {
      this.logger.debug('释放 pi 会话失败', errorFields(err));
    }
    this.logger.info('已释放会话运行态', { conversationId });
  }

  private teardown(): void {
    for (const conversationId of [...this.slots.keys()]) this.disposeSlot(conversationId);
    this.runQueues.clear();
    this.startingRuns.clear();
    this.pendingAborts.clear();
    this.pendingTeardown = false;
  }

  /** 进程级并发闸门：最多同时跑 maxParallelRuns 个会话。 */
  private async acquireRunSlot(): Promise<void> {
    if (this.activeRuns < this.maxParallelRuns) {
      this.activeRuns += 1;
      return;
    }
    await new Promise<void>((resolve) => this.runWaiters.push(resolve));
    this.activeRuns += 1;
  }

  private releaseRunSlot(): void {
    this.activeRuns = Math.max(0, this.activeRuns - 1);
    const next = this.runWaiters.shift();
    if (next) next();
  }

  run(
    text: string,
    promptId: string | null,
    emit: AgentEmitter,
    requiredTool: string | null = null,
    conversationId: string | null = null,
  ): Promise<void> {
    let target = conversationId ?? this.activeConversationId;
    if (!target) {
      // 主菜单/未绑定存档也允许直接聊天：创建一个只存在于本进程/本目录的会话。
      target = this.newConversationId();
      this.activeConversationId = target;
      this.activeConversationDraft = true;
      this.activeConversationDraftTitle = null;
    }

    const task = async (): Promise<void> => {
      this.startingRuns.add(target);
      await this.acquireRunSlot();
      try {
        await this.runOnce(target, text, promptId, emit, requiredTool);
      } finally {
        this.startingRuns.delete(target);
        this.pendingAborts.delete(target);
        this.releaseRunSlot();
        this.applyPendingTeardown();
      }
    };
    const previous = this.runQueues.get(target) ?? Promise.resolve();
    const next = previous.then(task, task);
    this.runQueues.set(target, next.catch(() => undefined));
    return next;
  }

  /** A.15：指定会话是否有回合正在跑；不传 = 任意会话。 */
  isRunning(conversationId?: string | null): boolean {
    if (conversationId) {
      return this.slots.get(conversationId)?.run != null || this.startingRuns.has(conversationId);
    }
    return this.startingRuns.size > 0 || [...this.slots.values()].some((slot) => slot.run);
  }

  /** 切回一个正在跑的会话时，用已生成的正文补上切走期间丢失的显示。 */
  getProgress(conversationId: string): { text: string; thinking: string; running: boolean } {
    const slot = this.slots.get(conversationId);
    if (!slot?.run) return { text: '', thinking: '', running: false };
    return {
      text: slot.run.text,
      thinking: slot.run.thinking,
      running: true,
    };
  }

  /** A.15：中断指定会话；缺省为当前焦点会话。 */
  async abort(conversationId?: string | null): Promise<boolean> {
    const target = conversationId ?? this.activeConversationId;
    if (!target) return false;
    if (this.startingRuns.has(target) && !this.slots.get(target)?.run) {
      this.pendingAborts.add(target);
      return true;
    }
    const slot = this.slots.get(target);
    if (!slot?.run) return false;
    slot.abortRequested = true;
    slot.run.stopReason = 'aborted';
    try {
      await slot.session.abort();
      return true;
    } catch (err) {
      this.logger.warn('中断会话失败', { conversationId: target, ...errorFields(err) });
      return false;
    }
  }

  /**
   * A.15：插话到指定会话的当前回合。没有回合在跑时返回 false。
   */
  async steer(text: string, conversationId?: string | null): Promise<boolean> {
    const target = conversationId ?? this.activeConversationId;
    if (!target) return false;
    const slot = this.slots.get(target);
    if (!slot?.run || slot.abortRequested) return false;
    try {
      await slot.session.steer(text);
      this.logger.info('插话已投递', { conversationId: target, chars: text.length });
      return true;
    } catch (err) {
      this.logger.warn('插话失败', { conversationId: target, ...errorFields(err) });
      return false;
    }
  }

  private async runOnce(
    conversationId: string,
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
      conversationId,
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
      todoWritten: false,
    };
    emit.status('thinking', `${data.provider}/${data.model}`);

    let slot: ConversationSlot;
    try {
      slot = await this.ensureSession(
        conversationId,
        data.provider,
        data.model,
        data.apiKey,
        data.baseUrl,
        data.thinkingLevel,
        data.providers[data.provider]?.models.find((m) => m.id === data.model)?.name,
      );
    } catch (err) {
      const safe = redactSecrets(describeError(err), [data.apiKey]);
      this.logger.error('无法建立 pi 会话', {
        promptId,
        conversationId,
        message: safe,
        ...safeErrorFields(err, data.apiKey),
        apiKey: this.store.maskedApiKey,
      });
      if (emit.isActive()) {
        const code = safe.includes('会话不存在') ? 'conversation_not_found' : 'agent_unavailable';
        emit.error(code, safe, promptId);
        emit.status('idle');
      }
      return;
    }

    const session = slot.session;
    slot.run = run;
    slot.abortRequested = false;
    if (this.activeConversationId === conversationId) this.activeConversationDraft = false;
    if (this.pendingAborts.delete(conversationId)) {
      slot.abortRequested = true;
      run.stopReason = 'aborted';
      void session.abort().catch((err: unknown) => {
        this.logger.debug('启动阶段的中断失败', { conversationId, ...errorFields(err) });
      });
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
      slot.run = null;
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

    try {
      if (!emit.isActive()) return;
      const start = session.messages.length;
      // 不解析斜杠命令/模板。
      await this.host.runWithContext(conversationId, emit, () =>
        session.prompt(text, { expandPromptTemplates: false }),
      );
      // 第一条用户消息已经落盘；之后“新会话”应该创建新的 session，而不是复用它。
      if (this.activeConversationId === conversationId) this.activeConversationDraft = false;

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
        await this.host.runWithContext(conversationId, emit, () =>
          session.prompt(
            `请现在调用工具 ${run.requiredTool}，然后再回答。`,
            { expandPromptTemplates: false },
          ),
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
      if (slot.abortRequested) {
        // 中断引起的异常不是失败：收尾走 interrupted 终稿（附录 A.15）。
        run.stopReason = 'aborted';
      } else {
        run.error = run.error ?? describeError(err);
        run.stopReason = 'error';
      }
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
      // 一轮结束：本轮写过的待办从游戏目标面板撤掉，不把清单留到下一轮。
      if (run.todoWritten) {
        try {
          await this.host.runWithContext(conversationId, emit, () =>
            this.host.call('game_todo_write', { todos: [] }, {
              timeoutMs: 3000,
              risk: 0,
              internal: true,
              conversationId,
            }),
          );
        } catch (err) {
          this.logger.debug('清空待办失败', errorFields(err));
        }
      }
      slot.run = null;
    }

    if (!emit.isActive()) return;

    // A.15：玩家中断的回合不是失败——保留已生成文本，按 interrupted 终稿收尾。
    const interrupted = run.stopReason === 'aborted' || slot.abortRequested;
    const failed = run.stopReason === 'error';
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
    } else if (interrupted) {
      this.logger.info('本轮被玩家中断', { promptId, chars: run.text.length });
      emit.message(run.text, promptId, true, { interrupted: true });
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
      this.appendBlueprintCards(run.saveKey, run.userText, uniqueCards, run.conversationId);
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
      this.appendLinkCards(
        run.saveKey,
        run.userText,
        run.text,
        uniqueLinks,
        run.conversationId,
      );
      for (const card of uniqueLinks) emit.linkCard?.(card);
    }

    emit.status('idle');

    // 标题生成放在 idle 之后异步执行：不阻塞主回答，也不改变模型上下文。
    // 只尝试新会话的前两次成功问答；已有 session_info.name 的会话不再自动覆盖。
    const titleRuntime = slot.runtime;
    const titleModel = session.model;
    const hadTitle = (session.sessionName ?? '').trim().length > 0;
    const userTurns = session.messages.filter((message) => message.role === 'user').length;
    if (
      !failed &&
      !interrupted &&
      run.text.trim().length > 0 &&
      run.saveKey &&
      titleRuntime &&
      titleModel &&
      !hadTitle &&
      userTurns <= 2
    ) {
      this.scheduleConversationTitle(
        run.saveKey,
        session.sessionId,
        run.userText,
        run.text,
        titleRuntime,
        titleModel,
        emit,
      );
    }
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
  private currentForcedToolPrompt(conversationId?: string | null): string | null {
    const run = conversationId ? this.slots.get(conversationId)?.run ?? null : null;
    if (!run || !run.requiredTool || run.requiredToolSatisfied) return null;
    return forcedToolInstruction(run.requiredTool);
  }

  /**
   * “本会话注册了哪些工具 + 每个工具的使用规则”。
   *
   * 工具清单必须按注册表生成：人格里手写的清单会在加工具后立刻过期
   * （实机踩过：人格仍称“只能读”，模型拒绝调用写工具）。
   * 规则段落也不手写：pi 的 promptGuidelines 只有在默认 coding prompt 分支里
   * 才会被拼进去，我们整轮替换 system prompt，所以必须自己渲染。
   *
   * 按工具集签名缓存：能力没变时每轮都是同一段字节，对 prompt cache 友好。
   */
  private toolPromptSection(): string | null {
    let tools: ToolDefinition[];
    try {
      tools = this.buildToolDefinitions();
    } catch (err) {
      // 注册表还没就绪（例如极早期回合）时不要打断本轮；下一轮会重新尝试。
      this.logger.warn('生成工具提示段落失败，本轮跳过', errorFields(err));
      return null;
    }
    const signature = toolsSignature(tools);
    if (this.toolPromptCache && this.toolPromptCache.signature === signature) {
      return this.toolPromptCache.text || null;
    }
    const parts = [renderCapabilitySection(tools), renderToolRules(tools)].filter(
      (part): part is string => typeof part === 'string' && part.length > 0,
    );
    const text = parts.join('\n\n');
    this.toolPromptCache = { signature, text };
    return text || null;
  }

  /**
   * 每轮 system prompt 的附加段（顺序：稳定 → 易变）：
   *   工具能力/规则 → 权限档位 → 沙盒状态 → 强制工具。
   * 沙盒/权限都按当前状态实时读取，不写进人格常量，避免旧 mod 被误导。
   */
  private currentExtraPrompt(conversationId?: string | null): string | null {
    const parts: string[] = [];
    const tools = this.toolPromptSection();
    if (tools) parts.push(tools);

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

    // 强制工具放最后：它是本轮最强的指令，离用户消息最近效果最稳。
    const forced = this.currentForcedToolPrompt(conversationId);
    if (forced) parts.push(forced);

    return parts.length > 0 ? parts.join('\n\n') : null;
  }

  /**
   * 构造本次能力下实际注册的全部工具定义。
   * ensureSession 与 ready.payload.toolCatalog 共用，避免两处工具集漂移。
   */
  private buildToolDefinitions(): ToolDefinition[] {
    return this.buildAllToolDefinitions().filter(
      (tool) => !this.host.isToolDisabled(tool.name),
    );
  }

  /** A.14.1：完整工具集（含被关掉的工具），设定页目录用。 */
  private buildAllToolDefinitions(): ToolDefinition[] {
    return [
      ...this.host.allTools(),
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

  /** 本次连接广告的工具名；按 host 当前能力/扩展启用状态生成。 */
  toolNames(capabilities: readonly string[], extensions: readonly ExtensionSummary[] = []): readonly string[] {
    this.host.setCapabilities(capabilities);
    this.host.setExtensions(extensions);
    return this.buildToolDefinitions().map((tool) => tool.name);
  }

  /** M4l：ready.payload.toolCatalog；只暴露显示需要的短字段。 */
  toolCatalog(
    capabilities: readonly string[],
    extensions: readonly ExtensionSummary[] = [],
  ): ToolCatalogEntry[] {
    this.host.setCapabilities(capabilities);
    this.host.setExtensions(extensions);
    const disabled = new Set(this.store.disabledToolNames?.() ?? []);
    return this.buildAllToolDefinitions().map((tool) => {
      const source = toolSourceOf(tool.name, extensions);
      return {
        name: tool.name,
        label: tool.label || tool.name,
        description: clipText(tool.description ?? '', 160),
        group: toolGroupOf(tool.name),
        risk: toolRiskOf(tool.name),
        source: source.source,
        sourceName: source.sourceName,
        enabled: !disabled.has(tool.name),
      };
    });
  }

  /** A.14.1：设定页保存工具开关。只有真正变化时才会重建会话。 */
  applyDisabledTools(disabled: readonly string[]): { disabled: string[]; rebuilt: boolean } {
    const view = this.store.update({ disabledTools: [...disabled] });
    const next = view.disabledTools ?? [];
    const changed = this.host.setDisabledTools?.(next) ?? false;
    this.logger.info('工具开关已更新', {
      disabled: next,
      rebuilt: changed,
    });
    return { disabled: [...next], rebuilt: changed };
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
    conversationId: string,
    provider: string,
    modelId: string,
    apiKey: string,
    baseUrl: string | null,
    thinkingLevel: string,
    displayName?: string,
  ): Promise<ConversationSlot> {
    const configKey = [provider, modelId, baseUrl ?? '', thinkingLevel].join('\u0000');
    const existingSlot = this.slots.get(conversationId);
    if (existingSlot) {
      if (existingSlot.configKey === configKey) return existingSlot;
      this.disposeSlot(conversationId);
    }

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
      () => this.currentExtraPrompt(conversationId),
      this.systemPromptText,
    );
    // pi 只有在「自己创建 loader」时才自动 reload；外部传入的 loader 必须我们自己 reload，
    // 否则 getExtensions() 还是空的（内联人格扩展就不会被注册）。
    await resourceLoader.reload();

    const sessionDir = join(this.dataDir, 'sessions', this.sessionDirName());
    const existing = pi.SessionManager.findById(this.dataDir, conversationId, sessionDir);
    let sessionManager;
    let draft = false;
    if (existing) {
      sessionManager = pi.SessionManager.open(existing, sessionDir, this.dataDir);
      if (this.activeConversationId === conversationId) {
        this.activeConversationDraft = false;
        this.activeConversationDraftTitle = null;
      }
    } else {
      // 只有“当前焦点且尚未落盘的空会话”可以第一次落盘；其他会话找不到就报错。
      if (this.activeConversationId !== conversationId || !this.activeConversationDraft) {
        throw new Error(`会话不存在：${conversationId}`);
      }
      const draftTitle = this.activeConversationDraftTitle;
      sessionManager = pi.SessionManager.create(this.dataDir, sessionDir, {
        id: conversationId,
      });
      if (draftTitle) sessionManager.appendSessionInfo(draftTitle);
      this.writeActiveConversationId(this.saveKey, conversationId, draftTitle);
      draft = true;
    }

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
    const slot: ConversationSlot = {
      conversationId,
      session: created.session,
      runtime,
      unsubscribe: () => {},
      run: null,
      abortRequested: false,
      draft,
      configKey,
    };
    slot.unsubscribe = created.session.subscribe((event) => this.handleEvent(event, slot));
    this.slots.set(conversationId, slot);

    // 保险：确认 pi 真的用上了我们的人格（没生效就明确记一条 warn，别静默退化）。
    const effectivePrompt = created.session.systemPrompt ?? '';
    const expectedPrompt = this.systemPromptText;
    if (!effectivePrompt.startsWith(expectedPrompt)) {
      this.logger.warn('pi 没有采用自定义 system prompt，助手人格可能未生效', {
        personaName: ICARUS_II_PERSONA_NAME,
        promptChars: effectivePrompt.length,
        expectedChars: expectedPrompt.length,
        override: this.promptOverride ? this.promptOverride.path : null,
      });
    }

    this.logger.info('pi 会话已建立', {
      conversationId,
      provider,
      model: model.id,
      cwd: this.dataDir,
      agentDir: this.agentDir,
      thinkingLevel: created.session.thinkingLevel,
      tools: created.session.getActiveToolNames(),
      persona: ICARUS_II_PERSONA_NAME,
      systemPromptChars: effectivePrompt.length,
      systemPromptSha256: this.promptOverride?.sha256 ?? null,
      systemPromptOverride: this.promptOverride?.path ?? null,
      extensions: created.extensionsResult.extensions.length,
      extensionErrors: created.extensionsResult.errors.length,
      apiKey: maskSecret(apiKey),
    });
    if (created.modelFallbackMessage) {
      this.logger.warn('pi 会话回退到了其它模型', { detail: created.modelFallbackMessage });
    }

    return slot;
  }

  // ---------------------------------------------------------------- 事件映射

  /** 工具名是否由 mod 执行（那几个已经由 mod 自己出卡片，不能重复）。 */
  private isHostTool(name: string): boolean {
    return this.host.tools().some((tool) => tool.name === name);
  }

  private handleEvent(event: AgentSessionEvent, slot: ConversationSlot): void {
    const run = slot.run;
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
        if (message.stopReason === 'error') {
          run.stopReason = 'error';
          run.error = message.errorMessage ?? run.error ?? '上游返回错误';
        } else if (message.stopReason === 'aborted') {
          // 玩家中断：保留已生成文本，不产生 error（附录 A.15）。
          run.stopReason = 'aborted';
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
        if (event.toolName === 'game_todo_write' && !event.isError) {
          run.todoWritten = true;
        }
        // 强制工具已被调用：恢复完整工具集，并允许后续文本正常流式输出。
        if (run.requiredTool && event.toolName === run.requiredTool && !run.requiredToolSatisfied) {
          run.requiredToolSatisfied = true;
          if (run.savedTools) {
            try {
              slot.session.setActiveToolsByName(run.savedTools);
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
            const branch = slot.session.sessionManager.getBranch();
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
