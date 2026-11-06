// 在 QuickJS-WASM 里执行 DSL 源码：只暴露 builder 与少量纯函数，没有 require/process/fetch/eval。
import {
  newQuickJSWASMModuleFromVariant,
  type QuickJSWASMModule,
} from 'quickjs-emscripten-core';
import QUICKJS_VARIANT_MODULE from '@jitl/quickjs-singlefile-cjs-release-sync';
import { DslError, normalizeIr, type DslIr } from './ir.js';

const PRELUDE = `
const __out = { target: [], raw: [], options: {}, module: [], connect: [], group: [], station: [], constraint: [] };
function target(item, rate) { __out.target.push({ item: String(item), rate: Number(rate) }); }
function raw(...items) { for (const item of items) __out.raw.push(String(item)); }
function options(o) { Object.assign(__out.options, o || {}); }
function module(item, o) {
  const id = 'm' + (__out.module.length + 1);
  const machines = o && o.machines !== undefined ? o.machines : 'auto';
  const building = o && o.building !== undefined ? o.building : null;
  __out.module.push({ id, item: String(item), machines, building });
  return { get id() { return id; }, get in() { return id + '.in'; }, get out() { return id + '.out'; } };
}
function connect(from, to, rate) {
  const row = { from: String(from), to: String(to) };
  if (rate !== undefined) row.rate = Number(rate);
  __out.connect.push(row);
}
function group(name, members, o) { __out.group.push({ name: String(name), members: (members || []).map(String), options: o || {} }); }
function station(name, o) { __out.station.push({ name: String(name), options: o || {} }); }
function constraint(text) { __out.constraint.push(String(text)); }
function range(n) { return Array.from({ length: Math.max(0, Number(n) | 0) }, (_, i) => i); }
function repeat(n, fn) { for (let i = 0; i < (Number(n) | 0); i += 1) fn(i); }
function sum(list) { return (list || []).reduce((a, b) => Number(a) + Number(b), 0); }
delete globalThis.Date;
delete Math.random;
delete globalThis.eval;
delete globalThis.Function;
delete globalThis.Proxy;
delete globalThis.Reflect;
`;

let modulePromise: Promise<QuickJSWASMModule> | null = null;

function quickjs(): Promise<QuickJSWASMModule> {
  if (!modulePromise) modulePromise = newQuickJSWASMModuleFromVariant(QUICKJS_VARIANT_MODULE as never);
  return modulePromise;
}

export interface RunDslOptions {
  timeoutMs?: number;
  memoryLimitBytes?: number;
}

/** 执行 DSL 源码，返回 IR；语法/运行时错误都转成 DslError（E0xx）。 */
export async function runDsl(source: string, options: RunDslOptions = {}): Promise<DslIr> {
  const timeoutMs = options.timeoutMs ?? 2000;
  const QuickJS = await quickjs();
  const runtime = QuickJS.newRuntime();
  runtime.setMemoryLimit(options.memoryLimitBytes ?? 64 * 1024 * 1024);
  const deadline = Date.now() + timeoutMs;
  runtime.setInterruptHandler(() => Date.now() > deadline);
  const vm = runtime.newContext();
  try {
    const program = `${PRELUDE}\n${source}\nJSON.stringify(__out);`;
    const result = vm.evalCode(program, 'blueprint.bp.js');
    if (result.error) {
      const error = vm.dump(result.error) as { name?: string; message?: string } | string;
      result.error.dispose();
      const message = typeof error === 'string' ? error : `${error?.name ?? '错误'}：${error?.message ?? ''}`;
      const timedOut = Date.now() > deadline;
      throw new DslError(timedOut ? 'E009' : 'E008', timedOut ? `DSL 执行超过 ${timeoutMs} 毫秒` : message);
    }
    const json = vm.getString(result.value);
    result.value.dispose();
    let parsed: unknown;
    try {
      parsed = JSON.parse(json);
    } catch {
      throw new DslError('E010', 'DSL 返回的 IR 不是合法 JSON');
    }
    return normalizeIr(parsed);
  } finally {
    vm.dispose();
    runtime.dispose();
  }
}
