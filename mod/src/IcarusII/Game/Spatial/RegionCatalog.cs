using System;
using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using IcarusII.Game.Tools;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>区域里的一类成员：建筑、矿脉、植被、黑雾建筑、动点或天体。</summary>
    internal readonly struct RegionMember
    {
        public readonly string Kind;
        public readonly int Id;
        public readonly string Name;
        public readonly int PlanetId;
        public readonly Vector3 Local;
        /// <summary>天体/单位用世界坐标；行星表面成员为 false。</summary>
        public readonly bool WorldFrame;
        public readonly VectorLF3 World;

        private RegionMember(string kind, int id, string name, int planetId,
            Vector3 local, bool worldFrame, VectorLF3 world)
        {
            Kind = kind;
            Id = id;
            Name = name;
            PlanetId = planetId;
            Local = local;
            WorldFrame = worldFrame;
            World = world;
        }

        public static RegionMember OnPlanet(string kind, int id, string name, int planetId, Vector3 local) =>
            new RegionMember(kind, id, name, planetId, local, false, VectorLF3.zero);

        public static RegionMember InWorld(string kind, int id, string name, int planetId, VectorLF3 world) =>
            new RegionMember(kind, id, name, planetId, Vector3.zero, true, world);
    }

    /// <summary>
    /// 按区域求成员。
    ///
    /// 行星类区域直接线性扫 EntityData：建筑数在一万量级，单次扫描远低于一帧预算，
    /// 不需要并行或空间哈希（世界区域才需要用 AABB 过滤后再逐颗行星摊开）。
    /// </summary>
    internal static class RegionCatalog
    {
        public static List<RegionMember> Members(RegionTarget target, string kindFilter,
            out bool partial, out string note)
        {
            partial = false;
            note = null;
            var result = new List<RegionMember>();
            switch (target.Type)
            {
                case SpatialTypes.StarSystem:
                    AddAstroMembers(result, target, includeHive: true);
                    break;
                case SpatialTypes.Star:
                    AddAstroMembers(result, target, includeHive: false);
                    AddDysonMembers(result, target);
                    break;
                case SpatialTypes.Planet:
                    AddPlanetMembers(result, target.Planet, null);
                    break;
                case SpatialTypes.PowerGrid:
                    AddGridMembers(result, target);
                    break;
                case SpatialTypes.Factory:
                    AddFactoryMembers(result, target);
                    break;
                case SpatialTypes.DfHive:
                    AddHiveMembers(result, target, out partial);
                    break;
                case SpatialTypes.DfBase:
                    AddBaseMembers(result, target.Planet, target.SeedEntityId);
                    break;
                case SpatialTypes.WorldBox:
                    AddWorldBoxMembers(result, target.Shape, out partial, out note);
                    break;
                case SpatialTypes.PlanetPatch:
                    AddPatchMembers(result, target.Planet, target.Shape);
                    break;
                default:
                    throw new SpatialException($"类型 {target.Type} 没有成员枚举");
            }

            if (string.IsNullOrEmpty(kindFilter)) return result;
            var filtered = new List<RegionMember>(result.Count);
            foreach (var member in result)
                if (string.Equals(member.Kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(member);
            return filtered;
        }

        // ------------------------------------------------------------ 天体

        private static void AddAstroMembers(List<RegionMember> result, RegionTarget target, bool includeHive)
        {
            var star = target.Star;
            if (star == null) return;
            if (includeHive)
            {
                result.Add(RegionMember.InWorld("star", star.id, PlanetFrame.StarName(star), 0, star.uPosition));
            }
            if (star.planets != null)
            {
                foreach (var planet in star.planets)
                {
                    if (planet == null) continue;
                    result.Add(RegionMember.InWorld("planet", planet.id,
                        PlanetFrame.PlanetName(planet), planet.id, planet.uPosition));
                }
            }
            if (!includeHive) return;

            var sector = GameMain.data != null ? GameMain.data.spaceSector : null;
            var hives = sector != null ? sector.dfHivesByAstro : null;
            if (hives == null || star.index >= hives.Length) return;
            var hive = hives[star.index];
            if (hive == null) return;
            result.Add(RegionMember.InWorld("df_hive", star.id, "太空黑雾巢穴", 0, hive.hiveAstroOrbit.orbitRadius > 0
                ? HiveUPosition(hive)
                : star.uPosition));
        }

        private static VectorLF3 HiveUPosition(EnemyDFHiveSystem hive)
        {
            try
            {
                // 巢穴没有单一实体位置，取当前可见的战斗中心作为代表点。
                return hive.player_upos.sqrMagnitude > 0 ? hive.player_upos : VectorLF3.zero;
            }
            catch
            {
                return VectorLF3.zero;
            }
        }

        private static void AddDysonMembers(List<RegionMember> result, RegionTarget target)
        {
            var sphere = DysonSpheres.Get(target.StarId);
            if (sphere == null) return;
            result.Add(RegionMember.InWorld("dyson_sphere", target.StarId,
                "戴森球", 0, target.Star != null ? target.Star.uPosition : VectorLF3.zero));
        }

        // ------------------------------------------------------------ 行星表面

        private static void AddPlanetMembers(List<RegionMember> result, PlanetData planet, RegionShape shape)
        {
            if (planet == null) return;
            var factory = planet.factory;
            if (factory == null) return;

            AddEntityMembers(result, planet, shape);
            AddVeinMembers(result, planet, shape);
            AddVegeMembers(result, planet, shape);
            AddEnemyMembers(result, planet, shape);
        }

        private static void AddEntityMembers(List<RegionMember> result, PlanetData planet, RegionShape shape)
        {
            var factory = planet.factory;
            if (factory == null || factory.entityPool == null) return;
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                if (!AcceptLocal(planet, shape, entity.pos)) continue;
                result.Add(RegionMember.OnPlanet("building", id,
                    EntityToolUtil.ProtoName(entity.protoId), planet.id, entity.pos));
            }
        }

        private static void AddVeinMembers(List<RegionMember> result, PlanetData planet, RegionShape shape)
        {
            var factory = planet.factory;
            if (factory == null || factory.veinPool == null) return;
            var upper = Math.Min(factory.veinCursor, factory.veinPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var vein = factory.veinPool[id];
                if (vein.id != id) continue;
                if (!AcceptLocal(planet, shape, vein.pos)) continue;
                result.Add(RegionMember.OnPlanet("vein", id,
                    GameToolFormat.VeinName(vein.type), planet.id, vein.pos));
            }
        }

        private static void AddVegeMembers(List<RegionMember> result, PlanetData planet, RegionShape shape)
        {
            var factory = planet.factory;
            if (factory == null || factory.vegePool == null) return;
            var upper = Math.Min(factory.vegeCursor, factory.vegePool.Length);
            for (int id = 1; id < upper; id++)
            {
                var vege = factory.vegePool[id];
                if (vege.id != id) continue;
                if (!AcceptLocal(planet, shape, vege.pos)) continue;
                result.Add(RegionMember.OnPlanet("vegetation", id,
                    EntityToolUtil.ProtoName(vege.protoId), planet.id, vege.pos));
            }
        }

        private static void AddEnemyMembers(List<RegionMember> result, PlanetData planet, RegionShape shape)
        {
            var factory = planet.factory;
            if (factory == null || factory.enemyPool == null) return;
            var upper = Math.Min(factory.enemyCursor, factory.enemyPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var enemy = factory.enemyPool[id];
                if (enemy.id != id || enemy.protoId <= 0) continue;
                var local = PlanetFrame.WorldToLocal(planet, enemy.pos);
                if (!AcceptLocal(planet, shape, local)) continue;
                result.Add(RegionMember.OnPlanet("df_ground_building", id,
                    EntityToolUtil.ProtoName(enemy.protoId), planet.id, local));
            }
        }

        private static bool AcceptLocal(PlanetData planet, RegionShape shape, Vector3 local)
        {
            if (shape == null) return true;
            var (lat, lon) = GeoMath.ToLatLon(new Vec3d(local.x, local.y, local.z));
            return shape.ContainsPatch(lat, lon);
        }

        private static void AddPatchMembers(List<RegionMember> result, PlanetData planet, RegionShape shape)
        {
            AddPlanetMembers(result, planet, shape);
        }

        // ------------------------------------------------------------ 电网 / 工厂

        private static void AddGridMembers(List<RegionMember> result, RegionTarget target)
        {
            var planet = target.Planet;
            var factory = planet != null ? planet.factory : null;
            if (factory == null || factory.entityPool == null) return;
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                if (RegionResolver.NetworkOf(factory, id) != target.NetworkId) continue;
                result.Add(RegionMember.OnPlanet("building", id,
                    EntityToolUtil.ProtoName(entity.protoId), planet.id, entity.pos));
            }
        }

        private static void AddFactoryMembers(List<RegionMember> result, RegionTarget target)
        {
            var planet = target.Planet;
            var factory = planet != null ? planet.factory : null;
            if (factory == null) return;

            var seeds = FactoryClusters.Members(factory, target.SeedEntityId, target.NetworkId);
            foreach (var id in seeds)
            {
                var entity = factory.entityPool[id];
                result.Add(RegionMember.OnPlanet("building", id,
                    EntityToolUtil.ProtoName(entity.protoId), planet.id, entity.pos));
            }
        }

        // ------------------------------------------------------------ 黑雾

        private static void AddHiveMembers(List<RegionMember> result, RegionTarget target, out bool partial)
        {
            partial = false;
            var sector = GameMain.data != null ? GameMain.data.spaceSector : null;
            var star = target.Star;
            if (sector == null || star == null || sector.dfHivesByAstro == null) return;
            if (star.index >= sector.dfHivesByAstro.Length) return;
            var hive = sector.dfHivesByAstro[star.index];
            if (hive == null) return;

            var world = HiveUPosition(hive);
            result.Add(RegionMember.InWorld("df_hive", star.id, "巢穴核心", 0, world));
            if (sector.enemyPool == null) return;

            // 巢穴自带 starData，用它判断归属；enemy.astroId 与 planet/star 的 id
            // 不在同一个编号域，直接比会漏掉所有单位。
            var hiveStarIndex = hive.starData != null ? hive.starData.index : -1;
            for (int id = 1; id < sector.enemyCursor && id < sector.enemyPool.Length; id++)
            {
                var enemy = sector.enemyPool[id];
                if (enemy.id != id || enemy.protoId <= 0 || !enemy.isSpace) continue;
                if (!BelongsToStar(enemy, hiveStarIndex)) continue;
                result.Add(RegionMember.InWorld("df_space_unit", id,
                    EntityToolUtil.ProtoName(enemy.protoId), 0, enemy.pos));
            }
        }

        /// <summary>
        /// 单位是否属于某个恒星：先按 astroId 反查天体，再比恒星下标。
        /// astroId 是独立编号，不能直接和 star.id 比。
        /// </summary>
        public static bool BelongsToStar(EnemyData enemy, int starIndex)
        {
            if (starIndex < 0) return false;
            var galaxy = GameMain.data != null ? GameMain.data.galaxy : null;
            if (galaxy == null) return false;
            var astro = enemy.astroId > 0 ? galaxy.PlanetByAstroId(enemy.astroId) : null;
            if (astro?.star != null) return astro.star.index == starIndex;
            // 巢穴本体等非行星天体：回退到 originAstroId
            var origin = enemy.originAstroId > 0 ? galaxy.PlanetByAstroId(enemy.originAstroId) : null;
            return origin?.star != null && origin.star.index == starIndex;
        }

        private static void AddBaseMembers(List<RegionMember> result, PlanetData planet, int baseEntityId)
        {
            var factory = planet != null ? planet.factory : null;
            if (factory == null || factory.enemyPool == null) return;
            var upper = Math.Min(factory.enemyCursor, factory.enemyPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var enemy = factory.enemyPool[id];
                if (enemy.id != id || enemy.protoId <= 0) continue;
                if (enemy.dynamic) continue;    // 会动的单位属于动点，不是基地建筑
                if (enemy.dfGBaseId != baseEntityId) continue;
                var local = PlanetFrame.WorldToLocal(planet, enemy.pos);
                result.Add(RegionMember.OnPlanet("df_ground_building", id,
                    EntityToolUtil.ProtoName(enemy.protoId), planet.id, local));
            }
        }

        // ------------------------------------------------------------ 世界区域

        private static void AddWorldBoxMembers(List<RegionMember> result, RegionShape shape,
            out bool partial, out string note)
        {
            partial = false;
            note = null;
            var data = GameMain.data;
            if (data == null || data.galaxy == null) return;

            var loaded = new HashSet<int>();
            if (data.factories != null)
            {
                for (int i = 0; i < data.factoryCount && i < data.factories.Length; i++)
                {
                    var factory = data.factories[i];
                    if (factory != null && factory.planet != null) loaded.Add(factory.planet.id);
                }
            }

            // 天体：恒星按 uPosition 判定，行星按 uPosition 判定
            foreach (var star in data.galaxy.stars)
            {
                if (star == null) continue;
                if (shape.ContainsBox(PlanetFrame.ToVec3d(star.uPosition)))
                    result.Add(RegionMember.InWorld("star", star.id, PlanetFrame.StarName(star), 0, star.uPosition));
                if (star.planets == null) continue;
                foreach (var planet in star.planets)
                {
                    if (planet == null) continue;
                    if (shape.ContainsBox(PlanetFrame.ToVec3d(planet.uPosition)))
                        result.Add(RegionMember.InWorld("planet", planet.id,
                            PlanetFrame.PlanetName(planet), planet.id, planet.uPosition));
                }
            }

            // 建筑只在已加载行星上可得：没有工厂的行星即使落在盒子里也枚举不到它的建筑。
            var scanned = 0;
            var unscanned = 0;
            foreach (var star in data.galaxy.stars)
            {
                if (star?.planets == null) continue;
                foreach (var planet in star.planets)
                {
                    if (planet == null) continue;
                    if (planet.factory == null)
                    {
                        // 未加载：只有落在盒子里的才算潜在遗漏
                        if (shape.ContainsBox(PlanetFrame.ToVec3d(planet.uPosition))) unscanned++;
                        continue;
                    }
                    scanned++;
                    var factory = planet.factory;
                    var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
                    for (int id = 1; id < upper; id++)
                    {
                        var entity = factory.entityPool[id];
                        if (entity.id != id || entity.protoId <= 0) continue;
                        var world = PlanetFrame.LocalToWorld(planet, entity.pos);
                        if (!shape.ContainsBox(PlanetFrame.ToVec3d(world))) continue;
                        result.Add(RegionMember.InWorld("building", id,
                            EntityToolUtil.ProtoName(entity.protoId), planet.id, world));
                    }
                }
            }

            if (unscanned > 0)
            {
                partial = true;
                note = $"另有 {unscanned} 颗行星落在该区域内但工厂未加载，它们的建筑没有计入";
            }
            else if (scanned == 0)
            {
                partial = true;
                note = "没有已加载的行星，只能报告天体";
            }
        }
    }
}
