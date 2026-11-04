#!/usr/bin/env node
/**
 * 并行会话：
 *   - 两个会话可以同时跑，互不阻塞；
 *   - 焦点决定哪些运行期帧可见，切走/切回不打断后台；
 *   - conversation.switched 带回 running/state，并补发已生成的正文；
 *   - abort 只中断目标会话；
 *   - 后台会话的 status/message/stream 不污染焦点会话。
 *
 *   node test/parallel-sessions.mjs
 */

import { Session } from '../dist/session.js';

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

function makeConfig(permission) {
  return {
    view: () => ({
      state: 'ready', provider: 'test', model: 'test',
      permissionLevel: permission, message: null,
    }),
    update() { return this.view(); },
  };
}

function conversation(id) {
  return {
    id,
    title: `会话 ${id}`,
    createdAt: 1,
    updatedAt: 1,
    recordCount: 0,
    records: [],
  };
}

function makeAgent() {
  let activeId = 'conv-a';
  const list = [conversation('conv-a'), conversation('conv-b')];
  const pending = new Map();
  const started = [];
  const chains = new Map();
  let aborted = [];

  const runOnce = async (text, promptId, emit, _requiredTool, conversationId) => {
    started.push(conversationId);
    const entry = { text, promptId, emit, partial: `${conversationId}:${text}`, aborted: false };
    pending.set(conversationId, entry);
    emit.status('thinking', 'test');
    emit.stream('text', entry.partial, promptId);
    if (text.startsWith('tool:')) {
      entry.toolResult = await emit.callTool(
        'game_set_research',
        { techId: 1 },
        { timeoutMs: 5000, risk: 2 },
      );
    }
    await new Promise((resolve) => { entry.resolve = resolve; });
    if (entry.aborted) {
      emit.message('已中断', promptId, true, { interrupted: true });
    } else {
      emit.stream('text', '!', promptId);
      emit.message(`${conversationId}:${text}!`, promptId, true);
    }
    emit.status('idle');
    pending.delete(conversationId);
  };

  return {
    info: { provider: 'test', model: 'test', available: true },
    started,
    get aborted() { return aborted; },
    run(text, promptId, emit, requiredTool, conversationId) {
      const previous = chains.get(conversationId) ?? Promise.resolve();
      const next = previous.then(
        () => runOnce(text, promptId, emit, requiredTool, conversationId),
        () => runOnce(text, promptId, emit, requiredTool, conversationId),
      );
      chains.set(conversationId, next.catch(() => undefined));
      return next;
    },
    resolve(id) {
      const entry = pending.get(id);
      if (!entry) return false;
      entry.resolve();
      return true;
    },
    async abort(id) {
      const entry = pending.get(id);
      if (!entry) return false;
      entry.aborted = true;
      aborted.push(id);
      entry.resolve();
      return true;
    },
    isRunning(id) { return pending.has(id); },
    getProgress(id) {
      const entry = pending.get(id);
      return entry
        ? { text: entry.partial, thinking: '', running: true }
        : { text: '', thinking: '', running: false };
    },
    onSaveChanged: () => {},
    getActiveConversationId: () => activeId,
    getConversations: async () => list,
    getHistory: async () => [],
    getHistoryFor: async () => [],
    async createConversation() {
      const created = conversation(`conv-new-${list.length}`);
      list.unshift(created);
      activeId = created.id;
      return { ok: true, conversation: created, history: [] };
    },
    async switchConversation(_key, id) {
      if (!list.some((item) => item.id === id)) return { ok: false, error: 'conversation_not_found' };
      activeId = id;
      return { ok: true, conversation: list.find((item) => item.id === id), history: [] };
    },
    async deleteConversation(_key, id) {
      const index = list.findIndex((item) => item.id === id);
      if (index < 0) return { ok: false, error: 'conversation_not_found' };
      list.splice(index, 1);
      if (activeId === id) activeId = list[0]?.id ?? null;
      return { ok: true, activeConversationId: activeId };
    },
    renameConversation: async (_key, _id, title) => ({ ok: true, title }),
    recordReplyTime: () => {},
    deleteHistory: async () => ({ ok: true, removed: 0 }),
    undoCompaction: async () => ({ ok: true, removed: 0 }),
  };
}

async function newSession(agent, permission = 'takeover') {
  const socket = new FakeSocket();
  const session = new Session(socket, {
    token: 'test-token',
    serverVersion: 'test',
    agent,
    config: makeConfig(permission),
    tools: () => [],
    createSessionId: () => 'sess-parallel',
  });
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'h-1', type: 'hello',
    payload: {
      token: 'test-token', modVersion: 'test', gameVersion: 'test-1.0',
      gameLanguage: 'zh-CN', capabilities: [], saveKey: null,
    },
  })), false);
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 's-1', type: 'save.bind',
    payload: { saveKey: 'saveA', saveName: '存档 A' },
  })), false);
  await waitFor(() => socket.frames.some((frame) => frame.type === 'save.bound'));
  socket.frames.length = 0;
  return { socket, session };
}

