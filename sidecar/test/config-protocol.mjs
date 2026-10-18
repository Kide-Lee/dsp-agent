#!/usr/bin/env node
/**
 * 配置协议（附录 A）端到端测试 —— 不联网、不需要密钥。
 *
 *   node test/config-protocol.mjs
 *
 * 断言：
 *   1. 没有 key 时握手照样成功，`ready.payload.config.state === "missing_key"`
 *   2. 此时 `prompt` 回 `error{code:"config_required"}`，**不产生任何 stream/message**
 *   3. `config.set` 带上合法 key 后回 `config.state{state:"ready"}`
 *   4. **任何出站帧都不含 key 明文**，也没有任何名为 apiKey 的字段
 *   5. echo agent（--agent echo）的帧顺序与文本不变量与 M0 完全一致
 *
 * 用例 3 之后不再发 prompt：那会真的去连上游（本文件禁止联网）。
 */

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  FAKE_KEY,
  assertBuilt,
  check,
  connect,
  delay,
  findApiKeyField,
  startSidecar,
  statMode,
  summary,
} from './helpers.mjs';

assertBuilt();

const TOKEN = 'test-token-config';

/** 收到的所有帧（原始文本）里是否出现某个子串。 */
function rawContains(clients, needle) {
  return clients.some((c) => c.raws.some((raw) => raw.includes(needle)));
}

