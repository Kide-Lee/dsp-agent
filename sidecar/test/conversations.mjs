#!/usr/bin/env node
/**
 * A.13 多会话回归：
 *   1. Session 层的 conversation.create/switch/delete 消息与 save.bound 字段；
 *   2. PiAgent 的会话列表、活动会话持久化、切换、删除（真实 pi SessionManager 落盘）。
 *
 *   node test/conversations.mjs
 */

import { readFileSync, rmSync, existsSync, utimesSync } from 'node:fs';
import { join } from 'node:path';
import { Session } from '../dist/session.js';
import {
  PiAgent,
  compareConversations,
  normalizeConversationRename,
  normalizeConversationTitle,
} from '../dist/agent.js';
import { MessageType, buildConversationUpdatedPayload } from '../dist/protocol.js';
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
      // 切走一个没落盘的空会话时，引擎会丢弃它并把 id 回传给 UI。
      const discardedConversationId = activeId === 'conv-new' && id !== activeId ? activeId : null;
      activeId = id;
      return {
        ok: true,
        conversation: found,
        history: historyFor(id),
        ...(discardedConversationId ? { discardedConversationId } : {}),
      };
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
    pinConversation: async (_key, id, pinned) => {
      const found = list.find((c) => c.id === id);
      if (!found) return { ok: false, error: 'conversation_not_found' };
      found.pinned = pinned === true;
      return { ok: true };
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
  check(
    'conversation.switch 带回被丢弃的空会话',
    switched?.payload?.discardedConversationId === 'conv-new',
    JSON.stringify(switched?.payload?.discardedConversationId),
  );

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
    v: 1, id: 'c-6', type: 'conversation.pin',
    payload: { conversationId: 'conv-b', pinned: true },
  })), false);
  const pinnedFrame = await waitFrame(socket, 'conversation.pinned');
  check('conversation.pin 回执 ok', pinnedFrame?.payload?.ok === true, JSON.stringify(pinnedFrame?.payload));
  check('conversation.pin 回传 pinned', pinnedFrame?.payload?.pinned === true);
  check('conversation.pin 回传会话 id', pinnedFrame?.payload?.conversationId === 'conv-b');
  check('conversation.pin 落到 agent',
    (await agent.getConversations()).find((c) => c.id === 'conv-b')?.pinned === true);

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'c-7', type: 'conversation.pin',
    payload: { conversationId: 'conv-missing', pinned: true },
  })), false);
  let missingPin = null;
  for (let i = 0; i < 100 && !missingPin; i++) {
    missingPin = socket.frames.find((f) => f.type === 'conversation.pinned' && f.replyTo === 'c-7') ?? null;
    if (!missingPin) await delay(2);
  }
  check('置顶不存在的会话返回失败',
    missingPin?.payload?.ok === false && missingPin?.payload?.error === 'conversation_not_found',
    JSON.stringify(missingPin?.payload));

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'c-8', type: 'conversation.pin',
    payload: { conversationId: 'conv-b', pinned: 'yes' },
  })), false);
  let badPin = null;
  for (let i = 0; i < 100 && !badPin; i++) {
    badPin = socket.frames.find((f) => f.type === 'error' && f.replyTo === 'c-8') ?? null;
    if (!badPin) await delay(2);
  }
  check('pinned 非布尔被拒绝', badPin?.payload?.code === 'bad_request', JSON.stringify(badPin));

  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'p-1', type: 'prompt',
    payload: { text: '你好', conversationId: 'conv-a' },
  })), false);
  const mismatch = socket.frames.find((f) => f.type === 'error' && f.replyTo === 'p-1') ?? null;
  check('prompt 指定会话不再要求先切换', mismatch == null, JSON.stringify(mismatch));
}

