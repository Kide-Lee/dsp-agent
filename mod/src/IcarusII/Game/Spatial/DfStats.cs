using System;
using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;

namespace IcarusII.Game.Spatial
{
    /// <summary>
    /// 黑雾的物质与能量。
    ///
    /// 巢穴的物质统计是"每分钟产量/消耗量"的自维护值（matterProduction / matterConsumption），
    /// 不是分档桶，所以 window 对它无效，只有 unit 生效；这一点必须在结果里说清楚。
    /// </summary>
    internal static class DfStats
    {
        public static JsonValue HiveMatter(EnemyDFHiveSystem hive)
        {
            var obj = JsonValue.NewObject();
            if (hive == null)
            {
                obj.Set("available", false);
                return obj;
            }
            obj.Set("available", true);
            obj.Set("matterProduction", hive.matterProduction);
            obj.Set("matterConsumption", hive.matterConsumption);
            obj.Set("statWindowNative", false);
            obj.Set("statPeriod", EnemyDFHiveSystem.kMatterStatPeriod);
            return obj;
        }

        public static JsonValue BaseMatter(DFGBaseComponent basis)
        {
            var obj = JsonValue.NewObject();
            if (basis == null)
            {
                obj.Set("available", false);
                return obj;
            }
            obj.Set("available", true);
            obj.Set("matterGenPerMin", basis.currentMatterGen);
            obj.Set("energyUsePerMin", basis.currentEnergyUse);
            obj.Set("statWindowNative", false);
            return obj;
        }

        /// <summary>巢穴当前存续的实体数量，用于 storage 的"物质与能量总量"之外的结构统计。</summary>
        public static JsonValue HiveStructure(EnemyDFHiveSystem hive)
        {
            var obj = JsonValue.NewObject();
            if (hive == null) return obj;
            obj.Set("cores", CountStruct(hive.cores.buffer, hive.cores.cursor));
            obj.Set("nodes", CountStruct(hive.nodes.buffer, hive.nodes.cursor));
            obj.Set("connectors", CountStruct(hive.connectors.buffer, hive.connectors.cursor));
            obj.Set("replicators", CountStruct(hive.replicators.buffer, hive.replicators.cursor));
            obj.Set("gammas", CountStruct(hive.gammas.buffer, hive.gammas.cursor));
            obj.Set("turrets", CountStruct(hive.turrets.buffer, hive.turrets.cursor));
            obj.Set("units", CountStruct(hive.units.buffer, hive.units.cursor));
            obj.Set("relays", hive.relays != null ? hive.relays.count : 0);
            obj.Set("tinders", CountStruct(hive.tinders.buffer, hive.tinders.cursor));
            return obj;
        }

        /// <summary>DataPool 的 buffer 是结构体数组，回收槽用 id==0 表示。</summary>
        private static int CountStruct<T>(T[] buffer, int cursor) where T : struct
        {
            if (buffer == null) return 0;
            var upper = Math.Min(cursor, buffer.Length);
            var count = 0;
            for (int i = 1; i < upper; i++)
                if (!EqualityComparer<T>.Default.Equals(buffer[i], default)) count++;
            return count;
        }
    }
}
