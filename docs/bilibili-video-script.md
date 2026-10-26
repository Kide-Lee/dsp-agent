# B站视频稿：伊卡洛斯·贰安装与上手

> 目标时长：约 10 分钟（可剪到 5 分钟）
> 对应版本：`0.4.0`（2026-10-26）
> 适用平台：Windows / Steam；Linux / Steam Deck 见安装段的补充说明
> 安装方式：**r2modman 为主**；手动安装只作为备用方案，不放进主线

---

## 一、发布信息

### 标题备选

1. 《戴森球计划》AI 助手：r2modman 一键安装 + DeepSeek Key 配置
2. 按 R 直接问产线：戴森球 AI 助手「伊卡洛斯·贰」萌新安装指南
3. 给戴森球装个 AI 工程师：能读存档、算配方、出蓝图（附安装与 Key 配置）

### 封面

- 主图用自己录的游戏内聊天窗 + 一张蓝图卡片，背景压暗。
- 大字：`按 R 问 AI`
- 小字：`r2modman 安装 + Key 配置｜萌新保姆级`
- 避免「最强」「百分百」这类绝对化词。

### 标签

`戴森球计划`、`戴森球计划MOD`、`单机游戏`、`AI`、`DeepSeek`、`教程`、`伊卡洛斯`、`DSP`

### 相关链接

- mod 发布页：<https://thunderstore.io/c/dyson-sphere-program/p/from_dsp_bwiki/IcarusII/>
- r2modman：<https://thunderstore.io/c/dyson-sphere-program/p/ebkr/r2modman/>
- BepInEx 5.4.17（手动安装备用）：<https://github.com/BepInEx/BepInEx/releases/tag/v5.4.17>
- DeepSeek 开放平台：<https://platform.deepseek.com/>

---

## 二、录制前准备

- **测试档**：单独复制一个存档，提前解锁蓝图，准备一片可演示的产线。
- **备份**：录制前手动备份一次存档，尤其是要演示写操作时。
- **Key**：专门建一个录制用 Key，录完删掉；录屏前清空剪贴板，避免粘贴时露出历史内容。
- **打码**：登录页的手机号/邮箱、余额、Key 输入框、日志里的敏感路径，全部打码。
- **画面**：1920×1080、60fps；游戏 UI 缩放 100%；路径和按钮用后期大字叠出来，B站手机端才看得清。
- **演示问题**：先试跑，选回答稳定的问法；蓝图配方提前生成一次，失败就换简单的。
- **节奏**：等待 AI 回答的空白剪掉，保留 1 秒加载画面说明「第一次要等」。
- **字幕**：全程硬字幕；`BepInEx/plugins/IcarusII` 这类路径用等宽字体。
- **BGM**：B站音频库的低音量电子/lo-fi，不要盖过口播。
- **精简版**：想压到 5 分钟，删掉「03:10 备用方案」和「08:45 排障」两段，演示只留「阶段建议 + 蓝图生成」。

---

## 三、分镜脚本

### 00:00–00:20｜开场：按 R 叫出 AI

**【画面】** 游戏内建造场景，按下 `R`，聊天窗滑入。快速切三个镜头：提问、工具卡展开、蓝图卡片、确认页。

**【屏幕字】**

`按 R，叫出你的 AI 工程师`

`伊卡洛斯·贰 / Icarus II`

**【口播】**
> 在戴森球里，最耗时间的往往不是画产线，而是算产线：机器要几台、原料够不够、电会不会崩。今天这个 mod，让你在游戏里按一下 R，直接把这些事交给 AI。它叫伊卡洛斯·贰。

---

### 00:20–01:05｜它到底能干什么

**【画面】** 聊天窗依次输入问题；工具卡展开显示读取了哪些数据；回答里出现具体数字；蓝图卡片；确认页。

**【屏幕字】**

`读存档` `算配方` `看产线` `出蓝图` `动手前先问你`

**【口播】**
> 它不是只会背攻略的聊天框。
>
> 第一，它带游戏本体知识库，配方、科技、建筑数据都来自你当前版本的游戏，不靠模型瞎编。
>
> 第二，它能读当前存档：电力、背包、物流、研究、建筑列表，然后给这个档的建议。
>
> 第三，它能按真实配方生成整条产线蓝图，直接进游戏粘贴。
>
> 第四，它也能动手改配方、整理存储、投料，但每次都会先弹确认，你同意它才做。
>
> 简单说，它像个住在游戏里的老玩家，随叫随到，但不会背着你乱点。

