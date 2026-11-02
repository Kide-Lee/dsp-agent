#!/usr/bin/env node
/**
 * M4m：调试直调（!tool）的离线测试。
 *
 *   node test/tool-debug.mjs
 *
 * 覆盖：tool_debug 解析、Session 直调、不进入 prompt、开关关闭、
 * 任意工具（含写工具与 run_script）都可直调、目录不带 debuggable 字段。
 */

import { Session } from '../dist/session.js';
import {
  buildReadyPayload,
  parseToolDebugPayload,
} from '../dist/protocol.js';
import { GameToolHost } from '../dist/tools/host.js';
import { PiAgent } from '../dist/agent.js';
import '../dist/tools/registry.js';
import { makeTempDir } from './helpers.mjs';

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

const config = {
  view: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
  update: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
};

function hello() {
  return {
    v: 1,
    id: 'h-1',
    type: 'hello',
    payload: {
      token: 'test-token',
      modVersion: 'test',
      gameVersion: 'test',
      gameLanguage: 'zh-CN',
      capabilities: ['game.read.v1'],
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

function makeAgent({ withDebug = true, result } = {}) {
  const state = { debugCalls: 0, runCalls: 0, name: null, args: null };
  const agent = {
    info: { provider: 'test', model: 'test', available: true },
    onClientCapabilities() {},
    onConfigChanged() {},
    async run() { state.runCalls += 1; },
    dispose() {},
  };
  if (withDebug) {
    agent.debugTool = async (name, args) => {
      state.debugCalls += 1;
      state.name = name;
      state.args = args;
      return result ?? {
        ok: true,
        tool: name,
        source: 'game',
        durationMs: 7,
        content: '{"ok":true}',
        details: { ok: true, data: { answer: 42 } },
        error: null,
        truncated: false,
        image: null,
      };
    };
  }
  return { agent, state };
}

async function makeSession({ debugTools = true, withDebug = true, result } = {}) {
  const socket = new FakeSocket();
  const { agent, state } = makeAgent({ withDebug, result });
  const session = new Session(socket, {
    token: 'test-token',
    serverVersion: 'test',
    agent,
    config,
    tools: ['game_get_power'],
    debugTools,
    helloTimeoutMs: 500,
  });
  session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
  await waitFor(() => socket.frames.some((f) => f.type === 'ready'));
  return { session, socket, state };
}

function send(session, frame) {
  session.handleFrame(Buffer.from(JSON.stringify(frame)), false);
}

// ---------------------------------------------------------------- 1. 载荷解析

console.log('── 1. tool_debug 载荷解析 ──');
{
  const parsed = parseToolDebugPayload({ name: ' game_get_power ', args: { planetId: 1 } });
  check('name trim', parsed?.name === 'game_get_power', parsed?.name);
  check('args 保留', parsed?.args?.planetId === 1);
  check('args 缺省为 {}', parseToolDebugPayload({ name: 'game_get_player' })?.args?.constructor === Object);
  check('args 数组拒绝', parseToolDebugPayload({ name: 'x', args: [] }) === null);
  check('空 name 拒绝', parseToolDebugPayload({ name: '   ' }) === null);
  check('非对象拒绝', parseToolDebugPayload(null) === null);
}

// ---------------------------------------------------------------- 2. 直调

console.log('\n── 2. 调试直调 ──');
{
  const { session, socket, state } = await makeSession();
  send(session, {
    v: 1, id: 'd-1', type: 'tool_debug',
    payload: { name: 'game_get_power', args: { planetId: 2 } },
  });
  const got = await waitFor(() => socket.frames.some((f) => f.type === 'tool_debug_result'));
  check('收到 tool_debug_result', got);
  const result = socket.frames.find((f) => f.type === 'tool_debug_result');
  check('replyTo 匹配', result?.replyTo === 'd-1', result?.replyTo);
  check('结果 ok', result?.payload?.ok === true);
  check('details 原样返回', result?.payload?.details?.data?.answer === 42);
  check('durationMs 写入', result?.payload?.durationMs === 7);
  check('没有 prompt 帧', !socket.frames.some((f) => f.type === 'prompt'));
  check('没有 status 帧', !socket.frames.some((f) => f.type === 'status'));
  check('没有调用 agent.run', state.runCalls === 0);
  check('只调用了一次 debugTool', state.debugCalls === 1);
  check('参数透传', state.args?.planetId === 2 && state.name === 'game_get_power');
  session.dispose('test done');
}

// ---------------------------------------------------------------- 3. 开关/缺失

console.log('\n── 3. 开关与 agent 缺失 ──');
{
  const { session, socket } = await makeSession({ debugTools: false });
  send(session, { v: 1, id: 'd-2', type: 'tool_debug', payload: { name: 'game_get_power', args: {} } });
  const got = await waitFor(() => socket.frames.some((f) => f.type === 'error'));
  const error = socket.frames.find((f) => f.type === 'error');
  check('关闭时拒绝', got && error?.payload?.code === 'debug_disabled', error?.payload?.code);
  session.dispose('test done');
}
{
  const { session, socket } = await makeSession({ withDebug: false });
  send(session, { v: 1, id: 'd-3', type: 'tool_debug', payload: { name: 'game_get_power', args: {} } });
  const got = await waitFor(() => socket.frames.some((f) => f.type === 'error'));
  const error = socket.frames.find((f) => f.type === 'error');
  check('agent 缺失时拒绝', got && error?.payload?.code === 'tool_unavailable', error?.payload?.code);
  session.dispose('test done');
}

// ---------------------------------------------------------------- 4. 目录

console.log('\n── 4. toolCatalog 不带 debuggable ──');
{
  const payload = buildReadyPayload({
    serverVersion: 'test',
    sessionId: 's1',
    agent: { provider: 'test', model: 'test', available: true },
    tools: ['game_get_power'],
    toolCatalog: [
      { name: 'game_get_power', label: '电力状态', description: '读取电网' },
      { name: 'game_copy_blueprint', label: '存为蓝图', description: '写文件' },
    ],
  });
  check('toolCatalog 不写 debuggable', payload.toolCatalog?.every((entry) => !('debuggable' in entry)) === true);

  // 调试直调期间 host 调用必须标记 internal，mod 才不会画普通工具卡。
  const host = new GameToolHost();
  let seen = null;
  host.bind({
    callTool: async (_name, _args, options) => {
      seen = options;
      return { ok: true, data: {}, error: null, meta: {} };
    },
  });
  host.setDebugMode(true);
  await host.call('game_get_power', {}, { timeoutMs: 1000 });
  check('调试期间 host 调用 internal=true', seen?.internal === true);
  host.setDebugMode(false);
  await host.call('game_get_power', {}, { timeoutMs: 1000 });
  check('普通调用 internal=false', seen?.internal === false);
}

// ---------------------------------------------------------------- 5. PiAgent 实际执行

console.log('\n── 5. PiAgent.debugTool 实际执行 ──');
{
  const store = {
    get: () => ({ apiKey: null, provider: 'test', model: 'test', thinkingLevel: 'high' }),
    hasKey: () => false,
    maskedApiKey: null,
    blueprintRoot: () => null,
    wikiEndpoint: () => null,
  };
  const host = new GameToolHost();
  host.setCapabilities(['game.read.v1', 'game.entity.v1', 'game.view.v1']);
  const agent = new PiAgent({ dataDir: makeTempDir('icarus-debug-'), store, host });
  const emit = {
    isActive: () => true,
    callTool: async (name, args) => ({
      ok: true,
      data: name === 'game_capture_view' ? { legend: [] } : { demandKw: 1, args },
      error: null,
      meta: {},
      ...(name === 'game_capture_view'
        ? { image: { mimeType: 'image/jpeg', data: 'AAAA', width: 320, height: 200 } }
        : {}),
    }),
    status() {}, stream() {}, message() {}, error() {}, configState() {},
    toolActivity() {}, blueprintCard() {},
  };

  const result = await agent.debugTool('game_get_power', {}, emit);
  check('PiAgent 直调成功', result.ok === true, result.error?.code);
  check('PiAgent 结果 source=game', result.source === 'game');
  check('PiAgent 结果 details 原样', result.details?.data?.demandKw === 1);
  check('PiAgent 直调没有 content 截断', result.truncated === false);

  const script = await agent.debugTool('run_script', { code: 'return 1' }, emit);
  check('PiAgent 允许 run_script', script.ok === true, script.error?.code);

  // 写工具不再被调试直调拒绝；确认与令牌仍由 host/transport 负责。
  const writeTool = await agent.debugTool(
    'game_copy_blueprint',
    { planetId: 1, entityIds: [1] },
    emit,
  );
  check('PiAgent 允许写工具 game_copy_blueprint', writeTool.ok === true, writeTool.error?.code);

  // entityId 现在可为空（批量走 entityIds），用错误的字段类型验证 schema 仍会拦截。
  const invalid = await agent.debugTool('game_get_entity', { entityId: 'bad' }, emit);
  check('PiAgent schema 校验', invalid.ok === false && invalid.error?.code === 'invalid_args');

  const fieldsArray = await agent.debugTool('game_get_entity', { entityId: 1, fields: ['assembler'] }, emit);
  check('game_get_entity fields 数组可校验', fieldsArray.ok === true, fieldsArray.error?.code);
  const fieldsString = await agent.debugTool('game_get_entity', { entityId: 1, fields: 'assembler' }, emit);
  check('game_get_entity fields 字符串可校验', fieldsString.ok === true, fieldsString.error?.code);
  const batchIds = await agent.debugTool('game_get_entity', { entityIds: [1, 2] }, emit);
  check('game_get_entity entityIds 可校验', batchIds.ok === true, batchIds.error?.code);

  const shot = await agent.debugTool('game_capture_view', { width: 320, height: 200 }, emit);
  check('PiAgent 截图成功', shot.ok === true, shot.error?.code);
  check('截图只保留元数据', shot.image?.mimeType === 'image/jpeg' && shot.image?.bytes > 0);
  check('截图 details 不携带 base64', shot.details?.image?.data === undefined);

  const catalog = agent.toolCatalog(['game.read.v1', 'game.entity.v1', 'game.view.v1']);
  check('目录包含 run_script', Boolean(catalog.find((t) => t.name === 'run_script')));
  check('目录包含 game_copy_blueprint', Boolean(catalog.find((t) => t.name === 'game_copy_blueprint')));
  check('目录不带 debuggable 字段', catalog.every((t) => !('debuggable' in t)));
  agent.dispose();
}

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
