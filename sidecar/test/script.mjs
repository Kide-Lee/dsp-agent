#!/usr/bin/env node
/**
 * M4d/M5 脚本层测试（离线）：只读 + 受控写脚本。
 *
 *   node test/script.mjs
 *
 * 覆盖：dsp.call 往返、console 收集、顶层 return、白名单、调用预算、
 * 无限循环中断、语法错误、无宿主全局（process/require/fetch 都不存在）、
 * 没有可用 game 工具时的明确报错。
 */

import { createScriptTool } from '../dist/tools/script.js';
import { SCRIPT_TOOL_NAME } from '../dist/tools/host.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

function makeHost({ approve = true } = {}) {
  const calls = [];
  const confirms = [];
  const releases = [];
  return {
    calls,
    confirms,
    releases,
    host: {
      has: (capability) => capability === 'script.write.v1',
      tools: () => [
        { name: 'game_get_power' },
        { name: 'game_get_entities' },
        { name: 'game_set_recipe' },
      ],
      confirmScript: async (request) => {
        confirms.push(request);
        return approve ? { token: 'lease-token', scriptHash: request.scriptHash } : null;
      },
      releaseScript: (token) => releases.push(token),
      call: async (name, args, options) => {
        calls.push({ name, args, options });
        return { ok: true, data: { name, args } };
      },
    },
  };
}

const emptyHost = {
  has: () => false,
  tools: () => [],
  call: async () => ({ ok: true, data: null }),
};

async function run(tool, code, extra = {}) {
  const result = await tool.execute('call_1', { code, ...extra }, undefined);
  return { text: result.content[0].text, details: result.details };
}

// ---------------------------------------------------------------- 1. 正常往返

console.log('── 1. dsp.call 往返与返回值 ──');
const { host, calls, confirms } = makeHost();
const tool = createScriptTool(host);

{
  const out = await run(
    tool,
    `
    const a = JSON.parse(dsp.call('game_get_power', {}));
    const b = JSON.parse(dsp.call('game_get_entities', { limit: 2 }));
    console.log('saw', a.data.name, b.data.name);
    return { names: [a.data.name, b.data.name], total: 2 };
  `,
  );
  const payload = JSON.parse(out.text);
  check('返回 ok', payload.ok === true);
  check('值正确', payload.value?.total === 2 && payload.value?.names?.length === 2, out.text.slice(0, 200));
  check('console.log 被收集', payload.logs?.some((line) => line.includes('saw game_get_power game_get_entities')));
  check('调用次数统计', payload.calls === 2);
  check('纯读脚本不弹写确认', confirms.length === 0);
  check('宿主收到两次调用', calls.length === 2);
  check('参数被透传', calls[1].args?.limit === 2 && calls[1].name === 'game_get_entities');
}

// ---------------------------------------------------------------- 2. 顶层 return

console.log('\n── 2. 顶层 return 与表达式 ──');
{
  const out = await run(tool, `return 1 + 2;`);
  check('顶层 return 生效', JSON.parse(out.text).value === 3);
}

// ---------------------------------------------------------------- 3. 白名单

console.log('\n── 3. 白名单、写确认与预算 ──');
{
  const out = await run(tool, `dsp.call('game_not_a_tool', {});`);
  check('未知工具被拒绝', out.text.includes('不在脚本白名单'), out.text.slice(0, 160));
  check('错误码是 script_error', out.text.includes('[script_error]'));
}

{
  const dynamic = await run(
    tool,
    `const n = 'game_set_recipe'; dsp.call(n, { buildingId: 1, recipeId: 2 });`,
  );
  check('动态拼写写工具名被拒绝', dynamic.text.includes('未确认的写操作'), dynamic.text.slice(0, 160));
}

{
  const denied = makeHost({ approve: false });
  const deniedTool = createScriptTool(denied.host);
  const out = await run(deniedTool, `dsp.call('game_set_recipe', { buildingId: 1, recipeId: 2 });`);
  check('写脚本未确认被拒绝', out.text.includes('[confirmation_denied]'), out.text.slice(0, 160));
  check('拒绝时没有执行写工具', denied.calls.length === 0);
}

{
  const approved = makeHost({ approve: true });
  const approvedTool = createScriptTool(approved.host);
  const out = await run(
    approvedTool,
    `dsp.call('game_set_recipe', { buildingId: 1, recipeId: 2 }); return 1;`,
  );
  check('写脚本确认后执行', JSON.parse(out.text).ok === true, out.text.slice(0, 160));
  check('只确认一次', approved.confirms.length === 1);
  check('确认包含写工具名', approved.confirms[0]?.writeNames?.includes('game_set_recipe'));
  check('写调用携带租约 token', approved.calls[0]?.options?.confirmToken === 'lease-token');
  check('写调用携带脚本哈希', typeof approved.calls[0]?.options?.confirmArgsHash === 'string');
  check('脚本结束释放租约', approved.releases.includes('lease-token'));
}

{
  const out = await run(
    tool,
    `dsp.call('game_get_power', {}); dsp.call('game_get_power', {});`,
    { maxCalls: 1 },
  );
  check('调用预算生效', out.text.includes('调用次数超过上限 1'), out.text.slice(0, 160));
}

// ---------------------------------------------------------------- 4. 中断与错误

console.log('\n── 4. 中断与错误 ──');
{
  const started = Date.now();
  const out = await run(tool, `while (true) {}`, { timeoutMs: 300 });
  const elapsed = Date.now() - started;
  check('死循环被中断', out.text.includes('script_timeout'), out.text.slice(0, 160));
  check('中断在合理时间内', elapsed < 3000, `${elapsed}ms`);
}

{
  const out = await run(tool, `return {{;`);
  check('语法错误报 script_error', out.text.includes('[script_error]'), out.text.slice(0, 160));
}

{
  const out = await run(tool, `throw new Error('boom')`);
  check('运行时错误被捕获', out.text.includes('boom'), out.text.slice(0, 160));
}

{
  const out = await run(
    tool,
    `try { dsp.call('game_get_power', {}); } catch (e) {} return { done: true };`,
  );
  check('吞掉挂起会明确报错', out.text.includes('try/catch'), out.text.slice(0, 200));
  check('错误码是 script_error', out.text.includes('[script_error]'));
}

// ---------------------------------------------------------------- 5. 没有宿主能力

console.log('\n── 5. 沙箱边界 ──');
{
  const out = await run(
    tool,
    `return { process: typeof process, require: typeof require, fetch: typeof fetch, globalThisProcess: typeof globalThis.process };`,
  );
  const value = JSON.parse(out.text).value;
  check(
    'process/require/fetch 都不存在',
    value.process === 'undefined' &&
      value.require === 'undefined' &&
      value.fetch === 'undefined' &&
      value.globalThisProcess === 'undefined',
    JSON.stringify(value),
  );
}

{
  const out = await run(tool, `return 1`, {});
  check('有宿主时正常执行', JSON.parse(out.text).value === 1);
}

{
  const emptyTool = createScriptTool(emptyHost);
  const out = await run(emptyTool, `return 1`);
  check('没有 game 工具时明确报错', out.text.includes('[tool_unavailable]'), out.text.slice(0, 160));
}

// ---------------------------------------------------------------- 6. 工具名

console.log('\n── 6. 工具元数据 ──');
check('工具名是 run_script', tool.name === SCRIPT_TOOL_NAME && SCRIPT_TOOL_NAME === 'run_script');
check('描述提到 dsp.call', tool.description.includes('dsp.call'));

console.log(`\n${failed === 0 ? '✅' : '❌'} script 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
