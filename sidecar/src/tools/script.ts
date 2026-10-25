/**
 * 脚本层（M4d/M5）。
 *
 * 目标：把「取数 → 判断 → 循环 → 再取数/写」压成一次工具调用，省 token 也省延迟；
 * 同时保持与现有安全立场一致：
 *
 *  - 脚本跑在 QuickJS-WASM 里，与宿主零共享内存，只有注入的 `dsp` 对象；
 *  - `dsp.call` 走**同一个** GameToolHost / 同一份工具 schema，自动继承路由与超时；
 *  - 默认只读；脚本出现字面量写工具时，执行前统一确认一次，写调用凭 mod 脚本租约；
 *  - 动态拼写写工具名无法通过预检，运行期没有租约会被拒绝；
 *  - 有调用次数、CPU 时间与返回大小三重预算；
 *  - 没有 fs / net / process / require：不是"把 node 关起来"，而是根本不存在。
 *
 * 为什么不用 `node:vm`：它不是安全边界（`constructor.constructor` 可拿回宿主 Function）。
 * 为什么用 singlefile 变体：WASM 以 base64 内联，esbuild → SEA 的单文件链路才带得走。
 *
 * 关键实现选择：**同步 + 重放**，而不是 Asyncify。
 * - Asyncify 的异步宿主函数在 await 之后创建句柄会踩 WASM 内存（实测 memory access out of bounds）；
 * - 同步变体更小更快，代价是脚本里的 `dsp.call` 只能同步调用（本来就是本地数据，够用）；
 * - 异步工具调用用"抛出挂起 → 宿主执行 → 从头重放并命中 memo"实现，
 *   调用次数上限 50，重放成本可接受。
 */

import { createHash } from 'node:crypto';
import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import {
  newQuickJSWASMModuleFromVariant,
  shouldInterruptAfterDeadline,
  type QuickJSWASMModule,
} from 'quickjs-emscripten-core';
import QUICKJS_VARIANT_MODULE from '@jitl/quickjs-singlefile-cjs-release-sync';
import type { JsonObject } from '../protocol.js';
import { SCRIPT_TOOL_NAME, type GameToolHost } from './host.js';
import { isWriteToolName } from './write.js';

const MAX_CODE_CHARS = 8000;
const DEFAULT_TIMEOUT_MS = 30000;
const MAX_TIMEOUT_MS = 120000;
const DEFAULT_MAX_CALLS = 100;
const MAX_MAX_CALLS = 10000;
const MAX_RESULT_CHARS = 6000;
const MAX_LOGS = 40;

/** 挂起标记：`dsp.call` 抛出它，宿主据此执行工具并重放。 */
const SUSPEND_PREFIX = '__ICARUS_SUSPEND__:';

/**
 * 这个包是 CJS，且导出形状随 Node/TS 互操作不同：直接 import 可能是 variant 本身，
 * 也可能是 `{ default: variant }`。两种都接受，避免打包后运行时才炸。
 */
type VariantInput = Parameters<typeof newQuickJSWASMModuleFromVariant>[0];
const QUICKJS_VARIANT: VariantInput = (() => {
  const raw = QUICKJS_VARIANT_MODULE as unknown;
  const maybe = (raw as { default?: VariantInput }).default;
  return (maybe ?? raw) as VariantInput;
})();

/** QuickJS 模块创建一次就够；WASM 编译有成本，不能每次调用都做。 */
let modulePromise: Promise<QuickJSWASMModule> | null = null;

