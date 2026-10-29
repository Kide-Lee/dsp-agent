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
 *   1. happy path：帧顺序 / delta 拼接 == message 文本 / **恰好一个终稿帧** /
 *      上游请求形状（默认 thinking enabled/high、无 game_* 工具、有 kb_* 工具、Authorization 用的是我们注入的 key）/
 *      **system prompt 已被替换成游戏内助手人格**（问题 1 与问题 3 的回归）
 *   2. 401 → error{invalid_key} + config.state{invalid_key}，
 *      且上游回显的 key 被脱敏后才出站；下一次成功 → config.state{ready}
 *   3. 429 → error{rate_limited}（并覆盖 status{retrying}）
 *   4. 连不通的端口 → error{upstream_error} + config.state{unreachable}
 *   5. 未知 provider → error{agent_unavailable}
 *   6. 流式过程中 config.set → 流不被截断（且仍然只有一个终稿帧）
 *   7. 以上所有场景：出站帧与 sidecar stderr 都不含密钥明文
 *
 * 收尾保证：每个等待都有超时、整个文件有看门狗；任何失败都**立刻**清理
 * （关 WS、SIGKILL 子进程、关夹具并强断 keep-alive 连接、删数据目录）后退出，
 * 绝不让 `npm test` 挂住。
 */

import { createServer } from 'node:http';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { maskSecret } from '../dist/config.js';
import { ICARUS_II_PERSONA_NAME, ICARUS_II_SYSTEM_PROMPT } from '../dist/persona.js';
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
import { makeTempDir } from './helpers.mjs';

const KEY = FAKE_KEY;
const PROVIDER = 'deepseek';
const MODEL = 'deepseek-v4-flash';

/**
 * pi 默认 system prompt（coding agent）的特征串。
 *
 * 这些串**必须**在发给上游的整个请求体里一个都不出现：pi 的默认 prompt 是英文的
 * （见 pi 的 core/system-prompt.js），而我们的人格是中文的，所以不会有误伤。
 * 其中 `Current working directory` 最关键：pi 在 customPrompt 分支里**无条件**追加
 * 这一行，实机就导致模型开口就说"我看到当前工作目录是 …"。
 */
const PI_DEFAULT_PROMPT_MARKERS = [
  'You are an expert coding assistant',
  'coding agent harness',
  'Available tools:',
  'Current working directory',
  'working directory',
  'AGENTS.md',
  'CLAUDE.md',
  'Pi documentation',
  'pi-coding-agent',
];

/** 人格里必须出现的稳定标识串（mod 名）。 */
const PERSONA_MARKER = 'Icarus II';

