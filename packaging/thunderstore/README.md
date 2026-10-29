# 伊卡洛斯·贰 / Icarus II

> 游戏内 AI 助手。按 **`R`** 打开终端，问机制、产线排布、存档状态；
> AI 能读取你当前的存档给建议，并在**你确认之后**执行游戏内操作。

---

## 你需要三样东西

| 项 | 说明 |
| --- | --- |
| 游戏 | Dyson Sphere Program —— **0.10.30.3100 已验证**，其它版本未测 |
| 框架 | **BepInEx 5.4.17+**（r2modman 自动装；5.4.23.5 也验证通过） |
| API Key | 你自己的 DeepSeek Key；或任意 OpenAI 兼容端点（Ollama / LM Studio / vLLM…） |

> DeepSeek Key 在 <https://platform.deepseek.com/> → 左侧 **API Keys** → 创建。
> API 按量计费，和网页版聊天是两套计费；新账号如果没有赠送额度，需要先充值。

### BepInEx 版本

**5.4.17 就行**——就是 Thunderstore 社区包 `xiaoye97-BepInEx` 的版本，本 mod 已把它写成
依赖，mod 管理器会自动装。**5.4.23.5 也实测通过**，用哪个都可以。

> 网上流传"Unity 2022.3.62f3 必须用 5.4.23.5"——那是 BepInEx 5.4.23.5 changelog 里的一条
> 修复，但它只影响"连 `Application.isBatchMode` 都取不到"的构建；本游戏的 UnityEngine
> 里该 API 存在，所以 5.4.17 能正常加载（0.10.30.3100 已实测）。

手动装 BepInEx 的话：[BepInEx 5.4.17](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.17)
→ `BepInEx_x64_5.4.17.0.zip`，解压到游戏根目录（和 `DSPGAME.exe` 同级）。

---

## 安装（推荐：r2modman）

