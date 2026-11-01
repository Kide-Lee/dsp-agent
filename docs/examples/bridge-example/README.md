# Bridge 示例 mod

最小可用的第三方 manifest 示例。

## 文件

- `ExampleBridgePlugin.cs`：BepInEx 插件，暴露公开静态方法 `Add(int, int)`。
- `IcarusII.tools.json`：把这个方法声明成工具 `add`。

## 使用

1. 用 BepInEx 5.4.17 的 `BepInEx.dll` 编译 `ExampleBridgePlugin.cs` 为 `ExampleBridge.dll`。
2. 把 DLL 与 `IcarusII.tools.json` 一起放进 r2modman 配置档（或手动安装的游戏目录）的 `BepInEx/plugins/ExampleBridge/`。
3. 在 `BepInEx/config/icarus-ii.cfg` 里把 `[Bridge] AllowThirdPartyTools` 改成 `true`，重启游戏。
4. 让 AI 调 `mods_list`，应能看到 `com.example.bridge`；工具名是 `mod_com_example_bridge_add`。

## 说明

- `invoke.type` 必须是插件自己程序集里的类型；这里是 `ExampleBridge.ExampleBridgePlugin`。
- 版本不匹配时整份 manifest 禁用；`risk 2` 的工具必须声明 `undo`。
- 完整字段见 [../../third-party-mod-bridge.md](../../third-party-mod-bridge.md)。
