# 蓝图（本地文件 + 社区站点）

蓝图只操作游戏蓝图根目录内的受限路径；`blueprint_save` 与 `blueprint_generate` 写入的是 `.txt` 蓝图文本，不改变游戏状态。

共 5 个工具。提示词如何进入模型上下文见 [prompt-shape.md](prompt-shape.md)。

---

### `blueprint_fs`

**label** 蓝图文件 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
在游戏的蓝图目录里查看和整理文件，类似受限的 ls/cd/pwd/cat/mkdir/rm/rmdir。路径以蓝图根为 `/`；默认保存目录是 `/IcarusII`。不能访问蓝图目录之外。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要查看蓝图目录、进入子目录、读取蓝图信息、建目录、删除或保存任意蓝图文本时使用。
- 默认保存到 `/IcarusII`；用 op=write 保存模型从其他途径拿到的蓝图原文（必须是合法 .txt 蓝图）。
- op=rm/rmdir 是永久删除；必须先向玩家确认，只有玩家明确同意后才传 confirm=true。
- cat 默认返回蓝图元数据；只有确实需要原文时才传 raw=true。
- 路径不能含 .. 或盘符；不能越出蓝图根。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `op` | `string \| string \| string \| string \| string \| string \| string \| string` | 是 | — | 操作类型 |
| `path` | `string` | 否 | — | 虚拟路径；相对当前目录，或以 / 开头 |
| `content` | `string` | 否 | — | op=write：蓝图 .txt 原文 |
| `recursive` | `boolean` | 否 | — | ls 递归 / rmdir 递归删除，默认 false |
| `confirm` | `boolean` | 否 | — | rm/rmdir 必须为 true；永久删除 |
| `overwrite` | `boolean` | 否 | — | write 是否覆盖同名文件，默认 false |
| `raw` | `boolean` | 否 | — | cat 是否返回蓝图原文，默认 false（只返回元数据） |
| `details` | `boolean` | 否 | — | ls 是否解析 .txt 蓝图元数据，默认 false |
| `limit` | `integer` | 否 | minimum=1，maximum=1000 | ls 最多返回条目，默认 200 |

#### 返回

返回文本摘要。写入类工具还会广播蓝图卡片事件（协议 `blueprint_card` / `link_card`），卡片走协议通道，不在工具返回值里。

---

### `blueprint_search`

**label** 蓝图搜索 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
在 DSPBluePrints/FactoryBluePrints 仓库和 Dyson Sphere Blueprints 网站里按关键词搜索蓝图（在线按需，不打包内容）。GitHub 结果带仓库路径与许可 CC BY-NC-SA 4.0；网站结果带详情页 URL。引用时必须署名来源与作者。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家想要现成蓝图/产线设计时使用。
- 路径多为中文目录+文件名，用“物品/产线/规模”这类关键词搜索；可多次换词。
- GitHub 结果用 path 继续 blueprint_info/blueprint_save；Dyson Sphere Blueprints 结果用 url。
- GitHub 的来源链接用 blobUrl（文件页），不是 repo；Dyson Sphere Blueprints 用 url。
- 结果必须附来源链接与作者；GitHub 内容许可 CC BY-NC-SA 4.0，不要把蓝图内容说成自己生成的。
- GitHub 搜索条目没有作者时不要编造；作者以 blueprint_info/blueprint_save 读出的 header 为准。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `query` | `string` | 是 | — | 路径关键词，例如“卡西米尔”“白糖”“300/min” |
| `limit` | `integer` | 否 | minimum=1，maximum=20 | 默认 10 |

#### 返回

返回文本摘要。写入类工具还会广播蓝图卡片事件（协议 `blueprint_card` / `link_card`），卡片走协议通道，不在工具返回值里。

---

### `blueprint_info`

