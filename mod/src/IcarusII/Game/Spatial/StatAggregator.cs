using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using IcarusII.Game.Tools;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>一个物品在一组建筑上的量。</summary>
    internal sealed class ItemAmount
    {
        public int ItemId;
        public string Name;
        public double Amount;
        public long Stored;
        public int Buildings;
        /// <summary>产出减消耗；只有同时取了两侧才有意义，单侧查询保持 0。</summary>
        public double Net;
        public bool HasNet;
    }

    /// <summary>
    /// 把一组建筑聚合成 xlsx 要求的那些数。
    ///
    /// input/output 走物流口径（TrafficStatistics 的 planet 级进出），
    /// production/consumption 走生产口径（ProductionStatistics 的工厂级桶），
    /// 两者不可相加；storage 是当前存量。
    /// </summary>
    internal static class StatAggregator
    {
        /// <summary>当前存量：把每个建筑的存储网格累加。</summary>
        public static List<ItemAmount> Storage(PlanetFactory factory, IReadOnlyCollection<int> entityIds)
        {
            var map = new Dictionary<int, ItemAmount>();
            if (factory == null || factory.entityPool == null) return new List<ItemAmount>();

            foreach (var id in entityIds)
            {
                if (id <= 0 || id >= factory.entityCursor || id >= factory.entityPool.Length) continue;
                var entity = factory.entityPool[id];
                if (entity.storageId <= 0) continue;
                var storage = GetStorage(factory, entity.storageId);
                if (storage == null) continue;
                AccumulateStorage(storage, map);
            }
            return Sorted(map);
        }

        public static List<ItemAmount> Storage(StorageComponent storage)
        {
            var map = new Dictionary<int, ItemAmount>();
            AccumulateStorage(storage, map);
            return Sorted(map);
        }

        private static void AccumulateStorage(StorageComponent storage, Dictionary<int, ItemAmount> map)
        {
            var grids = storage.grids;
            if (grids == null) return;
            for (int i = 0; i < grids.Length; i++)
            {
                var grid = grids[i];
                if (grid.itemId <= 0 || grid.count <= 0) continue;
                if (!map.TryGetValue(grid.itemId, out var entry))
                {
                    entry = new ItemAmount
                    {
                        ItemId = grid.itemId,
                        Name = GameToolFormat.ItemName(grid.itemId),
                    };
                    map[grid.itemId] = entry;
                }
                entry.Amount += grid.count;
                entry.Stored += grid.count;
                entry.Buildings++;
            }
        }

        /// <summary>
        /// 生产/消耗速度：读游戏自己的分档桶（ProductionStatistics），
        /// 按 window 取对应累计值再折算 unit。
        /// </summary>
        /// <summary>
        /// 统计桶是否为空。
        ///
        /// 刚读档或游戏暂停时，桶里的累计值可能全是 0；这不等于"没有产线"。
        /// 调用方需要据此提示模型改用装机口径，而不是让模型答"你没在生产"。
        /// </summary>
        public static bool StatsEmpty(PlanetData planet)
        {
            var factory = planet != null ? planet.factory : null;
            if (factory == null) return true;
            var stats = GameMain.data != null ? GameMain.data.statistics : null;
            var production = stats != null ? stats.production : null;
            var index = planet.factoryIndex;
            if (production?.factoryStatPool == null || index < 0 ||
                index >= production.factoryStatPool.Length) return true;
            var stat = production.factoryStatPool[index];
            return stat?.productPool == null;
        }

        /// <summary>
        /// 生产/消耗的实际统计。
        ///
        /// 游戏的桶按物品存，产出与消耗是同一个 stat 的两段 total；
        /// produce 决定取哪一段，不能两段一起算——那会把"生产了多少"和"消耗了多少"
        /// 混成一个数，回答不出来。
        /// </summary>
        public static List<ItemAmount> ProductionDelta(PlanetData planet,
            IReadOnlyCollection<int> entityIds, StatWindow window, StatUnit unit, bool produce)
        {
            var result = new Dictionary<int, ItemAmount>();
            var factory = planet != null ? planet.factory : null;
            if (factory == null) return new List<ItemAmount>();

            var stats = GameMain.data != null ? GameMain.data.statistics : null;
            var production = stats != null ? stats.production : null;
            var factoryIndex = planet.factoryIndex;
            if (production?.factoryStatPool == null || factoryIndex < 0 ||
                factoryIndex >= production.factoryStatPool.Length)
                return new List<ItemAmount>();

            var factoryStat = production.factoryStatPool[factoryIndex];
            if (factoryStat?.productIndices == null || factoryStat.productPool == null)
                return new List<ItemAmount>();

            // 选中建筑决定"哪些物品属于这个区域"；数值来自工厂桶，
            // 因为游戏的桶不按建筑切分，无法只取子集的真实产量。
            var selected = new Dictionary<int, double>();
            var other = new Dictionary<int, double>();
            CollectRecipeFlow(factory, entityIds, selected, other, 1.0, out _);

            var level = Math.Min((int)window, ProductionStatistics.STAT_LEVEL_COUNT);
            var producedLevel = level + 1;
            var consumedLevel = ProductionStatistics.STAT_LEVEL_COUNT + producedLevel;

            // 产出与消耗都要覆盖：纯消耗品（燃料、弹药）只出现在消耗侧。
            foreach (var itemId in Union(selected, other))
            {
                if (itemId <= 0 || itemId >= factoryStat.productIndices.Length) continue;
                var productIndex = factoryStat.productIndices[itemId];
                if (productIndex <= 0 || productIndex >= factoryStat.productPool.Length) continue;
                var stat = factoryStat.productPool[productIndex];
                if (stat?.total == null || stat.total.Length <= consumedLevel) continue;

                var raw = produce ? stat.total[producedLevel] : stat.total[consumedLevel];
                if (raw == 0) continue;

                var oppositeRaw = produce ? stat.total[consumedLevel] : stat.total[producedLevel];
                var amount = SpatialRequest.ConvertUnit(raw, window, unit);
                var otherAmount = SpatialRequest.ConvertUnit(oppositeRaw, window, unit);
                result[itemId] = new ItemAmount
                {
                    ItemId = itemId,
                    Name = GameToolFormat.ItemName(itemId),
                    Amount = amount,
                    Stored = stat.storageCount,
                    Net = produce ? amount - otherAmount : otherAmount - amount,
                    HasNet = true,
                };
            }

            return Sorted(result);
        }

        /// <summary>
        /// 按配方与建筑数算出的理论值（basis=installed）。
        /// 只覆盖装配机类建筑，不含采矿与物流——这由调用方在结果里如实标注。
        /// </summary>
        public static List<ItemAmount> InstalledDelta(PlanetFactory factory,
            IReadOnlyCollection<int> entityIds, StatWindow window, StatUnit unit, bool produce)
        {
            var produced = new Dictionary<int, double>();
            var consumed = new Dictionary<int, double>();
            CollectRecipeFlow(factory, entityIds, produced, consumed, 1.0, out _);
            var source = produce ? produced : consumed;

            var result = new Dictionary<int, ItemAmount>();
            foreach (var pair in source)
            {
                var entry = new ItemAmount
                {
                    ItemId = pair.Key,
                    Name = GameToolFormat.ItemName(pair.Key),
                    Amount = SpatialRequest.ConvertUnit(pair.Value, window, unit),
                };
                result[pair.Key] = entry;
            }
            return Sorted(result);
        }

        /// <summary>装配机按配方折算的每分钟产出/消耗；同时回填建筑数。</summary>
        private static void CollectRecipeFlow(PlanetFactory factory, IReadOnlyCollection<int> entityIds,
            Dictionary<int, double> produced, Dictionary<int, double> consumed, double scale,
            out int assemblerCount)
        {
            assemblerCount = 0;
            var system = factory != null ? factory.factorySystem : null;
            if (system == null || system.assemblerPool == null || LDB.recipes == null) return;

            foreach (var id in entityIds)
            {
                if (id <= 0 || id >= factory.entityCursor || id >= factory.entityPool.Length) continue;
                var entity = factory.entityPool[id];
                if (entity.assemblerId <= 0) continue;
                if (entity.assemblerId >= system.assemblerPool.Length) continue;

                var assembler = system.assemblerPool[entity.assemblerId];
                if (assembler.recipeId <= 0) continue;
                var recipe = LDB.recipes.Select(assembler.recipeId);
                if (recipe == null || recipe.TimeSpend <= 0) continue;

                assemblerCount++;
                var craftsPerMin = 60.0 / (recipe.TimeSpend / 60.0) * (assembler.speed / 10000.0) * scale;
                AddAmounts(produced, recipe.Results, recipe.ResultCounts, craftsPerMin);
                AddAmounts(consumed, recipe.Items, recipe.ItemCounts, craftsPerMin);
            }
        }

        private static List<int> Union(Dictionary<int, double> a, Dictionary<int, double> b)
        {
            var ids = new List<int>(a.Keys);
            foreach (var key in b.Keys)
                if (!a.ContainsKey(key)) ids.Add(key);
            return ids;
        }

        private static void AddAmounts(Dictionary<int, double> target, int[] ids, int[] counts, double craftsPerMin)
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
        /// 电力：把选中建筑的 consumer/gen/acc 累加。
        ///
        /// 四个量对应 xlsx 的「发电性能、耗电需求、实际发电、蓄电量」：
        /// - capacityKw = genEnergyPerTick 之和（装机发电性能）
        /// - demandKw   = requiredEnergy 之和（耗电需求）
        /// - servedKw   = 电网实际供给按该区域需求占比折算
        /// - storedKwh  = accPool 的 curEnergy 之和
        ///
        /// 单栋建筑没有"已供能"字段，供给是电网级的；
        /// 直接拿 workEnergyPerTick 当供给会把"满载能耗"误报成"实际发电"。
        /// </summary>
        public static JsonValue Power(PlanetFactory factory, IReadOnlyCollection<int> entityIds)
        {
            var obj = JsonValue.NewObject();
            long required = 0;
            long served = 0;
            long capacity = 0;
            long stored = 0;
            long accumulated = 0;
            var consumers = 0;
            var generators = 0;

            if (factory == null || factory.powerSystem == null)
            {
                obj.Set("factoryLoaded", false);
                return obj;
            }

            var system = factory.powerSystem;
            var networks = new HashSet<int>();
            foreach (var id in entityIds)
            {
                if (id <= 0 || id >= factory.entityCursor || id >= factory.entityPool.Length) continue;
                var entity = factory.entityPool[id];

                if (entity.powerConId > 0 && system.consumerPool != null &&
                    entity.powerConId < system.consumerPool.Length)
                {
                    var consumer = system.consumerPool[entity.powerConId];
                    required += Math.Max(0, consumer.requiredEnergy);
                    if (consumer.networkId > 0) networks.Add(consumer.networkId);
                    consumers++;
                }
                if (entity.powerGenId > 0 && system.genPool != null &&
                    entity.powerGenId < system.genPool.Length)
                {
                    var gen = system.genPool[entity.powerGenId];
                    capacity += Math.Max(0, gen.genEnergyPerTick);
                    if (gen.networkId > 0) networks.Add(gen.networkId);
                    generators++;
                }
                if (entity.powerAccId > 0 && system.accPool != null &&
                    entity.powerAccId < system.accPool.Length)
                {
                    var acc = system.accPool[entity.powerAccId];
                    stored += Math.Max(0, acc.curEnergy);
                    accumulated += acc.curPower;
                }
            }

            // 实际供给按所在电网的满足率折算到本区域的需求份额：
            // 区域是电网的子集时，只能这样给出"这块区域实际用上了多少"。
            var satisfaction = 0.0;
            if (networks.Count > 0 && system.netPool != null)
            {
                long netRequired = 0;
                long netServed = 0;
                foreach (var networkId in networks)
                {
                    if (networkId <= 0 || networkId >= system.netCursor ||
                        networkId >= system.netPool.Length) continue;
                    var net = system.netPool[networkId];
                    netRequired += Math.Max(0, net.energyRequired);
                    netServed += Math.Max(0, net.energyServed);
                }
                if (netRequired > 0) satisfaction = netServed * 100.0 / netRequired;
            }
            served = satisfaction > 0 ? (long)(required * satisfaction / 100.0) : 0;

            obj.Set("factoryLoaded", true);
            obj.Set("demandKw", GameToolFormat.Round(GameToolFormat.Kw(required), 2));
            obj.Set("servedKw", GameToolFormat.Round(GameToolFormat.Kw(served), 2));
            obj.Set("capacityKw", GameToolFormat.Round(GameToolFormat.Kw(capacity), 2));
            obj.Set("storedKwh", GameToolFormat.Round(GameToolFormat.Kwh(stored), 2));
            obj.Set("accumulatorKw", GameToolFormat.Round(GameToolFormat.Kw(accumulated), 2));
            obj.Set("satisfactionPct", required > 0
                ? GameToolFormat.Round(served * 100.0 / required, 1) : 100);
            obj.Set("consumers", consumers);
            obj.Set("generators", generators);
            obj.Set("nets", NetworkRows(system, networks));
            return obj;
        }

        /// <summary>按电网给明细：区域横跨多片电网时，光看合计判断不出缺在哪一片。</summary>
        private static JsonValue NetworkRows(PowerSystem system, HashSet<int> networkIds)
        {
            var rows = JsonValue.NewArray();
            if (system?.netPool == null || networkIds == null) return rows;
            foreach (var networkId in networkIds)
            {
                if (networkId <= 0 || networkId >= system.netCursor ||
                    networkId >= system.netPool.Length) continue;
                var net = system.netPool[networkId];
                var row = JsonValue.NewObject();
                row.Set("id", net.id);
                row.Set("demandKw", GameToolFormat.Round(GameToolFormat.Kw(net.energyRequired), 2));
                row.Set("servedKw", GameToolFormat.Round(GameToolFormat.Kw(net.energyServed), 2));
                row.Set("capacityKw", GameToolFormat.Round(GameToolFormat.Kw(net.energyCapacity), 2));
                row.Set("storedKwh", GameToolFormat.Round(GameToolFormat.Kwh(net.energyStored), 2));
                row.Set("satisfactionPct", net.energyRequired > 0
                    ? GameToolFormat.Round(net.energyServed * 100.0 / net.energyRequired, 1) : 100);
                rows.Add(row);
            }
            return rows;
        }

        public static StorageComponent GetStorage(PlanetFactory factory, int storageId)
        {
            var storage = factory != null ? factory.factoryStorage : null;
            if (storage?.storagePool == null) return null;
            if (storageId <= 0 || storageId >= storage.storageCursor ||
                storageId >= storage.storagePool.Length) return null;
            return storage.storagePool[storageId];
        }

        private static List<ItemAmount> Sorted(Dictionary<int, ItemAmount> map)
        {
            var list = new List<ItemAmount>(map.Values);
            list.Sort((a, b) =>
            {
                var byAmount = b.Amount.CompareTo(a.Amount);
                return byAmount != 0 ? byAmount : string.CompareOrdinal(a.Name, b.Name);
            });
            return list;
        }
    }
}