/** system/user 消息的 content 可能是字符串，也可能是 text block 数组。 */
function messageText(message) {
  const content = message?.content;
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content
    .filter((block) => block && typeof block === 'object' && block.type === 'text')
    .map((block) => block.text ?? '')
    .join('');
}

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

  dataDir = makeTempDir('icarus-ii-loopback-');
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
    const messages = round.filter((f) => f.type === 'message');
    const message = messages[0];
    const text = message?.payload?.content?.[0]?.text;
    check('delta 拼接 = "你好，世界！"', joined === '你好，世界！', joined);
    check('message 文本 = delta 拼接', text === joined, `${text} != ${joined}`);
    check('message.final = true', message?.payload?.final === true);
    check('message.role = assistant', message?.payload?.role === 'assistant');
    check('stream/message 的 replyTo 指向 prompt', streams.every((f) => f.replyTo === 'm-happy') && message?.replyTo === 'm-happy');
    check('stream channel 均为 text', streams.every((f) => f.payload.channel === 'text'));

    // ---- 问题 2 回归：一次 prompt 恰好一个终稿帧（不是 >= 1）----
    // 曾经实机出现过"回答在窗口里显示两遍"。这里把协议不变量钉死：
    // 一次 prompt 的 message 帧数必须**恰好为 1**，且是 final。
    check('一次 prompt 恰好 1 个 message 帧', messages.length === 1, `实际 ${messages.length} 个`);
    check('恰好 1 个 message{final:true} 帧', round.filter((f) => f.type === 'message' && f.payload?.final === true).length === 1);
    check('终稿帧文本非空且 = delta 拼接', typeof text === 'string' && text.length > 0 && text === joined);

    // 上游请求形状：证明 M3 纯对话（只有 kb_* 本地工具），且默认档位 high 会请求思考。
    const body = upstream.lastBody();
    const last = upstream.requests[upstream.requests.length - 1];
    check('上游收到 POST /chat/completions', last?.method === 'POST' && last?.url === '/chat/completions', `${last?.method} ${last?.url}`);
    check('上游 model 正确', body.model === MODEL, body.model);
    check('上游要求流式', body.stream === true);
    check(
      '默认请求思考：thinking = {type:"enabled"}',
      body.thinking?.type === 'enabled',
      JSON.stringify(body.thinking ?? null),
    );
    check('默认思考档位 high', body.reasoning_effort === 'high', String(body.reasoning_effort));
    // M3：kb_* 是 sidecar 本地工具，始终注册；没有 game.read.v1 时不应出现 game_*。
    const toolNames = Array.isArray(body.tools)
      ? body.tools.map((tool) => tool?.function?.name).filter((name) => typeof name === 'string')
      : [];
    check('M3 纯对话：不注册 game_* 工具', !toolNames.some((name) => name.startsWith('game_')), JSON.stringify(toolNames));
    check('M3 纯对话：注册 kb_* 工具', toolNames.includes('kb_search') && toolNames.includes('kb_get') && toolNames.includes('kb_recipe_tree'), JSON.stringify(toolNames));
    check('Authorization 用的是 config.set 注入的 key', last?.authorization === `Bearer ${KEY}`);

    // ---- 问题 3：system prompt 已经被整个替换（人格），pi 的 coding prompt 不在请求里 ----
    const reqMessages = body.messages;
    check('上游请求里有 messages 数组', Array.isArray(reqMessages) && reqMessages.length > 0);
    const systemMessage = Array.isArray(reqMessages) ? reqMessages[0] : undefined;
    check('messages[0].role === "system"', systemMessage?.role === 'system', systemMessage?.role);
    const systemText = messageText(systemMessage);
    check('system message 非空', systemText.length > 0, `${systemText.length} 字符`);
    // 人格之后允许追加本轮的动态段（权限档位/强制工具/沙盒状态），
    // 但人格本身必须逐字保留在开头，不能被 pi 的默认 prompt 混入。
    check(
      'system message 以代码里的游戏内助手人格开头',
      systemText === ICARUS_II_SYSTEM_PROMPT ||
        systemText.startsWith(`${ICARUS_II_SYSTEM_PROMPT}\n\n`),
      `收到 ${systemText.length} 字符 / 人格 ${ICARUS_II_SYSTEM_PROMPT.length} 字符`,
    );
    check('system message 含当前权限说明', systemText.includes('当前权限为'));
    check(
      `system message 含人格标识串「${PERSONA_MARKER}」`,
      systemText.includes(PERSONA_MARKER) && systemText.includes(ICARUS_II_PERSONA_NAME),
    );
    for (const marker of PI_DEFAULT_PROMPT_MARKERS) {
      check(
        `system message 不含 pi 默认 prompt 特征：${JSON.stringify(marker)}`,
        !systemText.includes(marker),
      );
    }
    const rawBody = last?.body ?? '';
    for (const marker of PI_DEFAULT_PROMPT_MARKERS) {
      check(
        `整个请求体不含 pi 默认 prompt 特征：${JSON.stringify(marker)}`,
        !rawBody.includes(marker),
      );
    }
    // coding agent 之外，还要确认"它没在承诺自己没有的能力"这件事靠 prompt 兜住了：
    // 这个场景没有 game.* 能力，能力段落必须按注册表只列 kb/wiki/蓝图/脚本，不能凭空出现 game_*。
    check(
      'system message 说明实际注册的读工具边界',
      systemText.includes('本会话注册的工具') &&
        systemText.includes('kb_search') &&
        !systemText.includes('game_get_overview') &&
        !systemText.includes('game_drop_item') &&
        !systemText.includes('你只能读取当前存档'),
    );
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
    await configure('c-4', { provider: 'no-such-provider', model: 'no-such-model', apiKey: KEY });

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
    check(
      '慢速路径同样恰好 1 个终稿帧',
      round.filter((f) => f.type === 'message' && f.payload?.final === true).length === 1,
      `实际 ${round.filter((f) => f.type === 'message').length} 个`,
    );
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
