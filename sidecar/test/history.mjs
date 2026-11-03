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

const withCompaction = extractHistoryMessages([
  { type: 'message', timestamp: '2026-01-01T00:00:00Z', message: { role: 'user', content: '旧问题', timestamp: 1 } },
  {
    type: 'compaction', id: 'c1', timestamp: '2026-01-01T00:00:02Z',
    summary: '之前的对话摘要', tokensBefore: 1000, estimatedTokensAfter: 200,
  },
  { type: 'message', timestamp: '2026-01-01T00:00:03Z', message: { role: 'user', content: '新问题', timestamp: 3 } },
]);
check('压缩记录被回放', withCompaction.length === 3, JSON.stringify(withCompaction));
check('压缩记录 kind', withCompaction[1]?.kind === 'compaction');
check('压缩记录摘要', withCompaction[1]?.text === '之前的对话摘要');
check('压缩记录 id/token', withCompaction[1]?.compactionId === 'c1' && withCompaction[1]?.tokensBefore === 1000 && withCompaction[1]?.estimatedTokensAfter === 200);

const clipped = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: 'x'.repeat(30) } },
], 10, 10);
check('单条文本截断', clipped[0]?.text === `${'x'.repeat(10)}…`, clipped[0]?.text);

const many = extractHistoryMessages(
  Array.from({ length: 10 }, (_, i) => ({ type: 'message', message: { role: 'user', content: `m${i}` } })),
  3,
);
check('只取最近 N 条', many.length === 3 && many[0]?.text === 'm7' && many[2]?.text === 'm9', JSON.stringify(many));

// ---- 1b. 思考/工具步骤随回答回放（附录 A.6 steps）----
console.log('── 1b. 思考/工具步骤 ──');
const withSteps = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: '查一下产量', timestamp: 1 } },
  {
    type: 'message',
    message: {
      role: 'assistant',
      content: [
        { type: 'thinking', thinking: '先看整体' },
        { type: 'toolCall', id: 't1', name: 'game_get_overview', arguments: { x: 1 } },
        { type: 'toolCall', id: 't2', name: 'kb_plan', arguments: {} },
      ],
      timestamp: 2,
    },
  },
  {
    type: 'message',
    message: {
      role: 'toolResult', toolCallId: 't1', toolName: 'game_get_overview', isError: false,
      content: [{ type: 'text', text: '结果 A\n第二行' }],
    },
  },
  {
    type: 'message',
    message: {
      role: 'toolResult', toolCallId: 't2', toolName: 'kb_plan', isError: true,
      content: [{ type: 'text', text: '失败原因' }],
    },
  },
  {
    type: 'message',
    message: {
      role: 'assistant',
      content: [
        { type: 'thinking', thinking: '再核对' },
        { type: 'toolCall', id: 't3', name: 'game_get_production', arguments: { item: 'iron' } },
      ],
      timestamp: 3,
    },
  },
  {
    type: 'message',
    message: {
      role: 'toolResult', toolCallId: 't3', toolName: 'game_get_production', isError: false,
      content: [{ type: 'text', text: '产量 30' }],
    },
  },
  {
    type: 'message',
    message: {
      role: 'assistant',
      content: [{ type: 'thinking', thinking: '得出答案' }, { type: 'text', text: '产量是 30' }],
      timestamp: 4,
    },
  },
]);

check('中间步骤不单独成消息', withSteps.length === 2, JSON.stringify(withSteps));
const steps = withSteps[1]?.steps ?? [];
check('步骤按发生顺序附加到最终回答', steps.length === 6, JSON.stringify(steps));
check('第一段是思考', steps[0]?.kind === 'thinking' && steps[0]?.text === '先看整体');
check('工具参数序列化', steps[1]?.kind === 'tool' && steps[1]?.name === 'game_get_overview' && steps[1]?.args === '{"x":1}');
check('空参数工具不带 args', steps[2]?.kind === 'tool' && steps[2]?.args === undefined);
check('结果回填 ok/summary/detail', steps[1]?.ok === true
  && steps[1]?.summary === '结果 A 第二行' && steps[1]?.detail === '结果 A\n第二行', JSON.stringify(steps[1]));
check('失败结果 ok=false', steps[2]?.ok === false, JSON.stringify(steps[2]));
check('第二轮思考也在列表里', steps[3]?.kind === 'thinking' && steps[3]?.text === '再核对');
check('收尾思考在正文之前', steps[5]?.kind === 'thinking' && steps[5]?.text === '得出答案');
check('user 消息不带 steps', withSteps[0]?.steps === undefined);

// ---- 1c. 过程正文按时刻回放（附录 A.6 kind:"text"）----
console.log('── 1c. 过程正文步骤 ──');
const withTextSteps = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: '看看电网', timestamp: 1 } },
  {
    type: 'message',
    message: {
      role: 'assistant',
      content: [
        { type: 'thinking', thinking: '先看整体' },
        { type: 'text', text: '我先看一下电网' },
        { type: 'toolCall', id: 'u1', name: 'game_get_overview', arguments: {} },
      ],
      timestamp: 2,
    },
  },
  {
    type: 'message',
    message: {
      role: 'toolResult', toolCallId: 'u1', toolName: 'game_get_overview', isError: false,
      content: [{ type: 'text', text: '电网正常' }],
    },
  },
  {
    type: 'message',
    message: {
      role: 'assistant',
      content: [{ type: 'thinking', thinking: '可以回答' }, { type: 'text', text: '电网正常。' }],
      timestamp: 3,
    },
  },
]);
check('过程正文不单独成回答', withTextSteps.length === 2, JSON.stringify(withTextSteps));
const textSteps = withTextSteps[1]?.steps ?? [];
check('过程正文按发生顺序插入', textSteps[1]?.kind === 'text' && textSteps[1]?.text === '我先看一下电网',
  JSON.stringify(textSteps));
