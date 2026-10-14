#!/usr/bin/env node
/**
 * 真实上游（DeepSeek）联调测试 —— **默认跳过**。
 *
 *   ICARUS_II_LIVE_TEST=1 DEEPSEEK_API_KEY=sk-… node test/live.mjs
 *
 * 这是整个仓库里**唯一**允许访问外部网络的测试，因此**不在 `npm test` 里**
 * （`npm run test:live` 也需要显式设 `ICARUS_II_LIVE_TEST=1`）。
 *
 * 约束：
 *   - 密钥只从环境变量读取，通过 `config.set` 走 socket 送出（不进 argv、不进日志）
 *   - 临时数据目录里会留下含密钥的 `config.json`，退出前一定删掉
 *   - 任何断言失败都不打印密钥
 */

import {
  assertBuilt,
  check,
  connect,
  findApiKeyField,
  startSidecar,
  summary,
} from './helpers.mjs';

const ENABLED = process.env.ICARUS_II_LIVE_TEST === '1';
const API_KEY = process.env.DEEPSEEK_API_KEY ?? '';

if (!ENABLED || API_KEY.length === 0) {
  console.log('⏭  跳过 live 测试（需要 ICARUS_II_LIVE_TEST=1 且 DEEPSEEK_API_KEY）');
  process.exit(0);
}

assertBuilt();

const TOKEN = 'test-token-live';
const MODEL = process.env.ICARUS_II_LIVE_MODEL ?? 'deepseek-v4-flash';

let server;
try {
  console.log(`── live：deepseek/${MODEL} ──`);
  server = await startSidecar({ agent: 'pi', token: TOKEN });
  const client = await connect(server.port, { token: TOKEN });

  const ready = client.frames.find((f) => f && f.type === 'ready');
  check('未配置时 ready.config.state = missing_key', ready?.payload?.config?.state === 'missing_key');

  client.send({
    v: 1,
    id: 'm-2',
    type: 'config.set',
    payload: { provider: 'deepseek', model: MODEL, apiKey: API_KEY },
  });
  const configured = await client.waitFor(
    (f) => f && f.type === 'config.state' && f.replyTo === 'm-2',
    10000,
    'config.state',
  );
  check('config.set 后 state = ready', configured?.payload?.state === 'ready', configured?.payload?.state);

  client.send({ v: 1, id: 'm-3', type: 'prompt', payload: { text: '只回答两个字：你好' } });
  // 真实模型可能较慢：给 120 秒。
  await client.waitFor(
    () => client.frames.filter((f) => f && f.type === 'status').length >= 2,
    120000,
    'status{idle}',
  );

  const errors = client.frames.filter((f) => f && f.type === 'error');
  check('没有 error 帧', errors.length === 0, JSON.stringify(errors.map((e) => e.payload)).slice(0, 300));

  const message = client.frames.find((f) => f && f.type === 'message');
  const text = message?.payload?.content?.[0]?.text ?? '';
  check('收到 message 帧', !!message);
  check('message.final = true', message?.payload?.final === true);
  check('回复非空', text.trim().length > 0, `长度 ${text.length}`);
  check(
    '流式增量拼接非空',
    client.frames.filter((f) => f.type === 'stream').map((f) => f.payload.delta).join('').length > 0,
  );
  check('末帧 status{idle}', client.frames[client.frames.length - 1]?.payload?.state === 'idle');
  console.log(`   （模型回复长度 ${text.length} 字符）`);

  // 脱敏：任何出站帧、stderr 都不得出现密钥
  check('出站帧不含密钥', !client.raws.some((raw) => raw.includes(API_KEY)));
  const leak = client.frames.map((f) => findApiKeyField(f)).find(Boolean);
  check('出站帧不含 apiKey 字段', !leak, leak ?? undefined);
  check('stderr 不含密钥', !server.stderr().includes(API_KEY));

  client.close();
} catch (err) {
  check('live 测试执行完成', false, err instanceof Error ? err.message : String(err));
} finally {
  server?.stop(); // 会删掉临时数据目录（含 config.json 里的真实密钥）
}

process.exit(summary('live 测试') ? 0 : 1);
