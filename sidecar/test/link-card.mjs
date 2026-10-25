#!/usr/bin/env node
/** 实体链接卡片提取/历史挂接测试（离线，协议附录 A.15）。 */
import {
  extractLinkCard,
  attachLinkCards,
  compactLinkCard,
} from '../dist/link/card.js';
import { buildLinkCardPayload } from '../dist/protocol.js';

let passed = 0;
let failed = 0;
const check = (name, cond, detail) => {
  if (cond) { passed++; console.log(`  ok   ${name}`); }
  else { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); }
};
const eq = (name, expected, actual) => check(name, expected === actual, `${JSON.stringify(actual)}`);

const building = {
  content: [{ type: 'text', text: '{}' }],
  details: {
    ok: true,
    data: {
      kind: 'building',
      planetId: 103,
      buildingId: 6956,
      name: '制造台Mk.I',
      subtitle: '南门二A',
      description: '生产速度: 30.0/min',
      iconLabel: '#6956',
    },
  },
};
const vein = {
  details: {
    ok: true,
    data: { kind: 'vein', planetId: 103, veinId: 415, name: '铁矿脉', subtitle: '（储量 1000，1.00°，2.00°）' },
  },
};

const planet = {
  details: {
    ok: true,
    data: { kind: 'planet', planetId: 103, name: '文昌六 - 3号星', subtitle: '文昌六星系' },
  },
};

const b = extractLinkCard('game_tether_add', building);
check('建筑卡提取', b !== null && b.kind === 'building' && b.buildingId === 6956);
eq('建筑名原样', '制造台Mk.I', b?.name);
const p = extractLinkCard('game_tether_add', planet);
check('星球卡提取', p !== null && p.kind === 'planet' && p.planetId === 103);
const v = extractLinkCard('game_tether_add', vein);
check('矿脉卡提取', v !== null && v.kind === 'vein' && v.veinId === 415);
check('矿脉卡缺 id 不产卡',
  extractLinkCard('game_tether_add', { details: { data: { kind: 'vein', planetId: 1, name: 'x' } } }) === null);
check('其他工具不产卡', extractLinkCard('game_get_planet', planet) === null);
check('失败结果不产卡', extractLinkCard('game_tether_add', building, true) === null);
check('缺 kind 不产卡', extractLinkCard('game_tether_add', { details: { data: { name: 'x' } } }) === null);
check('建筑缺 id 不产卡',
  extractLinkCard('game_tether_add', { details: { data: { kind: 'building', planetId: 1, name: 'x' } } }) === null);

const payload = buildLinkCardPayload(b);
eq('协议 kind', 'building', payload.kind);
eq('协议 name', '制造台Mk.I', payload.name);

const long = compactLinkCard({ ...b, description: 'x'.repeat(5000) });
check('长描述压缩', long.description.length < 1300);

const messages = [
  { role: 'user', text: '给 6956 做方位指示' },
  { role: 'assistant', text: '好的' },
  { role: 'user', text: '别的' },
  { role: 'assistant', text: '好' },
];
const attached = attachLinkCards(messages, [{ prompt: '给 6956 做方位指示', card: b }]);
check('按提问挂到最终回答', attached[1].linkCards?.length === 1 && attached[3].linkCards === undefined);

console.log(failed === 0 ? `\n✅ link-card 测试全部通过（${passed} 项）` : `\n❌ ${failed}/${passed + failed} 项失败`);
process.exit(failed === 0 ? 0 : 1);
