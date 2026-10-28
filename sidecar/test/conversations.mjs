#!/usr/bin/env node
/**
 * A.13 多会话回归：
 *   1. Session 层的 conversation.create/switch/delete 消息与 save.bound 字段；
 *   2. PiAgent 的会话列表、活动会话持久化、切换、删除（真实 pi SessionManager 落盘）。
 *
 *   node test/conversations.mjs
 */

import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Session } from '../dist/session.js';
import { PiAgent, normalizeConversationRename, normalizeConversationTitle } from '../dist/agent.js';
import { MessageType, buildConversationUpdatedPayload } from '../dist/protocol.js';

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

function conv(id, title, records = []) {
  return {
    id,
    title,
    createdAt: 1000,
    updatedAt: 1000,
    recordCount: records.length,
    records,
  };
}

function historyFor(id) {
  return [
    { role: 'user', text: `问题-${id}`, timestamp: 1 },
    { role: 'assistant', text: `回答-${id}`, timestamp: 2 },
  ];
}

function makeAgent() {
  let saveKey = null;
  let activeId = 'conv-a';
  const list = [conv('conv-a', '会话 A', [{ text: '问题-a', timestamp: 1 }]), conv('conv-b', '会话 B', [{ text: '问题-b', timestamp: 2 }])];
  return {
    info: { provider: 'test', model: 'test', available: true },
    run: async () => {},
    onSaveChanged: (key) => { saveKey = key; },
    getActiveConversationId: () => activeId,
    getConversations: async () => list,
    getHistory: async () => (saveKey ? historyFor(activeId) : []),
    getHistoryFor: async (_key, id) => historyFor(id),
    createConversation: async () => {
      activeId = 'conv-new';
      const created = conv('conv-new', '新会话');
      list.unshift(created);
      return { ok: true, conversation: created, history: [] };
    },
    switchConversation: async (_key, id) => {
      const found = list.find((c) => c.id === id);
      if (!found) return { ok: false, error: 'conversation_not_found' };
      activeId = id;
      return { ok: true, conversation: found, history: historyFor(id) };
    },
    renameConversation: async (_key, id, title) => {
      const found = list.find((c) => c.id === id);
      if (!found) return { ok: false, error: 'conversation_not_found' };
      const clean = String(title ?? '').trim();
      if (!clean) return { ok: false, error: 'invalid_title' };
      found.title = clean;
      return { ok: true, title: clean };
    },
    deleteConversation: async (_key, id) => {
      const at = list.findIndex((c) => c.id === id);
      if (at < 0) return { ok: false, error: 'conversation_not_found' };
      list.splice(at, 1);
      if (activeId === id) activeId = list[0]?.id ?? 'conv-new';
      return { ok: true, activeConversationId: activeId };
    },
    getActiveConversationIdRaw: () => activeId,
    dispose: () => {},
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
  return { socket, session };
}

async function waitFrame(socket, type) {
  for (let i = 0; i < 200; i++) {
    const frame = socket.frames.find((f) => f.type === type);
    if (frame) return frame;
    await delay(2);
  }
  return null;
}

// ---- 1. Session 层 ----
console.log('── 1. Session 消息 ──');
{
  const agent = makeAgent();
  const { socket, session } = await newSession(agent);
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 's-1', type: 'save.bind',
    payload: { saveKey: 'saveA', saveName: '存档 A' },
  })), false);
  const bound = await waitFrame(socket, 'save.bound');
  check('save.bound 带回 activeConversationId', bound?.payload.activeConversationId === 'conv-a');
  check('save.bound 带回 conversations', bound?.payload.conversations?.length === 2);
  check('save.bound history 是活动会话', bound?.payload.history?.[0]?.text === '问题-conv-a');

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'c-1', type: 'conversation.create', payload: {},
  })), false);
  const created = await waitFrame(socket, 'conversation.created');
  check('conversation.create 回执', created?.payload?.conversation?.id === 'conv-new');
  check('conversation.create 切换 active', agent.getActiveConversationId() === 'conv-new');

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'c-2', type: 'conversation.switch', payload: { conversationId: 'conv-b' },
  })), false);
  const switched = await waitFrame(socket, 'conversation.switched');
  check('conversation.switch 回执', switched?.payload?.conversationId === 'conv-b');
  check('conversation.switch 带回历史', switched?.payload?.history?.length === 2);

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'c-3', type: 'conversation.delete', payload: { conversationId: 'conv-a' },
  })), false);
  const deleted = await waitFrame(socket, 'conversation.deleted');
  check('conversation.delete 回执', deleted?.payload?.ok === true);
  check('conversation.delete 带回剩余列表', deleted?.payload?.conversations?.length === 2);

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'c-4', type: 'conversation.rename',
    payload: { conversationId: 'conv-b', title: '  物流站改名  ' },
  })), false);
  const renamedFrame = await waitFrame(socket, 'conversation.renamed');
  check('conversation.rename 回执', renamedFrame?.payload?.ok === true);
  check('conversation.rename 回传标题', renamedFrame?.payload?.title === '物流站改名');
  check('conversation.rename 更新列表', agent.getConversations && (await agent.getConversations()).find((c) => c.id === 'conv-b')?.title === '物流站改名');

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'c-5', type: 'conversation.rename',
    payload: { conversationId: 'conv-b', title: '   ' },
  })), false);
  let emptyRename = null;
  for (let i = 0; i < 100 && !emptyRename; i++) {
    emptyRename = socket.frames.find((f) => f.type === 'conversation.renamed' && f.replyTo === 'c-5') ?? null;
    if (!emptyRename) await delay(2);
  }
  check('空标题被拒绝', emptyRename?.payload?.ok === false && emptyRename?.payload?.error === 'invalid_title');

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'p-1', type: 'prompt',
    payload: { text: '你好', conversationId: 'conv-a' },
  })), false);
  const mismatch = await waitFrame(socket, 'error');
  check('prompt 会话不匹配被拒绝', mismatch?.payload?.code === 'conversation_mismatch');
}

