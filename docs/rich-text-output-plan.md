# 游戏富文本输出支持方案

> 目标：让 mod 的**输出文本**尽可能用满游戏定制 uGUI 的富文本能力，同时接受 LLM 最自然的 Markdown 输出。
> 范围：只改输出渲染，不改输入框语义；不切 `useMeshGenerator=false`；不引入第三方 Markdown 渲染器；不做运行期布局补丁。
>
> 状态（2026-10-23）：P0/P1/P2 核心已落地 —— `Shared/Text/GameRichText.cs`、
> `ChatWindow` 接线、`ui.richtext.v2` 能力与 sidecar 提示词；真表格已用独立 Text 段实现；
> 网页链接用 900000–999999 顺序 ID，点击打开外部浏览器；科技链接用 1000–9999，配方链接用 10000–19999，成就链接用 20000–21000，分别打开科技树、合成器、成就页。

---

## 0. 结论

当前 mod 的渲染层只放行了 `<i>`、`<color=#RRGGBB[AA]>`、`<size=N>`，并把 `<b>` 降级成亮白；游戏解析器实际支持的 **9 个标签**里有一半以上没用上，颜色也只支持了 2 种写法。

建议加一层 **“输出富文本编译器”**：

```text
LLM 原文（Markdown 文档 + 内联 HTML 子集）
        │
        ▼
GameRichTextFormatter（纯 C#，Unity-free，可单测）
  ├─ 块级/行内 Markdown AST
  ├─ 解析 HTML 子集（color/size/tint/box/sqrt/sup/sub/i/a/icon）
  ├─ 拒绝非法裸标签（按普通文本转义）
  ├─ 保护代码块/行内代码
  ├─ 样式优先级展平 + 配平
  └─ 输出游戏标签 + 纯文本副本
        │
        ▼
ChatWindow 渲染适配
  ├─ Text.supportRichText / useCheckBox / raycastTarget
  ├─ linkId → 动作注册表
  └─ iconMapping（已有）
        │
        ▼
游戏 TextGenerator
```

这样既能直接吃模型输出的 Markdown，也能保留游戏标签的精确控制；所有标签都经过白名单、配平和上限约束，不会因为一个坏标签把整段排版带崩。

### 0.1 本次调整：Markdown 外层 + 小型 HTML 子集

Markdown 允许**内联 HTML**，所以游戏样式不必都塞进链接。推荐 **MD-first + 小型 HTML 子集**：

- 结构、强调、标题、列表、代码、引用、普通链接继续用标准 Markdown；
- 游戏独有样式用**内联 HTML 标签**表达，标签名尽量贴近游戏标签；
- 只白名单下列这些 HTML 标签，不追求“支持 HTML”；其它 HTML 一律转义；
- `<color=...>` 这种**没有属性名**的写法不是合法 HTML；现在的解析器**不再接受**，会按普通文本转义。

核心写法：

```markdown
**<color value="#FFD966">重点</color>**
# <color value="#FFD966">标题</color>
<size value="20">大字</size>
<box>关键值</box>
<sqrt value="√">x+1</sqrt>
H<sub>2</sub>O、x<sup>2</sup>
<icon tag="tk" />
[文档](https://...)
```

为什么用 `value="..."` 而不是 `<color=#FFD966>`：

- `<color=#FFD966>` 没有属性名，`#FFD966` 直接跟在 `=` 后面，**不是合法的 raw HTML 标签**；标准 MD 解析器可能把它当普通文本或非法 HTML。
- `<color value="#FFD966">` 是合法的 raw HTML 开标签（未知元素 + 合法属性），CommonMark 会把它当内联 HTML 保留。
- 内容仍由 Markdown 解析：`**<color value="#FFD966">重点</color>**` 是“MD 粗体包 HTML 颜色”，外层结构始终是 MD。

> 如果要求 HTML5 校验也通过，可以把非标准标签名改成 `<dsp-color>`、`<dsp-size>` 这类带连字符的自定义元素；但为了贴近游戏标签，方案默认用 `<color>`/`<size>`/`<tint>`/`<box>`/`<sqrt>`/`<icon>`。
> 输出给游戏 Text 的 `<color=...>` 是格式化器的展开结果，不是解析器输入；输入里出现它会按普通文本转义。

---

## 1. 现状核对

### 1.1 实机 prefab 里的 Text 配置

在 Unity 里直接读取 `Assets/IcarusII/ChatWindow.prefab`，关键节点如下：

| 节点 | fontSize | supportRichText | useMeshGenerator | useCheckBox | raycastTarget | iconMapping |
| --- | ---: | --- | --- | --- | --- | --- |
| `example-reply/body`（助手正文模板） | 14 | true | true | false | false | null（运行期赋值） |
| `example-user/bubble/text` | 14 | true | true | false | false | null |
| `thinking-block/body` | 13 | true | true | false | false | null |
| `tool-template/body` | 13 | true | true | false | false | null |
| `chat-index/.../label` | 12 | false | true | false | false | 有（原生索引行） |

