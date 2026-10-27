# 提示词源文件

这里的 markdown 是**唯一手写源**，`scripts/gen-prompts.mjs` 在 `npm run build` / `typecheck` 时
把它编译成 `src/prompts.generated.ts`（纯字符串常量，跟 sidecar 二进制一起发布）。

## 文件职责

| 文件 | 内容 |
| --- | --- |
| `system.zh.md` | 完整人格：身份、职责、语气、回复格式（Markdown + 游戏 HTML 子集）、数据纪律、能力边界 |

只有这一份提示词：mod 已统一支持 `ui.richtext.v2`，不再保留“只用游戏标签”的 legacy 版本，
也没有 `{{NAME}}` / `{{FORMAT}}` 占位符——名字和格式直接写在 markdown 里。

## 规则

- **不要在人格里写具体工具名**。能力清单由 `src/prompt/sections.ts` 按本轮注册表生成；
  工具的使用规则写在工具定义的 `promptGuidelines` 里（同样由 sections 渲染进 system prompt）。
  手写清单会腐烂：实机踩过“人格仍称只能读 → 模型拒绝调用写工具”。
- 文本里出现 `{{...}}` 会让**构建直接失败**（占位符机制已移除），不会静默回退。
- 改完运行 `npm run test:prompts`：它会检查生成物是否最新、人格是否含 pi 默认 prompt 的特征串、
  是否偷偷写回了工具名或英文规则模板。
- 人格文本不能出现 `Current working directory` / `Available tools:` / `AGENTS.md` 等 pi 默认 prompt 特征串。

## 调试覆盖

```bash
node dist/index.js serve --system-prompt-file /tmp/prompt.md
```

整份替换内置人格，启动读一次，日志记录 path + sha256；文件缺失或为空直接报错。
仅用于调试/评测，游戏内 UI 不暴露。
