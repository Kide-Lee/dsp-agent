using System;
using IcarusII.Common.Json;
using IcarusII.Game;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 返回玩家当前**已解锁**的蓝图相关建筑等级，供生成器选默认档位。
    /// 只读，不碰存档。
    /// </summary>
    internal sealed class BlueprintCapsTool : IGameToolHandler
    {
        public string Name => "game_get_blueprint_caps";
        public int TimeoutMs => 8000;

        private static readonly int[] Belts = { 2001, 2002, 2003 };
        private static readonly int[] Inserters = { 2011, 2012, 2013, 2014 };
        private static readonly int[] Assemblers = { 2303, 2304, 2305 };

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(Build);
        }

        private static GameToolJobResult Build()
        {
            var data = JsonValue.NewObject();
            var history = GameMain.history;
            if (history == null)
            {
                data.Set("available", false);
                data.Set("display", "存档尚未加载");
                return new GameToolJobResult { Data = data };
            }

            int beltTier = Highest(history, Belts);
            int inserterTier = Highest(history, Inserters);
            int assemblerTier = Highest(history, Assemblers);

            data.Set("available", true);
            data.Set("beltTier", beltTier);
            data.Set("beltId", beltTier >= 0 ? Belts[beltTier] : 2001);
            data.Set("inserterTier", inserterTier);
            data.Set("inserterId", inserterTier >= 0 ? Inserters[inserterTier] : 2011);
            data.Set("assemblerTier", assemblerTier);
            data.Set("assemblerId", assemblerTier >= 0 ? Assemblers[assemblerTier] : 2303);
            data.Set("hasPiler", history.ItemUnlocked(2314));
            data.Set("hasStacking", history.ItemUnlocked(2314));
            data.Set("display", "最高档位：建筑 Mk." + (assemblerTier + 1)
                + " · 带子 Mk." + (beltTier + 1) + " · 分拣器 " + (inserterTier + 1));
            return new GameToolJobResult { Data = data };
        }

        private static int Highest(GameHistoryData history, int[] ids)
        {
            for (int i = ids.Length - 1; i >= 0; i--)
                if (history.ItemUnlocked(ids[i])) return i;
            return -1;
        }
    }
}
