# icarus-ii 想法记录与可行性分析

> 本文件记录产品想法（含用户原始表述）、技术摸底结果、待确认项。
> 与 `design.md` 的关系：design.md 描述**当前已确定并实现**的设计；
> 本文件描述**尚未定案**的方向，定案后再合并进 design.md。

---

## 想法一：项目更名为「伊卡洛斯二号」

**原始表述**：项目改名叫伊卡洛斯二号（的英文）。

**说明**：DSP 中玩家驾驶的机甲名为**伊卡洛斯（Icarus）**。"伊卡洛斯二号"即 **Icarus II**，
与想法三（LLM 控制的第二台机甲）同名——项目名与核心玩法指向同一件事，是自洽的。

**待定**：标识符规范与游戏内显示名（见文末待确认清单 Q1）。

---

## 想法二：LLM 输出受科技约束、并消耗玩家能量

**原始表述**：
> LLM 输出的 TPS 受到升级科技约束，输出时会消耗玩家的能量。

### 已明确

- 输出的**速率（TPS）**不是一个固定值，而是随玩家的科技/升级进度增长。
- 输出**有资源成本**：消耗玩家的能量。

### 技术锚点（已用游戏源码核实，修正了此前的元数据推断）

> 上一版这张表是从程序集元数据推断的，**有两处是错的**，已按源码改正（附 `文件:行号`）。

| 需求 | 游戏侧锚点 | 说明 |
| --- | --- | --- |
| 扣能量 | **`Mecha.UseEnergy(double)` / `Mecha.QueryEnergy(...)`** | 唯一权威 API（`Mecha.cs:1325,1339`）。既有扣能点全是"`QueryEnergy` → `coreEnergy -= energyGet` → `MarkEnergyChange(slot, -energyWant)`"，例如采矿 `PlayerAction_Mine.cs:82-135`、建造 `PlayerAction_Inspect.cs:763-770` |
| 能量明细 | `Mecha.energyChanges[EC_*]` + `MarkEnergyChange` | `EC_MAX = 32`，现有语义只用到 **0..15** ⇒ **16..31 是空槽**，可给"LLM token"单开一类（`Mecha.cs:2682-2699`）。⚠️ 该数组**每 tick 在 `GenerateEnergy` 首行被清零** ⇒ 必须每 tick 都标记，且顺序在 `GenerateEnergy` **之后** |
| 能量单价 | `Configs/ModeConfig.cs` 的 `mechaMiningPower` / `mechaResearchPower` / `mechaInstantBuildEnergy` … | 我们的每-token 成本应**照这套量纲定价**，才不会突兀（`Mecha.cs:195-208` 装载） |
| ~~能耗系数~~ | ~~`energyConsumptionCoef`~~ | ❌ **更正**：它**不是科技升级来的**，唯一写入点是机甲 DIY 编辑器（`UIMechaEditor.cs:599`）。用它做"科技约束"是错的 |
| ~~核心等级~~ | ~~`mecha.coreLevel`~~ | ⚠️ 它是**升级产物**（`case 6` 同时改 `coreEnergyCap` 与 `coreLevel`），可以作为"进度"读数，但不是"输出速率"的现成字段 |
| 速率类数值的现成范式 | `gameHistory.techSpeed`（`case 22` 提升） | 研究速度倍率，其消费方式是"能量足够才推进，进度 += speed"（`MechaLab.cs:99-131`）——**这正是我们做"TPS 受科技约束"最该抄的范式** |

**新增自定义科技的完整做法（源码结论）**

