# 第三方 mod 能力接入：实测与方案

- 日期：2026-10-30
- 数据来源：Thunderstore `dyson-sphere-program` 社区（`/c/dyson-sphere-program/api/v1/package/`，2026-10-30 抓取，734 个包），以及抽样 15 个热门 mod 的 DLL 元数据。
- 前置结论见 [computer-use-调研与引入研判.md](computer-use-调研与引入研判.md)：通用 computer use 不适合本项目。
- 本文只回答一件事：**要让 AI 调用玩家装的其他 mod，最佳方案是什么。**

## 0. 直接回答

**computer use 不是最佳方案，也不是次佳。** 对这个目标它是差解：它发现不了 mod 有什么功能，也够不到多数 mod 的实现方式。

最佳方案是**进程内能力桥**，按能力从强到弱分四层：

| 层 | 机制 | 覆盖 | 每个 mod 的成本 |
| --- | --- | --- | --- |
| L0 发现 | 枚举 `Chainloader.PluginInfos` + `ConfigFile` | 全部 BepInEx 插件 | 零 |
| L1 配置 | 读写 `ConfigEntryBase` | 抽样中 13/15 | 零 |
| L2 适配器 | 声明式 manifest 调公开方法 | 有 API 的少数 | 每个 mod 一份 manifest |
| L3 UI 树 | 控件路径 + 事件注入 | 有 UI 的 | 零（但不稳） |
| L4 SDK | mod 自己声明工具 | 未来的 mod | 作者负担 |
| L5 computer use | 像素点击 | 有常驻 UI 的 | 零（但最差） |

先把 L0+L1 做出来，就能回答「你装了什么、帮我开/关某个功能」这类问题，而且完全不碰别人的私有实现。

## 1. 问题拆解

「用别人的 mod」其实是三件不同的事，机制不能混：

1. **认识 mod 加的内容**（新物品、配方、建筑、星球）：这些会注入游戏的 `LDB`，我们现有的 `game_export_prototypes` + `kb_*` 本来就能读到，不需要调 mod 的代码。
2. **开关 mod 的功能**：QoL 类 mod 的绝大多数功能就是一个配置项。读写 `ConfigEntry` 即可，不需要 UI。
3. **调用 mod 的动作**：少数 mod 暴露了公开方法；更多的是 Harmony patch 改游戏行为，没有「执行一次」的入口。

computer use 只对第 3 类里有常驻 UI 的那一小撮有效，对第 1、2 类完全帮不上忙——而第 2 类恰恰是数量最大的。

## 2. Thunderstore 实测

### 2.1 社区规模

| 指标 | 值 |
| --- | --- |
| 包总数 | 734 |
| 2026 年仍有更新 | 262 |
| 已废弃 | 133 |
| 中位包体 | 0.09 MB |

### 2.2 热门 mod（按总下载量，已剔除 r2modman / UnityExplorer / GaleModManager 等工具，以及 BepInEx / CommonAPI / LDBTool / DSPModSave / Nebula API / CessilCells 等库）

| # | mod | 下载 | 最新版 | 更新 | 版本数 |
| --- | --- | --- | --- | --- | --- |
| 1 | Auxilaryfunction | 305019 | 3.1.1 | 2026-10-18 | 205 |
| 2 | NebulaMultiplayerMod | 293472 | 0.9.24 | 2026-10-14 | 58 |
| 3 | BulletTime | 274484 | 1.5.14 | 2026-09-21 | 45 |
| 4 | GalacticScale | 272312 | 2.80.5 | 2026-10-18 | 261 |
| 5 | Multfuntion_mod | 253350 | 4.0.8 | 2026-10-29 | 276 |
| 6 | UXAssist | 181238 | 1.6.2 | 2026-10-18 | 80 |
| 7 | BlueprintTweaks | 169748 | 1.6.13 | 2026-06-21 | 57 |
| 8 | MoreMegaStructure | 117670 | 2.0.1 | 2026-10-13 | 80 |
| 9 | SplitterOverBelt | 105358 | 1.1.7 | 2024-08-16 | 15 |
| 10 | NebulaCompatibilityAssist | 94948 | 0.5.1 | 2026-02-03 | 50 |
| 11 | GenesisBook | 92452 | 3.2.3 | 2026-03-09 | 97 |
| 12 | DeliverySlotsTweaks | 91005 | 1.5.19 | 2026-09-26 | 33 |
| 13 | CheatEnabler | 89183 | 2.4.7 | 2026-10-18 | 52 |
| 14 | ErrorAnalyzer | 87632 | 1.3.3 | 2025-10-23 | 11 |
| 15 | LSTM | 82701 | 0.9.5 | 2025-10-01 | 42 |
| 16 | MoreStatInfo | 81270 | 2.0.3 | 2026-02-01 | 65 |
| 17 | DSPOptimizations | 79901 | 1.1.19 | 2025-11-11 | 25 |
| 18 | DSPJapanesePlugin | 74307 | 1.2.16 | 2026-01-24 | 36 |
| 19 | DSPAutoSorter | 73274 | 1.2.12 | 2026-04-20 | 16 |
| 20 | DSPCalculator | 72376 | 0.5.25 | 2026-02-03 | 50 |

