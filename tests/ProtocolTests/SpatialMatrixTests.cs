using System;
using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;

namespace IcarusII.Tests
{
    /// <summary>
    /// 支持矩阵必须与 docs/dsp-agent.xlsx 完全一致。
    ///
    /// 这张矩阵是唯一真相来源：xlsx 里填了内容，工具才允许调用；
    /// 留空即不支持。任何一边改了另一边没跟上，都会在这里断掉。
    /// 表里的已支持组合是逐格抄下来的，不是按规则算出来的；
    /// 单元格里写「（不适用）」的按不支持处理。
    /// </summary>
    internal static class SpatialMatrixTests
    {
        private static readonly (string Method, string Type)[] Expected =
        {
            ("list", "star_system"),
            ("list", "star"),
            ("list", "planet"),
            ("list", "power_grid"),
            ("list", "factory"),
            ("list", "df_hive"),
            ("list", "df_base"),
            ("list", "world_box"),
            ("list", "planet_patch"),
            ("list", "blueprint"),
            ("storage", "star_system"),
            ("storage", "star"),
            ("storage", "planet"),
            ("storage", "power_grid"),
            ("storage", "factory"),
            ("storage", "df_hive"),
            ("storage", "df_base"),
            ("storage", "dyson_sphere"),
            ("storage", "world_box"),
            ("storage", "planet_patch"),
            ("input", "star_system"),
            ("input", "planet"),
            ("input", "power_grid"),
            ("input", "factory"),
            ("input", "df_hive"),
            ("input", "df_base"),
            ("input", "world_box"),
            ("input", "planet_patch"),
            ("input", "blueprint"),
            ("output", "star_system"),
            ("output", "star"),
            ("output", "planet"),
            ("output", "power_grid"),
            ("output", "factory"),
            ("output", "df_hive"),
            ("output", "df_base"),
            ("output", "dyson_sphere"),
            ("output", "world_box"),
            ("output", "planet_patch"),
            ("output", "blueprint"),
            ("production", "star_system"),
            ("production", "star"),
            ("production", "planet"),
            ("production", "power_grid"),
            ("production", "factory"),
            ("production", "df_hive"),
            ("production", "df_base"),
            ("production", "dyson_sphere"),
            ("production", "world_box"),
            ("production", "planet_patch"),
            ("production", "blueprint"),
            ("consumption", "star_system"),
            ("consumption", "star"),
            ("consumption", "planet"),
            ("consumption", "power_grid"),
            ("consumption", "factory"),
            ("consumption", "df_hive"),
            ("consumption", "df_base"),
            ("consumption", "dyson_sphere"),
            ("consumption", "world_box"),
            ("consumption", "planet_patch"),
            ("consumption", "blueprint"),
            ("power", "star_system"),
            ("power", "star"),
            ("power", "planet"),
            ("power", "power_grid"),
            ("power", "factory"),
            ("power", "df_hive"),
            ("power", "df_base"),
            ("power", "dyson_sphere"),
            ("power", "world_box"),
            ("power", "planet_patch"),
            ("power", "blueprint"),
            ("parent", "star_system"),
            ("parent", "star"),
            ("parent", "planet"),
            ("parent", "power_grid"),
            ("parent", "factory"),
            ("parent", "df_hive"),
            ("parent", "df_base"),
            ("parent", "dyson_sphere"),
            ("parent", "world_box"),
            ("parent", "planet_patch"),
            ("parent", "blueprint"),
            ("location", "star_system"),
            ("location", "star"),
            ("location", "planet"),
            ("location", "power_grid"),
            ("location", "factory"),
            ("location", "df_hive"),
            ("location", "df_base"),
            ("location", "dyson_sphere"),
            ("location", "world_box"),
            ("location", "planet_patch"),
            ("navigation", "star_system"),
            ("navigation", "star"),
            ("navigation", "planet"),
            ("navigation", "power_grid"),
            ("navigation", "factory"),
            ("navigation", "df_hive"),
            ("navigation", "df_base"),
            ("navigation", "dyson_sphere"),
            ("navigation", "world_box"),
            ("navigation", "planet_patch"),
            ("information", "star"),
            ("information", "planet"),
            ("information", "df_hive"),
            ("information", "dyson_sphere"),
            ("information", "blueprint"),
            ("note", "star_system"),
            ("note", "star"),
            ("note", "planet"),
            ("note", "power_grid"),
            ("note", "factory"),
            ("note", "world_box"),
            ("note", "planet_patch"),
            ("note", "blueprint"),
            ("list", "building"),
            ("list", "df_space_building"),
            ("list", "df_ground_building"),
            ("storage", "building"),
            ("storage", "vein"),
            ("storage", "vegetation"),
            ("storage", "df_space_building"),
            ("storage", "df_ground_building"),
            ("input", "building"),
            ("input", "df_space_building"),
            ("input", "df_ground_building"),
            ("output", "building"),
            ("output", "vein"),
            ("output", "df_space_building"),
            ("output", "df_ground_building"),
            ("production", "building"),
            ("production", "df_space_building"),
            ("production", "df_ground_building"),
            ("consumption", "building"),
            ("consumption", "df_space_building"),
            ("consumption", "df_ground_building"),
            ("power", "building"),
            ("power", "df_space_building"),
            ("power", "df_ground_building"),
            ("parent", "building"),
            ("parent", "vein"),
            ("parent", "vegetation"),
            ("parent", "df_space_building"),
            ("parent", "df_ground_building"),
            ("parent", "world_position"),
            ("parent", "planet_position"),
            ("location", "building"),
            ("location", "vein"),
            ("location", "vegetation"),
            ("location", "df_space_building"),
            ("location", "df_ground_building"),
            ("location", "world_position"),
            ("location", "planet_position"),
            ("navigation", "building"),
            ("navigation", "vein"),
            ("navigation", "vegetation"),
            ("navigation", "df_space_building"),
            ("navigation", "df_ground_building"),
            ("navigation", "world_position"),
            ("navigation", "planet_position"),
            ("note", "building"),
            ("list", "player"),
            ("storage", "player"),
            ("storage", "construction_drone"),
            ("storage", "delivery_courier"),
            ("storage", "station_drone"),
            ("storage", "station_ship"),
            ("storage", "trash"),
            ("parent", "construction_drone"),
            ("parent", "delivery_courier"),
            ("parent", "station_drone"),
            ("parent", "station_ship"),
            ("parent", "dyson_rocket"),
            ("parent", "combat_drone"),
            ("parent", "combat_fleet"),
            ("parent", "df_space_unit"),
            ("parent", "df_ground_unit"),
            ("parent", "trash"),
            ("location", "player"),
            ("location", "construction_drone"),
            ("location", "delivery_courier"),
            ("location", "station_drone"),
            ("location", "station_ship"),
            ("location", "dyson_rocket"),
            ("location", "combat_drone"),
            ("location", "combat_fleet"),
            ("location", "df_space_unit"),
            ("location", "df_ground_unit"),
            ("location", "trash"),
            ("information", "player"),
            ("information", "construction_drone"),
            ("information", "delivery_courier"),
            ("information", "station_drone"),
            ("information", "station_ship"),
            ("information", "dyson_rocket"),
            ("information", "combat_drone"),
            ("information", "combat_fleet"),
            ("information", "df_space_unit"),
            ("information", "df_ground_unit"),
            ("information", "trash"),
            ("navigation", "player"),
            ("navigation", "construction_drone"),
            ("navigation", "delivery_courier"),
            ("navigation", "station_drone"),
            ("navigation", "station_ship"),
            ("navigation", "dyson_rocket"),
            ("navigation", "combat_drone"),
            ("navigation", "combat_fleet"),
            ("navigation", "df_space_unit"),
            ("navigation", "df_ground_unit"),
            ("navigation", "trash"),
        };

