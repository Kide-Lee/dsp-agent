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

## 界面形态的决定：独立窗口（不是总控面板）

曾经尝试把对话**嵌进游戏的总控面板**（`UIControlPanelWindow`），实现完成并实机跑通，
**最终放弃**，改回独立窗口 + 热键 `R`。原因有两条，都是源码级的事实：

1. **总控面板受科技门控**（`UIGame.cs:1419`）：
   ```csharp
   && (GameMain.history.logisticsSystemUnlocked || GameMain.history.markerUnlocked)
   ```
   未解锁「行星内物流」前按 `I` 完全无效。虽然可以由 mod 代为打开（门控关着时
   我们处理、门控开了不插手，两者条件互斥不会双重触发），但这意味着**界面形态
   取决于存档进度**，而不是玩家的选择。

2. **它属于游戏的「功能窗口」互斥集合**（`UIGame.ShutAllFunctionWindow`，约 30 个窗口）：
   打开背包 `E`、机体信息 `C`、任何建筑详情，都会把总控面板连同对话一起关掉。
   `ManualBehaviour._Close()` 只做 `SetActive(false)`，所以**内容不会丢**，
   但**对话会被不断打断**——而对话的使用节奏是"问一句、去做事、回来看答案"。

**实现已删除**，但完整保留在 git 历史里，需要时可取回：

| 提交 | 内容 |
| --- | --- |
| `5246431` | 对话嵌入总控面板（标签克隆、视图切换、覆盖层方案） |
| `82ecfeb` | 修复坐标系混用导致对话区宽度为负 |
| `af67c27` | 修复强制激活 / 铺满整行 / 双层边框与标题 |
| `adadb4e` | 共用窗口标题、改用宿主配色、补欢迎语 |

取回方式：`git show adadb4e:mod/src/IcarusII/UI/ControlPanelHost.cs > 目标路径`，
或直接 `git revert` 那串提交（注意后面的独立窗口回退是 `5c2e438`）。

若将来游戏的科技门控或窗口互斥行为改变，可基于上面的提交重新评估。

### 这一轮记录下来的、将来仍有用的源码事实

| 事实 | 出处 | 日后的用处 |
| --- | --- | --- |
| `ManualBehaviour._Close()` 只是 `SetActive(false)`，不销毁 | `UI/ManualBehaviour.cs:128-138` | 任何"嵌进游戏窗口"的方案，内容都不会因开关而丢失 |
| 所有热键都带 `!VFInput.inputing` 守卫 | `UI/Game/UIGame.cs:1398-1440` | 在自建输入框里打字不会被游戏当成快捷键 |
| `VFInput._openControlPanel` 尊重玩家改键 | `Controller/VFInputConfig.cs:255` | 想接管某个游戏热键时，用它而不是硬编码 `KeyCode` |
| 无边框全屏/独占全屏/窗口化的枚举与存档字段 | `Data/GameOption.cs:1286,891-892` | 改 `options.xml` 调分辨率时要对齐语义 |



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

---

## 工具面扩展评估（第二轮修订）

> 基线：能核实的都用**本地游戏源码**核对公开 API（`dsp-game/Assets/Scripts/`，附 `文件:行号`）；
> 外部仓库只做了检索（本机 DNS 受限，无法直接抓取），标「待验证」。
> **本轮按用户确认重写，并作废上一版「默认模型没有视觉」的结论（那是错的）。**

### 0. 本轮确认的前提

