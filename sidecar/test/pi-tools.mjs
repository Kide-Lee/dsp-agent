#!/usr/bin/env node
/**
 * pi 全链路工具测试（离线，loopback 假上游）。
 *
 * 与 tools.mjs 的区别：这里走真实的 `--agent pi`，验证：
 *   - `createAgentSession({ customTools })` 真的把 6 个工具发给了上游；
 *   - 上游返回 tool_calls 时，pi 会执行自定义工具；
 *   - 工具 execute 里的 host.call 会把 tool_call 发到 mod；
 *   - mod 的 tool_result 回填后，pi 继续第二轮并产出最终文本。
 *
 *   node test/pi-tools.mjs
 */

import { createServer } from 'node:http';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  FAKE_KEY,
  assertBuilt,
  check,
  connect,
  startSidecar,
  summary,
} from './helpers.mjs';

const MODEL = 'deepseek-v4-flash';
const TOKEN = 'test-token-pi-tools';

class ToolFixture {
  constructor() {
    this.requests = [];
    this.modelsRequests = 0;
    /** 'tool' = 第一轮要求调用工具；'text' = 直接回答。 */
    this.mode = 'tool';
    this.server = createServer((req, res) => {
      const path = (req.url || '').split('?')[0];
      if (req.method === 'GET' && (path === '/v1/models' || path === '/models')) {
        this.modelsRequests += 1;
        if (req.headers.authorization === 'Bearer sk-bad') {
          res.writeHead(401, { 'content-type': 'application/json; charset=utf-8' });
          res.end(JSON.stringify({ error: { message: 'invalid api key', type: 'authentication_error' } }));
          return;
        }
        res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({
          object: 'list',
          data: [
            { id: 'my-model', object: 'model', owned_by: 'fixture' },
            { id: 'my-other-model', object: 'model', owned_by: 'fixture' },
          ],
        }));
        return;
      }
      let body = '';
      req.on('data', (chunk) => { body += chunk; });
      req.on('end', () => {
        const parsed = safeJson(body);
        this.requests.push({ body: parsed, raw: body });
        this.respond(res, parsed);
      });
      req.on('error', () => {});
    });
  }

  async start() {
    await new Promise((resolve, reject) => {
      this.server.once('error', reject);
      this.server.listen(0, '127.0.0.1', resolve);
    });
    return this;
  }

  get baseUrl() {
    return `http://127.0.0.1:${this.server.address().port}`;
  }

  respond(res, body) {
    const messages = Array.isArray(body?.messages) ? body.messages : [];
    const hasToolResult = messages.some((m) => m?.role === 'tool');
    res.writeHead(200, {
      'content-type': 'text/event-stream; charset=utf-8',
      'cache-control': 'no-cache',
      connection: 'keep-alive',
    });

    const base = {
      id: 'chatcmpl-pi-tools',
      object: 'chat.completion.chunk',
      created: 1,
      model: MODEL,
    };

    if (this.mode === 'tool' && !hasToolResult) {
      res.write(`data: ${JSON.stringify({
        ...base,
        choices: [{
          index: 0,
          delta: {
            role: 'assistant',
            content: null,
            tool_calls: [{
              index: 0,
              id: 'call_pi_1',
              type: 'function',
              function: { name: 'game_get_overview', arguments: '{}' },
            }],
          },
          finish_reason: null,
        }],
      })}\n\n`);
      res.write(`data: ${JSON.stringify({
        ...base,
        choices: [{ index: 0, delta: {}, finish_reason: 'tool_calls' }],
      })}\n\n`);
      res.write('data: [DONE]\n\n');
      res.end();
      return;
    }

    res.write(`data: ${JSON.stringify({
      ...base,
      choices: [{ index: 0, delta: { role: 'assistant', content: '概览读取完成。' }, finish_reason: null }],
    })}\n\n`);
    res.write(`data: ${JSON.stringify({
      ...base,
      choices: [{ index: 0, delta: {}, finish_reason: 'stop' }],
    })}\n\n`);
    res.write('data: [DONE]\n\n');
    res.end();
  }

  async close() {
    try { this.server.closeAllConnections?.(); } catch {}
    await new Promise((resolve) => {
      const timer = setTimeout(resolve, 1000);
      timer.unref?.();
      try { this.server.close(() => { clearTimeout(timer); resolve(); }); } catch { clearTimeout(timer); resolve(); }
    });
  }
}

function safeJson(text) {
  try { return JSON.parse(text); } catch { return {}; }
}

let upstream;
let server;
let client;
let dataDir;
let finished = false;

function cleanup() {
  if (finished) return;
  finished = true;
  try { client?.close(); } catch {}
  try { server?.stop(); } catch {}
  void upstream?.close();
  if (dataDir) {
    try { rmSync(dataDir, { recursive: true, force: true }); } catch {}
  }
}

