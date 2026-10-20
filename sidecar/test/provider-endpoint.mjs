#!/usr/bin/env node
/**
 * 供应商端点探测回归（离线）：`model.catalog` 与 `provider.test` 必须用同一套
 * 地址清洗 + 鉴权风格 + 响应解析。
 *
 *   node test/provider-endpoint.mjs
 *
 * 覆盖的真实故障：
 *   1. Anthropic 风格端点（x-api-key）：以前「检测」通过、「获取可用模型」401；
 *   2. Google Gemini（x-goog-api-key）：同上，且模型 id 必须去掉 `models/` 前缀；
 *   3. 地址漏写 `/v1`：以前两条路径都 404；
 *   4. 地址粘贴成 `.../v1/chat/completions`：以前会拼出 `.../chat/completions/models`；
 *   5. 401/404 的错误文案要能指导玩家改地址/key。
 *
 * 全程只连 127.0.0.1 的临时端口，不联网、不用真实 key。
 */

import { createServer } from 'node:http';
import {
  buildProbeAttempts,
  canonicalBaseForChat,
  normalizeBaseUrl,
  parseModelList,
} from '../dist/provider-endpoint.js';
import {
  FAKE_KEY,
  assertBuilt,
  check,
  connect,
  findApiKeyField,
  startSidecar,
  summary,
} from './helpers.mjs';

assertBuilt();

/** 起一个假上游；`handler` 返回 `{status, body}` 或 null（404）。 */
function makeUpstream(handler) {
  const seen = [];
  const server = createServer((req, res) => {
    const url = new URL(req.url, 'http://local');
    seen.push({
      path: url.pathname,
      query: url.search,
      authorization: req.headers.authorization ?? null,
      xApiKey: req.headers['x-api-key'] ?? null,
      googleKey: req.headers['x-goog-api-key'] ?? null,
      userAgent: req.headers['user-agent'] ?? null,
    });
    const hit = handler(url, req.headers);
    if (!hit) {
      res.writeHead(404, { 'content-type': 'application/json' });
      res.end('{"error":{"message":"not found"}}');
      return;
    }
    res.writeHead(hit.status ?? 200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(hit.body));
  });
  return {
    seen,
    listen: () =>
      new Promise((resolve) => {
        server.listen(0, '127.0.0.1', () => resolve(`http://127.0.0.1:${server.address().port}`));
      }),
    close: () => server.close(),
  };
}

const bearer = (headers) => headers.authorization === `Bearer ${FAKE_KEY}`;

/** OpenAI 兼容中转：`/v1/models` 用 Bearer；`/models` 故意 404，验证补齐 `/v1`。 */
const openai = makeUpstream((url, headers) => {
  if (url.pathname !== '/v1/models' || !bearer(headers)) return null;
  return { body: { object: 'list', data: [{ id: 'relay-gpt-1' }, { id: 'relay-gpt-2' }] } };
});

/** Anthropic 风格：只认 x-api-key（Bearer 一律 401）。 */
const anthropic = makeUpstream((url, headers) => {
  if (url.pathname !== '/v1/models') return null;
  if (headers['x-api-key'] !== FAKE_KEY) return { status: 401, body: { error: 'bad key' } };
  return { body: { data: [{ id: 'claude-relay-1', display_name: 'Claude Relay 1' }] } };
});

/** Gemini 风格：只认 x-goog-api-key，返回 `models/xxx` + displayName。 */
const google = makeUpstream((url, headers) => {
  if (url.pathname !== '/v1beta/models') return null;
  if (headers['x-goog-api-key'] !== FAKE_KEY) return { status: 401, body: { error: 'bad key' } };
  return {
    body: {
      models: [
        { name: 'models/gemini-test-1', displayName: 'Gemini Test 1' },
        { name: 'models/gemini-test-2' },
      ],
    },
  };
});

/** 永远 404：验证错误文案。 */
const notFound = makeUpstream(() => null);

/** 永远 401：验证 key 被拒文案（与 provider 风格无关）。 */
const rejected = makeUpstream(() => ({ status: 401, body: { error: { message: 'invalid api key' } } }));

