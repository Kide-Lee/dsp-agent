#!/usr/bin/env node
/**
 * 并行会话的游戏资源锁：层级冲突、工具映射、等待/释放/重入。
 *
 *   node test/locks.mjs
 */

import {
  GameLockManager,
  buildingLockKey,
  factoryLockKey,
  lockKeysForTool,
  lockPathsConflict,
  planetLockKey,
} from '../dist/tools/locks.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

console.log('── 1. 层级冲突 ──');
check('同建筑冲突', lockPathsConflict(buildingLockKey(7), buildingLockKey(7)));
check('不同建筑不冲突', !lockPathsConflict(buildingLockKey(7), buildingLockKey(8)));
check('星球锁覆盖建筑', lockPathsConflict(planetLockKey(1), buildingLockKey(7)));
check('建筑锁不能越过星球锁', lockPathsConflict(buildingLockKey(7), planetLockKey(1)));
check('星球锁覆盖工厂', lockPathsConflict(planetLockKey(1), factoryLockKey(1, 2)));
check('不同工厂不冲突', !lockPathsConflict(factoryLockKey(1, 2), factoryLockKey(1, 3)));
check('global 覆盖一切', lockPathsConflict('global', buildingLockKey(7)));
check('7 与 70 不误撞', !lockPathsConflict(buildingLockKey(7), buildingLockKey(70)));

console.log('\n── 2. 工具 → 锁 key ──');
check('改配方锁建筑', JSON.stringify(lockKeysForTool('game_set_recipe', { buildingId: 7 })) === '["building:7"]');
check('批量锁多台', JSON.stringify(lockKeysForTool('game_set_assembler_batch', {
  selections: [{ buildingId: 7 }, { buildingId: 8 }, { buildingId: 7 }],
})) === '["building:7","building:8"]');
check('移库锁源和目标', JSON.stringify(lockKeysForTool('game_storage_move', {
  sourceId: 7, targetId: 8,
})) === '["building:7","building:8"]');
check('面板全局锁', JSON.stringify(lockKeysForTool('game_open_panel', {})) === '["global"]');
check('研究全局锁', JSON.stringify(lockKeysForTool('game_set_research', { techId: 1 })) === '["global"]');
check('脚本自己不加锁', JSON.stringify(lockKeysForTool('run_script', { code: 'x' })) === '[]');
check('读工具不加锁', JSON.stringify(lockKeysForTool('game_get_overview', {})) === '[]');
check('存蓝图锁星球快照', JSON.stringify(lockKeysForTool('game_copy_blueprint', {
  planetId: 1, entityIds: [7, 8],
})) === '["planet:1"]');
check('存蓝图未传星球时保守全局锁', JSON.stringify(lockKeysForTool('game_copy_blueprint', {
  entityIds: [7, 8],
})) === '["global"]');
check('未知写工具兜底全局', JSON.stringify(lockKeysForTool('mystery_write', {}, 2)) === '["global"]');

console.log('\n── 3. 锁管理器 ──');
{
  const locks = new GameLockManager();
  const a = await locks.acquire('conv-a', 'call-1', [buildingLockKey(7)]);
  check('会话 A 拿到建筑 7', a.ok === true);

  const b = await locks.acquire('conv-b', 'call-2', [buildingLockKey(7)], { waitMs: 80 });
  check('会话 B 等不到同建筑', b.ok === false && b.conflicts?.[0]?.conversationId === 'conv-a', JSON.stringify(b));

  const other = await locks.acquire('conv-b', 'call-3', [buildingLockKey(8)]);
  check('会话 B 可拿不同建筑', other.ok === true);
  locks.release('call-3');

  const planet = await locks.acquire('conv-b', 'call-4', [planetLockKey(1)], { waitMs: 80 });
  check('星球锁被建筑锁挡住', planet.ok === false);

  locks.release('call-1');
  const again = await locks.acquire('conv-b', 'call-5', [buildingLockKey(7)]);
  check('释放后可重新拿到', again.ok === true);
  locks.release('call-5');

  const self1 = await locks.acquire('conv-a', 'call-6', ['global']);
  const self2 = await locks.acquire('conv-a', 'call-7', ['global']);
  check('同会话重入', self1.ok && self2.ok);
  locks.release('call-6');
  const self3 = await locks.acquire('conv-b', 'call-8', ['global'], { waitMs: 50 });
  check('同会话还有一层时不释放给别的会话', self3.ok === false);
  locks.release('call-7');
  const after = await locks.acquire('conv-b', 'call-9', ['global']);
  check('两层都释放后可用', after.ok === true);
  locks.release('call-9');
}

console.log(`\n${failed === 0 ? '✅' : '❌'} locks 测试${failed === 0 ? '全部通过' : '失败'}（${passed} 项，失败 ${failed}）`);
process.exit(failed === 0 ? 0 : 1);
