using System;
using System.Collections.Generic;
using BepInEx;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 三个参考适配器（P2）。它们不是「热门 mod 清单」，只是 manifest 格式的可执行样例；
    /// 外部文件（BepInEx/config/icarus-ii/bridge/&lt;guid&gt;.json 或插件目录 IcarusII.tools.json）优先。
    ///
    /// UXAssist 原本在计划里，但它的公开 API 只有存档导入/导出钩子（Action&lt;BinaryWriter&gt;），
    /// 没有可调用的用户动作；这里换成 BulletTime（公开的变速/暂停方法）。
    /// </summary>
    internal static class ReferenceAdapters
    {
        private static readonly Dictionary<string, string> Adapters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DSPCalculator"] = @"{
  ""schema"": 1,
  ""pluginAssembly"": ""DSPCalculator"",
  ""pluginVersion"": "">=0.5"",
  ""tools"": [
    {
      ""name"": ""open_calculator"",
      ""title"": ""打开计算器"",
      ""description"": ""打开 DSPCalculator 的计算器窗口。"",
      ""risk"": 1,
      ""timeoutMs"": 5000,
      ""parameters"": { ""type"": ""object"", ""properties"": {} },
      ""invoke"": { ""type"": ""DSPCalculator.API"", ""method"": ""OpenOneWindow"", ""args"": [] }
    },
    {
      ""name"": ""open_demand"",
      ""title"": ""按需求打开计算器"",
      ""description"": ""按物品与数量打开计算器窗口；recipeMode=true 时按配方模式打开。"",
      ""risk"": 1,
      ""timeoutMs"": 5000,
      ""parameters"": {
        ""type"": ""object"",
        ""properties"": {
          ""itemId"": { ""type"": ""integer"" },
          ""count"": { ""type"": ""number"" },
          ""recipeMode"": { ""type"": ""boolean"" }
        },
        ""required"": [""itemId"", ""count""]
      },
      ""invoke"": {
        ""type"": ""DSPCalculator.API"",
        ""method"": ""OpenNewWindowWithDemand"",
        ""args"": [""itemId"", ""count"", { ""default"": false }]
      }
    }
  ]
}",
                ["LSTM"] = @"{
  ""schema"": 1,
  ""pluginAssembly"": ""LSTM"",
  ""pluginVersion"": "">=0.9"",
  ""tools"": [
    {
      ""name"": ""toggle_balance_window"",
      ""title"": ""物流站平衡窗口"",
      ""description"": ""打开/关闭 LSTM 的物流站平衡窗口。"",
      ""risk"": 1,
      ""timeoutMs"": 5000,
      ""parameters"": { ""type"": ""object"", ""properties"": {} },
      ""invoke"": { ""type"": ""LSTMMod.LSTM"", ""method"": ""ToggleBalanceWindow"", ""args"": [] }
    },
    {
      ""name"": ""toggle_log_window"",
      ""title"": ""物流站日志窗口"",
      ""description"": ""打开/关闭 LSTM 的日志窗口。"",
      ""risk"": 1,
      ""timeoutMs"": 5000,
      ""parameters"": { ""type"": ""object"", ""properties"": {} },
      ""invoke"": { ""type"": ""LSTMMod.LSTM"", ""method"": ""ToggleLogWindow"", ""args"": [] }
    },
    {
      ""name"": ""max_item_count"",
      ""title"": ""物流站单格上限"",
      ""description"": ""读取 LSTM 计算的远程物流站单格物品上限。"",
      ""risk"": 0,
      ""timeoutMs"": 5000,
      ""parameters"": { ""type"": ""object"", ""properties"": {} },
      ""invoke"": { ""type"": ""LSTMMod.LSTM"", ""method"": ""RemoteStationMaxItemCount"", ""args"": [] }
    }
  ]
}",
                ["BulletTime"] = @"{
  ""schema"": 1,
  ""pluginAssembly"": ""BulletTime"",
  ""pluginVersion"": "">=1.4"",
  ""tools"": [
    {
      ""name"": ""set_speed_ratio"",
      ""title"": ""设置游戏速度"",
      ""description"": ""设置 BulletTime 的游戏速度倍率（1 = 正常）。"",
      ""risk"": 1,
      ""timeoutMs"": 5000,
      ""parameters"": {
        ""type"": ""object"",
        ""properties"": { ""ratio"": { ""type"": ""number"" } },
        ""required"": [""ratio""]
      },
      ""invoke"": { ""type"": ""BulletTime.GameStateManager"", ""method"": ""SetSpeedRatio"", ""args"": [""ratio""] }
    },
    {
      ""name"": ""set_pause_mode"",
      ""title"": ""暂停/继续"",
      ""description"": ""开启或关闭 BulletTime 的暂停模式。"",
      ""risk"": 1,
      ""timeoutMs"": 5000,
      ""parameters"": {
        ""type"": ""object"",
        ""properties"": { ""paused"": { ""type"": ""boolean"" } },
        ""required"": [""paused""]
      },
      ""invoke"": { ""type"": ""BulletTime.GameStateManager"", ""method"": ""SetPauseMode"", ""args"": [""paused""] }
    }
  ]
}",
            };

        internal static Manifest For(PluginInfo info)
        {
            var assembly = ModBridge.AssemblyName(info);
            return Adapters.TryGetValue(assembly, out var json) ? ModManifest.Parse(json) : null;
        }
    }
}