结论：正文走的是自研解析器，标签能力按本文档第 2 节生效；`useCheckBox`、`useTokenMapping` 都没开，`raycastTarget=false`，正文也没有 `TextEventTrigger`。链接颜色是默认的 `#3B94F8`。

### 1.2 当前代码路径

| 位置 | 现状 |
| --- | --- |
| `mod/src/IcarusII/UI/ChatWindow.cs:6908` | `SanitizeRichText`：只放行 `i / color / size / b`，其余 `<` 换成 `<\u200B` |
| `ChatWindow.cs:6954` | `IsAllowedTag`：`size` 最多 3 位数字；`color` 只接受 6/8 位十六进制；没有命名色、短色 |
| `ChatWindow.cs:6927` | `<b>` 转成 `<color=#FFFFFF>`，`</b>` 转成 `</color>` |
| `ChatWindow.cs:4912` | 正文无条件 `supportRichText = true` |
| `ChatWindow.cs:5863` | 流式输出每帧对整段 `_stream` 做一次 `SanitizeRichText` |
| `ChatWindow.cs:3708 / 4357 / 5327` | 结束流、历史回放、图标表就绪后都会重新清洗 |
| `sidecar/src/persona.ts:51` | 明确要求模型“不要输出 Markdown”，只教了 `i / size / color` |
| `design.md:560` | 仍写着“支持 `<b>`”，已与游戏实际解析器不符 |

### 1.3 差距

| 能力 | 现状 | 影响 |
| --- | --- | --- |
| 9 个标签 | 只用 `i / color / size` | `tint / sup / sub / sqrt / box / link` 全部浪费 |
| 颜色写法 | 只认 `#RRGGBB` / `#RRGGBBAA` | 30 个命名色、`#RGB`、`#RGBA`、`#A`、`#AF`、引号写法都显示成字面量 |
| 字号 | 最多 3 位，且不支持 `<size>` | 文档允许 1–6 位，`<size>` 恢复默认 |
| 标签配平 | 不检查闭合 | 未闭合的 `<color>` 会把后面的脚注也染色；错配时游戏解析器只按 `current_rich_style.scope` 弹栈，可能整段失效 |
| 大小写 | 校验时转小写、输出保留原样 | `<COLOR>` 能过校验但游戏不认，显示成字面量 |
| 图标 | `\tag;` 可用，`\tag-;` 也有解析 | 但 `SanitizeIconTags` 会误伤代码块里的反斜杠/Tab |
| 复选框 | 未开启 | `[-] [+] [/] [*]` 不会渲染 |
| 表格 | 未开启 | 正文 Text 的 `tableColWidths/RowHeights` 都是 0，`useTable=false` |
| Markdown | 被 system prompt 禁止 | 模型稍微“自然”一点就会输出 `**`、`#`、反引号，直接显示符号 |

---

## 2. 目标与非目标

### 2.1 目标

1. 输出正文支持全部 9 个游戏标签：`i / color / tint / size / sup / sub / sqrt / box / link`。
2. 支持文档里的全部颜色写法：命名色、`#A`、`#AF`、`#RGB`、`#RGBA`、`#RRGGBB`、`#RRGGBBAA`、带引号。
3. 支持 `\图标;`、`\图标-;`、只读复选框、真表格。
4. 接受 Markdown 子集：标题、粗体、斜体、行内代码、代码块、列表、任务列表、引用、链接、分隔线、表格。
5. 流式输出与历史回放结果一致；坏标签不破坏后面内容。
6. 所有点击行为可控：网页链接只允许 http/https（先校验）；科技/配方/成就链接只接受 `dsp://tech|recipe|achievement/<key>` 并解析到对应 ID；复选框只做只读展示。

### 2.2 非目标

- **不做真加粗**：游戏解析器没有 `Bold`，也没有下划线、删除线、背景色。粗体只能用亮白/字号近似，不能假装是真粗体。
- **不切 `useMeshGenerator=false`**：原生富文本只认 `b/i/size/color/material/quad`，会丢掉图标、`tint`、表格和自研排版，不值得。
- **不引入第三方 Markdown 库**：保持包体、字体和依赖可控；只实现 LLM 常用子集。
- **不支持任意 HTML、图片、LaTeX、外链直接打开**。
- **表格不与正文共用同一个 Text**：正文 Text 一旦有表格数组，整个 Text 都会按表格排版；实现方式是按表格把正文切成多个 Text 段（文本段/表格段），不把表格塞进段落 Text。

---

## 3. 总体设计

### 3.1 分层

| 层 | 位置 | 职责 |
| --- | --- | --- |
| 纯文本层 | `mod/src/Shared/Text/GameRichText*.cs`（Unity-free） | 词法/语法、标签校验、配平、Markdown 转换、转义、长度上限 |
| UI 适配层 | `mod/src/IcarusII/UI/RichTextOutput.cs` | 主题色、图标表谓词、链接动作、Text 组件开关 |
| 调用点 | `ChatWindow.cs` | 正文/思考/工具/卡片/历史/流式统一走 `FormatBody / FormatInline / FormatPlain` |
| 提示词 | `sidecar/src/persona.ts` | 按 `ui.richtext.v2` 能力选择格式化说明 |
| 协议 | `docs/protocol-v1.md` + `Protocol.cs` + `protocol.ts` | 新增可选能力串，不改既有字段 |

