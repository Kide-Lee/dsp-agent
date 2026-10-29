#!/usr/bin/env node
/**
 * sidecar 负向 / 边界测试。
 *
 * 这些用例覆盖"协议被违反时服务端必须怎么反应"——它们比正常路径更容易在
 * 后续重构中被悄悄破坏，所以固化成脚本。
 *
 *   node test/negative.mjs
 *
 * 需要先构建：npm run build（npm test 已包含）。
 */

import { spawn } from 'node:child_process';
import { rmSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocket } from 'ws';
import { makeTempDir } from './helpers.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ENTRY = join(HERE, '..', 'dist', 'index.js');
const TOKEN = 'test-token-negative';

let passed = 0;
let failed = 0;

function ok(name) { passed++; console.log(`  ok   ${name}`); }
function fail(name, detail) { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); }
function check(name, cond, detail) { cond ? ok(name) : fail(name, detail); }

/** 启动 sidecar，等它打印 READY，返回 { proc, port, stop() }。 */
function startSidecar() {
  return new Promise((resolve, reject) => {
    const dataDir = makeTempDir('icarus-ii-neg-');
    const proc = spawn(process.execPath, [ENTRY, 'serve', '--port', '0', '--token', TOKEN, '--data-dir', dataDir], {
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let out = '';
    let settled = false;
    const timer = setTimeout(() => {
      if (!settled) { settled = true; proc.kill(); reject(new Error('sidecar 启动超时')); }
    }, 10000);

    proc.stdout.on('data', (chunk) => {
      out += chunk.toString();
      const line = out.split('\n').find((l) => l.startsWith('READY '));
      if (line && !settled) {
        settled = true;
        clearTimeout(timer);
        const info = JSON.parse(line.slice('READY '.length));
        resolve({
          proc,
          port: info.port,
          stop() {
            try { proc.kill(); } catch { /* ignore */ }
            try { rmSync(dataDir, { recursive: true, force: true }); } catch { /* ignore */ }
          },
        });
      }
    });

    proc.stderr.on('data', () => { /* sidecar 日志走 stderr，这里不关心 */ });
    proc.on('error', (e) => { if (!settled) { settled = true; clearTimeout(timer); reject(e); } });
    proc.on('exit', (code) => {
      if (!settled) { settled = true; clearTimeout(timer); reject(new Error(`sidecar 提前退出 code=${code}`)); }
    });
  });
}

/** 连上后发一帧，收集响应直到连接关闭或超时。 */
function exchange(port, frame, { timeoutMs = 4000 } = {}) {
  return new Promise((resolve) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const messages = [];
    let closed = null;

    const finish = () => resolve({ messages, closed });

    const timer = setTimeout(() => { try { ws.terminate(); } catch {} finish(); }, timeoutMs);

    ws.on('open', () => {
      if (frame !== null) ws.send(JSON.stringify(frame));
    });
    ws.on('message', (d) => {
      try { messages.push(JSON.parse(d.toString())); } catch { messages.push({ __unparsable: d.toString() }); }
    });
    ws.on('close', (code) => { clearTimeout(timer); closed = code; finish(); });
    ws.on('error', () => { /* close 会跟着来 */ });
  });
}

function helloFrame(token, version = 1) {
  return {
    v: version,
    id: 'm-1',
    type: 'hello',
    payload: {
      token,
      modVersion: '0.1.0',
      gameVersion: 'test',
      gameLanguage: 'zh-CN',
      capabilities: [],
    },
  };
}

function firstOfType(messages, type) {
  return messages.find((m) => m && m.type === type);
}

async function main() {
  if (!existsSync(ENTRY)) {
    console.error(`找不到 ${ENTRY}，请先运行 npm run build`);
    process.exit(2);
  }

  const server = await startSidecar();
  console.log(`── sidecar 已在 127.0.0.1:${server.port} 启动 ──`);

  try {
    // 1) 错误 token → error{unauthorized} + close 1008
    {
      const { messages, closed } = await exchange(server.port, helloFrame('wrong-token'));
      const err = firstOfType(messages, 'error');
      check('错误 token 返回 error', !!err);
      check('错误 token 的 code = unauthorized', err?.payload?.code === 'unauthorized', `实际 ${err?.payload?.code}`);
      check('错误 token 后关闭码 = 1008', closed === 1008, `实际 ${closed}`);
      check('错误 token 不发 ready', !firstOfType(messages, 'ready'));
    }

    // 2) 协议版本不匹配 → error{protocol_mismatch} + close 1008
    {
      const { messages, closed } = await exchange(server.port, helloFrame(TOKEN, 999));
      const err = firstOfType(messages, 'error');
      check('版本不匹配返回 error', !!err);
      check('版本不匹配的 code = protocol_mismatch', err?.payload?.code === 'protocol_mismatch', `实际 ${err?.payload?.code}`);
      check('版本不匹配后关闭码 = 1008', closed === 1008, `实际 ${closed}`);
    }

    // 3) 正确 token → ready，且 ready 里带 sessionId / protocol
    {
      const { messages } = await exchange(server.port, helloFrame(TOKEN), { timeoutMs: 1500 });
      const ready = firstOfType(messages, 'ready');
      check('正确 token 返回 ready', !!ready);
      check('ready.protocol = 1', ready?.payload?.protocol === 1, `实际 ${ready?.payload?.protocol}`);
      check('ready 带 sessionId', typeof ready?.payload?.sessionId === 'string' && ready.payload.sessionId.length > 0);
    }

    // 4) 未知 type 必须被忽略且不断开（前向兼容，协议 §2）
    {
      const { messages, closed } = await exchange(server.port, null, { timeoutMs: 1200 });
      // 上面这次没发 hello，先单独做：连上 → hello → 未知类型 → ping
      void messages; void closed;
    }
    {
      const result = await new Promise((resolve) => {
        const ws = new WebSocket(`ws://127.0.0.1:${server.port}`);
        const messages = [];
        let closed = null;
        const timer = setTimeout(() => resolve({ messages, closed, timedOut: true }), 3000);
        ws.on('open', () => ws.send(JSON.stringify(helloFrame(TOKEN))));
        ws.on('message', (d) => {
          const m = JSON.parse(d.toString());
          messages.push(m);
          if (m.type === 'ready') {
            ws.send(JSON.stringify({ v: 1, id: 'm-2', type: 'totally_unknown_type', payload: { x: 1 } }));
            ws.send(JSON.stringify({ v: 1, id: 'm-3', type: 'ping', payload: { t: 4242 } }));
          }
          if (m.type === 'pong') { clearTimeout(timer); ws.close(); }
        });
        ws.on('close', (code) => { closed = code; clearTimeout(timer); resolve({ messages, closed }); });
        ws.on('error', () => {});
      });
      const pong = firstOfType(result.messages, 'pong');
      check('未知 type 被忽略且连接保持', !!pong, '未在未知 type 之后收到 pong');
      check('pong 原样回传 t', pong?.payload?.t === 4242, `实际 ${pong?.payload?.t}`);
      check('pong 的 replyTo 指向 ping', pong?.replyTo === 'm-3', `实际 ${pong?.replyTo}`);
    }

    // 5) 非 JSON 帧 → error{bad_request}，但不应在 hello 之前就断开
    {
      const result = await new Promise((resolve) => {
        const ws = new WebSocket(`ws://127.0.0.1:${server.port}`);
        const messages = [];
        let closed = null;
        const timer = setTimeout(() => resolve({ messages, closed, timedOut: true }), 3000);
        ws.on('open', () => ws.send(JSON.stringify(helloFrame(TOKEN))));
        ws.on('message', (d) => {
          const m = JSON.parse(d.toString());
          messages.push(m);
          if (m.type === 'ready') ws.send('this is not json');
        });
        ws.on('close', (code) => { closed = code; clearTimeout(timer); resolve({ messages, closed }); });
        ws.on('error', () => {});
        // 给服务端一点时间回 bad_request
        setTimeout(() => { if (closed === null) { ws.close(); } }, 1500);
      });
      const err = firstOfType(result.messages, 'error');
      check('非 JSON 帧返回 error', !!err, '未收到 error');
      check('非 JSON 帧的 code = bad_request', err?.payload?.code === 'bad_request', `实际 ${err?.payload?.code}`);
    }
  } finally {
    server.stop();
  }

  // 6) 非回环地址必须在启动阶段被拒绝
  {
    const dataDir = makeTempDir('icarus-ii-host-');
    const proc = spawn(process.execPath, [
      ENTRY, 'serve', '--host', '0.0.0.0', '--port', '0', '--token', TOKEN, '--data-dir', dataDir,
    ], { stdio: ['ignore', 'pipe', 'pipe'] });
    let stderr = '';
    proc.stderr.on('data', (chunk) => { stderr += chunk.toString(); });
    const code = await new Promise((resolve) => proc.on('exit', resolve));
    check('非回环 --host 被拒绝', code === 2, `code=${code} stderr=${stderr.trim()}`);
    try { rmSync(dataDir, { recursive: true, force: true }); } catch { /* ignore */ }
  }

  console.log();
  if (failed === 0) {
    console.log(`✅ 负向测试全部通过（${passed} 项）`);
    process.exit(0);
  } else {
    console.log(`❌ 负向测试失败 ${failed} / ${passed + failed} 项`);
    process.exit(1);
  }
}

main().catch((e) => {
  console.error('测试运行异常：', e);
  process.exit(1);
});
