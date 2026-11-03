/**
 * 并行会话的游戏资源锁。
 *
 * 资源层级按玩家可见的粒度建模：
 *  - `building:<id>`：一台建筑（文件）；
 *  - `planet:<id>:factory:<id>`：一片用传送带/分拣器连起来的建筑群（目录）；
 *  - `planet:<id>`：一颗星球上的全部工厂（仓库）；
 *  - `global`：研究、面板、待办、撤销等玩家级操作。
 *
 * 锁只约束**不同会话**；同一会话内部的工具调用可以重入。等待超时返回
 * `resource_locked`，由模型决定换目标还是请玩家切换会话。
 */

export interface LockConflict {
  path: string;
  heldPath: string;
  conversationId: string;
}

export interface LockHandle {
  keys: string[];
}

export interface LockAcquireResult {
  ok: boolean;
  conflicts?: LockConflict[];
}

interface HeldLock {
  conversationId: string;
  count: number;
}

/** 按 `:` 分段比较；父路径（planet / factory）与所有后代冲突。 */
export function lockPathsConflict(a: string, b: string): boolean {
  if (a === b) return true;
  if (a === 'global' || b === 'global') return true;
  if (a.startsWith(`${b}:`) || b.startsWith(`${a}:`)) return true;

  // 建筑锁来自没有 planetId 的工具；无法判断它属于哪颗星球，因此让星球锁
  // 对所有建筑/工厂锁保守地冲突，避免“以为锁了星球，其实还能改建筑”。
  if (a.startsWith('planet:') && (b.startsWith('building:') || b.startsWith('factory:'))) return true;
  if (b.startsWith('planet:') && (a.startsWith('building:') || a.startsWith('factory:'))) return true;
  return false;
}

export function buildingLockKey(buildingId: number): string {
  return `building:${buildingId}`;
}

export function factoryLockKey(planetId: number, factoryId: number | string): string {
  return `planet:${planetId}:factory:${factoryId}`;
}

export function planetLockKey(planetId: number): string {
  return `planet:${planetId}`;
}

