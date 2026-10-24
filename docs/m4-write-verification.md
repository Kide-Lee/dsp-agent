# M4 写操作验收：确认、令牌、写工具与撤销

> 代码已落地：`game.write.v1` + `confirm_request/confirm_result` + 一次性令牌 +
> `game_focus_camera` / `game_place_marker` / `game_open_panel` / `game_set_research`
> + `game_undo_last_write`。以下**实机项**需要在游戏里逐条过；离线项已由测试覆盖。

## 1. 能力与工具目录

- [ ] 进存档后 `hello.capabilities` 含 `game.write.v1`。
- [ ] `ready.payload.tools` 出现 5 个写工具；旧 mod（不声明能力）不出现。
- [ ] 未声明能力时模型完全看不到写工具。

## 2. 确认 UI

- [ ] 模型调用 `game_open_panel` 时，`choose/question` 显示标题，
      `choose/explain` 显示详情。
- [ ] 右下角只显示红色「拒绝」和蓝色「同意」；跳过/返回隐藏。
- [ ] 红色拒绝按钮 hover 为警告红、按下变暗；蓝色同意按钮可点击。
- [ ] 点「拒绝」：sidecar 收到 `confirm_result{decision:"deny"}`，模型看到
      `confirmation_denied`，游戏**无任何副作用**。
- [ ] 关闭聊天窗 / 按 Esc：按拒绝处理，不执行。
- [ ] 60 秒不回答：UI 自动收起，按拒绝处理。

## 3. 令牌门禁

- [x] sidecar 在确认前只发 `confirm_request`，不发 `tool_call`（离线测试）。
- [x] 允许后才发 `tool_call`，且带 `confirmToken/confirmArgsHash`（离线测试）。
- [ ] 手工重放同一个 `tool_call`：mod 返回 `confirmation_invalid`，不执行第二次。
- [ ] 修改参数后重放：argsHash 不匹配，返回 `confirmation_invalid`。
- [ ] 60 秒后重放：返回 `confirmation_expired`。

## 4. 写工具

- [ ] `game_focus_camera`：行星/星系 id 打开星图并定位；建筑 id 移动相机目标；
      撤销后回到原视角/关闭星图。
- [ ] `game_place_marker`：标记出现在指定经纬度，文字正确；撤销后标记消失。
- [ ] `game_open_panel`：inventory/tech/starmap/marker/mecha/replicator/statistics/blueprint
      都能打开；撤销后关闭**本次打开**的面板（原本已开的不要关）。
- [ ] `game_set_research`：当前研究切换；拒绝时研究不变；撤销恢复原研究。
- [ ] 写工具执行失败时返回结构化错误，不留下半完成状态。

## 5. 撤销与生命周期

- [ ] 连续执行两次写操作，`game_undo_last_write` 只撤销最近一次。
- [ ] 没有可撤销项时返回明确错误。
- [ ] 换档/断线后撤销栈清空，不能跨存档撤销。
- [ ] 撤销本身不弹确认（risk 0），但只能撤销 mod 记录的一步。

## 6. 回归

- [ ] 原有只读工具、ask_user、截图、蓝图卡片仍正常。
- [ ] `run_script` 的 `dsp.call` 仍**看不到写工具**（脚本只读）。
- [ ] 关闭确认 UI 后 input 区恢复，输出区高度正确。
