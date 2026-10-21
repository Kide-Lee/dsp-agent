# M4i 验收：蓝图卡片

> 需求：AI 生成 / 下载 / 复制蓝图成功后，在对话末尾追加一张蓝图卡片；
> 卡片展示蓝图基本信息，可「查看详情」或「粘贴铺设」。
> 协议：`docs/protocol-v1.md` 附录 A.9；历史回放字段见 A.6。

## 1. 自动化

```bash
scripts/build-mod.sh
cd sidecar && npm test
dotnet run --project tests/ProtocolTests
```

覆盖点：

- sidecar 从蓝图工具结果提取卡片（本地工具 / mod 工具两种结构、失败/无关结果不发）；
- `blueprint_card` 在 `message{final}` 之后、`status{idle}` 之前发出；
- 卡片记录持久化，`save.bound.history` 的 assistant 消息带可选 `cards`；
- C# 侧容错解析（缺字段、null 载荷、历史里的 cards）。

## 2. 实机

前置：已进入存档，蓝图功能已解锁（`GameMain.history.blueprintLimit != 0`）。

| # | 操作 | 期望 |
| --- | --- | --- |
| 1 | 让 AI「生成一张电磁涡轮 20/min 的产线蓝图」 | 回答末尾出现卡片；来源角标「生成」 |
| 1b | 卡片出现的瞬间 | 输出区立刻贴底、滚动条下移，最新卡片完整露出，不用手动滚；关窗再开也停在最新一条 |
| 2 | 卡片信息 | 有图标、名称、文件名、产量、建筑数、占地、作者/版本 |
| 3 | 点「查看详情」 | 聊天窗关闭；蓝图浏览器打开并定位到蓝图目录；右侧 inspector 显示该文件 |
| 4 | 点「粘贴铺设」 | 进入蓝图粘贴预览，可正常旋转/放置 |
| 5 | 让 AI「把这张社区蓝图下载下来」 | 卡片来源角标「下载」，带许可 `CC BY-NC-SA 4.0` |
| 6 | 让 AI「把这片产线存成蓝图」 | 卡片来源角标「复制」，建筑数与占地来自实际复制结果 |
| 7 | 关闭聊天窗再打开 | 卡片仍在，按钮仍可点 |
| 8 | 重进存档 | 历史回放后，卡片重新出现在对应回答之后 |
| 9 | 删除蓝图文件后再点「查看详情」 | 聊天窗内提示「找不到蓝图文件：…」，不崩 |
| 10 | 未解锁蓝图时点「粘贴铺设」 | 提示「尚未解锁蓝图粘贴，或当前状态不允许粘贴」 |
| 11 | 删除该问答回合 | 卡片随回合一起消失；重新进入存档后不再回放 |

## 3. 实现说明

- 卡片是**数据驱动的消息卡片**，与工具块/思考块同一套做法：运行期按
  `blueprint_card` 载荷创建，不新增 prefab 静态节点，因此本需求**没有改
  `ChatWindow.prefab`、也不需要重打 UI AssetBundle**。
- 卡片插进回复的网格后，内容高度要等 Canvas 布局重建才变大。直接设
  `ScrollRect.verticalNormalizedPosition` 只能按旧高度算，滚动条会停在旧底部、
  新卡片压在视口下面；流式文本每帧都会重贴所以看不出来，卡片只贴一次就会露馅。
  因此插入/开窗后走 `ScrollToBottomAfterContentChange()`：先强制重建布局再贴底，
  接下来两帧继续贴底兜底；Tick 里每次内容渲染后也补两帧贴底，
  覆盖系统提示、非流式终稿这类“只改一次内容”的场景（折叠块展开/收起与索引跳转除外）。
- 卡片动作在 `mod/src/IcarusII/Game/BlueprintActions.cs`：
  - 详情：`UIGame.OpenBlueprintBrowser()` → `SetCurrentDirectory(dir)` →
    `UIBlueprintInspector.SetBlueprint(bp, path)` → `_Open()`；
  - 粘贴：`PlayerController.OpenBlueprintPasteMode(bp, path, !isGlobe)`，
    星图模式下先 `SyncBlueprintModeByPlanetMode()`。
- 路径优先用 `GameConfig.blueprintFolder + fileName` 重新拼：sidecar 可能跑在
  Linux 侧，绝对路径与游戏在 Wine 里看到的路径不一定一致。
- 一个 GameObject 只能挂一个 Graphic（uGUI 的 `DisallowMultipleComponent`）：
  在已有 Image 的节点上再加 Text 会被 Unity 拒绝并返回 null，紧接着就 NRE。
  来源角标的文字因此放在子节点 `label` 上；`UiFactory.AddLabel` 遇到这种节点会
  告警并自动改挂子节点，不会再静默返回 null。
- 卡片创建失败只丢这一张卡片：历史回放 / 消息分发 / 建窗补建都做了逐条容错，
  一张卡片不会再把整段历史（或整个窗口）打断。

## 4. 已知限制

- 卡片只在 sidecar 的持久化记录里；手工删除 sidecar 的
  `sessions/<saveKey>/blueprint-cards.jsonl` 后，历史回放不再有卡片（文本不受影响）。
- 同名蓝图会由工具自动加 `(2)`/`(3)`，卡片按实际文件名显示。
- 历史回放只保留最近的回答（与 `save.bound.history` 的 40 条上限一致）。