1. 安装 [r2modman](https://thunderstore.io/c/dyson-sphere-program/p/ebkr/r2modman/)；
2. 游戏列表里选 **Dyson Sphere Program**，进入 **Online**，搜索 `IcarusII`
   （作者 `from_dsp_bwiki`），安装；BepInEx 5.4.17 会自动作为依赖装好；
3. 安装完点 **Start modded** 启动游戏——**不要从 Steam 直接启动**，否则配置档不会生效；
4. 进游戏按 `R`。

> r2modman 把 mod 和 BepInEx 放在自己的配置档目录，不会写进游戏根目录。
> 更新也在 r2modman 里完成；不要手动往配置档里覆盖 DLL。

**手动安装（不推荐 / 备用）**

只有不用 r2modman、或需要自己管理游戏目录时才走这条路。手动安装和 r2modman 配置档
是两套目录，**不要混着装**。

先安装 BepInEx 5.4.17+（或 5.4.23.5）到游戏根目录，和 `DSPGAME.exe` 同级；启动一次游戏
让 `BepInEx/plugins/` 生成，再把压缩包里的 `IcarusII` 文件夹（Thunderstore 包路径是
`plugins/IcarusII/`，手动包是 `IcarusII/BepInEx/plugins/IcarusII/`）整个复制到
`<游戏目录>/BepInEx/plugins/`，最终结构应为：

```
BepInEx/plugins/IcarusII/
├── IcarusII.dll
├── sidecar/icarus-ii-sidecar.exe
└── ui/icarusii-ui
```

---

## 首次使用

1. 从 r2modman 点 **Start modded** 进游戏，按 `R`；
2. 第一次会提示尚未配置 AI。点聊天窗右上角的 **⚙** 齿轮——聊天窗会先收起，
   设置窗口会直接停在 mod 新增的「模型」页；
3. 在 **API 密钥** 里粘贴 DeepSeek Key，点「检测」，看到「Key 可用」再往下；
4. 模型列表里点一个模型（萌新用 `DeepSeek V4 Flash`）；用 DeepSeek 时 API 地址留空，
   列表为空或没有可用模型时，点 **获取可用模型** 从账号实际可用的模型里添加；
5. 点右下角 **应用设置**，关掉设置窗口，再按 `R` 打开聊天窗；
6. 状态变成「就绪」后就能提问了。

> 保存后密钥框会清空，显示「已配置，留空不修改」，这是正常的，不是没保存。
>
> Key 只保存在本机 sidecar 的数据目录里，不会回传到游戏界面，也不会写进聊天记录。

---

## 能做什么

- 问游戏机制、配方、产线排布、卡关了该先修什么；
- 让 AI 读取你**当前存档**的状态（背包、电力、物流、研究…）再给建议；
- 在**你确认之后**执行游戏内操作（例如改配方、存储整理、投料、建立方位指示）；
- 生成 / 下载 / 复制蓝图后，对话末尾会出现**蓝图卡片**：显示产量、建筑数、占地与作者，
  点「查看」打开蓝图浏览器，点「使用」直接进入粘贴预览；重进存档也会随历史回放；
- 右上角 **⚙ → 模型** 页签：改提供商 / 模型名 / API Key / API 地址
  （把地址指向本地 Ollama 就能完全离线跑）；
- 输入框工具条：插入游戏图标、复选框、文字颜色——和游戏原生输入框同一套弹窗。

---

## 隐私与安全

- **API Key 只存在本机**，聊天内容发送到**你自己配置的那个端点**；
- 调用 Wiki / 蓝图搜索时会访问对应网站，但不会把整个存档打包上传；
- sidecar 是本地子进程，只监听 `127.0.0.1`；
- AI 执行任何游戏操作前都会先征求你的确认；
- 卸载：在 r2modman 里卸载 IcarusII；手动安装则删除 `BepInEx/plugins/IcarusII/`
  （数据目录 `%APPDATA%/icarus-ii` 可另行删除）。

---

## 常见问题

**装了没效果 / 游戏里没有 mod**
确认你是从 r2modman 的 **Start modded** 启动，而不是从 Steam 直接点开始。
r2modman 的配置档不会注入 Steam 直接启动。

**按 `R` 没反应**
在 r2modman 里打开 **Settings → Locations → Browse profile folder**，
看 `BepInEx/LogOutput.log` 里有没有 `Icarus II 1.0.0 已加载`。
没有 → BepInEx 没生效（依赖没装好 / 启动方式不对）；有 → 建造模式下 `R` 会主动让位给游戏
（旋转建筑），退出建造模式再按。星图、戴森球编辑器等全屏界面也不响应。

**窗口显示「未连接」**
检查 r2modman 配置档里的 `BepInEx/plugins/IcarusII/sidecar/icarus-ii-sidecar.exe`
在不在；多半是被杀软隔离了，把整个 `IcarusII` 文件夹加进白名单。

**杀毒软件报毒 / 提示"未知发布者"**
sidecar 是用 Node.js 打包的自包含 exe，且会拉起本地进程，容易被启发式引擎误报。
可用下面的 SHA256 校验文件，或把样本提交给微软误报申诉。介意的话可以先只装 `.dll`，
不复制 `sidecar/`（此时聊天不可用，但界面能看）。

**Linux / Steam Deck / Proton**
用 r2modman 的 Linux 版 / flatpak，点 **Start modded** 即可，Proton 环境它会自己处理，
不需要手动加 `winhttp`。

只有**手动安装**（不用 r2modman）时才需要二选一：

```ini
# Steam 启动选项
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

或向前缀注册表写入（与安装脚本做的事相同）：

```ini
[Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
"winhttp"="native,builtin"
```

**设置窗口里没有「模型」页**
说明 mod 没加载成功（页签是运行期注入的），先按上面的方法查日志。

**接口报错 / 401 / 404**
进 ⚙ → 模型，确认 provider、模型名、API 地址是否与你的 Key 匹配；
用本地模型时地址要带 `/v1`（例如 `http://127.0.0.1:11434/v1`）。

**游戏更新后不能用了**
DSP 每次大版本更新都可能改动内部类型。看日志里的报错，或等本 mod 适配新版本。

---

## 遇到问题？加群讨论

QQ 群 **1091497260**（伊卡洛斯二号研发中心）。

提问时带上现象描述和 `BepInEx/LogOutput.log` 里对应的报错，能更快定位。

---

## 文件校验（1.0.0）

```
icarus-ii-sidecar.exe   SHA256 5f88bdfb68eb337bd6a64172897a50817a540119f9891f5322110eb2ba726103
IcarusII.dll            SHA256 82ebda43921a49e5e5efe51ca2679f8450b1bee76ecf231e2440342eea7fd437
```

---

## English (short)

In-game AI assistant for **Dyson Sphere Program**. Press `R`, ask about mechanics,
factory layouts or your save; the AI can read your save and, **only after your
confirmation**, perform in-game actions.

Requires **BepInEx 5.4.17+** (5.4.23.5 also tested) and your own **DeepSeek** (or any OpenAI-compatible) API key.
The key is stored locally only; all traffic goes to the endpoint you configure.

Questions? Join the QQ group **1091497260**.

---

MIT licensed. Bundles Node.js (MIT) and [pi](https://github.com/earendil-works/pi-mono) (MIT).
BepInEx is **not** redistributed — r2modman installs it as a dependency, or install it manually.