async function piWithoutKey() {
  console.log('── --agent pi（未配置）──');
  const server = await startSidecar({ agent: 'pi', token: TOKEN });
  const clients = [];
  try {
    const client = await connect(server.port, { token: TOKEN });
    clients.push(client);

    // 1) 握手成功 + ready.config
    const ready = client.frames.find((f) => f && f.type === 'ready');
    const config = ready?.payload?.config;
    check('未配置时握手仍然成功（收到 ready）', !!ready);
    check('ready.payload.config 存在', !!config);
    check('ready.config.state = missing_key', config?.state === 'missing_key', config?.state);
    check('ready.config.provider = deepseek', config?.provider === 'deepseek', config?.provider);
    check(
      'ready.config.model = deepseek-v4-flash',
      config?.model === 'deepseek-v4-flash',
      config?.model,
    );
    check(
      'ready.agent.model = deepseek-v4-flash（C# mod 读这个字段）',
      ready?.payload?.agent?.model === 'deepseek-v4-flash',
      ready?.payload?.agent?.model,
    );
    check('ready.agent.available = false（还没 key）', ready?.payload?.agent?.available === false);

    // 2) 未配置时 prompt ⇒ config_required
    client.send({ v: 1, id: 'm-2', type: 'prompt', payload: { text: '你好' } });
    const err = await client.waitForType('error');
    check('未配置时 prompt 回 error', !!err);
    check('error.code = config_required', err?.payload?.code === 'config_required', err?.payload?.code);
    check(
      'error.message 说明是哪个供应商',
      err?.payload?.message === '尚未配置 API Key（DeepSeek）',
      err?.payload?.message,
    );
    check('error.replyTo 指向 prompt', err?.replyTo === 'm-2', err?.replyTo);
    check('未配置时不发 stream', !client.frames.some((f) => f.type === 'stream'));
    check('未配置时不发 message', !client.frames.some((f) => f.type === 'message'));

    await delay(150);
    check('连接未因 config_required 断开', client.closedCode === null, String(client.closedCode));

    // 3) config.set ⇒ config.state
    client.send({
      v: 1,
      id: 'm-3',
      type: 'config.set',
      payload: { provider: 'deepseek', model: 'deepseek-v4-flash', apiKey: FAKE_KEY, baseUrl: null },
    });
    const state = await client.waitForType('config.state');
    check('config.set 回 config.state', !!state);
    check('config.state.state = ready', state?.payload?.state === 'ready', state?.payload?.state);
    check('config.state.replyTo 指向 config.set', state?.replyTo === 'm-3', state?.replyTo);
    check('config.state.provider 回显', state?.payload?.provider === 'deepseek');
    check('config.state.model 回显', state?.payload?.model === 'deepseek-v4-flash');
    check('config.state.message 为 null', state?.payload?.message === null);
    check('config.state 带 providers 列表', Array.isArray(state?.payload?.providers), typeof state?.payload?.providers);
    check(
      'config.state.providers 含 deepseek 且 hasKey',
      state?.payload?.providers?.find((p) => p.id === 'deepseek')?.hasKey === true,
    );
    check(
      'config.state.providers 的 deepseek 有两个模型',
      state?.payload?.providers?.find((p) => p.id === 'deepseek')?.models?.length === 2,
    );
    check(
      'config.state.active 指向 deepseek/flash',
      state?.payload?.active?.provider === 'deepseek' && state?.payload?.active?.model === 'deepseek-v4-flash',
    );

    // 4) 链路仍然活着
    client.send({ v: 1, id: 'm-4', type: 'ping', payload: { t: 7 } });
    const pong = await client.waitForType('pong');
    check('config.set 之后连接仍可用（pong）', pong?.payload?.t === 7);

    // 5) 非法 payload ⇒ bad_request（不崩、不断开）
    client.send({ v: 1, id: 'm-5', type: 'config.set', payload: { apiKey: 12345 } });
    const bad = await client.waitFor(
      (f) => f && f.type === 'error' && f.replyTo === 'm-5',
      3000,
      'bad_request',
    );
    check('config.set 非法字段 ⇒ bad_request', bad?.payload?.code === 'bad_request', bad?.payload?.code);
    await delay(100);
    check('bad_request 后连接仍开着', client.closedCode === null, String(client.closedCode));

    // 6) 只改 model
    client.send({ v: 1, id: 'm-6', type: 'config.set', payload: { model: 'deepseek-v4-pro' } });
    const state2 = await client.waitFor(
      (f) => f && f.type === 'config.state' && f.replyTo === 'm-6',
      3000,
      'config.state#2',
    );
    check('只改 model 也能升级状态', state2?.payload?.model === 'deepseek-v4-pro', state2?.payload?.model);
    check('只改 model 后 state = ready', state2?.payload?.state === 'ready');

    // 6b) 思考档位：合法值回显 + 非法值 bad_request
    client.send({ v: 1, id: 'm-6b', type: 'config.set', payload: { thinkingLevel: 'max' } });
    const state3 = await client.waitFor(
      (f) => f && f.type === 'config.state' && f.replyTo === 'm-6b',
      3000,
      'config.state#3',
    );
    check('thinkingLevel 回显', state3?.payload?.thinkingLevel === 'max', state3?.payload?.thinkingLevel);

    client.send({ v: 1, id: 'm-6c', type: 'config.set', payload: { thinkingLevel: 'turbo' } });
    const badLevel = await client.waitFor(
      (f) => f && f.type === 'error' && f.replyTo === 'm-6c',
      3000,
      'error#thinkingLevel',
    );
    check('thinkingLevel 非法 ⇒ bad_request', badLevel?.payload?.code === 'bad_request', badLevel?.payload?.code);

    // 6d) 多供应商：新增 openai、切 active、state 回显
    const OPENAI_KEY = 'sk-openai-fake-1234567890';
    client.send({
      v: 1,
      id: 'm-6d',
      type: 'config.set',
      payload: {
        providerConfig: {
          openai: {
            apiKey: OPENAI_KEY,
            models: [
              { id: 'gpt-4o', name: 'GPT-4o' },
              { id: 'gpt-4o-mini', name: 'GPT-4o mini' },
            ],
          },
        },
        activeProvider: 'openai',
        activeModel: 'gpt-4o',
      },
    });
    const multi = await client.waitFor(
      (f) => f && f.type === 'config.state' && f.replyTo === 'm-6d',
      3000,
      'config.state#multi',
    );
    check('多供应商：active 切到 openai', multi?.payload?.active?.provider === 'openai');
    check('多供应商：activeModel = gpt-4o', multi?.payload?.active?.model === 'gpt-4o');
    check(
      '多供应商：openai 出现在 providers',
      multi?.payload?.providers?.some((p) => p.id === 'openai' && p.hasKey === true),
    );
    check(
      '多供应商：deepseek 仍在',
      multi?.payload?.providers?.some((p) => p.id === 'deepseek'),
    );
    check('多供应商：state = ready', multi?.payload?.state === 'ready', multi?.payload?.state);
    check('多供应商：出站帧不含 openai 明文 key', !rawContains(clients, OPENAI_KEY));

    // 7) 脱敏：任何出站帧都不含 key
    check('出站帧不含 key 明文', !rawContains(clients, FAKE_KEY));
    check('出站帧不含 sk- 前缀的原始 token', !rawContains(clients, FAKE_KEY.slice(0, 20)));
    const leak = clients.flatMap((c) => c.frames.map((f) => findApiKeyField(f))).find(Boolean);
    check('出站帧不含任何 apiKey 字段', !leak, leak ?? undefined);

    // 8) 落盘权限
    const cfgPath = join(server.dataDir, 'config.json');
    check('config.json 已写成', readFileSync(cfgPath, 'utf8').includes(FAKE_KEY));
    if (process.platform !== 'win32') {
      check('config.json 权限 0600', statMode(cfgPath) === 0o600, `0${statMode(cfgPath).toString(8)}`);
    }

    client.close();
  } finally {
    for (const c of clients) c.close();
    server.stop();
  }
}

