# 当前状态与下一步

> 更新时间：2026-10-25。M4（M4a–M4j + 写操作）已完成并通过实机验收；M5 离线实现完成，待实机验收。
> 本页是当前唯一的状态/计划入口；验收用的临时清单和提示词文档已移除。

## 1. 已完成

| 范围 | 状态 |
| --- | --- |
| M0 插件加载 / 窗口 / 握手 | 已完成并实机验证 |
| M1 sidecar + pi + DeepSeek | 已完成并实机跑通 |
| M2 六个只读工具 / 工具卡 / 会话 | 已完成 |
| M2.5 思考档位 / 折叠思考块与工具块 | 已完成；聊天窗档位调整为四档：关闭 / 低 / 高 / 极限 |
| 权限档位（只读 / 受限 / 接管） | 默认「受限」。已实现聊天窗「权限」下拉框 + 协议 `permissionLevel`；只读=所有写操作确认，受限=risk 1 直接放行、risk 2 确认，接管=全部自动放行；一次性令牌始终校验；待实机验收 |
| M2.6 多供应商 / 多模型 | 已完成 |
| M2.7 提问索引 / 删除上下文 | 已完成 |
| M3 游戏知识库 + `kb_*` + `kb_plan` | 已完成并通过验收 |
| M4a 建筑/进度/截图/标注 | 已完成并通过实机验收 |
| M4b/M4c 量化计算、区域产线 | 已完成并通过实机验收 |
| M4d `run_script` 沙箱 | 已完成并通过验收；默认只读，M5 增加受控写脚本（脚本级确认 + 租约） |
| M4g/M4h Wiki、蓝图仓库、`blueprint_save` | 已完成并通过实机验收 |
| M4i 蓝图卡片 | 已完成并通过实机验收 |
| M4j `blueprint_generate` | 已完成并通过实机验收 |
| M4 写操作（确认/令牌/焦点/标记/面板/研究/撤销） | 已完成并通过实机验收 |
| M5 前置：沙盒门禁基础设施 | 已完成（`save.bind.sandbox` + 双重拒绝 + UI 提醒 + 离线测试）；沙盒工具未实现 |
| M5 前置：读侧优化 | 已实现（`game_get_entity` 的 `fields`/`entityIds`；`game_get_planet` 的矿脉群/逐矿脉节点）；离线构建/测试通过，待实机冒烟 |
| M5 前置：丢弃工具 | 已实现 `game_drop_item`（`game.drop.v1`，risk 2，落地可拾回、不进入撤销栈）；已修复 system prompt 仍称“只能读”导致模型拒调；代理实测会调用并进入确认页，待实机确认 |
| M5 前置：生产配方写工具 | 已实现 `game_set_recipe` / `game_set_assembler_batch`（`game.build.v1`，risk 2，批量一次确认/一层撤销，先退料）；离线测试通过，待实机 |
| M5 受控写脚本 | `run_script` 默认只读；脚本含字面量写工具时先弹一次脚本级确认，mod 发脚本租约（token+脚本哈希+写工具名+最多 10000 次），结束立即释放；动态拼写写工具名拒绝；离线测试通过，待实机 |
| M5 存储/生产写工具 | 已实现存储 filter/limit/move + 背包存入/取出、`game_feed_input`、`game_take_output`；存储/投料可撤销，取成品不可撤销（栈顶标记）。按 design §9.1 未实现 `game_dismantle`；离线测试通过，待实机 |
| M5 实体链接 | 富文本 `dsp://entity/planet/<id>` / `dsp://entity/building/<planet>/<building>` / `dsp://entity/vein/<planet>/<vein>` 已支持点击聚焦；800000–899999 段；与卡片共用聚焦逻辑 |
| M5 link 卡片持久化 | sidecar 按「提问 + 回答」写入 `link-cards.jsonl`，历史 `linkCards` 回放（含矿脉卡）；建筑卡统计、星球备忘录、图标解析已接 |
| M5 link 卡片详情 | 聊天窗每次打开刷新一次卡片快照；建筑按装配/电力/采矿/传送带/分拣/分流/监测/喷涂/存储/配送/物流站/武器/护盾/弹射/发射井等类型生成描述与角标 |
| M5 link 卡片 | 运行期接 `cards-group` 三模板；整卡聚焦、`定位/已定位` 双向连线；多行 ContentSizeFitter；建筑统计/星球备忘录与图标已接 |
| M5 游戏目标 | D3 改为 B：注入原生 GoalProto/GoalSystem，复用左侧 UIGoalPanel；`game_todo_templates/list/add/remove`，触发器复用原生 GoalDeterminator；定义存 Global TodoModule JSON。2026-10-27 修正即时显示：Level=Off、插队首、补跑 `DetermineOnPatch/OnLoad`；玩家选“自由探索”时工具返回 `goalPanelOff`/`notice`，由模型提醒开目标显示 |
| M5 矿脉卡片 | 复用建筑卡模板：矿脉图标/#id、标题、储量+经纬度、星系/行星、定位；支持 `dsp://entity/vein/<planet>/<vein>` 与历史持久化 |
| M5 方位指示（tether） | 已实现 `game_tether_add/list/clear`，最多 20 个目标；本星球多建筑多线，跨行星原生导航一次一个、落地后切换；换档/断线清理；离线测试通过，待实机 |
| Game Menu 聊天入口 | 右下角 Game Menu 的截图键正上方新增聊天入口按钮（`button-agent.prefab` 随 UI bundle 分发）；Game Menu 是 `DSPGame.unity` 里的场景对象，运行期按 `UIGameMenu.buttonS` 锚定并绑定打开聊天窗；离线构建通过，待实机验收 |
| 首次配置引导 | 取消聊天输入框的密钥录入态；`missing_key` / `invalid_key` / `unreachable` 时在聊天窗提示点击右上角设置按钮，由设置页「模型」页提交 Key。未配置时发送提问会被拦截并再次指向设置页，不再把输入当成 Key |
| 输入工具条修复 | 图标 / 复选框 / 文字颜色改为按点击工具条前的光标/选区插入：工具栏按钮带 Unity `Button`，点击会让 InputField 失焦并把内部 caret 清 0；现在运行期记住最后聚焦时的选区并在插入前恢复 |

