# 第三方 mod 接入 IcarusII

> 面向其他 BepInEx 插件作者、以及想给某个 mod 写适配的高级玩家。
> 代码约束与协议字段以 [protocol-v1.md](protocol-v1.md) 附录 B.1.6 / B.1.7 / B.1.8 为准。

IcarusII 的第三方桥**默认关闭**。玩家要在 `BepInEx/config/icarus-ii.cfg` 里把
`[Bridge] AllowThirdPartyTools` 改成 `true`，重启游戏后才会出现 `mods_*` / `mod_*` 工具。

## 1. 三种接入级别

| 级别 | 你要做什么 | 玩家得到什么 |
| --- | --- | --- |
| 配置 | 什么都不用做（BepInEx `ConfigEntry`） | `mod_<id>_config`：列/读/写你的配置项 |
| manifest | 在插件目录放一份 `IcarusII.tools.json` | `mod_<id>_<name>`：调用你声明的公开静态方法 |
| UI 树 | 什么都不用做 | 没有 API 时，IcarusII 用 `ui_snapshot`/`ui_click` 操作你的 uGUI 面板 |

本文只讲 manifest；配置与 UI 树由 IcarusII 自动处理。

## 2. manifest 放在哪

按优先级从高到低：

1. `BepInEx/config/icarus-ii/bridge/<插件 GUID>.json`（玩家/AI 起草，覆盖下面两项）
2. 插件目录下的 `IcarusII.tools.json`（随包分发，推荐）
3. IcarusII 内置的参考适配器（DSPCalculator / LSTM / BulletTime）

插件被卸载或加载失败时，IcarusII 的下一条 `hello` 不再列出它，工具会从模型上下文里消失。
安装/卸载都需要重启游戏（BepInEx 不支持热加载）。

## 3. 最小例子

插件：

```csharp
using BepInEx;

[BepInPlugin("com.example.bridge", "ExampleBridge", "1.0.0")]
public sealed class ExamplePlugin : BaseUnityPlugin
{
    /// <summary>公开静态方法才能被 manifest 调用。</summary>
    public static int Add(int left, int right) => left + right;
}
```

插件目录下的 `IcarusII.tools.json`：

```json
{
  "schema": 1,
  "pluginGuid": "com.example.bridge",
  "pluginVersion": ">=1.0",
  "tools": [
    {
      "name": "add",
      "title": "加法",
      "description": "返回两个整数之和。",
      "risk": 0,
      "timeoutMs": 5000,
      "parameters": {
        "type": "object",
        "properties": {
          "left": { "type": "integer" },
          "right": { "type": "integer" }
        },
        "required": ["left", "right"]
      },
      "invoke": {
        "type": "ExamplePlugin",
        "method": "Add",
        "args": ["left", "right"]
      }
    }
  ]
}
```

玩家会在工具列表里看到 `mod_com_example_bridge_add`（`id` 由 IcarusII 从 GUID 归一化生成）。

## 4. 字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `schema` | 是 | 固定 `1` |
| `pluginGuid` | 二选一 | 插件的 `BepInPlugin.GUID` |
| `pluginAssembly` | 二选一 | 程序集名；参考适配器用它，避免依赖 GUID |
| `pluginVersion` | 否 | 版本范围，如 `>=1.2 <2`；空格分隔多个条件。不匹配整份禁用 |
| `tools[]` | 是 | 工具列表 |

工具字段：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `name` | 是 | 短名，只允许 `[a-z0-9_]`；加载时补成 `mod_<id>_<name>` |
| `title` | 否 | 显示名，默认同 `name` |
| `description` | 否 | 模型看到的说明；写清楚“做什么、有什么副作用” |
| `risk` | 否 | `0` 只读（默认）/ `1` 可逆写 / `2` 改存档写 |
| `timeoutMs` | 否 | 默认 8000；只应声明快方法（同步调用，不能中断） |
| `parameters` | 是 | JSON Schema；直接作为工具参数 schema |
| `invoke` | 是 | `{ type, method, args }` |
| `undo` | risk 2 必填 | 反向调用；`{ type, method, args }` |

`args` 的每一项：

- 字符串：取同名工具参数；
- `{"const": x}`：字面量；
- `{"default": x}`：参数缺省时的值；
- `{"arg": "name"}`：等价于字符串。

## 5. 调用约束

- `type` 必须是**声明插件自己程序集**里的类型；不能指向 `UnityEngine` 或游戏内部。
- `method` 必须是公开静态方法；按参数个数匹配。
- 参数类型只支持 `int/long/float/double/bool/string/enum`；不支持对象、委托、`ref/out`。
- 返回值支持基本类型/字符串/枚举/void；其它对象转 `ToString()`。
- 同步执行在主线程；不要在里面做重活。

## 6. 安全与失败

- `risk > 0` 的工具走 IcarusII 的确认卡 + 一次性令牌；`risk 2` 还要求 `undo`，否则不注册。
- 没有 `undo` 的写工具会把撤销栈顶标记为不可撤销，`game_undo_last_write` 不会跳过它。
- manifest 解析失败、版本不匹配、方法找不到：该工具不注册，插件仍会出现在 `mods_list` 里。
- 加载成功时 BepInEx 日志会有 `[mods] 已加载 N 个 manifest 工具`。

## 7. 怎么验证

1. 打开 `[Bridge] AllowThirdPartyTools`，重启游戏。
2. 让 AI 调 `mods_list`：应能看到你的插件和 `configCount`。
3. 启用工具后应出现 `mod_<id>_<name>`；调用一次，看返回与日志。
4. 卸载插件并重启：`mods_list` 与工具都应消失。

## 8. 示例

完整示例（插件源码 + manifest）在 [examples/bridge-example/](examples/bridge-example/README.md)。

## 9. 不建议暴露的东西

- 删档、批量拆除、不可逆的作弊入口；
- 依赖 UI 焦点/鼠标位置的方法；
- 会长时间阻塞主线程的方法（manifest 没有中断机制）。

这类能力要么做成 `risk 2 + undo`，要么留给玩家手动操作。
