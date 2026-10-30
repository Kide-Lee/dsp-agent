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
import {
  CAPABILITY_MOD_BRIDGE,
  createGameTools,
  toolNamesForCapabilities,
} from '../dist/tools/registry.js';
import { isWriteToolName, writeToolRisk } from '../dist/tools/write.js';
import { createAskTool, UI_ASK_TOOL_NAME, UI_ASK_TOOL_NAME_ALIASES } from '../dist/tools/ask.js';

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

function makeConfig(permissionLevel = 'limited') {
  const view = () => ({ state: 'ready', provider: 'test', model: 'test', permissionLevel, message: null });
  return { view, update: view };
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
  const state = { result: null, calls: 0, requiredTool: undefined };
  const agent = {
    info: { provider: 'test', model: 'test', available: true },
    onClientCapabilities(caps) {
      host.setCapabilities(caps);
    },
    toolCatalog() {
      return [{ name: 'game_get_overview', label: '存档概览', description: '测试目录项' }];
    },
    async run(_text, promptId, emit, requiredTool) {
      state.calls += 1;
      state.requiredTool = requiredTool ?? null;
      if (opts.activity) emit.toolActivity?.(opts.activity);
      host.bind(emit);
      try {
        state.result = await emit.callTool(
          callOptions.name ?? 'game_get_overview',
          callOptions.args ?? {},
          { timeoutMs: callOptions.timeoutMs ?? 800, risk: callOptions.risk ?? 0 },
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

function makeSession({ callOptions, activity, permission = 'limited' } = {}) {
  const socket = new FakeSocket();
  const host = new GameToolHost();
  const { agent, state } = makeAgent(host, callOptions, { activity });
  const session = new Session(socket, {
    token: 'test-token',
    serverVersion: 'test',
    agent,
    config: makeConfig(permission),
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
    check('六个只读 + 四个 kb + 两个 wiki + 五个蓝图 + 脚本', tools.length === 18, JSON.stringify(tools));
    check('包含 game_get_production', tools.includes('game_get_production'));
    check('包含 kb_search / kb_get / kb_recipe_tree / kb_plan', ['kb_search', 'kb_get', 'kb_recipe_tree', 'kb_plan'].every((name) => tools.includes(name)));
    const catalog = ready?.payload?.toolCatalog ?? [];
    check('ready.toolCatalog 由 agent 提供', catalog.length === 1 && catalog[0].label === '存档概览', JSON.stringify(catalog));

    {
      const host = new GameToolHost();
      host.setCapabilities(['game.read.v1', 'game.entity.v1', 'game.drop.v1', 'game.build.v1', 'game.storage.v1', 'game.tether.v1']);
      const tools = createGameTools(host);
      const entity = tools.find((tool) => tool.name === 'game_get_entity');
      const planet = tools.find((tool) => tool.name === 'game_get_planet');
      check('game_get_entity 暴露 fields', !!entity?.parameters?.properties?.fields);
      check('game_get_entity 暴露 entityIds', !!entity?.parameters?.properties?.entityIds);
      check('game_get_planet 暴露 veins', !!planet?.parameters?.properties?.veins);
      check('game_get_planet 暴露 veinOffset/veinLimit',
        !!planet?.parameters?.properties?.veinOffset && !!planet?.parameters?.properties?.veinLimit);
      const drop = tools.find((tool) => tool.name === 'game_drop_item');
      check('game_drop_item 受 game.drop.v1 注册', !!drop);
      check('game_drop_item 参数含 itemId/count',
        !!drop?.parameters?.properties?.itemId && !!drop?.parameters?.properties?.count);
      const recipe = tools.find((tool) => tool.name === 'game_set_recipe');
      const batch = tools.find((tool) => tool.name === 'game_set_assembler_batch');
      check('game_set_recipe 受 game.build.v1 注册', !!recipe);
      check('game_set_assembler_batch 受 game.build.v1 注册', !!batch);
      check('批量工具暴露 selections', !!batch?.parameters?.properties?.selections);
      check('存储过滤器工具存在', !!tools.find((tool) => tool.name === 'game_storage_set_filter'));
      check('存储上限工具存在', !!tools.find((tool) => tool.name === 'game_storage_set_limit'));
      check('取成品工具存在', !!tools.find((tool) => tool.name === 'game_take_output'));
      check('移库工具存在', !!tools.find((tool) => tool.name === 'game_storage_move'));
      check('存入工具存在', !!tools.find((tool) => tool.name === 'game_storage_deposit'));
      check('取出工具存在', !!tools.find((tool) => tool.name === 'game_storage_withdraw'));
      check('投料工具存在', !!tools.find((tool) => tool.name === 'game_feed_input'));
      check('方位添加工具存在', !!tools.find((tool) => tool.name === 'game_tether_add'));
      check('方位清除工具存在', !!tools.find((tool) => tool.name === 'game_tether_clear'));
      check('方位列表工具存在', !!tools.find((tool) => tool.name === 'game_tether_list'));
      check('拆除工具已按 design §9.1 移除', !tools.find((tool) => tool.name === 'game_dismantle'));
    }

    const empty = makeSession();
    empty.session.handleFrame(Buffer.from(JSON.stringify(hello([]))), false);
    const emptyReady = empty.socket.frames.find((f) => f.type === 'ready');
    const emptyTools = emptyReady?.payload?.tools ?? [];
    check('无 game 能力时仍声明 kb/wiki/blueprint/脚本', emptyTools.length === 12 && emptyTools.every((name) => name.startsWith('kb_') || name.startsWith('wiki_') || name.startsWith('blueprint_') || name === 'run_script'));
  }

  console.log('── 1b. 权限不是能力：只读仍注册写工具，但写操作走确认 ──');
  {
    const caps = ['game.read.v1', 'game.build.v1'];
    const { socket, session, state } = makeSession({
      permission: 'readonly',
      callOptions: { name: 'game_set_recipe', risk: 2, timeoutMs: 900 },
    });
    session.handleFrame(Buffer.from(JSON.stringify(hello(caps))), false);
    const ready = socket.frames.find((f) => f.type === 'ready');
    const tools = ready?.payload?.tools ?? [];
    check('只读 ready.tools 仍含写工具', tools.includes('game_set_recipe'), JSON.stringify(tools));
    check('只读 ready.tools 仍含读工具', tools.includes('game_get_overview'));
    check('只读 ready.config.permissionLevel', ready?.payload?.config?.permissionLevel === 'readonly');

    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-1b', type: 'prompt', payload: { text: '改配方' } })),
      false,
    );
    const gotConfirm = await waitFor(() => socket.frames.some((f) => f.type === 'confirm_request'));
    check('只读写操作先发 confirm_request', gotConfirm);
    const confirm = socket.frames.find((f) => f.type === 'confirm_request');
    check('确认请求工具名正确', confirm?.payload?.tool === 'game_set_recipe');
    check('确认请求 risk=2', confirm?.payload?.risk === 2, confirm?.payload?.risk);
    check('确认前没有 tool_call', !socket.frames.some((f) => f.type === 'tool_call'));

    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-1b-c',
          type: 'confirm_result',
          replyTo: confirm.id,
          payload: { requestId: confirm.payload.requestId, decision: 'allow', token: confirm.payload.token },
        }),
      ),
      false,
    );
    const gotCall = await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    check('玩家同意后发出 tool_call', gotCall);
    const call = socket.frames.find((f) => f.type === 'tool_call');
    check('tool_call 携带确认令牌', typeof call?.payload?.confirmToken === 'string');
    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-1b-r',
          type: 'tool_result',
          replyTo: call.payload.callId,
          payload: { ok: true, data: { display: 'ok' }, error: null, meta: {} },
        }),
      ),
      false,
    );
    await waitFor(() => state.result !== null, 1500);
    check('只读写操作按正常提交执行', state.result?.ok === true, JSON.stringify(state.result));
  }

  console.log('── 1c. prompt.requiredTool 透传给 agent ──');
  {
    const { session, state } = makeSession({ callOptions: { timeoutMs: 80 } });
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({
        v: 1,
        id: 'm-2',
        type: 'prompt',
        payload: { text: '/game_get_overview 测试', requiredTool: 'game_get_overview' },
      })),
      false,
    );
    await waitFor(() => state.requiredTool !== undefined);
    check('agent.run 收到 requiredTool', state.requiredTool === 'game_get_overview', String(state.requiredTool));
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

  console.log('── 8. ask_user 工具目录 ──');
  {
    const names = toolNamesForCapabilities(['ui.ask.v1']);
    check('声明 ui.ask.v1 时广告 ask_user', names.includes(UI_ASK_TOOL_NAME));
    const without = toolNamesForCapabilities([]);
    check('未声明能力时不广告 ask_user', !without.includes(UI_ASK_TOOL_NAME));
    check('保留 ui_ask_user 旧名别名', UI_ASK_TOOL_NAME_ALIASES.includes('ui_ask_user'));
    const ask = createAskTool(new GameToolHost());
    check('ask 工具名', ask.name === UI_ASK_TOOL_NAME);
    check('ask 工具 label', ask.label === '向玩家提问');
    check('ask 参数是 object', ask.parameters?.type === 'object');
  }

  console.log('── 9. ask_user 长超时往返 ──');
  {
    const { socket, session, state } = makeSession({
      callOptions: {
        name: 'ask_user',
        args: {
          questions: [
            { question: '今晚想吃什么？', options: [{ title: '火锅' }, { title: '烧烤' }] },
            { question: '几点吃？', options: [{ title: '六点' }, { title: '七点' }], allowMultiple: true },
          ],
        },
        timeoutMs: 600000,
      },
    });
    session.handleFrame(Buffer.from(JSON.stringify(hello(['game.read.v1', 'ui.ask.v1']))), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-90', type: 'prompt', payload: { text: '问我' } })),
      false,
    );

    const gotCall = await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    check('发出 ask_user', gotCall);
    const call = socket.frames.find((f) => f.type === 'tool_call');
    check('ask_user 名', call?.payload?.name === 'ask_user');
    check('ask_user timeout=600000', call?.payload?.timeoutMs === 600000, String(call?.payload?.timeoutMs));

    const answers = {
      v: 1,
      id: 'm-91',
      type: 'tool_result',
      replyTo: call?.payload?.callId,
      payload: {
        callId: call?.payload?.callId,
        ok: true,
        data: {
          answers: [
            { index: 0, question: '今晚想吃什么？', answer: '火锅', optionIndex: 0,
              answers: ['火锅'], optionIndexes: [0], custom: false, skipped: false },
            { index: 1, question: '几点吃？', answer: null, optionIndex: -1,
              answers: [], optionIndexes: [], custom: false, skipped: true },
          ],
        },
        error: null,
        meta: {},
      },
    };
    session.handleFrame(Buffer.from(JSON.stringify(answers)), false);
    await waitFor(() => state.result !== null);
    check('ask_user 结果 ok', state.result?.ok === true);
    check('ask_user 答案数', state.result?.data?.answers?.length === 2);
  }


  console.log('── 10. 写操作 confirm_request + 一次性令牌 ──');
  {
    const { socket, session, state } = makeSession({
      callOptions: {
        name: 'game_open_panel',
        args: { panel: 'inventory' },
        risk: 1,
        timeoutMs: 800,
      },
    });
    session.handleFrame(
      Buffer.from(JSON.stringify(hello(['game.read.v1', 'game.write.v1']))),
      false,
    );
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-100', type: 'prompt', payload: { text: '打开背包' } })),
      false,
    );

    const gotConfirm = await waitFor(() =>
      socket.frames.some((f) => f.type === 'confirm_request'),
    );
    check('写操作先发 confirm_request', gotConfirm);
    const confirm = socket.frames.find((f) => f.type === 'confirm_request');
    check('confirm_request 带 tool', confirm?.payload?.tool === 'game_open_panel');
    check('confirm_request 带 token', typeof confirm?.payload?.token === 'string' && confirm.payload.token.length > 20);
    check('confirm_request 带 argsHash', typeof confirm?.payload?.argsHash === 'string');
    check('确认前没有 tool_call', !socket.frames.some((f) => f.type === 'tool_call'));

    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-101',
          type: 'confirm_result',
          payload: {
            requestId: confirm?.payload?.requestId,
            decision: 'allow',
            token: confirm?.payload?.token,
          },
        }),
      ),
      false,
    );

    const gotCall = await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    check('确认后发 tool_call', gotCall);
    const call = socket.frames.find((f) => f.type === 'tool_call');
    check('tool_call risk=1', call?.payload?.risk === 1);
    check('tool_call 带 confirmToken', call?.payload?.confirmToken === confirm?.payload?.token);
    check('tool_call 带 confirmArgsHash', call?.payload?.confirmArgsHash === confirm?.payload?.argsHash);

    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-102',
          type: 'tool_result',
          replyTo: call?.payload?.callId,
          payload: { callId: call?.payload?.callId, ok: true, data: { display: '已打开背包' }, error: null, meta: {} },
        }),
      ),
      false,
    );
    await waitFor(() => state.result !== null);
    check('写工具结果回传', state.result?.ok === true);
  }

  console.log('── 11. 拒绝确认不执行 ──');
  {
    const { socket, session, state } = makeSession({
      callOptions: { name: 'game_set_research', args: { techId: 1 }, risk: 2, timeoutMs: 800 },
    });
    session.handleFrame(
      Buffer.from(JSON.stringify(hello(['game.read.v1', 'game.write.v1']))),
      false,
    );
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-110', type: 'prompt', payload: { text: '切研究' } })),
      false,
    );
    const confirm = await waitFor(() => socket.frames.some((f) => f.type === 'confirm_request')) &&
      socket.frames.find((f) => f.type === 'confirm_request');
    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-111',
          type: 'confirm_result',
          payload: { requestId: confirm?.payload?.requestId, decision: 'deny', token: confirm?.payload?.token },
        }),
      ),
      false,
    );
    await waitFor(() => state.result !== null);
    check('拒绝后返回 confirmation_denied', state.result?.error?.code === 'confirmation_denied');
    check('拒绝后没有 tool_call', !socket.frames.some((f) => f.type === 'tool_call'));
  }

  console.log('── 12. 丢弃确认卡说明“需手动拾回” ──');
  {
    const { socket, session, state } = makeSession({
      callOptions: {
        name: 'game_drop_item',
        args: { itemId: 1001, count: 3 },
        risk: 2,
        timeoutMs: 800,
      },
    });
    session.handleFrame(Buffer.from(JSON.stringify(hello(['game.drop.v1']))), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-120', type: 'prompt', payload: { text: '丢掉 3 个' } })),
      false,
    );

    const confirm = await waitFor(() => socket.frames.some((f) => f.type === 'confirm_request')) &&
      socket.frames.find((f) => f.type === 'confirm_request');
    check('丢弃先发 confirm_request', !!confirm);
    check('丢弃确认 title', confirm?.payload?.title === '丢弃物品？');
    check('丢弃确认 body 含手动拾回', String(confirm?.payload?.body ?? '').includes('手动拾回'));
    check('丢弃确认 body 含数量', String(confirm?.payload?.body ?? '').includes('3'));
    check('丢弃确认 risk=2', confirm?.payload?.risk === 2);

    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-121',
          type: 'confirm_result',
          payload: { requestId: confirm?.payload?.requestId, decision: 'deny', token: confirm?.payload?.token },
        }),
      ),
      false,
    );
    await waitFor(() => state.result !== null);
    check('丢弃拒绝后不执行', state.result?.error?.code === 'confirmation_denied');
    check('丢弃拒绝后没有 tool_call', !socket.frames.some((f) => f.type === 'tool_call'));
  }

  console.log('── 13. 批量改配方：一次确认、一次撤销 ──');
  {
    const { socket, session, state } = makeSession({
      callOptions: {
        name: 'game_set_assembler_batch',
        args: { selections: [{ buildingId: 11, recipeId: 2 }, { buildingId: 12, recipeId: 2 }] },
        risk: 2,
        timeoutMs: 800,
      },
    });
    session.handleFrame(Buffer.from(JSON.stringify(hello(['game.build.v1']))), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-130', type: 'prompt', payload: { text: '这两台改成同一个配方' } })),
      false,
    );
    const confirm = await waitFor(() => socket.frames.some((f) => f.type === 'confirm_request')) &&
      socket.frames.find((f) => f.type === 'confirm_request');
    check('批量改配方先发 confirm_request', !!confirm);
    check('批量确认 title', confirm?.payload?.title === '批量更换配方？');
    check('批量确认 body 含两台', String(confirm?.payload?.body ?? '').includes('2 台'));
    check('批量确认 body 含建筑 id', String(confirm?.payload?.body ?? '').includes('#11'));
    check('批量确认 risk=2', confirm?.payload?.risk === 2);

    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-131',
          type: 'confirm_result',
          payload: { requestId: confirm?.payload?.requestId, decision: 'allow', token: confirm?.payload?.token },
        }),
      ),
      false,
    );
    const gotCall = await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
    check('批量确认后只发一次 tool_call', gotCall && socket.frames.filter((f) => f.type === 'tool_call').length === 1);
    const call = socket.frames.find((f) => f.type === 'tool_call');
    check('批量 tool_call 参数完整', JSON.stringify(call?.payload?.args ?? {}).includes('buildingId'));
    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-132',
          type: 'tool_result',
          replyTo: call?.payload?.callId,
          payload: { callId: call?.payload?.callId, ok: true, data: { display: '批量完成' }, error: null, meta: {} },
        }),
      ),
      false,
    );
    await waitFor(() => state.result !== null);
    check('批量结果回传', state.result?.ok === true);
  }


  console.log('── 14. 脚本写租约：一次确认、可释放 ──');
  {
    const { socket, session } = makeSession();
    session.handleFrame(Buffer.from(JSON.stringify(hello(['game.build.v1']))), false);
    const pending = session.confirmScript({
      scriptHash: 'script-hash-123',
      writeNames: ['game_set_recipe'],
      maxWrites: 5,
    });
    const confirm = await waitFor(() => socket.frames.some((f) => f.type === 'confirm_request')) &&
      socket.frames.find((f) => f.type === 'confirm_request');
    check('脚本确认 tool=run_script', confirm?.payload?.tool === 'run_script');
    check('脚本确认 argsHash=脚本哈希', confirm?.payload?.argsHash === 'script-hash-123');
    check('脚本确认 maxWrites=5', confirm?.payload?.maxWrites === 5);
    check('脚本确认 title', confirm?.payload?.title === '脚本包含写操作？');
    check('脚本确认 body 含写工具', String(confirm?.payload?.body ?? '').includes('game_set_recipe'));

    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-140',
          type: 'confirm_result',
          payload: { requestId: confirm?.payload?.requestId, decision: 'allow', token: confirm?.payload?.token },
        }),
      ),
      false,
    );
    const lease = await pending;
    check('脚本确认返回 token', typeof lease?.token === 'string' && lease.token.length > 20);
    check('脚本确认返回哈希', lease?.scriptHash === 'script-hash-123');

    session.releaseScript(lease.token);
    const released = await waitFor(() => socket.frames.some((f) => f.type === 'confirm_release'));
    check('释放脚本租约', released);
    session.dispose('test');
  }


  console.log('── 15. 标记确认：可撤销写操作 ──');
  {
    const { socket, session, state } = makeSession({
      callOptions: {
        name: 'game_place_marker',
        args: { latitude: 1.5, longitude: 2.5, text: '矿点' },
        risk: 1,
        timeoutMs: 800,
      },
    });
    session.handleFrame(Buffer.from(JSON.stringify(hello(['game.write.v1']))), false);
    session.handleFrame(
      Buffer.from(JSON.stringify({ v: 1, id: 'm-150', type: 'prompt', payload: { text: '在这里标一下' } })),
      false,
    );
    const confirm = await waitFor(() => socket.frames.some((f) => f.type === 'confirm_request')) &&
      socket.frames.find((f) => f.type === 'confirm_request');
    check('标记先发 confirm_request', !!confirm);
    check('标记确认 title', confirm?.payload?.title === '新增标记？');
    check('标记确认 body 含文字', String(confirm?.payload?.body ?? '').includes('矿点'));
    check('标记确认 risk=1', confirm?.payload?.risk === 1);
    session.handleFrame(
      Buffer.from(
        JSON.stringify({
          v: 1,
          id: 'm-151',
          type: 'confirm_result',
          payload: { requestId: confirm?.payload?.requestId, decision: 'deny', token: confirm?.payload?.token },
        }),
      ),
      false,
    );
    await waitFor(() => state.result !== null);
    check('标记拒绝后不执行', state.result?.error?.code === 'confirmation_denied');
    check('标记拒绝后没有 tool_call', !socket.frames.some((f) => f.type === 'tool_call'));
  }

  console.log('── 16. 第三方 mod 工具与插件同生命周期 ──');
  {
    const extension = {
      id: 'aux',
      guid: 'Auxilaryfunction',
      name: 'Auxilaryfunction',
      version: '3.1.1',
      configCount: 72,
      tools: [],
    };
    const adapterTool = {
      name: 'mod_aux_open',
      label: '打开窗口',
      description: '测试用 manifest 工具',
      risk: 1,
      timeoutMs: 5000,
      parameters: { type: 'object', properties: {} },
    };
    const names = toolNamesForCapabilities([CAPABILITY_MOD_BRIDGE], [extension]);
    check('声明能力+插件时广告 mods_list', names.includes('mods_list'));
    check('声明能力+插件时广告 mod_aux_config', names.includes('mod_aux_config'));
    check('未声明能力时不广告 mod_aux_config', !toolNamesForCapabilities([], [extension]).includes('mod_aux_config'));
    check('没有插件时不广告 mod_aux_config', !toolNamesForCapabilities([CAPABILITY_MOD_BRIDGE], []).includes('mod_aux_config'));
    check(
      '没有配置项的插件不产生工具',
      !toolNamesForCapabilities([CAPABILITY_MOD_BRIDGE], [{ ...extension, configCount: 0 }]).includes(
        'mod_aux_config',
      ),
    );
    check('mods_config_set 仍记为写工具', isWriteToolName('mods_config_set'));
    check('mods_config_set risk=1', writeToolRisk('mods_config_set') === 1);
    check('未知写工具仍按 risk 2', writeToolRisk('mods_unknown_tool') === 2);

    const host = new GameToolHost();
    host.setCapabilities([CAPABILITY_MOD_BRIDGE]);
    host.setExtensions([extension]);
    const tool = createGameTools(host).find((item) => item.name === 'mod_aux_config');
    check('per-mod 工具已注册', !!tool);
    check('per-mod 工具参数是 object', tool?.parameters?.type === 'object');
    check('per-mod 工具有 action 参数', !!tool?.parameters?.properties?.action);
    check('per-mod 工具描述带 mod 名', String(tool?.description ?? '').includes('Auxilaryfunction'));

    // manifest 工具：与 config 工具一样绑定插件生命周期。
    const withTool = { ...extension, tools: [adapterTool] };
    const toolNames = toolNamesForCapabilities([CAPABILITY_MOD_BRIDGE], [withTool]);
    check('manifest 工具进入工具名列表', toolNames.includes('mod_aux_open'));
    const toolHost = new GameToolHost();
    toolHost.setCapabilities([CAPABILITY_MOD_BRIDGE]);
    toolHost.setExtensions([withTool]);
    const manifestTool = createGameTools(toolHost).find((item) => item.name === 'mod_aux_open');
    check('manifest 工具已注册', !!manifestTool);
    check('manifest 工具使用插件给的 schema', manifestTool?.parameters?.type === 'object');
    check('manifest 工具描述来自 manifest', String(manifestTool?.description ?? '').includes('测试用'));

    // 卸载插件：下一条 hello 不再列出它，工具随之消失。
    host.setExtensions([]);
    check('卸载后工具不再生成', !createGameTools(host).some((item) => item.name === 'mod_aux_config'));
    toolHost.setExtensions([extension]);
    check(
      '卸载后 manifest 工具不再生成',
      !createGameTools(toolHost).some((item) => item.name === 'mod_aux_open'),
    );

    // 会话层：hello.extensions 要真的进入 ready.tools。
    const withExt = makeSession();
    const helloWithExt = hello([CAPABILITY_MOD_BRIDGE]);
    helloWithExt.payload.extensions = [extension];
    withExt.session.handleFrame(Buffer.from(JSON.stringify(helloWithExt)), false);
    const readyWithExt = withExt.socket.frames.find((frame) => frame.type === 'ready');
    check('ready.tools 含 per-mod 工具', readyWithExt?.payload?.tools?.includes('mod_aux_config'));

    const withoutExt = makeSession();
    withoutExt.session.handleFrame(Buffer.from(JSON.stringify(hello([CAPABILITY_MOD_BRIDGE]))), false);
    const readyWithoutExt = withoutExt.socket.frames.find((frame) => frame.type === 'ready');
    check('没有 extensions 时 ready.tools 不含 per-mod 工具', !readyWithoutExt?.payload?.tools?.includes('mod_aux_config'));
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