### M4 收尾修复

- 确认页第一次打开时 `AskPanel.Update()` 越界：已修复。
- 选项 hover/选中/复选框：全部由 prefab 原生 `Toggle` / `UIToggle` 参数驱动，
  没有自定义按钮动画脚本。
- `explain` 内容垂直居中；第一题「跳过」居中，最后一题仍显示「下一个」。
- 建筑/玩家位置统一为 `location`：星系/行星/经纬度，不再输出会随行星转动变化的 x/y/z。
- `game_focus_camera`：当前行星/建筑先进行星视图，再用游戏原生
  `MoveToViewTargetTwoStep` 聚焦；镜头在星球外，退出行星视图后注视结束。
- `game_set_research`：拒绝已完成科技；接受前置满足、尚未完成的科技；
  通过队列 + `ResumeTechQueue` 切换，不再反射写 `currentTech`。

## 2. 本地验证结果

- `scripts/build-mod.sh`：0 warning / 0 error。
- `dotnet run --project tests/ProtocolTests`：通过（新增 permissionLevel / AutoApprovesWrite 断言）。
- `cd sidecar && npm test`：退出码 0（新增 config 权限往返、确认流程不隐藏写工具、脚本级确认按最高 risk 计算、本地蓝图写入确认）。
- UI AssetBundle 与 `mod/unity/Prefab/ChatWindow.prefab`、`button-agent.prefab`（含图标）快照已同步；
  prefab 已含 `permission-group/permission-level` 与四档思考下拉框。
- `scripts/package.sh` 成功；Steam 安装目录通过 SHA-256 校验更新。
- sidecar `config.json` 的 provider/model/key 迁移自检通过；玩家配置未被覆盖。
- M4 实机验收：用户确认全部通过（M4a–M4j + 写操作）。
- M5：离线构建/测试通过；实机验收待做。

## 3. 已知限制

这些不是 M4 验收阻塞项，后续按需处理：

- 旧存档若已经存在超长 marker `word`，可能在 `MarkerSanitizer` 运行前崩溃；
  优先载入该标记之前的自动存档。
- 当前行星/建筑聚焦依赖原生行星视图；退出视图或撤销会结束注视。远程行星仍用星图，
  无法在星图上进入地表视角。
- 连续写操作只有单层撤销，后一次写操作会覆盖“上一步聚焦”的撤销项。
- 蓝图生成的多产物/多副产物、部分三输入配方仍受 dsp-calc 布局器限制；
  单张混带总线最多 3 条蓝带，高产量会拆成多张。
- `game_analyze_area` 的范围仍是行星局部 XZ 平面上的球形切分，靠近极点时与游戏网格
  有偏差；输出坐标已改用经纬度。
- `run_script` 默认只读；含字面量写工具的脚本按最高 risk 走脚本级确认（只读下必弹，受限下纯 risk 1 可自动放行，接管下自动同意），执行期凭 mod 脚本租约最多写 10000 次；动态拼写工具名会被拒绝。
- 「接管」/「受限」会自动放行部分写操作；一次性令牌 + 工具白名单仍然生效，但被放行的操作没有人工复核。
  只在玩家明确选择时使用，切换后聊天窗会给出提示。
- 上下文压缩后的历史回合删除仍受 pi 会话 entry 类型限制，见
  [context-compression.md](context-compression.md)。

## 4. 接下来做什么

M4 已收口，下一步从“继续验收”转为“发布与后续规划”：

1. **M5 实机验收**：新档/旧档各启动一次，逐项确认丢弃、配方、存储、投料、取成品、tether、
   link 卡片、矿脉卡、脚本租约与撤销栈。
2. **发布收尾**：确认 0.4.0 的 sidecar/UI/Thunderstore 产物与 Steam 安装一致。
3. **清理已知限制**：优先上下文压缩后的历史删除、建筑聚焦的单层撤销覆盖、
   高产量/多副产物蓝图。
4. **M5 之后**：合成器队列、仪表盘等候选能力仍需逐条设计（D1–D5 已拍板）；
   见 [../design.md §20](../design.md#20-候选能力清单m5待实现)。

## 5. 文档说明

- 协议契约：[protocol-v1.md](protocol-v1.md)
- 架构：[../design.md](../design.md)
- KB： [kb-v1.md](kb-v1.md)
- 蓝图生成：[blueprint-planner.md](blueprint-planner.md)
- 建筑字段：[entity-fields.md](entity-fields.md)
- 发布：[publishing.md](publishing.md)
- B站安装/上手视频稿：[bilibili-video-script.md](bilibili-video-script.md)
