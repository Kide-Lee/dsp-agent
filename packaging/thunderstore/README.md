# 伊卡洛斯·贰 / Icarus II

> 游戏内 AI 助手。按 **`R`** 打开终端，问机制、产线排布、存档状态；
> AI 能读取你当前的存档给建议，并在**你确认之后**执行游戏内操作。

---

## 你需要三样东西

| 项 | 说明 |
| --- | --- |
| 游戏 | Dyson Sphere Program —— **0.10.30.3100 已验证**，其它版本未测 |
| 框架 | **BepInEx 5.4.17+**（社区包自带；5.4.23.5 也验证通过） |
| API Key | 你自己的 DeepSeek Key；或任意 OpenAI 兼容端点（Ollama / LM Studio / vLLM…） |

### BepInEx 版本

**5.4.17 就行**——就是 Thunderstore 社区包 `xiaoye97-BepInEx` 的版本，本 mod 已把它写成
依赖，mod 管理器会自动装。**5.4.23.5 也实测通过**，用哪个都可以。

> 网上流传"Unity 2022.3.62f3 必须用 5.4.23.5"——那是 BepInEx 5.4.23.5 changelog 里的一条
> 修复，但它只影响"连 `Application.isBatchMode` 都取不到"的构建；本游戏的 UnityEngine
> 里该 API 存在，所以 5.4.17 能正常加载（0.10.30.3100 已实测）。

手动装 BepInEx 的话：[BepInEx 5.4.17](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.17)
→ `BepInEx_x64_5.4.17.0.zip`，解压到游戏根目录（和 `DSPGAME.exe` 同级）。

---

## 安装

**推荐（mod 管理器）**

1. BepInEx 5.4.17 会作为依赖自动装好（手动安装的话，先解压一份 5.4.17 到游戏根目录）；
2. 在 Thunderstore / r2modman 里安装本 mod（会自动落到 `BepInEx/plugins/IcarusII/`）；
3. 启动游戏，按 `R`。

**手动安装**

把压缩包里的 `plugins/IcarusII/` 整个文件夹复制到
`<游戏目录>/BepInEx/plugins/`，最终结构应为：

```
BepInEx/plugins/IcarusII/
├── IcarusII.dll
├── sidecar/icarus-ii-sidecar.exe
└── ui/icarusii-ui
```

---

## 首次使用

1. 进游戏后按 `R`；
2. 终端会提示"尚未配置 API Key"，把 Key 粘贴进去并回车（**输入不会回显**，也不会写进聊天记录）；
3. 状态变成「就绪」后就能提问了。

Key 只保存在本机 sidecar 的数据目录里，不会上传到任何第三方服务。

---

## 能做什么

- 问游戏机制、配方、产线排布、卡关了该先修什么；
- 让 AI 读取你**当前存档**的状态（背包、电力、物流、研究…）再给建议；
- 在**你确认之后**执行游戏内操作（例如批量摆放/拆除）；
- 右上角 **⚙ → 模型** 页签：改提供商 / 模型名 / API Key / API 地址
  （把地址指向本地 Ollama 就能完全离线跑）；
- 输入框工具条：插入游戏图标、复选框、文字颜色——和游戏原生输入框同一套弹窗。

---

## 隐私与安全

- **API Key 只存在本机**，聊天内容只发送到**你自己配置的那个端点**；
- sidecar 是本地子进程，只监听 `127.0.0.1`；
- AI 执行任何游戏操作前都会先征求你的确认；
- 卸载：删除 `BepInEx/plugins/IcarusII/` 即可（数据目录 `%APPDATA%/icarus-ii` 可另行删除）。

---

## 常见问题

**按 `R` 没反应**
先看 `BepInEx/LogOutput.log` 里有没有 `Icarus II 0.1.0 已加载`。
没有 → BepInEx 没生效（版本不对 / 没装好）；有 → 建造模式下 `R` 会主动让位给游戏（旋转建筑），退出建造模式再按。

**杀毒软件报毒 / 提示"未知发布者"**
sidecar 是用 Node.js 打包的自包含 exe，且会拉起本地进程，容易被启发式引擎误报。
可用下面的 SHA256 校验文件，或把样本提交给微软误报申诉。介意的话可以先只装 `.dll`，
不复制 `sidecar/`（此时聊天不可用，但界面能看）。

**Linux / Steam Deck / Proton**
Wine 默认优先加载内置 `winhttp.dll`，会盖掉 BepInEx 的代理 DLL。二选一：

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
说明 mod 没加载成功（页签是运行期注入的），先按第一条查日志。

**接口报错 / 401 / 404**
进 ⚙ → 模型，确认 provider、模型名、API 地址是否与你的 Key 匹配；
用本地模型时地址要带 `/v1`（例如 `http://127.0.0.1:11434/v1`）。

**游戏更新后不能用了**
DSP 每次大版本更新都可能改动内部类型。看日志里的报错，或等本 mod 适配新版本。

---

## 文件校验（0.1.0）

```
icarus-ii-sidecar.exe   SHA256 181d06401c71c0278c3903e3bef0cc68232e0b98dba7b2221122bb080af66890
IcarusII.dll            SHA256 293f34f2a3deb49209da7253eb1b7946ae80a0059e4fe6a44bffc33163442212
```

---

## English (short)

In-game AI assistant for **Dyson Sphere Program**. Press `R`, ask about mechanics,
factory layouts or your save; the AI can read your save and, **only after your
confirmation**, perform in-game actions.

Requires **BepInEx 5.4.17+** (5.4.23.5 also tested) and your own **DeepSeek** (or any OpenAI-compatible) API key.
The key is stored locally only; all traffic goes to the endpoint you configure.

---

MIT licensed. Bundles Node.js (MIT) and [pi](https://github.com/earendil-works/pi-mono) (MIT).
BepInEx is **not** redistributed — install it yourself.
