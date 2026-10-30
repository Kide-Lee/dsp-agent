# Computer Use 调研与 IcarusII 引入研判

- 调研日期：2026-10-30
- 调研基线：`master` / `bec7b96`；调研开始时 `git status --short` 为空。
- 调研范围：外部 computer use 产品/框架/基准，以及本仓库的工具面与安全边界。
- 调研方式：只读。外部资料按「能核验的一手页面」与「未能核验」分开记录；仓库侧读代码、协议与设计文档。未改代码、未构建、未部署。

> 后续：如果目标是「让 AI 调用玩家装的其他 mod」，结论与方案见 [第三方mod能力接入方案.md](第三方mod能力接入方案.md)。

## 0. 结论

**不引入通用 computer use。** 建议维持并强化现在这条「程序状态优先、像素只用来理解」的路线。

三条依据，按重要性排序：

1. **产品形态不允许。** IcarusII 的第一目标（`design.md` §1.1 G1）是「打开聊天窗不打断游戏操作」，而 computer use 的定义就是接管鼠标键盘与屏幕。这两件事在同一个进程里不可能并存：玩家正在开飞船，模型去点菜单，点的是同一个光标。
2. **技术上我们用不到它。** 通用 computer use 存在的唯一理由是「目标软件没有可用 API」。IcarusII 跑在游戏进程内，游戏侧工具全部走游戏公开类型与成员，`game_open_panel` 甚至已经是「程序化操作游戏 UI」的样板（`mod/src/IcarusII/Game/Tools/WriteTools.cs:197` 直接调 `uiGame.OpenTechTree()` 一类的原生方法）；`kb_*` / `wiki_*` / `blueprint_*` 在 sidecar 本地，本来也不需要看屏幕。用像素点击替代进程内 API，是把最强的接口换成最弱的接口。
3. **行业自己已经转向。** 2026 年的 SOTA 方案不是「多截图多点击」，而是 StateAct 那种「代码/状态优先，GUI 子代理只在少数子目标上兜底」（[arXiv:2607.22798](https://arxiv.org/abs/2607.22798)），以及 Sai 那种「把重复操作固化成代码回放」（[simular.ai](https://www.simular.ai/articles/sai-tops-osworld-2-0)）。IcarusII 天然就站在这一侧。

真正值得做的替代动作在 §8：把还没有 API 的两三个面板补成 typed tool，以及给截图加一个「只给模型看、不给坐标动作」的可选 UI 图层。

## 1. 调研范围与证据边界

本机的出网条件限制很硬，必须先讲清楚，否则下面的表格会被误读：

| 站点 | 状态 | 影响 |
| --- | --- | --- |
| `anthropic.com` | 可访问 | Anthropic 的发布说明与研究文章为一手核验 |
| `arxiv.org` / `ar5iv` | 可访问 | OSWorld、UI-TARS、StateAct 原文可达 |
| `simular.ai` | 可访问 | OSWorld 2.0/2.1 的厂商侧结果可达 |
| `benchlm.ai` | 可访问但正文被截断 | 只能看到榜单页头，取不到逐条分数 |
| `platform.openai.com` / `openai.com` | 403 | OpenAI 的具体动作集与限制**未核验** |
| `ai.google.dev` | 连接失败 | Gemini computer use 文档**未核验**（仅在检索结果里看到模型名 `gemini-2.5-computer-use-preview-10-2025`） |
| `learn.microsoft.com` | 连接失败 | 微软 Foundry 的 computer use 文档**未核验**（检索摘要提到 screenshot→action→screenshot 循环） |
| `github.com` / `raw.githubusercontent.com` | 不可达 | browser-use / Stagehand / Playwright MCP 的源码与 README**未核验** |

因此本文的量化结论只引用已核验页面；对 OpenAI、Gemini、微软只做「产品定位」级别的描述，并明确标注未核验。这不影响 §0 的判断，因为判断依据是机制（§3）与架构（§4、§5），不是某家某一版的分数。

## 2. 外部现状

### 2.1 第一方 API

| 提供方 | 形态 | 已核验的事实 |
| --- | --- | --- |
| Anthropic | `computer` 工具类型（2024-10 公测） | 官方原话：让模型「looking at a screen, moving a cursor, clicking buttons, and typing text」；发布时自评「experimental，at times cumbersome and error-prone」；推荐从低风险任务开始（[发布说明](https://www.anthropic.com/news/3-5-models-and-computer-use)） |
| OpenAI | Operator / CUA / Responses API 的 computer-use 预览 | **未核验**（站点 403）。仅能确认这类产品存在，不作为论据 |
| Google | Gemini 的 computer use 预览模型 | **未核验**（站点不可达）。检索结果里出现模型名 `gemini-2.5-computer-use-preview-10-2025`，未取到正文 |
| 微软 | Foundry / Azure 的 computer use 工具 | **未核验**（站点不可达）。检索摘要描述为 screenshot→action→screenshot 循环，与 Anthropic 同构 |

四家的共同骨架是一样的：**截图 → 模型给出坐标与动作 → 执行 → 再截图**。差别在模型、封装与安全策略，不在范式。

### 2.2 专用 GUI 模型与框架

| 项目 | 定位 | 已核验的事实 |
| --- | --- | --- |
| UI-TARS（字节） | 端到端 GUI 模型，纯截图输入 | OSWorld 上 24.6（50 步）/ 22.7（15 步），对比 Claude 的 22.0 / 14.9；ScreenSpot Pro 38.1（[arXiv:2501.12326](https://ar5iv.labs.arxiv.org/html/2501.12326v1)） |
| Simular Sai / Agent S | 商用 computer agent + 开源框架 | OSWorld 1.0 上 Agent S 72.6%，人类基线 72.36%（2025-12）；OSWorld 2.0 上 Sai 73.0% partial / 28.25% binary，$15.70/任务（[文章](https://www.simular.ai/articles/sai-tops-osworld-2-0)） |
| StateAct | 研究框架：状态优先、GUI 兜底 | 见 §4.1 |
| browser-use / Stagehand / Playwright MCP | 浏览器自动化，读 DOM/可访问性树而非像素 | **未核验**（GitHub 不可达）。机制上属于「有结构化接口就先用接口」，与本文结论同向 |

### 2.3 量化基线（决定「值不值得」的那几个数）

| 基准 | 人类 | 最佳自动化 | 说明 |
| --- | --- | --- | --- |
| OSWorld 1.0（369 任务，分钟级） | 72.36% | 12.24%（论文基线，2024-04） | 论文自述失败主因是 GUI grounding 与操作知识（[arXiv:2404.07972](https://arxiv.org/abs/2404.07972)） |
| OSWorld 1.0 | 70–75% | 14.9% → 22.0%（Claude，2024-10） | 官方发布说明 |
| OSWorld 1.0 | 72.36% | 72.6%（Agent S，2025-12） | 已到人类水平，但这是**分钟级**任务 |
| OSWorld 2.0（108 任务，小时级） | — | 73.0% partial / 28.25% binary（Sai，2026-08） | $15.70/任务；Opus 5 70.6%/$23.70，GPT-5.6 Sol 62.6%/$26.62 |
| OSWorld 2.1 | — | 77.3% partial / 41.7% binary（Sai，2026-10） | $14.34/任务 |

读法很重要：**partial 分数高不代表能干活，binary 才是「一次做对」**。2026 年小时级任务的一次成功率仍在 28%–42%，单任务成本 14–27 美元。这个价位对「玩游戏时顺手问一句」的助手是不可接受的。

## 3. computer use 的五个结构性事实

这一节是判断的主体。它们不是「现在还不够强」，而是**范式自带的**。

### 3.1 坐标是数像素数出来的

Anthropic 原文：「Claude looks at screenshots … then counts how many pixels vertically or horizontally it needs to move a cursor」，并称「训练模型准确数像素是关键的」（[研究文章](https://www.anthropic.com/news/developing-computer-use)）。

推论：任何缩放都会引入系统性漂移——HiDPI、窗口缩放、UI 缩放、多显示器、非原生分辨率。DSP 正好踩在这条线上：游戏 UI 的 Canvas 用 `ScaleWithScreenSize`（`dsp-game/Assets/Scripts/UI/Other/UICanvasScalerHandler.cs:92`），同一排按钮在不同分辨率和宽高比下坐标不同；Steam Deck 又是 1280×800。

### 3.2 采样是「翻页」，不是视频

同一篇原文：「The 'flipbook' nature of Claude's view of the screen … means that it can miss short-lived actions or notifications」。

对实时游戏这是致命的：确认弹窗、拾取提示、0.3 秒的动画过渡，翻页式采样直接漏掉，而漏掉之后模型会认为「没发生」，继续下一步。

### 3.3 没有等待原语

框架不提供「等这个动画结束 / 等这个面板加载完」的原语。模型能做的只有「执行一步，再截一张图看看」，失败靠重试——每一步重试都是一次完整请求，成本与延迟成倍放大。

### 3.4 每一步都是一次完整模型调用

一步 = 一张截图（1280×720 按常见的 (宽×高)/750 口径约 1.2k image tokens）+ 一次决策。一个 15 步的任务就是 15 次往返；即使全部顺利，玩家也要等 20–60 秒，并付出 2 万 token 量级。

对照 IcarusII：工具超时 5–20 秒（多数 5–12 秒），一次调用拿到结构化数据。两者不是一个数量级。

### 3.5 Prompt injection 是官方承认的开放问题

Anthropic 明确写了：模型会读互联网截图，其中可能藏指令（[研究文章](https://www.anthropic.com/news/developing-computer-use)）。缓解手段是分类器与提示词，不是硬隔离。

IcarusII 的威胁模型（`design.md` §9.1）已经认定 Wiki 页面、物品描述是不可信输入。把这些不可信输入接到一个「可以点任何东西」的工具上，等于把现有的「写操作必须人工确认」防线拆掉：一次点击的语义是未知的，确认卡无法写清后果。

## 4. 2026 的转向：状态优先，像素兜底

### 4.1 StateAct：截图只是程序状态的有损渲染

论文的核心论点一句话就能概括：「a screenshot is only a lossy rendering of the underlying program state」；不同状态可以产生同样的像素，而代码可以直接读写状态（[arXiv:2607.22798](https://arxiv.org/abs/2607.22798)）。

它的做法是主代理用代码操作程序状态，GUI 子代理只在少数子目标上截图点击——**108 个任务里只有 28 个用到 GUI，占主代理步数的 1.1%**。结果：Claude Opus 4.8 在 OSWorld 2.0 上 binary 从 20.6% 提到 26.9%，partial 从 54.8% 提到 61.6%，**单任务成本约为纯截图方案的 1/9**。

同一篇还有一个反面数据：完全不用 GUI 子代理的纯代码版本只有 45.9% partial，低于纯截图基线 54.8%。也就是说 **GUI 兜底确实有不可替代的场合**，但那是少数场合。

### 4.2 Sai：把重复操作固化成代码

Sai 的复现方法写得很直白：一次任务成功后，解法以代码形式存在，第一百次执行和第一次一样稳（[文章](https://www.simular.ai/articles/sai-tops-osworld-2-0)）。文章里 Chrome Dino 那个例子最能说明问题：Sai 没有「看着画面一下一下跳」，而是量出地面线、障碍速度、自己的输入延迟，写了一个控制循环放进一次执行里——「把点击问题变成编程问题」。

### 4.3 对我们的含义

这两条正好描述 IcarusII 已有的形态：**状态由游戏 API 直接给，操作由 typed tool 直接做，截图只用于理解**。真要补，也该补「结构化状态还不够的那几块」，而不是补一个通用点击器。

## 5. IcarusII 现在是什么

### 5.1 工具面

`docs/tools/index.md` 记录 50 个模型可见工具（`game_*` 37 个、`blueprint_*` 5、`kb_*` 4、`wiki_*` 2、`ask_user` 1、`run_script` 1）。按 `docs/tools/tools.json` 里的 `name + description + parameters` 统计，每次请求要整份发送约 2.5 万字符；再挂一族工具不是零成本。

pi 的内置工具是关闭的（`sidecar/src/agent.ts:3313` `noTools: 'builtin'`），只注册自定义工具。

### 5.2 已经有的「看」

- `game_capture_view`：JPEG，长边 ≤1280、quality 80、≤2 MiB；用主相机临时机位渲染到 RenderTexture 后立即还原，**玩家画面不受影响**（`docs/protocol-v1.md:1320-1330`）。
- 只拍游戏层（`cullingMask = gameLayerMask`），UGUI 不进画面。
- 返回可见实体的屏幕坐标与 `location`（经纬度），图上带编号框与 legend。
- 工具规则里写死了一句：「先按编号定位，再结合 `game_get_entity` / `game_get_entities` 的数值下结论，**不要只凭像素猜配方**」（`docs/tools/game-progress.md`）。

这就是「用像素理解、用状态决策」，和 §4 的结论同构。

### 5.3 已经有的「操作」

`game_open_panel` 是最直接的证据：它不是模拟点击，而是拿到 `UIRoot.instance.uiGame` 后调原生方法开面板，并把「关闭面板」压进撤销栈（`mod/src/IcarusII/Game/Tools/WriteTools.cs:197-250`）。

也就是说，「操作游戏 UI」这条路早就通了，只是走的是进程内 API，不是鼠标。

### 5.4 边界

- `design.md` §1.2：不把 `bash` / 文件系统暴露给模型；不做全自动代打；不直接读写存档。
- `AGENTS.md`：不向模型暴露 shell、任意文件系统、任意 Unity API；例外只有三个蓝图工具。
- 写操作：确认卡 + 一次性令牌 + 单层撤销；risk 1/2 分级；权限三档（只读 / 受限 / 接管）。
- 沙盒门禁 `save.bind.sandbox` 已经就位，但沙盒专属工具尚未实现。

现在 mod 里**没有任何输入模拟**：`Input.GetKeyDown` 只用于读玩家按键（聊天窗热键、对话框回车），没有 `SendInput`、没有 `SetCursor`、没有 `ExecuteEvents` 点击注入；`EventSystem` 只用于读选中对象，以及接收本 mod 自己界面的点击。

### 5.5 开发侧（本会话）的等价物

「我们的 agent」如果指开发这个仓库的 agent，答案不同但结论一样：

| 需求 | 现有工具 | 是否算 computer use |
| --- | --- | --- |
| 看/操作网页 | `browser-harness`（CDP 控 Helium，可点击输入、可复用登录态） | 是浏览器级，不是桌面级 |
| 改 Unity 场景/prefab | MCP for Unity（改组件、截图、dump、跑测试） | 否，走的是编辑器 API |
| 逆向二进制/托管代码/Electron 包 | REA | 否，走的是解析器 |
| 桌面任意 App | **没有** | — |

也就是说，开发侧唯一缺的是「任意桌面 App」，而这个缺口被规则 17 主动关掉了：「GUI 的构建/验证必须后台完成，禁止抢前台焦点或注入键鼠」。制度上已经排除，不需要用技术方案去补。

## 6. 四种候选形态逐个判断

「computer use」在我们这里不是一个东西，至少有四种。分开看才有意义。

| 形态 | 能不能 | 该不该 | 主要理由 |
| --- | --- | --- | --- |
| A. 宿主桌面级（OS 鼠标键盘 + 全屏） | Windows 可行；Linux/X11 可行；Proton+Wayland/Steam Deck 基本不可行 | **不该** | 违反 G1（抢玩家输入）、违反 §1.2/§9.1（等于把整机交给模型）、跨平台不可用 |
| B. 游戏内像素级点击（截图 + 坐标点击） | 可行（进程内 `EventSystem` 注入比 OS 注入更稳） | **不该** | 坐标随分辨率/UI 缩放/语言漂移；不可撤销；确认卡写不清后果；比现有 typed API 更慢更脆 |
| C. 游戏内语义化 UI 自动化（按控件路径定位后注入事件） | 可行 | **不做通用版** | 它等价于「给面板补 API」，但更脆、更难测；只在对某个面板确实无 API 时才值得做单点 |
| D. 只读视觉增强（含 UI 的截图，不给坐标动作） | 可行，且改动很小 | **可以做** | 不影响输入、不扩大权限面，只多一张图；能回答「我的聊天窗为什么显示不对」这类问题 |

### 6.1 A：宿主桌面级

技术上不是做不到：mod 是 BepInEx 插件，Windows 下 P/Invoke `SendInput`、Linux/X11 下 `XTest` 都能注入。但：

- **玩家与模型共用同一个光标。** DSP 是实时游戏，聊天窗是「边玩边问」。模型一动鼠标，玩家正在拖的传送带就断了。G1 把「不打断游戏操作」列为第一目标，A 直接违背。
- **Steam Deck 与 Proton 上没有可靠路径。** gamescope 嵌套合成器、没有 X11 root、窗口焦点由 compositor 管。要么不支持，要么写两套平台代码还不保证能用。
- **威胁模型直接崩掉。** 一次点击可以做任何事，包括点到游戏窗口外面。§9.1 里「写操作强制人确认 + 撤销栈」的前提是「每个工具的后置条件已知」，点击没有后置条件。
- **收益接近零。** 游戏内的状态读取与操作已经有 API；游戏外的事情（装 mod、开文件夹）不是这个产品的职责。

### 6.2 B：游戏内像素级点击

比 A 温和，但问题同源：

- 坐标依赖分辨率、UI 缩放、语言（中英文按钮宽度不同）、窗口模式。
- 点击不可撤销。现有撤销栈是「工具级」的（关面板、退研究、恢复配方），点击只能记录坐标，没法记录语义。
- 实时游戏里 UI 会动（列表滚动、弹窗淡入）。§3.2 的翻页采样会漏状态。
- 代价高：每次点击前要一张图，点完要一张图确认。

真正需要「点一下」的场景（改配方、设过滤器、投料、取成品）现在都有 typed tool，而且都带前置校验与撤销。用点击替代它们是倒退。

### 6.3 C：语义化 UI 自动化

这是唯一「技术上体面」的形态：不碰 OS，用 `EventSystem` + 控件路径（例如 `uiGame.replicator/.../confirm-button`）定位，注入 `PointerClick`。

但它和「给这个面板写一个 typed tool」的工作量差不多，而收益更低：

- typed tool 的返回值是结构化状态，可以断言；点击只能再截一张图去猜。
- typed tool 可以进撤销栈、可以算 risk、可以写确认卡文案；点击不行。
- 控件路径同样会随游戏版本变化，且没有编译期检查。

结论：**哪个面板真的没有 API，就为那个面板写一个工具**，不要造一个通用点击器再让它去猜。

### 6.4 D：只读视觉增强

这个值得做，而且和 computer use 无关：

- 现在 `game_capture_view` 只拍游戏层。玩家问「聊天窗这里为什么叠住了」「设置页这个下拉框为什么是空的」时，模型看不到 UI。
- 加一个 `includeUI=true`（`ScreenCapture.CaptureScreenshotAsTexture` 或把 UI 相机也渲进 RenderTexture），只返回图片，**不提供任何坐标动作**。
- 风险面：截图会进模型上下文，可能含存档名、聊天内容。§9.1 已经披露过这一点，只需要在工具描述里再说一次。

## 7. 如果一定要做：最小形态

假设将来有明确需求（例如某个面板确实没有公开 API，又必须让模型操作），按这个顺序收缩：

1. **不做 OS 输入**，只做进程内 `EventSystem` 注入；这样至少不抢玩家光标、跨平台一致。
2. **按控件路径定位，不按坐标**；坐标只在「图上点一下」这种一次性兜底里出现。
3. **只允许点「游戏自己注册过的可交互控件」**，白名单来自 prefab 快照，不是任意 GameObject。
4. **每一步后置校验**：点完必须用 `game_get_*` 读回状态，读不回就判定失败并停止，不继续下一步。
5. **步数上限 + 一次性令牌 + 沙盒门禁**：挂到 `game.sandbox.v1` 或新的能力串下，默认不注册。
6. **确认卡写清楚**：「要点哪个面板的哪个控件、期望发生什么、失败会怎样」。

但请对照 §6.3：这套东西做完，效果仍然不如给那个面板补一个 typed tool。除非「面板数量多到补不过来」，否则不划算。

## 8. 建议

按优先级：

1. **维持 state-first，不引入通用 computer use。** 在 `design.md` §1.2 明确写一条「不提供屏幕坐标点击/OS 输入注入」，避免以后被当成「还没做的能力」反复提。
2. **补 §20 里剩下的面板**：仪表盘（`game_dashboard_*`）、合成器队列（`game_craft_queue_*`）。这两个是「玩家真的会想让我代劳、但现在没有 API」的地方，也是唯一可能让人误以为「需要 computer use」的地方。
3. **给截图加 `includeUI`（只读）。** 成本低、风险小，能解决「模型看不见自己的界面」这个真实痛点。
4. **开发侧不要引入键鼠注入**（理由见 §5.5）。UI 回归继续走「Unity MCP 改 prefab → dump 对比 → 截图」这条既有链路。
5. **如果将来重启「自动驾驶/长任务」讨论**，它需要的也不是 computer use，而是 `game.task.v1` 那种 start/progress/cancel 的长任务协议（`design.md` §20.1 D2）。

## 9. 不做的理由（备查）

按「如果将来有人问为什么不做」组织：

- **产品**：G1 要求不打断操作；computer use 必然打断。
- **架构**：进程内有强类型 API，没有理由退到像素。
- **安全**：点击没有后置条件，无法纳入 risk/确认/撤销模型；prompt injection 会直接变成任意操作。
- **成本**：一步一张图 + 一次模型往返；15 步任务 2 万 token、20–60 秒，对比现有工具一次 5–12 秒。
- **可靠性**：坐标随分辨率/缩放/语言漂移；翻页采样漏瞬态；没有等待原语。OSWorld 2.x 的一次成功率 28%–42%，单任务 14–27 美元。
- **平台**：Steam Deck / Proton 下 OS 注入没有可靠路径。
- **行业**：2026 的 SOTA 已经在往「状态优先、GUI 兜底」收敛（StateAct 的 GUI 步数占比 1.1%）。

## 10. 需要你拍板的问题

1. **是否把「不做 OS 输入注入」写进 `design.md` §1.2？** 我倾向于写，且写死。
2. **§8 第 2 条（仪表盘 / 合成器队列）是否排期？** 这是目前唯一能让「要不要 computer use」这个问题再次出现的缺口。
3. **§8 第 3 条（`includeUI` 只读截图）要不要做？** 我倾向于做，工作量小，能直接改善「玩家问界面问题」的体验。
4. **开发侧是否需要「任意桌面 App 自动化」？** 如果需要，先说明具体场景；目前的开发链路（浏览器、Unity、反编译）都已有专用工具覆盖。
