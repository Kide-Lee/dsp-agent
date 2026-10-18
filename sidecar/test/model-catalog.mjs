#!/usr/bin/env node
/**
 * `model.catalog` 协议测试（M3，echo agent，不联网）。
 *
 *   node test/model-catalog.mjs
 */

import { assertBuilt, check, connect, findApiKeyField, startSidecar, summary } from './helpers.mjs';

assertBuilt();

const TOKEN = 'test-token-model-catalog';

async function main() {
  const server = await startSidecar({ agent: 'echo', token: TOKEN });
  const client = await connect(server.port, { token: TOKEN });
  try {
    client.send({
      v: 1,
      id: 'cat-echo',
      type: 'model.catalog',
      payload: { provider: 'echo' },
    });
    const reply = await client.waitFor(
      (f) => f?.type === 'model.catalog' && f.replyTo === 'cat-echo',
      5000,
      'model.catalog(echo)',
    );
    const models = Array.isArray(reply.payload?.models) ? reply.payload.models : [];
    check('echo agent 返回 model.catalog', reply.payload?.provider === 'echo', reply.payload?.provider);
    check('echo agent 返回 m0-echo', models.some((m) => m?.id === 'm0-echo'), JSON.stringify(models));
    check('没有 error 字段', !('error' in (reply.payload ?? {})));

    client.send({
      v: 1,
      id: 'cat-bad',
      type: 'model.catalog',
      payload: {},
    });
    const bad = await client.waitFor(
      (f) => f?.type === 'error' && f.replyTo === 'cat-bad',
      5000,
      'error(bad model.catalog)',
    );
    check('缺 provider ⇒ bad_request', bad.payload?.code === 'bad_request', bad.payload?.code);

    client.send({
      v: 1,
      id: 'pt-echo',
      type: 'provider.test',
      payload: { provider: 'echo' },
    });
    const testReply = await client.waitFor(
      (f) => f?.type === 'provider.test' && f.replyTo === 'pt-echo',
      5000,
      'provider.test(echo)',
    );
    check('echo agent 返回 provider.test', testReply.payload?.ok === true, testReply.payload?.ok);
    check('provider.test status=200', testReply.payload?.status === 200, testReply.payload?.status);

    client.send({
      v: 1,
      id: 'pt-bad',
      type: 'provider.test',
      payload: {},
    });
    const badTest = await client.waitFor(
      (f) => f?.type === 'error' && f.replyTo === 'pt-bad',
      5000,
      'error(bad provider.test)',
    );
    check('provider.test 缺 provider ⇒ bad_request', badTest.payload?.code === 'bad_request', badTest.payload?.code);

    const leaked = client.frames.map((f) => findApiKeyField(f)).find(Boolean);
    check('model.catalog/provider.test 帧不含 apiKey 字段', !leaked, leaked ?? undefined);
  } finally {
    client.close();
    server.stop();
  }
  return true;
}

main()
  .then((ok) => {
    if (!summary('model-catalog') || !ok) process.exit(1);
    process.exit(0);
  })
  .catch((err) => {
    console.error(err);
    process.exit(1);
  });
