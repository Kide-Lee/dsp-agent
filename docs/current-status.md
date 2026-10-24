# 当前状态与下一步

> 更新时间：2026-10-24。M4（M4a–M4j + 写操作）已完成并通过实机验收。
> 本页是当前唯一的状态/计划入口；验收用的临时清单和提示词文档已移除。

## 1. 已完成

| 范围 | 状态 |
| --- | --- |
| M0 插件加载 / 窗口 / 握手 | 已完成并实机验证 |
| M1 sidecar + pi + DeepSeek | 已完成并实机跑通 |
| M2 六个只读工具 / 工具卡 / 会话 | 已完成 |
| M2.5 思考档位 / 折叠思考块与工具块 | 已完成 |
| M2.6 多供应商 / 多模型 | 已完成 |
| M2.7 提问索引 / 删除上下文 | 已完成 |
| M3 游戏知识库 + `kb_*` + `kb_plan` | 已完成并通过验收 |
| M4a 建筑/进度/截图/标注 | 已完成并通过实机验收 |
| M4b/M4c 量化计算、区域产线 | 已完成并通过实机验收 |
| M4d `run_script` 只读沙箱 | 已完成并通过验收 |
| M4g/M4h Wiki、蓝图仓库、`blueprint_save` | 已完成并通过实机验收 |
| M4i 蓝图卡片 | 已完成并通过实机验收 |
| M4j `blueprint_generate` | 已完成并通过实机验收 |
| M4 写操作（确认/令牌/五个写工具/撤销） | 已完成并通过实机验收 |

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
- `game_place_marker`：简述超长会崩溃的问题已修复，并兼容旧存档清理。

## 2. 本地验证结果

- `scripts/build-mod.sh`：0 warning / 0 error。
- `dotnet run --project tests/ProtocolTests`：457 项断言全部通过。
- `cd sidecar && npm test`：退出码 0。
- UI AssetBundle 与 `mod/unity/Prefab/ChatWindow.prefab` 快照已同步。
- `scripts/package.sh` 成功；Steam 安装目录通过 SHA-256 校验更新。
- sidecar `config.json` 的 provider/model/key 迁移自检通过；玩家配置未被覆盖。
- M4 实机验收：用户确认全部通过（M4a–M4j + 写操作）。

## 3. 已知限制

这些不是 M4 验收阻塞项，后续按需处理：

- 旧存档若已经存在超长 marker `word`，可能在 `MarkerSanitizer` 运行前崩溃；
  优先载入该标记之前的自动存档。
- 当前行星/建筑聚焦依赖原生行星视图；退出视图或撤销会结束注视。远程行星仍用星图，
  无法在星图上进入地表视角。
- 连续写操作只有单层撤销，后一次写操作会覆盖“上一步聚焦”的撤销项。
- `game_place_marker` 的 `altitude` 默认 0 表示行星基准面；要贴建筑请传
  `location.altitude` 或先读建筑 `location`。
- 蓝图生成的多产物/多副产物、部分三输入配方仍受 dsp-calc 布局器限制；
  单张混带总线最多 3 条蓝带，高产量会拆成多张。
- `game_analyze_area` 的范围仍是行星局部 XZ 平面上的球形切分，靠近极点时与游戏网格
  有偏差；输出坐标已改用经纬度。
- `run_script` 只读；写工具不可通过脚本调用。
- 上下文压缩后的历史回合删除仍受 pi 会话 entry 类型限制，见
  [context-compression.md](context-compression.md)。

## 4. 接下来做什么

M4 已收口，下一步从“继续验收”转为“发布与后续规划”：

1. **发布收尾**：更新版本号、Thunderstore CHANGELOG/README，重新跑
   `scripts/build-sidecar.sh windows-x64` + `scripts/package.sh`，再做一次 Steam 安装校验。
2. **回归冒烟**：新档/旧档各启动一次，确认 R 开窗、握手、KB 导出、只读工具、
   截图、蓝图卡片和写操作确认页均正常。
3. **清理已知限制**：优先上下文压缩后的历史删除、建筑聚焦的单层撤销覆盖、
   高产量/多副产物蓝图。
4. **规划 M5**：目前未定；建议先收玩家反馈，再决定是扩展工具面、做任务/自动化，
   还是优化模型与上下文成本。

## 5. 文档说明

- 协议契约：[protocol-v1.md](protocol-v1.md)
- 架构：[../design.md](../design.md)
- KB： [kb-v1.md](kb-v1.md)
- 蓝图生成：[blueprint-planner.md](blueprint-planner.md)
- 建筑字段：[entity-fields.md](entity-fields.md)
- 发布：[publishing.md](publishing.md)
