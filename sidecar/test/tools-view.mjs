#!/usr/bin/env node
/**
 * M4a 视觉/建筑工具测试（离线）。
 *
 * 覆盖三件事：
 *   1. `tool_result.image` 的解析与拒绝（大小、形状、失败结果带图）；
 *   2. 新能力（entity/progress/view）只广告自己的工具，不串到 game.read.v1；
 *   3. `game_capture_view` 的工具结果真的变成 pi 的 `ImageContent` 内容块。
 *
 *   node test/tools-view.mjs
 */

import {
  parseToolResultPayload,
  MAX_TOOL_IMAGE_BYTES,
} from '../dist/protocol.js';
import { GameToolHost } from '../dist/tools/host.js';
import { createGameTools, toolNamesForCapabilities } from '../dist/tools/registry.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const BASE64_PIXEL = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

function imagePayload(data = BASE64_PIXEL, overrides = {}) {
  return {
    mimeType: 'image/jpeg',
    data,
    width: 1280,
    height: 720,
    ...overrides,
  };
}

function result(payloadOverrides = {}) {
  return {
    ok: true,
    data: { display: '已截图' },
    error: null,
    meta: { gameTick: 1, saveKey: 's' },
    ...payloadOverrides,
  };
}

// ---------------------------------------------------------------- 1. 协议解析

function testParseImage() {
  console.log('\n── 1. tool_result.image 解析 ──');

  const good = parseToolResultPayload(result({ image: imagePayload() }));
  check('合法图片被保留', good?.image?.mimeType === 'image/jpeg' && good.image.width === 1280);
  check('base64 原样保留', good?.image?.data === BASE64_PIXEL);

  const missing = parseToolResultPayload(result());
  check('没有 image 字段时没有附件', missing !== null && missing.image === undefined);

  const failedResult = parseToolResultPayload(
    result({ ok: false, error: { code: 'timeout', message: 'x' }, image: imagePayload() }),
  );
  check('ok=false 时丢弃附件', failedResult !== null && failedResult.image === undefined);

  const badMime = parseToolResultPayload(result({ image: imagePayload(BASE64_PIXEL, { mimeType: '' }) }));
  check('空 mimeType ⇒ 协议违例', badMime === null);

  const badSize = parseToolResultPayload(result({ image: imagePayload(BASE64_PIXEL, { width: 0 }) }));
  check('width=0 ⇒ 协议违例', badSize === null);

  const notBase64 = parseToolResultPayload(result({ image: imagePayload('not base64 !!') }));
  check('非 base64 ⇒ 协议违例', notBase64 === null);

  // 超过 2 MiB 的 base64（解码前按 4/3 估算）。
  const oversized = 'A'.repeat(Math.ceil((MAX_TOOL_IMAGE_BYTES * 4) / 3) + 16);
  const tooBig = parseToolResultPayload(result({ image: imagePayload(oversized) }));
  check('超过 2 MiB ⇒ 协议违例', tooBig === null);

  const notObject = parseToolResultPayload(result({ image: 'x' }));
  check('image 不是对象 ⇒ 协议违例', notObject === null);
}

// ---------------------------------------------------------------- 2. 能力 → 工具

function testToolGating() {
  console.log('\n── 2. 能力按组广告工具 ──');

  const none = toolNamesForCapabilities([]);
  check('没有能力时只有 kb/wiki/blueprint/脚本', none.every((n) => n.startsWith('kb_') || n.startsWith('wiki_') || n.startsWith('blueprint_') || n === 'run_script'));

  const read = toolNamesForCapabilities(['game.read.v1']);
  check('game.read.v1 ⇒ 6 个旧工具', read.filter((n) => n.startsWith('game_')).length === 6);
  check('game.read.v1 不含 entity 工具', !read.includes('game_get_entity'));

  const entity = toolNamesForCapabilities(['game.entity.v1']);
  check('game.entity.v1 ⇒ 两个建筑工具', entity.includes('game_get_entity') && entity.includes('game_get_entities'));
  check('game.entity.v1 不含截图', !entity.includes('game_capture_view'));

  const view = toolNamesForCapabilities(['game.view.v1']);
  check('game.view.v1 ⇒ 截图', view.includes('game_capture_view'));
  check('game.view.v1 不含 game.read 工具', !view.includes('game_get_power'));

  const drop = toolNamesForCapabilities(['game.drop.v1']);
  check('game.drop.v1 ⇒ 丢弃工具', drop.includes('game_drop_item'));
  const writeOnly = toolNamesForCapabilities(['game.write.v1']);
  check('game.write.v1 不含丢弃工具', !writeOnly.includes('game_drop_item'));

  const build = toolNamesForCapabilities(['game.build.v1']);
  check('game.build.v1 ⇒ 改配方工具', build.includes('game_set_recipe'));
  check('game.build.v1 ⇒ 批量改配方工具', build.includes('game_set_assembler_batch'));
  check('game.build.v1 ⇒ 取成品工具', build.includes('game_take_output'));
  check('game.build.v1 不含拆除工具', !build.includes('game_dismantle'));
  check('game.build.v1 ⇒ 投料工具', build.includes('game_feed_input'));
  check('game.write.v1 不含改配方工具', !writeOnly.includes('game_set_recipe'));

  const tether = toolNamesForCapabilities(['game.tether.v1']);
  check('game.tether.v1 ⇒ 添加方位', tether.includes('game_tether_add'));
  check('game.tether.v1 ⇒ 清除方位', tether.includes('game_tether_clear'));
  check('game.tether.v1 ⇒ 方位列表', tether.includes('game_tether_list'));
  check('game.write.v1 不含方位工具', !writeOnly.includes('game_tether_add'));

  const storage = toolNamesForCapabilities(['game.storage.v1']);
  check('game.storage.v1 ⇒ 存储过滤器', storage.includes('game_storage_set_filter'));
  check('game.storage.v1 ⇒ 存储上限', storage.includes('game_storage_set_limit'));
  check('game.storage.v1 ⇒ 移库', storage.includes('game_storage_move'));
  check('game.storage.v1 ⇒ 存入', storage.includes('game_storage_deposit'));
  check('game.storage.v1 ⇒ 取出', storage.includes('game_storage_withdraw'));
  check('game.write.v1 不含存储写工具', !writeOnly.includes('game_storage_set_filter'));

  const progress = toolNamesForCapabilities(['game.progress.v1']);
  check('game.progress.v1 ⇒ 进度', progress.includes('game_get_progress'));

  const analyze = toolNamesForCapabilities(['game.analyze.v1']);
  check('game.analyze.v1 ⇒ 区域分析', analyze.includes('game_analyze_area'));
  check('game.analyze.v1 不含截图', !analyze.includes('game_capture_view'));

  const all = toolNamesForCapabilities([
    'game.read.v1',
    'game.entity.v1',
    'game.progress.v1',
    'game.view.v1',
    'game.analyze.v1',
  ]);
  check('全能力 ⇒ 16 个 game 工具', all.filter((n) => n.startsWith('game_')).length === 16);
  check('全能力仍带 kb_*', all.some((n) => n.startsWith('kb_')));
  check('全能力仍带 run_script', all.includes('run_script'));
  check('全能力包含布局图', all.includes('game_render_layout'));
  check('全能力包含存为蓝图', all.includes('game_copy_blueprint'));
  check('全能力包含星球一览', all.includes('game_get_planets'));
  check('全能力包含星际物流', all.includes('game_get_logistics'));
  check('全能力包含产线拓扑', all.includes('game_trace_line'));
}