async function waitFor(predicate, timeout = 2000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await delay(2);
  }
  return false;
}

const send = (session, type, payload, id) => session.handleFrame(
  Buffer.from(JSON.stringify({ v: 1, id, type, payload })),
  false,
);

console.log('── 1. 两个会话同时跑 ──');
{
  const agent = makeAgent();
  const { socket, session } = await newSession(agent);

  send(session, 'prompt', { text: '甲', conversationId: 'conv-a' }, 'p-a');
  send(session, 'prompt', { text: '乙', conversationId: 'conv-b' }, 'p-b');

  check('A 已开始', await waitFor(() => agent.started.includes('conv-a')));
  check('B 未被 A 阻塞，已开始', await waitFor(() => agent.started.includes('conv-b')));
  check('A 的流式帧可见', await waitFor(() => socket.frames.some((f) =>
    f.type === 'stream' && f.payload?.conversationId === 'conv-a')));
  check('B 的流式帧被焦点过滤', !socket.frames.some((f) =>
    f.type === 'stream' && f.payload?.conversationId === 'conv-b'));
  check('A 运行中显示 loading', socket.frames.some((f) =>
    f.type === 'conversation.activity' && f.payload?.conversationId === 'conv-a' &&
    f.payload?.state === 'running'));
  check('B 运行中显示 loading', socket.frames.some((f) =>
    f.type === 'conversation.activity' && f.payload?.conversationId === 'conv-b' &&
    f.payload?.state === 'running'));

  send(session, 'prompt', { text: '丙', conversationId: 'conv-a' }, 'p-a2');
  await delay(30);
  check('同会话第二次提问排在 A 后面', !agent.started.includes('conv-a') || agent.started.filter((id) => id === 'conv-a').length === 1);

  agent.resolve('conv-a');
  check('A 完成后焦点收到终稿', await waitFor(() => socket.frames.some((f) =>
    f.type === 'message' && f.payload?.conversationId === 'conv-a' && f.payload?.final === true)));
  check('焦点 A 完成后恢复 chevron', socket.frames.some((f) =>
    f.type === 'conversation.activity' && f.payload?.conversationId === 'conv-a' &&
    f.payload?.state === 'idle'));
  check('B 在 A 完成后仍在跑', agent.isRunning('conv-b'));

  send(session, 'conversation.switch', { conversationId: 'conv-b' }, 'sw-b');
  const switched = await (async () => {
    await waitFor(() => socket.frames.some((f) => f.type === 'conversation.switched'));
    return socket.frames.find((f) => f.type === 'conversation.switched');
  })();
  check('切换到运行中的 B 不报 busy', switched?.payload?.running === true, JSON.stringify(switched?.payload));
  check('切回 B 补发已生成正文', await waitFor(() => socket.frames.some((f) =>
    f.type === 'stream' && f.payload?.conversationId === 'conv-b' && f.payload?.delta === 'conv-b:乙')));

  agent.resolve('conv-b');
  check('B 完成后收到终稿', await waitFor(() => socket.frames.some((f) =>
    f.type === 'message' && f.payload?.conversationId === 'conv-b')));
  check('B 的 idle 清掉 busy', await waitFor(() => socket.frames.some((f) =>
    f.type === 'status' && f.payload?.conversationId === 'conv-b' && f.payload?.state === 'idle')));

  // 同会话第二次提问在 A 结束后才启动。
  check('A 的排队提问最终启动', await waitFor(() => agent.started.filter((id) => id === 'conv-a').length === 2));
  agent.resolve('conv-a');
}

console.log('\n── 2. 后台完成不污染焦点 ──');
{
  const agent = makeAgent();
  const { socket, session } = await newSession(agent);
  send(session, 'prompt', { text: '甲', conversationId: 'conv-a' }, 'p-a');
  send(session, 'prompt', { text: '乙', conversationId: 'conv-b' }, 'p-b');
  await waitFor(() => agent.isRunning('conv-a') && agent.isRunning('conv-b'));
  send(session, 'conversation.switch', { conversationId: 'conv-b' }, 'sw-b');
  await waitFor(() => socket.frames.some((f) => f.type === 'conversation.switched'));

  agent.resolve('conv-a');
  await delay(40);
  check('后台 A 的终稿带 conversationId 送达 mod', socket.frames.some((f) =>
    f.type === 'message' && f.payload?.conversationId === 'conv-a' && f.payload?.final === true));
  check('后台 A 完成显示 new', socket.frames.some((f) =>
    f.type === 'conversation.activity' && f.payload?.conversationId === 'conv-a' &&
    f.payload?.state === 'completed'));
  check('后台 A 的 idle 没有清掉 B 的按钮', !socket.frames.some((f) =>
    f.type === 'status' && f.payload?.conversationId === 'conv-a' && f.payload?.state === 'idle'));

  agent.resolve('conv-b');
  await waitFor(() => socket.frames.some((f) =>
    f.type === 'message' && f.payload?.conversationId === 'conv-b'));

  send(session, 'conversation.switch', { conversationId: 'conv-a' }, 'sw-a');
  const switched = await (async () => {
    await waitFor(() => socket.frames.filter((f) => f.type === 'conversation.switched').length >= 2);
    return socket.frames.filter((f) => f.type === 'conversation.switched').at(-1);
  })();
  check('切回已完成的 A 显示 idle', switched?.payload?.running === false && switched?.payload?.state === 'idle');
  check('切回后清除 new', socket.frames.some((f) =>
    f.type === 'conversation.activity' && f.payload?.conversationId === 'conv-a' &&
    f.payload?.state === 'idle'));
}