---

### 01:05–01:40｜安装前准备

**【画面】** r2modman 的 Thunderstore 页面；mod 的 Thunderstore 页面；游戏库。

**【屏幕字】**

`r2modman`

`BepInEx 自动装`

`不需要 Node.js`

**【口播】**
> 安装只需要三样：游戏本体、r2modman、还有 mod 的 Thunderstore 页面。BepInEx 不用你手动装，r2modman 会作为依赖自动处理。游戏录制时验证的是 0.10.30.3100；你装的时候如果游戏更新了，先看发布页的适配说明。
>
> 还有一点：mod 的 AI 服务已经打包好，不需要你装 Node.js，也不用 npm。

---

### 01:40–03:10｜用 r2modman 安装（主路径）

**【画面】** 安装 r2modman；选择 Dyson Sphere Program；Online 搜索 `IcarusII`；点安装；Installed 列表出现 IcarusII + BepInEx；点 **Start modded**；游戏内按 R。

**【屏幕字】**

`Online → 搜 IcarusII`

`作者：from_dsp_bwiki`

`Start modded`

`不要从 Steam 直接启动`

**【口播】**
> 先装 r2modman。打开它的 Thunderstore 页面，或者搜 r2modman 官网，下载 Windows 安装包，装完打开。
>
> 第一次打开，在游戏列表里搜 Dyson Sphere Program，选它；如果问用哪个平台，选 Steam；配置档用默认的 Default 就行。
>
> 进去之后点左边的 Online，搜索 IcarusII。认准作者 `from_dsp_bwiki`，点安装。它会自动把 BepInEx 5.4.17 一起装上。
>
> 装完看左边的 Installed，应该同时有 IcarusII 和 BepInEx。没有 BepInEx，就在 Online 里搜 BepInEx（作者 `xiaoye97`）单独装一次，或者回到 IcarusII 页面点 **Install with dependencies**。
>
> 重点来了：不要从 Steam 点开始游戏。回到 r2modman，点 **Start modded**。从 Steam 直接启动，mod 不会加载，这是最常见的「装了没效果」。
>
> 游戏起来后按 R，窗口出来就成功了。
>
> 以后更新也在 r2modman 里点 Update；mod 文件在 r2modman 的配置档里，不在游戏根目录，不用手动去游戏文件夹里找。

---

### 03:10–03:35｜搜不到 / Linux / 备用方案

**【画面】** Thunderstore 页面的 `Install with App`；r2modman 的 `Settings → Profile → Import local mod`；Linux 上 r2modman 的 `Start modded`。

**【屏幕字】**

`搜不到 → Import local mod`

`Linux：Start modded 自动处理 Proton`

`手动安装仅备用`

**【口播】**
> 如果 Online 里搜不到，先更新 r2modman 的游戏列表，或者直接去 Thunderstore 页面点 Install with App。还不行，就下载 zip，在 r2modman 的 Settings → Profile → Import local mod 里选它；注意用 Thunderstore 下载的原始 zip，不要解压。
>
> Linux 和 Steam Deck 用 r2modman 的 Linux 版或 flatpak，同样点 Start modded，Proton 环境它会自己处理，不需要手动加 winhttp。只有你完全不用管理器、手动往游戏目录装时，才需要那条 `WINEDLLOVERRIDES`。

---

### 03:35–04:55｜获取 DeepSeek API Key

**【画面】** 浏览器打开 DeepSeek 开放平台；登录；充值/余额页面；左侧 API Keys；创建；复制 `sk-...`。登录信息、余额、Key 全部打码。

**【屏幕字】**

`API 按量计费`

`Key 只显示一次`

`不要外借`

**【口播】**
> mod 装好了，但它本身不带 AI。你需要一个模型服务的 API Key。默认支持 DeepSeek，也可以接任意 OpenAI 兼容端点。萌新建议先用 DeepSeek，国内访问方便，按量计费。mod 本身免费开源，你花的是模型 API 的钱。
>
> 打开 `platform.deepseek.com`，注册登录。注意：网页版 DeepSeek 能免费用，不代表 API 免费；API 是单独计费的。新账号如果没有赠送额度，先在充值页面充一点，够测试就行。
>
> 然后左侧找到 API Keys，点创建 API Key，名字随便起，比如 `icarus-ii`。创建完会给你一串 `sk-` 开头的字符。这个 Key 只显示一次，先复制到记事本。
>
> 不要把 Key 发到群里，也不要贴在视频里；万一泄露，回这个页面删掉，再建一个新的。
>
> 想用中转或者本地 Ollama 也可以，那是进阶玩法；今天先把 DeepSeek 跑通。

