/**
 * 游戏内助手人格（system prompt）。
 *
 * 背景：pi 本质是 **coding agent**，默认 system prompt 是
 * 「You are an expert coding assistant operating inside pi, a coding agent harness…」
 * 并在末尾追加 `Current working directory: <cwd>`。实机 M1 第一次跑通时，真实
 * DeepSeek 就按这个身份回答：
 *
 *   「我看到当前工作目录是 C:/users/steamuser/AppData/Roaming/icarus-ii。需要我做什么？」
 *
 * 因此 `agent.ts` 会把 pi 的默认 prompt **整个替换**（不是追加）成游戏内助手人格，
 * 并用 `before_agent_start` 扩展钩子把 pi 无条件追加的 cwd 尾巴也去掉。
 *
 * 文本源：`sidecar/prompts/system.zh.md`（唯一手写源）。
 * `scripts/gen-prompts.mjs` 在 build/typecheck 时把它编译成
 * `src/prompts.generated.ts`（纯字符串常量），这里再导出，保持 import 路径稳定。
 *
 * 为什么只有一份提示词：mod 已统一支持 `ui.richtext.v2`，不再为老 mod 保留
 * “只用游戏标签”的 legacy 版本；也没有 `{{FORMAT}}` / `{{NAME}}` 占位符。
 *
 * 为什么不运行时读 markdown：sidecar 用 esbuild 打成单文件、再塞进 Node SEA，
 * 运行期资源要额外分发；编译成常量则跟二进制一起走，行为可复现，测试也能做快照。
 *
 * 改动约束（改之前先读一遍）：
 *   - 只讲游戏；绝不提工作目录/文件/命令/代码库/编辑文件/执行 shell。
 *   - 不要在这里手写具体工具清单——工具段落由 agent.ts 按本轮注册表生成
 *     （`prompt/sections.ts`），否则加工具就会腐烂。
 *   - 能力/权限/沙盒等运行时事实由 agent.ts 每轮追加，不写进人格。
 *   - 玩家用哪种语言提问就用哪种语言回答。
 *   - 简洁，不要长篇报告。
 *   - 文本里不能出现 pi 默认 prompt 的特征串（见 test/prompts.mjs、test/pi-loopback.mjs 的禁词断言）。
 */

/** 助手身份标识（mod 名）。测试会断言生成的提示词里含这个串。 */
export const ICARUS_II_PERSONA_NAME = '伊卡洛斯·贰';

export { ICARUS_II_SYSTEM_PROMPT } from './prompts.generated.js';