### 2.3 依赖枢纽

731 个非工具包中，依赖分布：

| 依赖 | 包数 | 性质 |
| --- | --- | --- |
| `xiaoye97-BepInEx` | 629 | 加载器 |
| `xiaoye97-LDBTool` | 82 | 内容注入 |
| `CommonAPI-CommonAPI` | 75 | 内容注入/自定义行星 |
| `CommonAPI-DSPModSave` | 44 | 存档扩展 |
| `nebula-NebulaMultiplayerModApi` | 21 | 联机 API |

这说明生态有「内容注入」的公共层，但**没有**「动作调用」的公共层：想调别人的功能，没有现成的总线。

### 2.4 抽样 15 个热门 mod 的 DLL 元数据

对 15 个热门 mod 的插件 DLL 做只读元数据扫描（`ConfigEntry` 字段数、Harmony 引用数、公开静态方法数）；Nebula 因包体 21MB 未抽样：

| 程序集 | ConfigEntry 字段 | Harmony 引用 | 可调用 API |
| --- | --- | --- | --- |
| UXAssist | 114 | 16 | `RegisterExporter` / `RegisterImporter` |
| Multifunction_mod | 110 | 15 | 少量公开方法 |
| Auxilaryfunction | 72 | 6 | 多数是 patch 方法 |
| CheatEnabler | 41 | 14 | 少量 |
| LSTM | 25 | 8 | `OpenBalanceWindow` / `OpenStationWindow` |
| BlueprintTweaks | 23 | 14 | patch 为主 |
| PlanetFinder | 23 | 5 | UI 构建 |
| BulletTime | 16 | 14 | 少量 |
| DeliverySlotsTweaks | 13 | 14 | patch 为主 |
| DSPCalculator | 11 | 6 | `API.OpenNewWindowWithDemands` |
| DSPAutoSorter | 8 | 5 | patch 为主 |
| FactoryLocator | 3 | 10 | `MainLogic.GetPowerNetworkId` 等 |
| MoreStatInfo | 2 | 3 | 本地化辅助 |
| GalacticScale | 0 | 20 | 大量内部方法，无稳定 API |
| SplitterOverBelt | 0 | 6 | `ConnectBelts` / `DeleteConfusedBelts` |

### 2.5 从数据里读出的四件事

1. **配置面极大。** 15 个插件里 13 个声明了 `ConfigEntry`，最多 114 个。BepInEx 的 `ConfigEntryBase` 还带 `Description`（`ConfigDescription.Description`），可以直接给模型当语义说明。
2. **行为以 patch 为主。** 15 个全部引用 Harmony。很多功能没有「执行一次」的入口，是「开启后持续生效」——这类只能靠配置，不能靠「调用」。
3. **真正的 API 是少数。** 抽样里只有 5 个左右有可调用的公开方法，而且命名、参数、语义各不相同。
4. **版本迭代极快。** Auxilaryfunction 205 个版本、Multifunction 276 个、GalacticScale 261 个。任何按名字反射调用内部方法的方案，都必须在版本不匹配时直接禁用，不能猜。

## 3. 为什么 computer use 不是最佳

### 3.1 它发现不了能力

模型看到的是像素，不是「这个 mod 提供了什么」。它得先猜「这个按钮是干嘛的」，再点。而 mod 的能力清单本来就在 `PluginInfos`、`ConfigEntry`、程序集元数据里——有结构化的地方不看，去看像素，是把信息量最小化。

### 3.2 它够不到多数实现

抽样里 15/15 是 Harmony patch，大量功能是热键触发或后台持续生效（自动分拣、投料槽、性能优化、错误分析）。这些没有「可点的按钮」。computer use 对这些 mod 的覆盖率接近零。

### 3.3 它把「调用」降级成「点击」

调用有返回值、有异常、有前置条件；点击只有「点了」和「没点着」。我们本来就没有第三方 mod 的状态校验能力，再丢掉返回值，就只剩「看起来成功了」。