---

### 04:55–06:35｜游戏内配置 Key

**【画面】** 游戏内按 R；鼠标圈右上角齿轮；聊天窗收起、设置窗口打开并停在「模型」页；粘贴 Key；点「检测」；模型列表；点「应用设置」；关设置；再按 R；状态「就绪」。

**【屏幕字】**

`右上角齿轮 → 模型`

`检测`

`应用设置`

`状态：就绪`

**【口播】**
> 回到游戏，按 R 打开窗口。第一次用会提示尚未配置 AI。点右上角的齿轮——注意，聊天窗会先收起来，设置窗口会直接停在 mod 新增的「模型」页，别慌。
>
> 左边供应商列表里已经预置了 DeepSeek。右边第一行是 API 密钥，把刚才那串 Key 粘进去。输入框是隐藏的，不会显示明文。
>
> 先别急着保存，点旁边的「检测」。如果显示「Key 可用 · 多少毫秒」，说明 Key 和网络都通了。如果提示连接 sidecar 后才能检测，等窗口状态变成已连接再点。
>
> 下面是模型列表。萌新选 DeepSeek V4 Flash，速度快、日常够用；Pro 更强，复杂分析再切。点一下模型行，让它变成当前模型。API 地址留空就行，DeepSeek 用默认地址。
>
> 最后点右下角的「应用设置」。关掉设置窗口，再按 R 打开聊天窗；状态会变成「就绪」，系统提示「AI 配置已就绪，可以开始提问了」。
>
> 保存后密钥框会清空，显示「已配置，留空不修改」，这是正常的，不是没保存。
>
> 如果检测失败：401 多半是 Key 错了、被删了或者余额不足；404 检查模型名和 API 地址；连不上先检查网络。先看提示，不要反复粘贴。

---

### 06:35–08:05｜实战演示：怎么问才有用

**【画面】** 依次输入四个问题；展示工具卡、回答、蓝图卡片；点「使用」进入粘贴预览；展示「思考」「模型」下拉；可选：确认页红/蓝按钮。

**【屏幕字】**

`Enter 发送 / Shift+Enter 换行 / Esc 关闭`

`思考：高`

`模型：Flash`

**【口播】**
> 配置好之后，直接在输入框打字，Enter 发送，Shift+Enter 换行，Esc 关窗。问题越具体越好。
>
> 比如：我现在这个阶段该先做什么？它会读进度、电力、研究，再给建议。
>
> 再比如：帮我分析脚下这片产线，为什么缺货？它会按区域聚合机器、配方、产量和耗电，告诉你卡在哪。
>
> 算量也一样：量子芯片 60/min，需要多少机器和原料？它走知识库计算，直接给机器数、原料总量和副产物。
>
> 想铺蓝图就说：帮我生成一条 60/min 蓝马达的产线蓝图。生成完，对话末尾会出现蓝图卡片，点「使用」直接进粘贴预览。
>
> 它读存档的时候，聊天窗会显示工具卡，点开能看到它读了什么，不是黑箱。如果它反问你，按面板里的选项选就行。
>
> 输入框右边有「思考」和「模型」两个下拉。复杂问题把思考开到高，日常关闭更快；模型可以随时切 Flash 和 Pro，下一轮生效。输入 `/` 还能打开工具列表，强制这轮调用某个工具，进阶再玩。
>
> 记住：它要动你的存档时，会先弹确认页。红色是拒绝，蓝色是同意。它不会背着你拆家。
>
> 第一次进存档，它会先导出游戏知识库，可能要等几秒；如果它说知识库还在建立，等一会儿再问。

---

### 08:05–08:45｜隐私与安全

**【画面】** 设置页 Key 字段打码；任务管理器里的 sidecar；日志文件（Key 已脱敏）。

**【屏幕字】**

`Key 只存本机`

`只连 127.0.0.1`

`写操作要确认`