| 项 | 结论 |
| --- | --- |
| 数据源 | `LDB.techs` ← `Resources.Load("Prototypes/TechProtoSet")`，是 **ScriptableObject 资源、不是代码硬编码**（`LDB.cs:14-21,68`；`ProtoSet.cs:19-20` 注释"每个数据集将通过 Excel 导入成一个 Asset"） |
| 官方扩展点 | **没有**。源码无第二个构建入口、无 Import/Export、无 hook |
| 可行做法 | 在 `LDB.techs` 懒加载完成之后、`GameHistoryData.Init`（`GameHistoryData.cs:415`）之前**追加**到 `dataArray`。techStates 是 `Dictionary<int,TechState>` 且每次读档从 LDB 重建，缺 ID 有默认值兜底（`:639-643`） |
| ⚠️ 最容易踩的坑 | 追加后**必须调用 `LDB.techs.OnAfterDeserialize()`** 重建 `dataIndices`（`ProtoSet.cs:38` 是 public virtual）；否则 `LDB.techs.Select(新ID)` **永远返回 null**（`:55-60`） |
| ID 限制 | `kMaxProtoId = 12000`，超过会 `LogError`（`TechProto.cs:43,272`）；`page => ID < 2000 ? 0 : 1` 决定落在科技树哪一页（`:258`） |
| 图标 | 放 `Assets/Resources/Icons/Tech/<ID>.png`，`IconPath = "Icons/Tech/<ID>"`（`TechProto.cs:283`） |
| 本地化 | `Locale/<lcid>/prototype.txt`（UTF-16LE、Tab 分隔）：键为 `T<名字>` / `<名字>描述` / `<名字>结果`，第 3 列固定 `5`。需覆盖 `2052/1033/1031/1036/1041/1042/1049/3082` 各一份 |
| ⚠️ 升级函数的坑 | `UnlockTechFunction(int func, ...)` 是**裸 int switch，没有 enum、没有 default 消费**（`GameHistoryData.cs:1602`）⇒ **自定义 func 号不会被处理**。应复用现成分支（如 `case 22` = techSpeed） |
| 解锁触发 | `GameMain.history.onTechUnlocked`（`GameHistoryData.cs:277`）。⚠️ **`history` 每次读档都会被 new**，订阅必须重挂 |

### 模糊点### 模糊点

1. **TPS 约束作用在"哪里"？** 三种可能，体验差别很大：
   - (a) 约束**聊天文字**的滚出速度 → 玩家直接感受到"AI 在想得慢"；
   - (b) 约束**第二台伊卡洛斯的行动速率** → 聊天不受影响；
   - (c) 两者共用同一个"算力"池。
2. **能量耗尽时怎么办？** 中途截断一句话体验极差。
3. **TPS 的上下限是多少？** 若新档只有 2 token/s，一条 500 token 的回答要等 4 分钟，mod 会被认为坏了。
4. **"升级科技"是复用现有的，还是新增自定义的？** 新增需要打 `LDB.techs` 补丁 + 图标 + 本地化。

### 我的建议（可直接采纳，也可否决）

1. **解耦"生成"与"播放"**：让模型在后台**一次性生成完**（不占用游戏资源），
   再按当前 TPS 允许的速率**在窗口里滴出**，同时按 token 数扣能量。
   → 好处：LLM 的网络延迟与游戏的速率约束互不干扰；"慢"变成一种**刻意的手感**而非卡顿。
2. **TPS 设保底**：即使最低级也给一个可用速度（建议 ≥ 8 token/s），
   让 1 级是"明显慢但能用"，而不是"不可用"。
3. **能量耗尽不截断**：改为**自动暂停并保留上下文**，充能后继续；
   窗口显示"能量不足，已暂停"。绝不半句话截断。
4. **能量单价对齐游戏量纲**：照 `Configs/ModeConfig.cs` 里的
   `mechaMiningPower`/`mechaResearchPower`/`mechaInstantBuildEnergy` 定价，
   并复用"能量不足则按 `ratio` 降级"的既有范式（`MechaLab.cs:129-131`）。
   （原稿写的"用 `energyConsumptionCoef`"已证伪——那是机甲 DIY 属性，不是升级）
5. **科技先复用后自定义**：M2 先用 `Mecha.coreLevel` 验证手感（零新增内容），
   确认好玩之后再考虑加一条自定义科技线，避免一上来就做 LDB 补丁。

### ⚠️ 一个需要正视的设计张力

**给 AI 加资源成本，会直接抑制玩家使用 AI**——而"多问、多探索"恰恰是本 mod 的核心价值。
如果玩家因为心疼能量而不敢提问，这个功能就自我否定了。

因此我建议：**约束体现在"第二台伊卡洛斯"身上，而聊天问答保持廉价或免费**。
"算力"作为稀缺资源，限制的是 AI **替你做事的吞吐**，而不是 AI **回答你问题的速度**。
这样既保住了游戏性（成长感、资源权衡），又不惩罚提问。

---

## 想法三：第二台伊卡洛斯（已按源码重写）