> 纯文本层放在 `Shared` 是为了复用 `tests/ProtocolTests` 的编译与测试入口；它不引用 `UnityEngine`，不改变 Shared 的 Unity-free 约束。

### 3.2 对外 API 草案

```csharp
public enum RichTextProfile
{
    Body,    // 助手正文：完整 Markdown + 游戏标签
    Inline,  // 标题/卡片描述：只做行内 Markdown，不产出块级结构
    Plain    // 工具输出/错误：全部转义，只保留图标短码
}

public sealed class RichTextResult
{
    public string Rich;                 // 给 Text.text
    public string Plain;                // 给复制按钮
    public bool HasLinks;
    public bool HasCheckBoxes;
    public List<RichTextLink> Links;    // linkId -> 动作
}

public static class GameRichText
{
    public static RichTextResult Format(string raw, RichTextProfile profile, RichTextOptions options);
}

public sealed class RichTextOptions
{
    public int BodyFontSize = 14;
    public int MinSize = 10;
    public int MaxSize = 36;
    public int MaxLength = 12000;       // 游戏 TextGenerator 上限 14400
    public bool AllowMarkdown = true;
    public bool AllowTables = false;    // 首期只降级
    public Func<string, bool> IsKnownIcon;
    public RichTextTheme Theme;
}
```

### 3.3 调用点收口

现在 `SanitizeRichText` 有 20 多处调用，直接替换容易漏。建议：

- 在 `ChatWindow` 内新增 `FormatBody / FormatInline / FormatPlain` 三个私有方法，内部调 `GameRichText.Format`。
- `Item` / `Line` 增加 `RawText` 字段；`ResanitizeLabel` 改为**从 RawText 重新渲染**，不要对已经转换过的 `label.text` 再跑一遍（否则 Markdown 标记已经消失，无法恢复）。
- `CopyText` 改为 `RichTextResult.Plain`，复制出来是干净文本；历史仍然存 RawText。
- 脚注拼接放在格式化之后，保证正文标签已经闭合，不会把 footer 染色。

---

## 4. 语法与映射

### 4.1 游戏原生标签（输出层，不作为输入）

> 解析器**只接受 4.2 的合法 HTML 子集**；本节是格式化器把 HTML 子集展开成游戏 Text 标签的**输出格式**，不再作为输入解析。
> `<color=...>`、`<size=...>`、`<link=...>` 这类没有属性名的裸标签会按普通文本转义，不兼容。

所有标签输出时统一小写。

| 标签 | 输入写法 | 校验/归一化 | 典型用途 |
| --- | --- | --- | --- |
| `i` | `<i>…</i>` | 无参数；嵌套深度计入上限 | 术语、书名、对比项 |
| `color` | `<color=#RRGGBB>`、`<color=#RRGGBBAA>`、`<color=#RGB>`、`<color=#RGBA>`、`<color=red>` | 命名色转小写；`#` 长度只允许 1/2/3/4/6/8；不允许空参数 | 语义高亮、警告、代码、引用 |
| `tint` | `<tint=颜色>…</tint>` | 同上；语义是**乘法**，只建议包图标 | 给图标上色，如 `<tint=#FFD966>\tk;</tint>` |
| `size` | `<size=N>`（1–6 位数字） | N 夹到 `[MinSize, MaxSize]` | 标题、注释、公式上下标 |
| `sup` | `<sup>…</sup>` | 无参数；嵌套 level 上限 3 | 指数、单位上标 |
| `sub` | `<sub>…</sub>` | 无参数；嵌套 level 下限 -3 | 化学式、配方下标 |
| `sqrt` | `<sqrt=√>…</sqrt>` | 必须是单个 BMP 字符，建议只允许 `√`/`∛`/`∜`；多字符 | 根号、公式 |
| `box` | `<box>…</box>` | 无参数；建议只给短代码/关键值，避免长段落 | 代码块、按键、配方重点 |
| `link` | `<link=N>`（1–6 位数字） | 1000–9999 科技；10000–19999 配方；20000–21000 成就；21001–899999 其它；网页从 900000 顺序分配 | 网页浏览器，科技树，合成器，成就页（仅输出，不作为输入） |

补充：

