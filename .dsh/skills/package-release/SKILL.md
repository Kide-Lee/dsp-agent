---
name: package-release
description: 构建并打包 IcarusII 发布产物到 dist/pkg（Steam/手动安装），可选再打 Thunderstore zip，能安全复用已有产物。准备发布或部署到 Steam 之前使用。
---

# 打包 IcarusII 发布版

```bash
.dsh/skills/package-release/scripts/package-release.sh                    # 完整包
.dsh/skills/package-release/scripts/package-release.sh --skip-sidecar     # sidecar 未变，省时间
.dsh/skills/package-release/scripts/package-release.sh --with-thunderstore
```

产物：`dist/pkg/IcarusII/`（`update-steam-mod` 和 `install.sh` 使用的目录结构），加 `--with-thunderstore` 时另有 `dist/IcarusII-<version>.zip`。

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

- 部署：用 `update-steam-mod` 技能（先 `--dry-run`）。提前退出游戏。
- Thunderstore 上传：见 `docs/publishing.md`；不要手改 `dist/`。
- 脚本会打印文件清单和总大小；发布前确认 sidecar exe 与 UI bundle 都在。
