# 提示词源文件

这里的 markdown 是**唯一手写源**，`scripts/gen-prompts.mjs` 在 `npm run build` / `typecheck` 时
把它们编译成 `src/prompts.generated.ts`（纯字符串常量，跟 sidecar 二进制一起发布）。

## 文件职责

| 文件 | 内容 |
| --- | --- |
| `system.zh.md` | 人格主体：身份、职责、语气、数据纪律、能力边界。含 `{{NAME}}` / `{{FORMAT}}` 占位符 |
| `format.legacy.zh.md` | 老 mod（不支持 `ui.richtext.v2`）的回复格式段 |
| `format.rich.zh.md` | 支持 Markdown 的新 mod 的回复格式段 |

## 规则

- **不要在人格里写具体工具名**。能力清单由 `src/prompt/sections.ts` 按本轮注册表生成；
  工具的使用规则写在工具定义的 `promptGuidelines` 里（同样由 sections 渲染进 system prompt）。
  手写清单会腐烂：实机踩过“人格仍称只能读 → 模型拒绝调用写工具”。
- 占位符缺失/未知会让**构建直接失败**，不会静默回退（旧的 `indexOf` 拼接方案会）。
- 改完运行 `npm run test:prompts`：它会检查生成物是否最新、人格是否含 pi 默认 prompt 的特征串、
  是否偷偷写回了工具名。
- 人格文本不能出现 `Current working directory` / `Available tools:` / `AGENTS.md` 等 pi 默认 prompt 特征串。

## 调试覆盖

```bash
node dist/index.js serve --system-prompt-file /tmp/prompt.md
```

整份替换内置人格（legacy/rich 都用这一份），启动读一次，日志记录 path + sha256；
文件缺失或为空直接报错。仅用于调试/评测，游戏内 UI 不暴露。