process.on('exit', cleanup);
process.on('SIGINT', () => { cleanup(); process.exit(130); });
process.on('SIGTERM', () => { cleanup(); process.exit(143); });

const watchdog = setTimeout(() => {
  console.error('❌ pi-tools 超过 60s 看门狗上限');
  cleanup();
  process.exit(1);
}, 60000);

async function main() {
  assertBuilt();
  upstream = await new ToolFixture().start();
  dataDir = mkdtempSync(join(tmpdir(), 'icarus-ii-pi-tools-'));
  mkdirSync(join(dataDir, 'agent'), { recursive: true });
  writeFileSync(
    join(dataDir, 'agent', 'settings.json'),
    `${JSON.stringify({ retry: { enabled: false, maxRetries: 0 } }, null, 2)}\n`,
    'utf8',
  );

  server = await startSidecar({ agent: 'pi', token: TOKEN, dataDir });

  // 自己发 hello：带 game.read.v1 能力。
  client = await connect(server.port, { token: TOKEN, hello: false });
  client.send({
    v: 1,
    id: 'm-1',
    type: 'hello',
    payload: {
      token: TOKEN,
      modVersion: 'test',
      gameVersion: 'test',
      gameLanguage: 'zh-CN',
      capabilities: ['game.read.v1'],
    },
  });
  const ready = await client.waitForType('ready');
  check('ready.tools 含 15 个工具（6 game + 4 kb + 2 wiki + 2 蓝图 + 脚本）', (ready.payload?.tools ?? []).length === 15);

  // 配置假上游。
  client.send({
    v: 1,
    id: 'c-1',
    type: 'config.set',
    payload: { provider: 'deepseek', model: MODEL, apiKey: FAKE_KEY, baseUrl: upstream.baseUrl },
  });
  const configured = await client.waitFor(
    (f) => f?.type === 'config.state' && f.replyTo === 'c-1',
    5000,
    'config.state',
  );
  check('配置进入 ready', configured.payload?.state === 'ready');

  // 先绑定存档 A，再提问；确保会话落在独立目录。
  client.send({
    v: 1,
    id: 's-1',
    type: 'save.bind',
    payload: { saveKey: 'saveA', saveName: '存档 A' },
  });
  const boundA = await client.waitFor(
    (f) => f?.type === 'save.bound' && f.replyTo === 's-1',
    5000,
    'save.bound(A)',
  );
  check('save.bind 返回 save.bound', boundA.payload?.saveKey === 'saveA');

  // 发问题，等 pi 发起 tool_call。
  client.send({ v: 1, id: 'm-2', type: 'prompt', payload: { text: '看看我现在在哪' } });
  // save.bind 会触发 sidecar 的 internal 知识库导出 tool_call；这里只等模型发起的 game 工具。
  const toolCall = await client.waitFor(
    (f) => f?.type === 'tool_call' && f.payload?.name === 'game_get_overview',
    15000,
    'game_get_overview tool_call',
  );
  check('pi 执行了自定义工具并发出 tool_call', toolCall?.payload?.name === 'game_get_overview');

  // 回填 tool_result。
  client.send({
    v: 1,
    id: 'm-3',
    type: 'tool_result',
    replyTo: toolCall.payload.callId,
    payload: {
      callId: toolCall.payload.callId,
      ok: true,
      data: { display: '测试星 · 1h00m', planet: { name: '测试星' } },
      error: null,
      meta: { gameTick: 42, saveKey: 's1' },
    },
  });

  const message = await client.waitFor(
    (f) => f?.type === 'message' && f.payload?.final === true,
    15000,
    'message(final)',
  );
  const text = (message.payload.content ?? [])
    .filter((b) => b.type === 'text')
    .map((b) => b.text)
    .join('');
  check('最终回答包含第二轮文本', text.includes('概览读取完成'), text);

  const firstBody = upstream.requests[0]?.body ?? {};
  const toolNames = (firstBody.tools ?? []).map((t) => t?.function?.name);
  check('上游请求里包含 game_get_overview', toolNames.includes('game_get_overview'), JSON.stringify(toolNames));
  check('上游请求里包含 game_get_production', toolNames.includes('game_get_production'));

  const systemMessage = (firstBody.messages ?? []).find((m) => m?.role === 'system');
  const systemText = typeof systemMessage?.content === 'string'
    ? systemMessage.content
    : (systemMessage?.content ?? []).map((b) => b?.text ?? '').join('');
  check('system prompt 未被 pi 的 “Available tools” 段落污染', !systemText.includes('Available tools:'));

  const secondBody = upstream.requests[1]?.body ?? {};
  const hasToolMessage = (secondBody.messages ?? []).some((m) => m?.role === 'tool');
  check('第二轮请求携带 tool 结果', hasToolMessage);
  check('上游总共收到两轮请求', upstream.requests.length >= 2, String(upstream.requests.length));

  // 换到存档 B：必须换会话，第二轮请求里不能带上存档 A 的回答。
  upstream.mode = 'text';
  client.send({
    v: 1,
    id: 's-2',
    type: 'save.bind',
    payload: { saveKey: 'saveB', saveName: '存档 B' },
  });
  const boundB = await client.waitFor(
    (f) => f?.type === 'save.bound' && f.replyTo === 's-2',
    5000,
    'save.bound(B)',
  );
  check('换档返回 save.bound', boundB.payload?.saveKey === 'saveB');

  const markB = client.frames.length;
  client.send({ v: 1, id: 'm-4', type: 'prompt', payload: { text: '这是新存档' } });
  await client.waitUntil(
    () => client.frames.slice(markB).some((f) => f?.type === 'message' && f.payload?.final === true),
    15000,
    'message(final, saveB)',
  );
  check('存档 B 的 prompt 得到回答', true);

  const bodyB = upstream.requests[2]?.body ?? {};
  const bodyBText = JSON.stringify(bodyB.messages ?? []);
  check('存档 B 的请求不携带存档 A 的历史', !bodyBText.includes('概览读取完成'));
  check('存档 A 的会话目录已创建', existsSync(join(dataDir, 'sessions', 'saveA')));
  check('存档 B 的会话目录已创建', existsSync(join(dataDir, 'sessions', 'saveB')));

  const filesA = existsSync(join(dataDir, 'sessions', 'saveA'))
    ? readdirSync(join(dataDir, 'sessions', 'saveA'))
    : [];
  const filesB = existsSync(join(dataDir, 'sessions', 'saveB'))
    ? readdirSync(join(dataDir, 'sessions', 'saveB'))
    : [];
  check('存档 A 有独立 session 文件', filesA.some((f) => f.endsWith('.jsonl')), filesA.join(','));
  check('存档 B 有独立 session 文件', filesB.some((f) => f.endsWith('.jsonl')), filesB.join(','));

  // 思考档位：max / off 必须真的改变上游请求里的 thinking 字段。
  // （DeepSeek 的 thinkingFormat 是 deepseek，pi 会写 thinking:{type:"enabled"|"disabled"}。）
  client.send({ v: 1, id: 'c-max', type: 'config.set', payload: { thinkingLevel: 'max' } });
  const thinkState = await client.waitFor(
    (f) => f?.type === 'config.state' && f.replyTo === 'c-max',
    5000,
    'config.state(thinking=max)',
  );
  check('thinkingLevel=max 回显', thinkState.payload?.thinkingLevel === 'max', thinkState.payload?.thinkingLevel);

  let mark = upstream.requests.length;
  client.send({ v: 1, id: 'm-max', type: 'prompt', payload: { text: '这次请思考一下' } });
  await client.waitUntil(() => upstream.requests.length > mark, 15000, 'upstream(thinking=max)');
  const maxBody = upstream.requests[mark]?.body ?? {};
  check(
    'thinkingLevel=max ⇒ 上游 thinking.type = enabled',
    maxBody.thinking?.type === 'enabled',
    JSON.stringify(maxBody.thinking),
  );

  client.send({ v: 1, id: 'c-off', type: 'config.set', payload: { thinkingLevel: 'off' } });
  await client.waitFor(
    (f) => f?.type === 'config.state' && f.replyTo === 'c-off',
    5000,
    'config.state(thinking=off)',
  );

  mark = upstream.requests.length;
  client.send({ v: 1, id: 'm-off', type: 'prompt', payload: { text: '这次不用思考' } });
  await client.waitUntil(() => upstream.requests.length > mark, 15000, 'upstream(thinking=off)');
  const offBody = upstream.requests[mark]?.body ?? {};
  check(
    'thinkingLevel=off ⇒ 上游 thinking.type = disabled',
    offBody.thinking?.type === 'disabled',
    JSON.stringify(offBody.thinking),
  );

  // 自定义供应商：未知 provider + 显式 baseUrl ⇒ 按 OpenAI 兼容端点现造模型。
  client.send({
    v: 1,
    id: 'c-custom',
    type: 'config.set',
    payload: {
      providerConfig: {
        'my-proxy': {
          apiKey: 'sk-proxy-test',
          baseUrl: upstream.baseUrl,
          models: [{ id: 'my-model', name: 'My Model' }],
        },
      },
      activeProvider: 'my-proxy',
      activeModel: 'my-model',
    },
  });
  await client.waitFor(
    (f) => f?.type === 'config.state' && f.replyTo === 'c-custom',
    5000,
    'config.state(custom)',
  );

  mark = upstream.requests.length;
  client.send({ v: 1, id: 'm-custom', type: 'prompt', payload: { text: '自定义模型' } });
  await client.waitUntil(
    () =>
      upstream.requests.length > mark ||
      client.frames.some((f) => f?.type === 'error' && f.replyTo === 'm-custom'),
    15000,
    'upstream(custom)',
  );
  const customError = client.frames.find((f) => f?.type === 'error' && f.replyTo === 'm-custom');
  if (customError) console.log('custom error:', JSON.stringify(customError));
  const customBody = upstream.requests[mark]?.body ?? {};
  check('自定义供应商请求使用配置的模型 id', customBody.model === 'my-model', customBody.model);

  // 模型目录：已知供应商走 pi 目录；显式 baseUrl 走 OpenAI 兼容 /models。
  client.send({ v: 1, id: 'cat-openai', type: 'model.catalog', payload: { provider: 'openai' } });
  const dsCatalog = await client.waitFor(
    (f) => f?.type === 'model.catalog' && f.replyTo === 'cat-openai',
    15000,
    'model.catalog(openai)',
  );
  const dsModels = Array.isArray(dsCatalog.payload?.models) ? dsCatalog.payload.models : [];
  check('OpenAI 目录非空', dsModels.length > 0, JSON.stringify(dsModels).slice(0, 200));
  check(
    'OpenAI 目录包含 gpt-4o',
    dsModels.some((m) => m?.id === 'gpt-4o'),
    dsModels.map((m) => m?.id).join(','),
  );

  client.send({
    v: 1,
    id: 'cat-custom',
    type: 'model.catalog',
    payload: {
      provider: 'my-proxy',
      baseUrl: `${upstream.baseUrl}/v1`,
      apiKey: 'sk-proxy-test',
    },
  });
  const customCatalog = await client.waitFor(
    (f) => f?.type === 'model.catalog' && f.replyTo === 'cat-custom',
    15000,
    'model.catalog(custom)',
  );
  const customModels = Array.isArray(customCatalog.payload?.models) ? customCatalog.payload.models : [];
  check('自定义端点目录来自 /models', customModels.length === 2, JSON.stringify(customModels));
  check('自定义端点目录包含 my-other-model', customModels.some((m) => m?.id === 'my-other-model'));
  check('自定义端点确实被请求', upstream.modelsRequests > 0, `${upstream.modelsRequests}`);

  // Key 检测：/models 200 ⇒ ok；401 ⇒ 明确失败；请求里不回传 key。
  client.send({
    v: 1,
    id: 'pt-good',
    type: 'provider.test',
    payload: { provider: 'my-proxy', baseUrl: `${upstream.baseUrl}/v1`, apiKey: 'sk-proxy-test' },
  });
  const goodTest = await client.waitFor(
    (f) => f?.type === 'provider.test' && f.replyTo === 'pt-good',
    15000,
    'provider.test(good)',
  );
  check('Key 检测成功', goodTest.payload?.ok === true, JSON.stringify(goodTest.payload));
  check('Key 检测 HTTP 200', goodTest.payload?.status === 200, goodTest.payload?.status);

  client.send({
    v: 1,
    id: 'pt-bad',
    type: 'provider.test',
    payload: { provider: 'my-proxy', baseUrl: `${upstream.baseUrl}/v1`, apiKey: 'sk-bad' },
  });
  const badTest = await client.waitFor(
    (f) => f?.type === 'provider.test' && f.replyTo === 'pt-bad',
    15000,
    'provider.test(bad)',
  );
  check('坏 Key 检测失败', badTest.payload?.ok === false, JSON.stringify(badTest.payload));
  check('坏 Key HTTP 401', badTest.payload?.status === 401, badTest.payload?.status);
  check(
    '失败信息不含 key 明文',
    !JSON.stringify(badTest.payload).includes('sk-bad'),
    JSON.stringify(badTest.payload),
  );

  // 已知供应商 + 已保存的 baseUrl/Key：不带 payload key 也应能测通。
  client.send({
    v: 1,
    id: 'pt-stored',
    type: 'provider.test',
    payload: { provider: 'deepseek' },
  });
  const storedTest = await client.waitFor(
    (f) => f?.type === 'provider.test' && f.replyTo === 'pt-stored',
    15000,
    'provider.test(stored)',
  );
  check('已保存的 Key/baseUrl 能检测', storedTest.payload?.ok === true, JSON.stringify(storedTest.payload));

  clearTimeout(watchdog);
  cleanup();
  return true;
}

main()
  .then((ok) => {
    if (!summary('pi-tools') || !ok) process.exit(1);
    process.exit(0);
  })
  .catch((err) => {
    console.error(err);
    cleanup();
    process.exit(1);
  });