        public static void Run()
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (method, type) in Expected)
                expected.Add(method + "\u0000" + type);

            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in new[] { SpatialKind.Region, SpatialKind.Point, SpatialKind.Motion })
            {
                foreach (var type in SpatialTypes.TypesOf(kind))
                {
                    for (int i = 0; i <= (int)SpatialMethod.Note; i++)
                    {
                        var method = (SpatialMethod)i;
                        if (SpatialTypes.Supports(method, type))
                            actual.Add(SpatialTypes.NameOf(method) + "\u0000" + type);
                    }
                }
            }

            var missing = new List<string>();
            foreach (var key in expected)
                if (!actual.Contains(key)) missing.Add(key.Replace('\u0000', ' '));

            var extra = new List<string>();
            foreach (var key in actual)
                if (!expected.Contains(key)) extra.Add(key.Replace('\u0000', ' '));

            missing.Sort(StringComparer.Ordinal);
            extra.Sort(StringComparer.Ordinal);

            foreach (var item in missing)
                Program.Check($"矩阵缺少（xlsx 已填、代码未支持）：{item}", false);
            foreach (var item in extra)
                Program.Check($"矩阵多出（代码支持、xlsx 留空）：{item}", false);

            Program.Check("支持组合总数与 xlsx 一致", extra.Count == 0 && missing.Count == 0);
            Program.Check($"已支持组合数：{Expected.Length}", Expected.Length > 200);
        }
    }
}