// ---------------------------------------------------------------- 3. 内容块

async function testContentBlocks() {
  console.log('\n── 3. 工具结果 → pi 内容块 ──');

  const host = new GameToolHost();
  let reply = result({ image: imagePayload() });

  host.setCapabilities(['game.view.v1']);
  host.bind({
    async callTool() {
      return reply;
    },
  });

  const tools = createGameTools(host);
  const capture = tools.find((t) => t.name === 'game_capture_view');
  check('截图工具被注册', capture !== undefined);
  if (!capture) return;

  // 单次只拍一个机位；多角度由模型分多次调用完成，schema 不再暴露批量参数。
  const schema = JSON.stringify(capture.parameters);
  check('截图 schema 含 orbit 机位', schema.includes('orbit'));
  check('截图 schema 含 distance/pitch', schema.includes('distance') && schema.includes('pitch'));
  check('截图 schema 不含 angles', !schema.includes('angles'));
  check('截图 schema 不含 count', !schema.includes('count'));

  const withImage = await capture.execute('call_1', {}, undefined);
  check('结果有两个内容块', withImage.content.length === 2);
  check('第一块是文本', withImage.content[0].type === 'text');
  check('第二块是图片', withImage.content[1].type === 'image');
  check('图片 mimeType 透传', withImage.content[1].mimeType === 'image/jpeg');
  check('图片数据透传', withImage.content[1].data === BASE64_PIXEL);

  reply = result();
  const textOnly = await capture.execute('call_2', {}, undefined);
  check('没有图片时只有文本块', textOnly.content.length === 1 && textOnly.content[0].type === 'text');

  // 失败结果即使带了 image 也不该产生图片块。
  reply = result({ ok: false, error: { code: 'timeout', message: '超时' }, image: imagePayload() });
  const failedCall = await capture.execute('call_3', {}, undefined);
  check('失败结果不产生图片块', failedCall.content.every((block) => block.type !== 'image'));
  check('失败结果文本包含错误码', failedCall.content[0].text.includes('timeout'));
}

// ---------------------------------------------------------------- 4. 模态兜底

async function testModelInputDefault() {
  console.log('\n── 4. 老配置的模态兜底 ──');

  const { defaultModelInput, DEFAULT_DEEPSEEK_MODELS } = await import('../dist/config.js');

  const flash = defaultModelInput('deepseek', 'deepseek-v4-flash');
  check('DeepSeek 默认带 image', Array.isArray(flash) && flash.includes('image'));
  check('DeepSeek 默认仍带 text', Array.isArray(flash) && flash.includes('text'));

  const unknown = defaultModelInput('deepseek', 'some-future-model');
  check('未知 DeepSeek 模型不瞎猜', unknown === null);

  const other = defaultModelInput('openai', 'gpt-x');
  check('非 DeepSeek 不套用 DeepSeek 默认', other === null);

  check(
    '预置模型都声明了 image',
    DEFAULT_DEEPSEEK_MODELS.every((m) => m.input?.includes('image')),
  );
}

// ---------------------------------------------------------------- main

async function main() {
  testParseImage();
  testToolGating();
  await testContentBlocks();
  await testModelInputDefault();

  console.log(`\n${failed === 0 ? '✅' : '❌'} tools-view 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
  process.exit(failed === 0 ? 0 : 1);
}

main().catch((err) => {
  console.error('测试异常：', err);
  process.exit(1);
});