### 3.4 它和现有确认/撤销模型冲突

现有模型要求每个工具声明 risk、后置条件、撤销方式。点击的语义是未知的：点一个按钮可能是开面板，也可能是删存档。确认卡写不出「会发生什么」，撤销栈也无从记录。

### 3.5 它的覆盖率反而更低

同样是零成本接入，「配置桥」覆盖 13/15，computer use 只覆盖「有常驻 UI 且能被认出来」的那部分。用更差的手段换更低的覆盖率，没有理由。

## 4. 方案：四层能力桥

### 4.1 L0 发现（只读，零风险）

- `BepInEx.Bootstrap.Chainloader.PluginInfos`：拿到所有插件的 `GUID`、`Name`、`Version`、`Location`、`Dependencies`、`Incompatibilities`。
- `plugin.Instance.Config`（`ConfigFile`）：拿到全部 `ConfigEntryBase`，含 `Definition.Section/Key`、`Description.Description`、`SettingType`、`DefaultValue`、`BoxedValue`。
- 输出 `mods_list`（装了什么），以及**每个已加载插件一个** `mod_<id>_config`（这个 mod 有哪些开关）。
  工具名里带插件 id，插件不在 hello.extensions 里时这个工具就不存在——不广告、不进模型上下文。
  模型据此知道「能做什么」，不需要猜。

已用 BepInEx 5.4.17 的元数据核实：`Chainloader.PluginInfos` 是公开静态属性；`ConfigFile` 实现 `IDictionary<ConfigDefinition, ConfigEntryBase>`，有 `Keys` / `Values` / `Count` / `GetConfigEntries()` / `TryGetEntry()` / `Save()`。

### 4.2 L1 配置读写（零 per-mod 成本，覆盖最大）

- `mod_<id>_config` 的 `action=get` / `action=set`；线级下发的仍是带 `guid` 的内部工具
  `mods_config_get` / `mods_config_set`，它们不广告给模型。
- `value` 的写入走 `ConfigEntryBase.SetSerializedValue`（它按 `SettingType` 解析字符串，解析失败直接报参数错误）。
- 写操作：先记旧值 → 压撤销栈；设置后 `ConfigFile.Save()`；返回「是否需要重启」的提示（无法自动判断，就在结果里提醒「部分 mod 需要重启生效」）。
- risk 默认 1（可逆），但允许 mod 分类覆盖：作弊类 mod 的开关按 risk 2 处理。
- 设置会触发 `SettingChanged` → 执行 mod 自己的代码，必须 try/catch，并把异常作为工具错误返回，不能让它冒泡到游戏主循环。

### 4.3 L2 适配器（声明式，按需）

给「有公开方法」的 mod 写一份 manifest，放在 `BepInEx/config/icarus-ii/bridge/<guid>.json` 或随 mod 分发：

```jsonc
{
  "schema": 1,
  "pluginGuid": "jinxOAO-DSPCalculator",
  "pluginVersion": ">=0.5 <1",
  "tools": [{
    "name": "open_calculator",
    "title": "打开计算器",
    "description": "按配方需求打开计算器窗口",
    "risk": 1,
    "parameters": {
      "type": "object",
      "properties": { "itemId": { "type": "integer" } },
      "required": ["itemId"]
    },
    "invoke": { "type": "DSPCalculator.API", "method": "OpenNewWindowWithDemand", "args": ["itemId"] }
  }]
}
```

约束：

- **只允许调用声明它的那个插件所在程序集里的类型**（校验 `Type.Assembly` 与 `PluginInfo.Location` 一致），不能借此摸到 `UnityEngine` 或游戏内部。
- 参数类型白名单：`int/long/float/double/bool/string/enum`，以及它们的数组；不支持任意对象构造。
- 版本不匹配 → 整份 manifest 禁用，`mods_list` 里标出原因，不猜。
- 调用在主线程，同步执行；超过 `timeoutMs` 只能报错，不能中断，所以 manifest 只应声明快方法。

### 4.4 L3 UI 树自动化（零 per-mod 成本，但不是像素）

对没有 API、只有 UI 的 mod，用 uGUI 层级的「控件路径」而不是坐标：

- `ui_snapshot()`：遍历 `Canvas` 下的 `Selectable`，返回路径、类型、可见文本、`interactable`、`active`。
- `ui_click(path)`：对目标节点派发一次合成的 `PointerClick`（`ExecuteEvents.Execute<IPointerClickHandler>` + 位置设为该控件中心的 `PointerEventData`），而不是去猜某个回调字段。游戏原生按钮的回调在 `UIButton.onClick`（`dsp-game/Assets/Scripts/UI/Other/UIButton.cs:889`），标准 uGUI 按钮在 `Button.onClick`；合成事件能同时覆盖两者，和真实点击走同一条链路。
- 点完再 `ui_snapshot()` 比对，作为后置校验。

