using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;
using IcarusII.Game;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    // ---------------------------------------------------------------- 银河星球一览

    /// <summary>
    /// 列出银河里的行星，并按**玩家实际能看到的范围**披露数据。
    ///
    /// 公示规则与游戏星际地图一致（UIPlanetDetail）：
    ///   本地行星 1 级、同恒星 2 级、6 光年内 3 级、更远 4 级；
    ///   已建工厂的星球只要宇宙探索 ≥1 也视为已观察。
    /// 看不到的矿脉只返回 requiredLevel，不泄露类型/储量。
    /// </summary>
    internal sealed class PlanetsTool : IGameToolHandler
    {
        public string Name => "game_get_planets";
        public int TimeoutMs => 20000;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(() => Build(args));
        }

        private static GameToolJobResult Build(JsonValue args)
        {
            var data = JsonValue.NewObject();
            var game = GameMain.data;
            var galaxy = game != null ? game.galaxy : null;
            if (galaxy == null || galaxy.stars == null)
            {
                data.Set("available", false);
                data.Set("display", "银河数据尚未加载");
                return new GameToolJobResult { Data = data };
            }

            int observeLevel = game.history != null ? game.history.universeObserveLevel : 0;
            var localPlanet = GameMain.localPlanet;
            var localStar = GameMain.localStar;
            var playerPos = game.mainPlayer != null ? game.mainPlayer.uPosition : new VectorLF3(0, 0, 0);

            var includeVeins = args["includeVeins"].AsBool(true);
            var planets = JsonValue.NewArray();
            int observedCount = 0;
            int factoryCount = 0;

            for (int s = 0; s < galaxy.stars.Length; s++)
            {
                var star = galaxy.stars[s];
                if (star == null || star.planets == null) continue;
                for (int p = 0; p < star.planets.Length; p++)
                {
                    var planet = star.planets[p];
                    if (planet == null) continue;

                    int required = planet == localPlanet ? 1
                        : planet.star == localStar ? 2
                        : (playerPos - planet.uPosition).magnitude < GalaxyData.LY * 6 ? 3
                        : 4;
                    bool observed = observeLevel >= required
                        || (planet.factory != null && observeLevel >= 1);
                    if (observed) observedCount++;

                    var row = JsonValue.NewObject();
                    row.Set("id", planet.id);
                    row.Set("name", planet.displayName ?? planet.name ?? string.Empty);
                    row.Set("starId", star.id);
                    row.Set("starName", star.displayName ?? star.name ?? string.Empty);
                    row.Set("type", planet.type.ToString());
                    row.Set("orbitRadiusAu", GameToolFormat.Round(planet.orbitRadius, 3));
                    row.Set("orbitIndex", planet.orbitIndex);
                    row.Set("orbitalPeriod", GameToolFormat.Round(planet.orbitalPeriod, 1));
                    row.Set("rotationPeriod", GameToolFormat.Round(planet.rotationPeriod, 1));
                    row.Set("radius", GameToolFormat.Round(planet.radius, 0));
                    row.Set("theme", planet.theme);
                    row.Set("scanned", planet.scanned);
                    row.Set("observed", observed);
                    row.Set("requiredObserveLevel", required);
                    row.Set("observeLevel", observeLevel);

                    bool hasFactory = planet.factory != null;
                    row.Set("hasFactory", hasFactory);
                    row.Set("factoryLoaded", planet.factoryLoaded);
                    if (hasFactory)
                    {
                        factoryCount++;
                        row.Set("buildingCount", CountBuildings(planet.factory));
                        var power = PowerSummary(planet.factory);
                        if (power != null) row.Set("power", power);
                    }

                    if (includeVeins)
                    {
                        if (observed && planet.scanned)
                        {
                            row.Set("veins", VeinSummary(planet));
                        }
                        else
                        {
                            row.Set("veinsHidden", true);
                        }
                    }

                    planets.Add(row);
                }
            }

            data.Set("available", true);
            data.Set("observeLevel", observeLevel);
            data.Set("planetCount", planets.Count);
            data.Set("observedCount", observedCount);
            data.Set("factoryCount", factoryCount);
            data.Set("planets", planets);
            data.Set("note", "未观察的星球只返回基础轨道信息；veinsHidden=true 表示受宇宙探索等级限制。");
            data.Set("display", "银河 " + planets.Count + " 颗行星 · 已观察 " + observedCount
                + " · 有工厂 " + factoryCount);
            return new GameToolJobResult { Data = data };
        }

        private static int CountBuildings(PlanetFactory factory)
        {
            if (factory.entityPool == null) return 0;
            int upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            int count = 0;
            for (int i = 1; i < upper; i++)
                if (factory.entityPool[i].id == i && factory.entityPool[i].protoId > 0) count++;
            return count;
        }

        private static JsonValue PowerSummary(PlanetFactory factory)
        {
            var ps = factory.powerSystem;
            if (ps == null || ps.netPool == null) return null;
            long demand = 0;
            long served = 0;
            long capacity = 0;
            for (int i = 1; i < ps.netCursor && i < ps.netPool.Length; i++)
            {
                var net = ps.netPool[i];
                if (net == null) continue;
                demand += Math.Max(0, net.energyRequired);
                served += Math.Max(0, net.energyServed);
                capacity += Math.Max(0, net.energyCapacity);
            }
            var power = JsonValue.NewObject();
            power.Set("demandKw", GameToolFormat.Round(GameToolFormat.Kw(demand)));
            power.Set("servedKw", GameToolFormat.Round(GameToolFormat.Kw(served)));
            power.Set("capacityKw", GameToolFormat.Round(GameToolFormat.Kw(capacity)));
            power.Set("satisfactionPct", demand <= 0 ? 100 : GameToolFormat.Round(served * 100.0 / demand));
            return power;
        }

        private static JsonValue VeinSummary(PlanetData planet)
        {
            var rows = JsonValue.NewArray();
            var pool = planet.runtimeVeinPool;
            if (pool == null) return rows;
            int cursor = Math.Min(planet.runtimeVeinCursor, pool.Length);
            var agg = new Dictionary<int, long>();
            var counts = new Dictionary<int, int>();
            for (int i = 1; i < cursor; i++)
            {
                var vein = pool[i];
                if (vein.id == 0 || vein.productId <= 0) continue;
                long amount;
                agg.TryGetValue(vein.productId, out amount);
                agg[vein.productId] = amount + Math.Max(0, vein.amount);
                int count;
                counts.TryGetValue(vein.productId, out count);
                counts[vein.productId] = count + 1;
            }
            var keys = new List<int>(agg.Keys);
            keys.Sort();
            foreach (var productId in keys)
            {
                var row = JsonValue.NewObject();
                row.Set("productId", productId);
                row.Set("name", GameToolFormat.ItemName(productId));
                row.Set("iconTag", GameToolFormat.ItemIconTag(productId));
                row.Set("amount", agg[productId]);
                row.Set("veins", counts[productId]);
                rows.Add(row);
            }
            return rows;
        }
    }

    // ---------------------------------------------------------------- 跨行星物流

    /// <summary>
    /// 汇总所有**已加载工厂**的物流塔，按物品给出全银河的供给/需求与具体站点。
    /// 这是跨行星产线分析的入口：先看物品在哪供给，再去对应行星做区域分析。
    /// </summary>
    internal sealed class LogisticsTool : IGameToolHandler
    {
        public string Name => "game_get_logistics";
        public int TimeoutMs => 20000;

        private const int MaxStations = 40;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(() => Build(args));
        }

        private static GameToolJobResult Build(JsonValue args)
        {
            var data = JsonValue.NewObject();
            var game = GameMain.data;
            var galaxy = game != null ? game.galaxy : null;
            if (galaxy == null || galaxy.stars == null)
            {
                data.Set("available", false);
                data.Set("display", "银河数据尚未加载");
                return new GameToolJobResult { Data = data };
            }

            var filter = args["itemFilter"].AsString(null);
            var filterLower = filter != null ? filter.Trim().ToLowerInvariant() : null;
            var planetFilter = args["planetId"].AsInt(0);
            var offset = Math.Max(0, args["offset"].AsInt(0));
            var limit = Math.Min(MaxStations, Math.Max(1, args["limit"].AsInt(20)));

            var stationRows = new List<JsonValue>();
            var itemAgg = new Dictionary<int, ItemAgg>();
            int totalStations = 0;
            int matched = 0;

            for (int s = 0; s < galaxy.stars.Length; s++)
            {
                var star = galaxy.stars[s];
                if (star == null || star.planets == null) continue;
                for (int p = 0; p < star.planets.Length; p++)
                {
                    var planet = star.planets[p];
                    if (planet == null || planet.factory == null) continue;
                    if (planetFilter > 0 && planet.id != planetFilter) continue;
                    var transport = planet.factory.transport;
                    if (transport == null || transport.stationPool == null) continue;

                    int upper = Math.Min(transport.stationCursor, transport.stationPool.Length);
                    for (int i = 1; i < upper; i++)
                    {
                        var station = transport.stationPool[i];
                        if (station == null || station.id == 0) continue;
                        totalStations++;

                        var items = StationItems(station, filterLower);
                        if (items == null || items.aggregates.Count == 0) continue;
                        matched++;
                        if (matched <= offset || stationRows.Count >= limit) continue;

                        var row = JsonValue.NewObject();
                        row.Set("planetId", planet.id);
                        row.Set("planetName", planet.displayName ?? planet.name ?? string.Empty);
                        row.Set("stationId", station.id);
                        row.Set("isStellar", station.isStellar);
                        row.Set("isCollector", station.isCollector);
                        row.Set("warperCount", station.warperCount);
                        row.Set("idleShipCount", station.idleShipCount);
                        row.Set("workShipCount", station.workShipCount);
                        row.Set("idleDroneCount", station.idleDroneCount);
                        row.Set("workDroneCount", station.workDroneCount);
                        row.Set("items", items.value);
                        stationRows.Add(row);

                        foreach (var entry in items.aggregates)
                        {
                            ItemAgg agg;
                            if (!itemAgg.TryGetValue(entry.Key, out agg))
                            {
                                agg = new ItemAgg();
                                itemAgg[entry.Key] = agg;
                            }
                            agg.Supply += entry.Value.Supply;
                            agg.Demand += entry.Value.Demand;
                            agg.Stations++;
                        }
                    }
                }
            }

            var itemRows = JsonValue.NewArray();
            var itemKeys = new List<int>(itemAgg.Keys);
            itemKeys.Sort((a, b) => itemAgg[b].Supply.CompareTo(itemAgg[a].Supply));
            for (int i = 0; i < itemKeys.Count && i < 40; i++)
            {
                var itemId = itemKeys[i];
                var agg = itemAgg[itemId];
                var row = JsonValue.NewObject();
                row.Set("itemId", itemId);
                row.Set("name", GameToolFormat.ItemName(itemId));
                row.Set("iconTag", GameToolFormat.ItemIconTag(itemId));
                row.Set("supply", agg.Supply);
                row.Set("demand", agg.Demand);
                row.Set("stations", agg.Stations);
                itemRows.Add(row);
            }

            var stations = JsonValue.NewArray();
            foreach (var row in stationRows) stations.Add(row);

            data.Set("available", true);
            data.Set("itemFilter", filter);
            data.Set("totalStations", totalStations);
            data.Set("matchedStations", matched);
            data.Set("offset", offset);
            data.Set("limit", limit);
            data.Set("items", itemRows);
            data.Set("stations", stations);
            data.Set("display", "物流塔 " + matched + "/" + totalStations + " 座匹配 · 物品 "
                + itemKeys.Count + " 种");
            return new GameToolJobResult { Data = data };
        }

        private sealed class ItemAgg
        {
            public long Supply;
            public long Demand;
            public int Stations;
        }

        private sealed class SlotAgg
        {
            public long Supply;
            public long Demand;
        }

        private sealed class StationItemView
        {
            public JsonValue value;
            public Dictionary<int, SlotAgg> aggregates;
        }

        private static StationItemView StationItems(StationComponent station, string filterLower)
        {
            var view = new StationItemView
            {
                value = JsonValue.NewArray(),
                aggregates = new Dictionary<int, SlotAgg>(),
            };
            var rows = view.value;
            var aggregates = view.aggregates;
            if (station.storage == null) return view;

            for (int i = 0; i < station.storage.Length; i++)
            {
                var store = station.storage[i];
                if (store.itemId <= 0) continue;
                var name = GameToolFormat.ItemName(store.itemId);
                if (filterLower != null && !name.ToLowerInvariant().Contains(filterLower)
                    && !store.itemId.ToString(CultureInfo.InvariantCulture).Contains(filterLower)) continue;

                var row = JsonValue.NewObject();
                row.Set("itemId", store.itemId);
                row.Set("name", name);
                row.Set("iconTag", GameToolFormat.ItemIconTag(store.itemId));
                row.Set("count", store.count);
                row.Set("max", store.max);
                row.Set("inTransit", store.inc);
                row.Set("localLogic", store.localLogic.ToString());
                row.Set("remoteLogic", store.remoteLogic.ToString());
                rows.Add(row);

                SlotAgg agg;
                if (!aggregates.TryGetValue(store.itemId, out agg))
                {
                    agg = new SlotAgg();
                    aggregates[store.itemId] = agg;
                }
                if (store.remoteLogic == ELogisticStorage.Supply) agg.Supply += store.count;
                if (store.remoteLogic == ELogisticStorage.Demand) agg.Demand += Math.Max(0, store.max - store.count);
            }
            return view;
        }
    }
}
