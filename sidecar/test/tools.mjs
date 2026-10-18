#!/usr/bin/env node
/**
 * M2 工具链路测试（离线）。
 *
 * 不走真实游戏：用 FakeSocket + 一个会调用 emit.callTool 的假 agent
 * 直接驱动 Session，验证：
 *   - ready.payload.tools 按 capability 生成
 *   - tool_call → tool_result 往返
 *   - 超时、断线都 resolve 成结构化错误
 *   - 未知/非法 tool_result 不会让会话崩
 *
 *   node test/tools.mjs
 */

import { Session } from '../dist/session.js';
import { GameToolHost } from '../dist/tools/host.js';
import { toolNamesForCapabilities } from '../dist/tools/registry.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

class FakeSocket {
  constructor() {
    this.isOpen = true;
    this.peer = 'test';
    this.frames = [];
  }
  send(text, cb) {
    try {
      this.frames.push(JSON.parse(text));
      cb?.();
    } catch (err) {
      cb?.(err);
    }
  }
  close() {
    this.isOpen = false;
  }
}

function makeConfig() {
  return {
    view: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
    update: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
  };
}

function hello(capabilities = ['game.read.v1']) {
  return {
    v: 1,
    id: 'm-1',
    type: 'hello',
    payload: {
      token: 'test-token',
      modVersion: 'test',
      gameVersion: 'test',
      gameLanguage: 'zh-CN',
      capabilities,
    },
  };
}

async function waitFor(predicate, timeoutMs = 1500, label = 'condition') {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await delay(5);
  }
  return false;
}

function makeAgent(host, callOptions = {}, opts = {}) {
  const state = { result: null, calls: 0 };
  const agent = {
    info: { provider: 'test', model: 'test', available: true },
    onClientCapabilities(caps) {
      host.setCapabilities(caps);
    },
    async run(_text, promptId, emit) {
      state.calls += 1;
      if (opts.activity) emit.toolActivity?.(opts.activity);
      host.bind(emit);
      try {
        state.result = await emit.callTool(
          callOptions.name ?? 'game_get_overview',
          callOptions.args ?? {},
          { timeoutMs: callOptions.timeoutMs ?? 800 },
        );
      } finally {
        host.unbind(emit);
      }
      emit.message(state.result.ok ? 'ok' : `fail:${state.result.error?.code}`, promptId, true);
    },
    onConfigChanged() {},
    async deleteHistory(saveKey, text, timestamp) {
      state.deleted = { saveKey, text, timestamp };
      return { ok: true, removed: 2 };
    },
    dispose() {},
  };
  return { agent, state };
}

function makeSession({ callOptions, activity } = {}) {
  const socket = new FakeSocket();
  const host = new GameToolHost();
  const { agent, state } = makeAgent(host, callOptions, { activity });
  const session = new Session(socket, {
    token: 'test-token',
    serverVersion: 'test',
    agent,
    config: makeConfig(),
    tools: toolNamesForCapabilities,
  });
  return { socket, host, session, state };
}