> 本节已用游戏源码（`dsp-game/Assets/Scripts/`，1797 个 C# 文件）**逐条核对**，
> 替换掉此前仅凭程序集元数据做的推断。每条结论附 `文件:行号`。

### 源码结论：原设想不成立

原设想是"造一台新的 `Mecha`"。源码证明这条路在结构上就不通：

| 原假设 | 源码事实 | 依据 |
| --- | --- | --- |
| `Mecha` 是机甲实体 | **`Mecha` 是挂在 `Player` 上的数值/能量模块**，整个类里**没有任何位置字段**，位置全读 `player.position/uPosition` | `Controller/Player/Mecha/Mecha.cs:1570,1577,1644` |
| 再造一个 `Player` | **不可能**。`mainPlayer` 是 `private set`，全文 **814 处**引用；建造工具 `_Init` **无视传入的 player**，直接 `player = gameData.mainPlayer; mainCamera = GameCamera.main;` | `Data/GameData.cs:291`；`Controller/Player/Build/BuildTool.cs:31-41` |
| 复用建设无人机 | **可行但受死限**。`CraftData.owner` 只有 `0/-1 = 伊卡洛斯` 与 `>0 = 实体` 两种，非玩家 owner **必须是真实战场分析基站**，而实体是静态建筑、**无任何移动 API** | `Data/CraftData.cs:8`；`Component/Systems/ConstructionSystem.cs:436-439`；`Component/Fleet/FleetComponent.cs:103-146` |
| 复用载具系统 | **现在不行**。`CreationSystem` 的 Export 被注释掉（`// creationSystem.Export(w); // version 2+`），`CreationPhysics` 是空壳 | `Data/SpaceSector.cs:818`；`Physics/CreationPhysics.cs:126-135` |
| 第二台能被攻击 / 被仇恨 | **不会**。战斗系统只有 `target.id == 1` 一个玩家分支；黑雾 AI 直接读 `gameData.mainPlayer` | `Component/Systems/SkillSystem.cs:4698-4707`；`Component/Enemy/Enemy Unit/EnemyUnit_SensorLogic_Ground.cs:199` |
| 第二台能在别的星球存在 | **不能**。`localPlanet` 单例，`factoryLoaded` 单星球，`PlanetData.gameObject` 只对当前星球创建 | `Data/GameData.cs:289,322-343`；`Data/PlanetData.cs:133` |

**⇒ 结论：需要做的是给游戏补上"第二个玩家"这个概念，而不是"造一台机甲"。**

### 已确认路线：A —— 自建虚拟实体，完全自定义

我们自己持有它的位置、速度、能量、生命与任务队列；用源码内建的 `MechaDIY` 方式渲染外观；
通过游戏公开 API 执行动作；用官方预留的存档扩展点持久化。

**外观有源码先例可循**（这是最省事的一环）：机甲编辑器造"预览机甲"的方式就是直接实例化 DIY prefab：

```csharp
// Mecha DIY/Modeling/MechaDIY.cs:13-41
GameObject res = Resources.Load<GameObject>(proto.DIYPath);   // "Characters/Icarus 1/Mecha DIY (Icarus 1)"
GameObject go = GameObject.Instantiate(res);
```

⇒ 我们能得到一台**有完整骨骼装甲、可上色**的机体，且**不需要 `Player`**。注意 `MechaArmorModel._OnInit` 里 `data as Player` + `player.mecha` 是硬要求（`MechaArmorModel.cs:68-85`），
所以外观数据会共享 `player.mecha.diyAppearance`，独立配色需要额外 hack。

### 分期（按"每期都有可玩结果"切）

| 期 | 目标 | 关键工作 | 风险 |
| --- | --- | --- | --- |
| **A1** | 它**存在且可见**：一个自建实体，有机甲外观，能按我们给的位置移动，能存读档 | `Instantiate(DIYPath)` + 自建 tick + `featureValues` 持久化身份 | 低 |
| **A2** | 它**能替玩家做事**：下达"去 X 建 Y"，动作通过游戏 API 执行 | 复用建设无人机（`EjectMechaDrone`）或 `PlanetFactory` 建造路径 | **中高**：`ConstructionSystem.UpdateDrones` 对非玩家 owner 硬绑基站，需实验验证 |
| **A3** | 它**是个"角色"**：有能量、有任务队列、能被 LLM 编排、UI 里能看状态 | Agent 工具层 + 状态 UI | 中 |
| **A4**（可选） | 它**参与战斗/会被打** | Harmony 补 `SkillSystem` 的 Player 分支、黑雾仇恨 | 高，且可能影响平衡与成就 |

