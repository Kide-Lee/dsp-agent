/**
 * 沙盒门禁（设计 §20.1 D5 / §20.2 第 8 项）。
 *
 * 沙盒是**每次载入存档**的游戏运行时状态（`GameMain.sandboxToolsEnabled`），
 * 不是 sidecar 的进程设置。mod 通过 `save.bind.sandbox` 上报，sidecar 据此：
 *  - 不把沙盒专属工具广告给模型；
 *  - 在 `GameToolHost.call` 里再拒绝一次（防止脚本/调试直调绕过）。
 *
 * 工具名必须和 mod 的 `Protocol.ToolSpawnItem` / `ToolTeleport` 保持一致。
 */
export const CAPABILITY_GAME_SANDBOX = 'game.sandbox.v1';

export const SANDBOX_ONLY_TOOL_NAMES = ['game_spawn_item', 'game_teleport'] as const;

const SANDBOX_ONLY = new Set<string>(SANDBOX_ONLY_TOOL_NAMES);

export function isSandboxOnlyTool(name: string): boolean {
  return SANDBOX_ONLY.has(name);
}
