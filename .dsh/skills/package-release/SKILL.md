---
name: package-release
description: 构建并打包 IcarusII 发布产物到 dist/pkg（r2modman 配置档/手动安装），可选再打 Thunderstore zip，能安全复用已有产物，并在每次打包前强制检查依赖版本。**发布产物、出包、重新打包、上传 Thunderstore 之前必须使用本技能**——不要直接跑 scripts/package.sh，也不要手搓 dist/pkg。
---

# 打包 IcarusII 发布版

**任何产出发布物的动作都必须走本技能**：`dist/pkg`、Thunderstore zip、
给 `update-steam-mod` 用的包，也包括"只是重打一下"。直接调用 `scripts/package.sh`
会跳过依赖版本检查，等于允许带着过期依赖发布。

```bash
.dsh/skills/package-release/scripts/package-release.sh                    # 完整包
.dsh/skills/package-release/scripts/package-release.sh --skip-sidecar     # sidecar 未变，省时间
.dsh/skills/package-release/scripts/package-release.sh --with-thunderstore
```

产物：`dist/pkg/IcarusII/`（`update-steam-mod` 和 `install.sh` 使用的目录结构；前者默认写 r2modman 配置档），加 `--with-thunderstore` 时另有 `dist/IcarusII-<version>.zip`。

## 依赖版本（每次打包都会检查）

`package-release.sh` 起步先跑 `scripts/check-deps.mjs --check`：

```bash
node scripts/check-deps.mjs            # 只检查；有同大版本可更新项时退出码 1
node scripts/check-deps.mjs --update   # 升到同大版本最新
node scripts/check-deps.mjs --update --major   # 连大版本一起升（人工确认后）
```

- 检查范围：`sidecar/package.json` 的全部依赖、`scripts/build-sidecar.sh` 里的
  `NODE_VERSION` / `ESBUILD_VERSION` / `POSTJECT_VERSION`、`scripts/fetch-deps.sh` 里的 `BEPINEX_VERSION`。
- **同大版本有新版就会阻塞打包**。按提示更新，然后重跑 `verify-changes` 再打包；
  或者直接 `--update-deps`：脚本会更新、跑 `npm run typecheck && npm test`，通过后才继续打包。
- **跨大版本不会阻塞**，只打印 `仅大版本可用`，由人决定：
  - `@types/node` 跟着 `NODE_VERSION` 的大版本走，换 Node 大版本时一起换。
  - Node 运行时换大版本要实机验证（见 `docs/plans/` 里 Node 升级验证清单），不能只靠单测。
- `BepInEx` 跟的是**社区包版本**，不是上游最新：每次检查都会向 Thunderstore 查
  `xiaoye97-BepInEx` 的最新版本（玩家通过 r2modman 装的就是它）。编译目标**高于**社区版本会
  阻塞打包（会刷 `targets a wrong version of BepInEx` 警告），社区发新版则只提示、是否跟进由人
  决定。改它要同步 `THIRD-PARTY.md`、`packaging/thunderstore/manifest.json` 与 README。
- `--skip-dep-check` 只用于断网排障，不允许出现在正常发布流程里。
- `dsp-handcraft`（蓝图生成器的参考源码，GPL-3.0）只比对本地 `dsp-handcraft/` 与上游 `master`：
  没部署时提示克隆命令，上游有新提交时提示 `git -C dsp-handcraft pull --ff-only`。
  它不参与构建、不进发布产物，**不阻塞打包**，脚本也不自动拉取；更新后不要复制其代码。

## `package.sh` 做了什么

1. `scripts/build-mod.sh` → 复制 `IcarusII.dll` + `.pdb`。
2. 当 `dist/icarus-ii-sidecar.exe` 存在**且** `SKIP_SIDECAR=1` 时直接复用；否则跑 `scripts/build-sidecar.sh windows-x64`。
3. 存在 `dsp-game/Build/IcarusIIBundle/icarusii-ui` 就复制 —— 缺 resource bundle 只告警，游戏会回退到代码构建的界面。
4. 复制 `scripts/install.sh`、Thunderstore 的 README、`docs/protocol-v1.md`、`LICENSE`、`THIRD-PARTY.md`。

## 参数

| 参数 | 作用 |
| --- | --- |
| `--skip-sidecar` | `SKIP_SIDECAR=1`：复用已有的 exe，不重新构建 |
| `--with-thunderstore` | 额外跑 `scripts/package-thunderstore.sh`（复用本次构建） |
| `--skip-ui` | Thunderstore zip 里不含 `ui/icarusii-ui`（不二次分发游戏美术资源） |
| `--skip-build` | 给 Thunderstore 阶段传 `SKIP_BUILD=1` |
| `--update-deps` | 依赖过期时自动更新，并跑 sidecar typecheck + 测试 |
| `--skip-dep-check` | 跳过依赖检查（仅断网排障用） |

## 打包前

- 按改动范围跑 `verify-changes` 技能；不要打包一个测试失败的树。
- prefab/UI 改动要**先**跑 `rebuild-ui-bundle` 技能，否则包会悄悄带上旧窗口。
- 版本号规则见下一节；发布前核对。
- 更新日志必须按 git history 重写，见下下节。

## 版本号

格式 `大.中.小`：

- **大版本号、中版本号由用户决定**，没明确指定时不要自行提升。
- **小版本号 = 发布准备时的 `git rev-list --count HEAD`**（仓库提交总数，只增不减）。
- 以下位置必须一致：`packaging/thunderstore/manifest.json` 的 `version_number`、
  `mod/Directory.Build.props` 的 `ModVersion`、`mod/src/IcarusII/Plugin.cs` 的
  `PluginVersion`、sidecar 的 `SERVER_VERSION` 与 `sidecar/package.json`。
- 版本号写进提交后提交数会再 +1，这是预期行为：小版本号取的是**写入前**的数量。

## 更新日志

发布前重写 `packaging/thunderstore/CHANGELOG.md` 的目标版本条目：

1. 找基线：`git log --oneline --grep '^release:'`，取上一个 `release: <version>` 提交；
   用 `git log <基线>..HEAD` 看本次范围。
2. 只写玩家能感知的变化（feat / fix / style / perf）；docs、chore、refactor、测试等内部提交不写。
3. **不写中间版本或开发迭代**（M0–M5、dev 构建、临时版本号）；把它们合并成一条
   面向该版本的完整说明。
4. 检查历史：已发布但旧条目漏掉的功能/修复，补进对应版本。
5. 写玩家能感知的结果，不要复述提交信息（写“左栏新增会话列表”，不写“feat(ui): …”）。

## 打包后

- 部署：用 `update-steam-mod` 技能（默认写 r2modman 配置档；手动安装加 `--game-dir`；先 `--dry-run`）。提前退出游戏。
- Thunderstore 上传：见 `docs/publishing.md`；不要手改 `dist/`。
- 脚本会打印文件清单和总大小；发布前确认 sidecar exe 与 UI bundle 都在。
