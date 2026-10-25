/**
 * 工具宿主：把「模型可调用的工具定义」与「本轮 prompt 绑定的 mod 连接」解耦。
 *
 * 约定（design.md §7.3.1）：
 *  - sidecar 进程内只有一个 PiAgent，但可能有多个 mod 连接；
 *  - 每轮 prompt 开始前 `bind(emit)`，结束后 `unbind(emit)`；
 *  - 工具执行只发往绑定的连接，不广播、不取“最后一个连接”；
 *  - 能力变化必须让 PiAgent 重建会话，避免模型看到不存在的工具。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import type { JsonObject, ToolResultPayload } from '../protocol.js';
import { isSandboxOnlyTool } from './sandbox.js';

/** 脚本工具名（sidecar 本地执行）。 */
export const SCRIPT_TOOL_NAME = 'run_script';

export interface ToolCallOptions {
  timeoutMs: number;
  signal?: AbortSignal;
  /** true = mod 不显示工具卡（知识库导出、调试直调）。 */
  internal?: boolean;
  /** 写操作风险等级；>0 时走 confirm_request + 一次性令牌。 */
  risk?: number;
  /** 已由脚本写租约确认的 token；提供时跳过单次 confirm。 */
  confirmToken?: string;
  /** 脚本写租约绑定的脚本哈希。 */
  confirmArgsHash?: string;
}

/** run_script 的脚本级确认（一次确认，允许最多 maxWrites 次写调用）。 */
export interface ScriptConfirmRequest {
  scriptHash: string;
  writeNames: string[];
  maxWrites: number;
}

export interface ScriptConfirmResult {
  token: string;
  scriptHash: string;
}

/** Session 实现它；工具宿主只依赖这个窄接口。 */
export interface ToolTransport {
  callTool(name: string, args: JsonObject, options: ToolCallOptions): Promise<ToolResultPayload>;
  /** 脚本包含写操作时，向玩家确认整段脚本；null = 拒绝。 */
  confirmScript?(request: ScriptConfirmRequest): Promise<ScriptConfirmResult | null>;
  /** 脚本结束/失败后释放租约。 */
  releaseScript?(token: string): void;
}

export function toolError(code: string, message: string): ToolResultPayload {
  return {
    ok: false,
    data: null,
    error: { code, message, retryable: false },
    meta: {},
  };
}

export class GameToolHost {
  private transport: ToolTransport | null = null;
  private capabilities: string[] = [];
  private sandboxEnabled = false;
  private cachedTools: ToolDefinition[] | null = null;
  /** 调试直调期间把 host 调用标记为 internal，mod 不显示普通工具卡。 */
  private debugMode = false;

  /** 返回能力集合是否真的变了。 */
  setCapabilities(next: readonly string[]): boolean {
    const normalized = [...new Set(next)].sort();
    const same =
      normalized.length === this.capabilities.length &&
      normalized.every((value, index) => value === this.capabilities[index]);
    if (same) return false;
    this.capabilities = normalized;
    this.cachedTools = null;
    return true;
  }

  /**
   * 本次载入是否启用游戏沙盒工具。返回是否真的变化；
   * 变化时必须让 agent 在回合边界重建 pi 会话，否则模型仍拿着旧工具集。
   */
  setSandbox(enabled: boolean): boolean {
    if (this.sandboxEnabled === enabled) return false;
    this.sandboxEnabled = enabled;
    this.cachedTools = null;
    return true;
  }

  get sandbox(): boolean {
    return this.sandboxEnabled;
  }

  async confirmScript(request: ScriptConfirmRequest): Promise<ScriptConfirmResult | null> {
    if (!this.transport?.confirmScript) return null;
    return this.transport.confirmScript(request);
  }

  releaseScript(token: string): void {
    this.transport?.releaseScript?.(token);
  }

  has(capability: string): boolean {
    return this.capabilities.includes(capability);
  }

  get capabilityList(): readonly string[] {
    return this.capabilities;
  }

  bind(transport: ToolTransport): void {
    this.transport = transport;
  }

  setDebugMode(enabled: boolean): void {
    this.debugMode = enabled;
  }

  unbind(transport: ToolTransport): void {
    if (this.transport === transport) this.transport = null;
  }

  get isBound(): boolean {
    return this.transport !== null;
  }

  /** 按当前能力返回工具定义；未声明 `game.read.v1` 时返回空数组。 */
  tools(): ToolDefinition[] {
    if (this.cachedTools) return this.cachedTools;
    const factory = gameToolsFactory;
    if (!factory) {
      throw new Error('game tools factory 尚未注册（index.ts 必须先 import tools/registry.js）');
    }
    const tools = factory(this);
    this.cachedTools = this.sandboxEnabled
      ? tools
      : tools.filter((tool) => !isSandboxOnlyTool(tool.name));
    return this.cachedTools;
  }

  async call(
    name: string,
    args: JsonObject,
    options: ToolCallOptions,
  ): Promise<ToolResultPayload> {
    if (!this.sandboxEnabled && isSandboxOnlyTool(name)) {
      return toolError(
        'sandbox_required',
        '当前存档未启用游戏沙盒工具，沙盒专属工具不可用',
      );
    }

    const transport = this.transport;
    if (!transport) {
      return toolError('tool_unavailable', '当前没有绑定到游戏连接（可能已断线或尚未进入存档）');
    }
    try {
      return await transport.callTool(name, args, {
        ...options,
        internal: options.internal ?? this.debugMode,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return toolError('tool_unavailable', `工具调用失败：${message}`);
    }
  }
}

// 静态 import 会让 host <-> registry 形成 ESM 循环；因此由 registry 在模块初始化时
// 注册工厂，host 只在真正需要工具定义时调用它。
let gameToolsFactory: ((host: GameToolHost) => ToolDefinition[]) | null = null;

export function registerGameToolsFactory(
  factory: (host: GameToolHost) => ToolDefinition[],
): void {
  gameToolsFactory = factory;
}