**建议先做 A1**：它不依赖任何未验证的假设，却能立刻验证"外观 + 移动 + 存档"这条主干。
A2 之前必须先做两个低成本实验（见下）。

### 必须实验验证的问题（源码看不出）

1. 把 `ConstructionModuleComponent.baseEjectPos` 每帧改成"移动载体"位置，能否让无人机的发射点跟着动？
   （`ConstructionSystem.cs:411` 只在那一处使用该值，理论上可动；但返程语义与 `battleBase.energy` 扣能是否可接受，源码看不出。）
2. 让自建 module 指向一个**真实**战场分析基站，会不会与基站自己的无人机/编队/能量互踩？
   （`BattleBaseComponent.cs:40-45` 会互相覆写引用。）
3. 非本地星球上模拟"抽象机甲"（无 transform、无碰撞，只跑逻辑）能否被工厂后台 tick 正常处理——
   游戏没有"player-like 实体在未加载星球上"的先例。

### 持久化：用官方预留的扩展点

```csharp
// Data/GameHistoryData.cs:2880
public const int FEATURES_FOR_CUSTOM_MOD_ID_START = 100010000;   // 注释即"给自定义 mod 预留"
```

- `featureKeys: HashSet<int>`（布尔标记）与 `featureValues: Dictionary<int,int>`，
  **随存档读写，且 `Import` 不做任何 LDB 校验**（`GameHistoryData.cs:2152-2163,2303-2327`）
- API：`RegFeatureKey / SetFeatureValue / AddFeatureValue / GetFeatureValue`（`:1317-1349`）
- ⚠️ 只有 `int → int`。第二台机甲的完整状态（位置/能量/任务队列）需要**打包成整数**
  （位置按 0.01m 定点、能量取 kJ 等），或者只用它存**身份与版本**、把大块状态放存档同目录的旁路文件
- 它的 ID 区间（≥100010000）与 `JournalData` 那类"塞未知 ID 会 NRE 导致读档失败"的坑无关
  （`Component/Journal/JournalData.cs:84-86` 是明确的反例，不能用来存 mod 数据）

### 顺带发现：对**当前**工作立刻有用的东西

| 发现 | 用途 |
| --- | --- |
| `GameMain.history.onTechUnlocked` 事件（`GameHistoryData.cs:277`） | 想法二要的"解锁某科技后触发"的现成钩子。⚠️ `history` 每次读档都会 `new`，订阅必须重挂 |
| `IntelliAssist`（`Misc/IntelliAssist.cs:29,60,306,430`） | **只读**游戏状态查询：附近矿脉、阵列建筑、物体坐标、建议。后续做"让 AI 感知游戏"的工具层可直接复用 |
| `Player.Order(OrderNode.MoveTo(pos), false)`（`Player.cs:126`） | 程序化指挥**玩家自己那台**机甲移动的现成接口 |
| `.mecha` 外观文件目录（`Configs/GameConfig.cs:167-178`） | 新增机甲外观**不改存档、不改程序集**，纯资源通道 |

## 修订后的里程碑

| 里程碑 | 内容 | 状态 | 依赖 |
| --- | --- | --- | --- |
| **M0** | 骨架：插件加载 → 窗口（`R` 开/关）→ 握手 → 回显 | ✅ 实机验证 | — |
| **M1** | 真实对话：pi + DeepSeek、首次密钥引导、游戏助手人格、窗口原生外观 | 收尾中 | — |
| **M2** | 存档绑定对话串；「解锁科技 → 提升 AI 输出速率 + 每 token 扣能量」（想法二） | 未开始 | 自定义科技线怎么做（源码调查中） |
| **M3** | 第二台伊卡洛斯 **A1**：自建实体 + 机甲外观 + 我们驱动的位置 + `featureValues` 存档 | 未开始 | 无（不依赖未验证假设） |
| **M4** | 第二台伊卡洛斯 **A2**：能替玩家做事（建造） | 未开始 | **先做上面两个实验**（无人机发射点 / 基站互踩） |
| **M5** | 第二台伊卡洛斯 **A3**：能量、任务队列、LLM 编排、状态 UI | 未开始 | M4 |
| **M6**（可选） | 第二台参与战斗、会被攻击 | 未开始 | Harmony 补 `SkillSystem` 玩家分支与黑雾仇恨；需评估平衡与成就影响 |