- `<b>` 不是游戏标签。格式化器把 `<b>` / `</b>` 和 Markdown `**…**` 都映射为 `<color=#FFFFFF>…</color>`；这是“亮白强调”，不是真加粗。
- 未在白名单里的 `<...>` 统一转义为 `<\u200B`，保留可读性但不参与解析。
- `<size>` 的 N 是绝对像素；正文 14，建议标题 20/18/16，代码 13，注释 12。
- `<link>` 不带参数时使用 21001–899999 的通用段，避开科技/配方/成就/网页段；网页链接统一由 `RichTextLinkAllocator` 从 900000 分配。
- 裸 `http(s)://...` 也会自动转成可点击链接（代码块/行内代码除外）；模型直接贴蓝图来源 URL 时不需要额外 Markdown。
- 块级排版：标题、段落、列表组、引用、代码块、分隔线之间保留一个空行；连续空行折叠；同一列表的相邻项之间不加空行。
- 段落内单换行按 Markdown 软换行合并：中文/中文标点之间不插空格，英文之间补一个空格；行尾两个空格或反斜杠保留为硬换行。

### 4.2 Markdown 外层 + 小型 HTML 子集

**原则：模型只写 Markdown；游戏样式用内联 HTML 表达。** 只白名单下列标签，其它 HTML 一律转义。格式化器把 Markdown 解析成 AST，再把 HTML 子集映射为游戏标签。

#### 4.2.1 标准 Markdown 映射

| Markdown | 输出 |
| --- | --- |
| `#`…`######` | 行首标题 → `<size=20/18/16>` + `<color=#FFFFFF>`，去掉 `#` |
| `**粗体**` / `__粗体__` | `<color=#FFFFFF>`（无真粗体；若当前已有显式颜色/链接，则不叠加白色） |
| `*斜体*` / `_斜体_` | `<i>` |
| `` `行内代码` `` | `<color=#9CDCFE>` + 转义；不套 `box`，避免每个词都带框 |
| ``` 围栏代码 ``` | `<box><color=#9CDCFE><size=13>` + 原样文本 + 闭合；代码内不解析标签/Markdown |
| `-` / `*` / `+` 列表 | 行首 `• `；缩进按层级 |
| `1.` 有序列表 | 行首 `1. ` |
| `- [ ]` / `- [x]` | `[/] ` / `[*] `（只读复选框） |
| `> 引用` | `│ ` + `<color=#8FA3B8>` |
| `[文字](https://...)` | `<link=900000+>文字</link>`；注册 url 供点击打开 |
| `[科技名](dsp://tech/dcx)` | 用科技 `IconTag` 解析为 ID，输出 `<link=1001>科技名</link>`；点击打开科技树并选中/展开科技 |
| `[物品](dsp://recipe/tieb)` | 用物品 `IconTag` 解析为物品 ID，输出 `<link=11101>物品</link>`；点击打开合成器并选中该物品主配方 |
| `[成就](dsp://achievement/1)` | 用成就 ID/SID 解析，输出 `<link=20001>成就</link>`；点击打开成就页并定位条目 |
| `---` / `***` | `────────`（暗色） |
| 表格 | 由 `SplitTables` 切成独立 Text 段，使用 `tableColWidths/tableRowHeights` 真表格排版（最多 4 列/40 行） |
| `![alt](url)` | 只输出 `alt` + 链接色；不加载图片 |
| `~~删除线~~` | 暗色文本（无删除线），保留可读性 |
| 其它 HTML / 未知标签 | 转义 |

#### 4.2.2 HTML 子集

只支持这些标签；标签名大小写不敏感，属性名小写，属性值建议总是加引号。

| 想要的效果 | 推荐写法 | 展开为 |
| --- | --- | --- |
| 颜色 | `<color value="#FFD966">重点</color>` | `<color=#FFD966>重点</color>` |
| 命名色 | `<color value="red">重点</color>` | `<color=red>重点</color>` |
| 字号 | `<size value="20">大字</size>` | `<size=20>大字</size>` |
| 图标着色 | `<tint value="#FFD966"><icon tag="tk" /></tint>` | `<tint=#FFD966>\tk;</tint>` |
| 方框 | `<box>关键值</box>` | `<box>关键值</box>` |
| 根号 | `<sqrt value="√">x+1</sqrt>`（可省略 value，默认 √） | `<sqrt=√>x+1</sqrt>` |
| 上标 | `x<sup>2</sup>` | `x<sup>2</sup>` |
| 下标 | `H<sub>2</sub>O` | `H<sub>2</sub>O` |
| 斜体 | `<i>术语</i>` 或 `<em>术语</em>` | `<i>术语</i>` |
| 粗体（降级） | `<b>重点</b>` 或 `<strong>重点</strong>` | `<color=#FFFFFF>重点</color>` |
| 网页链接 | `<a href="https://...">文档</a>` 或 `[文档](https://...)` | `<link=900000+>文档</link>` |
| 科技链接 | `[电磁学](dsp://tech/dcx)`（dcx 是 IconTag） | `<link=1001>电磁学</link>` |
| 配方链接 | `[铁块](dsp://recipe/tieb)`（tieb 是物品 IconTag） | `<link=11101>铁块</link>` |
| 成就链接 | `[成就](dsp://achievement/1)` | `<link=20001>成就</link>` |
| 图标 | `<icon tag="tk" />` | `\tk;` |
| 图标 + 名称 | `<icon tag="tk" name />` | `\tk-;` |
| 图标 + 名称 + 着色 | `<tint value="#FFD966"><icon tag="tk" name /></tint>` | `<tint=#FFD966>\tk-;</tint>` |