**【口播】**
> 最后说安全和隐私。Key 只保存在你本机的 sidecar 配置里，不会回传到游戏界面，也不会写进聊天记录；日志里也会脱敏。
>
> 聊天内容发给你配置的模型端点；如果它调用 Wiki 或蓝图搜索，会向对应网站发查询，但不会把整个存档打包上传。sidecar 只监听 `127.0.0.1`，不对外开端口。
>
> 如果你用第三方中转，聊天内容会经过中转服务器，这一点要自己判断。
>
> 还有，第一次让它动手改存档之前，建议先手动备份存档。它有确认和撤销，但备份最稳。
>
> 卸载：在 r2modman 里卸载 IcarusII；手动安装的话，删 `BepInEx/plugins/IcarusII`。数据目录 `%APPDATA%\icarus-ii` 可以另行删除。

---

### 08:45–09:30｜常见问题

**【画面】** r2modman 的 `Settings → Locations → Browse profile folder`；`BepInEx/LogOutput.log`；杀软隔离区；Steam 游戏页。

**【屏幕字】**

`装了没效果 → Start modded`

`R 没反应 → 建造模式 / 全屏界面`

`401 / 404`

**【口播】**
> 最后几个高频问题。
>
> 第一，装了没效果：确认你是从 r2modman 的 Start modded 启动，不是从 Steam 直接点开始。
>
> 第二，按 R 没反应：先看是不是在建造模式，建造模式下 R 会让给游戏旋转建筑；星图、戴森球编辑器这些全屏界面也不响应。
>
> 第三，窗口显示未连接：在 r2modman 里打开 Settings → Locations → Browse profile folder，检查 `BepInEx/plugins/IcarusII/sidecar/icarus-ii-sidecar.exe` 在不在；多半是被杀软隔离了，加白名单。
>
> 第四，设置里没有「模型」页：说明 mod 没加载，查配置档里的 `BepInEx/LogOutput.log`，搜 Icarus II，看有没有「Icarus II 0.4.0 已加载」。
>
> 第五，401 是 Key 或余额问题；404 是模型名或地址问题。中文显示方块，说明字体没借到。游戏大版本更新后失效，等 mod 适配。

---

### 09:30–09:50｜结尾

**【画面】** 聊天窗、蓝图卡片、Thunderstore 页面、关注引导。

**【屏幕字】**

`发布页 / r2modman：简介 & 置顶评论`

`有问题带日志截图`

**【口播】**
> 发布页、r2modman 链接和安装包放在简介和置顶评论。装的时候卡住，评论区带日志截图，我会看。觉得有用点个赞；下一期讲怎么让它从零规划一条完整产线。拜拜。

---

## 四、简介 / 置顶评论

### 简介模板

```text
这期讲《戴森球计划》的 AI 助手 mod「伊卡洛斯·贰 / Icarus II」：
按 R 呼出，能读存档、算配方、看产线、生成蓝图；写操作需要你确认。

⏱ 时间轴
00:00 它能干什么
01:05 安装前准备
01:40 r2modman 安装
03:10 搜不到 / Linux / 备用方案
03:35 获取 DeepSeek API Key
04:55 游戏内配置 Key
06:35 实战演示
08:05 隐私与安全
08:45 常见问题

📦 mod 发布页：https://thunderstore.io/c/dyson-sphere-program/p/from_dsp_bwiki/IcarusII/
🧰 r2modman：https://thunderstore.io/c/dyson-sphere-program/p/ebkr/r2modman/
🔑 DeepSeek 开放平台：https://platform.deepseek.com/

⚠️ 用 r2modman 安装后，必须点 Start modded 启动，不要从 Steam 直接开。
⚠️ 需要 BepInEx 5.4.17+（r2modman 会自动装）；录制时游戏版本 0.10.30.3100 已验证。
⚠️ Key 只存本机，不要把自己的 Key 发到评论区。
```

### 置顶评论模板

```text
常见问题先看这里：

1. 装了没效果：必须从 r2modman 的 Start modded 启动，不要从 Steam 直接开。
2. 按 R 没反应：退出建造模式；星图/戴森球编辑器里不响应。
3. 未连接：r2modman → Settings → Locations → Browse profile folder，检查
   BepInEx/plugins/IcarusII/sidecar/icarus-ii-sidecar.exe；杀软加白名单。
4. 设置里没有「模型」页：mod 没加载，查配置档里的 BepInEx/LogOutput.log 有没有
   “Icarus II 0.4.0 已加载”。
5. 401：Key 错误/被删/余额不足；404：模型名或 API 地址不对。

发布页和 r2modman 链接在简介；只从发布页下载，别用来路不明的整合包。
```

