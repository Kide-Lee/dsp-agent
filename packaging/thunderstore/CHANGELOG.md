# 更新日志

## 0.2.0

从「能聊」到「能查、能看、能算、能规划」。

**新工具**

- 视觉：`game_capture_view` 截图进模型上下文，聊天窗显示缩略图
- 建筑：`game_get_entity` / `game_get_entities`（配方、输入输出槽、供电、物流塔槽位、
  传送带线路与货物、分拣器手上物品；其余建筑走通用组件快照）
- 进度：`game_get_progress`
- 量化：`kb_plan` 按目标产量算机器数 / 电力 / 传送带
- 产线：`game_analyze_area`（装机产能、耗电、物流设施、电网）、`game_render_layout` 字符布局图
- 跨行星：`game_get_planets`（按宇宙探索等级披露矿脉）、`game_get_logistics`（全银河物流塔供需）
- 蓝图：`blueprint_search` / `blueprint_info` / `blueprint_save`（在线检索、格式体检、直接存到游戏蓝图目录）、
  `game_copy_blueprint`（把现有建筑存成蓝图）
- 知识：`kb_search` / `kb_get` / `kb_recipe_tree`、中英 `wiki_search` / `wiki_page`
- 脚本：`run_script`（QuickJS-WASM 只读沙箱，`dsp.call` 复用同一工具注册表）

**界面**

- 工具卡支持图片缩略图；`kb_*` / `run_script` 也会显示工具卡（`tool_activity`）
- 聊天索引的删除按钮现在会同步改写会话文件，重进存档不再出现

**修复**

- 产线统计为空时回退装机产能，不再误报“没有产线”
- `run_script` 中 `dsp.call` 被 try/catch 吞掉时明确报错，而不是返回假数据


## 0.1.0

首个公开版本。

**功能**

- 游戏内聊天终端：`R` 开关，流式回复，可回车发送 / `Ctrl+Enter` 换行
- 读取当前存档状态作为上下文，按存档绑定对话历史
- 在玩家确认之后执行游戏内操作
- 设置窗口注入「模型」页签：提供商 / 模型 / API Key / API 地址
- 输入工具条：游戏图标、复选框、文字颜色（复用游戏原生弹窗）
- 界面走游戏原生 prefab（AssetBundle），可回退到代码构建

**构建**

- 编译目标锁定 BepInEx 5.4.17（Thunderstore 社区包同款），避免 5.4.17 环境下
  BepInEx 打印 "plugin targets a wrong version"；5.4.23.5 运行时同样验证通过

**修复**

- 右上角 `⚙` 按钮点击无效：它其实是原生窗口的「功能菜单」按钮，
  真正收点击的是子节点 `menubtn` 的 `UIButton`。改为接到该 `UIButton` 上，
  并摘掉遗留的 `UIGenericMenuButton`（它会把空菜单当成设置、并每帧覆盖提示文案）
- 按钮悬停提示被聊天窗遮挡、只显示半截：把游戏原生提示层（按钮/物品/弹出菜单）
  抬到聊天窗画布之上
- 「模型」页签关闭再打开后标题被本地化改回模板页（克隆体上的 `Localizer` 所致）
- 设置窗口销毁时 `revertButtons` 可能越界（缺模板时也补齐数组长度）
