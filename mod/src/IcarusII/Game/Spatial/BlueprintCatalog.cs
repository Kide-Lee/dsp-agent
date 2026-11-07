using System;
using System.Collections.Generic;
using System.IO;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using IcarusII.Game.Tools;

namespace IcarusII.Game.Spatial
{
    /// <summary>
    /// 蓝图文件的只读视图。
    ///
    /// 蓝图是磁盘上的静态文件，不是运行中的对象：它没有实际产量，只能给理论值；
    /// 也没有位置与流量，所以 location / navigation / storage 对它不适用。
    /// 路径一律限制在游戏蓝图根内，与 BlueprintActions 同一道门。
    /// </summary>
    internal static class BlueprintCatalog
    {
        /// <summary>把用户给的相对路径拼到蓝图根下，并确认没有逃逸。</summary>
        public static string ResolvePath(string pathText)
        {
            if (string.IsNullOrWhiteSpace(pathText))
                throw new SpatialException("蓝图必须给文件路径");
            var root = GameConfig.blueprintFolder;
            if (string.IsNullOrWhiteSpace(root))
                throw new SpatialException("游戏蓝图目录尚未就绪");

            var candidate = Path.IsPathRooted(pathText) ? pathText : Path.Combine(root, pathText);
            var full = Path.GetFullPath(candidate);
            if (!IsInsideRoot(root, full))
                throw new SpatialException("蓝图路径必须位于游戏蓝图目录内");
            if (!File.Exists(full))
                throw new SpatialException($"蓝图文件不存在：{pathText}");
            return full;
        }

        private static bool IsInsideRoot(string root, string full)
        {
            try
            {
                var normalized = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(full, normalized, StringComparison.OrdinalIgnoreCase)) return true;
                return full.StartsWith(normalized + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static BlueprintData Load(string pathText)
        {
            var full = ResolvePath(pathText);
            BlueprintData blueprint;
            try
            {
                blueprint = BlueprintData.CreateFromFile(full);
            }
            catch (Exception ex)
            {
                throw new SpatialException($"读取蓝图失败：{ex.Message}");
            }
            if (BlueprintData.IsNullOrEmpty(blueprint))
                throw new SpatialException("蓝图文件无效或为空");
            return blueprint;
        }

        /// <summary>按物品统计建筑台数；同种建筑合并计数。</summary>
        public static List<(int ItemId, string Name, int Count)> Buildings(BlueprintData blueprint)
        {
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            var buildings = blueprint.buildings;
            if (buildings != null)
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    var itemId = buildings[i].itemId;
                    if (itemId <= 0) continue;
                    if (!map.ContainsKey(itemId)) order.Add(itemId);
                    map.TryGetValue(itemId, out var count);
                    map[itemId] = count + 1;
                }
            }

            var result = new List<(int, string, int)>();
            foreach (var itemId in order)
                result.Add((itemId, GameToolFormat.ItemName(itemId), map[itemId]));
            result.Sort((a, b) => b.Item3.CompareTo(a.Item3));
            return result;
        }

        /// <summary>
        /// 蓝图的理论流量：按配方倍数与建筑台数算每分钟产出/消耗。
        /// 不考虑供电、堵料、物流延迟——蓝图里没有这些状态。
        /// </summary>
        public static void TheoreticalFlow(BlueprintData blueprint, bool produce,
            out Dictionary<int, double> amounts, out int recipeBuildings)
        {
            amounts = new Dictionary<int, double>();
            recipeBuildings = 0;

            var buildings = blueprint.buildings;
            if (buildings == null || LDB.recipes == null) return;

            for (int i = 0; i < buildings.Length; i++)
            {
                var recipeId = buildings[i].recipeId;
                if (recipeId <= 0) continue;
                var recipe = LDB.recipes.Select(recipeId);
                if (recipe == null || recipe.TimeSpend <= 0) continue;

                recipeBuildings++;
                // 蓝图不保存装配机速度档位，按 1 倍算；需要精确值时应读存档里的实际建筑。
                var craftsPerMin = 60.0 * 60.0 / recipe.TimeSpend;
                if (produce)
                    AddAmounts(amounts, recipe.Results, recipe.ResultCounts, craftsPerMin);
                else
                    AddAmounts(amounts, recipe.Items, recipe.ItemCounts, craftsPerMin);
            }
        }

        private static void AddAmounts(Dictionary<int, double> target, int[] ids, int[] counts,
            double craftsPerMin)
        {
            if (ids == null || counts == null) return;
            var length = Math.Min(ids.Length, counts.Length);
            for (int i = 0; i < length; i++)
            {
                var itemId = ids[i];
                if (itemId <= 0 || counts[i] <= 0) continue;
                target.TryGetValue(itemId, out var current);
                target[itemId] = current + counts[i] * craftsPerMin;
            }
        }

        /// <summary>
        /// 理论电力：按建筑原型的耗电/发电字段乘台数。
        /// 蓝图不保存电网连接，所以只能给"装机会用到多少"，给不出满足度。
        /// </summary>
        public static JsonValue PowerEstimate(BlueprintData blueprint)
        {
            var obj = JsonValue.NewObject();
            double demandKw = 0;
            double genKw = 0;
            var consumers = 0;
            var generators = 0;

            var buildings = blueprint.buildings;
            if (buildings == null || LDB.items == null)
            {
                obj.Set("demandKw", 0.0);
                obj.Set("genKw", 0.0);
                return obj;
            }

            for (int i = 0; i < buildings.Length; i++)
            {
                var itemId = buildings[i].itemId;
                if (itemId <= 0) continue;
                var proto = LDB.items.Select(itemId);
                var prefab = proto?.prefabDesc;
                if (prefab == null) continue;

                var work = prefab.workEnergyPerTick;
                if (work > 0)
                {
                    demandKw += work * 60.0 / 1000.0;
                    consumers++;
                }
                var gen = prefab.genEnergyPerTick;
                if (gen > 0)
                {
                    genKw += gen * 60.0 / 1000.0;
                    generators++;
                }
            }

            obj.Set("demandKw", GameToolFormat.Round(demandKw, 2));
            obj.Set("genKw", GameToolFormat.Round(genKw, 2));
            obj.Set("netKw", GameToolFormat.Round(genKw - demandKw, 2));
            obj.Set("consumers", consumers);
            obj.Set("generators", generators);
            obj.Set("basis", "installed");
            return obj;
        }

        /// <summary>蓝图头信息：作者、版本、时间、缩略图图标与布局。</summary>
        public static JsonValue Header(BlueprintData blueprint)
        {
            var obj = JsonValue.NewObject();
            obj.Set("author", blueprint.author ?? string.Empty);
            obj.Set("desc", blueprint.desc ?? string.Empty);
            obj.Set("shortDesc", blueprint.shortDesc ?? string.Empty);
            obj.Set("customVersion", blueprint.customVersion ?? string.Empty);
            obj.Set("gameVersion", blueprint.gameVersion ?? string.Empty);
            obj.Set("time", blueprint.time.ToString("yyyy-MM-dd HH:mm:ss"));
            obj.Set("layout", blueprint.layout.ToString());
            obj.Set("icon0", blueprint.icon0);
            obj.Set("icon1", blueprint.icon1);
            obj.Set("icon2", blueprint.icon2);
            obj.Set("icon3", blueprint.icon3);
            obj.Set("icon4", blueprint.icon4);
            obj.Set("buildingCount", blueprint.buildings != null ? blueprint.buildings.Length : 0);
            obj.Set("areaCount", blueprint.areas != null ? blueprint.areas.Length : 0);
            return obj;
        }
    }
}
