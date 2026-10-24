#!/usr/bin/env node
/**
 * M4e 删除对话回合测试（离线）。
 *
 *   node test/history-delete.mjs
 *
 * 覆盖：按文本/时间戳定位、删除到下一条用户消息之前、parentId 重连、
 * 压缩/分支会话拒绝、坏 JSONL、找不到回合。
 */

import {
  parseSessionLines,
  removeCompactionFromEntries,
  removeTurnFromEntries,
  serializeSessionEntries,
} from '../dist/history.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

// ---------------------------------------------------------------- fixture

const T1 = Date.parse('2026-01-01T00:00:00.000Z');
const T2 = Date.parse('2026-01-01T00:01:00.000Z');

function fixture() {
  return [
    { type: 'session', id: 'sess', timestamp: '2026-01-01T00:00:00.000Z', cwd: '/tmp' },
    {
      type: 'message', id: 'u1', parentId: null, timestamp: '2026-01-01T00:00:00.000Z',
      message: { role: 'user', content: '第一个问题', timestamp: T1 },
    },
    {
      type: 'message', id: 'a1', parentId: 'u1', timestamp: '2026-01-01T00:00:01.000Z',
      message: { role: 'assistant', content: '第一个回答' },
    },
    {
      type: 'message', id: 't1', parentId: 'a1', timestamp: '2026-01-01T00:00:02.000Z',
      message: { role: 'toolResult', toolName: 'game_get_power', content: '{}' },
    },
    {
      type: 'message', id: 'u2', parentId: 't1', timestamp: '2026-01-01T00:01:00.000Z',
      message: { role: 'user', content: '第二个问题', timestamp: T2 },
    },
    {
      type: 'message', id: 'a2', parentId: 'u2', timestamp: '2026-01-01T00:01:01.000Z',
      message: { role: 'assistant', content: '第二个回答' },
    },
  ];
}

console.log('── 1. 删除第一个回合 ──');
{
  const { outcome, entries } = removeTurnFromEntries(fixture(), '第一个问题');
  check('ok', outcome.ok === true);
  check('removed = 3（user + assistant + tool）', outcome.removed === 3, String(outcome.removed));
  check('保留头', entries?.[0]?.type === 'session');
  check('剩下 3 条（头 + u2 + a2）', entries?.length === 3, String(entries?.length));
  const u2 = entries?.[1];
  const a2 = entries?.[2];
  check('u2 parentId 重新指向 null', u2?.parentId === null, JSON.stringify(u2?.parentId));
  check('a2 parentId 指向 u2', a2?.parentId === 'u2', JSON.stringify(a2?.parentId));
  check('内容没丢', u2?.message?.content === '第二个问题' && a2?.message?.content === '第二个回答');
}

console.log('\n── 2. 按时间戳定位 ──');
{
  // 两条同文本的用户消息：没有时间戳时删最后一条；有时间戳时删对应的那条。
  const entries = fixture();
  entries.push({
    type: 'message', id: 'u3', parentId: 'a2', timestamp: '2026-01-01T00:02:00.000Z',
    message: { role: 'user', content: '第一个问题', timestamp: T2 + 60000 },
  });
  const byText = removeTurnFromEntries(entries, '第一个问题');
  check('无时间戳删最后一条', byText.outcome.ok && byText.entries?.some((e) => e.id === 'u1'), JSON.stringify(byText.entries?.map((e) => e.id)));

  const byTs = removeTurnFromEntries(entries, '第一个问题', T1);
  check('有时间戳删对应那条', byTs.outcome.ok && !byTs.entries?.some((e) => e.id === 'u1') && byTs.entries?.some((e) => e.id === 'u3'), JSON.stringify(byTs.entries?.map((e) => e.id)));
  check('时间戳匹配 removed = 3', byTs.outcome.removed === 3, String(byTs.outcome.removed));
}

console.log('\n── 3. 找不到 / 不支持 ──');
{
  const missing = removeTurnFromEntries(fixture(), '不存在的问题');
  check('找不到回合', missing.outcome.ok === false && missing.outcome.error === 'turn_not_found');

  const empty = removeTurnFromEntries(fixture(), '   ');
  check('空文本拒绝', empty.outcome.error === 'turn_not_found');

  const noHeader = removeTurnFromEntries(fixture().slice(1), '第一个问题');
  check('缺会话头拒绝', noHeader.outcome.error === 'unsupported_session');

  const compacted = fixture();
  compacted.splice(2, 0, { type: 'compaction', id: 'c1', parentId: 'u1', summary: 'x' });
  const unsupported = removeTurnFromEntries(compacted, '第一个问题');
  check('压缩会话拒绝', unsupported.outcome.error === 'unsupported_session');

  const branched = fixture();
  branched.splice(2, 0, { type: 'branch_summary', id: 'b1', parentId: 'u1', summary: 'x' });
  check('分支会话拒绝', removeTurnFromEntries(branched, '第一个问题').outcome.error === 'unsupported_session');

  const edited = fixture();
  edited.splice(2, 0, {
    type: 'context_edit', id: 'e1', parentId: 'u1', targetId: 'a1', replacement: null,
  });
  check('context_edit 会话拒绝', removeTurnFromEntries(edited, '第一个问题').outcome.error === 'unsupported_session');
}