规则：

- `value` 必须带引号；`#FFD966`、`red`、`20`、`√` 都按字符串处理。
- `<a>` 只接受 `href`，且必须是 `http://` 或 `https://`；其它 scheme 只保留文字，不注册点击。
- `<icon>` 只接受 `tag`（1–6 位字母/数字）和布尔 `name`；没有 `tag` 时整段按文字输出。
- `<color>`/`<size>`/`<tint>`/`<sqrt>` 必须闭合；`<box>`/`<sup>`/`<sub>`/`<i>`/`<b>` 必须闭合；`<icon>` 自闭合。
- 语法必须是合法 HTML：属性必须有名字和 `=`（如 `<color value="#FFD966">`）；`<color#...>`、`<color=...>` 等裸标签一律转义。
- HTML 标签只作为**内联**扩展；不要把开标签单独放一行（标准 MD 可能把它当 HTML block）。代码块用围栏代码，不要用 `<box>` 包多段。
- 其它 HTML 标签（`<div>`、`<p>`、`<table>`、`<span>`、`<link>` 等）一律转义；`<link>` 是真实的 void HTML 元素，不拿它当游戏链接，统一用 `<a href>`。
- 游戏样式需要组合时用 HTML 嵌套，例如：
  ```markdown
  **<color value="#FFD966"><size value="20">重点</size></color>**
  ```
  Markdown 在外层、HTML 在内层，既保留 MD 结构，也保留游戏样式。

#### 4.2.3 样式优先级

Markdown/HTML 解析成 AST 后，按“每个文本 run 的有效样式”展开，而不是简单字符串替换：

1. `link`（最外层，负责 linkId/点击）；
2. `color`（显式色；链接色只在没有显式色时由游戏 `<link>` 自己给）；
3. `tint`（在 color 内层，乘法）；
4. `size`；
5. `sup`/`sub`；
6. `box`（在 color/size 内层，边框用当前文字色）；
7. `sqrt`；
8. `<i>`（最内层）。

补充规则：

- `**粗体**`、`<b>`、`<strong>` 都映射为白色；如果当前 run 已有 `color` 或 `link`，**不再叠加白色**，否则会把链接色/显式色覆盖掉。
- `tint` 只建议包图标；包普通文字时按乘法处理，颜色会变暗。
- 相邻且有效样式相同的文本 run 合并，减少标签数量。
- 代码块/行内代码不参与上面的样式叠加；代码块固定 `box + code color + size 13`。

### 4.3 颜色主题

不要在格式化器里散落硬编码颜色；用 `RichTextTheme` 从 `ChatWindow` 现有色板传入：

| 用途 | 建议色 |
| --- | --- |
| 正文 | 沿用 Text 自身颜色（不额外包 `<color>`） |
| 强调/标题 | `#FFFFFF` |
| 链接/信息 | `#5ABFFF`（现有 accent） |
| 次要/引用 | `#8FA3B8` |
| 代码 | `#9CDCFE` |
| 警告 | `#FF7852`（现有 WarnColor） |
| 成功 | `#7CE38B` |

注意：`<color=#RRGGBB>` 会把 alpha 设为 255；正文本身是半透明白，强行包 6 位色会变不透明。需要保留透明度时用 8 位色，或只对局部强调使用。

### 4.4 图标、复选框、表格

- 图标：保留 `\tag;` 和 `\tag-;`；用 `IsKnownIcon` 查当前 `iconMapping`，未知短码删掉（沿用现在的策略），避免画成乱码。代码块内的 `\` 要打断，不能被图标解析器命中。
- 复选框：只输出只读形式 `[/]` 和 `[*]`；不输出可点击的 `[-]`/`[+]`，避免点击后 `Text.text` 被游戏改写、与 RawText/历史不一致。需要 `useCheckBox=true`，只在 `HasCheckBoxes` 时开。
- 表格：`GameRichText.SplitTables` 把正文切成文本段/表格段；`ChatWindow` 克隆 `body` 生成独立表格 Text，设置 `tableColWidths/tableRowHeights/tableColAlignments`，并按原顺序插在 `body` 后面。表格 Text 与正文共用同一套字体/材质，但仍受 4 列/40 行上限约束。

---

## 5. 关键实现

### 5.1 Markdown 外层 + 标签内层

不要再从“裸文本里找 `<`”开始。顺序反过来：

```text
1. 规范化换行/空白，切出 fenced code 块
2. 块级 Markdown 解析：
   heading / paragraph / list / blockquote / table / hr
3. 行内 Markdown 解析：
   code span → link/autolink → strong/em → 文本节点
4. 文本节点内解析 HTML 子集：
   白名单标签/属性 → style span；Markdown 可以在 HTML 外层，也可以在内层
