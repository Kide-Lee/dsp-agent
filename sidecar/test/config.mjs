#!/usr/bin/env node
/**
 * 配置存储单元测试（协议附录 A.4）。
 *
 *   node test/config.mjs
 *
 * 覆盖：往返读写、损坏 JSON 容错、权限 0600、脱敏函数、
 *       view() 绝不含 apiKey、runtime 状态的推导与清除。
 *
 * 不联网、不启动 sidecar。
 */

import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  CONFIG_FILE_NAME,
  ConfigStore,
  DEFAULT_MODEL,
  DEFAULT_PROVIDER,
  maskSecret,
  redactSecrets,
} from '../dist/config.js';
import { classifyUpstreamError } from '../dist/agent.js';
import { FAKE_KEY, check, summary } from './helpers.mjs';

const dir = mkdtempSync(join(tmpdir(), 'icarus-ii-cfg-'));
const configPath = join(dir, CONFIG_FILE_NAME);

function fresh() {
  return new ConfigStore({ path: configPath });
}

function writeRaw(text) {
  writeFileSync(configPath, text, 'utf8');
}

try {
  console.log('── config.ts ──');

  // 1) 未配置
  {
    const store = fresh();
    check('文件不存在 ⇒ state = missing_key', store.view().state === 'missing_key', store.view().state);
    check('未配置 ⇒ hasKey() = false', store.hasKey() === false);
    check(
      '默认 provider/model 来自 CLI 默认值',
      store.view().provider === DEFAULT_PROVIDER && store.view().model === DEFAULT_MODEL,
      `${store.view().provider}/${store.view().model}`,
    );
  }

  // 2) 往返读写 + 权限
  {
    const store = fresh();
    const view = store.update({ apiKey: FAKE_KEY });
    check('写入 key 后 state = ready', view.state === 'ready', view.state);
    check('config.json 已落盘', statSync(configPath).isFile());

    if (process.platform !== 'win32') {
      const mode = statSync(configPath).mode & 0o777;
      check('config.json 权限为 0600', mode === 0o600, `实际 0${mode.toString(8)}`);
    }

    const reloaded = fresh();
    check('重新加载后 key 仍在', reloaded.hasKey() === true);
    check('重新加载后 get().apiKey 一致', reloaded.get().apiKey === FAKE_KEY);
    check('重新加载后 state = ready', reloaded.view().state === 'ready', reloaded.view().state);
  }

  // 3) 增量更新：未给出的字段保持不变
  {
    const store = fresh();
    store.update({ provider: 'deepseek', model: 'deepseek-v4-pro', baseUrl: 'https://example.invalid' });
    check('provider 已更新', store.get().provider === 'deepseek');
    check('model 已更新', store.get().model === 'deepseek-v4-pro');
    check('baseUrl 已更新', store.get().baseUrl === 'https://example.invalid');
    check('apiKey 未被清掉', store.get().apiKey === FAKE_KEY);

    store.update({ baseUrl: null });
    check('baseUrl: null ⇒ 清除覆盖', store.get().baseUrl === null, String(store.get().baseUrl));
    check('baseUrl 清除后 model 保持', store.get().model === 'deepseek-v4-pro');

    store.update({ apiKey: '' });
    check('apiKey: "" ⇒ 视为清除', store.hasKey() === false);
    check('清除 key 后 state = missing_key', store.view().state === 'missing_key');
  }

  // 3b) 思考档位：默认 off、往返读写、非法值拒绝
  {
    const store = fresh();
    check('默认 thinkingLevel = high', store.view().thinkingLevel === 'high', store.view().thinkingLevel);
    check('thinkingLevel 写入视图', store.update({ thinkingLevel: 'high' }).thinkingLevel === 'high');
    check('thinkingLevel 落盘', JSON.parse(readFileSync(configPath, 'utf8')).thinkingLevel === 'high');
    check('重新加载后 thinkingLevel 仍在', fresh().view().thinkingLevel === 'high');
    check('thinkingLevel: off 可写', store.update({ thinkingLevel: 'off' }).thinkingLevel === 'off');
  }

  // 3c) 多供应商 / 多模型
  {
    const store = fresh();
    store.update({ provider: 'deepseek', apiKey: FAKE_KEY });
    const view = store.update({
      providerConfig: {
        openai: {
          apiKey: 'sk-openai-test',
          models: [
            { id: 'gpt-4o', name: 'GPT-4o' },
            { id: 'gpt-4o-mini', name: 'GPT-4o mini' },
          ],
        },
      },
      activeProvider: 'openai',
      activeModel: 'gpt-4o',
    });
    check('多供应商：openai 出现', view.providers.some((p) => p.id === 'openai'));
    check('多供应商：deepseek 仍在', view.providers.some((p) => p.id === 'deepseek'));
    check('多供应商：active 切换到 openai', view.provider === 'openai' && view.model === 'gpt-4o');
    check('多供应商：openai 的 hasKey = true', view.providers.find((p) => p.id === 'openai')?.hasKey === true);
    check(
      '多供应商：openai 模型列表',
      view.providers.find((p) => p.id === 'openai')?.models.map((m) => m.id).join(',') === 'gpt-4o,gpt-4o-mini',
    );
    check('多供应商：get() 展开 active 的 key', store.get().apiKey === 'sk-openai-test');
    check('多供应商：deepseek 的 key 不受影响', fresh().get().providers.deepseek?.apiKey === FAKE_KEY);

    const offView = store.update({ activeProvider: 'deepseek', activeModel: 'deepseek-v4-pro' });
    check('多供应商：切回 deepseek 用回旧 key', offView.provider === 'deepseek' && store.get().apiKey === FAKE_KEY);
    check('多供应商：切回后 model 正确', store.get().model === 'deepseek-v4-pro');

    const removed = store.update({ removeProviders: ['openai'] });
    check('删除供应商', !removed.providers.some((p) => p.id === 'openai'));

    const reread = fresh();
    check('多供应商落盘可重读', Object.keys(reread.get().providers).join(',') === 'deepseek');
  }

  // 3d) 旧格式自动迁移
  {
    writeRaw(JSON.stringify({ provider: 'openai', model: 'gpt-4o', apiKey: 'sk-legacy' }));
    const store = fresh();
    check('旧格式迁移出 openai', store.get().provider === 'openai' && store.get().model === 'gpt-4o');
    check('旧格式迁移保留 key', store.get().apiKey === 'sk-legacy');
    check('旧格式迁移后 view 有 providers', store.view().providers.length === 1);
    check('旧格式迁移后 hasKey', store.hasKey() === true);
  }

  // 3e) 自定义供应商（未知 id + baseUrl）
  {
    const store = fresh();
    const view = store.update({
      providerConfig: {
        'my-proxy': {
          apiKey: 'sk-proxy',
          baseUrl: 'https://proxy.example/v1',
          models: [{ id: 'my-model', name: 'My Model' }],
        },
      },
      activeProvider: 'my-proxy',
      activeModel: 'my-model',
    });
    check('自定义供应商出现在 view', view.providers.some((p) => p.id === 'my-proxy'));
    check('自定义供应商显示名回退到 id', view.providers.find((p) => p.id === 'my-proxy')?.name === 'my-proxy');
    check('自定义供应商 baseUrl 回显', view.providers.find((p) => p.id === 'my-proxy')?.baseUrl === 'https://proxy.example/v1');
    check('自定义供应商 hasKey', store.hasKey() === true);
  }

  // 4) 损坏 / 非法 JSON 容错
  {
    writeRaw('{ this is not json');
    let threw = false;
    let store;
    try {
      store = fresh();
    } catch {
      threw = true;
    }
    check('损坏 JSON 不抛异常', threw === false);
    check('损坏 JSON ⇒ missing_key', store?.view().state === 'missing_key', store?.view().state);
    check('损坏 JSON 后仍可写入', store?.update({ apiKey: FAKE_KEY }).state === 'ready');
  }
  {
    writeRaw('"just a string"');
    const store = fresh();
    check('JSON 是字符串 ⇒ 按未配置处理', store.view().state === 'missing_key');
  }
  {
    writeRaw('[1,2,3]');
    const store = fresh();
    check('JSON 是数组 ⇒ 按未配置处理', store.view().state === 'missing_key');
  }
  {
    writeRaw(JSON.stringify({ provider: 42, model: null, apiKey: {}, baseUrl: [] }));
    const store = fresh();
    check('字段类型非法 ⇒ 退化为默认值', store.get().provider === DEFAULT_PROVIDER);
    check('字段类型非法 ⇒ key 视为未配置', store.hasKey() === false);
  }
  {
    writeRaw(JSON.stringify({ apiKey: FAKE_KEY, unknownField: 'ignore me' }));
    const store = fresh();
    check('未知字段被忽略', store.hasKey() === true);
  }

  // 5) 脱敏
  {
    check('maskSecret 形态 = 前 4 位…(长度)', maskSecret('sk-abcdefghij') === 'sk-a…(13)', maskSecret('sk-abcdefghij'));
    check('maskSecret 对空值', maskSecret(null) === '(none)' && maskSecret('') === '(none)');
    check(
      'maskSecret 对真实长度',
      maskSecret(FAKE_KEY) === `${FAKE_KEY.slice(0, 4)}…(${FAKE_KEY.length})`,
      maskSecret(FAKE_KEY),
    );

    const text = `upstream said: Bearer ${FAKE_KEY} is wrong`;
    const redacted = redactSecrets(text, [FAKE_KEY]);
    check('redactSecrets 抹掉已配置的 key', !redacted.includes(FAKE_KEY), redacted);
    check('redactSecrets 保留可读上下文', redacted.includes('upstream said'), redacted);
    check(
      'redactSecrets 兜底打码 sk- 形态 token',
      !redactSecrets('token=sk-abcdefghijklmnopqrstuvwx', []).includes('sk-abcdefghijklmnopqrstuvwx'),
    );
  }

  // 6) 出站视图绝不含 apiKey
  {
    const store = fresh();
    store.update({ apiKey: FAKE_KEY });
    const serialized = JSON.stringify(store.view());
    check('view() 不含 apiKey 值', !serialized.includes(FAKE_KEY), serialized);
    check('view() 不含 apiKey 字段', !serialized.toLowerCase().includes('apikey'), serialized);
    check('view() 字段与附录 A.1 一致', ['state', 'provider', 'model', 'message'].every((k) => k in store.view()));
    check('view().message 在 ready 时为 null', store.view().message === null);
  }

  // 7) toJSON 防呆
  {
    const store = fresh();
    store.update({ apiKey: FAKE_KEY });
    const dumped = JSON.stringify(store);
    check('JSON.stringify(store) 不含明文 key', !dumped.includes(FAKE_KEY), dumped);
  }

  // 8) requireApiKey = false（echo 模式）
  {
    const echoPath = join(dir, 'echo-config.json');
    const store = new ConfigStore({ path: echoPath, requireApiKey: false });
    check('echo 模式：没有 key 也是 ready', store.view().state === 'ready', store.view().state);
    check('echo 模式：config.set 后仍是 ready', store.update({ apiKey: FAKE_KEY }).state === 'ready');
  }

  // 9) 上游推导出的状态
  {
    const store = fresh();
    store.update({ apiKey: FAKE_KEY });
    check('初始 ready', store.view().state === 'ready');
    check('invalid_key 状态可写', store.setRuntimeState('invalid_key', '401').state === 'invalid_key');
    check('invalid_key 携带脱敏后的 message', store.view().message === '401');
    check('update() 清除临时状态', store.update({ model: 'deepseek-v4-pro' }).state === 'ready');
    check('unreachable 状态可写', store.setRuntimeState('unreachable', 'fetch failed').state === 'unreachable');
    check('clearRuntimeState 回到 ready', store.clearRuntimeState().state === 'ready');
  }

  // 10) 落盘内容是 JSON 且含 key（这是唯一允许出现明文的地方）
  {
    const store = fresh();
    store.update({ apiKey: FAKE_KEY });
    const onDisk = JSON.parse(readFileSync(configPath, 'utf8'));
    check(
      '落盘文件里保存了 key（唯一允许的明文位置）',
      onDisk.providers?.deepseek?.apiKey === FAKE_KEY,
    );
    check(
      '落盘文件不含多余字段',
      Object.keys(onDisk).sort().join(',') === 'active,providers,thinkingLevel',
      Object.keys(onDisk).join(','),
    );
    check(
      '落盘文件含 active 指针',
      onDisk.active?.provider === 'deepseek' && typeof onDisk.active?.model === 'string',
    );
  }

  // 11) 上游错误分类（纯函数，离线；--agent echo 路径不加载 pi）
  {
    check('401 ⇒ invalid_key', classifyUpstreamError('401 {"error":"Authentication Fails"}').code === 'invalid_key');
    check(
      '401 应把配置状态改成 invalid_key',
      classifyUpstreamError('401 unauthorized').state === 'invalid_key',
    );
    check('403 ⇒ invalid_key', classifyUpstreamError('403 Forbidden: invalid api key').code === 'invalid_key');
    check('429 ⇒ rate_limited', classifyUpstreamError('429 {"error":"Rate limit reached"}').code === 'rate_limited');
    check(
      '429 不改变配置状态（可重试）',
      classifyUpstreamError('429 too many requests').state === null,
    );
    check(
      '限流文本无状态码也能识别',
      classifyUpstreamError('Rate limit exceeded, please retry').code === 'rate_limited',
    );
    check('402 ⇒ upstream_error', classifyUpstreamError('402 Insufficient Balance').code === 'upstream_error');
    check(
      '网络错误 ⇒ upstream_error + unreachable',
      classifyUpstreamError('fetch failed').code === 'upstream_error' &&
        classifyUpstreamError('fetch failed').state === 'unreachable',
    );
    check('DNS 失败也算 unreachable', classifyUpstreamError('getaddrinfo ENOTFOUND api.deepseek.com').state === 'unreachable');
    check('无关文本 ⇒ upstream_error', classifyUpstreamError('model overloaded the buffer').code === 'upstream_error');
    check(
      '长数字里的 401 不误判',
      classifyUpstreamError('request id 9401999 failed').code === 'upstream_error',
      classifyUpstreamError('request id 9401999 failed').code,
    );
  }
} finally {
  rmSync(dir, { recursive: true, force: true });
}

process.exit(summary('config 测试') ? 0 : 1);
