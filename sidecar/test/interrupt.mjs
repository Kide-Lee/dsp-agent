#!/usr/bin/env node
/**
 * A.15 打断与插话回归：
 *   1. prompt.mode 解析（缺省 queue / steer / 非法值拒绝）；
 *   2. abort 消息 → agent.abort 被调用，且只在真的停了回合时回 status(aborting)；
 *   3. prompt{mode:"steer"} 走 agent.steer，不再排进串行链、不开新回合；
 *   4. 没有运行中的回合时，steer 退回普通提问（不能把玩家的话丢掉）；
 *   5. message.interrupted 的负载构造。
 *
 *   node test/interrupt.mjs
 */

import { Session } from '../dist/session.js';
import { MessageType, buildMessagePayload, parsePromptPayload } from '../dist/protocol.js';

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
  close() { this.isOpen = false; }
}

const config = {
  view: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
  update: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
};

/** 记录 abort/steer/run 调用的假 agent。 */
function makeAgent(options = {}) {
  const calls = { abort: 0, steer: [], run: [] };
  let running = options.running === true;
  let release = null;
  const agent = {
    info: { provider: 'test', model: 'test', available: true },
    isRunning: () => running,
    run: async (text, promptId, emit) => {
      calls.run.push(text);
      running = true;
      emit.status('thinking');
      if (options.blocking) {
        // 一直跑到 abort 放行，模拟真实流式回合。
        await new Promise((resolve) => { release = resolve; });
      } else {
        await delay(5);
      }
      emit.message(`回答-${text}`, promptId, true);
      emit.status('idle');
      running = false;
    },
    abort: async () => {
      calls.abort += 1;
      release?.();
      return true;
    },
    steer: async (text) => {
      calls.steer.push(text);
      return options.steerable === true && running;
    },
    onSaveChanged: () => {},
    getActiveConversationId: () => 'conv-a',
    dispose: () => {},
  };
  return {
    agent,
    calls,
    setRunning: (value) => { running = value; },
  };
}

async function newSession(agent) {
  const socket = new FakeSocket();
  const session = new Session(socket, {
    token: 'test-token',
    serverVersion: 'test',
    agent,
    config,
    tools: () => [],
  }, () => {});
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'h-1', type: 'hello',
    payload: {
      token: 'test-token', modVersion: 'test', gameVersion: 'test-1.0',
      gameLanguage: 'zh-CN', capabilities: [], saveKey: null,
    },
  })), false);
  // prompt 要求先绑定存档。
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 's-1', type: 'save.bind',
    payload: { saveKey: 'saveA', saveName: '存档 A' },
  })), false);
  await delay(10);
  return { socket, session };
}

const frameOf = (socket, type) => socket.frames.find((f) => f.type === type);

// ---- 1. prompt.mode 解析 ----
console.log('── 1. prompt.mode ──');
check('缺省 mode = queue', parsePromptPayload({ text: 'a' })?.mode === 'queue');
check('mode=steer 保留', parsePromptPayload({ text: 'a', mode: 'steer' })?.mode === 'steer');
check('mode=queue 保留', parsePromptPayload({ text: 'a', mode: 'queue' })?.mode === 'queue');
check('非法 mode 被拒绝', parsePromptPayload({ text: 'a', mode: 'nope' }) === null);
check('mode 非字符串被拒绝', parsePromptPayload({ text: 'a', mode: 1 }) === null);

// ---- 2. abort ----
console.log('── 2. abort ──');
{
  const { agent, calls, setRunning } = makeAgent();
  const { socket, session } = await newSession(agent);

  // 没有回合在跑：什么都不做（不能凭空发一个 aborting 把界面卡在“回答中”）。
  setRunning(false);
  session.handleFrame(Buffer.from(JSON.stringify({ v: 1, id: 'a-0', type: 'abort', payload: {} })), false);
  await delay(10);
  check('空闲时 abort 不调用 agent.abort', calls.abort === 0);
  check('空闲时 abort 不回 status', frameOf(socket, 'status') === undefined);

  setRunning(true);
  session.handleFrame(Buffer.from(JSON.stringify({ v: 1, id: 'a-1', type: 'abort', payload: {} })), false);
  await delay(10);
  const status = frameOf(socket, 'status');
  check('回答中 abort 回 status', status !== undefined);
  check('status.state = aborting', status?.payload?.state === 'aborting');
  check('abort 调用了 agent.abort', calls.abort === 1);
}

// ---- 2b. aborting 必须在终稿和 idle 之前 ----
console.log('── 2b. aborting 的帧序 ──');
{
  const { agent } = makeAgent({ blocking: true });
  const { socket, session } = await newSession(agent);

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'p-ab', type: 'prompt',
    payload: { text: '慢慢说', conversationId: 'conv-a' },
  })), false);
  await delay(20);
  session.handleFrame(Buffer.from(JSON.stringify({ v: 1, id: 'a-2', type: 'abort', payload: {} })), false);
  await delay(40);

  const order = socket.frames
    .filter((f) => f.type === 'status' || f.type === 'message')
    .map((f) => (f.type === 'message' ? 'message' : `status:${f.payload.state}`))
    .join(',');
  check('帧序 = thinking,aborting,message,idle', order === 'status:thinking,status:aborting,message,status:idle', order);
}

// ---- 3. 插话 ----
console.log('── 3. 插话 ──');
{
  const { agent, calls, setRunning } = makeAgent({ running: true, steerable: true });
  const { socket, session } = await newSession(agent);

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'p-1', type: 'prompt',
    payload: { text: '顺便看一下电力', conversationId: 'conv-a', mode: 'steer' },
  })), false);
  await delay(20);
  check('插话调用 agent.steer', calls.steer.length === 1 && calls.steer[0] === '顺便看一下电力');
  check('插话不新开回合', calls.run.length === 0);
  check('插话不产生终稿', frameOf(socket, 'message') === undefined);
}

// ---- 4. 没有运行中的回合时退回普通提问 ----
console.log('── 4. 插话退化为普通提问 ──');
{
  const { agent, calls, setRunning } = makeAgent({ running: false, steerable: true });
  const { socket, session } = await newSession(agent);

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'p-2', type: 'prompt',
    payload: { text: '插话但没在跑', conversationId: 'conv-a', mode: 'steer' },
  })), false);
  await delay(40);
  check('先尝试插话', calls.steer.length === 1);
  check('插话失败后退回普通提问', calls.run.length === 1 && calls.run[0] === '插话但没在跑');
  check('普通提问有终稿', frameOf(socket, 'message') !== undefined);

  setRunning(false);
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'p-3', type: 'prompt',
    payload: { text: '普通提问', conversationId: 'conv-a' },
  })), false);
  await delay(40);
  check('普通提问不调用 steer', calls.steer.length === 1);
  check('普通提问开新回合', calls.run.length === 2);
}

// ---- 5. interrupted 终稿 ----
console.log('── 5. message.interrupted ──');
{
  const normal = buildMessagePayload([{ type: 'text', text: '答案' }], { role: 'assistant', final: true });
  check('普通终稿没有 interrupted', normal.interrupted === undefined);
  const stopped = buildMessagePayload([{ type: 'text', text: '半截' }], {
    role: 'assistant',
    final: true,
    interrupted: true,
  });
  check('中断终稿带 interrupted', stopped.interrupted === true);
  check('中断终稿保留内容', stopped.content[0].text === '半截');
}

console.log();
if (failed === 0) {
  console.log(`✅ interrupt: ${passed} 项通过`);
  process.exit(0);
}
console.log(`❌ interrupt: ${failed} / ${passed + failed} 项失败`);
process.exit(1);
