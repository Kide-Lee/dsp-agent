#!/usr/bin/env node
/**
 * 历史脚注游戏时间回放测试：
 *   reply.time 记录按「用户提问原文」挂回整轮最后一条 assistant 消息。
 *
 *   node test/game-time.mjs
 */

import { attachGameTimes } from '../dist/history/game-time.js';
import { parseReplyTimePayload } from '../dist/protocol.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

console.log('── 回答游戏时间挂载 ──');
{
  const messages = [
    { role: 'user', text: '第一个问题' },
    { role: 'assistant', text: '第一个回答' },
    { role: 'user', text: '第二个问题' },
    { role: 'assistant', text: '第二个回答' },
  ];
  const attached = attachGameTimes(messages, [
    { prompt: '第一个问题', gameTime: '08:42' },
    { prompt: '第二个问题', gameTime: '09:10' },
  ]);
  check('第一轮时间', attached[1].gameTime === '08:42', JSON.stringify(attached[1]));
  check('第二轮时间', attached[3].gameTime === '09:10', JSON.stringify(attached[3]));
  check('user 消息也带时间（气泡 footer 用）', attached[0].gameTime === '08:42' && attached[2].gameTime === '09:10',
    JSON.stringify(attached));
}

{
  const attached = attachGameTimes(
    [{ role: 'user', text: '很长的问题…' }, { role: 'assistant', text: '回答' }],
    [{ prompt: '很长的问题，后面还有很多很多字', gameTime: '10:00' }],
  );
  check('截断的提问也能匹配', attached[1].gameTime === '10:00');
  check('截断的提问 user 也带上时间', attached[0].gameTime === '10:00');
}

{
  const attached = attachGameTimes(
    [
      { role: 'user', text: '用工具的问题' },
      { role: 'assistant', text: '我先看看。' },
      { role: 'assistant', text: '已经完成。' },
      { role: 'user', text: '谢谢' },
      { role: 'assistant', text: '不客气' },
    ],
    [{ prompt: '用工具的问题', gameTime: '11:11' }],
  );
  check('挂到整轮最后一条回答', attached[2].gameTime === '11:11');
  check('不挂到中间过程', attached[1].gameTime === undefined);
  check('不越过下一轮', attached[4].gameTime === undefined);
  check('提问回退时 user 也带时间', attached[0].gameTime === '11:11');
}

{
  const attached = attachGameTimes(
    [
      { role: 'user', text: '继续' },
      { role: 'assistant', text: '先检查传送带。' },
      { role: 'user', text: '继续' },
      { role: 'assistant', text: '再检查分拣器。' },
    ],
    [
      { prompt: '继续', reply: '先检查传送带。', gameTime: '12:00' },
      { prompt: '继续', reply: '再检查分拣器。', gameTime: '12:05' },
    ],
  );
  check('重复提问按回答匹配第一轮', attached[1].gameTime === '12:00', JSON.stringify(attached[1]));
  check('重复提问按回答匹配第二轮', attached[3].gameTime === '12:05', JSON.stringify(attached[3]));
  check('重复提问 user 也各归各的时间', attached[0].gameTime === '12:00' && attached[2].gameTime === '12:05',
    JSON.stringify(attached));
}

{
  const attached = attachGameTimes(
    [{ role: 'user', text: '找不到的提问' }, { role: 'assistant', text: '回答' }],
    [{ prompt: '别的问题', gameTime: '12:00' }],
  );
  check('找不到回合不挂载', attached[1].gameTime === undefined);
}

{
  const attached = attachGameTimes(
    [{ role: 'user', text: '只有提问' }],
    [{ prompt: '只有提问', gameTime: '13:00' }],
  );
  check('没有回答不消费', attached[0].gameTime === undefined);
}

{
  const attached = attachGameTimes(
    [{ role: 'user', text: '重试过的问题' }, { role: 'assistant', text: '回答' }],
    [
      { prompt: '重试过的问题', gameTime: '14:00' },
      { prompt: '重试过的问题', gameTime: '14:05' },
    ],
  );
  check('同一提问以最后一条记录为准', attached[1].gameTime === '14:05');
  check('重试时 user 也用最后一条记录', attached[0].gameTime === '14:05');
}

{
  const messages = [{ role: 'user', text: '问题' }, { role: 'assistant', text: '回答' }];
  const attached = attachGameTimes(messages, []);
  check('空记录不改消息', attached[1].gameTime === undefined);
}

{
  // 同一句提问出现在两个会话：新记录按 conversationId 只挂自己的会话。
  const makeMessages = () => [
    { role: 'user', text: '同一句提问' },
    { role: 'assistant', text: 'A 的回答' },
    { role: 'user', text: '同一句提问' },
    { role: 'assistant', text: 'B 的回答' },
  ];
  const records = [
    { conversationId: 'conv-a', prompt: '同一句提问', reply: 'A 的回答', gameTime: '08:00' },
    { conversationId: 'conv-b', prompt: '同一句提问', reply: 'B 的回答', gameTime: '09:00' },
  ];
  const a = attachGameTimes(makeMessages(), records, 'conv-a');
  check('按会话只挂 A', a[1].gameTime === '08:00' && a[3].gameTime === undefined, JSON.stringify(a));
  const b = attachGameTimes(makeMessages(), records, 'conv-b');
  check('按会话只挂 B', b[1].gameTime === undefined && b[3].gameTime === '09:00', JSON.stringify(b));

  const legacy = attachGameTimes(makeMessages(), [
    { prompt: '同一句提问', reply: 'A 的回答', gameTime: '07:00' },
    { prompt: '同一句提问', reply: 'B 的回答', gameTime: '07:30' },
  ], 'conv-a');
  check('老记录无会话归属时仍可按回答回退匹配', legacy[1].gameTime === '07:00' && legacy[3].gameTime === '07:30',
    JSON.stringify(legacy));
}

console.log('── reply.time 载荷 ──');
{
  const parsed = parseReplyTimePayload({
    prompt: '问题', reply: '回答', gameTime: '08:42:00', conversationId: 'conv-a',
  });
  check('解析 prompt/reply/gameTime/会话',
    parsed?.prompt === '问题' && parsed.reply === '回答' && parsed.gameTime === '08:42:00' &&
      parsed.conversationId === 'conv-a', JSON.stringify(parsed));
  check('缺 prompt 拒绝', parseReplyTimePayload({ gameTime: '08:42:00' }) === null);
  check('缺 gameTime 拒绝', parseReplyTimePayload({ prompt: '问题' }) === null);

  const withTick = parseReplyTimePayload({ prompt: '问题', gameTime: '08:42:00', gameTick: 42123456 });
  check('接受合法 gameTick', withTick?.gameTick === 42123456, JSON.stringify(withTick));
  for (const [name, value] of [['负数', -1], ['小数', 1.5], ['字符串', '42'], ['NaN', Number.NaN]]) {
    const bad = parseReplyTimePayload({ prompt: '问题', gameTime: '08:42:00', gameTick: value });
    check(`非法 gameTick（${name}）不影响其它字段`,
      bad !== null && bad.gameTick === undefined && bad.gameTime === '08:42:00', JSON.stringify(bad));
  }
}

console.log(`\n${failed === 0 ? '✅ 全部通过' : '❌ 失败'}（${passed} 项断言${failed > 0 ? `，${failed} 失败` : ''}）\n`);
if (failed > 0) process.exit(1);