**排序理由**：
- M2 在 M3 之前，因为它是**廉价且立刻改变手感**的一期，且自定义科技线一旦跑通，
  第二台机甲的"解锁"也能复用同一条流水线。
- M3 刻意只做"存在且可见"，因为它是**唯一不依赖任何未验证假设**的一期——
  外观方式已由 `MechaDIY` 源码证实，移动由我们自己实现，存档有官方扩展点。
- M4 之前必须做实验：源码明确不支持"非玩家 owner 的无人机"，
  如果实验失败，A2 的执行器要换成别的（例如直接调 `PlanetFactory` 建造路径），
  这会改变工作量但不改变 A1。

## 已确认决策

| # | 决策 |
| --- | --- |
| Q1 | 标识规范 **`IcarusII`（程序集/类名） / `icarus-ii`（目录、包名、GUID） / `Icarus II`（显示名）**；游戏内窗口标题用**「伊卡洛斯·贰」** |
| Q2 | **TPS 约束聊天文字的滞出速度，并扣除能量**（即"算力"直接限制阅读速度） |
| Q3 | **新增自定义科技/升级线**，不复用现有升级 |
| Q4 | ~~先做可行性 spike~~ → **spike 已完成**（源码调查），选定**路线 A：自建虚拟实体，完全自定义**；分四期，先做 A1（存在且可见） |
| Q4b | 持久化**用官方预留的 `FEATURES_FOR_CUSTOM_MOD_ID_START` + `featureValues`** |
| Q5 | **先完成 M1（接入 DeepSeek）**，之后再做游戏化整合 |

### Q2 的必要护栏（用户选择了"约束阅读速度"，因此这三条不是可选项）

选这条路意味着"问 AI"本身有资源成本，而"多问、多探索"是本 mod 的核心价值。
必须在实现时同时满足以下三条，否则功能会自我否定：

1. **生成与播放解耦**：模型后台一次性生成完，再按当前 TPS 在窗口里滴出。
   这样 LLM 的网络延迟与游戏速率约束互不干扰，"慢"是可预期的节奏而不是卡顿。
2. **TPS 设保底**：即使最低等级也保证可用（建议 ≥ 8 token/s）。
   1 级必须是"明显慢但能用"，不能是"不可用"。
3. **能量耗尽不截断**：改为自动暂停 + 保留上下文，充能后继续，
   窗口显示「能量不足，已暂停」。绝不半句话截断。

另外**能量单价需对齐游戏量纲**：照 `Configs/ModeConfig.cs` 的
`mechaMiningPower`/`mechaResearchPower`/`mechaInstantBuildEnergy` 定价，
并复用游戏"能量不足按 `ratio` 降级"的范式。
（原稿写的 `energyConsumptionCoef` 已证伪：那是机甲 DIY 属性，与科技无关。）

### Q3 带来的工作量（需单独立项）

新增自定义科技/升级线需要：
- 打 `LDB.techs` / 升级表的补丁（M1 之后再评估，可能需要 Harmony 或数据后处理）
- 新增图标资源
- 中英文本地化词条（接 `Locale/<lcid>/` 体系）
- 与现有科技树 UI 的兼容性验证

## 仍然有效的默认建议（未经否决的部分）

以下条目用户未表态，暂按此推进；有异议随时改：

| 项 | 默认 |
| --- | --- |
| 第二台机甲的能力起点 | 极窄：**移动到指定位置 + 建造指定建筑**，先验证乐趣再扩 |
| 资源归属 | 优先消耗玩家背包（与游戏内建造一致），不做独立库存 |
| 指令方式 | 聊天自然语言（复用现有窗口），暂不做专用指令面板 |
| 存档持久化 | 视为**独立立项**——游戏实体必须能被存/读档，工作量大且不可省 |
| 第二台机甲能否被摧毁 | 待定；倾向"可被摧毁但有回收机制"，需在 spike 后讨论 |
