#!/usr/bin/env node
/**
 * M3 会话接线测试：save.bind 触发 internal 知识库导出，导出完成后 store 就绪。
 *
 *   node test/kb-session.mjs
 */

import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Session } from '../dist/session.js';
import { KnowledgeStore } from '../dist/kb/store.js';
import { KnowledgeManager } from '../dist/kb/manager.js';
import { toolNamesForCapabilities } from '../dist/tools/registry.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const here = dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(readFileSync(join(here, 'fixtures', 'kb-test.json'), 'utf8'));
const temp = mkdtempSync(join(tmpdir(), 'icarus-kb-session-'));

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
const agent = {
  info: { provider: 'test', model: 'test', available: true },
  run: async () => {},
  onClientCapabilities: () => {},
  onConfigChanged: () => {},
  onSaveChanged: () => {},
  dispose: () => {},
};

const sets = {
  items: fixture.items,
  recipes: fixture.recipes,
  techs: fixture.techs,
  models: fixture.models ?? [],
  veins: fixture.veins ?? [],
  signals: fixture.signals ?? [],
};

function makeExportData(name, args) {
  if (name === 'game_export_text') {
    const rows = fixture.texts ?? [];
    const offset = Number(args.offset ?? 0);
    const limit = Number(args.limit ?? 200);
    const page = rows.slice(offset, offset + limit);
    return { offset, limit, total: rows.length, done: offset + page.length >= rows.length, rows: page };
  }
  const set = args.set;
  const rows = sets[set] ?? [];
  const offset = Number(args.offset ?? 0);
  const limit = Number(args.limit ?? 200);
  const page = rows.slice(offset, offset + limit);
  return { set, offset, limit, total: rows.length, done: offset + page.length >= rows.length, rows: page };
}

try {
  const store = new KnowledgeStore(join(temp, 'kb'));
  const knowledge = new KnowledgeManager({ store, chunkSize: 50, timeoutMs: 1000 });
  const socket = new FakeSocket();
  const session = new Session(
    socket,
    {
      token: 'test-token',
      serverVersion: 'test',
      agent,
      config,
      tools: toolNamesForCapabilities,
      knowledge,
    },
    () => {},
  );

  session.handleFrame(
    Buffer.from(JSON.stringify({
      v: 1,
      id: 'h-1',
      type: 'hello',
      payload: {
        token: 'test-token',
        modVersion: 'test',
        gameVersion: 'test-1.0',
        gameLanguage: 'zh-CN',
        capabilities: [],
        saveKey: null,
      },
    })),
    false,
  );

  const ready = socket.frames.find((frame) => frame.type === 'ready');
  check('收到 ready', Boolean(ready));
  const tools = ready?.payload?.tools ?? [];
  check('ready.tools 含 kb 工具', ['kb_search', 'kb_get', 'kb_recipe_tree'].every((name) => tools.includes(name)));
  check('无 game 能力时不广告 game_*', !tools.some((name) => name.startsWith('game_')));

  session.handleFrame(
    Buffer.from(JSON.stringify({
      v: 1,
      id: 's-1',
      type: 'save.bind',
      payload: { saveKey: 'saveA', saveName: '存档 A' },
    })),
    false,
  );
  check('收到 save.bound', socket.frames.some((frame) => frame.type === 'save.bound'));

  // 模拟 mod：回应所有 internal tool_call。
  const answered = new Set();
  const deadline = Date.now() + 3000;
  while (!store.isReady && Date.now() < deadline) {
    for (const frame of socket.frames) {
      if (frame.type !== 'tool_call' || frame.payload?.internal !== true) continue;
      const callId = frame.payload.callId;
      if (answered.has(callId)) continue;
      answered.add(callId);
      const data = makeExportData(frame.payload.name, frame.payload.args ?? {});
      session.handleFrame(
        Buffer.from(JSON.stringify({
          v: 1,
          id: `r-${answered.size}`,
          type: 'tool_result',
          replyTo: callId,
          payload: { callId, ok: true, data, error: null, meta: {} },
        })),
        false,
      );
    }
    await delay(5);
  }

  check('知识库已就绪', store.isReady === true, JSON.stringify(store.meta()));
  check('store 物品数', store.meta().counts?.items === 11, JSON.stringify(store.meta().counts));
  check('store 文本数', store.meta().counts?.texts === 5, JSON.stringify(store.meta().counts));
  const internalCalls = socket.frames.filter((frame) => frame.type === 'tool_call' && frame.payload?.internal === true);
  check('导出调用带 internal=true', internalCalls.length > 0);
  check('导出调用名正确', internalCalls.every((frame) => frame.payload.name === 'game_export_prototypes' || frame.payload.name === 'game_export_text'));
  check('缓存文件可重新加载', new KnowledgeStore(join(temp, 'kb')).loadFromDisk('test-1.0', 'zh-CN')?.stats.items === 11);

  session.dispose('test done');
} finally {
  rmSync(temp, { recursive: true, force: true });
}

console.log(`\n${failed === 0 ? '✅' : '❌'} kb-session 测试${failed === 0 ? '全部通过' : '有失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