async function run() {
  console.log('── 1. capability → ready.tools ──');
  {
    const { socket, session } = makeSession();
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    const ready = socket.frames.find((f) => f.type === 'ready');
    check('收到 ready', !!ready);
    const tools = ready?.payload?.tools ?? [];
    check('六个只读 + 四个 kb + 两个 wiki + 三个蓝图 + 脚本', tools.length === 16, JSON.stringify(tools));
    check('包含 game_get_production', tools.includes('game_get_production'));
    check('包含 kb_search / kb_get / kb_recipe_tree / kb_plan', ['kb_search', 'kb_get', 'kb_recipe_tree', 'kb_plan'].every((name) => tools.includes(name)));

    const empty = makeSession();
    empty.session.handleFrame(Buffer.from(JSON.stringify(hello([]))), false);
    const emptyReady = empty.socket.frames.find((f) => f.type === 'ready');
    const emptyTools = emptyReady?.payload?.tools ?? [];
    check('无 game 能力时仍声明 kb/wiki/blueprint/脚本', emptyTools.length === 10 && emptyTools.every((name) => name.startsWith('kb_') || name.startsWith('wiki_') || name.startsWith('blueprint_') || name === 'run_script'));
  }

  console.log('── 2. tool_call → tool_result 往返 ──');
  {
    const { socket, session, state } = makeSession({ callOptions: { timeoutMs: 900 } });
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-2', type: 'prompt', payload: { text: '测试' } })),
      false,
    );

    const gotCall = await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    check('sidecar 发出 tool_call', gotCall);
    const call = socket.frames.find((f) => f.type === 'tool_call');
    check('tool_call.name 正确', call?.payload?.name === 'game_get_overview');
    check('tool_call.callId 存在', typeof call?.payload?.callId === 'string');

    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-3',
          type: 'tool_result',
          replyTo: call.payload.callId,
          payload: { ok: true, data: { display: '测试概览' }, error: null, meta: { gameTick: 7 } },
        }),
      ),
      false,
    );

    const gotMessage = await waitFor(() => socket.frames.some((f) => f.type === 'message'));
    check('工具结果回填后 agent 继续并发出 message', gotMessage);
    check('agent 拿到 ok=true', state.result?.ok === true);
    check('agent 拿到 display', state.result?.data?.display === '测试概览');
    check('meta.gameTick 透传', state.result?.meta?.gameTick === 7);
  }

  console.log('── 3. 超时 → timeout ──');
  {
    const { socket, session, state } = makeSession({ callOptions: { timeoutMs: 80 } });
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-2', type: 'prompt', payload: { text: '测试' } })),
      false,
    );
    const gotCall = await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    check('超时用例也发出了 tool_call', gotCall);
    const gotMessage = await waitFor(() => socket.frames.some((f) => f.type === 'message'), 1500);
    check('超时后 agent 不悬挂', gotMessage);
    check('错误码是 timeout', state.result?.error?.code === 'timeout', state.result?.error?.code);
  }

  console.log('── 4. 断线 → tool_unavailable ──');
  {
    const { socket, session, state } = makeSession({ callOptions: { timeoutMs: 5000 } });
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-2', type: 'prompt', payload: { text: '测试' } })),
      false,
    );
    await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    session.dispose('test disconnect');
    const gotResult = await waitFor(() => state.result !== null, 1500);
    check('断线后 agent 立刻拿到失败结果', gotResult);
    check('错误码是 tool_unavailable', state.result?.error?.code === 'tool_unavailable', state.result?.error?.code);
  }

  console.log('── 5. 非法 tool_result 不崩 ──');
  {
    const { socket, session, state } = makeSession({ callOptions: { timeoutMs: 900 } });
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-2', type: 'prompt', payload: { text: '测试' } })),
      false,
    );
    const call = await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    check('非法用例收到 tool_call', call);
    const frame = socket.frames.find((f) => f.type === 'tool_call');
    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-3',
          type: 'tool_result',
          replyTo: frame.payload.callId,
          payload: { ok: 'yes' },
        }),
      ),
      false,
    );
    const gotMessage = await waitFor(() => socket.frames.some((f) => f.type === 'message'));
    check('非法结果被转为错误而非异常', gotMessage);
    check('错误码是 bad_request', state.result?.error?.code === 'bad_request', state.result?.error?.code);
  }

  console.log('── 6. history.delete（附录 A.7） ──');
  {
    const { socket, session, state } = makeSession();
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-60',
          type: 'save.bind',
          payload: { saveKey: 'save-a', saveName: 'A' },
        }),
      ),
      false,
    );
    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-61',
          type: 'history.delete',
          payload: { text: '被删的问题', timestamp: 1760000000000 },
        }),
      ),
      false,
    );
    const gotDeleted = await waitFor(() => socket.frames.some((f) => f.type === 'history.deleted'));
    const deleted = socket.frames.find((f) => f.type === 'history.deleted');
    check('收到 history.deleted', gotDeleted);
    check('replyTo 指向请求', deleted?.replyTo === 'm-61');
    check('ok=true', deleted?.payload?.ok === true);
    check('removed 透传', deleted?.payload?.removed === 2);
    check('saveKey 回落到当前绑定', deleted?.payload?.saveKey === 'save-a');
    check(
      'agent 收到文本与时间戳',
      state.deleted?.text === '被删的问题' && state.deleted?.timestamp === 1760000000000,
    );

    const bad = makeSession();
    bad.session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    bad.session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-62', type: 'history.delete', payload: {} })),
      false,
    );
    const gotBad = await waitFor(() => bad.socket.frames.some((f) => f.type === 'history.deleted'));
    const badReply = bad.socket.frames.find((f) => f.type === 'history.deleted');
    check('缺 text 报 bad_request', gotBad && badReply?.payload?.ok === false && badReply?.payload?.error === 'bad_request');
  }

  console.log('── 7. tool_activity（附录 A.8） ──');
  {
    const activity = { callId: 'local:x1', phase: 'start', name: 'kb_plan', args: '{"rate":20}' };
    const { socket, session } = makeSession({ activity });
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-70', type: 'prompt', payload: { text: '规划' } })),
      false,
    );
    const got = await waitFor(() => socket.frames.some((f) => f.type === 'tool_activity'));
    check('收到 tool_activity', got);
    const frame = socket.frames.find((f) => f.type === 'tool_activity');
    check('callId 不变', frame?.payload?.callId === 'local:x1');
    check('phase 透传', frame?.payload?.phase === 'start');
    check('name 透传', frame?.payload?.name === 'kb_plan');
    check('args 透传', frame?.payload?.args === '{"rate":20}');
  }

  console.log();
  if (failed === 0) {
    console.log(`✅ tools 测试全部通过（${passed} 项）`);
    process.exit(0);
  }
  console.log(`❌ tools 测试失败 ${failed} / ${passed + failed} 项`);
  process.exit(1);
}

run().catch((err) => {
  console.error(err);
  process.exit(1);
});
