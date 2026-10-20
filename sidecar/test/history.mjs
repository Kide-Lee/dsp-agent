#!/usr/bin/env node
/**
 * 历史会话回放测试（M2.7）：
 *   - pi SessionEntry → UI 消息的投影（只取 user/assistant 文本）
 *   - save.bind → save.bound.history / messageCount
 *   - 历史读取失败不阻断绑定
 *
 *   node test/history.mjs
 */

import { Session } from '../dist/session.js';
import { extractHistoryMessages } from '../dist/agent.js';

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

function makeAgent(history = [], fail = false) {
  return {
    info: { provider: 'test', model: 'test', available: true },
    run: async () => {},
    getHistory: async (saveKey) => {
      if (fail) throw new Error('history read failed');
      return saveKey ? history : [];
    },
    onSaveChanged: () => {},
    dispose: () => {},
  };
}

function helloFrame(saveKey = null) {
  return {
    v: 1,
    id: 'h-1',
    type: 'hello',
    payload: {
      token: 'test-token',
      modVersion: 'test',
      gameVersion: 'test-1.0',
      gameLanguage: 'zh-CN',
      capabilities: [],
      saveKey,
    },
  };
}

async function bind(agent, saveKey = 'saveA') {
  const socket = new FakeSocket();
  const session = new Session(socket, {
    token: 'test-token',
    serverVersion: 'test',
    agent,
    config,
    tools: () => [],
  }, () => {});
  session.handleFrame(Buffer.from(JSON.stringify(helloFrame())), false);
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1,
    id: 's-1',
    type: 'save.bind',
    payload: { saveKey, saveName: '存档 A' },
  })), false);
  for (let i = 0; i < 100 && !socket.frames.some((f) => f.type === 'save.bound'); i++) await delay(2);
  return { socket, session };
}

// ---- 1. 提取规则 ----
console.log('── 1. SessionEntry → UI 消息 ──');
const extracted = extractHistoryMessages([
  { type: 'message', timestamp: '2026-01-01T00:00:00Z', message: { role: 'user', content: '你好', timestamp: 1 } },
  {
    type: 'message',
    timestamp: '2026-01-01T00:00:01Z',
    message: {
      role: 'assistant',
      content: [
        { type: 'text', text: '你好，' },
        { type: 'thinking', text: '内部思考' },
        { type: 'text', text: '世界' },
      ],
      timestamp: 2,
    },
  },
  { type: 'message', message: { role: 'toolResult', content: [{ type: 'text', text: '工具结果' }] } },
  { type: 'message', message: { role: 'assistant', content: [{ type: 'thinking', text: '只有思考' }] } },
  { type: 'message', message: { role: 'user', content: '' } },
  { type: 'model_change', provider: 'deepseek', modelId: 'x' },
]);
check('只保留 user/assistant 文本', extracted.length === 2, JSON.stringify(extracted));
check('user 文本', extracted[0]?.role === 'user' && extracted[0]?.text === '你好');
check('assistant 文本块拼接，忽略 thinking', extracted[1]?.role === 'assistant' && extracted[1]?.text === '你好，\n世界');
check('timestamp 来自 message', extracted[1]?.timestamp === 2);

const clipped = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: 'x'.repeat(30) } },
], 10, 10);
check('单条文本截断', clipped[0]?.text === `${'x'.repeat(10)}…`, clipped[0]?.text);

const many = extractHistoryMessages(
  Array.from({ length: 10 }, (_, i) => ({ type: 'message', message: { role: 'user', content: `m${i}` } })),
  3,
);
check('只取最近 N 条', many.length === 3 && many[0]?.text === 'm7' && many[2]?.text === 'm9', JSON.stringify(many));

// ---- 2. save.bound 带历史 ----
console.log('── 2. save.bound.history ──');
const history = [
  { role: 'user', text: '上次的问题' },
  { role: 'assistant', text: '上次的回答' },
];
{
  const { socket } = await bind(makeAgent(history));
  const bound = socket.frames.find((f) => f.type === 'save.bound');
  check('收到 save.bound', Boolean(bound));
  check('messageCount = 历史条数', bound?.payload?.messageCount === 2, JSON.stringify(bound?.payload));
  check('history 数组透传', Array.isArray(bound?.payload?.history) && bound.payload.history.length === 2);
  check('history role/text', bound?.payload?.history?.[0]?.text === '上次的问题' && bound?.payload?.history?.[1]?.role === 'assistant');
}

// ---- 3. 无历史 / 读取失败 ----
console.log('── 3. 降级 ──');
{
  const { socket } = await bind(makeAgent([], false));
  const bound = socket.frames.find((f) => f.type === 'save.bound');
  check('无历史时 messageCount=0', bound?.payload?.messageCount === 0);
  check('无历史时 history=[]', Array.isArray(bound?.payload?.history) && bound.payload.history.length === 0);
}
{
  const { socket } = await bind(makeAgent([], true));
  const bound = socket.frames.find((f) => f.type === 'save.bound');
  check('历史读取失败仍发 save.bound', Boolean(bound));
  check('读取失败按 0 条处理', bound?.payload?.messageCount === 0 && bound?.payload?.history?.length === 0);
}

// ---- 4. 解绑 ----
console.log('── 4. 解绑 ──');
{
  const { socket } = await bind(makeAgent(history), null);
  const bound = socket.frames.find((f) => f.type === 'save.bound');
  check('解绑时 messageCount=0', bound?.payload?.messageCount === 0);
  check('解绑时 history=[]', bound?.payload?.history?.length === 0);
}

// ---- 5. reply.time 回存 ----
console.log('── 5. reply.time ──');
{
  const calls = [];
  const agent = makeAgent(history);
  agent.recordReplyTime = (saveKey, prompt, reply, gameTime) => calls.push({ saveKey, prompt, reply, gameTime });
  const { session } = await bind(agent, 'saveA');
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1,
    id: 'm-1',
    type: 'reply.time',
    payload: { prompt: '问题', reply: '回答', gameTime: '08:42' },
  })), false);
  check('reply.time 调用 agent', calls.length === 1, JSON.stringify(calls));
  check('reply.time saveKey', calls[0]?.saveKey === 'saveA');
  check('reply.time prompt', calls[0]?.prompt === '问题');
  check('reply.time reply', calls[0]?.reply === '回答');
  check('reply.time gameTime', calls[0]?.gameTime === '08:42');

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1,
    id: 'm-2',
    type: 'reply.time',
    payload: { prompt: '', gameTime: '09:00' },
  })), false);
  check('空 prompt 被忽略', calls.length === 1);
}

console.log(`\n${failed === 0 ? '✅' : '❌'} history 测试${failed === 0 ? '全部通过' : '有失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
