#!/usr/bin/env node
/**
 * A.14 设定页回归：
 *   1. tools.set / config.setState 解析与 disabledTools 落盘迁移；
 *   2. GameToolHost 的开关过滤（脚本白名单同源）；
 *   3. PiAgent.toolCatalog 的 group/risk/source/enabled；
 *   4. conversations.index 只读扫描、saves.json 显示名；
 *   5. conversations.export 的 Markdown、文件名清洗与重名；
 *   6. Session 层 tools.set / conversations.index / conversations.export 回执。
 *
 *   node test/settings.mjs
 */

import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { Session } from '../dist/session.js';
import { PiAgent } from '../dist/agent.js';
import { GameToolHost } from '../dist/tools/host.js';
import { createGameTools } from '../dist/tools/registry.js';
import { registerGameToolsFactory } from '../dist/tools/host.js';
import {
  MessageType,
  buildConversationsExportRequest,
  buildToolsSetPayload,
  parseConfigSetPayload,
  parseConversationsExportPayload,
  parseToolsSetPayload,
} from '../dist/protocol.js';
import { ConfigStore } from '../dist/config.js';
import { makeTempDir } from './helpers.mjs';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const noopLog = { info() {}, warn() {}, error() {}, debug() {} };
const silentLogger = { child: () => noopLog };

// 注册游戏工具工厂（真实注册表），host.tools() 才能返回定义。
registerGameToolsFactory(createGameTools);

console.log('── 1. tools.set / disabledTools 解析 ──');
{
  const parsed = parseToolsSetPayload({ disabled: [' game_a ', 'game_a', '', 'game_b'] });
  check('tools.set 去空去重', parsed?.disabled?.join(',') === 'game_a,game_b', JSON.stringify(parsed));
  check('tools.set 拒绝非数组', parseToolsSetPayload({ disabled: 'game_a' }) === null);
  check('tools.set 拒绝非字符串项', parseToolsSetPayload({ disabled: [1] }) === null);
  const built = buildToolsSetPayload(['game_a']);
  check('tools.set payload 构造', built.disabled?.[0] === 'game_a');

  const configPatch = parseConfigSetPayload({ disabledTools: ['x', 'x', 'y'] });
  check('config.set 接收 disabledTools', configPatch?.disabledTools?.join(',') === 'x,y');
  check('config.set 拒绝非法 disabledTools', parseConfigSetPayload({ disabledTools: [true] }) === null);

  const request = parseConversationsExportPayload({ conversationId: 'conv_a', saveKey: 'saveA' });
  check('export 解析', request?.conversationId === 'conv_a' && request?.saveKey === 'saveA' && request?.format === 'md');
  check('export 拒绝未知格式', parseConversationsExportPayload({ conversationId: 'conv_a', format: 'pdf' }) === null);
  check('export 缺 conversationId 拒绝', parseConversationsExportPayload({}) === null);
  const exported = buildConversationsExportRequest({ saveKey: 'saveA', conversationId: 'conv_a' });
  check('export 请求构造', exported.saveKey === 'saveA' && exported.format === 'md');
}

