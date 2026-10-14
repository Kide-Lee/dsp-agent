#!/usr/bin/env node
/**
 * pi 全链路离线测试：用一个**本地 loopback 假上游**替代 DeepSeek。
 *
 *   node test/pi-loopback.mjs
 *
 * 为什么需要它：`--agent pi` 的代码路径（ModelRuntime 凭证注入、createAgentSession、
 * pi 事件 → 协议帧映射、错误分类、密钥脱敏）是 M1 的核心，而真实端点
 * （test/live.mjs）在没有 API Key 的环境里永远跑不了。这个夹具完全离线、
 * 确定性、可重复，是唯一能在 CI 里自动证明这条链路正确的测试。
 *
 * **不联网**：只绑定 127.0.0.1 的临时端口，由本进程自己起停；不读任何真实密钥
 * （`startSidecar` 会把 `DEEPSEEK_API_KEY` 从子进程环境里删掉，密钥用假值走
 * `config.set`）。
 *
 * 覆盖：
 *   1. happy path：帧顺序 / delta 拼接 == message 文本 / 上游请求形状
 *      （thinking disabled、无 tools、Authorization 用的是我们注入的 key）
 *   2. 401 → error{invalid_key} + config.state{invalid_key}，
 *      且上游回显的 key 被脱敏后才出站；下一次成功 → config.state{ready}
 *   3. 429 → error{rate_limited}（并覆盖 status{retrying}）
 *   4. 连不通的端口 → error{upstream_error} + config.state{unreachable}
 *   5. 未知 provider → error{agent_unavailable}
 *   6. 流式过程中 config.set → 流不被截断
 *   7. 以上所有场景：出站帧与 sidecar stderr 都不含密钥明文
 *
 * 收尾保证：每个等待都有超时、整个文件有看门狗；任何失败都**立刻**清理
 * （关 WS、SIGKILL 子进程、关夹具并强断 keep-alive 连接、删数据目录）后退出，
 * 绝不让 `npm test` 挂住。
 */

import { createServer } from 'node:http';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { maskSecret } from '../dist/config.js';
import {
  FAKE_KEY,
  assertBuilt,
  check,
  connect,
  delay,
  findApiKeyField,
  startSidecar,
  summary,
} from './helpers.mjs';

const KEY = FAKE_KEY;
const PROVIDER = 'deepseek';
const MODEL = 'deepseek-v4-flash';

/** 单个等待的上限；夹具走 loopback，正常都在毫秒级。 */
const WAIT_MS = 20000;
/** 整个文件的上限——兜底，防止任何意外把 `npm test` 挂住。 */
const WATCHDOG_MS = 120000;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// ---------------------------------------------------------------- 假上游

/**
 * OpenAI-compatible 假上游：按 `mode` 回应，并记录收到的请求。
 *
 * mode: `ok`（3 段快速流）| `slow`（3 段带间隔）| `401` | `429`
 */
class FixtureUpstream {
  constructor() {
    /** @type {'ok'|'slow'|'401'|'429'} */
    this.mode = 'ok';
    /** @type {{url: string|undefined, method: string|undefined, authorization: string|null, body: string}[]} */
    this.requests = [];
    this.server = createServer((req, res) => {
      let body = '';
      req.on('data', (chunk) => {
        body += chunk;
      });
      req.on('end', () => {
        this.requests.push({
          url: req.url,
          method: req.method,
          authorization: req.headers.authorization ?? null,
          body,
        });
        void this._respond(res, body);
      });
      req.on('error', () => {
        /* 客户端提前断开 */
      });
    });
  }

  async start() {
    await new Promise((resolve, reject) => {
      this.server.once('error', reject);
      this.server.listen(0, '127.0.0.1', resolve);
    });
    this.port = this.server.address().port;
    return this;
  }

  get baseUrl() {
    return `http://127.0.0.1:${this.port}`;
  }

  /** 最后一个请求的 JSON body（解析失败给空对象）。 */
  lastBody() {
    const last = this.requests[this.requests.length - 1];
    if (!last) return {};
    try {
      return JSON.parse(last.body);
    } catch {
      return {};
    }
  }