它比 computer use 好在：不受分辨率/缩放/语言影响（读的是文本，不是像素位置），不抢 OS 焦点，Steam Deck 也能用。坏在：仍然不知道语义，仍然要每次确认。

### 4.5 L4 SDK（长期）

给 mod 作者一个声明方式：要么就是 L2 的 manifest 规范，要么是一个软依赖接口。作者自己声明工具，我们不需要逐个适配。这是唯一能把覆盖率做到「新 mod 开箱即用」的路，但需要生态配合，不能当作近期方案。

### 4.6 L5 computer use（兜底）

只在「L3 也点不到」时考虑，且默认关闭、玩家显式开启。理由见前置报告。

## 5. 与现有架构的接缝

### 5.1 协议

`hello` 增加可选字段（老端忽略），并新增能力串 `mod.bridge.v1`（没有该能力串就不注册 `mods_*` 元工具）：

```jsonc
"extensions": [
  { "id": "uxassist", "name": "UXAssist", "version": "1.6.2", "toolCount": 0, "configCount": 114 }
]
```

工具 schema 不塞进 `hello`（可能很多），而是新增一对请求：`ext_tools` / `ext_tools_result`，按 `id` 拉取。

### 5.2 动态工具与按需启用

现在工具集由 `capabilities` 静态决定，session 在能力变化时重建。第三方工具是动态的，按同样机制处理：

1. 默认只注册 `mods_list`；每个 `extensions` 里的插件生成一个 `mod_<id>_config`（`configCount > 0` 才生成）。
2. 模型要用某个 mod 的具体工具时，先 `mods_tools_enable(guid)`；sidecar 拉 schema、重建 session、下一轮生效。
3. 用完 `mods_tools_disable(guid)`，或回合结束自动关。

这样避免一次性把上百个第三方工具塞进 `tools` 数组（当前内置工具已经是约 2.5 万字符/请求）。

### 5.3 命名

工具名统一 `<extId>_<tool>`，`extId` 取 GUID 规范化后的短串（只留 `[a-z0-9]`，截断到 16 字符）。注册时检查与内置工具、其他扩展是否撞名，撞名直接拒绝该扩展。

### 5.4 确认、令牌、撤销

- 第三方写工具走现有 `confirm_request` + 一次性令牌，mod 侧校验逻辑不变（桥在 IcarusII 内，令牌仍由 IcarusII 验）。
- 配置写：撤销 = 恢复旧值再 `Save()`。
- 适配器调用：manifest 可声明 `undo`（反向方法 + 参数），没有声明就标记「不可撤销」，按 §9.1 的硬约束，默认不注册不可撤销的 risk 2 工具。

### 5.5 权限与沙盒

- 新增设置项「允许第三方 mod 工具」，默认关闭。关闭时只保留 L0 只读。
- 沙盒 `save.bind.sandbox` 与第三方工具正交：沙盒管的是「游戏内作弊」，第三方工具管的是「跨 mod 调用」，不要混。

### 5.6 内容类 mod 不需要桥

GenesisBook / MoreMegaStructure 这类注入 `LDB` 的内容 mod，`game_export_prototypes` 导出后 `kb_*` 就能搜到。需要验证两点：导出发生在所有插件加载之后；遇到未知 proto 字段时不崩。这两条是测试项，不是新功能。

## 6. 新装一个 mod 的完整接入流程

> 这一节回答「玩家刚装了一个从没见过的 mod，AI 怎么用上它」。
> 前提：按 §4 分层实现；L0/L1 必做，L2 按需，L3 可选，L5 默认关。

### 6.1 先把「全新 mod」分成五类

| 类型 | 特征 | 接入方式 | 需要人工吗 |
| --- | --- | --- | --- |
| 内容型 | 注入 LDB：新物品/配方/建筑/星球 | 不需要新工具，kb 导出后自然可见 | 否 |
| 配置型 | 只有 `ConfigEntry` | L0 + L1 | 否 |
| 动作型·有 manifest | 随包带 `IcarusII.tools.json` | L2 | 作者写一次 |
| 动作型·无 manifest | 有公开方法，但我们没见过 | `mods_api_list` 起草 + 玩家批准 | **玩家批准一次** |
| 动作型·只有 UI | 没有 API，只有面板 | L3 | 否，但每步确认 |

