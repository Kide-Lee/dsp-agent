#!/usr/bin/env node
/**
 * 蓝图卡片提取测试（离线，不联网、不启动 sidecar）。
 *
 * 覆盖：sidecar 本地工具 / mod 工具两种 result 结构、失败结果、非蓝图工具、
 * dir+fileName 拼路径、source 推断。协议见 docs/protocol-v1.md 附录 A.9。
 */

import { existsSync, mkdtempSync, rmSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  extractBlueprintCard,
  blueprintCardFileExists,
  dropDeletedBlueprintCards,
  attachBlueprintCards,
  compactBlueprintCard,
  BLUEPRINT_PRODUCING_TOOLS,
} from '../dist/blueprint/card.js';
import { buildBlueprintCardPayload } from '../dist/protocol.js';

let passed = 0;
let failed = 0;
const check = (name, cond, detail) => {
  if (cond) {
    passed++;
    console.log(`  ok   ${name}`);
  } else {
    failed++;
    console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`);
  }
};

const generated = {
  content: [{ type: 'text', text: '{"saved":true}' }],
  details: {
    saved: true,
    source: 'generated',
    path: '/home/me/Documents/Dyson Sphere Program/Blueprint/电磁涡轮 20.txt',
    fileName: '电磁涡轮 20.txt',
    item: '电磁涡轮',
    itemId: 1206,
    rate: 20,
    name: '电磁涡轮 20',
    author: '自动生成',
    version: '0.10.35.29151',
    description: '由规划器生成',
    iconItemId: 1206,
    buildings: 24,
    width: 12,
    height: 5,
  },
};

console.log('── 1. sidecar 本地工具 ──');
{
  const card = extractBlueprintCard('blueprint_generate', generated);
  check('成功结果产出卡片', card !== null, JSON.stringify(card));
  check('fileName', card.fileName === '电磁涡轮 20.txt', card.fileName);
  check('item/rate', card.item === '电磁涡轮' && card.rate === 20);
  check('buildings/size', card.buildings === 24 && card.width === 12 && card.height === 5);
  check('author/version', card.author === '自动生成' && card.version === '0.10.35.29151');
  check('source=generated', card.source === 'generated', card.source);

  const payload = buildBlueprintCardPayload(card);
  check('builder 保留必需字段', payload.fileName === card.fileName);
  check('builder 丢弃空字段', !('attributes' in payload));
}

console.log('\n── 2. mod 工具（ToolResultPayload） ──');
{
  const host = {
    content: [{ type: 'text', text: '已存蓝图' }],
    details: {
      ok: true,
      data: {
        saved: true,
        path: 'C:\\users\\steamuser\\Documents\\Dyson Sphere Program\\Blueprint\\产线.txt',
        fileName: '产线.txt',
        buildings: 8,
        iconItemId: 1101,
        shortDesc: '产线',
        desc: '复制的产线',
      },
      error: null,
      meta: {},
    },
  };
  const card = extractBlueprintCard('game_copy_blueprint', host);
  check('host 工具产出卡片', card !== null);
  check('source=copied', card.source === 'copied', card.source);
  check('从 shortDesc 取名称', card.name === '产线', card.name);
  check('从 desc 取介绍', card.description === '复制的产线', card.description);
  check('itemId 兼容 iconItemId', card.itemId === 1101, String(card.itemId));
}

console.log('\n── 3. 失败/无关结果 ──');
{
  check('失败标记不发卡', extractBlueprintCard('blueprint_generate', generated, true) === null);
  const failed = { details: { ok: false, data: { saved: false, error: 'x' } } };
  check('saved=false 不发卡', extractBlueprintCard('blueprint_generate', failed) === null);
  check('非蓝图工具不发卡', extractBlueprintCard('game_get_power', generated) === null);
  check('没有 fileName 不发卡',
    extractBlueprintCard('blueprint_generate', { details: { saved: true } }) === null);
  check('空结果不发卡', extractBlueprintCard('blueprint_generate', null) === null);
}

console.log('\n── 4. dir + fileName ──');
{
  const card = extractBlueprintCard('blueprint_save', {
    details: { saved: true, dir: '/tmp/blueprint', fileName: '下载.txt', license: 'CC BY-NC-SA 4.0' },
  });
  check('拼出 path', card.path === join('/tmp/blueprint', '下载.txt'), card.path);
  check('保留许可', card.license === 'CC BY-NC-SA 4.0');
  check('工具清单', BLUEPRINT_PRODUCING_TOOLS.length === 4, BLUEPRINT_PRODUCING_TOOLS.join(','));
}

console.log('\n── 5. 历史回放挂载 ──');
{
  const card = { fileName: 'a.txt', name: 'A', path: '/bp/a.txt' };
  const messages = [
    { role: 'user', text: '生成蓝图' },
    { role: 'assistant', text: '已生成 A。' },
    { role: 'user', text: '再生成一张' },
    { role: 'assistant', text: '已生成 B。' },
  ];
  const attached = attachBlueprintCards(messages, [
    { prompt: '生成蓝图', card },
    { prompt: '再生成一张', card: { fileName: 'b.txt', name: 'B' } },
  ]);
  check('第一轮卡片挂到第一轮回答', attached[1].cards?.[0]?.fileName === 'a.txt', JSON.stringify(attached[1]));
  check('第二轮卡片挂到第二轮回答', attached[3].cards?.[0]?.fileName === 'b.txt', JSON.stringify(attached[3]));
  check('不误挂到 user 消息', !attached[0].cards && !attached[2].cards);

  const truncated = attachBlueprintCards(
    [{ role: 'user', text: '很长的问题…' }, { role: 'assistant', text: '回答' }],
    [{ prompt: '很长的问题，后面还有很多很多字', card }],
  );
  check('截断的 user 文本也能匹配', truncated[1].cards?.[0]?.fileName === 'a.txt');

  const orphan = attachBlueprintCards(
    [{ role: 'user', text: '别的问题' }, { role: 'assistant', text: '回答' }],
    [{ prompt: '找不到的提问', card }],
  );
  check('找不到回合不回放', orphan[1].cards === undefined);

  const noAssistant = attachBlueprintCards(
    [{ role: 'user', text: '生成蓝图' }],
    [{ prompt: '生成蓝图', card }],
  );
  check('没有回答不消费', noAssistant[0].cards === undefined);

  const multi = attachBlueprintCards(
    [
      { role: 'user', text: '生成蓝图' },
      { role: 'assistant', text: '我先看看配方。' },
      { role: 'assistant', text: '已经生成好了。' },
      { role: 'user', text: '谢谢' },
      { role: 'assistant', text: '不客气' },
    ],
    [{ prompt: '生成蓝图', card }],
  );
  check('卡片挂到整轮最后一条回答', multi[2].cards?.[0]?.fileName === 'a.txt', JSON.stringify(multi));
  check('不挂到中间过程', multi[1].cards === undefined);
  check('不越过下一轮', multi[4].cards === undefined);
}

console.log('\n── 6. 历史副本截断 ──');
{
  const compact = compactBlueprintCard({
    fileName: 'x.txt',
    name: 'N'.repeat(500),
    description: 'D'.repeat(500),
    itemId: 1204,
    rate: 20,
  });
  check('名称被截断', compact.name.length <= 161, String(compact.name.length));
  check('介绍被截断', compact.description.length <= 281, String(compact.description.length));
  check('数字字段保留', compact.itemId === 1204 && compact.rate === 20);
}

console.log('\n── 7. 生成后被删除的蓝图不发卡 ──');
{
  const dir = mkdtempSync(join(tmpdir(), 'icarus-card-'));
  const path = join(dir, 'a.txt');
  writeFileSync(path, 'blueprint');
  check('文件存在时保留卡片', blueprintCardFileExists({ fileName: 'a.txt', path, source: 'generated' }));
  unlinkSync(path);
  check('文件被删后丢弃卡片', !blueprintCardFileExists({ fileName: 'a.txt', path, source: 'generated' }));
  check('copied 路径可能是游戏侧路径，保持卡片', blueprintCardFileExists({ fileName: 'a.txt', path, source: 'copied' }));
  check('没有 path 时不误删', blueprintCardFileExists({ fileName: 'a.txt', source: 'generated' }));
  rmSync(dir, { recursive: true, force: true });
}

console.log('\n── 8. 删除蓝图后撤回待发卡片 ──');
{
  const cards = [
    { fileName: 'a.txt', relPath: 'IcarusII/a.txt', path: '/bp/IcarusII/a.txt' },
    { fileName: 'b.txt', relPath: 'IcarusII/b.txt', path: '/bp/IcarusII/b.txt' },
  ];
  const rm = dropDeletedBlueprintCards(cards, { details: { removed: true, relPath: 'IcarusII/a.txt' } });
  check('rm 只撤回对应卡片', rm.length === 1 && rm[0]?.fileName === 'b.txt', JSON.stringify(rm));
  const rmdir = dropDeletedBlueprintCards(cards, {
    details: { removed: true, directory: true, relPath: 'IcarusII' },
  });
  check('rmdir 撤回目录下卡片', rmdir.length === 0, JSON.stringify(rmdir));
  const failed = dropDeletedBlueprintCards(cards, { details: { removed: false, relPath: 'IcarusII/a.txt' } });
  check('未删除时不动卡片', failed.length === 2);
  const noRel = dropDeletedBlueprintCards(cards, { details: { removed: true, path: '/bp/IcarusII/a.txt' } });
  check('按绝对 path 也能撤回', noRel.length === 1 && noRel[0]?.fileName === 'b.txt');
}

console.log('\n── 9. 构建产物存在 ──');
check('dist/blueprint/card.js 已构建', existsSync(new URL('../dist/blueprint/card.js', import.meta.url)));

console.log(`\n${failed === 0 ? '✅' : '❌'} blueprint-card 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