async function main() {
  const [openaiBase, anthropicBase, googleBase, missingBase, rejectedBase] = await Promise.all([
    openai.listen(),
    anthropic.listen(),
    google.listen(),
    notFound.listen(),
    rejected.listen(),
  ]);

  const sc = await startSidecar({ agent: 'pi', token: 't' });
  const client = await connect(sc.port, { token: 't' });
  let seq = 0;

  const ask = async (type, payload) => {
    const id = `${type}-${++seq}`;
    client.send({ v: 1, id, type, payload });
    return client.waitFor((frame) => frame?.type === type && frame.replyTo === id, 20000, id);
  };

  const catalog = async (provider, baseUrl) => {
    const reply = await ask('model.catalog', { provider, baseUrl, apiKey: FAKE_KEY });
    return {
      ids: (reply.payload?.models ?? []).map((model) => model.id),
      names: (reply.payload?.models ?? []).map((model) => model.name),
      error: reply.payload?.error ?? null,
      source: reply.payload?.source ?? null,
      resolvedBaseUrl: reply.payload?.resolvedBaseUrl ?? null,
    };
  };

  const test = async (provider, baseUrl) => {
    const reply = await ask('provider.test', { provider, baseUrl, apiKey: FAKE_KEY });
    return {
      ok: reply.payload?.ok === true,
      status: reply.payload?.status ?? 0,
      message: reply.payload?.message ?? '',
      resolvedBaseUrl: reply.payload?.resolvedBaseUrl ?? null,
    };
  };

  try {
    // ---- 1. OpenAI 兼容中转 ----
    const openaiCatalog = await catalog('custom', `${openaiBase}/v1`);
    check('OpenAI 中转：目录能取到', openaiCatalog.ids.length === 2, JSON.stringify(openaiCatalog));
    check('OpenAI 中转：source=openai-compatible', openaiCatalog.source === 'openai-compatible', openaiCatalog.source);
    const openaiTest = await test('custom', `${openaiBase}/v1`);
    check('OpenAI 中转：检测通过', openaiTest.ok, openaiTest.message);

    // ---- 2. Anthropic 风格（回归：以前目录 401）----
    const anthropicCatalog = await catalog('anthropic', `${anthropicBase}/v1`);
    check(
      'Anthropic 风格：目录不再用 Bearer（回归）',
      anthropicCatalog.ids.length === 1 && anthropicCatalog.error === null,
      JSON.stringify(anthropicCatalog),
    );
    check('Anthropic 风格：显示名用 display_name', anthropicCatalog.names[0] === 'Claude Relay 1', anthropicCatalog.names[0]);
    check(
      'Anthropic 风格：聊天地址去掉 /v1（SDK 会补）',
      anthropicCatalog.resolvedBaseUrl === anthropicBase,
      anthropicCatalog.resolvedBaseUrl,
    );
    const anthropicTest = await test('anthropic', `${anthropicBase}/v1`);
    check('Anthropic 风格：检测通过', anthropicTest.ok, anthropicTest.message);

    // ---- 3. Gemini 风格（回归：以前目录 401，且 id 带 models/）----
    const googleCatalog = await catalog('google', `${googleBase}/v1beta`);
    check(
      'Gemini 风格：目录能取到且去掉 models/ 前缀',
      googleCatalog.ids[0] === 'gemini-test-1',
      JSON.stringify(googleCatalog),
    );
    check('Gemini 风格：显示名用 displayName', googleCatalog.names[0] === 'Gemini Test 1', googleCatalog.names[0]);
    check(
      'Gemini 风格：聊天地址保留版本段',
      googleCatalog.resolvedBaseUrl === `${googleBase}/v1beta`,
      googleCatalog.resolvedBaseUrl,
    );
    const googleTest = await test('google', `${googleBase}/v1beta`);
    check('Gemini 风格：检测通过', googleTest.ok, googleTest.message);

    // ---- 4. 漏写 /v1：回退一次 ----
    const missingCatalog = await catalog('custom', openaiBase);
    check('漏写 /v1：回退后能取到目录', missingCatalog.ids.length === 2, JSON.stringify(missingCatalog));
    check(
      '漏写 /v1：返回补全后的聊天地址',
      missingCatalog.resolvedBaseUrl === `${openaiBase}/v1`,
      missingCatalog.resolvedBaseUrl,
    );
    const missingTest = await test('custom', openaiBase);
    check('漏写 /v1：检测也能通过', missingTest.ok, missingTest.message);

    // ---- 5. 粘贴成完整端点 ----
    const fullCatalog = await catalog('custom', `${openaiBase}/v1/chat/completions`);
    check('完整端点：清洗后能取到目录', fullCatalog.ids.length === 2, JSON.stringify(fullCatalog));

    // ---- 6. 404 / 401 文案 ----
    const notFoundCatalog = await catalog('custom', `${missingBase}/v1`);
    check(
      '404：提示 /v1 而不是空白错误',
      typeof notFoundCatalog.error === 'string' && notFoundCatalog.error.includes('404'),
      notFoundCatalog.error,
    );
    const notFoundTest = await test('custom', `${missingBase}/v1`);
    check('404：检测返回 false 且有提示', !notFoundTest.ok && notFoundTest.message.includes('404'), notFoundTest.message);

    const rejectedTest = await test('custom', `${rejectedBase}/v1`);
    check(
      '401：提示 API Key 被拒绝',
      !rejectedTest.ok && rejectedTest.status === 401 && rejectedTest.message.includes('API Key 被拒绝'),
      JSON.stringify(rejectedTest),
    );

    // ---- 7. 地址清洗与解析（纯函数）----
    check('清洗：漏写 scheme 补 https', normalizeBaseUrl('api.example.com/v1/') === 'https://api.example.com/v1', normalizeBaseUrl('api.example.com/v1/'));
    check(
      '清洗：去掉 /chat/completions',
      normalizeBaseUrl('https://api.example.com/v1/chat/completions') === 'https://api.example.com/v1',
      normalizeBaseUrl('https://api.example.com/v1/chat/completions'),
    );
    check(
      '清洗：保留 api-version',
      normalizeBaseUrl('https://x.openai.azure.com/openai?api-version=2024-10-21') ===
        'https://x.openai.azure.com/openai?api-version=2024-10-21',
      normalizeBaseUrl('https://x.openai.azure.com/openai?api-version=2024-10-21'),
    );
    const azureAttempts = buildProbeAttempts({
      provider: 'azure-openai',
      baseUrl: 'https://x.openai.azure.com?api-version=2024-10-21',
      apiKey: FAKE_KEY,
    });
    check(
      '聊天地址：Anthropic 去掉 /v1（SDK 自己补）',
      canonicalBaseForChat('anthropic', 'https://api.anthropic.com/v1') === 'https://api.anthropic.com',
      canonicalBaseForChat('anthropic', 'https://api.anthropic.com/v1'),
    );
    check(
      '聊天地址：Gemini 补 /v1beta',
      canonicalBaseForChat('google', 'https://generativelanguage.googleapis.com') ===
        'https://generativelanguage.googleapis.com/v1beta',
      canonicalBaseForChat('google', 'https://generativelanguage.googleapis.com'),
    );
    check(
      '聊天地址：自定义供应商（openai-completions）不套 Gemini 版本规则',
      canonicalBaseForChat('custom', 'https://generativelanguage.googleapis.com', 'openai-completions') ===
        'https://generativelanguage.googleapis.com',
      canonicalBaseForChat('custom', 'https://generativelanguage.googleapis.com', 'openai-completions'),
    );
    check(
      'Azure：/openai/models 插在 query 之前',
      azureAttempts[0]?.url === 'https://x.openai.azure.com/openai/models?api-version=2024-10-21',
      azureAttempts[0]?.url,
    );
    const azureDeployment = buildProbeAttempts({
      provider: 'azure-openai',
      baseUrl: 'https://x.openai.azure.com/openai/deployments/gpt4',
      apiKey: FAKE_KEY,
    });
    check(
      'Azure：去掉 deployments 段',
      azureDeployment[0]?.url === 'https://x.openai.azure.com/openai/models?api-version=2024-10-21',
      azureDeployment[0]?.url,
    );
    check(
      '解析：Gemini models[] 去前缀',
      parseModelList({ models: [{ name: 'models/gemini-x', displayName: 'Gemini X' }] })[0]?.id === 'gemini-x',
      JSON.stringify(parseModelList({ models: [{ name: 'models/gemini-x' }] })),
    );
    check(
      '解析：OpenAI data[] 用 id',
      parseModelList({ data: [{ id: 'gpt-x', name: 'GPT X' }] })[0]?.name === 'GPT X',
      JSON.stringify(parseModelList({ data: [{ id: 'gpt-x', name: 'GPT X' }] })),
    );

    // ---- 8. 出站帧不含 key ----
    const leaked = client.frames.map((frame) => findApiKeyField(frame)).find(Boolean);
    check('响应帧不回传 apiKey', !leaked, leaked ?? undefined);
    check(
      '请求带 user-agent（网关白名单更容易放行）',
      openai.seen.every((entry) => typeof entry.userAgent === 'string' && entry.userAgent.includes('icarus-ii')),
      JSON.stringify(openai.seen.slice(0, 2)),
    );
  } finally {
    client.close();
    sc.stop();
    for (const upstream of [openai, anthropic, google, notFound, rejected]) upstream.close();
  }
  return true;
}

main()
  .then((ok) => {
    if (!summary('provider-endpoint') || !ok) process.exit(1);
    process.exit(0);
  })
  .catch((err) => {
    console.error(err);
    process.exit(1);
  });
