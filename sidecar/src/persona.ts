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
 * 文本源在 `sidecar/prompts/`：
 *   - `system.zh.md`          人格主体，含 `{{NAME}}` / `{{FORMAT}}` 占位符
 *   - `format.legacy.zh.md`   老 mod 的游戏富文本格式段
 *   - `format.rich.zh.md`     支持 ui.richtext.v2 的 Markdown 格式段
 *
 * `scripts/gen-prompts.mjs` 在 build/typecheck 时把它们编译成
 * `src/prompts.generated.ts`（纯字符串常量），这里只做转导出，保持 import 路径稳定。
 *
 * 为什么不运行时读 markdown：sidecar 用 esbuild 打成单文件、再塞进 Node SEA，
 * 运行期资源要额外分发；编译成常量则跟二进制一起走，行为可复现，测试也能做快照。
 * 为什么不再用 indexOf 切割拼装：切不到标记时旧实现会静默回退整份文本，
 * 改动措辞就可能让新 mod 收到老格式；现在占位符缺失会让构建直接失败。
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
export {
  ICARUS_II_PERSONA_NAME,
  ICARUS_II_RICH_TEXT_PROMPT,
  ICARUS_II_SYSTEM_PROMPT,
} from './prompts.generated.js';