5. 非法裸标签检测：
   没有属性名的 `<color=...>` / `<size=...>` / `<link=...>` 按普通文本转义，不解析
6. 展平为“每个文本 run 的有效样式”
7. 按固定顺序输出游戏标签 + 转义文本
8. 配平兜底：任何未闭合标签在消息末尾补闭
```

约束：

- 输出标签一律小写；输入 `<COLOR>` 归一化为 `<color>`。
- 栈深度 ≤ 16；单条消息标签数 ≤ 512；输出长度 ≤ 12000（游戏上限 14400）。
- `size` 超范围夹紧；`link` 超范围夹紧；颜色参数不合法则整段按普通文本输出。
- 不允许 `<color>` 里再套一个未闭合的外层标签导致脚注染色；配平后 footer 永远在样式之外。
- HTML 子集只做内联扩展，不启动 CommonMark 的 HTML block；开标签不要单独占一行。
- 裸游戏标签如果跨段落，按“段落内闭合”处理，避免把后面的 Markdown 结构吞掉。

### 5.2 流式输出

- `BeginStream` 重置格式化器；`AppendStreamDelta` 只追加 RawText。
- `Render` 用 `_lastFormattedLength` 判重，只有 RawText 变长/图标表就绪时才重新格式化，避免每帧无谓分配。
- 格式化器有 `final=false` 模式：
  - 末尾未闭合的围栏代码、行内代码、标签、表格行先按普通文本输出，等下一段补齐再转换；
  - 已经完整的行/块可以稳定输出，减少闪烁。
- `EndStream` 用 `final=true` 做一次收尾，保证与历史回放完全一致。
- 长文本（>8K）时每帧全量解析仍然可接受；如果实测掉帧，再做“稳定前缀缓存 + 尾行增量”。

### 5.3 Text 组件接线

- `supportRichText = true` / `useMeshGenerator = true` 保持现状。
- `useCheckBox = result.HasCheckBoxes`：只读任务列表才开；关闭时 `[-]` 原样显示，不会误伤代码。
- `raycastTarget = result.HasLinks`：只有链接消息才开；避免所有正文都拦截点击。
- 链接：
  - 游戏在检测到 `<link>` 时会自动 `AddComponent<TextEventTrigger>()`（`Text.cs:1167`、`TextGenerator.cs:1470`），不需要手工加；
  - 订阅 `Text.onTriggerClick`，按 `TextTriggerBounds.type == LINK_TYPE` 取 `linkId`；
  - `RichTextLinkAllocator` 从 900000 开始顺序分配网页 ID；1000–9999 科技、10000–19999 配方、20000–21000 成就、21001–899999 其它；
  - 网页点击经 `GameRichText.IsHttpUrl` 二次校验后调用 `Application.OpenURL`；失败时复制 URL 并提示；
  - 科技链接先按 `dsp://tech/<iconTag>` 查 `TechProto.IconTag` 得到 1000–9999 的科技 ID，再调用 `UIRoot.instance.uiGame.OpenTechTree()` + `techTree.SelectTech(id)`；
  - 配方链接按 `dsp://recipe/<itemIconTag>` 解析为物品 ID，链接 ID = 10000 + 物品 ID，点击调用 `uiGame.FocusOnReplicate(itemId)`；
  - 成就链接按 `dsp://achievement/<id|SID>` 解析，链接 ID = 20000 + 成就 ID，点击调用 `statWindow.achievementPanelUI.HighlightAchievement(achId)`；
  - Text 销毁/重建时释放 `_linkHandlers`/`_linkTargets`。
- `linkColor` / `linkHoverColor` / `linkPressColor`：在 prefab 构建器里改成主题色（默认蓝已经接近 accent，但不完全一致）。
- `iconMapping` 继续走现有 `AssignIconMapping`。
- **不改任何 RectTransform**；以上都是行为/样式开关，不是布局补丁。

### 5.4 提示词与能力协商

新增可选能力 `ui.richtext.v2`：

- mod 在 `hello.capabilities` 里追加；老 sidecar 会忽略不认识的字符串，不影响工具注册。
- sidecar 在 persona 的“回复格式”段落按能力二选一：
  - **有 v2**：以标准 Markdown 为主；游戏样式用内联 HTML 子集，不直接写 `<color=...>` 这种无属性名形式；给出 5–6 个例子；说明粗体降级、图标只来自工具 `iconTag`、面板窄、不要 emoji。
  - **无 v2**：保持现有“只用游戏标签”的说明，避免老 mod 把 Markdown 符号画出来。
- 协议改动按硬规则走：先改 `docs/protocol-v1.md`，再改 `Protocol.cs` / `protocol.ts` / 测试。

建议给模型的格式说明（有 v2 时）：