「全新」这个词只对第 4 类构成障碍；前三类要么自动可用，要么作者已经声明。

### 6.2 首次启动的时序

```text
启动
  BepInEx 加载插件 → Chainloader.PluginInfos 逐个填充
  IcarusII 的 hello 只带自己的能力，不缓存 mod 列表

玩家提问
  sidecar 组 tools = 内置工具 + mods_list + 每个插件的 mod_<id>_config

模型调 mods_list
  mod 侧现读 PluginInfos，返回
  [{ guid, name, version, assembly, configCount, toolCount, hasManifest }]
```

**关键细节：不要在 `Awake` 里缓存 `PluginInfos`。** IcarusII 和目标 mod 是互不依赖的插件，加载顺序不保证；在 `Awake` 做快照会漏掉后加载的 mod。每次 `mods_list` 现读一次，成本可以忽略。

**绑定规则：** 每个插件的工具名里带它的 `id`（`mod_<id>_config`），工具集合完全由本条 `hello.extensions` 决定。
插件被卸载、被禁用或加载失败时，下一条 hello 不再列出它，sidecar 立刻重建会话，对应工具从模型上下文消失；
不会留下一个“调用时才报 mod 不存在”的空壳工具。

### 6.3 分支一：配置型（最常见）

```text
模型 mod_x_config(action=list)
  → [{ section, key, type, default, current, description }]

模型 mod_x_config(action=set, section, key, value)
  sidecar：risk 1（作弊类 mod 的开关按 risk 2），受限档位直接放行，只读档位弹确认卡
  线级：mods_config_set(guid=该插件, section, key, value)
  mod：校验 section/key 存在
      → ConfigEntryBase.SetSerializedValue(value)（解析失败 = 参数错误）
      → ConfigFile.Save()
      → 捕获 SettingChanged 回调里抛出的异常，转成工具错误
  → { old, new, restartHint }
  → 撤销栈 push「恢复 <mod> 的 <key>」
```

玩家看到两个东西：工具卡（改了哪个 mod 的哪个开关，旧值 → 新值）和一句提示（部分 mod 需要重启生效）。

### 6.4 分支二：随包 manifest

插件目录里有 `IcarusII.tools.json` → IcarusII 启动时扫描 → 校验 `pluginGuid` 与版本范围 → 登记为扩展。

模型看到的是 `mods_list` 里 `toolCount > 0`。要用时：

```text
模型 mods_tools_enable(<extId>)
  → sidecar 向 mod 拉 ext_tools（schema）
  → 会话重建
  → 下一轮模型就能看到这些工具
```

用完 `mods_tools_disable`，或回合结束自动关，避免几十个工具常驻 prompt。

### 6.5 分支三：有 API 但没有 manifest

这是「全新 mod」最真实的处境，必须诚实：**没有适配器就没有工具。** 流程是：

1. `mods_api_list(guid)`：只读，列出该插件程序集里「参数类型全在白名单内」的公开静态方法（类型名 + 方法名 + 参数签名）。这一步只读元数据，不执行任何东西。插件实例上的公开实例方法以后可以再放开，第一版只做静态，避免猜实例状态。
2. 模型把候选方法整理成一份 manifest 草案，在聊天里逐条列出（类型、方法、参数、打算叫什么工具、risk）。
3. 玩家确认「就是这几个」→ IcarusII 把草案写入 `BepInEx/config/icarus-ii/bridge/<guid>.json`，同时绑定当前插件版本。
4. 工具注册；之后每次调用走正常确认/令牌。

这条路的安全来自三点：只能调该插件自己程序集的方法；参数类型白名单；玩家逐条批准且版本绑定。

如果玩家不想批准，就退回「用配置 / 用 UI / 我告诉你手动怎么做」——**不要偷偷用 computer use 去点**。

### 6.6 分支四：只有 UI

```text
模型 ui_snapshot()
  → [{ path, type, text, interactable, active }]

模型选控件 → ui_click(path)（risk 2，确认卡）
  → 合成 PointerClick 派发
  → 等一帧（或几帧）让界面稳定

模型 ui_snapshot() 再取一次，比对差异
  → 有变化：报告结果
  → 没变化：如实说「可能没生效」，不假装成功
```

### 6.7 运行中热装

游戏运行期间把新 mod 放进 `BepInEx/plugins`，BepInEx 不会加载它。

- `mods_list` 看不到它；
- 模型应回答「需要重启游戏才会加载」；
- 不做插件目录监听：收益低，噪音大（mod 管理器切换 profile 会批量改文件）。

### 6.8 三个具体例子

**A. Auxilaryfunction（配置型）**