function quickjs(): Promise<QuickJSWASMModule> {
  if (!modulePromise) {
    modulePromise = newQuickJSWASMModuleFromVariant(QUICKJS_VARIANT);
  }
  return modulePromise;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function clip(text: string): string {
  if (text.length <= MAX_RESULT_CHARS) return text;
  return `${text.slice(0, MAX_RESULT_CHARS)}\n…（结果过长，已截断）`;
}

type ToolReply = { content: { type: 'text'; text: string }[]; details: unknown };

function fail(code: string, message: string): ToolReply {
  return {
    content: [{ type: 'text', text: `[${code}] ${message}` }],
    details: { ok: false, error: { code, message } },
  };
}

interface ScriptLimit {
  timeoutMs: number;
  maxCalls: number;
}

interface WriteLease {
  token: string;
  scriptHash: string;
  maxWrites: number;
  /** 确认卡上列出的写工具；运行期只允许这些名字。 */
  allowedNames: readonly string[];
}

/**
 * 标记注释/字符串区域，供字面量扫描排除误报。
 * 只用于“哪些位置是代码”；真正的参数校验仍由 mod 租约兜底。
 */
function codeMask(code: string): boolean[] {
  const mask = new Array<boolean>(code.length).fill(true);
  let i = 0;
  while (i < code.length) {
    const ch = code[i];
    const next = code[i + 1];
    if (ch === '/' && next === '/') {
      while (i < code.length && code[i] !== '\n') { mask[i] = false; i += 1; }
      continue;
    }
    if (ch === '/' && next === '*') {
      mask[i] = false; mask[i + 1] = false; i += 2;
      while (i < code.length && !(code[i] === '*' && code[i + 1] === '/')) {
        mask[i] = false; i += 1;
      }
      if (i < code.length) { mask[i] = false; mask[i + 1] = false; i += 2; }
      continue;
    }
    if (ch === '\'' || ch === '"' || ch === '`') {
      const quote = ch;
      mask[i] = false;
      i += 1;
      while (i < code.length) {
        if (code[i] === '\\') {
          mask[i] = false;
          if (i + 1 < code.length) mask[i + 1] = false;
          i += 2;
          continue;
        }
        mask[i] = false;
        if (code[i] === quote) { i += 1; break; }
        i += 1;
      }
      continue;
    }
    i += 1;
  }
  return mask;
}

/**
 * 扫描字面量 `dsp.call("name")` / `dsp["call"]("name")`。
 * 注释和字符串里的伪调用不算；运行期仍由 writeLease.allowedNames + mod 双保险。
 */
function detectWriteTools(code: string): string[] {
  const found: string[] = [];
  const mask = codeMask(code);
  const re = /dsp\s*(?:\??\.\s*call|\[\s*['"]call['"]\s*\])\s*\(\s*['"]([A-Za-z0-9_]+)['"]/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(code)) !== null) {
    if (!mask[match.index]) continue;
    const name = match[1] ?? '';
    if (isWriteToolName(name) && !found.includes(name)) found.push(name);
  }
  return found;
}

/**
 * 跑一次脚本；遇到 `dsp.call` 就抛出挂起标记，宿主执行后把结果 memo 起来重放。
 *
 * 重放是确定性的前提：脚本里不要用 Date.now / Math.random / 外部可变状态。
 */
async function runScript(
  host: GameToolHost,
  code: string,
  limits: ScriptLimit,
  writeLease?: WriteLease,
  signal?: AbortSignal,
): Promise<ToolReply> {
  const allowed = new Set(host.tools().map((tool) => tool.name));
  if (allowed.size === 0) {
    return fail('tool_unavailable', '当前没有可用的 game_* 工具（未进入存档或连接未就绪）');
  }

  const QuickJS = await quickjs();
  const startedAt = Date.now();
  const deadline = startedAt + limits.timeoutMs;
  const memo: string[] = [];
  let executed = 0;
  let executedWrites = 0;

  for (let attempt = 0; attempt <= limits.maxCalls; attempt += 1) {
    if (signal?.aborted) return fail('cancelled', '脚本已被取消');
    if (Date.now() > deadline) {
      return fail('script_timeout', `脚本在 ${limits.timeoutMs}ms 内没有结束（已中断）`);
    }

    const ctx = QuickJS.newContext();
    ctx.runtime.setInterruptHandler(shouldInterruptAfterDeadline(deadline));

    const logs: string[] = [];
    let callIndex = 0;
    // 这些句柄由我们创建，必须在释放 context 前显式 dispose；
    // 否则 QuickJS 运行时的 gc_obj_list 非空，JS_FreeRuntime 会直接断言崩溃。
    const handles: { alive: boolean; dispose: () => void }[] = [];

    try {
      const dsp = ctx.newObject();
      const callFn = ctx.newFunction('call', (nameHandle, argsHandle) => {
        const name = ctx.getString(nameHandle);
        const index = callIndex;
        callIndex += 1;

        if (index < memo.length) {
          return ctx.newString(memo[index] as string);
        }

        let args: JsonObject = {};
        if (argsHandle) {
          const dumped = ctx.dump(argsHandle);
          if (isObject(dumped)) args = dumped;
        }
        // 抛宿主 Error 让 quickjs-emscripten 自己转成 JS 异常（不要手造 error handle，
        // 那样每挂起一次就泄漏一个 QuickJS 对象，运行结束时触发 gc_obj_list 断言）。
        throw new Error(`${SUSPEND_PREFIX}${JSON.stringify({ name, args })}`);
      });
      handles.push(dsp, callFn);
      ctx.setProp(dsp, 'call', callFn);
      ctx.setProp(ctx.global, 'dsp', dsp);

      const consoleObj = ctx.newObject();
      const logFn = ctx.newFunction('log', (...handles) => {
        if (logs.length >= MAX_LOGS) return;
        const parts = handles.map((handle) => {
          const value = ctx.dump(handle);
          return typeof value === 'string' ? value : JSON.stringify(value);
        });
        logs.push(parts.join(' '));
      });
      handles.push(consoleObj, logFn);
      ctx.setProp(consoleObj, 'log', logFn);
      ctx.setProp(ctx.global, 'console', consoleObj);

      // 用户代码放在函数里，允许顶层 return。
      const evalResult = ctx.evalCode(`(function () {\n${code}\n})()`, 'script.js');
      if (evalResult.error) {
        const dumped = ctx.dump(evalResult.error);
        evalResult.error.dispose();

        const suspendMessage =
          typeof dumped === 'string'
            ? dumped
            : isObject(dumped) && typeof dumped['message'] === 'string'
              ? dumped['message']
              : '';
        if (suspendMessage.startsWith(SUSPEND_PREFIX)) {
          const raw = suspendMessage.slice(SUSPEND_PREFIX.length);
          let call: { name?: unknown; args?: unknown } = {};
          try {
            call = JSON.parse(raw) as { name?: unknown; args?: unknown };
          } catch {
            return fail('script_error', '脚本挂起载荷损坏');
          }
          const name = typeof call.name === 'string' ? call.name : '';
          const args = isObject(call.args) ? call.args : {};

          if (!allowed.has(name)) {
            return fail(
              'script_error',
              `工具不在脚本白名单：${name}（可用：${[...allowed].join(', ')}）`,
            );
          }
          if (executed >= limits.maxCalls) {
            return fail('script_error', `脚本调用次数超过上限 ${limits.maxCalls}`);
          }

          const isWrite = isWriteToolName(name);
          const lease = isWrite ? writeLease : undefined;
          if (isWrite && !lease) {
            return fail(
              'script_error',
              `脚本包含未确认的写操作 ${name}。` +
                '写工具必须在脚本里写成字面量 dsp.call("工具名", …)，由 run_script 在执行前统一确认。',
            );
          }
          if (lease && !lease.allowedNames.includes(name)) {
            return fail(
              'script_error',
              `脚本调用了未在确认卡中列出的写工具 ${name}；` +
                '写工具必须写成字面量 dsp.call("工具名", …)。',
            );
          }
          if (lease && executedWrites >= lease.maxWrites) {
            return fail('script_error', `脚本写调用超过本次确认上限 ${lease.maxWrites}`);
          }
          if (signal?.aborted) return fail('cancelled', '脚本已被取消');

          const remaining = Math.max(1, Math.min(8000, deadline - Date.now()));
          const result = await host.call(name, args, {
            timeoutMs: remaining,
            signal,
            ...(lease
              ? {
                  risk: 2,
                  confirmToken: lease.token,
                  confirmArgsHash: lease.scriptHash,
                }
              : {}),
          });
          if (lease) executedWrites += 1;
          memo.push(JSON.stringify(result));
          executed += 1;
          continue;
        }

        const message =
          isObject(dumped) && typeof dumped['message'] === 'string'
            ? dumped['message']
            : String(dumped);
        const interrupted = /interrupt/i.test(message);
        return fail(
          interrupted ? 'script_timeout' : 'script_error',
          interrupted ? `脚本在 ${limits.timeoutMs}ms 内没有结束（已中断）` : message,
        );
      }

      // 如果脚本把 `dsp.call` 的挂起异常 try/catch 掉了，执行会"正常"结束，
      // 但实际调用一次都没执行（模型会拿到一堆占位值）。必须显式报错。
      const unresolved = callIndex - memo.length;
      if (unresolved > 0) {
        evalResult.value.dispose();
        return fail(
          'script_error',
          '脚本吞掉了 dsp.call 的挂起（通常是 try/catch 包住了它）。'
            + 'dsp.call 靠抛异常实现挂起，不能放进 try/catch；请直接调用，或先收集数据再判断。',
        );
      }

      const value = ctx.dump(evalResult.value);
      evalResult.value.dispose();
      return {
        content: [
          {
            type: 'text',
            text: clip(
              JSON.stringify(
                {
                  ok: true,
                  value: value === undefined ? null : value,
                  logs,
                  calls: executed,
                  elapsedMs: Date.now() - startedAt,
                  limits,
                },
                null,
                2,
              ),
            ),
          },
        ],
        details: { ok: true, data: value === undefined ? null : value, logs, calls: executed },
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      const interrupted = /interrupt/i.test(message);
      return fail(
        interrupted ? 'script_timeout' : 'script_error',
        interrupted ? `脚本在 ${limits.timeoutMs}ms 内没有结束（已中断）` : message,
      );
    } finally {
      for (const handle of handles) {
        try {
          if (handle.alive) handle.dispose();
        } catch {
          // 已被连带释放时忽略。
        }
      }
      try {
        ctx.dispose();
      } catch {
        // 释放失败不影响本次结果。
      }
    }
  }

  return fail('script_error', `脚本调用次数超过上限 ${limits.maxCalls}`);
}

export function createScriptTool(host: GameToolHost): ToolDefinition {
  return {
    name: SCRIPT_TOOL_NAME,
    label: '脚本',
    executionMode: 'sequential',
    description:
      '在沙箱里执行一段 JavaScript，用来批量取数、循环、条件判断、聚合，或在确认后批量执行写操作。' +
      '可用 `dsp.call(工具名, 参数)`（同步）调用当前已注册的 game_* 工具，返回 JSON 字符串；' +
      '`console.log` 会被收集；脚本要 `return` 一个可 JSON 序列化的结果。' +
      '脚本包含写工具时，执行前会向玩家弹一次脚本级确认，确认后最多执行 10000 次写调用；' +
      '没有 fs/net/process/require。',
    promptSnippet: '执行脚本；批量取数，或在一次确认后批量写游戏状态',
    promptGuidelines: [
      'Use run_script 当需要对多个建筑/行星做批量查询、筛选、求和或条件判断时；单次查询仍直接用对应工具。',
      'dsp.call 是同步的，返回 JSON 字符串，需要 JSON.parse；脚本必须 return 一个可序列化的结果。',
      '脚本里要写写操作时，必须使用字面量 dsp.call("写工具名", …)，执行前会弹一次脚本级确认；动态拼工具名的写调用会被拒绝。',
      '写脚本一次最多 10000 次写调用；仍受 dsp.call 次数（默认 100，最大 10000）与运行时间（默认 30 秒，最大 120 秒）预算约束。',
      '不要用 try/catch 包住 dsp.call：它的挂起靠异常实现，被吞掉会导致结果错误；也不要用 Date.now/Math.random（重放会不一致）。',
    ],
    parameters: Type.Object({
      code: Type.String({
        description:
          '脚本正文。示例：const r = JSON.parse(dsp.call("game_get_power", {})); return {kw: r.data.demandKw};',
      }),
      timeoutMs: Type.Optional(
        Type.Integer({ minimum: 100, maximum: MAX_TIMEOUT_MS, description: '运行上限，默认 30000ms，最大 120000ms' }),
      ),
      maxCalls: Type.Optional(
        Type.Integer({ minimum: 1, maximum: MAX_MAX_CALLS, description: '工具调用次数上限，默认 100，最大 10000' }),
      ),
    }),
    async execute(_toolCallId, params, signal) {
      const args = (params ?? {}) as JsonObject;
      const code = typeof args['code'] === 'string' ? args['code'] : '';
      if (!code.trim()) return fail('bad_request', 'code 不能为空');
      if (code.length > MAX_CODE_CHARS) {
        return fail('bad_request', `脚本过长（${code.length} > ${MAX_CODE_CHARS} 字符）`);
      }

      const timeoutRaw =
        typeof args['timeoutMs'] === 'number' ? args['timeoutMs'] : DEFAULT_TIMEOUT_MS;
      const timeoutMs = Math.max(100, Math.min(MAX_TIMEOUT_MS, Math.floor(timeoutRaw)));
      const callsRaw = typeof args['maxCalls'] === 'number' ? args['maxCalls'] : DEFAULT_MAX_CALLS;
      const maxCalls = Math.max(1, Math.min(MAX_MAX_CALLS, Math.floor(callsRaw)));

      const writes = detectWriteTools(code);
      if (writes.length > 0 && !host.has('script.write.v1')) {
        return fail(
          'tool_unavailable',
          '当前 mod 不支持受控写脚本；请升级 mod，或在聊天里直接调用写工具。',
        );
      }
      const maxWrites = 10000;
      let lease: WriteLease | null = null;
      if (writes.length > 0) {
        const scriptHash = createHash('sha256').update(code, 'utf8').digest('hex');
        const approved = await host.confirmScript({ scriptHash, writeNames: writes, maxWrites });
        if (!approved) return fail('confirmation_denied', '玩家拒绝了脚本中的写操作，脚本未执行');
        lease = {
          token: approved.token,
          scriptHash: approved.scriptHash,
          maxWrites,
          allowedNames: writes,
        };
      }

      try {
        return await runScript(host, code, { timeoutMs, maxCalls }, lease ?? undefined, signal);
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        return fail('script_error', message);
      } finally {
        if (lease) host.releaseScript(lease.token);
      }
    },
  };
}