---

## 五、录制与剪辑清单

- **安装主线只讲 r2modman**，手动安装放到简介/置顶的备用说明里，避免萌新分不清。
- **把 Start modded 单独做一个强调镜头**，这是最高频的踩坑点。
- **不要展示游戏根目录里的 BepInEx**：r2modman 用户根本没有这个目录，展示会误导。
- **日志路径**：r2modman → Settings → Locations → Browse profile folder → `BepInEx/LogOutput.log`。
- **Key 打码**：登录页、余额、Key 字段、剪贴板预览全部打码；录完删掉录制用 Key。
- **路径叠字**：`Online`、`Installed`、`Start modded`、`Settings → Profile` 这些 UI 名称，后期用大字标出来。
- **演示问题**：提前试跑；蓝图生成失败就换简单配方，别在成片里等太久。
- **字幕**：全程硬字幕；专有名词统一为 `r2modman`、`BepInEx`、`DeepSeek`、`Icarus II`。
- **精简版**：删掉「03:10 备用方案」和「08:45 排障」，保留 r2modman 安装、Key 配置、两个演示问题。

---

## 六、备用：手动安装（不推荐）

只有明确不用 r2modman、或者管理器无法使用时才走这条路。手动安装和 r2modman 配置档是两套目录，**不要混着装**。

1. 安装 BepInEx 5.4.17（或 5.4.23.5）到游戏根目录，和 `DSPGAME.exe` 同级：
   `winhttp.dll`、`doorstop_config.ini`、`BepInEx/`。
2. 启动一次游戏，进主菜单后退出，让 BepInEx 生成 `BepInEx/plugins/`。
3. 把 mod 压缩包里的 `plugins/IcarusII/`（手动包是 `IcarusII/BepInEx/plugins/IcarusII/`）
   整个复制到 `<游戏目录>/BepInEx/plugins/`。
4. 启动游戏，按 `R`。

最终结构：

```text
BepInEx/plugins/IcarusII/
├── IcarusII.dll
├── sidecar/icarus-ii-sidecar.exe
└── ui/icarusii-ui
```

Linux / Proton 手动安装时，还需要处理 `winhttp` 覆盖（二选一）：

```ini
# Steam 启动选项
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

```ini
# Proton 前缀注册表
[Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
"winhttp"="native,builtin"
```

用 r2modman 时不需要这一步，`Start modded` 会处理 Proton 环境。

---

## 七、口播事实核对

| 项目 | 正确说法 |
| --- | --- |
| 安装方式 | r2modman → Online → 搜 `IcarusII` → 安装 |
| 启动方式 | r2modman 的 **Start modded**，不要从 Steam 直接启动 |
| BepInEx | 5.4.17+；r2modman 会自动装，5.4.23.5 也可 |
| mod 版本 | 录制时 `0.4.0` |
| 游戏版本 | `0.10.30.3100` 已验证 |
| Key 配置入口 | 聊天窗右上角齿轮 → 模型页 |
| Key 保存 | 点「应用设置」后生效；保存后输入框显示「已配置，留空不修改」 |
| 配置后 | 设置窗口关掉后，要再按 `R` 打开聊天窗 |
| 日志位置 | r2modman 配置档里的 `BepInEx/LogOutput.log` |
| 数据目录 | `%APPDATA%\icarus-ii` |
| 卸载 | r2modman 里卸载；手动安装则删 `BepInEx/plugins/IcarusII` |

### 容易说错的点

| 容易说错 | 正确说法 |
| --- | --- |
| 装 BepInEx 6 | 认准 BepInEx **5.x**；r2modman 会自动装 5.4.17 |
| 把压缩包丢进游戏目录 | r2modman 安装到自己的配置档；手动安装才复制 `IcarusII` 文件夹 |
| 从 Steam 启动 | 必须从 r2modman 的 `Start modded` 启动 |
| 网页版账号就是 API | 网页版免费，API 单独计费，要另外创建 Key |
| Key 以后还能看 | 只显示一次，先存好；泄露就删掉重建 |
| 设置按钮在游戏主菜单 | 在聊天窗右上角、关闭按钮左边 |
| 配置完直接聊天 | 设置关掉后要再按 `R` 打开聊天窗 |
| 401 和 404 混着说 | 401 = Key/余额；404 = 模型名/地址 |