console.log('\n── 3.5 pi 1.4 新 entry 的兼容性 ──');
{
  // 1.4 的首个请求会持久化一条 system message；删回合时它必须保留并重新接链。
  const withSystem = fixture();
  withSystem.splice(1, 0, {
    type: 'message', id: 'sys1', parentId: null, timestamp: '2026-01-01T00:00:00.000Z',
    message: { role: 'system', content: '', sections: { preamble: 'persona' } },
  });
  const sys = removeTurnFromEntries(withSystem, '第一个问题');
  check('system message 会话可删除', sys.outcome.ok === true, sys.outcome.error);
  check('system message 保留', sys.entries?.some((e) => e.id === 'sys1') === true);
  check('system message 仍在最前', sys.entries?.[1]?.id === 'sys1', JSON.stringify(sys.entries?.map((e) => e.id)));
  check('u2 接到 system 后', sys.entries?.find((e) => e.id === 'u2')?.parentId === 'sys1');

  // usage 不参与模型上下文；删回合时保留它，不能因为不认识就拒绝整个会话。
  const withUsage = fixture();
  withUsage.push({
    type: 'usage', id: 'us1', parentId: 'a2', kind: 'cache_warm',
    provider: 'test', model: 'test', usage: { input: 0, output: 0, totalTokens: 0 },
  });
  const usage = removeTurnFromEntries(withUsage, '第一个问题');
  check('usage 会话可删除', usage.outcome.ok === true, usage.outcome.error);
  check('usage 条目保留', usage.entries?.some((e) => e.id === 'us1') === true);
}

console.log('\n── 3.6 撤销上下文压缩 ──');
{
  const compacted = fixture();
  compacted.splice(2, 0, {
    type: 'compaction', id: 'c1', parentId: 'a1', summary: '旧对话摘要',
    firstKeptEntryId: 'u1', tokensBefore: 1000,
  });
  // 压缩 entry 是追加在链中间的：它的下一个条目 parentId 指向它。
  compacted[3].parentId = 'c1';
  const undo = removeCompactionFromEntries(compacted);
  check('撤销最近一次压缩', undo.outcome.ok === true, undo.outcome.error);
  check('compactionId 正确', undo.outcome.compactionId === 'c1');
  check('compaction entry 已删除', undo.entries?.some((e) => e.id === 'c1') === false);
  check('子条目接回原 parent', undo.entries?.find((e) => e.id === 't1')?.parentId === 'a1');
  check('其他条目未丢', undo.entries?.length === 6, String(undo.entries?.length));

  const noCompaction = removeCompactionFromEntries(fixture());
  check('没有压缩时 not_found', noCompaction.outcome.error === 'not_found');

  const twice = fixture();
  twice.splice(2, 0, { type: 'compaction', id: 'c1', parentId: 'a1', summary: '一' });
  twice[3].parentId = 'c1';
  twice.splice(6, 0, { type: 'compaction', id: 'c2', parentId: 'u2', summary: '二' });
  twice[7].parentId = 'c2';
  const old = removeCompactionFromEntries(twice, 'c1');
  check('只能撤销最近一次', old.outcome.error === 'not_latest', old.outcome.error);
  const latest = removeCompactionFromEntries(twice, 'c2');
  check('指定最近一次可撤销', latest.outcome.ok === true, latest.outcome.error);
  check('撤销后仍保留较早压缩', latest.entries?.some((e) => e.id === 'c1') === true);

  const missing = removeCompactionFromEntries(twice, 'nope');
  check('找不到指定压缩', missing.outcome.error === 'not_found', missing.outcome.error);
}

console.log('\n── 4. JSONL 解析与序列化 ──');
{
  const raw = fixture().map((e) => JSON.stringify(e)).join('\n') + '\n';
  const parsed = parseSessionLines(raw);
  check('正常解析', Array.isArray(parsed) && parsed.length === 6);

  check('坏行返回 null', parseSessionLines('{"a":1}\nnot json\n') === null);

  const { entries } = removeTurnFromEntries(parsed ?? [], '第二个问题');
  const serialized = serializeSessionEntries(entries ?? []);
  const roundTrip = parseSessionLines(serialized);
  check('序列化后可再解析', Array.isArray(roundTrip) && roundTrip.length === 4, String(roundTrip?.length));
  check('末尾有换行', serialized.endsWith('\n'));
}

console.log(`\n${failed === 0 ? '✅' : '❌'} history-delete 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
