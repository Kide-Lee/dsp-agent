# dsp-handcraft 试做与回退

## 目的

按 `docs/plans/2026-11-02-蓝图生成重写方案.md` 之后的决定，复刻 dsp-handcraft 的产线蓝图生成器：
同一目标产物、同一产量下，生成接近它的单张复杂蓝图，并返回科技、用电、原料与警告。

## 做法

- 把 dsp-handcraft 3.1.3 的 `src/`、`data/`、`web/src/worker.js` vendor 到 `sidecar/vendor/dsp-handcraft/`（GPL-3.0）；
- 写 `sidecar/src/blueprint/handcraft.ts` 做 Node 适配（`self`/`postMessage` 壳），单线程跑它的 `planLine`；
- `blueprint_generate` 只走 handcraft，参数、报告、警告都映射它的结果；
- 仓库协议整体改为 GPL-3.0。

## 结果

- 小型产线（电磁涡轮 60/min、量子芯片 30/min）能在秒级出图，游戏内粘贴验证通过；
- 复杂产线（引力矩阵 60/min）的搜索耗时以小时计，实机试做用了一整晚；
- 单张复杂蓝图的目标没有达到，生成耗时不可接受。

## 决定

- 放弃照搬 dsp-handcraft；
- 蓝图生成回退到上一个稳定版本（提交 `5cf7d01`）：总线布局 + 工厂布局的 `auto` 策略；
- 仓库协议回退 MIT；
- `dsp-handcraft/` 继续只作只读参考，已更新到上游最新。

## 备份

当前 handcraft 版本完整备份在 `dist/handcraft-backup-20261103/`（`dist/` 被 gitignore，不进仓库）：

- `worktree.diff`：相对 `5cf7d01` 的全部改动；
- `handcraft-code.tar.gz`：vendor 的上游代码与新增的 handcraft 适配文件。

恢复办法：`git checkout 5cf7d01`（或当前 HEAD）后应用 `worktree.diff`，再解包 `handcraft-code.tar.gz` 到仓库根。