  async _respond(res, body) {
    void body;
    const mode = this.mode;

    if (mode === '401') {
      // 故意把 key 回显在错误体里：这样才能证明 sidecar 真的脱敏了，
      // 而不是「上游碰巧没提到 key」。
      res.writeHead(401, { 'content-type': 'application/json; charset=utf-8' });
      res.end(
        JSON.stringify({
          error: {
            message: `Authentication Fails, Your api key: ${KEY} is invalid`,
            type: 'authentication_error',
          },
        }),
      );
      return;
    }

    if (mode === '429') {
      res.writeHead(429, { 'content-type': 'application/json; charset=utf-8', 'retry-after': '0' });
      res.end(
        JSON.stringify({
          error: { message: 'Rate limit reached for requests', type: 'rate_limit_error' },
        }),
      );
      return;
    }

    const chunks = mode === 'slow' ? ['第一段', '第二段', '第三段'] : ['你好', '，世界', '！'];
    res.writeHead(200, {
      'content-type': 'text/event-stream; charset=utf-8',
      'cache-control': 'no-cache',
      connection: 'keep-alive',
    });
    const base = {
      id: 'chatcmpl-loopback',
      object: 'chat.completion.chunk',
      created: 1,
      model: MODEL,
    };
    try {
      for (const text of chunks) {
        if (res.destroyed || res.writableEnded) return;
        res.write(
          `data: ${JSON.stringify({ ...base, choices: [{ index: 0, delta: { content: text }, finish_reason: null }] })}\n\n`,
        );
        if (mode === 'slow') await sleep(150);
      }
      res.write(
        `data: ${JSON.stringify({ ...base, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] })}\n\n`,
      );
      res.write('data: [DONE]\n\n');
      res.end();
    } catch {
      try {
        res.destroy();
      } catch {
        /* ignore */
      }
    }
  }

  async close() {
    // 先强断 keep-alive 连接，否则 close() 会一直等它们自然结束。
    try {
      this.server.closeAllConnections?.();
    } catch {
      /* ignore */
    }
    await new Promise((resolve) => {
      const timer = setTimeout(resolve, 1000);
      timer.unref?.();
      try {
        this.server.close(() => {
          clearTimeout(timer);
          resolve();
        });
      } catch {
        clearTimeout(timer);
        resolve();
      }
    });
  }
}

/** 占一个空闲端口再立刻释放，得到一个「一定没人监听」的端口号。 */
async function reserveDeadPort() {
  const probe = createServer();
  await new Promise((resolve) => probe.listen(0, '127.0.0.1', resolve));
  const port = probe.address().port;
  await new Promise((resolve) => probe.close(resolve));
  return port;
}

// ---------------------------------------------------------------- 收尾

const upstream = new FixtureUpstream();
let server = null;
let client = null;
let dataDir = null;
let finished = false;

function cleanup() {
  if (finished) return;
  finished = true;
  try {
    client?.close();
  } catch {
    /* ignore */
  }
  try {
    server?.stop(); // 内含 SIGKILL
  } catch {
    /* ignore */
  }
  // 夹具是异步关闭的；这里同步入口 + 顶层 finally 里的 await 兜住。
  void upstream.close();
  if (dataDir) {
    try {
      rmSync(dataDir, { recursive: true, force: true });
    } catch {
      /* ignore */
    }
  }
}

const watchdog = setTimeout(() => {
  console.error(`\n❌ pi-loopback 测试超过 ${WATCHDOG_MS}ms 看门狗上限，强制收尾`);
  cleanup();
  process.exit(1);
}, WATCHDOG_MS);

process.on('exit', cleanup);
process.on('SIGINT', () => {
  cleanup();
  process.exit(130);
});
process.on('SIGTERM', () => {
  cleanup();
  process.exit(143);
});

// ---------------------------------------------------------------- 场景

/** 记录 mark 之后收到的帧，便于按场景断言。 */
function since(mark) {
  return client.frames.slice(mark);
}

function statusesIn(frames) {
  return frames.filter((f) => f && f.type === 'status');
}

/** 等这一轮 prompt 结束（status{idle} 数量达到预期）。 */
async function waitForIdle(expectedIdle, timeoutMs = WAIT_MS) {
  await client.waitUntil(
    () =>
      client.frames.filter((f) => f && f.type === 'status' && f.payload?.state === 'idle').length >=
      expectedIdle,
    timeoutMs,
    `第 ${expectedIdle} 个 status{idle}`,
  );
}

