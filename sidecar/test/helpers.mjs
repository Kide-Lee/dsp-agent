#!/usr/bin/env node
/**
 * 测试公用小工具（仅 `test/` 使用；不参与打包）。
 *
 * 约定与 test/negative.mjs 一致：
 *   - sidecar 是真实子进程，靠 stdout 的 `READY {...}` 抓端口
 *   - 所有测试都必须能离线跑：任何会真正发请求的用例都要显式跳过
 */

import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocket } from 'ws';

export const HERE = dirname(fileURLToPath(import.meta.url));
export const ENTRY = join(HERE, '..', 'dist', 'index.js');

export function assertBuilt() {
  if (!existsSync(ENTRY)) {
    console.error(`找不到 ${ENTRY}，请先运行 npm run build`);
    process.exit(2);
  }
}

/** 语料：假的、明显不是真 key 的字符串（绝不使用真实密钥）。 */
export const FAKE_KEY = 'sk-test-0000000000000000000000000000000000';

/** 清掉可能让 sidecar 走「环境变量凭证」的变量，保证测试确定性。 */
export function cleanEnv(extra = {}) {
  const env = { ...process.env, ...extra };
  delete env.DEEPSEEK_API_KEY;
  delete env.PI_OFFLINE;
  delete env.PI_CODING_AGENT_DIR;
  delete env.PI_CODING_AGENT_SESSION_DIR;
  return env;
}

/**
 * 启动 sidecar 并等 READY。
 * @returns {Promise<{proc: import('node:child_process').ChildProcess, port: number,
 *                    token: string, dataDir: string, stderr: () => string,
 *                    stop: () => void}>}
 */
export function startSidecar({ agent = 'pi', token = 'test-token', dataDir, extraArgs = [] } = {}) {
  return new Promise((resolve, reject) => {
    const dir = dataDir ?? mkdtempSync(join(tmpdir(), 'icarus-ii-test-'));
    const ownsDir = dataDir === undefined;
    const args = [
      ENTRY,
      'serve',
      '--agent',
      agent,
      '--port',
      '0',
      '--token',
      token,
      '--data-dir',
      dir,
      ...extraArgs,
    ];
    const proc = spawn(process.execPath, args, { stdio: ['ignore', 'pipe', 'pipe'], env: cleanEnv() });

    let out = '';
    let err = '';
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      proc.kill('SIGKILL');
      reject(new Error(`sidecar 启动超时\nstderr:\n${err}`));
    }, 15000);

    const stop = () => {
      try {
        proc.kill('SIGKILL');
      } catch {
        /* ignore */
      }
      if (ownsDir) {
        try {
          rmSync(dir, { recursive: true, force: true });
        } catch {
          /* ignore */
        }
      }
    };

    proc.stdout.on('data', (chunk) => {
      out += chunk.toString();
      const line = out.split('\n').find((l) => l.startsWith('READY '));
      if (line && !settled) {
        settled = true;
        clearTimeout(timer);
        const info = JSON.parse(line.slice('READY '.length));
        resolve({ proc, port: info.port, token, dataDir: dir, stderr: () => err, stop });
      }
    });
    proc.stderr.on('data', (chunk) => {
      err += chunk.toString();
    });
    proc.on('error', (e) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(e);
    });
    proc.on('exit', (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(new Error(`sidecar 提前退出 code=${code}\nstderr:\n${err}`));
    });
  });
}

export function statMode(path) {
  return statSync(path).mode & 0o777;
}

export function helloFrame(token, version = 1) {
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

/**
 * 极简 WebSocket 客户端：把所有入站帧（含原始文本）都留下来，
 * 方便断言「任何出站帧都不含密钥」。
 */
export class Client {
  constructor(ws) {
    this.ws = ws;
    /** @type {object[]} */
    this.frames = [];
    /** @type {string[]} */
    this.raws = [];
    this.closedCode = null;
    this._waiters = [];
    ws.on('message', (data) => {
      const text = data.toString();
      this.raws.push(text);
      let parsed;
      try {
        parsed = JSON.parse(text);
      } catch {
        parsed = { __unparsable: text };
      }
      this.frames.push(parsed);
      this._notify();
    });
    ws.on('close', (code) => {
      this.closedCode = code;
      this._notify();
    });
    ws.on('error', () => {
      /* close 会跟着来 */
    });
  }

  _notify() {
    const waiters = this._waiters;
    this._waiters = [];
    for (const w of waiters) w();
  }

  send(obj) {
    this.ws.send(typeof obj === 'string' ? obj : JSON.stringify(obj));
  }

  /** 等到出现满足条件的帧（已出现的也算）。 */
  waitFor(predicate, timeoutMs = 5000, label = 'frame') {
    const found = this.frames.find(predicate);
    if (found) return Promise.resolve(found);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`等待 ${label} 超时`)), timeoutMs);
      const check = () => {
        const hit = this.frames.find(predicate);
        if (!hit) {
          this._waiters.push(check);
          return;
        }
        clearTimeout(timer);
        resolve(hit);
      };
      this._waiters.push(check);
    });
  }

  waitForType(type, timeoutMs = 5000) {
    return this.waitFor((f) => f && f.type === type, timeoutMs, `type=${type}`);
  }

  waitForCount(type, count, timeoutMs = 5000) {
    return this.waitFor(
      () => this.frames.filter((f) => f && f.type === type).length >= count,
      timeoutMs,
      `${count} 个 type=${type}`,
    );
  }

  close() {
    try {
      this.ws.close();
    } catch {
      /* ignore */
    }
  }
}

/** 连上 sidecar；默认立刻发 hello 并等 ready。 */
export async function connect(port, { token, hello = true, timeoutMs = 5000 } = {}) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}`);
  const client = new Client(ws);
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('WebSocket 连接超时')), timeoutMs);
    ws.on('open', () => {
      clearTimeout(timer);
      resolve();
    });
    ws.on('error', (e) => {
      clearTimeout(timer);
      reject(e);
    });
  });
  if (hello) {
    client.send(helloFrame(token));
    await client.waitForType('ready', timeoutMs);
  }
  return client;
}

export function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** 深度查找帧里任何名为 `apiKey` 的字段。 */
export function findApiKeyField(value, path = '$') {
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i += 1) {
      const hit = findApiKeyField(value[i], `${path}[${i}]`);
      if (hit) return hit;
    }
    return null;
  }
  if (value !== null && typeof value === 'object') {
    for (const [key, child] of Object.entries(value)) {
      if (key.toLowerCase() === 'apikey') return `${path}.${key}`;
      const hit = findApiKeyField(child, `${path}.${key}`);
      if (hit) return hit;
    }
  }
  return null;
}

// ---------------------------------------------------------------- 断言小工具

let passed = 0;
let failed = 0;

export function ok(name) {
  passed += 1;
  console.log(`  ok   ${name}`);
}

export function fail(name, detail) {
  failed += 1;
  console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`);
}

export function check(name, cond, detail) {
  if (cond) ok(name);
  else fail(name, detail);
}

export function summary(label) {
  console.log();
  if (failed === 0) {
    console.log(`✅ ${label}全部通过（${passed} 项）`);
    return true;
  }
  console.log(`❌ ${label}失败 ${failed} / ${passed + failed} 项`);
  return false;
}