```markdown
回复用标准 Markdown：标题、列表、**粗体**、*斜体*、`代码`、```代码块```、> 引用、[链接](https://...)。

需要游戏样式时，用一小撮内联 HTML 包住 Markdown 文字；不要把 <color=...> 这种没有属性名的写法当 HTML：
- 颜色：**<color value="#FFD966">重点</color>**
- 字号：<size value="20">大字</size>
- 标题：# <color value="#FFD966">标题</color>
- 方框：<box>关键值</box>
- 根号：<sqrt value="√">x+1</sqrt>
- 上标/下标：H<sub>2</sub>O、x<sup>2</sup>
- 图标：<icon tag="tk" />，显示名称写 <icon tag="tk" name />
- 链接：[文字](https://...)

粗体/HTML <b> 都会降级成亮白强调；不要依赖真加粗。
图标只能用工具结果里的 iconTag，不要编造短码。不要用 emoji。
```

如果不想动协议，也可以退化为按 `modVersion` 判断；但不推荐，版本比较不如能力串明确。

### 5.5 复制与历史

- `RichTextResult.Plain` 在格式化时同步生成：去掉标签、去掉 Markdown 标记、保留代码原文和链接文字。
- 复制按钮用 Plain；历史存 RawText；回放时重新格式化。
- 这能解决现在“复制出来带 `<color=...>`”的问题。

### 5.6 配置

新增 BepInEx 配置（默认 `full`）：

```ini
[UI]
RichTextOutput = full   # full | basic | off
```

- `full`：本文全部能力。
- `basic`：只保留 `i/color/size`（等价当前行为，作为回退）。
- `off`：全部转义，纯文本。

用于游戏更新导致解析器异常时快速止损，也方便对比问题。

---

## 6. 分阶段落地

| 阶段 | 内容 | 主要文件 | 验收 | 估算 |
| --- | --- | --- | --- | --- |
| **P0** | 抽出 `GameRichText`；补单测；修大小写/`size` 位数/颜色写法；行为保持 | `Shared/Text/*`、`ChatWindow.cs`、`ProtocolTests` | 单测通过；现有输出不回归 | 0.5–1 天 |
| **P1** | 9 个标签全开；配平/夹紧；组件开关；prompt 补标签速查 | `ChatWindow.cs`、`IcarusIIOutputArea`/`ReplyBuilder`、`persona.ts` | 游戏内标签画廊全部生效 | 1–2 天 |
| **P2** | Markdown 外层 + HTML 子集 + AST/样式优先级 + 流式 final 模式 + `ui.richtext.v2` | formatter、`persona.ts`、协议文档/测试 | MD/HTML 用例、合法 MD 校验、流式不闪、回放一致 | 2–4 天 |
| **P3** | 链接点击打开、只读复选框、复制纯文本 | `ChatWindow.cs`、prefab、`Strings.cs` | 已实现；待实机点击验收 | 1–2 天 |
| **P4（可选）** | 真表格 block、token 插值、更多主题 | `ChatWindow.cs` 动态正文段 | 表格混排验收 | 2–4 天 |

建议先做 **P0 + P1**：这两步就能把“游戏支持但 mod 没用上”的标签全部用起来，风险也最低；P2 再解决 LLM 自然输出 Markdown 的问题。

---

## 7. 测试

### 7.1 纯文本单测（ProtocolTests 内新增）

- 标签：9 个起始/闭合、大小写、无参数、错误参数、嵌套、错配、未闭合、栈深度。
- 颜色：命名色、`#A`/`#AF`/`#RGB`/`#RGBA`/`#RRGGBB`/`#RRGGBBAA`、引号、非法长度。
- 字号：`<size>`、`<size=0>`、1–6 位、超范围夹紧、`<size="22">` 无效。
- 根号：`<sqrt=√>`、多字符、空参数、非 BMP。
- 链接：`<link>`、`<link=1>`、6 位、超范围、动作注册。
- Markdown：标题、粗体/斜体、行内/围栏代码、列表、任务列表、引用、链接、分隔线、表格降级。
- HTML 子集：`color/size/tint/box/sqrt/sup/sub/i/b/strong/em/a/icon`；属性校验、非法属性降级为文字、HTML 嵌套。
- 样式优先级：`**<color value="#...">重点</color>**` 颜色不被白色覆盖；`*<color value="#...">术语</color>*` 同理；链接色不被粗体覆盖。
- 流式：HTML 标签/围栏/强调被 delta 切开；`final=false` 与 `final=true` 最终结果一致。
- 安全：`<script>`、`a < b`、超长、超深、超多标签、代码里的 `\tag;`/`[-]`。
- 图标：已知/未知短码、`-` 形式、代码块内不转换。

### 7.2 合法 Markdown 校验（开发期）

生产环境不引入 Markdown 渲染器，但测试里可以用 dev-only 的参考解析器（例如 `markdown-it`/`commonmark`）验证“推荐写法确实是合法 Markdown”：

- `<color value="#FFD966">重点</color>` 解析为 html_inline，属性 `value` 保留 `#FFD966`；
- `**<color value="#FFD966">重点</color>**` 解析为 strong 内含 html_inline；
- `<icon tag="tk" name />` 解析为自闭合 html_inline；
- `<a href="https://...">文档</a>` 解析为 link；
- `H<sub>2</sub>O` 解析为文本 + html_inline；
- 裸 `<color=...>` 不是合法 HTML，解析器直接拒绝并转义。