async function echoAgent() {
  console.log('── --agent echo（M0 冻结契约）──');
  const server = await startSidecar({ agent: 'echo', token: TOKEN });
  try {
    const client = await connect(server.port, { token: TOKEN });
    const ready = client.frames.find((f) => f && f.type === 'ready');
    check('echo 模式握手成功', !!ready);
    check('echo 的 agent.provider = echo', ready?.payload?.agent?.provider === 'echo');
    check('echo 的 agent.model = m0-echo', ready?.payload?.agent?.model === 'm0-echo');
    check('echo 的 config.state = ready（不需要 key）', ready?.payload?.config?.state === 'ready');

    client.send({ v: 1, id: 'm-2', type: 'prompt', payload: { text: 'hello' } });
    // 一个回合共两帧 status（thinking 开头、idle 收尾）。
    await client.waitForCount('status', 2);

    const order = client.frames.map((f) => f.type).filter((t) => t !== 'ready');
    check(
      '帧顺序 = status/stream/stream/message/status',
      order.join(',') === 'status,stream,stream,message,status',
      order.join(','),
    );
    check('第一帧是 status{thinking}', client.frames[1]?.payload?.state === 'thinking');
    check('末帧是 status{idle}', client.frames[client.frames.length - 1]?.payload?.state === 'idle');

    const streams = client.frames.filter((f) => f.type === 'stream');
    const joined = streams.map((f) => f.payload.delta).join('');
    const message = client.frames.find((f) => f.type === 'message');
    const text = message?.payload?.content?.[0]?.text;
    check('delta 拼接 = "echo: hello"', joined === 'echo: hello', joined);
    check('message 文本 = delta 拼接', text === joined, `${text} != ${joined}`);
    check('message.final = true', message?.payload?.final === true);
    check('stream 的 channel 均为 text', streams.every((f) => f.payload.channel === 'text'));
    check('stream/message 的 replyTo 指向 prompt', streams.every((f) => f.replyTo === 'm-2') && message?.replyTo === 'm-2');

    // 空 prompt：只发一个 delta（M0 不变量）
    const before = client.frames.length;
    client.send({ v: 1, id: 'm-3', type: 'prompt', payload: { text: '' } });
    await client.waitForCount('status', 4);
    const round2 = client.frames.slice(before);
    const emptyStreams = round2.filter((f) => f.type === 'stream');
    check('空 prompt 只发一个 delta', emptyStreams.length === 1, String(emptyStreams.length));
    check('空 prompt 的 delta = "echo: "', emptyStreams[0]?.payload?.delta === 'echo: ');
    check(
      '空 prompt 的 message 文本 = "echo: "',
      round2.find((f) => f.type === 'message')?.payload?.content?.[0]?.text === 'echo: ',
      round2.find((f) => f.type === 'message')?.payload?.content?.[0]?.text,
    );

    client.close();
  } finally {
    server.stop();
  }
}

async function main() {
  await piWithoutKey();
  await echoAgent();
  process.exit(summary('config 协议测试') ? 0 : 1);
}

main().catch((e) => {
  console.error('测试运行异常：', e);
  process.exit(1);
});