function numberArg(args: Record<string, unknown>, key: string): number | null {
  const value = args[key];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function buildingKeys(args: Record<string, unknown>): string[] {
  const ids: number[] = [];
  for (const key of ['buildingId', 'sourceId', 'targetId'] as const) {
    const id = numberArg(args, key);
    if (id !== null) ids.push(id);
  }
  const selections = args['selections'];
  if (Array.isArray(selections)) {
    for (const item of selections) {
      if (!item || typeof item !== 'object' || Array.isArray(item)) continue;
      const id = numberArg(item as Record<string, unknown>, 'buildingId');
      if (id !== null) ids.push(id);
    }
  }
  return [...new Set(ids)].sort((a, b) => a - b).map(buildingLockKey);
}

/** 这些工具会改变玩家级/全局状态，必须全局串行。 */
const GLOBAL_WRITE_TOOLS = new Set([
  'game_place_marker',
  'game_open_panel',
  'game_set_research',
  'game_drop_item',
  'game_tether_add',
  'game_tether_clear',
  'game_todo_add',
  'game_todo_remove',
  'game_todo_write',
  'game_undo_last_write',
  'game_spawn_item',
  'game_teleport',
  'ui_click',
  'mods_adapter_create',
  'mods_adapter_delete',
  'mods_config_set',
]);

/** 这些工具只改一台/几台建筑，按 building 粒度加锁。 */
const BUILDING_WRITE_TOOLS = new Set([
  'game_set_recipe',
  'game_set_assembler_batch',
  'game_storage_set_filter',
  'game_storage_set_limit',
  'game_storage_move',
  'game_storage_deposit',
  'game_storage_withdraw',
  'game_feed_input',
  'game_take_output',
]);

/**
 * 工具 → 锁 key。`risk` 只用于未知工具兜底：未知写工具一律全局锁，避免漏锁。
 * 没有锁 key 的写工具（如 blueprint_save / run_script）由各自实现或内层调用负责。
 */
export function lockKeysForTool(
  name: string,
  args: Record<string, unknown>,
  risk = 0,
): string[] {
  if (GLOBAL_WRITE_TOOLS.has(name)) return ['global'];

  if (BUILDING_WRITE_TOOLS.has(name)) {
    const keys = buildingKeys(args);
    if (keys.length > 0) return keys;
    return risk > 0 ? ['global'] : [];
  }

  // 预留工厂粒度：工具显式给出 factoryId 时按工厂锁，否则由 planet 兜底。
  const planetId = numberArg(args, 'planetId');
  const factoryId = args['factoryId'];
  if (planetId !== null && (typeof factoryId === 'number' || typeof factoryId === 'string')) {
    return [factoryLockKey(planetId, factoryId)];
  }

  // 存蓝图不改游戏状态，但要读一整片建筑；用星球锁避免复制期间别的会话改配方/库存，
  // 否则蓝图快照会混入半新半旧的状态。蓝图文件本身由蓝图工作区串行。
  if (name === 'game_copy_blueprint') {
    return planetId !== null ? [planetLockKey(planetId)] : [];
  }

  // 脚本内部的写工具会各自加锁；脚本本身不占全局锁。
  if (name === 'run_script') return [];

  if (risk > 0) return ['global'];
  return [];
}

/** 跨会话共享的锁管理器；同一会话重入，不同会话按层级冲突。 */
export class GameLockManager {
  private readonly held = new Map<string, HeldLock>();
  private readonly byCall = new Map<string, string[]>();

  snapshot(): Array<{ path: string; conversationId: string; count: number }> {
    return [...this.held.entries()].map(([path, entry]) => ({
      path,
      conversationId: entry.conversationId,
      count: entry.count,
    }));
  }

  /**
   * 尝试加锁；有冲突时最多等 `waitMs`。等待期间若 signal 中止，返回空 conflicts
   * 并让调用方按 cancelled 处理（调用方自己检查 signal）。
   */
  async acquire(
    conversationId: string,
    callId: string,
    keys: readonly string[],
    options: { signal?: AbortSignal; waitMs?: number } = {},
  ): Promise<LockAcquireResult> {
    const wanted = [...new Set(keys)].sort();
    if (wanted.length === 0) return { ok: true };

    const deadline = Date.now() + (options.waitMs ?? 3000);
    while (true) {
      const conflicts: LockConflict[] = [];
      for (const key of wanted) {
        for (const [heldPath, entry] of this.held.entries()) {
          if (entry.conversationId === conversationId) continue;
          if (lockPathsConflict(key, heldPath)) {
            conflicts.push({
              path: key,
              heldPath,
              conversationId: entry.conversationId,
            });
          }
        }
      }
      if (conflicts.length === 0) {
        for (const key of wanted) {
          const existing = this.held.get(key);
          if (existing && existing.conversationId === conversationId) {
            existing.count += 1;
          } else {
            this.held.set(key, { conversationId, count: 1 });
          }
        }
        this.byCall.set(callId, wanted);
        return { ok: true };
      }

      if (options.signal?.aborted) return { ok: false, conflicts };
      if (Date.now() >= deadline) return { ok: false, conflicts };
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }

  release(callId: string): void {
    const keys = this.byCall.get(callId);
    if (!keys) return;
    this.byCall.delete(callId);
    for (const key of keys) {
      const entry = this.held.get(key);
      if (!entry) continue;
      entry.count -= 1;
      if (entry.count <= 0) this.held.delete(key);
    }
  }

  releaseConversation(conversationId: string): void {
    for (const [key, entry] of [...this.held.entries()]) {
      if (entry.conversationId === conversationId) this.held.delete(key);
    }
    for (const [callId, keys] of [...this.byCall.entries()]) {
      if (keys.some((key) => !this.held.has(key))) this.byCall.delete(callId);
    }
  }

  releaseAll(): void {
    this.held.clear();
    this.byCall.clear();
  }
}
