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
import type { ExtensionSummary, JsonObject, ToolResultPayload } from '../protocol.js';
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
  /** 脚本里写工具的最高 risk（1/2）；mod 据此决定是否按权限档位自动放行。 */
  risk: 1 | 2;
}

export interface ScriptConfirmResult {
  token: string;
  scriptHash: string;
}

/**
 * 本地「写文件」工具的确认（blueprint_save / blueprint_generate / blueprint_fs）。
 * 它们不消耗 mod 令牌；确认只用于证明玩家同意按当前权限档位写入。
 */
export interface LocalWriteConfirmRequest {
  tool: string;
  args: JsonObject;
  risk: 1 | 2;
  title: string;
  body: string;
}

/** Session 实现它；工具宿主只依赖这个窄接口。 */
export interface ToolTransport {
  callTool(name: string, args: JsonObject, options: ToolCallOptions): Promise<ToolResultPayload>;
  /** 脚本包含写操作时，向玩家确认整段脚本；null = 拒绝。 */
  confirmScript?(request: ScriptConfirmRequest): Promise<ScriptConfirmResult | null>;
  /** 本地蓝图写操作：按当前权限档位确认；false = 拒绝。 */
  confirmLocalWrite?(request: LocalWriteConfirmRequest): Promise<boolean>;
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
  private extensions: ExtensionSummary[] = [];
  private disabledExtensions = new Set<string>();
  /** A.14.1：被玩家关掉的工具名；过滤点是 tools()/allTools()，脚本白名单同源。 */
  private disabledTools = new Set<string>();
  private cachedTools: ToolDefinition[] | null = null;
  /** 工具集变化（启用/禁用扩展）时通知 agent 在回合边界重建会话。 */
  onToolsetChanged?: () => void;
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
   * 第三方 mod 列表（来自 hello.extensions）。
   *
   * 工具必须与提供它的插件同生命周期：插件被卸载/加载失败时，下一条 hello 不再列出它，
   * 这里返回变化并让 agent 重建会话，旧工具从模型上下文里消失。
   */
  setExtensions(next: readonly ExtensionSummary[]): boolean {
    const normalized = [...next].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
    const same =
      normalized.length === this.extensions.length &&
      normalized.every((value, index) => {
        const current = this.extensions[index];
        if (!current) return false;
        return (
          value.id === current.id &&
          value.guid === current.guid &&
          value.name === current.name &&
          value.version === current.version &&
          value.configCount === current.configCount &&
          JSON.stringify(value.tools ?? []) === JSON.stringify(current.tools ?? [])
        );
      });
    if (same) return false;
    this.extensions = normalized;
    for (const id of [...this.disabledExtensions]) {
      if (!normalized.some((extension) => extension.id === id)) this.disabledExtensions.delete(id);
    }
    this.cachedTools = null;
    return true;
  }

  get extensionList(): readonly ExtensionSummary[] {
    return this.extensions;
  }

  /**
   * A.14.1：整份替换被关闭的工具名。返回是否真的变化。
   * 变化时清缓存并请求 agent 在回合边界重建会话。
   */
  setDisabledTools(next: readonly string[]): boolean {
    const normalized = new Set(next);
    if (
      normalized.size === this.disabledTools.size &&
      [...normalized].every((name) => this.disabledTools.has(name))
    ) {
      return false;
    }
    this.disabledTools = normalized;
    this.cachedTools = null;
    this.onToolsetChanged?.();
    return true;
  }

  get disabledToolNames(): readonly string[] {
    return [...this.disabledTools];
  }

  isToolDisabled(name: string): boolean {
    return this.disabledTools.has(name);
  }

  isExtensionEnabled(id: string): boolean {
    return !this.disabledExtensions.has(id);
  }

  /** 启用/禁用某个扩展的工具；变化时清缓存并请求重建。 */
  setExtensionEnabled(id: string, enabled: boolean): boolean {
    if (!this.extensions.some((extension) => extension.id === id)) return false;
    if (enabled) {
      if (!this.disabledExtensions.delete(id)) return false;
    } else {
      if (this.disabledExtensions.has(id)) return false;
      this.disabledExtensions.add(id);
    }
    this.cachedTools = null;
    this.onToolsetChanged?.();
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

  /** 本地蓝图写确认；老 transport 没有该能力时放行（保持旧行为）。 */
  async confirmLocalWrite(request: LocalWriteConfirmRequest): Promise<boolean> {
    if (!this.transport?.confirmLocalWrite) return false;
    return this.transport.confirmLocalWrite(request);
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

  /**
   * 按当前能力返回全部工具定义（忽略玩家开关）。
   * 设定页的完整目录用它，才能把 enabled=false 的工具也列出来。
   */
  allTools(): ToolDefinition[] {
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

  /** 按当前能力返回工具定义；被玩家关掉的工具不注册给模型，也不进脚本白名单。 */
  tools(): ToolDefinition[] {
    const all = this.allTools();
    if (this.disabledTools.size === 0) return all;
    return all.filter((tool) => !this.disabledTools.has(tool.name));
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