function idleCount() {
  return client.frames.filter((f) => f && f.type === 'status' && f.payload?.state === 'idle').length;
}

/** 配置并等回执。 */
async function configure(id, payload) {
  client.send({ v: 1, id, type: 'config.set', payload });
  return client.waitFor(
    (f) => f && f.type === 'config.state' && f.replyTo === id,
    WAIT_MS,
    `config.state(${id})`,
  );
}

// ---------------------------------------------------------------- 主流程

async function main() {
  assertBuilt();

  dataDir = mkdtempSync(join(tmpdir(), 'icarus-ii-loopback-'));
  // 把 pi 的重试压到最小：provider 层不重试，agent 层重试 1 次（10ms）。
  // 这样 429 场景既覆盖 status{retrying}，又不会因为退避把测试拖到几十秒。
  mkdirSync(join(dataDir, 'agent'), { recursive: true });
  writeFileSync(
    join(dataDir, 'agent', 'settings.json'),
    `${JSON.stringify(
      { retry: { enabled: true, maxRetries: 1, baseDelayMs: 10, provider: { maxRetries: 0 } } },
      null,
      2,
    )}\n`,
    'utf8',
  );

  await upstream.start();
  server = await startSidecar({ agent: 'pi', token: 'test-token-loopback', dataDir });
  client = await connect(server.port, { token: 'test-token-loopback' });

  // ------------------------------------------------------------ 1. happy path
  console.log('── 1. happy path（--agent pi → 假上游）──');
  {
    const ready = client.frames.find((f) => f && f.type === 'ready');
    check('未配置时 ready.config.state = missing_key', ready?.payload?.config?.state === 'missing_key');

    const configured = await configure('c-1', {
      provider: PROVIDER,
      model: MODEL,
      apiKey: KEY,
      baseUrl: upstream.baseUrl,
    });
    check('config.set 后 state = ready', configured?.payload?.state === 'ready', configured?.payload?.state);

    const mark = client.frames.length;
    const idles = idleCount();
    client.send({ v: 1, id: 'm-happy', type: 'prompt', payload: { text: '打个招呼' } });
    await waitForIdle(idles + 1);
    await delay(100);

    const round = since(mark);
    const order = round.map((f) => f.type).join(',');
    check(
      '帧顺序 = status,stream,stream,stream,message,status',
      order === 'status,stream,stream,stream,message,status',
      order,
    );
    check('首帧 status{thinking}', round[0]?.payload?.state === 'thinking', round[0]?.payload?.state);
    check('末帧 status{idle}', round[round.length - 1]?.payload?.state === 'idle');

    const streams = round.filter((f) => f.type === 'stream');
    const joined = streams.map((f) => f.payload.delta).join('');
    const message = round.find((f) => f.type === 'message');
    const text = message?.payload?.content?.[0]?.text;
    check('delta 拼接 = "你好，世界！"', joined === '你好，世界！', joined);
    check('message 文本 = delta 拼接', text === joined, `${text} != ${joined}`);
    check('message.final = true', message?.payload?.final === true);
    check('message.role = assistant', message?.payload?.role === 'assistant');
    check('stream/message 的 replyTo 指向 prompt', streams.every((f) => f.replyTo === 'm-happy') && message?.replyTo === 'm-happy');
    check('stream channel 均为 text', streams.every((f) => f.payload.channel === 'text'));

    // 上游请求形状：证明纯对话（无 tools）且没有请求思考。
    const body = upstream.lastBody();
    const last = upstream.requests[upstream.requests.length - 1];
    check('上游收到 POST /chat/completions', last?.method === 'POST' && last?.url === '/chat/completions', `${last?.method} ${last?.url}`);
    check('上游 model 正确', body.model === MODEL, body.model);
    check('上游要求流式', body.stream === true);
    check(
      '不请求思考：thinking = {type:"disabled"}',
      body.thinking?.type === 'disabled',
      JSON.stringify(body.thinking ?? null),
    );
    check('不请求思考：无 reasoning_effort', body.reasoning_effort === undefined, String(body.reasoning_effort));
    check('M1 纯对话：请求里没有 tools', body.tools === undefined, JSON.stringify(body.tools ?? null));
    check('Authorization 用的是 config.set 注入的 key', last?.authorization === `Bearer ${KEY}`);
  }

  // ------------------------------------------------------------ 2. 401 → invalid_key
  console.log('── 2. 401 → invalid_key（含脱敏与恢复）──');
  {
    upstream.mode = '401';
    const mark = client.frames.length;
    const idles = idleCount();
    client.send({ v: 1, id: 'm-401', type: 'prompt', payload: { text: 'hi' } });
    await waitForIdle(idles + 1);
    await delay(100);

    const round = since(mark);
    const err = round.find((f) => f && f.type === 'error' && f.replyTo === 'm-401');
    check('401 ⇒ error{invalid_key}', err?.payload?.code === 'invalid_key', err?.payload?.code);
    check('invalid_key 不可重试', err?.payload?.retryable === false, String(err?.payload?.retryable));
    check(
      'error.message 已脱敏（含前缀…长度，不含明文）',
      typeof err?.payload?.message === 'string' &&
        err.payload.message.includes(maskSecret(KEY)) &&
        !err.payload.message.includes(KEY),
      undefined,
    );
    check('失败不发 message{final}', !round.some((f) => f.type === 'message'));
    const pushed = round.filter((f) => f.type === 'config.state');
    check(
      '主动推送 config.state{invalid_key}',
      pushed.some((f) => f.payload?.state === 'invalid_key'),
      JSON.stringify(pushed.map((f) => f.payload?.state)),
    );

    // 上游恢复 ⇒ 状态收敛回 ready
    upstream.mode = 'ok';
    const mark2 = client.frames.length;
    const idles2 = idleCount();
    client.send({ v: 1, id: 'm-recover', type: 'prompt', payload: { text: '再来' } });
    await waitForIdle(idles2 + 1);
    await delay(100);
    const round2 = since(mark2);
    check('恢复后收到 message', round2.some((f) => f.type === 'message'));
    check(
      '恢复后主动推送 config.state{ready}',
      round2.some((f) => f.type === 'config.state' && f.payload?.state === 'ready'),
      JSON.stringify(round2.filter((f) => f.type === 'config.state').map((f) => f.payload?.state)),
    );
  }

  // ------------------------------------------------------------ 3. 429 → rate_limited
  console.log('── 3. 429 → rate_limited ──');
  {
    upstream.mode = '429';
    const mark = client.frames.length;
    const idles = idleCount();
    client.send({ v: 1, id: 'm-429', type: 'prompt', payload: { text: 'hi' } });
    await waitForIdle(idles + 1);
    await delay(100);

    const round = since(mark);
    const err = round.find((f) => f && f.type === 'error' && f.replyTo === 'm-429');
    check('429 ⇒ error{rate_limited}', err?.payload?.code === 'rate_limited', err?.payload?.code);
    check('rate_limited 可重试', err?.payload?.retryable === true, String(err?.payload?.retryable));
    check(
      '覆盖 status{retrying}（pi 自动重试映射）',
      statusesIn(round).some((f) => f.payload?.state === 'retrying'),
      JSON.stringify(statusesIn(round).map((f) => f.payload?.state)),
    );
    check(
      '限流不改配置状态（仍是 ready）',
      !round.some(
        (f) => f.type === 'config.state' && f.payload?.state !== 'ready',
      ),
    );
    check('429 的 error.message 不含密钥', !JSON.stringify(err?.payload ?? {}).includes(KEY));
  }

  // ------------------------------------------------------------ 4. 连不通 → unreachable
  console.log('── 4. 连不通的端口 → upstream_error + unreachable ──');
  {
    upstream.mode = 'ok';
    const deadPort = await reserveDeadPort();
    await configure('c-2', { baseUrl: `http://127.0.0.1:${deadPort}` });

    const mark = client.frames.length;
    const idles = idleCount();
    client.send({ v: 1, id: 'm-dead', type: 'prompt', payload: { text: 'hi' } });
    await waitForIdle(idles + 1);
    await delay(100);

    const round = since(mark);
    const err = round.find((f) => f && f.type === 'error' && f.replyTo === 'm-dead');
    check('连不通 ⇒ error{upstream_error}', err?.payload?.code === 'upstream_error', err?.payload?.code);
    check(
      '连不通 ⇒ config.state{unreachable}',
      round.some((f) => f.type === 'config.state' && f.payload?.state === 'unreachable'),
      JSON.stringify(round.filter((f) => f.type === 'config.state').map((f) => f.payload?.state)),
    );
    check('unreachable 的 message 不含密钥', !JSON.stringify(err?.payload ?? {}).includes(KEY));

    // 复位到夹具
    upstream.mode = 'ok';
    await configure('c-3', { baseUrl: upstream.baseUrl });
  }

  // ------------------------------------------------------------ 5. 未知 provider → agent_unavailable
  console.log('── 5. 未知 provider → agent_unavailable ──');
  {
    await configure('c-4', { provider: 'no-such-provider', model: 'no-such-model' });

    const mark = client.frames.length;
    const idles = idleCount();
    client.send({ v: 1, id: 'm-badmodel', type: 'prompt', payload: { text: 'hi' } });
    await waitForIdle(idles + 1);
    await delay(100);

    const round = since(mark);
    const err = round.find((f) => f && f.type === 'error' && f.replyTo === 'm-badmodel');
    check('未知 provider ⇒ error{agent_unavailable}', err?.payload?.code === 'agent_unavailable', err?.payload?.code);
    check('未知 provider 不发 message', !round.some((f) => f.type === 'message'));

    await configure('c-5', { provider: PROVIDER, model: MODEL });
  }

  // ------------------------------------------------------------ 6. 流中 config.set 不截断
  console.log('── 6. 流式过程中 config.set（不得截断）──');
  {
    upstream.mode = 'slow';
    const mark = client.frames.length;
    const idles = idleCount();
    client.send({ v: 1, id: 'm-slow', type: 'prompt', payload: { text: '分段' } });
    // 等到第一段已经吐出来，再在中途改配置。
    await client.waitForType('stream', WAIT_MS);
    await configure('c-6', { model: MODEL });
    await waitForIdle(idles + 1, 30000);
    await delay(100);

    const round = since(mark);
    const deltas = round.filter((f) => f.type === 'stream' && f.replyTo === 'm-slow').map((f) => f.payload.delta);
    const message = round.find((f) => f.type === 'message' && f.replyTo === 'm-slow');
    const text = message?.payload?.content?.[0]?.text;
    check('三段增量都在', deltas.join('') === '第一段第二段第三段', JSON.stringify(deltas));
    check('message 文本完整', text === '第一段第二段第三段', text);
    check('delta 拼接 = message 文本', deltas.join('') === text);
    check('末帧 status{idle}', round[round.length - 1]?.payload?.state === 'idle');
    upstream.mode = 'ok';
  }

  // ------------------------------------------------------------ 7. 脱敏总检
  console.log('── 7. 全场景脱敏总检 ──');
  {
    const leaked = client.raws.filter((raw) => raw.includes(KEY));
    check('所有出站帧都不含密钥明文', leaked.length === 0, `泄露 ${leaked.length} 帧`);
    const field = client.frames.map((f) => findApiKeyField(f)).find(Boolean);
    check('所有出站帧都不含 apiKey 字段', !field, field ?? undefined);
    check('sidecar stderr 不含密钥明文', !server.stderr().includes(KEY));
    check(
      'stderr 里的 key 只有脱敏形态',
      !server.stderr().includes(KEY) && server.stderr().includes(maskSecret(KEY)),
      undefined,
    );
    check('夹具确实收到了请求（不是空跑）', upstream.requests.length >= 4, String(upstream.requests.length));
  }
}

let ok = false;
try {
  await main();
  ok = summary('pi loopback 测试');
} catch (err) {
  check('pi loopback 测试执行完成', false, err instanceof Error ? err.message : String(err));
  console.log();
  console.log('❌ pi loopback 测试异常中止（已强制收尾，不会挂住 npm test）');
  ok = false;
} finally {
  clearTimeout(watchdog);
  cleanup();
  await upstream.close();
}

process.exit(ok ? 0 : 1);