| # | 确认项 | 对方案的影响 |
| --- | --- | --- |
| 1 | DeepSeek V4 Flash **能读图**；pi 的旧目录把它标成 text-only | 截图可直连主模型，**不需要**另接 VLM 旁路 |
| 2 | 截图**给 AI 看**，也**显示在对话串里给玩家看** | 协议要带图 + 聊天窗要有图片卡片 |
| 3 | 蓝图仓库**在线按需**：[DSPBluePrints/FactoryBluePrints](https://github.com/DSPBluePrints/FactoryBluePrints)，**CC BY-NC-SA 4.0** | 不随包分发；每次结果带署名/许可/链接 |
| 4 | 计算器已有大量成熟实现 | 不再自研公式，改为**集成 + 对拍** |
| 5 | 蓝图生成**接受模板**；已有多种实现 | v1 模板化，参考现成生成器 |
| 6 | Wiki 中英文都要 | 双站点、双缓存、双清洗 |
| 7 | 脚本**必要**，且未来要暴露读写 API 做自动化 | 脚本不是"计算器的附属"，要独立设计能力层 |

### 0.1 实现进度（M4a）

| 项 | 状态 | 落点 |
| --- | --- | --- |
| 模型 `input` 覆盖（打开读图） | ✅ | `ModelEntry.input` / `DEFAULT_DEEPSEEK_MODELS` / `resolveModel` |
| 协议 `tool_result.image` | ✅ | `docs/protocol-v1.md` B.3、两侧类型、`sidecar/test/tools-view.mjs` |
| 能力分组（`game.entity/progress/view.v1`） | ✅ | 协议 B.1、`registry.ts` 的 `TOOL_CAPABILITY`、mod `hello.capabilities` |
| `game_get_entity` / `game_get_entities` | ✅ | `mod/src/IcarusII/Game/Tools/GameEntityTools.cs` |
| `game_get_progress` | ✅ | 同上 |
| `game_capture_view`（JPEG + 可见实体投影） | ✅ | 同上（`GameCamera.CaptureScreenshot` → JPEG q80） |
| 聊天窗图片卡片 | ✅（数据驱动节点，不改 prefab） | `ChatWindow.AttachToolImage` + `UI/ToolImageSprite.cs` |
| `kb_plan` 量化计算器（机器/电力/传送带） | ✅ | `sidecar/src/kb/knowledge.ts` 的 `plan()` + `kb/tools.ts`；测试 `test/kb-plan.mjs` |
| `game_analyze_area` 产线分析（v1 装机口径） | ✅ | `GameEntityTools.cs`；范围=球形切分，不做拓扑 |
| `run_script` 只读脚本层 | ✅ | QuickJS-WASM + `dsp.call` 复用工具注册表；测试 `test/script.mjs` |
| wiki / 蓝图 | ⬜ 未开始 | 见 §6 排序 |

> 验证：`sidecar/test/tools-view.mjs`（36 项）+ `sidecar/test/kb-plan.mjs`（38 项）+ `sidecar/test/script.mjs`（19 项）+ 全套 sidecar 回归 + `ProtocolTests`（200 项断言）
> 全部通过；`scripts/build-mod.sh` 0 warning / 0 error。
> 实机验收仍需进游戏确认截图内容与 UI。

### 1. 修正：视觉链路本来就是通的，卡在我们自己的元数据

上一版说「模型看不见图」是错的。真实链路：

1. `ToolResultMessage.content` **允许** `ImageContent`（`pi-ai/dist/types.d.ts:293-297`）。
2. `openai-completions` 转换器会把工具结果里的图片抽出来，**先发 `role:"tool"` 文本，
   再补一条 `user` + `image_url`**（`pi-ai/dist/api/openai-completions.js:942-1000`）。
3. 唯一闸门是 `model.input.includes("image")`（同文件 `:972`）。

而我们的元数据把它关掉了：

- pi 的 DeepSeek 目录写的是 `"input":["text"]`（`pi-ai/dist/providers/data/deepseek.json`）；
- 自定义 OpenAI 兼容端点被硬编码成 `input: ['text']`（`sidecar/src/agent.ts:877,896`）；
- `ModelEntry` 只有 `{id, name}`，没有把 `input` 暴露给配置（`sidecar/src/config.ts:53-56`）。

⇒ **改 4 处即可打通**：

| # | 改动 | 位置 |
| --- | --- | --- |
| 1 | `ModelEntry` 增加可选 `input`，`resolveModel` 用它覆盖目录默认值 | `config.ts` / `agent.ts` |
| 2 | DeepSeek 两个模型设 `input: ['text','image']`；自定义端点默认 text、可显式开启 | `config.ts:53-56` |
| 3 | 工具结果从「字符串」改为 `content: [{type:'text'},{type:'image'}]` | `tools/registry.ts` 的 `formatToolResult` |
| 4 | 协议 `tool_result.data` 允许携带图片（或独立 `attachments[]`） | `docs/protocol-v1.md` 附录 B.3 |

剩下的才是真工作量：

- **图片体积**：`MAX_FRAME_BYTES = 8 MB`（`protocol.ts:19`）够用，但游戏画面噪声大，PNG 常 >2 MB。
  用 **JPEG q80、长边 ≤1280**（约 150–250 KB），别用 PNG。
- **聊天窗图片卡片**：工具卡里要能显示图（缩略图 + 点击放大 + 等比）。
  这属于**静态 UI，必须进 prefab**（AGENTS.md §2.2），不能用运行期改 `RectTransform` 打补丁。
- **上下文成本**：一张图约 1–2k token，且会长期留在历史里。默认应「缩略图进卡片、原图按需展开」，
  并在设置里给「截图是否进模型上下文」开关。

结论：截图从「要不要做」变成「图片卡片怎么进 prefab + 协议字段怎么定」。

### 2. 9 项修订结论

| # | 候选 | 修订后判断 | 关键变化 | 粗估 |
| --- | --- | --- | --- | --- |
| 1 | Wiki（中英） | ✅ 可行 | 双站点 + 双缓存；数值仍以 KB 为准 | 3–5 人日 |
| 2 | 量化计算器 | ✅ 集成优先 | 不重写公式，做「数据源 + 对拍」 | 2–4 人日 |
| 3 | 蓝图搜索下载 | ✅ 可行 | 在线按需 + 许可合规 | 2–4 人日 |
| 4 | 蓝图生成 | ✅ 模板 v1 | 参考现成生成器，价值在**合法性校验** | 5–8 人日 |
| 5 | 产线分析 | ✅ 可行 | 需要 `game_get_entities` 原语 | 6–10 人日 |
| 6 | 截图 | ✅ **链路已通** | 改元数据 + 协议 + 图片卡片 | 4–6 人日 |
| 7 | 建筑信息 | ✅ 可行 | ID 回收需陈旧检测 | 1–2 人日 |
| 8 | 脚本 | ✅ 必要，独立设计 | QuickJS 沙箱 + 能力 API | 5–8 人日 |
| 9 | 指引阶段 | ✅ 可行 | 「阶段」需定义 | 1–2 人日 |

#### 1. Wiki（中英双站）

- 站点：BWiki（`wiki.biligame.com/dsp`）+ 英文（`dsp-wiki.com` 或 Fandom，**待验证**哪个 API 稳定）。
- 仍是「最小 MediaWiki 客户端 + 固定 host 白名单」，不做通用 HTTP 工具（`design.md:857`）。
- 中英各一套清洗规则与缓存键（`lang + pageid + revid`）；中文按字符、英文按词做搜索。
- 铁律不变：**数字用 KB，wiki 只补攻略/术语/布局经验**；冲突时以本体为准并说明。
- 许可：BWiki 内容多为 CC BY-NC-SA，**只做在线读取 + 署名**，不镜像进包。

#### 2. 量化计算器（集成，不自研）

- 已有实现：[AimsCloudDrive/DSP](https://github.com/AimsCloudDrive/DSP)（量化计算器）、
  [izu-san/dsp-calc-tool](https://github.com/izu-san/dsp-calc-tool)（**待验证**功能覆盖）。
- 我方增量不是公式，而是：①数据换成本体运行时导出（版本永远对）②把结果做成
  `kb_plan(...)` 的结构化输出 ③用现成实现做**对拍测试**（同输入比数值）。
- 必须覆盖：机器数、电力、传送带/分拣器、原料 `/min`、副产物、增产剂模式、矿脉产率。
- 如果某个实现的公式经对拍确认无误，优先移植其**测试向量**而不是代码，避免许可纠缠。

#### 3. 蓝图搜索（在线按需 + 许可合规）

- 关键技巧：header 在 base64 **之前**，对文件发 `Range: bytes=0-8191` 就能拿到
  标题/作者/版本/描述，不必下全文；建筑数用自研 codec 算。
- 元数据索引**本地生成、不随包分发**；全文按需下载，缓存 `%APPDATA%/icarus-ii/blueprints/`。
- 每次结果必须带：`author`、`sourcePath`、仓库链接、`CC BY-NC-SA 4.0`。
- 风险：GitHub API 未认证 60 次/小时；仓库体积不宜整仓镜像；蓝图可能来自不兼容版本。

#### 4. 蓝图生成（模板 v1）

- 已有实现可参考：[bWFuanVzYWth/dspbptk](https://github.com/bWFuanVzYWth/dspbptk)（蓝图工具箱/编解码）、
  [Cosmin1490/DysonSphereProgram-BlueprintGenerator](https://github.com/Cosmin1490/DysonSphereProgram-BlueprintGenerator)（**待验证**）。
- 我方定位：**格式与合法性**。`BlueprintData.CreateNew(string)` 能直接解析
  （`BlueprintData.cs:114`），粘贴合法性由游戏 `CheckBuildConditions()` 判定
  （`BuildTool_BlueprintPaste.cs:1751`），复制有公开入口 `UseToPasteNow()`（`BuildTool_BlueprintCopy.cs:461`）。
- v1：先在游戏内采集「金块」模板 → 参数替换（数量/配方/输入输出方向）→ 生成 → 游戏内校验。
- 合法性：拒绝重叠/穿模/跨带"仙术拼接"；footprint 取 `ItemProto.prefabDesc`；粘贴前预览占地与建筑数。
- **不要承诺**「自然语言一键出任意布局」——那是布局求解器，不是模板工具。

#### 5. 产线分析

- 数据源不变（`EntityData`、`PowerSystem.netPool`、`ProductStat`、`assemblerPool`/`beltPool` 等）。
- 拆两层：`game_get_entities`（分页原语）+ `game_analyze_area`（聚合语义）。
- 三种范围：框选/选中、连通分量、整星球；`ProductStat` 有 6 档时间粒度
  （`ProductionStatistics.cs:23-33`），结果必须声明用了哪档。
- 万级实体要复用现有分帧采集 + 聚合 + 分页。

#### 6. 截图（链路已通，见 §1）

- 形态：`game_capture_view(region?, entities?, annotate?)` → JPEG + 图例。
- 标注建议：世界空间临时 `TextMesh` 标签（编号 → 实体 id），渲染后立即销毁；
  图例给 `{label, entityId}`，让标注成为**可回指坐标**，模型不必靠 OCR。
- 玩家侧：图片卡片 + 点击放大 + 「这张图对应哪些实体」。
- 仍建议保留**文本栅格图**作为补充（同一份快照的另一投影）：
  对"布局对不对"这类问题，字符网格比像素更省 token、更确定；但不是必需项。

#### 7. 建筑信息

- `game_get_entity(planetId, entityId)`：proto/配方/产物统计/功率/分拣器/传送带/仓储/塔/坐标/电网。
- 陈旧检测：返回 `simpleHash + protoId + pos`；调用方可传 `expectProtoId`，
  不匹配返回 `stale_entity`（ID 回收见 `PlanetFactory.cs:495-496,1708`）。

#### 8. 脚本（见 §4 专节）

- 结论：要做，而且要做成**能力层**，不是"计算器附属"。

#### 9. 指引阶段

- `techStates`（`GameHistoryData.cs:12`）、`tutorialUnlocked`（`:11`）、
  `MilestoneData.unlockTick`（`Scenario/Milestone/Data/MilestoneData.cs:4-6`）、研究队列、`GameDesc`。
- 「阶段」是派生概念：定义 8 级阶梯并给出判定依据，让模型能解释。
- 默认只报「当前阶段 + 下一阶段必要条件」，不剧透后续科技细节。

### 3. 蓝图许可（CC BY-NC-SA 4.0）的落地规则

| 事项 | 规则 |
| --- | --- |
| 随包分发 | **不分发**仓库任何内容（否则整包要变 CC BY-NC-SA，与 MIT 冲突） |
| 运行时取用 | 允许；本地缓存；保留 `author`/`desc`/`sourcePath` |
| 索引 | 本地生成，不随包；若必须分发，按 CC BY-NC-SA 并署名 |
| 展示 | 工具结果与聊天卡片都要显示「作者 + 仓库链接 + 许可证」 |
| 改编 | 从仓库蓝图改出来的，仍受 SA 约束；纯模板生成的归我们 |
| 文档 | 新增 `docs/blueprint-v1.md`，把上述规则与格式、codec、校验器写在一起 |

### 4. 脚本层设计（独立立项）

**目标**：不只是算数，而是「批量取数 → 条件判断 → 循环 → 调用工具」的自动化。

**形态**：`run_script(code, {timeoutMs, maxCalls})`，只读 v1。

```js
// 脚本内可用：纯 JS + dsp.call
const f = dsp.call('game_get_entities', { planetId: dsp.planet(), limit: 500 });
const asm = f.rows.filter(e => e.recipeId === 42);
let power = 0;
for (const e of asm) power += dsp.call('game_get_entity', { id: e.id }).powerKw;
return { count: asm.length, powerKw: power };
```

**沙箱**：QuickJS-WASM（`quickjs-emscripten` 的 singlefile 变体，WASM 内联 base64，可进 SEA）。

- 不用 `node:vm`：它不是安全边界（`constructor.constructor` 可逃逸）。
- 不用 Node 权限模型：Node 的 permission 不拦网络。
- QuickJS 与宿主零共享，只暴露注入的 `dsp` 对象。

**能力 API 与工具体系的关系（重要）**

- **一个注册表，两个前端**：脚本的 `dsp.call(name, args)` 与模型看到的 tool 调用走同一份
  schema、risk、超时、路由。不要写两套实现。
- **能力面 ≠ 工具面**：有些能力适合脚本、不适合作为模型工具（如 `entity_at(x,y)`、
  `belt_path(from,to)`）。它们只注入脚本，不广告给模型——正好解决
  `reference-implementations.md` §5「不要几十个细碎工具」的顾虑。
- **写操作**：v1 只读；v2 允许写，但脚本只能产生 **intent**，由 sidecar 汇总成一次
  dry-run 预览 + 一次确认，绝不逐个自动执行。确认令牌沿用现有机制。

**预算**：`maxCalls`（默认 30）、CPU 5s、返回 ≤64 KB、禁止无限循环（超时杀）。

**为什么值得独立做**：脚本能把「10 次工具调用 + 模型推理」压成 1 次调用，
既省 token 又省延迟；而且将来「自动化」（定时巡检、条件触发）必须有它。

**新增依赖的成本**：sidecar 体积 +1.5–3 MB（WASM base64）；要记进 `THIRD-PARTY.md`。

**实现状态（M4d，已落地）**：[sidecar/src/tools/script.ts](../sidecar/src/tools/script.ts)。

打包问题的解法：用 **`@jitl/quickjs-singlefile-cjs-release-sync`**——
WASM 以 base64 内联在 JS 里，esbuild → SEA 单文件链路可以直接带走，不需要额外文件。

实现上改用**同步变体 + 重放**，而不是 Asyncify：

- Asyncify 的异步宿主函数在 `await` 之后创建句柄会踩 WASM 内存（实测 `memory access out of bounds`）；
- 同步变体更小更快；`dsp.call` 因此是**同步**的，对本地数据查询够用；
- 真正的异步（`host.call`）用「`dsp.call` 抛挂起标记 → 宿主执行 → 从头重放并命中 memo」实现；
- 代价：脚本必须可重放（不要用 `Date.now` / `Math.random`），调用上限 50 次。

安全边界写进了 `AGENTS.md` §2.4 的「脚本沙箱」：只注入 `dsp.call`，没有 `process`/`require`/`fetch`；
白名单来自 `host.tools()`，因此权限/路由/超时与模型工具完全一致。

### 5. 补充工具（修订）

| 工具 | 为什么需要 | risk |
| --- | --- | --- |
| `game_capture_view` | 截图（给模型 + 给玩家） | 0 |
| `game_get_entities` | 5/7 的分页原语 | 0 |
| `game_get_entity` | 7 的单体读取 | 0 |
| `game_get_alerts` | 断电/缺料/堵塞/袭击 | 0 |
| `game_get_logistics` | `design.md:8.1` 已列未实现 | 0 |
| `game_get_storage` | 库存 ≠ 产能 | 0 |
| `game_get_selection` | 消除「指定范围」歧义 | 0 |
| `blueprint_fetch` / `blueprint_validate` | 3/4 的取用与安全闸门 | 0 |
| `game_diff` | 验证改动有没有变好 | 0 |
| `kb_tech_path` | 「下一步研究什么」 | 0 |
| `run_script` | 见 §4 | 0（v1 只读） |

### 6. 建议排序

| 批次 | 内容 | 理由 |
| --- | --- | --- |
| **P0** | 视觉链路（元数据 + 协议 + 图片卡片）→ `game_capture_view` | 用户明确要；打通后 4/5 都能复用 |
| **P1** | 7 建筑信息 → 9 指引阶段 → 2 计算器 | 薄封装先落地；计算器价值最高 |
| **P2** | 5 产线分析 → 8 脚本层（只读） | 脚本要有稳定 JSON 面才有意义 |
| **P3** | 1 Wiki（中英）→ 3 蓝图搜索 | 外部依赖，放后面 |
| **P4** | 4 蓝图生成 v1（模板） | 依赖 codec + 校验器 |
| **P5** | 脚本写操作 / 自动化触发 / 文本栅格图 | 按需 |

### 7. 要改的文档

- `design.md`：§8.1 删除「不做截图」，改写为 `game_capture_view`；§9.1 隐私行补
  「截图会进入模型上下文」；§19 决策表第 4 行改判；§8.1 工具表补新工具。
- `docs/protocol-v1.md`：附录 B.3 增加图片附件字段；B.1 增加新工具名与 risk。
- `docs/kb-v1.md`：补全 `building` 字段（算电力/速度必需）与时间粒度口径。
- 新增 `docs/blueprint-v1.md`：格式、TS codec、校验规则、许可与缓存。
- `THIRD-PARTY.md`：加 CC BY-NC-SA 4.0（若分发任何仓库内容——建议不分发）；
  若引入 QuickJS，加其 MIT 许可。
- `AGENTS.md` §2.4：补「脚本沙箱」边界（能力白名单、预算、写操作只能产生 intent）。

### 8. 剩余待确认

1. **图片默认是否进上下文**？每张约 1–2k token 且长期留存；建议默认「缩略图进卡片、原图按需」。
2. **脚本 v1 是否允许写**？建议 v1 只读，v2 走 intent + 一次性确认。
3. **蓝图索引**：确认「本地生成、不随包」这条底线。
4. **计算器**：是移植某个实现的公式，还是直接调用它的库？（后者要评估其许可证）
