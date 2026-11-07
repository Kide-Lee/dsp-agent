using System;
using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using IcarusII.Game.Tools;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>解析完成的定点：类型 + 定位所需的一切。</summary>
    internal sealed class PointTarget
    {
        public string Type;
        public int PlanetId;
        public int EntityId;
        public int VeinId;
        public int VegeId;
        public int EnemyId;
        public int StarId;

        public PlanetData Planet;
        public StarData Star;

        /// <summary>世界坐标；行星球面成员也可由此反算局部坐标。</summary>
        public VectorLF3 World;
        /// <summary>行星局部坐标；世界坐标类定点为 Vector3.zero。</summary>
        public Vector3 Local;
        public bool HasLocal;
        public bool IsWorldFrame;

        public double Latitude;
        public double Longitude;
        public double Altitude;

        public string Name;

        public string Describe()
        {
            switch (Type)
            {
                case SpatialTypes.Building:
                    return $"建筑 {Name}（#{EntityId}）";
                case SpatialTypes.Vein:
                    return $"矿脉 {Name}（#{VeinId}）";
                case SpatialTypes.Vegetation:
                    return $"植被 {Name}（#{VegeId}）";
                case SpatialTypes.DfSpaceBuilding:
                case SpatialTypes.DfGroundBuilding:
                    return $"{Name}（#{EnemyId}）";
                case SpatialTypes.WorldPosition:
                    return "世界坐标";
                case SpatialTypes.PlanetPosition:
                    return "行星坐标";
                default:
                    return Type ?? string.Empty;
            }
        }
    }

    /// <summary>定点目录：把 target 解析成具体实体，并提供坐标与归属。</summary>
    internal static class PointCatalog
    {
        public static PointTarget Resolve(SpatialRequest request)
        {
            var type = request.Type ?? InferType();
            if (!SpatialTypes.IsPoint(type))
                throw new SpatialException($"类型 {type} 不是定点");

            var target = new PointTarget { Type = type };
            var id = RegionResolver.ParseId(request.TargetId);

            switch (type)
            {
                case SpatialTypes.Building:
                    ResolveBuilding(target, id, request);
                    break;
                case SpatialTypes.Vein:
                    ResolveVein(target, id);
                    break;
                case SpatialTypes.Vegetation:
                    ResolveVegetation(target, id);
                    break;
                case SpatialTypes.DfSpaceBuilding:
                    ResolveDfSpaceBuilding(target, id, request);
                    break;
                case SpatialTypes.DfGroundBuilding:
                    ResolveDfGroundBuilding(target, id);
                    break;
                case SpatialTypes.WorldPosition:
                    ResolveWorldPosition(target, request);
                    break;
                case SpatialTypes.PlanetPosition:
                    ResolvePlanetPosition(target, request);
                    break;
                default:
                    throw new SpatialException($"未实现的定点类型：{type}");
            }

            FillFrame(target);
            return target;
        }

        public static string InferType() => SpatialTypes.Building;

        // ------------------------------------------------------------ 各类型

        private static void ResolveBuilding(PointTarget target, int id, SpatialRequest request)
        {
            var planet = RegionResolver.ResolvePlanet(null);
            var factory = planet.factory;
            if (factory == null || factory.entityPool == null)
                throw new SpatialException($"行星 {PlanetFrame.PlanetName(planet)} 的工厂尚未加载");

            if (id <= 0) id = RegionResolver.AimedEntityId(planet);
            if (id <= 0) id = NearestEntity(factory, planet);
            if (id <= 0) throw new SpatialException("找不到任何建筑");

            if (id >= factory.entityCursor || id >= factory.entityPool.Length)
                throw new SpatialException($"建筑 #{id} 不存在");
            var entity = factory.entityPool[id];
            if (entity.id != id || entity.protoId <= 0)
                throw new SpatialException($"建筑 #{id} 已失效");

            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.EntityId = id;
            target.Local = entity.pos;
            target.HasLocal = true;
            target.Name = EntityToolUtil.ProtoName(entity.protoId);
        }

        private static void ResolveVein(PointTarget target, int id)
        {
            var planet = RegionResolver.ResolvePlanet(null);
            var factory = planet.factory;
            if (factory == null || factory.veinPool == null)
                throw new SpatialException("该行星的矿脉尚未加载");

            if (id <= 0) throw new SpatialException("矿脉需要显式给 veinId");
            if (id >= factory.veinCursor || id >= factory.veinPool.Length)
                throw new SpatialException($"矿脉 #{id} 不存在");
            var vein = factory.veinPool[id];
            if (vein.id != id) throw new SpatialException($"矿脉 #{id} 已失效");

            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.VeinId = id;
            target.Local = vein.pos;
            target.HasLocal = true;
            target.Name = GameToolFormat.VeinName(vein.type);
        }

        private static void ResolveVegetation(PointTarget target, int id)
        {
            var planet = RegionResolver.ResolvePlanet(null);
            var factory = planet.factory;
            if (factory == null || factory.vegePool == null)
                throw new SpatialException("该行星的植被尚未加载");

            if (id <= 0) throw new SpatialException("植被需要显式给 vegeId");
            if (id >= factory.vegeCursor || id >= factory.vegePool.Length)
                throw new SpatialException($"植被 #{id} 不存在");
            var vege = factory.vegePool[id];
            if (vege.id != id) throw new SpatialException($"植被 #{id} 已失效");

            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.VegeId = id;
            target.Local = vege.pos;
            target.HasLocal = true;
            target.Name = EntityToolUtil.ProtoName(vege.protoId);
        }

        private static void ResolveDfSpaceBuilding(PointTarget target, int id, SpatialRequest request)
        {
            var sector = GameMain.data != null ? GameMain.data.spaceSector : null;
            if (sector == null || sector.enemyPool == null)
                throw new SpatialException("宇宙黑雾尚未加载");

            var star = RegionResolver.ResolveStar(null);
            if (id <= 0) id = NearestSpaceEnemy(sector, star);
            if (id <= 0) throw new SpatialException("找不到太空黑雾建筑");
            if (id >= sector.enemyCursor || id >= sector.enemyPool.Length)
                throw new SpatialException($"太空黑雾建筑 #{id} 不存在");
            var enemy = sector.enemyPool[id];
            if (enemy.id != id || enemy.protoId <= 0)
                throw new SpatialException($"太空黑雾建筑 #{id} 已失效");

            target.Star = star;
            target.StarId = star != null ? star.id : 0;
            target.EnemyId = id;
            target.IsWorldFrame = true;
            target.World = enemy.pos;
            target.Name = EntityToolUtil.ProtoName(enemy.protoId);
        }

        private static void ResolveDfGroundBuilding(PointTarget target, int id)
        {
            var planet = RegionResolver.ResolvePlanet(null);
            var factory = planet.factory;
            if (factory == null || factory.enemyPool == null)
                throw new SpatialException("该行星的黑雾尚未加载");

            if (id <= 0) throw new SpatialException("地面黑雾建筑需要显式给 enemyId");
            if (id >= factory.enemyCursor || id >= factory.enemyPool.Length)
                throw new SpatialException($"地面黑雾建筑 #{id} 不存在");
            var enemy = factory.enemyPool[id];
            if (enemy.id != id || enemy.protoId <= 0)
                throw new SpatialException($"地面黑雾建筑 #{id} 已失效");

            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.EnemyId = id;
            target.Local = PlanetFrame.WorldToLocal(planet, enemy.pos);
            target.HasLocal = true;
            target.Name = EntityToolUtil.ProtoName(enemy.protoId);
        }

        private static void ResolveWorldPosition(PointTarget target, SpatialRequest request)
        {
            var player = GameMain.mainPlayer;
            if (player == null) throw new SpatialException("当前不在游戏内");
            target.IsWorldFrame = true;
            target.World = player.uPosition;
            target.Planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            target.PlanetId = target.Planet != null ? target.Planet.id : 0;
            target.Star = GameMain.data != null ? GameMain.data.localStar : null;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.Name = "玩家所在位置";
        }

        private static void ResolvePlanetPosition(PointTarget target, SpatialRequest request)
        {
            var planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            if (planet == null) throw new SpatialException("当前不在行星上");
            var player = GameMain.mainPlayer;
            var local = player != null ? player.transform.position : Vector3.zero;
            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.Local = local;
            target.HasLocal = true;
            target.Name = "玩家所在位置";
        }

        // ------------------------------------------------------------ 坐标

        private static void FillFrame(PointTarget target)
        {
            if (target.IsWorldFrame && target.Planet != null)
            {
                var latLon = PlanetFrame.TryLatLon(target.Planet, target.World);
                if (latLon.HasValue)
                {
                    target.Latitude = latLon.Value.Latitude;
                    target.Longitude = latLon.Value.Longitude;
                    target.Altitude = latLon.Value.Altitude;
                }
                return;
            }
            if (!target.HasLocal) return;
            target.World = PlanetFrame.LocalToWorld(target.Planet, target.Local);
            var (lat, lon) = GeoMath.ToLatLon(new Vec3d(target.Local.x, target.Local.y, target.Local.z));
            target.Latitude = lat;
            target.Longitude = lon;
            target.Altitude = target.Local.magnitude - (target.Planet != null ? target.Planet.realRadius : 0);
        }

        // ------------------------------------------------------------ 查找

        private static int NearestEntity(PlanetFactory factory, PlanetData planet)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var origin = player.transform.position;
            var best = 0;
            var bestSqr = double.MaxValue;
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                var dx = entity.pos.x - origin.x;
                var dz = entity.pos.z - origin.z;
                var sqr = dx * dx + dz * dz;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = id;
            }
            return best;
        }

        private static int NearestSpaceEnemy(SpaceSector sector, StarData star)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var origin = player.uPosition;
            var best = 0;
            var bestSqr = double.MaxValue;
            for (int id = 1; id < sector.enemyCursor && id < sector.enemyPool.Length; id++)
            {
                var enemy = sector.enemyPool[id];
                if (enemy.id != id || enemy.protoId <= 0 || !enemy.isSpace) continue;
                if (star != null && enemy.astroId != star.id) continue;
                var dx = enemy.pos.x - origin.x;
                var dy = enemy.pos.y - origin.y;
                var dz = enemy.pos.z - origin.z;
                var sqr = dx * dx + dy * dy + dz * dz;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = id;
            }
            return best;
        }
    }
}