> 玩家：帮我关掉自动拾取。
> 模型：你装了 Auxilaryfunction 3.1.1，配置里有 `AutoPick`（自动拾取，当前开启）。要关掉吗？
> 玩家：关。
> → 确认/直接执行 → `mods_config_set` → 工具卡「AutoPick: true → false」+「可能需要重启」。

**B. DSPCalculator（有 API、无 manifest）**

> 玩家：用计算器帮我算一下这个产线。
> 模型：DSPCalculator 有公开接口 `API.OpenNewWindowWithDemand`，但还没有适配器。我可以生成一份只调用这个方法的适配，你确认后以后就能直接用；或者你自己打开计算器，我告诉你填什么。
> 玩家：生成吧。
> → `mods_api_list` → 草案 → 确认 → manifest 落盘 → 下一轮 `open_calculator` 可用。

**C. PlanetFinder（只有 UI）**

> 玩家：帮我找有可燃冰的星球。
> 模型：（先 `mods_list` 确认装了 PlanetFinder）我可以用它的搜索窗口，需要你确认两次点击。
> → `ui_snapshot` 拿到「搜索框、资源下拉、搜索按钮」→ 逐个 `ui_click` / 填值 → 再快照确认结果。

### 6.9 失败与降级

| 情况 | 表现 | 处理 |
| --- | --- | --- |
| mod 没加载 | `mods_list` 里没有 | 提示重启；不猜 |
| 插件加载失败 | `PluginInfo.Instance` 为 null | 列表里标 `failed`，不提供任何操作 |
| manifest 版本不匹配 | 扩展在列表里标 `disabled: version` | 不注册工具 |
| manifest 格式错 | 启动日志报错 | 整份忽略，不影响其他扩展 |
| 配置项不存在 | 工具返回 `unknown_config` | 不写、不压撤销 |
| 配置值解析失败 | `invalid_value` + 期望类型 | 让模型重试或问玩家 |
| `SettingChanged` 抛异常 | 工具返回 `mod_error` | 已写入的值保留，提示可能不一致 |
| 方法在更新后消失 | `api_missing` | 扩展自动禁用，提示需要更新适配器 |
| UI 控件找不到 | `control_not_found` | 重新快照，不盲点 |
| 主线程被卡住 | 超时报错 | 记录该 mod 为「慢调用」，在列表里标出 |

### 6.10 这套流程的代价

- 内容型、配置型：**装完就能用**，零人工。
- 有 manifest 的动作型：作者写一次，玩家零人工。
- 无 manifest 的动作型：玩家要批准一次（十几秒），之后固定可用。
- 只有 UI 的：每步确认，能用但不稳。

computer use 唯一能省掉的是「最后一次批准」，代价是失去返回值、失去撤销、失去跨分辨率稳定性。这笔交易不划算。

## 7. 安全与治理

| 风险 | 处置 |
| --- | --- |
| manifest 变成任意 API 调用 | 只允许调用声明插件自己的程序集；参数白名单；不做通用 `invoke` 工具 |
| 恶意/失误的 manifest | 默认关闭 + 玩家显式启用 + 每个工具确认卡 |
| 版本漂移 | manifest 声明版本范围，不匹配整份禁用 |
| 主线程被第三方代码卡死 | 只允许快方法；调用计时并记录；慢调用在 `mods_list` 里标红 |
| 配置写触发 mod 异常 | try/catch，异常转工具错误，不冒泡 |
| mod 列表出站 | 只发 GUID/名称/版本/配置项，不发文件内容；在隐私说明里披露，默认只在需要时发 |
| 撞名/劫持内置工具 | 扩展工具必须带前缀，注册期查重 |

## 8. 落地顺序

| 阶段 | 内容 | 验收 |
| --- | --- | --- |
| P0 | L0 发现 + L1 配置读写 + 撤销 | 装 5 个热门 mod，`mods_list` 数量与 BepInEx 日志一致；改 `Auxilaryfunction` 一个开关，重启后生效；撤销能改回 |
| P1 | `hello.extensions` + 动态工具注册 + 按需启用 | 启用/禁用扩展后，模型看到的工具集正确变化，session 重建不丢历史 |
| P2 | L2 manifest 加载器 + 3 个参考适配器（DSPCalculator / LSTM / UXAssist） | 版本不匹配时工具消失；调用成功且后置校验通过 |
| P3 | L3 UI 快照 + 点击 | 能列出某 mod 窗口的按钮并点中；点完快照变化可验证 |
| P4 | L4 SDK 文档 + 示例 | 一个最小 mod 自己声明工具并被识别 |

## 9. 风险清单