### 7.3 协议/提示词

- `hello.capabilities` 含 `ui.richtext.v2`。
- 老 mod 无该能力时，sidecar 仍输出旧格式提示。
- 新 mod + 老 sidecar：不崩溃，最多不启用 Markdown 提示。

### 7.4 游戏内验收

做一个开发用“富文本画廊”（`!richtext` 调试命令或独立测试场景），在 1920×1080 和至少一个非 1080p 分辨率下检查：

- 9 个标签逐项显示；嵌套、空标签、行尾标签不崩。
- `<box>` 单行/多行；`<sqrt>` 行首/行尾；`<sup>/<sub>` 与 `<size>` 组合。
- 颜色 30 个命名色 + 6 种 hex；透明度正确。
- 图标 `\tk;`、`\tk-;`、`<tint>` 组合。
- 只读复选框 `[/]`、`[*]`。
- 链接 hover/press、点击打开浏览器、滚动不冲突。
- 8K 长文流式输出不掉帧、不闪、不截断脚注。
- 历史回放与实时一致；复制为纯文本。
- 1080p/900p 下长代码块、`<box>`、`<sqrt>` 不越界、不遮按钮/滚动条。

### 7.5 回归命令

```bash
scripts/build-mod.sh
dotnet run --project tests/ProtocolTests
cd sidecar && npm test
# 改了 prefab/静态 UI 时
scripts/build-ui-bundle.sh
```

---

## 8. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| 游戏解析器的闭合逻辑只按当前 scope 弹栈 | 错配标签导致整段样式失效/染色 | 输出前强制配平；错配时补闭内层 |
| `<box>`/`<sqrt>` 的几何偏移在窄面板里溢出 | 文字压到按钮/滚动条 | 限制使用场景；画廊实测；长代码块拆行 |
| `raycastTarget=true` 影响滚动/点击 | 正文不能拖动滚动 | 只在有链接时开；实测 ScrollRect 拖动 |
| 字体缺 `·`、`√`、私有区复选框字形 | 显示方块 | 用游戏已有字形；画廊逐项确认 |
| 正文 14400 字符上限 | 长回答被截断并刷 warning | formatter 12000 截断 + 省略号 |
| 代码块里的 `\tag;`/`[-]` 被误解析 | 代码显示成图标/复选框 | 代码段内转义 `\`、`[`、`<` |
| 提示词变长/模型不遵守 | token 成本、输出仍带 Markdown | 提示词保持短；formatter 兜底转换 |
| 模型不会写 HTML 子集 | 游戏样式用不出来 | 提示词给 5–6 个合法 HTML 例子；非法裸标签直接转义，不静默兼容 |
| 老 mod + 新 sidecar | 新提示词输出 Markdown 但老 mod 不认 | 用 `ui.richtext.v2` 能力门控 |
| 真表格需要独立 Text | 正文混排做不了 | 已按表格切段，动态克隆 `body` 生成表格 Text |

---

## 9. 待确认

1. **HTML 子集范围**：建议就 `color/size/tint/box/sqrt/sup/sub/i/b/strong/em/a/icon` 这些；其它 HTML 一律转义，不引入通用 HTML。
2. **HTML5 校验**：`<color>`/`<size>` 等是 unknown element，CommonMark 能当 raw HTML，但严格 HTML5 校验器可能提示；如果必须过校验，用 `<dsp-color>` 这类自定义元素。方案默认保留 `<color>` 以贴近游戏标签。
3. **Markdown 是否作为默认输出语法？** 建议：有 `ui.richtext.v2` 时提示词优先 Markdown + HTML 子集，否则保持游戏标签。
4. **链接点击做什么？** 已改为：只对 http/https 调用外部浏览器；失败时复制 URL；没有动作的链接只做视觉。
5. **是否需要真表格？** 已做基础版：`SplitTables` 把正文切成文本段/表格段，表格段用独立 Text 的 `tableColWidths/tableRowHeights` 排版；列数上限 4、行数上限 40。
6. **复制按钮复制纯文本还是带格式原文？** 建议：纯文本（`RichTextResult.Plain`）。
7. **只读复选框默认开吗？** 建议：只在消息包含任务列表时开 `useCheckBox`，避免误伤代码里的 `[-]`。
8. **字号夹紧范围**：建议 `[10, 36]`，正文 14，标题 20/18/16；可按主题调整。

---

## 10. 一句话建议

先做 P0 + P1（抽层、配平、9 标签、组件开关、prompt 补全），这是“把游戏已有能力用满”的最低风险路径；P2 再把 Markdown 接进来，P3 做链接/复选框/表格降级。这样即使 LLM 输出的是最普通的 Markdown，玩家看到的也会是接近游戏原生质感的富文本，而不是一堆星号和反引号。