console.log('── 2. disabledTools 落盘与迁移 ──');
{
  const dir = makeTempDir('icarus-settings-config-');
  try {
    const path = join(dir, 'config.json');
    // 老配置：没有 disabledTools 字段 ⇒ 全部启用。
    writeFileSync(path, JSON.stringify({ providers: {}, active: { provider: 'deepseek', model: 'x' } }));
    const store = new ConfigStore({ path, logger: silentLogger });
    check('缺字段按全部启用', store.disabledToolNames().length === 0);
    const view = store.update({ disabledTools: ['game_a', 'run_script'] });
    check('update 返回 disabledTools', view.disabledTools?.length === 2);
    const onDisk = JSON.parse(readFileSync(path, 'utf8'));
    check('落盘包含 disabledTools', Array.isArray(onDisk.disabledTools) && onDisk.disabledTools.length === 2);
    const reloaded = new ConfigStore({ path, logger: silentLogger });
    check('重启后保留', reloaded.disabledToolNames().join(',') === 'game_a,run_script');
    const cleared = reloaded.update({ disabledTools: [] });
    check('清空开关', cleared.disabledTools?.length === 0);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

console.log('── 3. host 过滤 ──');
{
  const host = new GameToolHost();
  let toolsetChanged = 0;
  host.onToolsetChanged = () => { toolsetChanged += 1; };
  host.setCapabilities(['game.read.v1', 'game.entity.v1', 'game.view.v1']);
  const before = host.tools().map((tool) => tool.name);
  check('默认含 game_get_overview', before.includes('game_get_overview'));
  check('setDisabledTools 返回 true', host.setDisabledTools(['game_get_overview']) === true);
  check('tools() 不再含被关工具', !host.tools().some((tool) => tool.name === 'game_get_overview'));
  check('allTools() 仍含被关工具', host.allTools().some((tool) => tool.name === 'game_get_overview'));
  check('开关变化通知 agent', toolsetChanged === 1);
  check('重复设置同一份不通知', host.setDisabledTools(['game_get_overview']) === false && toolsetChanged === 1);
  check('恢复启用', host.setDisabledTools([]) === true && host.tools().some((tool) => tool.name === 'game_get_overview'));
  host.setDisabledTools([]);
}

async function makeAgent(dataDir) {
  return new PiAgent({
    dataDir,
    store: {
      get: () => ({ disabledTools: [] }),
      hasKey: () => false,
      blueprintRoot: () => null,
      wikiEndpoint: () => null,
      disabledToolNames: () => [],
    },
    host: {},
    logger: silentLogger,
    loadSdk: () => import('@earendil-works/pi-coding-agent'),
  });
}

async function seedConversation(dataDir, saveKey, id, userText, assistantText, name) {
  const pi = await import('@earendil-works/pi-coding-agent');
  const sessionDir = join(dataDir, 'sessions', saveKey);
  mkdirSync(sessionDir, { recursive: true });
  const manager = pi.SessionManager.create(dataDir, sessionDir, { id });
  if (name) manager.appendSessionInfo(name);
  manager.appendMessage({ role: 'user', content: userText, timestamp: Date.now() });
  manager.appendMessage({ role: 'assistant', content: [{ type: 'text', text: assistantText }], timestamp: Date.now() + 1 });
  return sessionDir;
}

console.log('── 4. toolCatalog 元数据 ──');
{
  const dataDir = makeTempDir('icarus-settings-catalog-');
  try {
    const host = new GameToolHost();
    host.setCapabilities(['game.read.v1', 'game.entity.v1', 'game.view.v1']);
    const store = {
      get: () => ({}),
      hasKey: () => false,
      blueprintRoot: () => null,
      wikiEndpoint: () => null,
      disabledToolNames: () => ['game_get_overview'],
    };
    const agent = new PiAgent({ dataDir, store, host, logger: silentLogger });
    const catalog = agent.toolCatalog(['game.read.v1', 'game.entity.v1', 'game.view.v1']);
    const overview = catalog.find((entry) => entry.name === 'game_get_overview');
    check('被关工具仍在目录里', Boolean(overview));
    check('enabled=false', overview?.enabled === false);
    check('group=game.read', overview?.group === 'game.read');
    check('risk=0', overview?.risk === 0);
    check('source=builtin', overview?.source === 'builtin' && overview?.sourceName === '内置');
    const script = catalog.find((entry) => entry.name === 'run_script');
    check('run_script group=other', script?.group === 'other');
    check('run_script risk=1', script?.risk === 1);
    check('run_script enabled=true', script?.enabled === true);
    agent.dispose();
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
}

console.log('── 5. conversations.index ──');
{
  const dataDir = makeTempDir('icarus-settings-index-');
  try {
    const dirA = await seedConversation(dataDir, '3f2a91c4', 'conv_a1', '提问 A1', '回答 A1', '会话 A1');
    const dirB = await seedConversation(dataDir, '77aa00bb', 'conv_b1', '提问 B1', '回答 B1', null);
    // active.json：A 存档指向 A1；B 存档指向一个还没落盘的 draft。
    writeFileSync(join(dirA, 'active.json'), JSON.stringify({ activeConversationId: 'conv_a1' }));
    writeFileSync(join(dirB, 'active.json'), JSON.stringify({ activeConversationId: 'conv_b_draft', title: 'B 的草稿' }));
    writeFileSync(join(dataDir, 'saves.json'), JSON.stringify({ '3f2a91c4': { name: '我的档', lastSeen: 1760000000000 } }));

    const agent = await makeAgent(dataDir);
    agent.onSaveChanged('3f2a91c4');
    const dirASnapshot = readFileSync(join(dirA, 'active.json'), 'utf8');
    const index = await agent.conversationIndex();
    check('索引包含两个存档', index.saves.length === 2, JSON.stringify(index.saves.map((s) => s.saveKey)));
    check('当前存档标记', index.saves.find((s) => s.saveKey === '3f2a91c4')?.current === true);
    check('saves.json 显示名', index.saves.find((s) => s.saveKey === '3f2a91c4')?.saveName === '我的档');
    check('缺索引的存档用兜底名', index.saves.find((s) => s.saveKey === '77aa00bb')?.saveName === '存档 77aa00bb');
    const a1 = index.saves.find((s) => s.saveKey === '3f2a91c4')?.conversations.find((c) => c.id === 'conv_a1');
    check('会话消息数', a1?.messageCount === 1, JSON.stringify(a1));
    check('当前活动会话标记', a1?.isActive === true);
    const draft = index.saves.find((s) => s.saveKey === '77aa00bb')?.conversations.find((c) => c.id === 'conv_b_draft');
    check('未落盘草稿也列出', draft?.title === 'B 的草稿' && draft?.messageCount === 0);
    check('索引不写 active.json', readFileSync(join(dirA, 'active.json'), 'utf8') === dirASnapshot);

    agent.noteSaveBinding('77aa00bb', '第二个档');
    const after = await agent.conversationIndex();
    check('noteSaveBinding 写入显示名', after.saves.find((s) => s.saveKey === '77aa00bb')?.saveName === '第二个档');
    check('saves.json 已落盘', JSON.parse(readFileSync(join(dataDir, 'saves.json'), 'utf8'))['77aa00bb']?.name === '第二个档');
    agent.dispose();
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
}

console.log('── 6. conversations.export ──');
{
  const dataDir = makeTempDir('icarus-settings-export-');
  try {
    await seedConversation(dataDir, 'saveA', 'conv_export', '帮我规划: 量子/芯片?', '先做<主线>', 'A/B: 标题*测试');
    const agent = await makeAgent(dataDir);
    const first = await agent.exportConversation('saveA', 'conv_export');
    check('导出成功', first.ok === true, JSON.stringify(first));
    check('导出文件存在', Boolean(first.path) && existsSync(first.path));
    const text = readFileSync(first.path, 'utf8');
    check('Markdown 含标题', text.includes('# A/B: 标题*测试'));
    check('Markdown 含提问', text.includes('帮我规划: 量子/芯片?'));
    check('Markdown 含回答', text.includes('先做<主线>'));
    check('文件名清洗非法字符', !/[\\/:*?"<>|]/.test(first.path.split('/').pop()), first.path);
    const second = await agent.exportConversation('saveA', 'conv_export');
    check('同名追加 -2', second.path !== first.path && second.path.includes('-2.md'), second.path);
    const missing = await agent.exportConversation('saveA', 'conv_missing');
    check('不存在的会话返回 conversation_not_found', missing.ok === false && missing.error === 'conversation_not_found');
    agent.dispose();
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
}

console.log('── 7. Session 回执 ──');
{
  class FakeSocket {
    constructor() { this.isOpen = true; this.peer = 'test'; this.frames = []; }
    send(text, cb) { try { this.frames.push(JSON.parse(text)); cb?.(); } catch (err) { cb?.(err); } }
    close() { this.isOpen = false; }
  }
  const config = {
    view: () => ({ state: 'ready', provider: 'test', model: 'test', message: null, disabledTools: [] }),
    update: (patch) => ({ state: 'ready', provider: 'test', model: 'test', message: null, disabledTools: patch.disabledTools ?? [] }),
  };
  const agent = {
    info: { provider: 'test', model: 'test', available: true },
    run: async () => {},
    onConfigChanged() {},
    applyDisabledTools: (disabled) => ({ disabled: [...disabled], rebuilt: true }),
    toolCatalog: () => [
      { name: 'game_get_overview', label: '存档概览', description: '', group: 'game.read', risk: 0, source: 'builtin', sourceName: '内置', enabled: true },
    ],
    conversationIndex: async () => ({
      currentSaveKey: 'saveA',
      saves: [{ saveKey: 'saveA', saveName: '存档 A', lastSeen: 1, current: true, conversations: [] }],
    }),
    exportConversation: async () => ({ ok: true, path: '/tmp/out.md', bytes: 10, messageCount: 2 }),
    noteSaveBinding() {},
    dispose() {},
  };
  const socket = new FakeSocket();
  const session = new Session(socket, { token: 't', serverVersion: 'test', agent, config, tools: () => [] }, () => {});
  session.handleFrame(Buffer.from(JSON.stringify({
    v: 1, id: 'h', type: 'hello',
    payload: { token: 't', modVersion: 'test', gameVersion: '1', gameLanguage: 'zh-CN', capabilities: [], saveKey: 'saveA' },
  })), false);
  session.handleFrame(Buffer.from(JSON.stringify({ v: 1, id: 't-1', type: 'tools.set', payload: { disabled: ['run_script'] } })), false);
  session.handleFrame(Buffer.from(JSON.stringify({ v: 1, id: 'i-1', type: 'conversations.index', payload: {} })), false);
  session.handleFrame(Buffer.from(JSON.stringify({ v: 1, id: 'e-1', type: 'conversations.export', payload: { conversationId: 'conv_a' } })), false);
  for (let i = 0; i < 200; i++) {
    if (socket.frames.some((f) => f.type === 'tools.state') &&
        socket.frames.some((f) => f.type === 'conversations.indexed') &&
        socket.frames.some((f) => f.type === 'conversations.exported')) break;
    await delay(2);
  }
  const toolsState = socket.frames.find((f) => f.type === 'tools.state');
  check('tools.state 回执', toolsState?.payload?.ok === true && toolsState?.payload?.rebuilt === true);
  check('tools.state 回 disabled', toolsState?.payload?.disabled?.[0] === 'run_script');
  check('tools.state 带回目录', toolsState?.payload?.toolCatalog?.[0]?.name === 'game_get_overview');
  const indexed = socket.frames.find((f) => f.type === 'conversations.indexed');
  check('conversations.indexed 回执', indexed?.payload?.saves?.[0]?.saveKey === 'saveA');
  const exported = socket.frames.find((f) => f.type === 'conversations.exported');
  check('conversations.exported 回执', exported?.payload?.ok === true && exported?.payload?.path === '/tmp/out.md');
  session.dispose?.();
}

console.log();
if (failed === 0) {
  console.log(`✅ settings: ${passed} 项通过`);
  process.exit(0);
}
console.log(`❌ settings: ${failed} / ${passed + failed} 项失败`);
process.exit(1);
