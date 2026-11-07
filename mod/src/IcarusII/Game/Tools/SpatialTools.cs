using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using IcarusII.Game.Spatial;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 三个空间查询工具的公共骨架。
    ///
    /// 每个工具的 op 与 xlsx 的方法行一一对应；不支持 (方法, 类型) 组合时直接拒绝，
    /// 让模型看到"这个类型支持什么"，而不是拿到一个空结果去猜。
    /// </summary>
    internal abstract class SpatialToolBase : IGameToolHandler
    {
        public abstract string Name { get; }
        public virtual int TimeoutMs => 12000;

        public IGameToolJob Start(JsonValue args)
        {
            var methodName = args != null ? args["op"].AsString(null) : null;
            var method = SpatialTypes.MethodOf(methodName);
            if (!method.HasValue)
            {
                throw new SpatialException(
                    $"op 必须是 {string.Join(" / ", MethodNames())}，收到：{methodName ?? "(空)"}");
            }
            var request = SpatialRequest.Parse(args, method.Value);
            return new OneShotJob(() =>
            {
                try
                {
                    return Execute(request, args);
                }
                catch (SpatialException ex)
                {
                    // 空间查询的失败都是参数/对象状态问题，不该报成 internal。
                    throw new ArgumentErrorException(ex.Message);
                }
            });
        }

        protected abstract GameToolJobResult Execute(SpatialRequest request, JsonValue args);

        private static IEnumerable<string> MethodNames()
        {
            for (int i = 0; i <= (int)SpatialMethod.Note; i++)
                yield return SpatialTypes.NameOf((SpatialMethod)i);
        }

        // ------------------------------------------------------------ 结果组装

        protected static GameToolJobResult Result(JsonValue data, string display,
            bool partial = false, bool truncated = false, int nextOffset = -1)
        {
            data.Set("available", true);
            data.Set("partial", partial);
            data.Set("truncated", truncated);
            if (nextOffset >= 0) data.Set("nextOffset", nextOffset);
            data.Set("display", display ?? string.Empty);
            return new GameToolJobResult
            {
                Data = data,
                Truncated = truncated,
                NextOffset = nextOffset,
            };
        }

        protected static JsonValue ItemArray(List<ItemAmount> items, SpatialRequest request)
        {
            var array = JsonValue.NewArray();
            var total = items.Count;
            var returned = 0;
            for (int i = request.Offset; i < total && returned < request.Limit; i++, returned++)
            {
                var item = items[i];
                var obj = JsonValue.NewObject();
                obj.Set("id", item.ItemId);
                obj.Set("name", item.Name);
                obj.Set("iconTag", GameToolFormat.ItemIconTag(item.ItemId));
                obj.Set("amount", GameToolFormat.Round(item.Amount, 2));
                if (item.Stored > 0) obj.Set("stored", item.Stored);
                array.Add(obj);
            }
            return array;
        }

        protected static string RateUnitLabel(SpatialRequest request)
        {
            switch (request.Unit)
            {
                case StatUnit.PerHour: return "/h";
                case StatUnit.Total: return "（窗内总量）";
                default: return "/min";
            }
        }

        protected static string WindowLabel(SpatialRequest request)
        {
            switch (request.Window)
            {
                case StatWindow.Minute1: return "1 分钟";
                case StatWindow.Minute10: return "10 分钟";
                case StatWindow.Hour1: return "1 小时";
                case StatWindow.Hour10: return "10 小时";
                case StatWindow.Hour100: return "100 小时";
                default: return "全部";
            }
        }

        /// <summary>统计类结果的通用头：把 window/unit/basis 如实回显，避免模型误解数字。</summary>
        protected static void WriteStatMeta(JsonValue data, SpatialRequest request, bool windowNative)
        {
            var meta = JsonValue.NewObject();
            meta.Set("window", WindowLabel(request));
            meta.Set("unit", request.Unit == StatUnit.PerHour ? "perHour"
                : request.Unit == StatUnit.Total ? "total" : "perMin");
            meta.Set("basis", request.Basis == StatBasis.Installed ? "installed" : "actual");
            meta.Set("windowNative", windowNative);
            if (!windowNative)
                meta.Set("hint", "该对象没有分档统计，window 不生效：返回值是该对象自维护的速率或即时值。");
            data.Set("stat", meta);
        }
    }

    // ---------------------------------------------------------------- 区域

    internal sealed class SpaceRegionTool : SpatialToolBase
    {
        public override string Name => IcarusII.Common.Protocol.Protocol.ToolSpaceRegion;

        protected override GameToolJobResult Execute(SpatialRequest request, JsonValue args)
        {
            if (request.Type is null) request.Type = RegionResolver.InferType();
            request.RequireSupported();
            var target = RegionResolver.Resolve(request);

            var data = JsonValue.NewObject();
            data.Set("type", target.Type);
            data.Set("target", target.Describe());
            if (target.PlanetId > 0) data.Set("planetId", target.PlanetId);
            if (target.StarId > 0) data.Set("starId", target.StarId);
            if (target.NetworkId > 0) data.Set("networkId", target.NetworkId);
            if (target.Shape != null && !string.IsNullOrEmpty(target.Type)
                && (target.Type == SpatialTypes.WorldBox || target.Type == SpatialTypes.PlanetPatch))
                data.Set("region", target.Shape.ToJson());

            switch (request.Method)
            {
                case SpatialMethod.List:
                    return ListMembers(data, request, target);
                case SpatialMethod.Storage:
                    return Storage(data, request, target);
                case SpatialMethod.Input:
                case SpatialMethod.Output:
                case SpatialMethod.Production:
                case SpatialMethod.Consumption:
                    return Flow(data, request, target);
                case SpatialMethod.Power:
                    return Power(data, request, target);
                case SpatialMethod.Parent:
                    return Parent(data, request, target);
                case SpatialMethod.Location:
                    return Location(data, request, target);
                case SpatialMethod.Information:
                    return Information(data, request, target);
                case SpatialMethod.Note:
                    return Note(data, request, target);
                case SpatialMethod.Navigation:
                    return NavigationUnsupported(data, request, target);
                default:
                    throw new SpatialException($"{SpatialTypes.NameOf(request.Method)} 尚未实现");
            }
        }

        private static GameToolJobResult ListMembers(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            if (target.Type == SpatialTypes.Blueprint)
                return BlueprintBuildings(data, request, target);

            var members = RegionCatalog.Members(target, args_kind(request), out var partial, out var note);
            var array = JsonValue.NewArray();
            var returned = 0;
            for (int i = request.Offset; i < members.Count && returned < request.Limit; i++, returned++)
            {
                var member = members[i];
                var obj = JsonValue.NewObject();
                obj.Set("kind", member.Kind);
                obj.Set("id", member.Id);
                obj.Set("name", member.Name);
                if (member.PlanetId > 0) obj.Set("planetId", member.PlanetId);
                if (member.WorldFrame)
                {
                    var position = JsonValue.NewObject();
                    position.Set("x", GameToolFormat.Round(member.World.x, 1));
                    position.Set("y", GameToolFormat.Round(member.World.y, 1));
                    position.Set("z", GameToolFormat.Round(member.World.z, 1));
                    obj.Set("world", position);
                }
                array.Add(obj);
            }

            data.Set("members", array);
            data.Set("total", members.Count);
            data.Set("offset", request.Offset);
            data.Set("limit", request.Limit);
            if (note != null) data.Set("note", note);

            var truncated = request.Offset + returned < members.Count;
            var display = $"{target.Describe()}：{members.Count} 个成员"
                + (truncated ? string.Format(CultureInfo.InvariantCulture, "（返回 {0} 个）", returned) : string.Empty);
            var nextOffset = truncated ? request.Offset + returned : -1;
            return Result(data, display, partial, truncated, nextOffset);
        }

        private static string args_kind(SpatialRequest request) => request.ItemFilter;

        private static GameToolJobResult Storage(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            var items = new List<ItemAmount>();
            switch (target.Type)
            {
                case SpatialTypes.StarSystem:
                    AddStorageOfStar(target.Star, items, includePlanets: true);
                    break;
                case SpatialTypes.Planet:
                case SpatialTypes.PowerGrid:
                case SpatialTypes.Factory:
                case SpatialTypes.WorldBox:
                case SpatialTypes.PlanetPatch:
                {
                    var members = RegionCatalog.Members(target, "building", out _, out _);
                    var ids = new List<int>(members.Count);
                    foreach (var member in members) ids.Add(member.Id);
                    items = StatAggregator.Storage(target.Planet.factory, ids);
                    break;
                }
                case SpatialTypes.Star:
                case SpatialTypes.DysonSphere:
                    return DysonStorage(data, request, target);
                case SpatialTypes.DfHive:
                case SpatialTypes.DfBase:
                    return DfStorage(data, request, target);
                default:
                    throw new SpatialException($"类型 {target.Type} 没有存量");
            }

            data.Set("items", ItemArray(items, request));
            data.Set("total", items.Count);
            WriteStatMeta(data, request, windowNative: false);
            var display = $"{target.Describe()}：{items.Count} 种物品";
            var truncated = request.Offset + Math.Min(request.Limit, items.Count) < items.Count;
            return Result(data, display, false, truncated,
                truncated ? request.Offset + request.Limit : -1);
        }

        private static void AddStorageOfStar(StarData star, List<ItemAmount> items, bool includePlanets)
        {
            var totals = new Dictionary<int, ItemAmount>();
            if (!includePlanets || star?.planets == null) return;
            foreach (var planet in star.planets)
            {
                if (planet?.factory == null) continue;
                var members = RegionCatalog.Members(
                    new RegionTarget { Type = SpatialTypes.Planet, Planet = planet, PlanetId = planet.id },
                    "building", out _, out _);
                var ids = new List<int>(members.Count);
                foreach (var member in members) ids.Add(member.Id);
                foreach (var entry in StatAggregator.Storage(planet.factory, ids))
                {
                    if (!totals.TryGetValue(entry.ItemId, out var agg))
                    {
                        agg = new ItemAmount
                        {
                            ItemId = entry.ItemId,
                            Name = entry.Name,
                        };
                        totals[entry.ItemId] = agg;
                    }
                    agg.Amount += entry.Amount;
                    agg.Stored += entry.Stored;
                }
            }
            items.AddRange(totals.Values);
        }

        private static GameToolJobResult DysonStorage(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            var sphere = DysonSpheres.Get(target.StarId);
            if (sphere == null) throw new SpatialException("该恒星系还没有戴森球");
            var swarm = DysonSpheres.SailCount(sphere);
            var (nodes, frames, shells, structurePoints, cellPoints) = DysonSpheres.StructureCounts(sphere);

            var obj = JsonValue.NewObject();
            obj.Set("sailCount", swarm);
            obj.Set("nodeCount", nodes);
            obj.Set("frameCount", frames);
            obj.Set("shellCount", shells);
            obj.Set("structurePoints", structurePoints);
            obj.Set("cellPoints", cellPoints);
            data.Set("dyson", obj);
            WriteStatMeta(data, request, windowNative: false);
            var display = $"戴森球：太阳帆 {swarm} · 节点 {nodes} · 框架 {frames} · 壳面 {shells}";
            return Result(data, display);
        }

        private static GameToolJobResult DfStorage(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            if (target.Type == SpatialTypes.DfHive)
            {
                var hive = FindHive(target.Star);
                data.Set("matter", DfStats.HiveMatter(hive));
                data.Set("structure", DfStats.HiveStructure(hive));
                WriteStatMeta(data, request, windowNative: false);
                return Result(data, "巢穴：物质与能量总量");
            }

            var basis = FindBase(target.Planet, target.SeedEntityId);
            data.Set("matter", DfStats.BaseMatter(basis));
            WriteStatMeta(data, request, windowNative: false);
            return Result(data, "黑雾基地：物质与能量总量");
        }

        private static GameToolJobResult Flow(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            // 恒星/戴森球的流量来自太阳帆与火箭，不是物流统计
            if (target.Type == SpatialTypes.Star || target.Type == SpatialTypes.DysonSphere)
                return DysonFlow(data, request, target);

            if (target.Type == SpatialTypes.DfHive || target.Type == SpatialTypes.DfBase)
                return DfFlow(data, request, target);

            if (target.Type == SpatialTypes.Blueprint)
                return BlueprintFlow(data, request, target);

            var planet = target.Planet;
            if (planet?.factory == null) throw new SpatialException("该区域的行星工厂尚未加载");
            var members = RegionCatalog.Members(target, "building", out var partial, out var note);
            var ids = new List<int>(members.Count);
            foreach (var member in members) ids.Add(member.Id);

            var isProduction = request.Method == SpatialMethod.Production;
            var isConsumption = request.Method == SpatialMethod.Consumption;
            var basis = request.Basis;

            List<ItemAmount> items;
            if (basis == StatBasis.Installed)
            {
                // input/output 没有"理论物流"，只有生产/消耗有装机口径
                if (!isProduction && !isConsumption)
                    throw new SpatialException(
                        $"{SpatialTypes.NameOf(request.Method)} 只支持 basis=actual；理论值只对 production/consumption 有意义");
                items = StatAggregator.InstalledDelta(planet.factory, ids, request.Window, request.Unit,
                    produce: isProduction);
            }
            else
            {
                // input/output 与 production/consumption 都取工厂的生产桶：
                // 游戏的物流统计是行星级、且不区分方向，无法按建筑子集切分。
                items = StatAggregator.ProductionDelta(planet, ids, request.Window, request.Unit);
            }

            data.Set("items", ItemArray(items, request));
            data.Set("total", items.Count);
            WriteStatMeta(data, request, windowNative: true);
            if (note != null) data.Set("note", note);

            var display = $"{target.Describe()}：{items.Count} 种物品"
                + $"（{WindowLabel(request)}{RateUnitLabel(request)}）";
            var truncated = request.Offset + Math.Min(request.Limit, items.Count) < items.Count;
            return Result(data, display, partial, truncated,
                truncated ? request.Offset + request.Limit : -1);
        }

        private static GameToolJobResult DysonFlow(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            var sphere = DysonSpheres.Get(target.StarId);
            if (sphere == null) throw new SpatialException("该恒星系还没有戴森球");

            var obj = JsonValue.NewObject();
            var (swarmKw, layerKw) = DysonSpheres.GenKw(sphere);
            obj.Set("swarmGenKw", GameToolFormat.Round(swarmKw, 2));
            obj.Set("layerGenKw", GameToolFormat.Round(layerKw, 2));
            obj.Set("sailCount", DysonSpheres.SailCount(sphere));
            var (nodes, frames, shells, structurePoints, cellPoints) = DysonSpheres.StructureCounts(sphere);
            obj.Set("nodes", nodes);
            obj.Set("frames", frames);
            obj.Set("shells", shells);
            obj.Set("structurePoints", structurePoints);
            obj.Set("cellPoints", cellPoints);
            data.Set("dyson", obj);
            WriteStatMeta(data, request, windowNative: false);
            return Result(data, $"戴森球：发电 {GameToolFormat.Round(swarmKw + layerKw, 1)} kW");
        }

        private static GameToolJobResult DfFlow(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            if (target.Type == SpatialTypes.DfHive)
            {
                var hive = FindHive(target.Star);
                data.Set("matter", DfStats.HiveMatter(hive));
            }
            else
            {
                var basis = FindBase(target.Planet, target.SeedEntityId);
                data.Set("matter", DfStats.BaseMatter(basis));
            }
            WriteStatMeta(data, request, windowNative: false);
            return Result(data, "黑雾物质流量");
        }

        private static GameToolJobResult Power(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            switch (target.Type)
            {
                case SpatialTypes.Star:
                case SpatialTypes.DysonSphere:
                {
                    var sphere = DysonSpheres.Get(target.StarId);
                    if (sphere == null) throw new SpatialException("该恒星系还没有戴森球");
                    var (swarmKw, layerKw) = DysonSpheres.GenKw(sphere);
                    var obj = JsonValue.NewObject();
                    obj.Set("maxGenKw", GameToolFormat.Round(swarmKw + layerKw, 2));
                    data.Set("power", obj);
                    WriteStatMeta(data, request, windowNative: false);
                    return Result(data, $"戴森球发电 {GameToolFormat.Round(swarmKw + layerKw, 1)} kW");
                }
                case SpatialTypes.DfHive:
                case SpatialTypes.DfBase:
                {
                    var matter = target.Type == SpatialTypes.DfHive
                        ? DfStats.HiveMatter(FindHive(target.Star))
                        : DfStats.BaseMatter(FindBase(target.Planet, target.SeedEntityId));
                    data.Set("matter", matter);
                    WriteStatMeta(data, request, windowNative: false);
                    return Result(data, "黑雾能量");
                }
                case SpatialTypes.Blueprint:
                    return BlueprintPower(data, request, target);
                default:
                {
                    if (target.Type == SpatialTypes.StarSystem)
                        return AggregateStarPower(data, request, target);

                    var planet = target.Planet;
                    if (planet?.factory == null) throw new SpatialException("该区域的行星工厂尚未加载");
                    var members = RegionCatalog.Members(target, "building", out var partial, out var note);
                    var ids = new List<int>(members.Count);
                    foreach (var member in members) ids.Add(member.Id);
                    data.Set("power", StatAggregator.Power(planet.factory, ids));
                    WriteStatMeta(data, request, windowNative: false);
                    if (note != null) data.Set("note", note);
                    return Result(data, $"{target.Describe()}：电力", partial);
                }
            }
        }

        private static GameToolJobResult AggregateStarPower(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            long required = 0;
            long served = 0;
            var planets = 0;
            if (target.Star?.planets != null)
            {
                foreach (var planet in target.Star.planets)
                {
                    var system = planet?.factory != null ? planet.factory.powerSystem : null;
                    if (system?.netPool == null) continue;
                    planets++;
                    for (int i = 1; i < system.netCursor && i < system.netPool.Length; i++)
                    {
                        required += Math.Max(0, system.netPool[i].energyRequired);
                        served += Math.Max(0, system.netPool[i].energyServed);
                    }
                }
            }
            var obj = JsonValue.NewObject();
            obj.Set("demandKw", GameToolFormat.Round(GameToolFormat.Kw(required), 2));
            obj.Set("servedKw", GameToolFormat.Round(GameToolFormat.Kw(served), 2));
            obj.Set("satisfactionPct", required > 0
                ? GameToolFormat.Round(served * 100.0 / required, 1) : 100);
            obj.Set("planets", planets);
            data.Set("power", obj);
            WriteStatMeta(data, request, windowNative: false);
            return Result(data, $"恒星系：{planets} 颗行星的电力合计");
        }

        private static GameToolJobResult Parent(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            if (target.Type == SpatialTypes.Blueprint) return BlueprintParent(data, target);

            var chain = JsonValue.NewArray();
            var current = target;
            for (int depth = 0; depth < request.Depth; depth++)
            {
                var parent = ParentOf(current);
                if (parent is null) break;
                var obj = JsonValue.NewObject();
                obj.Set("type", parent.Type);
                obj.Set("target", parent.Describe());
                if (parent.StarId > 0) obj.Set("starId", parent.StarId);
                if (parent.PlanetId > 0) obj.Set("planetId", parent.PlanetId);
                chain.Add(obj);
                current = parent;
            }
            if (chain.Count == 0)
            {
                var obj = JsonValue.NewObject();
                obj.Set("type", "root");
                obj.Set("target", "星区");
                chain.Add(obj);
            }
            data.Set("parents", chain);
            return Result(data, $"{target.Describe()} 的上级：{chain.Count} 层");
        }

        private static GameToolJobResult BlueprintParent(JsonValue data, RegionTarget target)
        {
            var full = BlueprintCatalog.ResolvePath(target.BlueprintPath);
            var root = GameConfig.blueprintFolder ?? string.Empty;
            var dir = Path.GetDirectoryName(full) ?? string.Empty;
            var relative = dir;
            if (!string.IsNullOrEmpty(root) && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                relative = dir.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

            var chain = JsonValue.NewArray();
            var obj = JsonValue.NewObject();
            obj.Set("type", "directory");
            obj.Set("path", relative.Length == 0 ? "." : relative);
            obj.Set("absolute", dir);
            chain.Add(obj);
            data.Set("parents", chain);
            return Result(data, $"蓝图父目录：{(relative.Length == 0 ? "蓝图根" : relative)}");
        }

        private static GameToolJobResult BlueprintPower(JsonValue data, SpatialRequest request,
            RegionTarget target)
        {
            var blueprint = BlueprintCatalog.Load(target.BlueprintPath);
            var power = BlueprintCatalog.PowerEstimate(blueprint);
            data.Set("power", power);
            WriteStatMeta(data, request, windowNative: false);
            data.Set("hint", "蓝图是静态文件，只给理论值；不考虑供电满足度与电网拓扑。");
            return Result(data, "蓝图理论电力");
        }

        private static GameToolJobResult BlueprintNote(JsonValue data, RegionTarget target)
        {
            var blueprint = BlueprintCatalog.Load(target.BlueprintPath);
            data.Set("note", blueprint.desc ?? string.Empty);
            data.Set("shortDesc", blueprint.shortDesc ?? string.Empty);
            return Result(data, string.IsNullOrEmpty(blueprint.desc) ? "蓝图没有介绍" : "蓝图介绍 1 条");
        }

        private static RegionTarget ParentOf(RegionTarget target)
        {
            switch (target.Type)
            {
                case SpatialTypes.StarSystem:
                    return null;
                case SpatialTypes.Star:
                case SpatialTypes.DfHive:
                    return new RegionTarget
                    {
                        Type = SpatialTypes.StarSystem,
                        Star = target.Star,
                        StarId = target.StarId,
                    };
                case SpatialTypes.Planet:
                case SpatialTypes.DfBase:
                    return target.Star is null ? null : new RegionTarget
                    {
                        Type = SpatialTypes.StarSystem,
                        Star = target.Star,
                        StarId = target.StarId,
                    };
                case SpatialTypes.PowerGrid:
                    return target.Planet is null ? null : new RegionTarget
                    {
                        Type = SpatialTypes.Planet,
                        Planet = target.Planet,
                        PlanetId = target.PlanetId,
                        Star = target.Star,
                        StarId = target.StarId,
                    };
                case SpatialTypes.Factory:
                    return target.Planet is null ? null : new RegionTarget
                    {
                        Type = SpatialTypes.PowerGrid,
                        NetworkId = target.NetworkId,
                        SeedEntityId = target.SeedEntityId,
                        Planet = target.Planet,
                        PlanetId = target.PlanetId,
                        Star = target.Star,
                        StarId = target.StarId,
                    };
                case SpatialTypes.DysonSphere:
                    return target.Star is null ? null : new RegionTarget
                    {
                        Type = SpatialTypes.Star,
                        Star = target.Star,
                        StarId = target.StarId,
                    };
                case SpatialTypes.PlanetPatch:
                    return target.Planet is null ? null : new RegionTarget
                    {
                        Type = SpatialTypes.Planet,
                        Planet = target.Planet,
                        PlanetId = target.PlanetId,
                        Star = target.Star,
                        StarId = target.StarId,
                    };
                case SpatialTypes.WorldBox:
                    return new RegionTarget { Type = SpatialTypes.StarSystem };
                default:
                    return null;
            }
        }

        private static GameToolJobResult Location(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            switch (target.Type)
            {
                case SpatialTypes.Star:
                case SpatialTypes.StarSystem:
                case SpatialTypes.DysonSphere:
                {
                    var star = target.Star;
                    if (star == null) throw new SpatialException("找不到恒星");
                    var obj = JsonValue.NewObject();
                    obj.Set("starId", star.id);
                    obj.Set("starName", PlanetFrame.StarName(star));
                    var position = JsonValue.NewObject();
                    position.Set("x", GameToolFormat.Round(star.uPosition.x, 1));
                    position.Set("y", GameToolFormat.Round(star.uPosition.y, 1));
                    position.Set("z", GameToolFormat.Round(star.uPosition.z, 1));
                    obj.Set("world", position);
                    data.Set("location", obj);
                    return Result(data, $"恒星中心 {PlanetFrame.StarName(star)}");
                }
                case SpatialTypes.Planet:
                case SpatialTypes.DfBase:
                case SpatialTypes.PlanetPatch:
                {
                    var planet = target.Planet;
                    if (planet == null) throw new SpatialException("找不到行星");
                    var obj = JsonValue.NewObject();
                    obj.Set("planetId", planet.id);
                    obj.Set("planetName", PlanetFrame.PlanetName(planet));
                    var local = PlanetFrame.WorldToLocal(planet, planet.uPosition);
                    obj.Set("local", Vector3Json(local));
                    data.Set("location", obj);
                    return Result(data, $"行星中心 {PlanetFrame.PlanetName(planet)}");
                }
                case SpatialTypes.DfHive:
                {
                    var hive = FindHive(target.Star);
                    var obj = JsonValue.NewObject();
                    obj.Set("hiveAstroId", target.StarId);
                    obj.Set("world", Vector3Json(PlanetFrame.ToVector3(PlanetFrame.ToVec3d(
                        hive != null ? HIVE_POS(hive) : VectorLF3.zero))));
                    data.Set("location", obj);
                    return Result(data, "巢穴中心");
                }
                default:
                {
                    if (target.Type == SpatialTypes.PowerGrid || target.Type == SpatialTypes.Factory)
                        return ClusterLocation(data, request, target);
                    if (target.Type == SpatialTypes.WorldBox)
                    {
                        var obj = JsonValue.NewObject();
                        obj.Set("center", Vector3Json(new Vector3(
                            (float)((target.Shape.BoxMin.X + target.Shape.BoxMax.X) * 0.5),
                            (float)((target.Shape.BoxMin.Y + target.Shape.BoxMax.Y) * 0.5),
                            (float)((target.Shape.BoxMin.Z + target.Shape.BoxMax.Z) * 0.5))));
                        data.Set("location", obj);
                        return Result(data, "区域中心");
                    }
                    throw new SpatialException($"类型 {target.Type} 没有位置");
                }
            }
        }

        private static VectorLF3 HIVE_POS(EnemyDFHiveSystem hive) => hive.player_upos;

        private static GameToolJobResult ClusterLocation(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            var members = RegionCatalog.Members(target, "building", out _, out _);
            if (members.Count == 0) throw new SpatialException("该区域没有建筑，无法取中心");
            var sum = Vector3.zero;
            foreach (var member in members) sum += member.Local;
            var center = sum / members.Count;
            var obj = JsonValue.NewObject();
            obj.Set("planetId", target.PlanetId);
            obj.Set("local", Vector3Json(center));
            var (lat, lon) = GeoMath.ToLatLon(new Vec3d(center.x, center.y, center.z));
            obj.Set("latitude", GameToolFormat.Round(lat, request.Precision));
            obj.Set("longitude", GameToolFormat.Round(lon, request.Precision));
            data.Set("location", obj);
            return Result(data, $"{target.Describe()} 中心（{members.Count} 座建筑的形心）");
        }

        private static GameToolJobResult Information(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            var info = JsonValue.NewObject();
            switch (target.Type)
            {
                case SpatialTypes.Star:
                {
                    var star = target.Star;
                    if (star == null) throw new SpatialException("找不到恒星");
                    info.Set("mass", GameToolFormat.Round(star.mass, 3));
                    info.Set("spectr", star.spectr.ToString());
                    info.Set("type", star.type.ToString());
                    info.Set("radius", GameToolFormat.Round(star.radius, 1));
                    info.Set("luminosity", GameToolFormat.Round(star.luminosity, 3));
                    info.Set("temperature", GameToolFormat.Round(star.temperature, 0));
                    info.Set("age", GameToolFormat.Round(star.age, 3));
                    info.Set("planetCount", star.planetCount);
                    break;
                }
                case SpatialTypes.Planet:
                {
                    var planet = target.Planet;
                    if (planet == null) throw new SpatialException("找不到行星");
                    info.Set("orbitRadius", GameToolFormat.Round(planet.orbitRadius, 2));
                    info.Set("orbitalPeriod", GameToolFormat.Round(planet.orbitalPeriod, 2));
                    info.Set("orbitInclination", GameToolFormat.Round(planet.orbitInclination, 3));
                    info.Set("orbitLongitude", GameToolFormat.Round(planet.orbitLongitude, 3));
                    info.Set("obliquity", GameToolFormat.Round(planet.obliquity, 3));
                    info.Set("planetType", planet.type.ToString());
                    info.Set("radius", GameToolFormat.Round(planet.realRadius, 1));
                    break;
                }
                case SpatialTypes.DfHive:
                {
                    var hive = FindHive(target.Star);
                    if (hive == null) throw new SpatialException("该恒星系没有黑雾巢穴");
                    info.Set("orbitRadius", GameToolFormat.Round(hive.hiveAstroOrbit.orbitRadius, 2));
                    info.Set("orbitalPeriod", GameToolFormat.Round(hive.hiveAstroOrbit.orbitalPeriod, 2));
                    info.Set("orbitInclination", GameToolFormat.Round(hive.hiveAstroOrbit.orbitInclination, 3));
                    info.Set("orbitLongitude", GameToolFormat.Round(hive.hiveAstroOrbit.orbitLongitude, 3));
                    break;
                }
                case SpatialTypes.DysonSphere:
                {
                    var sphere = DysonSpheres.Get(target.StarId);
                    if (sphere == null) throw new SpatialException("该恒星系还没有戴森球");
                    info.Set("layerCount", sphere.layerCount);
                    info.Set("grossRadius", GameToolFormat.Round(sphere.grossRadius, 1));
                    info.Set("maxLayerCount", DysonSphere.kMaxLayerCount);
                    break;
                }
                case SpatialTypes.Blueprint:
                {
                    var blueprint = BlueprintCatalog.Load(target.BlueprintPath);
                    info = BlueprintCatalog.Header(blueprint);
                    break;
                }
                default:
                    throw new SpatialException($"类型 {target.Type} 没有 information");
            }
            data.Set("information", info);
            return Result(data, $"{target.Describe()} 的静态信息");
        }

        private static GameToolJobResult Note(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            if (target.Type == SpatialTypes.Blueprint) return BlueprintNote(data, target);

            var text = MemoText(target);
            data.Set("note", text ?? string.Empty);
            return Result(data, string.IsNullOrEmpty(text) ? "暂无备忘录" : "备忘录 1 条");
        }

        private static GameToolJobResult NavigationUnsupported(JsonValue data, SpatialRequest request, RegionTarget target)
        {
            throw new SpatialException(
                $"导航是写操作，请用 game_tether_add（kind=planet/building/vein）或 game_trace_line；"
                + $"本次 target 为 {target.Describe()}");
        }

        // ------------------------------------------------------------ 蓝图

        private static GameToolJobResult BlueprintBuildings(JsonValue data, SpatialRequest request,
            RegionTarget target)
        {
            var blueprint = BlueprintCatalog.Load(target.BlueprintPath);
            var buildings = BlueprintCatalog.Buildings(blueprint);

            var array = JsonValue.NewArray();
            var returned = 0;
            for (int i = request.Offset; i < buildings.Count && returned < request.Limit; i++, returned++)
            {
                var (itemId, name, count) = buildings[i];
                var obj = JsonValue.NewObject();
                obj.Set("id", itemId);
                obj.Set("name", name);
                obj.Set("iconTag", GameToolFormat.ItemIconTag(itemId));
                obj.Set("count", count);
                array.Add(obj);
            }
            data.Set("buildings", array);
            data.Set("total", buildings.Count);
            if (buildings.Count > 0) data.Set("blueprint", BlueprintCatalog.Header(blueprint));

            var truncated = request.Offset + returned < buildings.Count;
            return Result(data,
                $"蓝图：{blueprint.buildings?.Length ?? 0} 座建筑，{buildings.Count} 种",
                false, truncated, truncated ? request.Offset + returned : -1);
        }

        private static GameToolJobResult BlueprintFlow(JsonValue data, SpatialRequest request,
            RegionTarget target)
        {
            // 蓝图只有理论值；basis=actual 没有数据源，直接按 installed 算并如实标注。
            var blueprint = BlueprintCatalog.Load(target.BlueprintPath);
            var produce = request.Method == SpatialMethod.Output
                || request.Method == SpatialMethod.Production;
            BlueprintCatalog.TheoreticalFlow(blueprint, produce, out var amounts, out var recipeBuildings);

            var items = new List<ItemAmount>();
            foreach (var pair in amounts)
                items.Add(new ItemAmount
                {
                    ItemId = pair.Key,
                    Name = GameToolFormat.ItemName(pair.Key),
                    Amount = SpatialRequest.ConvertUnit(pair.Value, request.Window, request.Unit),
                });
            items.Sort((a, b) => b.Amount.CompareTo(a.Amount));

            data.Set("items", ItemArray(items, request));
            data.Set("total", items.Count);
            data.Set("basis", "installed");
            data.Set("recipeBuildings", recipeBuildings);
            WriteStatMeta(data, request, windowNative: false);
            data.Set("hint", "蓝图是静态文件，只能给理论值；不考虑供电、堵料与物流延迟。");
            var truncated = request.Offset + Math.Min(request.Limit, items.Count) < items.Count;
            return Result(data, $"蓝图理论流量：{items.Count} 种物品（{recipeBuildings} 台有配方）",
                false, truncated, truncated ? request.Offset + request.Limit : -1);
        }

        // ------------------------------------------------------------ 备忘录

        public static string MemoText(RegionTarget target)
        {
            switch (target.Type)
            {
                case SpatialTypes.Star:
                    return TryStarMemo(target.Star);
                case SpatialTypes.Planet:
                case SpatialTypes.PlanetPatch:
                    return TryPlanetMemo(target.Planet);
                case SpatialTypes.PowerGrid:
                case SpatialTypes.Factory:
                    return TryPlanetMemo(target.Planet);
                case SpatialTypes.StarSystem:
                    return TryStarMemo(target.Star);
                default:
                    return null;
            }
        }

        /// <summary>行星备忘录存在工厂的数字系统里，和 tether 卡片同源。</summary>
        private static string TryPlanetMemo(PlanetData planet)
        {
            try
            {
                var factory = planet?.factory;
                if (factory?.digitalSystem?.planetTodo != null)
                    return factory.digitalSystem.planetTodo.content;
            }
            catch { /* 未初始化 */ }
            return null;
        }

        /// <summary>恒星没有独立的备忘录字段；恒星系的备忘录由所属行星的备忘录承载。</summary>
        private static string TryStarMemo(StarData star)
        {
            if (star?.planets == null) return null;
            foreach (var planet in star.planets)
            {
                var memo = TryPlanetMemo(planet);
                if (!string.IsNullOrEmpty(memo)) return memo;
            }
            return null;
        }

        // ------------------------------------------------------------ 黑雾查找

        public static EnemyDFHiveSystem FindHive(StarData star)
        {
            var sector = GameMain.data != null ? GameMain.data.spaceSector : null;
            if (sector?.dfHivesByAstro == null || star == null) return null;
            return star.index < sector.dfHivesByAstro.Length ? sector.dfHivesByAstro[star.index] : null;
        }

        public static DFGBaseComponent FindBase(PlanetData planet, int baseId)
        {
            var system = planet?.factory != null ? planet.factory.enemySystem : null;
            if (system?.bases == null) return null;
            if (baseId <= 0) return null;
            var buffer = system.bases.buffer;
            if (buffer == null || baseId >= buffer.Length) return null;
            var basis = buffer[baseId];
            return basis.id == baseId ? basis : null;
        }

        private static JsonValue Vector3Json(Vector3 value)
        {
            var obj = JsonValue.NewObject();
            obj.Set("x", GameToolFormat.Round(value.x, 1));
            obj.Set("y", GameToolFormat.Round(value.y, 1));
            obj.Set("z", GameToolFormat.Round(value.z, 1));
            return obj;
        }
    }

    // ---------------------------------------------------------------- 定点

    internal sealed class SpacePointTool : SpatialToolBase
    {
        public override string Name => IcarusII.Common.Protocol.Protocol.ToolSpacePoint;

        protected override GameToolJobResult Execute(SpatialRequest request, JsonValue args)
        {
            if (request.Type is null) request.Type = PointCatalog.InferType();
            request.RequireSupported();
            var target = PointCatalog.Resolve(request);

            var data = JsonValue.NewObject();
            data.Set("type", target.Type);
            data.Set("target", target.Describe());
            data.Set("name", target.Name ?? string.Empty);
            if (target.PlanetId > 0) data.Set("planetId", target.PlanetId);
            if (target.StarId > 0) data.Set("starId", target.StarId);
            if (target.EntityId > 0) data.Set("entityId", target.EntityId);
            if (target.VeinId > 0) data.Set("veinId", target.VeinId);
            if (target.VegeId > 0) data.Set("vegeId", target.VegeId);
            if (target.EnemyId > 0) data.Set("enemyId", target.EnemyId);

            switch (request.Method)
            {
                case SpatialMethod.List:
                    return ListOwned(data, request, target);
                case SpatialMethod.Storage:
                    return Storage(data, request, target);
                case SpatialMethod.Input:
                case SpatialMethod.Output:
                case SpatialMethod.Production:
                case SpatialMethod.Consumption:
                    return Flow(data, request, target);
                case SpatialMethod.Power:
                    return Power(data, request, target);
                case SpatialMethod.Parent:
                    return Parent(data, request, target);
                case SpatialMethod.Location:
                    return Location(data, request, target);
                case SpatialMethod.Note:
                    return Note(data, request, target);
                case SpatialMethod.Navigation:
                    return NavigationUnsupported(data, request, target);
                default:
                    throw new SpatialException($"{SpatialTypes.NameOf(request.Method)} 尚未实现");
            }
        }

        /// <summary>建筑名下的动点：站点/发射井/战斗基地才拥有单位，普通建筑返回空数组而不是报错。</summary>
        private static GameToolJobResult ListOwned(JsonValue data, SpatialRequest request, PointTarget target)
        {
            var array = JsonValue.NewArray();
            if (target.Type == SpatialTypes.Building && target.EntityId > 0)
            {
                var factory = target.Planet.factory;
                var entity = factory.entityPool[target.EntityId];
                var count = 0;
                if (entity.stationId > 0)
                    count += AppendStationUnits(factory, entity.stationId, array);
                if (entity.siloId > 0)
                    count += AppendSiloRockets(target.Star, entity.siloId, array);
                if (entity.battleBaseId > 0)
                    count += AppendBattleBaseUnits(factory, entity.battleBaseId, array);
                if (entity.constructionModuleId > 0)
                    count += AppendConstructionDrones(factory, target.EntityId, array);
                data.Set("owned", array);
                data.Set("total", count);
                return Result(data, $"{target.Describe()} 名下 {count} 个动点");
            }

            if (target.Type == SpatialTypes.DfSpaceBuilding || target.Type == SpatialTypes.DfGroundBuilding)
            {
                var count = AppendDfUnits(target, array);
                data.Set("owned", array);
                data.Set("total", count);
                return Result(data, $"{target.Describe()} 名下 {count} 个黑雾单位");
            }

            throw new SpatialException($"类型 {target.Type} 没有下辖动点");
        }

        private static int AppendStationUnits(PlanetFactory factory, int stationId, JsonValue array)
        {
            var transport = GameMain.data.galacticTransport;
            if (transport?.stationPool == null) return 0;
            var count = 0;
            for (int i = 1; i < transport.stationCursor && i < transport.stationPool.Length; i++)
            {
                var station = transport.stationPool[i];
                if (station == null || station.entityId != stationId - 1 + 1) continue;
                if (station.planetId != factory.planet.id) continue;
                if (station.entityId != StationEntityId(factory, stationId)) continue;

                for (int d = 0; d < station.workDroneCount && station.workDroneDatas != null; d++)
                {
                    if (d >= station.workDroneDatas.Length) break;
                    array.Add(OwnedMotion("station_drone", station.id, station.gid, "物流运输机"));
                    count++;
                }
                for (int sh = 0; sh < station.workShipCount && station.workShipDatas != null; sh++)
                {
                    if (sh >= station.workShipDatas.Length) break;
                    array.Add(OwnedMotion("station_ship", station.gid, sh, "星际物流运输船"));
                    count++;
                }
            }
            return count;
        }

        private static int StationEntityId(PlanetFactory factory, int stationId)
        {
            var pool = factory.entityPool;
            for (int id = 1; id < factory.entityCursor && id < pool.Length; id++)
                if (pool[id].stationId == stationId) return id;
            return 0;
        }

        private static int AppendSiloRockets(StarData star, int siloId, JsonValue array)
        {
            var sphere = GameMain.data.dysonSpheres != null && star != null
                ? DysonSpheres.Get(star) : null;
            if (sphere?.rocketPool == null) return 0;
            var count = 0;
            for (int id = 1; id < sphere.rocketCursor && id < sphere.rocketPool.Length; id++)
            {
                var rocket = sphere.rocketPool[id];
                if (rocket.id != id) continue;
                array.Add(OwnedMotion("dyson_rocket", id, 0, "小型运载火箭"));
                count++;
            }
            return count;
        }

        private static int AppendBattleBaseUnits(PlanetFactory factory, int battleBaseId, JsonValue array)
        {
            var system = factory.combatGroundSystem;
            if (system?.units?.buffer == null) return 0;
            var count = 0;
            for (int id = 1; id < system.units.cursor && id < system.units.buffer.Length; id++)
            {
                var unit = system.units.buffer[id];
                if (unit.id != id) continue;
                array.Add(OwnedMotion("combat_drone", id, 0, "战斗无人机"));
                count++;
            }
            return count;
        }

        private static int AppendConstructionDrones(PlanetFactory factory, int entityId, JsonValue array)
        {
            if (factory.craftPool == null) return 0;
            var count = 0;
            for (int id = 1; id < factory.craftCursor && id < factory.craftPool.Length; id++)
            {
                var craft = factory.craftPool[id];
                if (craft.id != id || craft.protoId <= 0) continue;
                if (craft.owner != entityId) continue;
                array.Add(OwnedMotion("construction_drone", id, 0, "建设无人机"));
                count++;
            }
            return count;
        }

        private static int AppendDfUnits(PointTarget target, JsonValue array)
        {
            var count = 0;
            if (target.Type == SpatialTypes.DfSpaceBuilding)
            {
                var sector = GameMain.data.spaceSector;
                if (sector?.enemyPool == null) return 0;
                for (int id = 1; id < sector.enemyCursor && id < sector.enemyPool.Length; id++)
                {
                    var enemy = sector.enemyPool[id];
                    if (enemy.id != id || enemy.protoId <= 0 || !enemy.isSpace || !enemy.dynamic) continue;
                    if (enemy.astroId != target.StarId) continue;
                    array.Add(OwnedMotion("df_space_unit", id, 0, EntityToolUtil.ProtoName(enemy.protoId)));
                    count++;
                }
                return count;
            }

            var factory = target.Planet.factory;
            if (factory?.enemyPool == null) return 0;
            for (int id = 1; id < factory.enemyCursor && id < factory.enemyPool.Length; id++)
            {
                var enemy = factory.enemyPool[id];
                if (enemy.id != id || enemy.protoId <= 0 || !enemy.dynamic) continue;
                if (enemy.dfGBaseId <= 0) continue;
                array.Add(OwnedMotion("df_ground_unit", id, 0, EntityToolUtil.ProtoName(enemy.protoId)));
                count++;
            }
            return count;
        }

        private static JsonValue OwnedMotion(string kind, int id, int subId, string name)
        {
            var obj = JsonValue.NewObject();
            obj.Set("kind", kind);
            obj.Set("id", id);
            if (subId > 0) obj.Set("subId", subId);
            obj.Set("name", name);
            return obj;
        }

        private static GameToolJobResult Storage(JsonValue data, SpatialRequest request, PointTarget target)
        {
            var items = new List<ItemAmount>();
            switch (target.Type)
            {
                case SpatialTypes.Building:
                {
                    var storage = StatAggregator.GetStorage(target.Planet.factory, TargetStorageId(target));
                    if (storage == null)
                    {
                        data.Set("items", JsonValue.NewArray());
                        data.Set("total", 0);
                        WriteStatMeta(data, request, windowNative: false);
                        return Result(data, $"{target.Describe()} 没有仓储");
                    }
                    items = StatAggregator.Storage(storage);
                    break;
                }
                case SpatialTypes.Vein:
                {
                    var vein = target.Planet.factory.veinPool[target.VeinId];
                    var entry = new ItemAmount
                    {
                        ItemId = vein.productId,
                        Name = GameToolFormat.ItemName(vein.productId),
                        Amount = vein.amount,
                        Stored = vein.amount,
                    };
                    items.Add(entry);
                    break;
                }
                case SpatialTypes.Vegetation:
                {
                    var vege = target.Planet.factory.vegePool[target.VegeId];
                    var proto = LDB.veges != null ? LDB.veges.Select(vege.protoId) : null;
                    if (proto?.MiningItem != null && proto.MiningCount != null)
                    {
                        var length = Math.Min(proto.MiningItem.Length, proto.MiningCount.Length);
                        for (int i = 0; i < length; i++)
                        {
                            var itemId = proto.MiningItem[i];
                            if (itemId <= 0) continue;
                            items.Add(new ItemAmount
                            {
                                ItemId = itemId,
                                Name = GameToolFormat.ItemName(itemId),
                                Amount = proto.MiningCount[i],
                            });
                        }
                    }
                    break;
                }
                case SpatialTypes.DfSpaceBuilding:
                    data.Set("matter", DfStats.HiveMatter(SpaceRegionTool.FindHive(target.Star)));
                    WriteStatMeta(data, request, windowNative: false);
                    return Result(data, "黑雾建筑的物质与能量");
                case SpatialTypes.DfGroundBuilding:
                    data.Set("matter", DfStats.BaseMatter(SpaceRegionTool.FindBase(target.Planet, BaseIdOf(target))));
                    WriteStatMeta(data, request, windowNative: false);
                    return Result(data, "黑雾建筑的物质与能量");
                default:
                    throw new SpatialException($"类型 {target.Type} 没有存量");
            }

            data.Set("items", ItemArray(items, request));
            data.Set("total", items.Count);
            WriteStatMeta(data, request, windowNative: false);
            return Result(data, $"{target.Describe()}：{items.Count} 种物品");
        }

        private static int TargetStorageId(PointTarget target)
        {
            var factory = target.Planet?.factory;
            if (factory?.entityPool == null) return 0;
            if (target.EntityId <= 0 || target.EntityId >= factory.entityCursor) return 0;
            return factory.entityPool[target.EntityId].storageId;
        }

        private static int BaseIdOf(PointTarget target)
        {
            var factory = target.Planet?.factory;
            if (factory?.enemyPool == null) return 0;
            if (target.EnemyId <= 0 || target.EnemyId >= factory.enemyCursor) return 0;
            return factory.enemyPool[target.EnemyId].dfGBaseId;
        }

        private static GameToolJobResult Flow(JsonValue data, SpatialRequest request, PointTarget target)
        {
            if (target.Type == SpatialTypes.DfSpaceBuilding || target.Type == SpatialTypes.DfGroundBuilding)
            {
                data.Set("matter", target.Type == SpatialTypes.DfSpaceBuilding
                    ? DfStats.HiveMatter(SpaceRegionTool.FindHive(target.Star))
                    : DfStats.BaseMatter(SpaceRegionTool.FindBase(target.Planet, BaseIdOf(target))));
                WriteStatMeta(data, request, windowNative: false);
                return Result(data, "黑雾物质流量");
            }

            if (target.Type == SpatialTypes.Vein)
            {
                // 矿脉视角的"输出"= 采矿机采出的速率；只能按装机算，没有实际统计。
                var vein = target.Planet.factory.veinPool[target.VeinId];
                var amount = VeinOutputPerMin(target.Planet.factory, vein);
                var obj = JsonValue.NewObject();
                obj.Set("itemId", vein.productId);
                obj.Set("name", GameToolFormat.ItemName(vein.productId));
                obj.Set("amount", GameToolFormat.Round(SpatialRequest.ConvertUnit(amount, request.Window, request.Unit), 2));
                obj.Set("basis", "installed");
                data.Set("items", obj);
                WriteStatMeta(data, request, windowNative: false);
                data.Set("hint", "矿脉没有实际统计，数值由开采机数量与矿物速度算出。");
                return Result(data, $"{target.Describe()}：{GameToolFormat.ItemName(vein.productId)} 输出");
            }

            if (target.Type != SpatialTypes.Building)
                throw new SpatialException($"类型 {target.Type} 没有流量");

            var planet = target.Planet;
            if (planet?.factory == null) throw new SpatialException("该行星的工厂尚未加载");
            var ids = new List<int> { target.EntityId };

            List<ItemAmount> items;
            if (request.Basis == StatBasis.Installed)
            {
                var isProduction = request.Method == SpatialMethod.Production;
                var isConsumption = request.Method == SpatialMethod.Consumption;
                if (!isProduction && !isConsumption)
                    throw new SpatialException(
                        $"{SpatialTypes.NameOf(request.Method)} 只支持 basis=actual");
                items = StatAggregator.InstalledDelta(planet.factory, ids, request.Window, request.Unit, isProduction);
            }
            else
            {
                items = StatAggregator.ProductionDelta(planet, ids, request.Window, request.Unit);
            }

            data.Set("items", ItemArray(items, request));
            data.Set("total", items.Count);
            WriteStatMeta(data, request, windowNative: true);
            var display = $"{target.Describe()}：{items.Count} 种物品";
            var truncated = request.Offset + Math.Min(request.Limit, items.Count) < items.Count;
            return Result(data, display, false, truncated,
                truncated ? request.Offset + request.Limit : -1);
        }

        /// <summary>
        /// 矿脉的理论采出速率：minerCount × 每分钟采出次数。
        /// MiningTime 是每轮的原生 tick 数，必须在调用前后各乘一次，别再按秒折算。
        /// </summary>
        private static double VeinOutputPerMin(PlanetFactory factory, VeinData vein)
        {
            var proto = LDB.veins != null ? LDB.veins.Select((int)vein.type) : null;
            if (proto == null || proto.MiningTime <= 0) return 0;
            var cyclesPerMin = 60.0 * 60.0 / proto.MiningTime;
            return vein.minerCount * cyclesPerMin;
        }

        private static GameToolJobResult Power(JsonValue data, SpatialRequest request, PointTarget target)
        {
            if (target.Type != SpatialTypes.Building)
            {
                data.Set("matter", target.Type == SpatialTypes.DfSpaceBuilding
                    ? DfStats.HiveMatter(SpaceRegionTool.FindHive(target.Star))
                    : DfStats.BaseMatter(SpaceRegionTool.FindBase(target.Planet, BaseIdOf(target))));
                WriteStatMeta(data, request, windowNative: false);
                return Result(data, "黑雾能量");
            }

            var ids = new List<int> { target.EntityId };
            data.Set("power", StatAggregator.Power(target.Planet.factory, ids));
            WriteStatMeta(data, request, windowNative: false);
            return Result(data, $"{target.Describe()}：电力");
        }

        private static GameToolJobResult Parent(JsonValue data, SpatialRequest request, PointTarget target)
        {
            var chain = JsonValue.NewArray();
            var obj = JsonValue.NewObject();
            switch (target.Type)
            {
                case SpatialTypes.WorldPosition:
                    obj.Set("type", "star_system");
                    obj.Set("note", "世界坐标跨恒星系，向上没有单一容器");
                    break;
                case SpatialTypes.PlanetPosition:
                    obj.Set("type", "planet");
                    obj.Set("planetId", target.PlanetId);
                    obj.Set("planetName", PlanetFrame.PlanetName(target.Planet));
                    break;
                case SpatialTypes.Vein:
                case SpatialTypes.Vegetation:
                    obj.Set("type", "planet");
                    obj.Set("planetId", target.PlanetId);
                    obj.Set("planetName", PlanetFrame.PlanetName(target.Planet));
                    break;
                case SpatialTypes.DfGroundBuilding:
                    obj.Set("type", "df_base");
                    obj.Set("planetId", target.PlanetId);
                    obj.Set("baseId", BaseIdOf(target));
                    break;
                case SpatialTypes.DfSpaceBuilding:
                    obj.Set("type", "df_hive");
                    obj.Set("starId", target.StarId);
                    break;
                default:
                    obj.Set("type", "factory");
                    obj.Set("planetId", target.PlanetId);
                    obj.Set("networkId", RegionResolver.NetworkOf(target.Planet.factory, target.EntityId));
                    obj.Set("planetName", PlanetFrame.PlanetName(target.Planet));
                    break;
            }
            chain.Add(obj);
            data.Set("parents", chain);
            return Result(data, $"{target.Describe()} 的上级");
        }

        private static GameToolJobResult Location(JsonValue data, SpatialRequest request, PointTarget target)
        {
            if (target.Type == SpatialTypes.WorldPosition || target.IsWorldFrame)
            {
                var world = JsonValue.NewObject();
                world.Set("x", GameToolFormat.Round(target.World.x, 1));
                world.Set("y", GameToolFormat.Round(target.World.y, 1));
                world.Set("z", GameToolFormat.Round(target.World.z, 1));
                data.Set("world", world);
                if (target.Planet != null && request.Frame != "world")
                {
                    var local = JsonValue.NewObject();
                    local.Set("latitude", GameToolFormat.Round(target.Latitude, request.Precision));
                    local.Set("longitude", GameToolFormat.Round(target.Longitude, request.Precision));
                    local.Set("altitude", GameToolFormat.Round(target.Altitude, 1));
                    local.Set("planetId", target.PlanetId);
                    data.Set("planet", local);
                }
                return Result(data, target.Describe());
            }

            var planetObj = JsonValue.NewObject();
            planetObj.Set("planetId", target.PlanetId);
            planetObj.Set("planetName", PlanetFrame.PlanetName(target.Planet));
            planetObj.Set("latitude", GameToolFormat.Round(target.Latitude, request.Precision));
            planetObj.Set("longitude", GameToolFormat.Round(target.Longitude, request.Precision));
            planetObj.Set("altitude", GameToolFormat.Round(target.Altitude, 1));
            data.Set("planet", planetObj);

            if (request.Frame != "planet")
            {
                var worldObj = JsonValue.NewObject();
                worldObj.Set("x", GameToolFormat.Round(target.World.x, 1));
                worldObj.Set("y", GameToolFormat.Round(target.World.y, 1));
                worldObj.Set("z", GameToolFormat.Round(target.World.z, 1));
                data.Set("world", worldObj);
            }
            return Result(data, target.Describe());
        }

        private static GameToolJobResult Note(JsonValue data, SpatialRequest request, PointTarget target)
        {
            var text = NoteText(target);
            data.Set("note", text ?? string.Empty);
            return Result(data, string.IsNullOrEmpty(text) ? "暂无备忘录" : "备忘录 1 条");
        }

        private static string NoteText(PointTarget target)
        {
            if (target.Type != SpatialTypes.Building) return null;
            var factory = target.Planet?.factory;
            if (factory?.entityPool == null) return null;
            if (target.EntityId <= 0 || target.EntityId >= factory.entityCursor) return null;
            var entity = factory.entityPool[target.EntityId];
            // 全息信标的文字存在 MarkerComponent 里，按实体 id 读
            if (entity.markerId <= 0) return null;
            return factory.ReadMarkerWord(entity.markerId);
        }

        private static GameToolJobResult NavigationUnsupported(JsonValue data, SpatialRequest request, PointTarget target)
        {
            throw new SpatialException(
                $"导航是写操作，请用 game_tether_add；本次 target 为 {target.Describe()}");
        }
    }

    // ---------------------------------------------------------------- 动点

    internal sealed class SpaceMotionTool : SpatialToolBase
    {
        public override string Name => IcarusII.Common.Protocol.Protocol.ToolSpaceMotion;

        protected override GameToolJobResult Execute(SpatialRequest request, JsonValue args)
        {
            if (request.Type is null) request.Type = SpatialTypes.Player;
            request.RequireSupported();
            var target = MotionCatalog.Resolve(request);

            var data = JsonValue.NewObject();
            data.Set("type", target.Type);
            data.Set("target", target.Describe());
            data.Set("name", target.Name ?? string.Empty);
            if (target.PlanetId > 0) data.Set("planetId", target.PlanetId);
            if (target.StarId > 0) data.Set("starId", target.StarId);

            switch (request.Method)
            {
                case SpatialMethod.List:
                    return ListOwned(data, request, target);
                case SpatialMethod.Storage:
                    return Storage(data, request, target);
                case SpatialMethod.Parent:
                    return Parent(data, request, target);
                case SpatialMethod.Location:
                    return Location(data, request, target);
                case SpatialMethod.Information:
                    return Information(data, request, target);
                case SpatialMethod.Navigation:
                    throw new SpatialException(
                        "导航是写操作，请用 game_tether_add；动点无法用 tether 追踪，"
                        + "需要位置时重新调用 space_motion op=location");
                default:
                    throw new SpatialException(
                        $"类型 {request.Type} 不支持 {SpatialTypes.NameOf(request.Method)}");
            }
        }

        private static GameToolJobResult ListOwned(JsonValue data, SpatialRequest request, MotionTarget target)
        {
            if (target.Type != SpatialTypes.Player)
                throw new SpatialException("只有玩家有下辖动点");

            var array = JsonValue.NewArray();
            var count = 0;

            var planet = GameMain.data.localPlanet;
            if (planet?.factory?.craftPool != null)
            {
                for (int id = 1; id < planet.factory.craftCursor && id < planet.factory.craftPool.Length; id++)
                {
                    var craft = planet.factory.craftPool[id];
                    if (craft.id != id || craft.protoId <= 0) continue;
                    if (craft.owner != 0 && craft.prototype != ECraftProto.ConstructionDrone) continue;
                    array.Add(OwnedMotion("construction_drone", id, EntityToolUtil.ProtoName(craft.protoId)));
                    count++;
                }
            }

            var units = GameMain.data.localPlanet?.factory?.combatGroundSystem?.units;
            if (units?.buffer != null)
            {
                for (int id = 1; id < units.cursor && id < units.buffer.Length; id++)
                {
                    if (units.buffer[id].id != id) continue;
                    array.Add(OwnedMotion("combat_drone", id, "战斗无人机"));
                    count++;
                }
            }

            data.Set("owned", array);
            data.Set("total", count);
            return Result(data, $"玩家名下 {count} 个动点");
        }

        private static JsonValue OwnedMotion(string kind, int id, string name)
        {
            var obj = JsonValue.NewObject();
            obj.Set("kind", kind);
            obj.Set("id", id);
            obj.Set("name", name);
            return obj;
        }

        private static GameToolJobResult Storage(JsonValue data, SpatialRequest request, MotionTarget target)
        {
            var items = new List<ItemAmount>();
            if (target.Type == SpatialTypes.Player)
            {
                var player = GameMain.mainPlayer;
                if (player == null) throw new SpatialException("当前不在游戏内");
                items.AddRange(StatAggregator.Storage(player.package));
                var inhand = player.inhandItemId;
                if (inhand > 0)
                {
                    var found = false;
                    foreach (var entry in items)
                        if (entry.ItemId == inhand) found = true;
                    if (!found)
                        items.Add(new ItemAmount
                        {
                            ItemId = inhand,
                            Name = GameToolFormat.ItemName(inhand),
                            Amount = 1,
                        });
                }
            }
            else if (target.Type == SpatialTypes.Trash)
            {
                var system = GameMain.data.trashSystem;
                if (system?.container?.trashDataPool == null)
                    throw new SpatialException("垃圾系统尚未加载");
                var trash = system.container.trashDataPool[target.TrashId];
                var proto = LDB.items != null ? LDB.items.Select(0) : null;
                items.Add(new ItemAmount { ItemId = 0, Name = "垃圾块", Amount = 1, Stored = trash.life });
            }
            else
            {
                items = CraftStorage(target);
            }

            data.Set("items", ItemArray(items, request));
            data.Set("total", items.Count);
            WriteStatMeta(data, request, windowNative: false);
            return Result(data, $"{target.Describe()}：{items.Count} 种物品");
        }

        /// <summary>运输中的 craft 携带的物品来自它正在搬运的那一件货。</summary>
        private static List<ItemAmount> CraftStorage(MotionTarget target)
        {
            var result = new List<ItemAmount>();
            var planet = GameMain.data.localPlanet;
            if (planet?.factory?.craftPool == null || target.CraftId <= 0) return result;
            if (target.CraftId >= planet.factory.craftCursor) return result;
            var craft = planet.factory.craftPool[target.CraftId];

            if (craft.droneId <= 0) return result;

            var station = FindStationByDrone(planet, craft.droneId);
            if (station?.workDroneDatas == null) return result;
            var index = DroneIndexOf(station, craft.droneId);
            if (index < 0) return result;

            var drone = station.workDroneDatas[index];
            if (drone.itemId > 0)
                result.Add(new ItemAmount
                {
                    ItemId = drone.itemId,
                    Name = GameToolFormat.ItemName(drone.itemId),
                    Amount = drone.itemCount,
                });
            return result;
        }

        /// <summary>无人机不落在全局池里，只能按站遍历 workDroneDatas 的 gene 找。</summary>
        private static StationComponent FindStationByDrone(PlanetData planet, int droneId)
        {
            var transport = GameMain.data.galacticTransport;
            if (transport?.stationPool == null) return null;
            for (int i = 1; i < transport.stationCursor && i < transport.stationPool.Length; i++)
            {
                var station = transport.stationPool[i];
                if (station == null || station.planetId != planet.id) continue;
                if (DroneIndexOf(station, droneId) >= 0) return station;
            }
            return null;
        }

        private static int DroneIndexOf(StationComponent station, int droneId)
        {
            var datas = station.workDroneDatas;
            if (datas == null) return -1;
            for (int d = 0; d < datas.Length; d++)
                if (datas[d].gene == droneId) return d;
            return -1;
        }

        private static GameToolJobResult Parent(JsonValue data, SpatialRequest request, MotionTarget target)
        {
            var obj = JsonValue.NewObject();
            switch (target.Type)
            {
                case SpatialTypes.Trash:
                    obj.Set("type", "none");
                    obj.Set("note", "垃圾块没有归属");
                    break;
                case SpatialTypes.ConstructionDrone:
                    obj.Set("type", "player");
                    break;
                case SpatialTypes.DeliveryCourier:
                    obj.Set("type", "player");
                    break;
                case SpatialTypes.StationDrone:
                case SpatialTypes.StationShip:
                    obj.Set("type", "station");
                    obj.Set("stationGid", target.StationGid);
                    break;
                case SpatialTypes.DysonRocket:
                    obj.Set("type", "silo");
                    obj.Set("starId", target.StarId);
                    break;
                case SpatialTypes.CombatDrone:
                case SpatialTypes.CombatFleet:
                    obj.Set("type", "fleet");
                    obj.Set("fleetId", target.FleetId);
                    break;
                case SpatialTypes.DfSpaceUnit:
                    obj.Set("type", "df_hive");
                    obj.Set("starId", target.StarId);
                    break;
                case SpatialTypes.DfGroundUnit:
                    obj.Set("type", "df_base");
                    obj.Set("planetId", target.PlanetId);
                    break;
                default:
                    obj.Set("type", "none");
                    break;
            }
            var chain = JsonValue.NewArray();
            chain.Add(obj);
            data.Set("parents", chain);
            return Result(data, $"{target.Describe()} 的归属");
        }

        private static GameToolJobResult Location(JsonValue data, SpatialRequest request, MotionTarget target)
        {
            var state = new MotionState
            {
                GameTick = GameMain.gameTick,
                UPosition = PlanetFrame.ToVec3d(target.World),
                PlanetId = target.PlanetId,
                OnPlanet = target.OnPlanet,
                Velocity = target.Velocity,
                Speed = target.Velocity.Length,
            };

            if (target.OnPlanet && target.Planet != null)
            {
                var local = PlanetFrame.WorldToLocal(target.Planet, target.World);
                var (lat, lon) = GeoMath.ToLatLon(new Vec3d(local.x, local.y, local.z));
                state.Latitude = lat;
                state.Longitude = lon;
                state.Altitude = local.magnitude - target.Planet.realRadius;
            }

            data.Set("location", state.ToJson(request.Precision));
            return Result(data, target.Describe());
        }

        private static GameToolJobResult Information(JsonValue data, SpatialRequest request, MotionTarget target)
        {
            var info = JsonValue.NewObject();
            info.Set("speed", GameToolFormat.Round(target.Velocity.Length, 2));

            switch (target.Type)
            {
                case SpatialTypes.Player:
                {
                    var player = GameMain.mainPlayer;
                    if (player == null) throw new SpatialException("当前不在游戏内");
                    info.Set("movementState", player.movementState.ToString());
                    info.Set("warpState", GameToolFormat.Round(player.warpState, 2));
                    var mecha = player.mecha;
                    if (mecha != null)
                    {
                        info.Set("coreEnergy", GameToolFormat.Round(mecha.coreEnergy, 1));
                        info.Set("coreEnergyCap", GameToolFormat.Round(mecha.coreEnergyCap, 1));
                    }
                    break;
                }
                case SpatialTypes.ConstructionDrone:
                case SpatialTypes.DeliveryCourier:
                case SpatialTypes.StationDrone:
                {
                    var planet = GameMain.data.localPlanet;
                    var factory = planet?.factory;
                    if (factory?.craftPool != null && target.CraftId > 0 &&
                        target.CraftId < factory.craftCursor)
                    {
                        var craft = factory.craftPool[target.CraftId];
                        info.Set("prototype", craft.prototype.ToString());
                        info.Set("dynamic", craft.dynamic);
                    }
                    break;
                }
                case SpatialTypes.StationShip:
                {
                    var station = FindStationByGid(target.StationGid);
                    if (station != null)
                    {
                        info.Set("warpState", GameToolFormat.Round(
                            station.workShipDatas[target.CraftId].warpState, 2));
                        info.Set("warperCount", station.warperCount);
                    }
                    break;
                }
                case SpatialTypes.DysonRocket:
                    info.Set("starId", target.StarId);
                    break;
                case SpatialTypes.CombatDrone:
                case SpatialTypes.CombatFleet:
                {
                    var planet = GameMain.data.localPlanet;
                    var system = planet?.factory?.combatGroundSystem;
                    if (system?.units?.buffer != null && target.UnitId > 0 &&
                        target.UnitId < system.units.buffer.Length)
                    {
                        var unit = system.units.buffer[target.UnitId];
                        info.Set("behavior", unit.behavior.ToString());
                        info.Set("hpShortage", unit.hpShortage);
                    }
                    break;
                }
                case SpatialTypes.DfSpaceUnit:
                case SpatialTypes.DfGroundUnit:
                {
                    var enemy = FindEnemy(target);
                    if (enemy != null)
                    {
                        info.Set("stateFlags", enemy.Value.stateFlags);
                        info.Set("dynamic", enemy.Value.dynamic);
                    }
                    break;
                }
                case SpatialTypes.Trash:
                {
                    var system = GameMain.data.trashSystem;
                    if (system?.container?.trashDataPool != null)
                    {
                        var trash = system.container.trashDataPool[target.TrashId];
                        info.Set("life", trash.life);
                        info.Set("landPlanetId", trash.landPlanetId);
                        info.Set("nearStarId", trash.nearStarId);
                    }
                    break;
                }
            }

            data.Set("information", info);
            return Result(data, $"{target.Describe()} 的状态");
        }

        private static StationComponent FindStationByGid(int gid)
        {
            var transport = GameMain.data.galacticTransport;
            if (transport?.stationPool == null) return null;
            for (int i = 1; i < transport.stationCursor && i < transport.stationPool.Length; i++)
            {
                var station = transport.stationPool[i];
                if (station != null && station.gid == gid) return station;
            }
            return null;
        }

        private static EnemyData? FindEnemy(MotionTarget target)
        {
            if (target.Type == SpatialTypes.DfSpaceUnit)
            {
                var sector = GameMain.data.spaceSector;
                if (sector?.enemyPool == null || target.EnemyId >= sector.enemyCursor) return null;
                var enemy = sector.enemyPool[target.EnemyId];
                return enemy.id == target.EnemyId ? enemy : (EnemyData?)null;
            }
            var factory = GameMain.data.localPlanet?.factory;
            if (factory?.enemyPool == null || target.EnemyId >= factory.enemyCursor) return null;
            var ground = factory.enemyPool[target.EnemyId];
            return ground.id == target.EnemyId ? ground : (EnemyData?)null;
        }
    }

}