**label** 蓝图详情 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
读取一个蓝图文件并做格式体检（头部/base64+gzip/md5），返回大小与解码后字节数，不解析建筑。支持 GitHub 仓库路径或 Dyson Sphere Blueprints 详情页 URL；内容版权归原作者，必须署名来源。
```

#### 模型可见 · 使用规则（system prompt）

- 在 blueprint_search 选中结果后使用，确认文件有效并给出下载链接。
- GitHub 结果传 path；Dyson Sphere Blueprints 结果传 url。
- valid=false 时说明原因（format/gzip），不要猜里面的建筑。
- 回答必须带来源链接和署名；GitHub 仓库内容许可为 CC BY-NC-SA 4.0。
- GitHub 的来源链接用返回的 blobUrl（文件页），不是 repo；Dyson Sphere Blueprints 用 url。
- 没有 author 时不要编造，按 header 里实际内容或“作者见蓝图页”处理。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `path` | `string` | 否 | — | GitHub 仓库路径（blueprint_search 的 path） |
| `url` | `string` | 否 | — | Dyson Sphere Blueprints 详情页 URL |

#### 返回

返回文本摘要。写入类工具还会广播蓝图卡片事件（协议 `blueprint_card` / `link_card`），卡片走协议通道，不在工具返回值里。

---

### `blueprint_save`

**label** 保存蓝图 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
把 blueprint_search 找到的蓝图保存到游戏蓝图目录（Documents/Dyson Sphere Program/Blueprint），支持 GitHub 仓库路径或 Dyson Sphere Blueprints 详情页 URL；保存后打开游戏内蓝图界面即可使用。内容版权归原作者，保存/再分发请遵守来源页面说明并署名。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家说“帮我保存/导入这个蓝图”时使用；GitHub 结果传 path，Dyson Sphere Blueprints 结果传 url。
- 默认不覆盖同名文件，会自动加 (2)/(3)；玩家明确要求覆盖时才传 overwrite=true。
- 保存不改变游戏状态，但回答要说明保存位置，并保留来源链接与作者署名。
- GitHub 的来源链接用 blobUrl（文件页），不是 repo；Dyson Sphere Blueprints 用 url。
- 没有 author 时不要编造。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `path` | `string` | 否 | — | GitHub 仓库路径（blueprint_search 的 path） |
| `url` | `string` | 否 | — | Dyson Sphere Blueprints 详情页 URL |
| `name` | `string` | 否 | — | 保存名（不含 .txt），默认用蓝图/文件名 |
| `dir` | `string` | 否 | — | 保存目录（相对蓝图根，默认 /IcarusII）；不存在会自动创建 |
| `overwrite` | `boolean` | 否 | — | 同名时覆盖，默认 false |

#### 返回

返回文本摘要。写入类工具还会广播蓝图卡片事件（协议 `blueprint_card` / `link_card`），卡片走协议通道，不在工具返回值里。

---

### `blueprint_generate`

**label** 生成产线蓝图 · **能力位** 无（sidecar 本地工具） · **风险** 0（只读） · **超时** —

#### 模型可见 · 工具描述（tools 数组）

```text
按知识库里的真实配方，从原矿开始生成完整产线蓝图（各阶段机器 + 传送带 + 分拣器 + 连接槽位），保存到游戏蓝图目录。需要先进入存档让知识库建立；输入物品与每分钟产量，可选机器/带子/分拣器级别与行数。
```

#### 模型可见 · 使用规则（system prompt）

- 玩家要“完整产线蓝图/带带子和爪子的蓝图”时使用，不要回答只能下载。
- item 可以是物品名（如“电磁涡轮”）或物品 id；rate 是每分钟产量。
- buildingTier: base 对应最低速机器 / mk2 / mk3；beltType: 0 黄带 / 1 绿带 / 2 蓝带；insertType: 0 分拣器 / 1 高速 / 2 极速 / 3 集装。
- 返回里的 stages 是每个阶段的机器数与产物，回答时先报最终产物与总机器数，再按阶段说明。
- warnings 非空时必须原样告诉玩家，不要声称“完整可用”。

#### 参数

| 参数 | 类型 | 必填 | 约束 | 说明 |
| --- | --- | --- | --- | --- |
| `item` | `string` | 是 | — | 物品名或物品 id |
| `rate` | `number` | 是 | — | 目标每分钟产量 |
| `buildingTier` | `string \| string \| string` | 否 | — | 机器档位，默认 base（知识库里的最低速机器） |
| `beltType` | `integer` | 否 | minimum=0，maximum=2 | 默认 2（蓝带） |
| `insertType` | `integer` | 否 | minimum=0，maximum=3 | 默认 2（极速） |
| `rows` | `integer` | 否 | minimum=1，maximum=8 | 产线行数，默认 1；超宽时会自动加行 |
| `supply` | `string \| string` | 否 | — | 原料供给方式：belt 走物品标签（默认），station 在行首放物流站并接出料带（最多 3 种原料） |
| `layout` | `string \| string \| string` | 否 | — | 布局方式：auto（默认，单张总线放不下时先试工厂布局，仍不行才拆张）；bus 只用单条混带；factory 每个阶段一个模块、模块间用物流站供货，整条产线一张图 |
| `stackSize` | `integer` | 否 | minimum=1，maximum=4 | 集装堆叠，默认 1 |
| `depth` | `integer` | 否 | minimum=1，maximum=16 | 配方展开深度，默认 8 |
| `from` | `string` | 否 | — | 起点：ore（默认，从原矿投入）/ smelted（从冶炼产物，如铁块/钛块开始）/ 物品名或 id（从该中间产物开始） |
| `dir` | `string` | 否 | — | 保存目录（相对蓝图根，默认 /IcarusII）；不存在会自动创建 |
| `name` | `string` | 否 | — | 蓝图文件名 |
| `author` | `string` | 否 | — | 作者，默认“自动生成” |
| `version` | `string` | 否 | — | 版本号，默认当前游戏版本 |
| `attributes` | `string` | 否 | — | 蓝图属性 |
| `description` | `string` | 否 | — | 蓝图介绍 |

#### 返回

返回文本摘要。写入类工具还会广播蓝图卡片事件（协议 `blueprint_card` / `link_card`），卡片走协议通道，不在工具返回值里。

---
