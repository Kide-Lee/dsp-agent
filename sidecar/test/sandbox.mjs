#!/usr/bin/env node
/**
 * 沙盒门禁测试（离线）。
 *
 *   - save.bind.sandbox 解析：缺失/非法 → false，绝不因此拒绝绑定
 *   - GameToolHost 按沙盒状态隐藏/拒绝沙盒专属工具
 *   - Session 把 save.bind.sandbox 转给 agent.onSandboxChanged
 *
 *   node test/sandbox.mjs
 */

import { parseSaveBindPayload } from '../dist/protocol.js';
import { GameToolHost, registerGameToolsFactory } from '../dist/tools/host.js';
import {
  CAPABILITY_GAME_SANDBOX,
  SANDBOX_ONLY_TOOL_NAMES,
  isSandboxOnlyTool,
} from '../dist/tools/sandbox.js';
import { Session } from '../dist/session.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitFor(predicate, timeoutMs = 1500) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await delay(5);
  }
  return false;
}

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

function fakeTool(name) {
  return {
    name,
    label: name,
    description: name,
    parameters: { type: 'object', properties: {} },
    executionMode: 'sequential',
    async execute() {
      return { content: [{ type: 'text', text: 'ok' }] };
    },
  };
}

function makeAgent(sandboxSeen) {
  return {
    info: { provider: 'test', model: 'test', available: true },
    onClientCapabilities() {},
    onSaveChanged() {},
    onSandboxChanged(enabled) { sandboxSeen.push(enabled); },
    onBlueprintTiers() {},
    toolCatalog() { return []; },
    async run() {},
    onConfigChanged() {},
    dispose() {},
  };
}

function makeSession(agent) {
  const socket = new FakeSocket();
  const session = new Session(socket, {
    token: 'test-token',
    serverVersion: 'test',
    agent,
    config: {
      view: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
      update: () => ({ state: 'ready', provider: 'test', model: 'test', message: null }),
    },
    tools: () => [],
  });
  return { socket, session };
}

function hello() {
  return {
    v: 1,
    id: 'm-1',
    type: 'hello',
    payload: {
      token: 'test-token',
      modVersion: 'test',
      gameVersion: 'test',
      gameLanguage: 'zh-CN',
      capabilities: ['game.read.v1', CAPABILITY_GAME_SANDBOX],
    },
  };
}

async function run() {
  console.log('── 1. save.bind.sandbox 解析 ──');
  {
    check('true 解析为 true', parseSaveBindPayload({ saveKey: 'a', sandbox: true })?.sandbox === true);
    check('false 解析为 false', parseSaveBindPayload({ saveKey: 'a', sandbox: false })?.sandbox === false);
    check('缺失按 false（旧 mod）', parseSaveBindPayload({ saveKey: 'a' })?.sandbox === false);
    check('非法类型按 false，不拒绝绑定', parseSaveBindPayload({ saveKey: 'a', sandbox: 'yes' })?.sandbox === false);
    check('解绑也带安全默认', parseSaveBindPayload({ saveKey: null })?.sandbox === false);
    check('缺 saveKey 仍然拒绝', parseSaveBindPayload({ sandbox: true }) === null);
  }

  console.log('── 2. 工具名表 ──');
  {
    check('spawn 是沙盒专属', isSandboxOnlyTool('game_spawn_item'));
    check('teleport 是沙盒专属', isSandboxOnlyTool('game_teleport'));
    check('读工具不是沙盒专属', !isSandboxOnlyTool('game_get_overview'));
    check('名单来自同一模块', SANDBOX_ONLY_TOOL_NAMES.includes('game_spawn_item'));
  }

  console.log('── 3. GameToolHost 隐藏/拒绝 ──');
  {
    registerGameToolsFactory(() => [
      fakeTool('game_spawn_item'),
      fakeTool('game_teleport'),
      fakeTool('game_get_overview'),
    ]);
    const host = new GameToolHost();
    host.setCapabilities(['game.read.v1']);

    check('默认不暴露沙盒工具', !host.tools().some((tool) => isSandboxOnlyTool(tool.name)));
    check('setSandbox(true) 报告变化', host.setSandbox(true) === true);
    check('沙盒开启后暴露沙盒工具', host.tools().some((tool) => tool.name === 'game_spawn_item'));
    check('setSandbox(true) 重复调用无变化', host.setSandbox(true) === false);
    check('setSandbox(false) 报告变化', host.setSandbox(false) === true);

    let transportCalls = 0;
    host.bind({
      async callTool() {
        transportCalls += 1;
        return { ok: true, data: { display: 'ok' }, error: null, meta: {} };
      },
    });

    const blocked = await host.call('game_spawn_item', {});
    check('沙盒关闭时 host.call 被拒绝', blocked.ok === false && blocked.error?.code === 'sandbox_required', JSON.stringify(blocked));
    check('被拒绝时没有发到 mod', transportCalls === 0);

    host.setSandbox(true);
    const allowed = await host.call('game_spawn_item', {}, { timeoutMs: 1000 });
    check('沙盒开启时 host.call 放行', allowed.ok === true);
    check('放行时发到 mod', transportCalls === 1);
  }

  console.log('── 4. Session 转发 save.bind.sandbox ──');
  {
    const sandboxSeen = [];
    const { socket, session } = makeSession(makeAgent(sandboxSeen));
    session.handleFrame(Buffer.from(JSON.stringify(hello())), false);
    check('握手完成', socket.frames.some((frame) => frame.type === 'ready'));

    session.handleFrame(Buffer.from(JSON.stringify({
      v: 1, id: 'm-2', type: 'save.bind',
      payload: { saveKey: 'save-s', sandbox: true },
    })), false);
    check('绑定回应', await waitFor(() => socket.frames.some((frame) => frame.type === 'save.bound')));
    await waitFor(() => sandboxSeen.includes(true));
    check('agent 收到 sandbox=true', sandboxSeen.includes(true), JSON.stringify(sandboxSeen));

    session.handleFrame(Buffer.from(JSON.stringify({
      v: 1, id: 'm-3', type: 'save.bind',
      payload: { saveKey: null, sandbox: true },
    })), false);
    await waitFor(() => sandboxSeen[sandboxSeen.length - 1] === false);
    check('解绑按 false 转发', sandboxSeen[sandboxSeen.length - 1] === false, JSON.stringify(sandboxSeen));
    session.dispose('test');
  }

  console.log();
  if (failed === 0) {
    console.log(`✅ 全部通过（${passed} 项断言）`);
    process.exit(0);
  }
  console.log(`❌ ${failed} / ${passed + failed} 项断言失败`);
  process.exit(1);
}

run().catch((err) => {
  console.error(err);
  process.exit(1);
});
