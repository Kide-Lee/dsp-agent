using System;
using System.Globalization;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>一次解析完成的区域：类型 + 用于判定的几何 + 领域对象 id。</summary>
    internal sealed class RegionTarget
    {
        public string Type;
        public int PlanetId;
        public int StarId;
        public int NetworkId;
        public int SeedEntityId;
        public string BlueprintPath;

        public RegionShape Shape;

        public PlanetData Planet;
        public StarData Star;

        public string Describe()
        {
            switch (Type)
            {
                case SpatialTypes.StarSystem:
                    return $"恒星系 {PlanetFrame.StarName(Star)}（#{StarId}）";
                case SpatialTypes.Star:
                    return $"恒星 {PlanetFrame.StarName(Star)}（#{StarId}）";
                case SpatialTypes.Planet:
                    return $"行星 {PlanetFrame.PlanetName(Planet)}（#{PlanetId}）";
                case SpatialTypes.PowerGrid:
                    return $"电网 #{NetworkId}" + (Planet != null ? $" @ {PlanetFrame.PlanetName(Planet)}" : string.Empty);
                case SpatialTypes.Factory:
                    return $"工厂（电网 #{NetworkId}，种子实体 #{SeedEntityId}）";
                case SpatialTypes.DfHive:
                    return $"太空黑雾巢穴 #{StarId}";
                case SpatialTypes.DfBase:
                    return $"地面黑雾基地 #{SeedEntityId}" + (Planet != null ? $" @ {PlanetFrame.PlanetName(Planet)}" : string.Empty);
                case SpatialTypes.DysonSphere:
                    return $"戴森球 @ {PlanetFrame.StarName(Star)}";
                case SpatialTypes.WorldBox:
                    return "世界区域";
                case SpatialTypes.PlanetPatch:
                    return "行星区域" + (Planet != null ? $" @ {PlanetFrame.PlanetName(Planet)}" : string.Empty);
                case SpatialTypes.Blueprint:
                    return $"蓝图 {BlueprintPath}";
                default:
                    return Type ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// 把 target 参数解析成具体对象。
    /// 规则来自 docs/dsp-agent.xlsx 区域页的 target 行：先看玩家上下文，再退到"最近"。
    /// </summary>
    internal static class RegionResolver
    {
        public static RegionTarget Resolve(SpatialRequest request)
        {
            var type = request.Type ?? InferType();
            var target = new RegionTarget { Type = type };
            if (!SpatialTypes.IsRegion(type))
                throw new SpatialException($"类型 {type} 不是区域");

            switch (type)
            {
                case SpatialTypes.StarSystem:
                case SpatialTypes.Star:
                    target.Star = ResolveStar(request.TargetId);
                    target.StarId = target.Star != null ? target.Star.id : 0;
                    target.Planet = GameMain.data != null ? GameMain.data.localPlanet : null;
                    target.PlanetId = target.Planet != null ? target.Planet.id : 0;
                    break;
                case SpatialTypes.Planet:
                    target.Planet = ResolvePlanet(request.TargetId);
                    target.PlanetId = target.Planet != null ? target.Planet.id : 0;
                    target.Star = target.Planet != null ? target.Planet.star : null;
                    target.StarId = target.Star != null ? target.Star.id : 0;
                    break;
                case SpatialTypes.PowerGrid:
                case SpatialTypes.Factory:
                    ResolveFactoryLike(request, target);
                    break;
                case SpatialTypes.DfHive:
                    target.Star = ResolveStar(request.TargetId);
                    target.StarId = target.Star != null ? target.Star.id : 0;
                    target.Planet = GameMain.data != null ? GameMain.data.localPlanet : null;
                    target.PlanetId = target.Planet != null ? target.Planet.id : 0;
                    break;
                case SpatialTypes.DfBase:
                    target.SeedEntityId = ParseId(request.TargetId);
                    target.Planet = target.SeedEntityId > 0
                        ? FindPlanetByEnemy(target.SeedEntityId)
                        : (GameMain.data != null ? GameMain.data.localPlanet : null);
                    target.PlanetId = target.Planet != null ? target.Planet.id : 0;
                    target.Star = target.Planet != null ? target.Planet.star : null;
                    target.StarId = target.Star != null ? target.Star.id : 0;
                    break;
                case SpatialTypes.DysonSphere:
                    target.Star = ResolveStar(request.TargetId);
                    target.StarId = target.Star != null ? target.Star.id : 0;
                    if (DysonSpheres.Get(target.StarId) == null)
                        throw new SpatialException($"恒星系 {PlanetFrame.StarName(target.Star)} 还没有戴森球");
                    break;
                case SpatialTypes.WorldBox:
                    BuildWorldBox(request, target);
                    break;
                case SpatialTypes.PlanetPatch:
                    BuildPlanetPatch(request, target);
                    break;
                case SpatialTypes.Blueprint:
                    target.BlueprintPath = request.TargetId;
                    if (string.IsNullOrWhiteSpace(target.BlueprintPath))
                        throw new SpatialException("蓝图类型必须显式给 target（蓝图文件路径）");
                    break;
                default:
                    throw new SpatialException($"未实现的区域类型：{type}");
            }

            return target;
        }

        /// <summary>target 没写类型时按玩家所在上下文定：在行星上就是行星，否则是恒星系。</summary>
        public static string InferType()
        {
            var planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            return planet != null ? SpatialTypes.Planet : SpatialTypes.StarSystem;
        }

        private static void ResolveFactoryLike(SpatialRequest request, RegionTarget target)
        {
            var planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            if (planet == null) throw new SpatialException("当前不在行星上，无法确定电网或工厂");

            // 显式 id：networkId:seedEntityId 或只给 networkId
            if (!string.IsNullOrEmpty(request.TargetId))
            {
                var parts = request.TargetId.Split(':');
                var networkId = ParseId(parts[0]);
                if (networkId <= 0) throw new SpatialException($"无法解析电网 id：{request.TargetId}");
                target.NetworkId = networkId;
                target.SeedEntityId = parts.Length > 1 ? ParseId(parts[1]) : 0;
                target.Planet = planet;
                target.PlanetId = planet.id;
                return;
            }

            var seed = AimedEntityId(planet);
            if (seed > 0)
            {
                var networkId = NetworkOf(planet.factory, seed);
                if (networkId > 0)
                {
                    target.NetworkId = networkId;
                    target.SeedEntityId = seed;
                    target.Planet = planet;
                    target.PlanetId = planet.id;
                    return;
                }
            }

            var nearest = NearestNetwork(planet);
            if (nearest <= 0) throw new SpatialException("玩家所在行星上没有可用的电网");
            target.NetworkId = nearest;
            target.SeedEntityId = 0;
            target.Planet = planet;
            target.PlanetId = planet.id;
        }

        private static void BuildWorldBox(SpatialRequest request, RegionTarget target)
        {
            var spec = request.TargetSpec;
            target.Shape = new RegionShape { IsWorldFrame = true };

            var min = spec?["min"];
            var max = spec?["max"];
            if (min != null && !min.IsNull && max != null && !max.IsNull)
            {
                target.Shape.BoxMin = ReadVector(min);
                target.Shape.BoxMax = ReadVector(max);
                return;
            }

            // auto：以玩家为中心的正方体
            var center = PlanetFrame.PlayerUPosition();
            var radius = request.Radius;
            target.Shape.BoxMin = new Vec3d(center.X - radius, center.Y - radius, center.Z - radius);
            target.Shape.BoxMax = new Vec3d(center.X + radius, center.Y + radius, center.Z + radius);
        }

        private static void BuildPlanetPatch(SpatialRequest request, RegionTarget target)
        {
            var planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            target.Planet = planet;
            target.PlanetId = planet != null ? planet.id : 0;
            target.Star = planet != null ? planet.star : null;
            target.StarId = target.Star != null ? target.Star.id : 0;
            if (planet == null) throw new SpatialException("当前不在行星上，无法构造行星区域");

            var spec = request.TargetSpec;
            var shape = new RegionShape { PlanetRadius = planet.realRadius };
            if (spec != null && spec["latMin"] != null && !spec["latMin"].IsNull)
            {
                shape.LatMin = spec["latMin"].AsDouble(-90);
                shape.LatMax = spec["latMax"].AsDouble(90);
                shape.LonStart = spec["lonStart"].AsDouble(-180);
                shape.LonSpan = spec["lonSpan"].AsDouble(360);
            }
            else
            {
                // auto：以玩家经纬度为中心的方块，边长由 radius 折算成度数
                var local = PlanetFrame.WorldToLocal(planet, PlanetFrame.ToVectorLF3(PlanetFrame.PlayerUPosition()));
                var (lat, lon) = GeoMath.ToLatLon(new Vec3d(local.x, local.y, local.z));
                var degPerMeter = GeoMath.RadToDeg / Math.Max(1.0, planet.realRadius);
                var halfLat = Math.Min(89.0, request.Radius * degPerMeter);
                shape.LatMin = Math.Max(-90, lat - halfLat);
                shape.LatMax = Math.Min(90, lat + halfLat);
                shape.LonStart = GeoMath.NormalizeLongitude(lon - halfLat);
                shape.LonSpan = Math.Min(360, halfLat * 2);
            }

            if (shape.LatMin > shape.LatMax)
            {
                var swap = shape.LatMin;
                shape.LatMin = shape.LatMax;
                shape.LatMax = swap;
            }
            target.Shape = shape;
        }

        // ------------------------------------------------------------ 查找

        public static StarData ResolveStar(string idText)
        {
            var galaxy = GameMain.data != null ? GameMain.data.galaxy : null;
            if (galaxy == null) throw new SpatialException("星系数据尚未加载");
            var id = ParseId(idText);
            if (id > 0)
            {
                var star = galaxy.StarById(id);
                if (star == null) throw new SpatialException($"恒星 #{id} 不存在");
                return star;
            }
            var local = GameMain.data.localStar;
            if (local == null) throw new SpatialException("当前没有载入恒星系");
            return local;
        }

        public static PlanetData ResolvePlanet(string idText)
        {
            var galaxy = GameMain.data != null ? GameMain.data.galaxy : null;
            if (galaxy == null) throw new SpatialException("星系数据尚未加载");
            var id = ParseId(idText);
            if (id > 0)
            {
                var planet = galaxy.PlanetById(id);
                if (planet == null) throw new SpatialException($"行星 #{id} 不存在");
                return planet;
            }
            var local = GameMain.data.localPlanet;
            if (local == null) throw new SpatialException("当前没有载入行星");
            return local;
        }

        private static PlanetData FindPlanetByEnemy(int enemyId)
        {
            var data = GameMain.data;
            if (data == null || data.factories == null) return null;
            for (int i = 0; i < data.factoryCount && i < data.factories.Length; i++)
            {
                var factory = data.factories[i];
                if (factory == null || factory.enemyPool == null) continue;
                for (int id = 1; id < factory.enemyCursor && id < factory.enemyPool.Length; id++)
                {
                    if (factory.enemyPool[id].id == id) return factory.planet;
                }
            }
            return null;
        }

        /// <summary>玩家瞄准/悬停的建筑 id；没有瞄准时为 0。</summary>
        public static int AimedEntityId(PlanetData planet)
        {
            try
            {
                var controller = GameMain.mainPlayer != null ? GameMain.mainPlayer.controller : null;
                var inspect = controller != null ? controller.actionInspect : null;
                if (inspect == null) return 0;
                if (inspect.hoveringEntityId > 0) return inspect.hoveringEntityId;
                return inspect.inspectType == EObjectType.Entity ? inspect.inspectId : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>建筑所在的电网；无电或未加载时为 0。</summary>
        public static int NetworkOf(PlanetFactory factory, int entityId)
        {
            if (factory == null || factory.entityPool == null) return 0;
            if (entityId <= 0 || entityId >= factory.entityCursor || entityId >= factory.entityPool.Length) return 0;
            var entity = factory.entityPool[entityId];
            var system = factory.powerSystem;
            if (system == null) return 0;
            if (entity.powerConId > 0 && system.consumerPool != null &&
                entity.powerConId < system.consumerPool.Length)
                return system.consumerPool[entity.powerConId].networkId;
            if (entity.powerGenId > 0 && system.genPool != null &&
                entity.powerGenId < system.genPool.Length)
                return system.genPool[entity.powerGenId].networkId;
            if (entity.powerAccId > 0 && system.accPool != null &&
                entity.powerAccId < system.accPool.Length)
                return system.accPool[entity.powerAccId].networkId;
            if (entity.powerNodeId > 0 && system.nodePool != null &&
                entity.powerNodeId < system.nodePool.Length)
                return system.nodePool[entity.powerNodeId].networkId;
            return 0;
        }

        /// <summary>离玩家最近的电网 id；没有电网时为 0。</summary>
        public static int NearestNetwork(PlanetData planet)
        {
            var factory = planet != null ? planet.factory : null;
            if (factory == null || factory.powerSystem == null) return 0;
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var playerLocal = player.transform != null ? player.transform.position : Vector3.zero;

            var best = 0;
            var bestDistance = double.MaxValue;
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                var networkId = NetworkOf(factory, id);
                if (networkId <= 0) continue;
                var dx = entity.pos.x - playerLocal.x;
                var dz = entity.pos.z - playerLocal.z;
                var distance = dx * dx + dz * dz;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = networkId;
                }
            }
            return best;
        }

        public static int ParseId(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var trimmed = text.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? id : 0;
        }

        private static Vec3d ReadVector(JsonValue value)
        {
            return new Vec3d(value["x"].AsDouble(0), value["y"].AsDouble(0), value["z"].AsDouble(0));
        }
    }
}
