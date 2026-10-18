# M4a 验收：建筑 / 进度 / 截图

> 对应协议附录 B.1 的三组新能力（`game.entity.v1` / `game.progress.v1` / `game.view.v1`）。
> 离线部分已由 `sidecar/test/tools-view.mjs`（33 项）、sidecar 全套回归、
> `ProtocolTests`（200 项断言）覆盖；本文只列**必须在游戏里看**的项目。

---

## 1. 能力协商

- [ ] 进存档后，sidecar 日志里 `hello` 的 `capabilities` 含：
  `game.read.v1`、`game.entity.v1`、`game.progress.v1`、`game.view.v1`。
- [ ] `ready.payload.tools` 里有 10 个 `game_*` 工具 + 3 个 `kb_*`。
- [ ] 回退验证：把 mod 换回旧版本（只声明 `game.read.v1`），sidecar 不应再广告新工具，
      `/model` 列表仍能正常建立会话。

## 2. 截图（`game_capture_view`）

- [ ] 问「看看我现在这片产线摆得对不对」，模型会调用 `game_capture_view`。
- [ ] 聊天窗出现「截图」工具卡；耗时 < 1s（1080p、JPEG q80）。
- [ ] **工具卡里出现截图缩略图**，且自动展开；收起工具块后图片一起隐藏。
- [ ] 缩略图等比、不变形；横向超宽时左右留白而不是拉伸。
- [ ] **截图中没有 UI**（没有聊天窗、没有工具栏、没有小地图）。
- [ ] 模型能描述画面内容（说明图片真的进了上下文，而不是只有文本）。
- [ ] `visibleEntities` 的 `x/y` 与图片里建筑位置大致对齐
      （拿一个靠画面上方的建筑对照）。
- [ ] 连续截图 5 次，帧率无明显下降、内存不持续增长。
- [ ] 清空对话 / 聊天记录滚动裁剪后，内存回落（纹理随节点销毁释放）。

### 已知限制（本条验收不要求）

- 没有点击放大；缩略图就是最终大小。
- 未实现标注（编号框 / 图例）与镜头控制（`game_focus_camera`）。

## 3. 建筑详情（`game_get_entity`）

- [ ] 从 `game_get_entities` 拿一个组装机 id，再查详情：配方名、`craftsPerMin`、
      `power.requiredKw` 与游戏面板一致（误差 < 1%）。
- [ ] 查询分拣器：`pickTargetName` / `insertTargetName` 与实际连接一致。
- [ ] 查询物流塔：`station.storage` 的前几项与塔面板一致。
- [ ] **回收检测**：拆掉一台机器后立刻用旧 id 查，应返回 `reason=empty_slot`；
      在同位置重建后旧 id 若被复用，带 `expectProtoId` 查询应返回 `stale=true`。
- [ ] 远程行星（未加载工厂）查询返回 `available=false, reason=factory_not_loaded`，不是报错。

## 4. 建筑列表（`game_get_entities`）

- [ ] 不传过滤：`total` 与游戏内建筑数同量级；分页 `offset` 连续翻到底不重不漏。
- [ ] `protoName=组装机` 过滤生效；`recipeId` 过滤只返回该配方的机器。
- [ ] `hasMore=true` 时结果文本提示了下一个 `offset`。

## 5. 进度阶段（`game_get_progress`）

- [ ] `signals` 里的布尔值与游戏实际进度一致（例如未解锁物流时不出现
      `logisticsSystem=true`）。
- [ ] `stage` 与玩家自我认知一致：前期 / 行星内物流 / 星际物流 / 戴森球。
- [ ] `recentMilestones` 的时间顺序正确（最近的在前）。
- [ ] 研究队列与游戏面板一致。

## 6. 回归

- [ ] 原有 6 个只读工具仍正常（尤其 `game_get_production` 与 `game_get_power`）。
- [ ] 知识库导出仍成功（`kb_search` 可用）。
- [ ] 换档后新工具仍绑定到当前连接（不串存档）。

---

## 已知问题 / 后续

| 项 | 说明 |
| --- | --- |
| 截图标注 | 未做；当前只给坐标，不给编号框 |
| 截图压缩 | 固定 JPEG q80；未做"超过 2 MiB 自动降质" |
| `game_get_entities` 性能 | 每次全表扫描；万级实体约几 ms，实测后再决定是否加游标 |
| 图片尺寸 | 卡片固定 220px 高、等比缩放；不做点击放大 |
