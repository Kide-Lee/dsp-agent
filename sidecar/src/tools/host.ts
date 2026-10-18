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

/** 只读脚本工具名（sidecar 本地执行）。 */
export const SCRIPT_TOOL_NAME = 'run_script';

export interface ToolCallOptions {
  timeoutMs: number;
  signal?: AbortSignal;
}

/** Session 实现它；工具宿主只依赖这个窄接口。 */
export interface ToolTransport {
  callTool(name: string, args: JsonObject, options: ToolCallOptions): Promise<ToolResultPayload>;
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
  private cachedTools: ToolDefinition[] | null = null;

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

  has(capability: string): boolean {
    return this.capabilities.includes(capability);
  }

  get capabilityList(): readonly string[] {
    return this.capabilities;
  }

  bind(transport: ToolTransport): void {
    this.transport = transport;
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
    this.cachedTools = factory(this);
    return this.cachedTools;
  }

  async call(
    name: string,
    args: JsonObject,
    options: ToolCallOptions,
  ): Promise<ToolResultPayload> {
    const transport = this.transport;
    if (!transport) {
      return toolError('tool_unavailable', '当前没有绑定到游戏连接（可能已断线或尚未进入存档）');
    }
    try {
      return await transport.callTool(name, args, options);
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