// ---- 2. PiAgent 落盘 ----
console.log('── 2. PiAgent 会话文件 ──');
{
  const dataDir = makeTempDir('icarus-conv-');
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
      store: { get: () => ({}), hasKey: () => false, blueprintRoot: () => null, wikiEndpoint: () => null },
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
    check(
      '切走空会话时回传被丢弃的 id',
      switched.discardedConversationId === created.conversation?.id,
      JSON.stringify(switched.discardedConversationId),
    );
    const afterDiscard = await agent.getConversations('saveA');
    check('被丢弃的空会话不再出现在列表', !afterDiscard.some((c) => c.id === created.conversation?.id));
    const backToDraft = await agent.switchConversation('saveA', created.conversation?.id);
    check(
      '切回被丢弃的空会话失败',
      backToDraft.ok === false && backToDraft.error === 'conversation_not_found',
      JSON.stringify(backToDraft),
    );

    const bPath = pi.SessionManager.findById(dataDir, 'conv-b', sessionDir);
    const renamed = await agent.renameConversation('saveA', 'conv-b', '  物流站标题  ');
    check('重命名现有会话', renamed.ok === true && renamed.title === '物流站标题');
    const renamedList = await agent.getConversations('saveA');
    check('重命名后列表标题更新', renamedList.find((c) => c.id === 'conv-b')?.title === '物流站标题');
    check('重命名写入 session_info', Boolean(bPath) && readFileSync(bPath, 'utf8').includes('"session_info"') && readFileSync(bPath, 'utf8').includes('物流站标题'));

    const backToB = await agent.switchConversation('saveA', 'conv-b');
    check('切回 B 后改过的标题保留', backToB.conversation?.title === '物流站标题', JSON.stringify(backToB.conversation?.title));
    const backToBList = await agent.getConversations('saveA');
    check('切回后列表标题保留', backToBList.find((c) => c.id === 'conv-b')?.title === '物流站标题');
    const sameB = await agent.switchConversation('saveA', 'conv-b');
    check('重复切到同一会话标题保留', sameB.conversation?.title === '物流站标题', JSON.stringify(sameB.conversation?.title));
    const backToA = await agent.switchConversation('saveA', 'conv-a');
    check('切回 A 成功', backToA.ok === true);
    check('两个已落盘会话之间切换不回传丢弃 id', backToA.discardedConversationId === undefined);

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
      store: { get: () => ({}), hasKey: () => false, blueprintRoot: () => null, wikiEndpoint: () => null },
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

// ---------------------------------------------------------------- 2.4 置顶与最后回复时间
console.log('── 2.4 置顶与最后回复时间 ──');
{
  const dataDir = makeTempDir('icarus-conv-pin-');
  const sessionDir = join(dataDir, 'sessions', 'saveA');
  try {
    const pi = await import('@earendil-works/pi-coding-agent');
    const noop = { info() {}, warn() {}, error() {}, debug() {} };
    const makeAgent = () => new PiAgent({
      dataDir,
      store: { get: () => ({}), hasKey: () => false, blueprintRoot: () => null, wikiEndpoint: () => null },
      host: {},
      logger: { child: () => noop },
      loadSdk: () => import('@earendil-works/pi-coding-agent'),
    });
    const a = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-a' });
    a.appendMessage({ role: 'user', content: '提问 A', timestamp: 1 });
    a.appendMessage({ role: 'assistant', content: [{ type: 'text', text: '回答 A' }], timestamp: 2 });
    await delay(5);
    const b = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-b' });
    b.appendMessage({ role: 'user', content: '提问 B', timestamp: 3 });
    b.appendMessage({ role: 'assistant', content: [{ type: 'text', text: '回答 B' }], timestamp: 4 });

    const fileOf = (id) => pi.SessionManager.findById(dataDir, id, sessionDir);
    // 把会话文件的 mtime 当成“最后一条回答的时间”来安排顺序。
    const touch = (id, epochMs) => utimesSync(fileOf(id), new Date(epochMs), new Date(epochMs));

    const agent = makeAgent();
    agent.onSaveChanged('saveA');

    // 时间标记用的绝对 gameTick（时钟读数会 24 小时归零，算不出间隔）。
    agent.recordReplyTime('saveA', '提问 A', '回答 A', '08:42:00', 'conv-a', 9000);
    const withTick = await agent.getConversations('saveA');
    check('lastReplyTick 回传该会话最后一条回答的 tick',
      withTick.find((c) => c.id === 'conv-a')?.lastReplyTick === 9000,
      JSON.stringify(withTick.map((c) => [c.id, c.lastReplyTick])));
    check('没有回答记录的会话没有 lastReplyTick',
      withTick.find((c) => c.id === 'conv-b')?.lastReplyTick === undefined);
    check('未置顶时排在前面的是最后回复更新的会话',
      (await agent.getConversations('saveA'))[0]?.id === 'conv-b',
      JSON.stringify((await agent.getConversations('saveA')).map((c) => c.id)));

    const missing = await agent.pinConversation('saveA', 'conv-missing', true);
    check('置顶不存在的会话被拒绝', missing.ok === false && missing.error === 'conversation_not_found',
      JSON.stringify(missing));
    check('没有存档时置顶被拒绝', (await agent.pinConversation(null, 'conv-a', true)).error === 'no_save');

    const pinnedA = await agent.pinConversation('saveA', 'conv-a', true);
    check('置顶成功', pinnedA.ok === true, JSON.stringify(pinnedA));
    const pinnedList = await agent.getConversations('saveA');
    check('置顶会话排到最前', pinnedList[0]?.id === 'conv-a', JSON.stringify(pinnedList.map((c) => c.id)));
    check('置顶状态回传', pinnedList[0]?.pinned === true && pinnedList[1]?.pinned !== true);
    check('置顶记录落盘', existsSync(join(sessionDir, 'conversation-pins.json')));

    await agent.pinConversation('saveA', 'conv-b', true);
    touch('conv-a', Date.now() - 60_000);
    const bothPinned = await agent.getConversations('saveA');
    check('多个置顶按最后回复从新到旧',
      bothPinned[0]?.id === 'conv-b' && bothPinned[1]?.id === 'conv-a',
      JSON.stringify(bothPinned.map((c) => c.id)));

    const restarted = makeAgent();
    restarted.onSaveChanged('saveA');
    const restartedList = await restarted.getConversations('saveA');
    check('重启后置顶保留', restartedList.filter((c) => c.pinned === true).map((c) => c.id).join(',') === 'conv-b,conv-a',
      JSON.stringify(restartedList.map((c) => [c.id, c.pinned])));

    await restarted.pinConversation('saveA', 'conv-b', false);
    const unpinned = await restarted.getConversations('saveA');
    check('取消置顶后回到按最后回复排序', unpinned[0]?.id === 'conv-a', JSON.stringify(unpinned.map((c) => c.id)));

    // 切换会话不写任何参与排序的字段：点一下不会让会话跳到最前。
    const beforeSwitch = (await restarted.getConversations('saveA')).map((c) => c.id).join(',');
    const firstSwitch = await restarted.switchConversation('saveA', 'conv-b');
    const againSwitch = await restarted.switchConversation('saveA', 'conv-a');
    const backSwitch = await restarted.switchConversation('saveA', 'conv-b');
    check('切换回执的 createdAt 是会话文件的创建时间（切换不刷新）',
      firstSwitch.conversation?.createdAt === backSwitch.conversation?.createdAt,
      `${JSON.stringify(firstSwitch.conversation?.createdAt)} vs ${JSON.stringify(backSwitch.conversation?.createdAt)}`);
    check('切换回执的 updatedAt 是文件 mtime（切换不刷新）',
      typeof againSwitch.conversation?.updatedAt === 'number' &&
        firstSwitch.conversation?.updatedAt === backSwitch.conversation?.updatedAt,
      JSON.stringify([firstSwitch.conversation?.updatedAt, backSwitch.conversation?.updatedAt]));
    const afterSwitch = await restarted.getConversations('saveA');
    check('切换会话不改变列表顺序', afterSwitch.map((c) => c.id).join(',') === beforeSwitch,
      `${beforeSwitch} → ${afterSwitch.map((c) => c.id).join(',')}`);

    // 取消置顶要落到「按最后回复」该在的位置，而不是留在置顶区。
    await restarted.pinConversation('saveA', 'conv-a', true);
    const pinnedOrder = (await restarted.getConversations('saveA')).map((c) => c.id).join(',');
    check('置顶会话排在最前', pinnedOrder === 'conv-a,conv-b', pinnedOrder);
    await restarted.pinConversation('saveA', 'conv-a', false);
    const unpinnedOrder = (await restarted.getConversations('saveA')).map((c) => c.id).join(',');
    check('取消置顶后按最后回复重新落位', unpinnedOrder === 'conv-b,conv-a', unpinnedOrder);
    await restarted.pinConversation('saveA', 'conv-a', true);

    // 切换会话的回执也要带 pinned：mod 会用它整体覆盖列表项。
    const switchedPinned = await restarted.switchConversation('saveA', 'conv-a');
    check('切换回执带 pinned', switchedPinned.conversation?.pinned === true, JSON.stringify(switchedPinned.conversation));

    await restarted.pinConversation('saveA', 'conv-a', true);
    await restarted.deleteConversation('saveA', 'conv-a');
    const pinsFile = JSON.parse(readFileSync(join(sessionDir, 'conversation-pins.json'), 'utf8'));
    check('删除会话清掉置顶记录', pinsFile.pins?.['conv-a'] === undefined, JSON.stringify(pinsFile));
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
}

// ---------------------------------------------------------------- 2.5 会话级删除/撤销定位
console.log('── 2.5 会话级删除/撤销定位 ──');
{
  const dataDir = makeTempDir('icarus-conv-target-');
  const sessionDir = join(dataDir, 'sessions', 'saveA');
  try {
    const pi = await import('@earendil-works/pi-coding-agent');
    const noop = { info() {}, warn() {}, error() {}, debug() {} };
    const agent = new PiAgent({
      dataDir,
      store: { get: () => ({}), hasKey: () => false, blueprintRoot: () => null, wikiEndpoint: () => null },
      host: {},
      logger: { child: () => noop },
      loadSdk: () => import('@earendil-works/pi-coding-agent'),
    });
    agent.onSaveChanged('saveA');

    const a = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-a' });
    a.appendMessage({ role: 'user', content: '同一句提问', timestamp: 1 });
    a.appendMessage({ role: 'assistant', content: [{ type: 'text', text: 'A 的回答' }], timestamp: 2 });
    const b = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-b' });
    b.appendMessage({ role: 'user', content: '同一句提问', timestamp: 3 });
    b.appendMessage({ role: 'assistant', content: [{ type: 'text', text: 'B 的回答' }], timestamp: 4 });

    const aPath = pi.SessionManager.findById(dataDir, 'conv-a', sessionDir);
    const bPath = pi.SessionManager.findById(dataDir, 'conv-b', sessionDir);
    const deleted = await agent.deleteHistory('saveA', '同一句提问', undefined, 'conv-a');
    check('按 conversationId 删除成功', deleted.ok === true, JSON.stringify(deleted));
    check('只改目标会话 A', Boolean(aPath) && !readFileSync(aPath, 'utf8').includes('A 的回答'));
    check('不碰同文本会话 B', Boolean(bPath) && readFileSync(bPath, 'utf8').includes('B 的回答'));

    const missing = await agent.deleteHistory('saveA', '同一句提问', undefined, 'conv-missing');
    check('指定会话不存在时明确失败', missing.ok === false && missing.error === 'conversation_not_found', JSON.stringify(missing));

    const c = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-c' });
    c.appendMessage({ role: 'user', content: '压缩前问题', timestamp: 5 });
    const cCompaction = c.appendCompaction('C 的压缩摘要', null, 1000);
    const d = pi.SessionManager.create(dataDir, sessionDir, { id: 'conv-d' });
    d.appendMessage({ role: 'user', content: '压缩前问题', timestamp: 6 });
    const dCompaction = d.appendCompaction('D 的压缩摘要', null, 1000);

    const cPath = pi.SessionManager.findById(dataDir, 'conv-c', sessionDir);
    const dPath = pi.SessionManager.findById(dataDir, 'conv-d', sessionDir);
    const undone = await agent.undoCompaction('saveA', cCompaction, 'conv-c');
    check('按 conversationId 撤销压缩成功', undone.ok === true, JSON.stringify(undone));
    check('只撤销目标会话 C', Boolean(cPath) && !readFileSync(cPath, 'utf8').includes('C 的压缩摘要'));
    check('不碰同文本会话 D', Boolean(dPath) && readFileSync(dPath, 'utf8').includes('D 的压缩摘要'));
    check('撤销结果带回 compactionId', undone.compactionId === cCompaction, JSON.stringify(undone));
    check('D 的 compactionId 仍有记录', typeof dCompaction === 'string' && dCompaction.length > 0);
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
}

// ---------------------------------------------------------------- 2.6 排序只与置顶/最后回复有关
console.log('── 2.6 会话排序规则 ──');
{
  const conv = (id, extra = {}) => ({
    id,
    title: id,
    createdAt: extra.createdAt ?? 1000,
    updatedAt: extra.updatedAt ?? 1000,
    recordCount: 0,
    records: [],
    ...(extra.pinned ? { pinned: true } : {}),
  });

  const order = (list) => [...list].sort(compareConversations).map((c) => c.id);

  check('置顶排在最前', order([conv('a', { updatedAt: 9000 }), conv('b', { pinned: true, updatedAt: 10 })]).join(',') === 'b,a');
  check('置顶之间按最后回复从新到旧',
    order([conv('a', { pinned: true, updatedAt: 500 }), conv('b', { pinned: true, updatedAt: 900 })]).join(',') === 'b,a');
  check('未置顶也按最后回复从新到旧',
    order([conv('a', { updatedAt: 100 }), conv('b', { updatedAt: 900 })]).join(',') === 'b,a');
  check('最后回复相同才比创建时间',
    order([conv('a', { updatedAt: 500, createdAt: 100 }), conv('b', { updatedAt: 500, createdAt: 900 })]).join(',') === 'b,a');
  check('创建时间也相同时按 id 稳定',
    order([conv('b'), conv('a')]).join(',') === 'a,b');
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