console.log('\n── 3. abort 只影响目标会话 ──');
{
  const agent = makeAgent();
  const { socket, session } = await newSession(agent);
  send(session, 'prompt', { text: '甲', conversationId: 'conv-a' }, 'p-a');
  send(session, 'prompt', { text: '乙', conversationId: 'conv-b' }, 'p-b');
  await waitFor(() => agent.isRunning('conv-a') && agent.isRunning('conv-b'));

  send(session, 'abort', { conversationId: 'conv-b' }, 'abort-b');
  await waitFor(() => agent.aborted.includes('conv-b'));
  check('只中断 B', agent.aborted.length === 1 && agent.aborted[0] === 'conv-b');
  check('A 仍在跑', agent.isRunning('conv-a'));

  agent.resolve('conv-a');
  check('A 的终稿仍能送到焦点', await waitFor(() => socket.frames.some((f) =>
    f.type === 'message' && f.payload?.conversationId === 'conv-a')));
  check('B 的中断终稿带 id 送达 mod（由 mod 静默处理）', socket.frames.some((f) =>
    f.type === 'message' && f.payload?.conversationId === 'conv-b' &&
    f.payload?.interrupted === true));
}

console.log('\n── 4. 后台等待授权显示 warning，不强制切会话 ──');
{
  const agent = makeAgent();
  const { socket, session } = await newSession(agent, 'limited');
  const switches = [];
  const originalSend = socket.send.bind(socket);
  socket.send = (text, cb) => {
    try {
      const frame = JSON.parse(text);
      if (frame.type === 'conversation.switched') switches.push(frame);
    } catch { /* ignore */ }
    originalSend(text, cb);
  };

  // 先切到 B，再让 A 在后台发起一个需要确认的写工具；不能从焦点 A 直接发。
  send(session, 'conversation.switch', { conversationId: 'conv-b' }, 'sw-b');
  await waitFor(() => socket.frames.some((f) => f.type === 'conversation.switched'));
  socket.frames.length = 0;
  send(session, 'prompt', { text: 'tool:甲', conversationId: 'conv-a' }, 'p-tool');
  await waitFor(() => socket.frames.some((f) =>
    f.type === 'conversation.activity' && f.payload?.conversationId === 'conv-a' &&
    f.payload?.state === 'attention'));
  check('后台会话等待授权显示 warning', socket.frames.some((f) =>
    f.type === 'conversation.activity' && f.payload?.conversationId === 'conv-a' &&
    f.payload?.state === 'attention' && f.payload?.reason === 'confirm'));
  check('未强制切回会话 A', switches.every((f) => f.payload?.conversationId === 'conv-b'),
    JSON.stringify(switches.map((f) => f.payload?.conversationId)));
  check('切到 A 之前不发确认页', !socket.frames.some((f) => f.type === 'confirm_request'));

  send(session, 'conversation.switch', { conversationId: 'conv-a' }, 'sw-a');
  await waitFor(() => socket.frames.some((f) => f.type === 'confirm_request'));
  const confirm = socket.frames.find((f) => f.type === 'confirm_request');
  check('切到 A 后才发确认页', confirm?.payload?.conversationId === 'conv-a');

  send(session, 'confirm_result', {
    requestId: confirm.payload.requestId,
    decision: 'allow',
    token: confirm.payload.token,
  }, 'cr-1');
  await waitFor(() => socket.frames.some((f) => f.type === 'tool_call'));
  const call = socket.frames.find((f) => f.type === 'tool_call');
  check('确认后执行目标会话工具', call?.payload?.conversationId === 'conv-a', JSON.stringify(call?.payload));
  send(session, 'tool_result', {
    callId: call.payload.callId,
    ok: true,
    data: {},
    error: null,
    meta: { gameTick: 1, saveKey: 'saveA', tickAtStart: 1, tickAtEnd: 1 },
  }, 'tr-1');
  check('授权完成后恢复运行态', await waitFor(() =>
    agent.started.filter((id) => id === 'conv-a').length === 1));
}

console.log(`\n${failed === 0 ? '✅' : '❌'} parallel-sessions 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