check('过程正文排在它之后的工具前面', textSteps[2]?.kind === 'tool' && textSteps[2]?.name === 'game_get_overview');
check('回答只取最终正文', withTextSteps[1]?.text === '电网正常。');
check('回答正文不重复进 steps',
  !textSteps.some((step) => step.kind === 'text' && step.text === '电网正常。'), JSON.stringify(textSteps));

const clippedTextStep = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: 'q' } },
  {
    type: 'message',
    message: {
      role: 'assistant',
      content: [
        { type: 'text', text: 'x'.repeat(3000) },
        { type: 'toolCall', id: 'c1', name: 'game_get_overview', arguments: {} },
      ],
    },
  },
  { type: 'message', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
]);
check('过程正文截断到 2000', clippedTextStep[1]?.steps?.[0]?.text === `${'x'.repeat(2000)}…`,
  String(clippedTextStep[1]?.steps?.[0]?.text?.length));

const interrupted = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: 'hi' } },
  {
    type: 'message',
    message: {
      role: 'assistant',
      content: [
        { type: 'toolCall', id: 'tx', name: 'game_get_entity', arguments: {} },
        { type: 'text', text: '抱歉' },
      ],
    },
  },
]);
check('没有结果的工具不带 ok', interrupted[1]?.steps?.[0]?.ok === undefined, JSON.stringify(interrupted[1]?.steps));

const bigResult = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: 'big' } },
  { type: 'message', message: { role: 'assistant', content: [{ type: 'toolCall', id: 'b1', name: 'run_script', arguments: { a: 1 } }, { type: 'text', text: 'done' }] } },
  { type: 'message', message: { role: 'toolResult', toolCallId: 'b1', toolName: 'run_script', isError: false, content: [{ type: 'text', text: 'y'.repeat(5000) }] } },
]);
check('工具详情截断到 2000', bigResult[1]?.steps?.[0]?.detail === `${'y'.repeat(2000)}…`,
  String(bigResult[1]?.steps?.[0]?.detail?.length));

const abortedTurn = extractHistoryMessages([
  { type: 'message', message: { role: 'user', content: '第一问' } },
  { type: 'message', message: { role: 'assistant', content: [{ type: 'thinking', thinking: '没答完' }] } },
  { type: 'message', message: { role: 'user', content: '第二问' } },
  { type: 'message', message: { role: 'assistant', content: [{ type: 'text', text: '这次答了' }] } },
]);
check('中断回合的步骤不串到下一轮', abortedTurn.length === 3 && abortedTurn[2]?.steps === undefined,
  JSON.stringify(abortedTurn));

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
  agent.recordReplyTime = (saveKey, prompt, reply, gameTime, conversationId) =>
    calls.push({ saveKey, prompt, reply, gameTime, conversationId });
  const { session } = await bind(agent, 'saveA');
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1,
    id: 'm-1',
    type: 'reply.time',
    payload: { conversationId: 'conv-a', prompt: '问题', reply: '回答', gameTime: '08:42' },
  })), false);
  check('reply.time 调用 agent', calls.length === 1, JSON.stringify(calls));
  check('reply.time saveKey', calls[0]?.saveKey === 'saveA');
  check('reply.time prompt', calls[0]?.prompt === '问题');
  check('reply.time reply', calls[0]?.reply === '回答');
  check('reply.time gameTime', calls[0]?.gameTime === '08:42');
  check('reply.time conversationId', calls[0]?.conversationId === 'conv-a');

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1,
    id: 'm-2',
    type: 'reply.time',
    payload: { prompt: '', gameTime: '09:00' },
  })), false);
  check('空 prompt 被忽略', calls.length === 1);
}

// ---- 6. 撤销压缩 ----
console.log('── 6. history.undo_compaction ──');
{
  const calls = [];
  const agent = makeAgent(history);
  agent.undoCompaction = async (saveKey, compactionId, conversationId) => {
    calls.push({ saveKey, compactionId, conversationId });
    return { ok: true, removed: 1, compactionId: compactionId ?? 'c1', mayRecompact: true };
  };
  const { socket, session } = await bind(agent, 'saveA');
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1,
    id: 'm-3',
    type: 'history.undo_compaction',
    payload: { conversationId: 'conv-a', compactionId: 'c1' },
  })), false);
  for (let i = 0; i < 100 && !socket.frames.some((f) => f.type === 'history.compaction_undone'); i++) {
    await delay(2);
  }
  const res = socket.frames.find((f) => f.type === 'history.compaction_undone');
  check('撤销压缩回复', Boolean(res), JSON.stringify(socket.frames.map((f) => f.type)));
  check('撤销压缩 replyTo', res?.replyTo === 'm-3');
  check('撤销压缩 ok', res?.payload?.ok === true);
  check('撤销压缩 compactionId', res?.payload?.compactionId === 'c1');
  check('撤销压缩 mayRecompact', res?.payload?.mayRecompact === true);
  check('撤销压缩调用 agent', calls.length === 1 && calls[0]?.saveKey === 'saveA' && calls[0]?.compactionId === 'c1');
  check('撤销压缩 conversationId', calls[0]?.conversationId === 'conv-a');
}

console.log(`\n${failed === 0 ? '✅' : '❌'} history 测试${failed === 0 ? '全部通过' : '有失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
