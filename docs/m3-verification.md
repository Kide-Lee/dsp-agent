# M3 实机验收清单（知识库）

> 离线回归：`cd sidecar && npm test`（含 `test/kb.mjs` 52 项、`test/kb-tools.mjs` 35 项、`test/kb-session.mjs` 10 项）。
> mod 编译：`scripts/build-mod.sh`。
> 完整逐条步骤见 [m2-verification.md](m2-verification.md) §8；本文件用于快速验收。

## 0. 预期规模（0.10.30.3100 参考）

| 数据 | 数量 |
| --- | --- |
| 物品 | 175 |
| 配方 | 162 |
| 科技 | 315 |
| 建筑模型 | 664 |
| 矿脉 / 信号 | 14 / 40 |
| 顾问提示 / 教程 | 100 / 22 |
| 里程碑 / 指引 | 42 / 148 |
| 控制提示 / 成就 | 28 / 128 |
| 里程碑日志 / 术语 | 42 / 181 |

文本实际入库约 690 条（完全为空的条目会被过滤）；全量 JSON 约 750KB；实际数量随游戏版本变化，以日志里的 `知识库已建立` 计数为准。

## 1. 导出

- [ ] 进入存档后日志出现 `开始导出知识库` → `知识库已建立`
- [ ] `ready.tools` / 日志含 `kb_search`、`kb_get`、`kb_recipe_tree`
- [ ] 缓存文件存在：`%APPDATA%/icarus-ii/kb/ldb-v1-r2-<版本>-<语言>.json`
- [ ] 二次进入同存档只看到 `知识库缓存已加载`，不重复导出
- [ ] 换语言/更新游戏后自动重建
- [ ] 首次导出 < 2s，窗口不卡顿（分帧）
- [ ] 导出失败时 `kb_*` 返回 `kb_unavailable`，助手说明“正在建立”，不凭记忆编数值

## 2. 配方 / 科技

- [ ] “造 10 个量子芯片需要什么？”→ `kb_recipe_tree`，直接输入/原料与配方面板一致
- [ ] “每分钟 60 个磁线圈需要多少铁？”→ `rate=60`，按每分钟口径
- [ ] “量子芯片是哪个科技解锁的？”→ 科技 1303，前置/解锁配方正确
- [ ] “电磁学需要什么前置？”→ 与科技树一致
- [ ] “大型采矿机的耗电/采矿周期？”→ `building` 参数与建筑面板一致
- [ ] 副产物（精炼油等）按同名原料抵扣；剩余单列，不把副产物算成净需求

## 3. 提示 / 教程 / 指引（M3b）

- [ ] “风力涡轮机怎么扩展电网？”→ 命中 `prompt` / `tip`
- [ ] “低空飞行怎么解锁？”→ 命中里程碑/科技/术语；默认 `spoiler=low`，剧情教程需显式 `story`
- [ ] “前情介绍讲什么？”→ 命中 `tutorial#1` 正文
- [ ] “我现在该做什么？”→ 命中 Goal/里程碑
- [ ] 文本不出现 `$LKEY_`、`<color`、`\tag;`、`{$Text`；`{0}` 标注为模板
- [ ] 知识库未就绪时提问 → `kb_unavailable`，助手不假装成功

## 4. 离线自查

```bash
cd sidecar && npm test
cd .. && dotnet run --project tests/ProtocolTests
scripts/build-mod.sh
```

可选：用本地游戏资源生成全量 fixture，人工抽查：

```bash
PYTHONPATH=.scratch/pylib python3 tools/build-kb.py \
  --prototypes dsp-game/Assets/Resources/Prototypes \
  --locale-dir dsp-game/Locale/2052 --tutorial-dir dsp-game/Wiki/Tutorial \
  --locale zh-CN --game-version 0.10.30.3100 --out .scratch/kb/kb-zh.json
```

schema、清洗规则与缓存细节见 [kb-v1.md](kb-v1.md)。
