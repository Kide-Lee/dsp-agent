#!/usr/bin/env node
/**
 * M4l：工具列表 / 强制工具的协议与纯函数测试（离线）。
 *
 *   node test/tool-menu.mjs
 */

import { buildReadyPayload, parsePromptPayload } from '../dist/protocol.js';
import { extractSlashTool, forcedToolInstruction, hasToolCallSince } from '../dist/agent.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

console.log('── prompt.requiredTool ──');
{
  const parsed = parsePromptPayload({ text: '/game_get_power 电够吗', requiredTool: 'game_get_power' });
  check('requiredTool 保留', parsed?.requiredTool === 'game_get_power', parsed?.requiredTool);
  check('text 原样保留', parsed?.text === '/game_get_power 电够吗', parsed?.text);

  const noTool = parsePromptPayload({ text: '你好' });
  check('缺省 requiredTool 为 null', noTool?.requiredTool === null);

  const empty = parsePromptPayload({ text: '你好', requiredTool: '   ' });
  check('空白 requiredTool 视为 null', empty?.requiredTool === null);

  const bad = parsePromptPayload({ text: '你好', requiredTool: 42 });
  check('非 string requiredTool ⇒ bad_request', bad === null);
}

console.log('── 从 /命令 解析工具名 ──');
{
  const tools = new Set(['game_get_power', 'kb_search']);
  check('精确匹配', extractSlashTool('/game_get_power 电够吗', tools) === 'game_get_power');
  check('行首空白容忍', extractSlashTool('  /kb_search 量子芯片', tools) === 'kb_search');
  check('未知名不强制', extractSlashTool('/nope 你好', tools) === null);
  check('非行首 / 不解析', extractSlashTool('你好 /game_get_power', tools) === null);
  check('无空格分隔不解析', extractSlashTool('/game_get_powerX', tools) === null);
}

console.log('── toolCall 检测 ──');
{
  const messages = [
    { role: 'user', content: 'hi' },
    {
      role: 'assistant',
      content: [
        { type: 'text', text: '我先查一下' },
        { type: 'toolCall', name: 'game_get_power', arguments: {} },
      ],
    },
  ];
  check('从 assistant content 找到 toolCall', hasToolCallSince(messages, 0, 'game_get_power'));
  check('指定起点之前的不算', !hasToolCallSince(messages, 2, 'game_get_power'));
  check('别的工具不算', !hasToolCallSince(messages, 0, 'kb_search'));
}

console.log('── ready.payload.toolCatalog ──');
{
  const payload = buildReadyPayload({
    serverVersion: 'test',
    sessionId: 's1',
    agent: { provider: 'test', model: 'test', available: true },
    tools: ['game_get_power'],
    toolCatalog: [
      { name: 'game_get_power', label: '电力状态', description: '读取电网' },
    ],
  });
  check('toolCatalog 写入', Array.isArray(payload.toolCatalog) && payload.toolCatalog.length === 1);
  check('label 写入', payload.toolCatalog?.[0]?.label === '电力状态');

  const without = buildReadyPayload({
    serverVersion: 'test',
    sessionId: 's1',
    agent: { provider: 'test', model: 'test', available: true },
    tools: ['game_get_power'],
  });
  check('无目录时不写 toolCatalog', without.toolCatalog === undefined);
}

console.log('── 强制工具 system prompt ──');
{
  const text = forcedToolInstruction('game_get_power');
  check('包含工具名', text.includes('game_get_power'));
  check('包含必须调用', text.includes('必须调用') || text.includes('至少调用一次'));
}

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