- 第三方 mod 的公开方法可能只是「碰巧 public」，语义没有承诺；适配器要按「每个版本重新验证」维护。
- 配置项可能互相依赖（打开 A 才能用 B），单看 `Description` 不一定能发现；`ask_user` 是兜底。
- 有些 mod 的配置在启动时读取一次，运行期改无效；结果里必须提示「可能需重启」。
- UI 树自动化会被自定义绘制（非 uGUI）绕过，例如直接 `OnGUI` 或自绘面板。
- `PluginInfos` 只能看到 BepInEx 插件；r2modman 的 profile 隔离、DSP 自带内容都不在此列。

## 10. 待拍板

1. 先做 P0（发现 + 配置），还是直接冲 P2（适配器）？我建议 P0，投入小、覆盖大，而且能立刻验证「玩家到底想让 AI 做什么」。
2. 第三方工具默认关闭、玩家在设置里开——是否接受？
3. L3（UI 树自动化）要不要做？它是「零适配覆盖 UI-only mod」的唯一体面方案，但仍然要每次确认。
4. 是否愿意维护一份「热门 mod 适配器」清单（P2 的三个参考实现之外）？
5. 是否允许 AI 起草 manifest（§6.5）？如果允许，是否只限公开静态方法 + 玩家逐条批准 + 版本绑定？

## 11. AI 能不能自己写工具

> 由 §6.5 的「生成 manifest」引申出来的边界问题：既然 AI 能为第三方 mod 起草工具，能不能干脆让它写「关于游戏的一切工具」。
> 结论：**能让它写「绑定」和「组合」，不能让它在运行期写「实现」。**

### 11.1 先纠正措辞：manifest 不是「写工具」

manifest 只是把**已经存在的方法**暴露成工具：

- 方法的实现是 mod 作者写的，不是 AI 写的；
- AI 只决定「叫什么名字、参数怎么映射、risk 几」；
- 执行面固定在该插件自己的程序集，参数类型白名单；
- 产物是一份十几行、人能一眼看完的 JSON，并且绑定版本。

真正的「写工具」是写方法体，能碰到 `UnityEngine` 和游戏内部状态。这两件事的风险差一个数量级。

### 11.2 三种「AI 参与工具构造」

| 形态 | AI 产出 | 执行面 | 谁能审 | 现状 |
| --- | --- | --- | --- | --- |
| 绑定 | manifest（既有方法 → 工具） | 该插件自己的程序集 | 玩家 30 秒能看完 | §6.5，允许 |
| 组合 | 脚本/工作流（串既有工具） | 既有工具白名单 | 沙箱 + 白名单 | 已有 `run_script` |
| 实现 | C# / Unity 代码 | 任意 API、任意游戏状态 | **没人能审** | 不允许 |

### 11.3 为什么不能泛化到「写一切工具」

1. **注入即代码执行。** 今天的 prompt injection 最多诱导模型调用一个**已存在、有确认、有撤销**的工具；一旦允许生成代码，wiki 里的一句话就能变成任意代码执行。
2. **没有后置条件。** 现有工具能写「改了什么、怎么撤」；生成的代码可以绕过撤销栈，确认卡也写不出后果。
3. **无法测试、无法复现。** 仓库里的工具都有离线测试；运行期生成的代码没有，出错也无法归因（是模型写的、游戏版本变了、还是别的 mod 冲突）。
4. **版本更新即失效。** 游戏和 mod 都在高频更新（样本里有的 mod 有 276 个版本）。绑定可以按版本禁用；代码只会在运行时崩。
5. **审核不可能。** 玩家不是程序员，给他看 200 行 C# 等于没审。安全模型依赖「人能确认」，这一条直接失效。
6. **一次性收益、永久风险。** 生成的工具只服务这一个玩家的这一次需求；仓库里的工具服务所有玩家、有测试、有文档。用永久风险换一次性收益，不划算。
7. **和产品定位冲突。** `design.md` §1.2 明确不把任意 Unity API 暴露给模型；「什么都让 AI 现写」等于把这条硬约束推翻。

### 11.4 可以安全泛化的两个方向

- **组合工具（workflow）。** 把已有工具按顺序/条件串起来，例如「先查电力，低于阈值就切研究」。这已经有基础：`run_script` 在 QuickJS 沙箱里执行，能力面只有 `dsp.call`，继承 risk/路由/超时，默认只读。AI 写编排不扩大能力面。
- **只读查询/视图。** 让 AI 写聚合、过滤、排序，产出表格或图表数据，不产生任何写副作用。这类可以做成受限查询 DSL，而不是执行代码。

