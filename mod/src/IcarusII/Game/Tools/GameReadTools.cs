using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using IcarusII.Common.Json;
using IcarusII.Game;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    // ---------------------------------------------------------------- 公共小工具

    internal static class GameToolFormat
    {
        public static int Offset(JsonValue args) => Math.Max(0, args["offset"].AsInt(0));

        public static int Limit(JsonValue args, int fallback, int max = 100)
        {
            var value = args["limit"].AsInt(fallback);
            if (value <= 0) value = fallback;
            return Math.Min(value, max);
        }

        public static string ItemName(int id)
        {
            try
            {
                var proto = LDB.items.Select(id);
                if (proto != null && !string.IsNullOrEmpty(proto.name)) return proto.name;
            }
            catch { /* LDB 尚未加载 */ }
            return "#" + id.ToString(CultureInfo.InvariantCulture);
        }

        public static string TechName(int id)
        {
            try
            {
                var proto = LDB.techs.Select(id);
                if (proto != null && !string.IsNullOrEmpty(proto.name)) return proto.name;
            }
            catch { /* ignore */ }
            return "#" + id.ToString(CultureInfo.InvariantCulture);
        }

        public static string VeinName(EVeinType type)
        {
            try
            {
                var proto = LDB.veins.Select((int)type);
                if (proto != null && !string.IsNullOrEmpty(proto.name)) return proto.name;
            }
            catch { /* ignore */ }
            return type.ToString();
        }

        private static UnityEngine.UI.Youthcat.TextIconMapping _iconMapSource;
        private static HashSet<string> _renderableIconTags;

        /// <summary>
        /// tag 是否真的在游戏全局图标表里。只有能渲染的短码才交给模型；
        /// 否则模型会写出 `\tag;`，Text 查不到就把 `tag;` 原样画出来。
        /// </summary>
        public static bool IsRenderableIconTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            try
            {
                var map = Configs.builtin != null ? Configs.builtin.generalIconMapping : null;
                if (map == null || map.iconMappings == null || map.iconMappings.Length == 0)
                    return false;
                if (!ReferenceEquals(map, _iconMapSource) || _renderableIconTags == null)
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in map.iconMappings)
                        if (entry != null && !string.IsNullOrEmpty(entry.tag)) set.Add(entry.tag);
                    _iconMapSource = map;
                    _renderableIconTags = set;
                }
                return _renderableIconTags.Contains(tag);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>物品/建筑图标短码（不含反斜杠与分号），供模型写成 \tag; 。</summary>
        public static string ItemIconTag(int id)
        {
            try
            {
                var proto = LDB.items.Select(id);
                if (proto != null && IsRenderableIconTag(proto.IconTag)) return proto.IconTag;
            }
            catch { /* ignore */ }
            return string.Empty;
        }

        public static string TechIconTag(int id)
        {
            try
            {
                var proto = LDB.techs.Select(id);
                if (proto != null && IsRenderableIconTag(proto.IconTag)) return proto.IconTag;
            }
            catch { /* ignore */ }
            return string.Empty;
        }

        public static string VeinIconTag(EVeinType type)
        {
            try
            {
                var proto = LDB.veins.Select((int)type);
                if (proto != null && IsRenderableIconTag(proto.IconTag)) return proto.IconTag;
            }
            catch { /* ignore */ }
            return string.Empty;
        }

        public static double Kw(long perTick) => perTick * 60.0 / 1000.0;

        public static double Kwh(long joules) => joules / 3.6e6;

        public static double Round(double value, int digits = 1)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Round(value, digits, MidpointRounding.AwayFromZero);
        }

        public static string PowerDisplay(double kw)
        {
            if (Math.Abs(kw) >= 1000)
                return (kw / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " MW";
            return kw.ToString("0.#", CultureInfo.InvariantCulture) + " kW";
        }

        public static string TimeDisplay(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalHours >= 1)
                return ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h"
                    + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
            if (span.TotalMinutes >= 1)
                return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m"
                    + span.Seconds.ToString("00", CultureInfo.InvariantCulture) + "s";
            return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        }

        public static JsonValue Vec3(Vector3 value)
        {
            var obj = JsonValue.NewObject();
            obj.Set("x", Round(value.x, 1));
            obj.Set("y", Round(value.y, 1));
            obj.Set("z", Round(value.z, 1));
            return obj;
        }

        public static PlanetData ResolvePlanet(JsonValue args)
        {
            var id = args["planetId"].AsInt(0);
            try
            {
                if (id > 0 && GameMain.data != null && GameMain.data.galaxy != null)
                {
                    var planet = GameMain.data.galaxy.PlanetById(id);
                    if (planet != null) return planet;
                }
                return GameMain.data != null ? GameMain.data.localPlanet : null;
            }
            catch
            {
                return null;
            }
        }

        public static double PowerSatisfactionPct(PlanetFactory factory)
        {
            if (factory == null || factory.powerSystem == null) return 0;
            long demand = 0;
            long served = 0;
            var ps = factory.powerSystem;
            var pool = ps.netPool;
            if (pool == null) return 0;
            for (int i = 1; i < ps.netCursor && i < pool.Length; i++)
            {
                var net = pool[i];
                if (net == null) continue;
                demand += Math.Max(0, net.energyRequired);
                served += Math.Max(0, net.energyServed);
            }
            return demand <= 0 ? 100 : served * 100.0 / demand;
        }
    }

    internal sealed class OneShotJob : IGameToolJob
    {
        private readonly Func<GameToolJobResult> _build;
        private bool _done;

        public OneShotJob(Func<GameToolJobResult> build)
        {
            _build = build;
        }

        public bool Step(int budgetMs)
        {
            _done = true;
            return true;
        }

        public GameToolJobResult Finish() => _done ? _build() : null;
    }

    // ---------------------------------------------------------------- overview

    internal sealed class OverviewTool : IGameToolHandler
    {
        public string Name => "game_get_overview";
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(Build);
        }

        private static GameToolJobResult Build()
        {
            var data = JsonValue.NewObject();
            var gameData = GameMain.data;

            data.Set("saveName", gameData != null ? (gameData.gameName ?? string.Empty) : string.Empty);
            data.Set("saveKey", GameRefs.CurrentSaveKey() ?? string.Empty);
            data.Set("gameVersion", GameRefs.GameVersion);
            data.Set("gameTick", GameMain.gameTick);

            var seconds = GameMain.gameTime;
            data.Set("gameTimeSec", GameToolFormat.Round(seconds, 1));
            data.Set("gameTimeDisplay", GameToolFormat.TimeDisplay(seconds));
            data.Set("paused", GameMain.isPaused);

            var planet = gameData != null ? gameData.localPlanet : null;
            var planetObj = JsonValue.NewObject();
            if (planet != null)
            {
                planetObj.Set("id", planet.id);
                planetObj.Set("name", planet.displayName ?? planet.name ?? string.Empty);
                planetObj.Set("type", planet.type.ToString());
                planetObj.Set("factoryLoaded", planet.factory != null && planet.factoryLoaded);
            }
            data.Set("planet", planetObj);

            var star = gameData != null ? gameData.localStar : null;
            var starObj = JsonValue.NewObject();
            if (star != null)
            {
                starObj.Set("id", star.id);
                starObj.Set("name", star.name ?? string.Empty);
                starObj.Set("type", star.type.ToString());
            }
            data.Set("star", starObj);

            var player = gameData != null ? gameData.mainPlayer : null;
            var playerObj = JsonValue.NewObject();
            if (player != null) playerObj.Set("position", GameToolFormat.Vec3(player.position));
            data.Set("player", playerObj);

            var mode = JsonValue.NewObject();
            var desc = gameData != null ? gameData.gameDesc : null;
            if (desc != null)
            {
                mode.Set("peace", desc.isPeaceMode);
                mode.Set("sandbox", desc.isSandboxMode);
                mode.Set("combat", desc.isCombatMode);
                mode.Set("goalLevel", desc.goalLevel.ToString());
            }
            data.Set("mode", mode);

            var planetName = planet != null
                ? (planet.displayName ?? planet.name ?? "当前行星")
                : "未进入存档";
            data.Set("display", planetName + " · " + GameToolFormat.TimeDisplay(seconds));

            return new GameToolJobResult { Data = data };
        }
    }

    // ---------------------------------------------------------------- player

    internal sealed class PlayerTool : IGameToolHandler
    {
        public string Name => "game_get_player";
        public int TimeoutMs => 6000;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(() => Build(args));
        }

        private static GameToolJobResult Build(JsonValue args)
        {
            var data = JsonValue.NewObject();
            var player = GameMain.data != null ? GameMain.data.mainPlayer : null;
            if (player == null)
            {
                data.Set("display", "玩家对象尚未就绪");
                return new GameToolJobResult { Data = data };
            }

            var mecha = player.mecha;
            var mechaObj = JsonValue.NewObject();
            if (mecha != null)
            {
                var cap = mecha.coreEnergyCap;
                var energy = mecha.coreEnergy;
                mechaObj.Set("coreEnergy", GameToolFormat.Round(energy, 0));
                mechaObj.Set("coreEnergyCap", GameToolFormat.Round(cap, 0));
                mechaObj.Set("coreEnergyPct", cap > 0 ? GameToolFormat.Round(energy * 100.0 / cap, 1) : 0);
                mechaObj.Set("reactorEnergy", GameToolFormat.Round(mecha.reactorEnergy, 0));
                mechaObj.Set("shieldEnergy", mecha.energyShieldEnergy);
                mechaObj.Set("shieldCapacity", mecha.energyShieldCapacity);
                mechaObj.Set("shieldPct", mecha.energyShieldCapacity > 0
                    ? GameToolFormat.Round(mecha.energyShieldEnergy * 100.0 / mecha.energyShieldCapacity, 1)
                    : 0);
            }
            data.Set("mecha", mechaObj);
            data.Set("position", GameToolFormat.Vec3(player.position));

            var items = new List<JsonValue>();
            var package = player.package;
            if (package != null && package.grids != null)
            {
                var size = package.size > 0
                    ? Math.Min(package.size, package.grids.Length)
                    : package.grids.Length;
                for (int i = 0; i < size; i++)
                {
                    var grid = package.grids[i];
                    if (grid.itemId <= 0 || grid.count <= 0) continue;
                    var item = JsonValue.NewObject();
                    item.Set("id", grid.itemId);
                    item.Set("name", GameToolFormat.ItemName(grid.itemId));
                    item.Set("iconTag", GameToolFormat.ItemIconTag(grid.itemId));
                    item.Set("count", grid.count);
                    items.Add(item);
                }
            }

            var offset = GameToolFormat.Offset(args);
            var limit = GameToolFormat.Limit(args, 40, 100);
            var total = items.Count;
            var page = new JsonValue[0];
            var returned = 0;
            if (offset < total)
            {
                returned = Math.Min(limit, total - offset);
                page = new JsonValue[returned];
                for (int i = 0; i < returned; i++) page[i] = items[offset + i];
            }

            var inventory = JsonValue.NewObject();
            var arr = JsonValue.NewArray();
            foreach (var item in page) arr.Add(item);
            inventory.Set("items", arr);
            inventory.Set("total", total);
            inventory.Set("offset", offset);
            inventory.Set("limit", limit);
            inventory.Set("truncated", offset + returned < total);
            inventory.Set("nextOffset", offset + returned < total ? offset + returned : -1);
            data.Set("inventory", inventory);

            var energyPct = mecha != null && mecha.coreEnergyCap > 0
                ? mecha.coreEnergy * 100.0 / mecha.coreEnergyCap
                : 0;
            data.Set("display", "能量 " + GameToolFormat.Round(energyPct, 0).ToString("0", CultureInfo.InvariantCulture)
                + "% · 背包 " + total.ToString(CultureInfo.InvariantCulture) + " 种");

            return new GameToolJobResult
            {
                Data = data,
                Truncated = offset + returned < total,
                NextOffset = offset + returned < total ? offset + returned : -1,
            };
        }
    }

    // ---------------------------------------------------------------- planet

    internal sealed class PlanetTool : IGameToolHandler
    {
        public string Name => "game_get_planet";
        public int TimeoutMs => 12000;

        public IGameToolJob Start(JsonValue args)
        {
            return new PlanetJob(args);
        }

        private sealed class PlanetJob : IGameToolJob
        {
            private readonly JsonValue _args;
            private readonly PlanetData _planet;
            private readonly Dictionary<string, VeinAgg> _veins =
                new Dictionary<string, VeinAgg>(StringComparer.Ordinal);
            private readonly Dictionary<int, int> _buildings = new Dictionary<int, int>();

            private int _phase;
            private int _veinIndex = 1;
            private int _buildingIndex = 1;
            private int _veinCursor;
            private int _buildingCursor;
            private bool _done;

            public PlanetJob(JsonValue args)
            {
                _args = args;
                _planet = GameToolFormat.ResolvePlanet(args);
                if (_planet == null) throw new ArgumentException("找不到行星");
            }

            public bool Step(int budgetMs)
            {
                if (_done) return true;
                var watch = Stopwatch.StartNew();

                while (watch.ElapsedMilliseconds < budgetMs)
                {
                    if (_phase == 0)
                    {
                        if (!StepVeins()) _phase = 1;
                        continue;
                    }

                    // 远程行星没有工厂：矿脉仍可读，建筑列表为空，必须正常结束而不是等超时。
                    if (_planet.factory == null || _planet.factory.entityPool == null)
                    {
                        _done = true;
                        return true;
                    }

                    if (!StepBuildings())
                    {
                        _done = true;
                        return true;
                    }
                }

                return _done;
            }

            private bool StepVeins()
            {
                var pool = _planet.runtimeVeinPool;
                if (pool == null || pool.Length == 0) return false;

                if (_veinCursor == 0)
                {
                    _veinCursor = Math.Min(_planet.runtimeVeinCursor, pool.Length);
                }

                var end = Math.Min(_veinIndex + 200, _veinCursor);
                for (; _veinIndex < end; _veinIndex++)
                {
                    var vein = pool[_veinIndex];
                    if (vein.id == 0) continue;

                    var key = ((int)vein.type).ToString(CultureInfo.InvariantCulture)
                        + ":" + vein.productId.ToString(CultureInfo.InvariantCulture);
                    VeinAgg agg;
                    if (!_veins.TryGetValue(key, out agg))
                    {
                        agg = new VeinAgg
                        {
                            Type = vein.type,
                            ProductId = vein.productId,
                            Name = vein.productId > 0
                                ? GameToolFormat.ItemName(vein.productId)
                                : GameToolFormat.VeinName(vein.type),
                        };
                        _veins[key] = agg;
                    }
                    agg.Amount += Math.Max(0, vein.amount);
                    agg.Count++;
                }

                return _veinIndex < _veinCursor;
            }

            private bool StepBuildings()
            {
                var factory = _planet.factory;
                if (factory == null || factory.entityPool == null) return false;

                if (_buildingCursor == 0)
                {
                    _buildingCursor = Math.Min(factory.entityCursor, factory.entityPool.Length);
                }

                var end = Math.Min(_buildingIndex + 300, _buildingCursor);
                for (; _buildingIndex < end; _buildingIndex++)
                {
                    var entity = factory.entityPool[_buildingIndex];
                    if (entity.id == 0 || entity.protoId <= 0) continue;
                    int count;
                    _buildings.TryGetValue(entity.protoId, out count);
                    _buildings[entity.protoId] = count + 1;
                }

                return _buildingIndex < _buildingCursor;
            }

            public GameToolJobResult Finish()
            {
                var data = JsonValue.NewObject();
                data.Set("planetId", _planet.id);
                data.Set("name", _planet.displayName ?? _planet.name ?? string.Empty);
                data.Set("type", _planet.type.ToString());
                data.Set("radius", GameToolFormat.Round(_planet.radius, 0));
                data.Set("factoryLoaded", _planet.factory != null && _planet.factoryLoaded);

                var veinList = new List<VeinAgg>(_veins.Values);
                veinList.Sort((a, b) => b.Amount.CompareTo(a.Amount));
                var veinArr = JsonValue.NewArray();
                foreach (var vein in veinList)
                {
                    var obj = JsonValue.NewObject();
                    obj.Set("type", vein.Type.ToString());
                    obj.Set("productId", vein.ProductId);
                    obj.Set("name", vein.Name);
                    obj.Set("iconTag", vein.ProductId > 0
                        ? GameToolFormat.ItemIconTag(vein.ProductId)
                        : GameToolFormat.VeinIconTag(vein.Type));
                    obj.Set("amount", vein.Amount);
                    obj.Set("count", vein.Count);
                    veinArr.Add(obj);
                }
                data.Set("veins", veinArr);

                var buildingList = new List<KeyValuePair<int, int>>(_buildings);
                buildingList.Sort((a, b) =>
                {
                    var byCount = b.Value.CompareTo(a.Value);
                    return byCount != 0 ? byCount : a.Key.CompareTo(b.Key);
                });

                var offset = GameToolFormat.Offset(_args);
                var limit = GameToolFormat.Limit(_args, 30, 100);
                var total = buildingList.Count;
                var arr = JsonValue.NewArray();
                var returned = 0;
                for (int i = offset; i < total && returned < limit; i++, returned++)
                {
                    var pair = buildingList[i];
                    var obj = JsonValue.NewObject();
                    obj.Set("id", pair.Key);
                    obj.Set("name", GameToolFormat.ItemName(pair.Key));
                    obj.Set("iconTag", GameToolFormat.ItemIconTag(pair.Key));
                    obj.Set("count", pair.Value);
                    arr.Add(obj);
                }

                var buildings = JsonValue.NewObject();
                buildings.Set("items", arr);
                buildings.Set("total", total);
                buildings.Set("offset", offset);
                buildings.Set("limit", limit);
                buildings.Set("truncated", offset + returned < total);
                buildings.Set("nextOffset", offset + returned < total ? offset + returned : -1);
                data.Set("buildings", buildings);

                var display = (_planet.displayName ?? _planet.name ?? "行星")
                    + " · 矿 " + veinList.Count.ToString(CultureInfo.InvariantCulture) + " 类"
                    + " · 建筑 " + total.ToString(CultureInfo.InvariantCulture) + " 类";
                data.Set("display", display);

                return new GameToolJobResult
                {
                    Data = data,
                    Truncated = offset + returned < total,
                    NextOffset = offset + returned < total ? offset + returned : -1,
                };
            }

            private sealed class VeinAgg
            {
                public EVeinType Type;
                public int ProductId;
                public string Name;
                public long Amount;
                public int Count;
            }
        }
    }

    // ---------------------------------------------------------------- power

    internal sealed class PowerTool : IGameToolHandler
    {
        public string Name => "game_get_power";
        public int TimeoutMs => 6000;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(() => Build(args));
        }

        private static GameToolJobResult Build(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");

            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);
            data.Set("planetName", planet.displayName ?? planet.name ?? string.Empty);

            var factory = planet.factory;
            if (factory == null || factory.powerSystem == null)
            {
                data.Set("factoryLoaded", false);
                data.Set("demandKw", 0);
                data.Set("servedKw", 0);
                data.Set("capacityKw", 0);
                data.Set("storedKwh", 0);
                data.Set("accumulatorKw", 0);
                data.Set("satisfactionPct", 0);
                data.Set("nets", JsonValue.NewArray());
                data.Set("display", "工厂未加载（远程行星）");
                return new GameToolJobResult { Data = data };
            }

            var ps = factory.powerSystem;
            var pool = ps.netPool;
            long required = 0;
            long served = 0;
            long capacity = 0;
            long stored = 0;
            long accumulated = 0;
            var nets = JsonValue.NewArray();

            if (pool != null)
            {
                for (int i = 1; i < ps.netCursor && i < pool.Length; i++)
                {
                    var net = pool[i];
                    if (net == null) continue;
                    required += Math.Max(0, net.energyRequired);
                    served += Math.Max(0, net.energyServed);
                    capacity += Math.Max(0, net.energyCapacity);
                    stored += Math.Max(0, net.energyStored);
                    accumulated += net.energyAccumulated;

                    var netObj = JsonValue.NewObject();
                    netObj.Set("id", net.id);
                    netObj.Set("demandKw", GameToolFormat.Round(GameToolFormat.Kw(net.energyRequired), 2));
                    netObj.Set("servedKw", GameToolFormat.Round(GameToolFormat.Kw(net.energyServed), 2));
                    netObj.Set("capacityKw", GameToolFormat.Round(GameToolFormat.Kw(net.energyCapacity), 2));
                    netObj.Set("storedKwh", GameToolFormat.Round(GameToolFormat.Kwh(net.energyStored), 2));
                    netObj.Set("accumulatorKw", GameToolFormat.Round(GameToolFormat.Kw(net.energyAccumulated), 2));
                    netObj.Set("satisfactionPct", net.energyRequired > 0
                        ? GameToolFormat.Round(net.energyServed * 100.0 / net.energyRequired, 1)
                        : 100);
                    nets.Add(netObj);
                }
            }

            var demandKw = GameToolFormat.Kw(required);
            var servedKw = GameToolFormat.Kw(served);
            var capacityKw = GameToolFormat.Kw(capacity);
            var storedKwh = GameToolFormat.Kwh(stored);
            var satisfaction = required > 0 ? served * 100.0 / required : 100;

            data.Set("factoryLoaded", true);
            data.Set("demandKw", GameToolFormat.Round(demandKw, 2));
            data.Set("servedKw", GameToolFormat.Round(servedKw, 2));
            data.Set("capacityKw", GameToolFormat.Round(capacityKw, 2));
            data.Set("storedKwh", GameToolFormat.Round(storedKwh, 2));
            data.Set("accumulatorKw", GameToolFormat.Round(GameToolFormat.Kw(accumulated), 2));
            data.Set("satisfactionPct", GameToolFormat.Round(satisfaction, 1));
            data.Set("demandDisplay", GameToolFormat.PowerDisplay(demandKw));
            data.Set("servedDisplay", GameToolFormat.PowerDisplay(servedKw));
            data.Set("capacityDisplay", GameToolFormat.PowerDisplay(capacityKw));
            data.Set("nets", nets);
            data.Set("generatorCount", Math.Max(0, ps.genCursor - 1));
            data.Set("consumerCount", Math.Max(0, ps.consumerCursor - 1));
            data.Set("accumulatorCount", Math.Max(0, ps.accCursor - 1));

            data.Set("display", "需求 " + GameToolFormat.PowerDisplay(demandKw)
                + " · 满足 " + GameToolFormat.Round(satisfaction, 0).ToString("0", CultureInfo.InvariantCulture)
                + "% · 储能 " + GameToolFormat.Round(storedKwh, 0).ToString("0", CultureInfo.InvariantCulture) + " kWh");

            return new GameToolJobResult { Data = data };
        }
    }

    // ---------------------------------------------------------------- research

    internal sealed class ResearchTool : IGameToolHandler
    {
        public string Name => "game_get_research";
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(Build);
        }

        private static GameToolJobResult Build()
        {
            var data = JsonValue.NewObject();
            var history = GameMain.data != null ? GameMain.data.history : null;
            if (history == null)
            {
                data.Set("display", "研究数据尚未就绪");
                return new GameToolJobResult { Data = data };
            }

            var current = history.currentTech;
            data.Set("currentTechId", current);

            var currentObj = JsonValue.NewObject();
            if (current > 0)
            {
                TechState state;
                if (history.techStates == null ||
                    !history.techStates.TryGetValue(current, out state))
                {
                    state = history.TechState(current);
                }

                var needed = state.hashNeeded;
                var uploaded = state.hashUploaded;
                currentObj.Set("id", current);
                currentObj.Set("name", GameToolFormat.TechName(current));
                currentObj.Set("iconTag", GameToolFormat.TechIconTag(current));
                currentObj.Set("level", state.curLevel);
                currentObj.Set("hashUploaded", uploaded);
                currentObj.Set("hashNeeded", needed);
                currentObj.Set("progressPct", needed > 0
                    ? GameToolFormat.Round(uploaded * 100.0 / needed, 1)
                    : 0);
            }
            data.Set("current", currentObj);

            var queue = JsonValue.NewArray();
            if (history.techQueue != null)
            {
                for (int i = 0; i < history.techQueue.Length && i < 8; i++)
                {
                    var id = history.techQueue[i];
                    if (id <= 0) continue;
                    var q = JsonValue.NewObject();
                    q.Set("id", id);
                    q.Set("name", GameToolFormat.TechName(id));
                    q.Set("iconTag", GameToolFormat.TechIconTag(id));
                    queue.Add(q);
                }
            }
            data.Set("queue", queue);
            data.Set("techSpeed", history.techSpeed);

            int unlocked = 0;
            if (history.techStates != null)
            {
                foreach (var pair in history.techStates)
                    if (pair.Value.unlocked) unlocked++;
            }
            data.Set("unlockedCount", unlocked);

            int total = 0;
            try { total = LDB.techs != null && LDB.techs.dataArray != null ? LDB.techs.dataArray.Length : 0; }
            catch { /* ignore */ }
            data.Set("totalTechs", total);

            var progress = current > 0 ? currentObj["progressPct"].AsDouble(0) : 0;
            data.Set("display", current > 0
                ? "研究 " + GameToolFormat.TechName(current) + " "
                    + GameToolFormat.Round(progress, 0).ToString("0", CultureInfo.InvariantCulture) + "%"
                    + " · 已解锁 " + unlocked.ToString(CultureInfo.InvariantCulture)
                : "当前没有研究任务 · 已解锁 " + unlocked.ToString(CultureInfo.InvariantCulture));

            return new GameToolJobResult { Data = data };
        }
    }

    // ---------------------------------------------------------------- production

    internal sealed class ProductionTool : IGameToolHandler
    {
        public string Name => "game_get_production";
        public int TimeoutMs => 12000;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(() => Build(args));
        }

        private static GameToolJobResult Build(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");

            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);
            data.Set("planetName", planet.displayName ?? planet.name ?? string.Empty);

            var factory = planet.factory;
            var stats = GameMain.data != null ? GameMain.data.statistics : null;
            var production = stats != null ? stats.production : null;
            var factoryIndex = planet.factoryIndex;

            if (factory == null || production == null || factoryIndex < 0 ||
                production.factoryStatPool == null || factoryIndex >= production.factoryStatPool.Length)
            {
                data.Set("statsPending", true);
                data.Set("items", JsonValue.NewArray());
                data.Set("shortageCount", 0);
                data.Set("surplusCount", 0);
                data.Set("display", "工厂/产线统计未就绪（远程行星或统计尚未建立）");
                return new GameToolJobResult { Data = data };
            }

            var factoryStat = production.factoryStatPool[factoryIndex];
            if (factoryStat == null)
            {
                data.Set("statsPending", true);
                data.Set("items", JsonValue.NewArray());
                data.Set("shortageCount", 0);
                data.Set("surplusCount", 0);
                data.Set("display", "产线统计尚未建立");
                return new GameToolJobResult { Data = data };
            }

            var filter = args["itemFilter"].AsString(null);
            var filterLower = filter != null ? filter.Trim().ToLowerInvariant() : null;

            var entries = new List<ProdEntry>();
            int producedLevel = 1;
            int consumedLevel = ProductionStatistics.STAT_LEVEL_COUNT + producedLevel;

            var itemArray = LDB.items != null ? LDB.items.dataArray : null;
            if (itemArray != null && factoryStat.productIndices != null && factoryStat.productPool != null)
            {
                for (int i = 0; i < itemArray.Length; i++)
                {
                    var itemProto = itemArray[i];
                    if (itemProto == null) continue;
                    var itemId = itemProto.ID;
                    if (itemId <= 0 || itemId >= factoryStat.productIndices.Length) continue;

                    var productIndex = factoryStat.productIndices[itemId];
                    if (productIndex <= 0 || productIndex >= factoryStat.productPool.Length) continue;

                    var stat = factoryStat.productPool[productIndex];
                    if (stat == null || stat.total == null || stat.total.Length <= consumedLevel) continue;

                    var produced = stat.total[producedLevel];
                    var consumed = stat.total[consumedLevel];
                    if (produced == 0 && consumed == 0) continue;

                    var name = !string.IsNullOrEmpty(itemProto.name) ? itemProto.name : GameToolFormat.ItemName(itemId);
                    if (filterLower != null &&
                        !name.ToLowerInvariant().Contains(filterLower) &&
                        !itemId.ToString(CultureInfo.InvariantCulture).Contains(filterLower))
                    {
                        continue;
                    }

                    entries.Add(new ProdEntry
                    {
                        Id = itemId,
                        Name = name,
                        Produced = produced,
                        Consumed = consumed,
                        Stored = stat.storageCount,
                    });
                }
            }

            // 统计的 1 分钟桶为空时（刚读档/暂停），用装配机的装机产能兜底，
            // 至少让模型能回答“这条线理论上做多少”，而不是一句“暂无产线统计”。
            bool installedFallback = false;
            if (entries.Count == 0 && filterLower == null)
            {
                var installed = BuildInstalledEntries(factory);
                if (installed.Count > 0)
                {
                    entries.AddRange(installed);
                    installedFallback = true;
                }
            }

            entries.Sort((a, b) =>
            {
                var byNet = a.Net.CompareTo(b.Net);
                return byNet != 0 ? byNet : string.CompareOrdinal(a.Name, b.Name);
            });

            int shortageCount = 0;
            int surplusCount = 0;
            foreach (var entry in entries)
            {
                if (entry.Net < 0) shortageCount++;
                else if (entry.Net > 0) surplusCount++;
            }

            var offset = GameToolFormat.Offset(args);
            var limit = GameToolFormat.Limit(args, 25, 100);
            var total = entries.Count;
            var arr = JsonValue.NewArray();
            var returned = 0;
            for (int i = offset; i < total && returned < limit; i++, returned++)
            {
                var entry = entries[i];
                var obj = JsonValue.NewObject();
                obj.Set("id", entry.Id);
                obj.Set("name", entry.Name);
                obj.Set("iconTag", GameToolFormat.ItemIconTag(entry.Id));
                obj.Set("producedPerMin", entry.Produced);
                obj.Set("consumedPerMin", entry.Consumed);
                obj.Set("netPerMin", entry.Net);
                obj.Set("stored", entry.Stored);
                if (entry.Net < 0 && entry.Stored > 0)
                    obj.Set("minutesOfStock", GameToolFormat.Round(entry.Stored / (double)(-entry.Net), 1));
                else
                    obj.Set("minutesOfStock", -1);
                arr.Add(obj);
            }

            data.Set("statsPending", false);
            data.Set("items", arr);
            data.Set("total", total);
            data.Set("offset", offset);
            data.Set("limit", limit);
            data.Set("shortageCount", shortageCount);
            data.Set("surplusCount", surplusCount);
            data.Set("truncated", offset + returned < total);
            data.Set("nextOffset", offset + returned < total ? offset + returned : -1);
            data.Set("powerSatisfactionPct", GameToolFormat.Round(GameToolFormat.PowerSatisfactionPct(factory), 1));

            // 统计为空不等于没有产线：刚读档/暂停时累计值可能全是 0。
            // 给出建筑数并指引 player 去看装机产能，避免模型据此下"没有产线"的结论。
            int buildingCount = CountBuildings(factory);
            data.Set("buildingCount", buildingCount);
            data.Set("statsEmpty", installedFallback || entries.Count == 0);
            data.Set("basis", installedFallback ? "installed" : entries.Count > 0 ? "stats" : "none");

            string display;
            if (entries.Count > 0)
            {
                var first = entries[0];
                display = (installedFallback ? "装机估算" : "缺料 " + shortageCount.ToString(CultureInfo.InvariantCulture) + " 种")
                    + " · 首项 " + first.Name + " " + first.Net.ToString(CultureInfo.InvariantCulture) + "/min"
                    + " · 电力满足 " + GameToolFormat.Round(GameToolFormat.PowerSatisfactionPct(factory), 0)
                        .ToString("0", CultureInfo.InvariantCulture) + "%";
                if (installedFallback) data.Set("hint", "统计 1 分钟桶为空，以上是按装配机与配方算的装机产能，不是实际产出。");
            }
            else
            {
                data.Set("hint",
                    "产线统计暂无数据（刚读档或游戏暂停时常见）。这不等于没有产线；"
                    + "用 game_analyze_area 查看该区域的装机产能与配方。");
                display = buildingCount > 0
                    ? "统计暂无数据 · 建筑 " + buildingCount.ToString(CultureInfo.InvariantCulture)
                        + " 座 · 用 game_analyze_area 看装机产能"
                    : "统计暂无数据";
            }
            data.Set("display", display);

            return new GameToolJobResult
            {
                Data = data,
                Truncated = offset + returned < total,
                NextOffset = offset + returned < total ? offset + returned : -1,
            };
        }

        /// <summary>
        /// 统计为空时的兜底：按装配机 × 配方速度估算每分钟产出/消耗。
        /// 只统计 assemblerPool（装配机/熔炉/化工厂等都在这里），不含采矿与物流。
        /// </summary>
        private static List<ProdEntry> BuildInstalledEntries(PlanetFactory factory)
        {
            var entries = new List<ProdEntry>();
            var system = factory != null ? factory.factorySystem : null;
            if (system == null || system.assemblerPool == null || LDB.recipes == null) return entries;

            var produced = new Dictionary<int, double>();
            var consumed = new Dictionary<int, double>();
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.assemblerId <= 0) continue;
                if (entity.assemblerId >= system.assemblerPool.Length) continue;

                var assembler = system.assemblerPool[entity.assemblerId];
                if (assembler.recipeId <= 0) continue;
                var recipe = LDB.recipes.Select(assembler.recipeId);
                if (recipe == null || recipe.TimeSpend <= 0) continue;

                double craftsPerMin = 60.0 / (recipe.TimeSpend / 60.0) * (assembler.speed / 10000.0);
                AddAmounts(produced, recipe.Results, recipe.ResultCounts, craftsPerMin);
                AddAmounts(consumed, recipe.Items, recipe.ItemCounts, craftsPerMin);
            }

            var ids = new HashSet<int>();
            foreach (var key in produced.Keys) ids.Add(key);
            foreach (var key in consumed.Keys) ids.Add(key);

            foreach (var itemId in ids)
            {
                double p, c;
                produced.TryGetValue(itemId, out p);
                consumed.TryGetValue(itemId, out c);
                if (p <= 0 && c <= 0) continue;

                entries.Add(new ProdEntry
                {
                    Id = itemId,
                    Name = GameToolFormat.ItemName(itemId),
                    Produced = (long)Math.Round(p),
                    Consumed = (long)Math.Round(c),
                    Stored = 0,
                });
            }
            return entries;
        }

        private static void AddAmounts(Dictionary<int, double> target, int[] ids, int[] counts, double craftsPerMin)
        {
            if (ids == null || counts == null) return;
            var length = Math.Min(ids.Length, counts.Length);
            for (int i = 0; i < length; i++)
            {
                var itemId = ids[i];
                if (itemId <= 0 || counts[i] <= 0) continue;
                double current;
                target.TryGetValue(itemId, out current);
                target[itemId] = current + counts[i] * craftsPerMin;
            }
        }

        /// <summary>统计为空时的兜底：至少告诉模型这颗星球有多少建筑。</summary>
        private static int CountBuildings(PlanetFactory factory)
        {
            if (factory == null || factory.entityPool == null) return 0;
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            int count = 0;
            for (int id = 1; id < upper; id++)
            {
                if (factory.entityPool[id].id == id && factory.entityPool[id].protoId > 0) count++;
            }
            return count;
        }

        private sealed class ProdEntry
        {
            public int Id;
            public string Name;
            public long Produced;
            public long Consumed;
            public long Stored;

            public long Net => Produced - Consumed;
        }
    }
}