// ---- 2. PiAgent 落盘 ----
console.log('── 2. PiAgent 会话文件 ──');
{
  const dataDir = mkdtempSync(join(tmpdir(), 'icarus-conv-'));
  const sessionDir = join(dataDir, 'sessions', 'saveA');
  try {
    const pi = await import('@earendil-works/pi-coding-agent');
    const a = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-a' });
    a.appendMessage({ role: 'user', content: '第一条提问 A', timestamp: 1 });
    a.appendMessage({ role: 'assistant', content: [{ type: 'text', text: '回答 A' }], timestamp: 2 });
    await delay(5);
    const b = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-b' });
    b.appendMessage({ role: 'user', content: '第一条提问 B', timestamp: 3 });
    b.appendMessage({ role: 'assistant', content: [{ type: 'text', text: '回答 B' }], timestamp: 4 });

    const noop = { info() {}, warn() {}, error() {}, debug() {} };
    const agent = new PiAgent({
      dataDir,
      store: { get: () => ({}), hasKey: () => false },
      host: {},
      logger: { child: () => noop },
      loadSdk: () => import('@earendil-works/pi-coding-agent'),
    });
    agent.onSaveChanged('saveA');
    const conversations = await agent.getConversations('saveA');
    check('列出两个会话', conversations.length === 2, JSON.stringify(conversations.map((c) => c.id)));
    check('最近会话在前', conversations[0]?.id === 'conv-b');
    check('标题取第一条用户消息', conversations[0]?.title === '第一条提问 B');
    check('记录只含用户提问', conversations[0]?.records?.length === 1 && conversations[0]?.records[0]?.text === '第一条提问 B');
    check('active.json 已写入', existsSync(join(sessionDir, 'active.json')));
    const activeFile = JSON.parse(readFileSync(join(sessionDir, 'active.json'), 'utf8'));
    check('active.json 指向活动会话', activeFile.activeConversationId === 'conv-b');

    const created = await agent.createConversation('saveA');
    check('新建空会话', created.ok === true && created.conversation?.id?.startsWith('conv_'));
    check('新会话成为活动会话', agent.getActiveConversationId() === created.conversation?.id);
    const withDraft = await agent.getConversations('saveA');
    check('列表包含空会话', withDraft.length === 3);
    check('空会话 history 为空', (await agent.getHistory('saveA')).length === 0);

    const switched = await agent.switchConversation('saveA', 'conv-a');
    check('切换到 A', switched.ok === true);
    check('切换后历史属于 A', switched.history?.[0]?.text === '第一条提问 A');
    check('切换后 active=A', agent.getActiveConversationId() === 'conv-a');

    const bPath = pi.SessionManager.findById(dataDir, 'conv-b', sessionDir);
    const renamed = await agent.renameConversation('saveA', 'conv-b', '  物流站标题  ');
    check('重命名现有会话', renamed.ok === true && renamed.title === '物流站标题');
    const renamedList = await agent.getConversations('saveA');
    check('重命名后列表标题更新', renamedList.find((c) => c.id === 'conv-b')?.title === '物流站标题');
    check('重命名写入 session_info', Boolean(bPath) && readFileSync(bPath, 'utf8').includes('"session_info"') && readFileSync(bPath, 'utf8').includes('物流站标题'));

    const deleted = await agent.deleteConversation('saveA', 'conv-b');
    check('删除非活动会话', deleted.ok === true);
    check('删除后文件不存在', Boolean(bPath) && !existsSync(bPath));
    check('删除后 active 不变', agent.getActiveConversationId() === 'conv-a');
    const afterDelete = await agent.getConversations('saveA');
    check('删除后列表只剩 A', afterDelete.length === 1, JSON.stringify(afterDelete.map((c) => c.id)));

    const deleteActive = await agent.deleteConversation('saveA', 'conv-a');
    check('删除活动会话成功', deleteActive.ok === true);
    check('删除活动会话后换新 active', deleteActive.activeConversationId !== 'conv-a');

    const draftId = deleteActive.activeConversationId;
    const draftRenamed = await agent.renameConversation('saveA', draftId, '空会话标题');
    check('重命名空会话', draftRenamed.ok === true && draftRenamed.title === '空会话标题');
    const draftList = await agent.getConversations('saveA');
    check('空会话重命名后列表可见', draftList.find((c) => c.id === draftId)?.title === '空会话标题');
    check('空会话重命名暂不落 session 文件', !pi.SessionManager.findById(dataDir, draftId, sessionDir));
    const activeDraft = JSON.parse(readFileSync(join(sessionDir, 'active.json'), 'utf8'));
    check('空会话标题写入 active.json', activeDraft.title === '空会话标题');
    const restoredAgent = new PiAgent({
      dataDir,
      store: { get: () => ({}), hasKey: () => false },
      host: {},
      logger: { child: () => noop },
      loadSdk: () => import('@earendil-works/pi-coding-agent'),
    });
    restoredAgent.onSaveChanged('saveA');
    const restoredList = await restoredAgent.getConversations('saveA');
    check('重启后空会话标题保留', restoredList.find((c) => c.id === draftId)?.title === '空会话标题');
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
}

// ---------------------------------------------------------------- 3. 模型标题
console.log('── 3. 模型标题归一化 ──');
check('conversation.updated 类型名', MessageType.ConversationUpdated === 'conversation.updated');
const updatedPayload = buildConversationUpdatedPayload({ conversationId: 'conv-a', title: '量子芯片产线规划' });
check('conversation.updated payload', updatedPayload.conversationId === 'conv-a' && updatedPayload.title === '量子芯片产线规划');
check('去掉 Markdown 强调', normalizeConversationTitle('**量子芯片产线**') === '量子芯片产线');
check('去掉标题前缀和结尾标点', normalizeConversationTitle('标题：量子芯片产线规划。') === '量子芯片产线规划');
check('去掉引号', normalizeConversationTitle('“帮我看下蓝图”') === '帮我看下蓝图');
check('去掉 Markdown 包裹的前缀', normalizeConversationTitle('**标题：量子芯片产线**') === '量子芯片产线');
check('只取第一行', normalizeConversationTitle('量子芯片产线\n这是解释') === '量子芯片产线');
check('空输出为空', normalizeConversationTitle('   ') === '');
check('超长截断', Array.from(normalizeConversationTitle('一'.repeat(80))).length === 24);
check('重命名压缩空白', normalizeConversationRename('  量子  芯片  ') === '量子 芯片');
check('重命名上限 40', Array.from(normalizeConversationRename('一'.repeat(80))).length === 40);

console.log();
if (failed === 0) {
  console.log(`✅ conversations: ${passed} 项通过`);
  process.exit(0);
}
console.log(`❌ conversations: ${failed} / ${passed + failed} 项失败`);
process.exit(1);