两者都不新增「副作用」，所以不需要逐条人工批准；真正的门槛在「新增能力」。

### 11.5 判定规则

> **新增副作用 / 新增系统访问 → 人写。**
> **重组已有能力 → AI 可以写，沙箱执行。**
> **绑定既有方法 → AI 可以起草，玩家批准一次。**

这条规则的好处是：能力集始终是「有限的、被审过的」，AI 的自由度只体现在「怎么组合」和「用哪个」。

### 11.6 开发期生成是另一回事

本会话就是 AI 在写新工具：读代码、写 C#、补测试、构建、打包、部署，每一步都有人在环里，产物进仓库、可回滚。

**运行期生成**把风险留给玩家，**开发期生成**把风险留在 CI。想让 AI 更快地扩展能力，正确做法是让它在这个仓库里写工具，而不是在玩家的游戏进程里写。

## 附：两个 IcarusII 能否互为主子代理

> 假设性问题，结论是「当前架构下不能，且没有做的价值」。记录在此，避免以后重复讨论。

### A.1 两个实例怎么才能同时存在

- 插件的 GUID 是常量 `icarus-ii`（`mod/src/IcarusII/Plugin.cs:34`）。BepInEx 按 GUID 去重，只保留较新的版本（`BepInEx.dll` 里的日志串 `Skipping [{0}] because a newer version exists ({1})`）。所以**同一份 mod 复制两份不会同时加载**。
- 要并存，只能改 GUID 做 fork。改了 GUID 之后，配置文件名（`BepInEx/config/<GUID>.cfg`）、数据目录、端口都可以各不相同，但默认值仍然相同。

### A.2 并存的第一个问题是共享资源

| 资源 | 默认值 | 冲突表现 |
| --- | --- | --- |
| sidecar 端口 | `8765`（`Sidecar`/`Port`） | 第二个 sidecar `EADDRINUSE`，进程退出，mod 显示「sidecar 提前退出」 |
| 数据目录 | `%APPDATA%/icarus-ii`（Proton 下在 compatdata 的 pfx 里） | 两个 sidecar 抢写 `runtime.json`；后写的 token 覆盖前面的，另一个 mod 握手失败 |
| 热键 | `R` | 两个聊天窗一起开关 |
| UI 排序 | `sortingOrder=30000` | 两个窗口叠在一起 |
| 撤销栈 | 进程内单层 | 两套互不知情，后一次写覆盖前一次的撤销项 |

端口可以改成 `0`（sidecar 支持内核分配），数据目录可以各自指定，热键可以改成可配——但这些都要玩家手工配两套，且 fork 的默认值得改。

### A.3 为什么「当子代理」调不动

1. **agent 不在 mod 里。** mod 只是协议中继；LLM 循环、会话、确认都在 sidecar。L2 桥能调的只是插件程序集里的公开方法，而 IcarusII 的实现几乎全是 `internal`，也没有 `Submit(prompt)` 这类入口。
2. **协议层倒是有一条路。** `prompt`（mod → sidecar）本来就是提交问题的消息；另一个进程只要拿到 `runtime.json` 里的 token，就能 `hello` + `prompt` 驱动它。但 token 只区分「本机同用户」，不区分「是不是另一个 IcarusII」，这等于给任何本地进程开了后门。
3. **确认归属无解。** B 的写操作要 B 的 sidecar 发确认、B 的 mod 验令牌；A 无法代答。玩家只有一个，会同时收到两个确认框。想让 A 代答，等于让 B 放弃自己的权限模型。
4. **双 LLM 循环没有收益。** 两个 agent 看同一份游戏状态，1+1 < 2；递归调用（A→B→A）还要额外做深度、步数、费用上限。

### A.4 如果真要做，最小形态

- 不做「两个 agent 互调」，做「一个 agent + 多组工具」：第三方能力通过 manifest / SDK 注册进同一个 IcarusII（即本文 §4 的 L2/L4）。
- 非要多实例，至少需要：显式开关 + 实例身份（GUID/端口/token 绑定）、公开的 `SubmitAsync` 或 MCP 化的 sidecar、子代理写操作的确认归属规则、递归与预算上限、端口与数据目录隔离。

### A.5 判断

技术上「能」的部分是跨进程发 `prompt` 和同进程调公开方法；「不能」的部分是把它当子代理用——没有 API、没有身份、没有确认归属。而这三样补齐的复杂度，远高于把能力直接做成工具。多 agent 真正有价值的形态是「一个主 agent + 多个无 LLM 的能力单元」，不是一个完整 agent 去驱动另一个完整 agent。
