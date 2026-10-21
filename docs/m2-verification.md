# M2 实机验收清单

> M2 的离线链路已经用假游戏验证；下面这些必须在真实存档里逐条过。
> 工具执行日志前缀：`[tool]`。sidecar 日志在 `%APPDATA%\icarus-ii\logs\`。

## 0. 准备

```bash
scripts/fetch-deps.sh
scripts/build-mod.sh
cd sidecar && npm install && npm run build
scripts/build-sidecar.sh          # 可选：验证发布包
scripts/install.sh --game-dir "/path/to/Dyson Sphere Program"
```

进入一个已有存档，按 `R` 打开窗口，确认状态栏显示“就绪”。

## 1. 基础工具

| # | 问法 | 期望调用 | 核对方式 |
| --- | --- | --- | --- |
| 1 | “我现在在哪个星球？” | `game_get_overview` | 星球名、坐标与游戏 UI 一致 |
| 2 | “我还有多少能量？背包里有什么？” | `game_get_player` | 核心能量/护盾与机甲面板一致；背包物品名/数量抽查 3 项 |
| 3 | “这个星球有什么矿？” | `game_get_planet` | 矿种与总量与矿脉面板/扫描一致；建筑计数抽查 2 类 |
| 4 | “电够不够？” | `game_get_power` | 需求/已供/满足率与电力面板一致（允许 1% 取整误差） |
| 5 | “现在研究什么？还差多少？” | `game_get_research` | 当前科技、进度、队列与研究面板一致 |
| 6 | “我现在缺什么？” | `overview + power + production`（至少 2 个） | 回答必须点名具体物品与净产出；不能出现工具结果里没有的物品 |
| 7 | “给我一个开局建造顺序” | 可不调工具 | 物品/建筑前出现游戏图标（`\tag;` 短码），而不是字面量；`<b>` 不再显示为标签 |

## 2. 边界

1. **主菜单**直接问 → 工具返回 `not_in_game`，助手说明“还没进入存档”，不能编造。
2. **远程行星**（未加载工厂）→ `game_get_planet` 返回 `factoryLoaded:false`，只给静态矿脉；
   `power/production` 明确说“工厂未加载”，不能返回全 0 冒充正常。
3. **换档**后再问 → `meta.saveKey` 是新的；旧档的产线/电力不能串进来。
4. **断线**：停掉 sidecar（或任务管理器结束进程）再发问 → 工具卡片显示失败，
   助手不能假装成功；重连后恢复正常。
5. **超时**：把 `game_get_planet` 的 `timeoutMs` 临时改小（或在超大工厂上触发）
   → 返回 `timeout`，窗口不卡死。
6. **主线程预算**：连续问 10 次 `game_get_planet`，窗口/游戏不出现明显掉帧；
   一次工具调用总耗时目标 < 1s（超大工厂允许 2s）。

## 3. 数值口径

- 电力：`kW`；储能：`kWh`；产线：`/min`；比例：`%`。
- 电力换算：`PowerNetwork` 每 tick 值 × 60 ÷ 1000。
- 产线：`ProductStat.total[1]`（最近 60s）作为 `/min`；`total[7]` 为消耗。
- 若电力面板显示的单位与 `kW` 不一致，以面板为准调整 `GameToolFormat`，并同步协议附录 B.4。

## 4. 已知风险

| 风险 | 观察点 | 处理 |
| --- | --- | --- |
| 产线统计未建立 | `game_get_production` 返回 `statsPending:true` | 打开一次统计面板或等待几秒；仍为空则记录存档规模 |
| `energyStored` 单位不是 J | `storedKwh` 与电力面板储能差一个数量级 | 实测后修正 `GameToolFormat.Kwh` |
| 远程行星 `runtimeVeinPool` 为空 | 矿脉列表为空但星球确实有矿 | 检查 `planet.data` 与 `factoryLoaded`，必要时改读 `PlanetRawData` |
| DeepSeek flash 不调工具 | 回答全是通用建议、日志无 `[tool]` | 先用 `game_get_overview` 单轮测试；检查上游请求里是否有 tools |
| 工具结果过长 | 回答只引用前几条 | 用 `offset/limit` 或 `itemFilter` 缩小范围 |

## 5. 离线回归（无需游戏）

```bash
dotnet run --project tests/ProtocolTests        # 252 项
cd sidecar && npm test                          # config 82 + 协议 57 + 目录 8 + tools 19 + pi-tools 32
```

离线测试覆盖：协议往返、callId 回退、超时钳制、`tool_result` 解析、
capability → `ready.tools`、tool_call→tool_result 往返、超时、断线、非法结果，
以及**真实 pi 会话**把 `customTools` 发给上游、执行工具、回填后继续第二轮的完整链路。

## 6. 思考档位 / 折叠块（M2.5）

| 检查项 | 期望 |
| --- | --- |
| 下拉框默认值 | 显示「高」；`icarus-ii.cfg` 的 `[Agent] ThinkingLevel=high` |
| 选「高」/「最高」 | 窗口发 `config.set{thinkingLevel}`；sidecar `config.json` 落盘；下一次回答前重建 pi session |
| 上游请求 | 开启后带 `thinking:{type:"enabled"}`；关闭后带 `thinking:{type:"disabled"}`（`sidecar/test/pi-tools.mjs` 已断言） |
| 思考块 | 流式时展开显示推理；正文开始或发起工具调用时自动收起，标题行显示一行摘要；点标题行可重新展开 |
| 多段思考 | 一次提问里「思考 → 工具 → 再思考 → …」每段各自一块，按发生顺序插在工具块之间；不会全部挤进第一块，旧内容也不会被新一段覆盖 |
| 工具块 | 与思考块同款折叠样式；读取中显示「读取中…」，完成后摘要显示结果；失败自动展开 |
| 断线改档 | 本地 `icarus-ii.cfg` 记住选择；重连后补发 `config.set` |
| 空思考 | 模型没有输出 reasoning 时不留下空块 |


## 7. 多供应商 / 多模型（M2.6）

| 检查项 | 期望 |
| --- | --- |
| 模型页入口 | 设置 → 模型；左栏供应商、右栏编辑卡片 |
| 默认供应商 | DeepSeek，预置 `deepseek-v4-flash` / `deepseek-v4-pro` |
| 添加供应商 | 输入 id（如 `openai`）→ 添加 → 填 key / baseURL / 模型 id |
| 密钥写入 | 输入新 key 并点「应用设置」→ `config.json` 对应 provider 的 `apiKey`；出站帧不含 key |
| 密钥只读 | 重新打开模型页，key 框为空、占位符显示「已配置，留空不修改」，绿点表示已配置 |
| 删除供应商 | 删除后点「应用设置」→ `providers` 里消失，其他供应商的 key 不受影响 |
| 切换当前模型 | 点模型行 → 点「应用设置」→ 聊天窗「模型」下拉框同步选中 |
| 聊天窗切模型 | 直接选另一个模型 → 下一轮 prompt 使用新 provider/model |
| 获取可用模型 | 点模型列表下的「获取可用模型」→ 弹层列出模型；已添加的显示「已添加」，其余可「+ 添加」 |
| 预设供应商 | 新增供应商时下拉框提供 DeepSeek/OpenAI/Anthropic/Google Gemini/OpenRouter/自定义；选中后填入 ID |
| Key 检测 | API Key 旁「检测」→ `provider.test` 的 `/models` 探测；成功显示绿色「Key 可用 · N ms」，401 显示红色拒绝 |
| 已知供应商目录 | DeepSeek/OpenAI 无 baseURL → pi 内置目录（`test/pi-tools.mjs` 断言 OpenAI 有 gpt-4o） |
| 自定义端点目录 | 自定义供应商 + baseURL → `GET {baseURL}/models`；失败时显示错误，不拿官方目录冒充 |
| 完整端点地址 | API 地址填成 `.../v1/chat/completions` → 检测/目录都按 `.../v1` 处理（不再拼出 `.../chat/completions/models`） |
| 漏写 /v1 | 地址只写到域名 → 自动补一次 `/v1`；仍 404 时提示「可能缺 /v1」 |
| Anthropic 风格端点 | 目录与检测都用 `x-api-key`（回归：以前目录固定 Bearer，导致 401） |
| Gemini 风格端点 | 用 `x-goog-api-key`；模型 id 去掉 `models/` 前缀，`displayName` 作为显示名 |
| 401/404 文案 | 401 显示「API Key 被拒绝」；404 提示检查 `/v1`；网络失败提示代理设置 |
| 地址自动补全 | 「检测」/「获取可用模型」成功后，聊天需要的 base（补 `/v1`、Anthropic 去 `/v1`、Gemini 补 `/v1beta`）会写回输入框；点「应用设置」生效 |
| 代理 | 不动系统代理；需要时代理通过 `HTTPS_PROXY` + `NODE_OPTIONS=--use-env-proxy` 提供 |
| 自定义端点 | 供应商 id 不在 pi 目录 + 填 baseURL → 按 OpenAI 兼容协议工作（`test/pi-tools.mjs` 已断言） |
| 旧配置迁移 | 旧 `config.json` 只有 `provider/model/apiKey/baseUrl` → 自动变成一条 provider，历史 key 不丢 |


## 7.5 历史会话回放（M2.7）

- [ ] 进入存档聊几句 → 退出到主菜单 → 再进入同一存档
- [ ] 聊天窗自动显示上次的 user/assistant 消息（最多最近 40 条）
- [ ] 重新打开窗口后历史仍在；不会重复粘贴
- [ ] 换到另一个存档 → 聊天窗是另一个存档的历史，不串档
- [ ] 同存档重连 sidecar → 不重复追加历史
- [ ] 历史很长时只回放最近 40 条，单条超长会截断

## 7.6 chat-index 提问索引（M2.7）

- [ ] 聊天窗左侧出现索引列表（无标题）；没有提问时显示「暂无提问」
- [ ] 每次用户提问后，索引新增一行，显示提问的前 26 个字符
- [ ] 点击某行，右侧聊天记录滚动到该提问（目标行高亮）
- [ ] 鼠标 hover 某行时行背景高亮，并显示行尾「×」；移出后 × 隐藏
- [ ] 点击「×」删除该行以及这一问一答的聊天气泡/工具块
- [ ] 删除后索引为空时显示「暂无提问」；不会误删其他回合
- [ ] 删除后**重进存档**，被删的问答不再出现（会话文件已改写，见协议 A.7）
- [ ] 删除后继续提问，模型不再引用被删回合的内容
- [ ] 长提问以省略号截断，不覆盖「×」或滚动条
- [ ] 长列表可在索引内滚轮/拖动滚动，右侧细滚动条可见
- [ ] 进入存档回放历史时，索引同步重建
- [ ] 注意：当前删除只清理聊天窗显示，sidecar 的模型上下文不会重写

## 8. 知识库（M3）

> 离线测试：`cd sidecar && npm test`（含 kb 52 项 + kb-tools 35 项 + kb-session 10 项）。
> 实机重点核对“导出是否成功、数值/文本是否与游戏一致”。

### 8.1 导出与状态

| 检查项 | 期望 |
| --- | --- |
| 进入存档后 | 日志出现 `开始导出知识库` → `知识库已建立`，无 `知识库导出失败` |
| `ready.tools` / 日志 | 含 `kb_search` / `kb_get` / `kb_recipe_tree` |
| 缓存文件 | `%APPDATA%/icarus-ii/kb/ldb-v1-r2-<版本>-<语言>.json` 存在 |
| 二次进入同存档 | 直接 `知识库缓存已加载`，不再逐块导出 |
| 换语言 / 更新游戏 | 缓存 key 变化，自动重建 |
| 导出耗时 | 首次 < 2s；期间窗口不卡死（分帧） |

### 8.2 配方 / 科技

| # | 问法 | 期望 |
| --- | --- | --- |
| 1 | “造 10 个量子芯片需要什么？” | 调 `kb_recipe_tree`；直接输入、原料总量与游戏配方面板一致 |
| 2 | “每分钟 60 个磁线圈需要多少铁？” | `rate=60`；结果按每分钟口径 |
| 3 | “量子芯片是哪个科技解锁的？” | `kb_search`/`kb_get` 命中科技 1303，前置/解锁配方正确 |
| 4 | “电磁学需要什么前置？” | 科技前置与游戏科技树一致 |
| 5 | “大型采矿机的耗电/采矿周期？” | 建筑参数来自 `prefabDesc`；与建筑面板一致 |
| 6 | 副产物 | 精炼油等副产物按同名原料抵扣；剩余单列，不能把副产物算成净需求 |

### 8.3 提示 / 教程 / 指引（M3b）

| # | 问法 | 期望 |
| --- | --- | --- |
| 1 | “风力涡轮机怎么扩展电网？” | 命中 `prompt`/`tip`，内容与游戏顾问字幕/提示一致 |
| 2 | “低空飞行怎么解锁？” | 命中里程碑/科技/术语，不给未解锁的后期剧透 |
| 3 | “前情介绍讲什么？” | 命中 `tutorial#1` 正文，与游戏教程文本一致 |
| 4 | “我现在该做什么？” | 命中 Goal/里程碑，按当前阶段给建议 |
| 5 | 文本清洗 | 不出现 `$LKEY_`、`<color`、`\tag;`、`{$Text`；`{0}` 标注为模板 |
| 6 | 知识库未就绪时提问 | `kb_*` 返回 `kb_unavailable`，助手说明“正在建立”，不凭记忆编数值 |

### 8.4 降级

1. 停掉 sidecar / 断线后提问 → 工具失败，助手不假装成功。
2. 把缓存文件改坏 → 启动时重新导出，不崩。
3. 老版本 mod（没有 `game_export_prototypes`）→ `kb_unavailable`，`game_*` 仍可用。
