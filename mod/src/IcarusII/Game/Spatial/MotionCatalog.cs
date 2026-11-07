using System;
using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using IcarusII.Game.Tools;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>解析完成的动点：类型 + 当前 tick 的位置与速度。</summary>
    internal sealed class MotionTarget
    {
        public string Type;
        public int PlanetId;
        public int StarId;
        public int CraftId;
        public int StationGid;
        public int RocketId;
        public int EnemyId;
        public int TrashId;
        public int FleetId;
        public int UnitId;
        /// <summary>运输船在所属站 workShipDatas 里的下标；-1 表示未指定。</summary>
        public int ShipIndex = -1;

        public PlanetData Planet;
        public StarData Star;

        public VectorLF3 World;
        public Vec3d Velocity;
        public bool OnPlanet;

        public string Name;

        public string Describe()
        {
            switch (Type)
            {
                case SpatialTypes.Player: return "玩家";
                case SpatialTypes.ConstructionDrone: return $"建设无人机 #{CraftId}";
                case SpatialTypes.DeliveryCourier: return $"配送运输机 #{CraftId}";
                case SpatialTypes.StationDrone: return $"物流运输机 #{CraftId}";
                case SpatialTypes.StationShip: return $"星际物流运输船 #{CraftId}";
                case SpatialTypes.DysonRocket: return $"小型运载火箭 #{RocketId}";
                case SpatialTypes.CombatDrone: return $"战斗无人机 #{UnitId}";
                case SpatialTypes.CombatFleet: return $"战舰 #{FleetId}";
                case SpatialTypes.DfSpaceUnit: return $"太空黑雾单位 #{EnemyId}";
                case SpatialTypes.DfGroundUnit: return $"地面黑雾单位 #{EnemyId}";
                case SpatialTypes.Trash: return $"垃圾块 #{TrashId}";
                default: return Type ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// 动点目录。只扫目标所在的池子，不为 list 全扫 CraftData。
    /// 各池子的 id 语义见 docs/dsp-agent.xlsx 动点页的 target 说明。
    /// </summary>
    internal static class MotionCatalog
    {
        public static MotionTarget Resolve(SpatialRequest request)
        {
            var type = request.Type ?? SpatialTypes.Player;
            if (!SpatialTypes.IsMotion(type))
                throw new SpatialException($"类型 {type} 不是动点");

            var target = new MotionTarget { Type = type };
            var id = RegionResolver.ParseId(request.TargetId);
            if (!string.IsNullOrEmpty(request.TargetId))
            {
                var parts = request.TargetId.Split(':');
                if (parts.Length > 1)
                {
                    id = RegionResolver.ParseId(parts[0]);
                    var index = RegionResolver.ParseId(parts[1]);
                    target.ShipIndex = index > 0 ? index : -1;
                }
            }

            switch (type)
            {
                case SpatialTypes.Player:
                    ResolvePlayer(target);
                    break;
                case SpatialTypes.ConstructionDrone:
                case SpatialTypes.DeliveryCourier:
                case SpatialTypes.StationDrone:
                    ResolvePlanetCraft(target, type, id);
                    break;
                case SpatialTypes.StationShip:
                    ResolveShip(target, id);
                    break;
                case SpatialTypes.DysonRocket:
                    ResolveRocket(target, id);
                    break;
                case SpatialTypes.CombatDrone:
                    ResolveCombatDrone(target, id);
                    break;
                case SpatialTypes.CombatFleet:
                    ResolveFleet(target, id);
                    break;
                case SpatialTypes.DfSpaceUnit:
                    ResolveDfSpaceUnit(target, id);
                    break;
                case SpatialTypes.DfGroundUnit:
                    ResolveDfGroundUnit(target, id);
                    break;
                case SpatialTypes.Trash:
                    ResolveTrash(target, id);
                    break;
                default:
                    throw new SpatialException($"未实现的动点类型：{type}");
            }

            return target;
        }

        // ------------------------------------------------------------ 玩家

        private static void ResolvePlayer(MotionTarget target)
        {
            var player = GameMain.mainPlayer;
            if (player == null) throw new SpatialException("当前不在游戏内");
            target.Planet = player.planetData;
            target.PlanetId = target.Planet != null ? target.Planet.id : 0;
            target.Star = target.Planet != null ? target.Planet.star : null;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.World = player.uPosition;
            var v = player.uVelocity;
            target.Velocity = new Vec3d(v.x, v.y, v.z);
            target.OnPlanet = player.planetData != null && player.planetData.factory != null
                && player.movementState == EMovementState.Walk;
            target.Name = "玩家";
        }

        // ------------------------------------------------------------ 行星内 craft 池

        private static void ResolvePlanetCraft(MotionTarget target, string type, int id)
        {
            var planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            if (planet == null) throw new SpatialException("当前不在行星上");
            var factory = planet.factory;
            if (factory == null || factory.craftPool == null)
                throw new SpatialException("该行星的 craft 池尚未加载");

            if (id <= 0) id = NearestCraft(factory, planet, type);
            if (id <= 0) throw new SpatialException($"找不到{Label(type)}");
            if (id >= factory.craftCursor || id >= factory.craftPool.Length)
                throw new SpatialException($"{Label(type)} #{id} 不存在");
            var craft = factory.craftPool[id];
            if (craft.id != id) throw new SpatialException($"{Label(type)} #{id} 已失效");

            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.CraftId = id;
            target.World = PlanetFrame.LocalToWorld(planet, craft.pos);
            target.Velocity = PlanetFrame.ToVec3d(craft.vel);
            target.OnPlanet = true;
            target.Name = Label(type);
        }

        private static int NearestCraft(PlanetFactory factory, PlanetData planet, string type)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var origin = player.transform.position;
            var best = 0;
            var bestSqr = double.MaxValue;
            var upper = Math.Min(factory.craftCursor, factory.craftPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var craft = factory.craftPool[id];
                if (craft.id != id || craft.protoId <= 0) continue;
                if (!MatchesCraft(craft, type)) continue;
                var dx = craft.pos.x - origin.x;
                var dz = craft.pos.z - origin.z;
                var sqr = dx * dx + dz * dz;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = id;
            }
            return best;
        }

        /// <summary>
        /// craft 池里混着无人机与配送机，用原型判断。
        /// 配送机（Courier）的原型 id 在 ItemProto 里带 Courier 前缀；这里退回按
        /// 速度与是否属于玩家来区分不可靠，直接用原型名字匹配。
        /// </summary>
        private static bool MatchesCraft(CraftData craft, string type)
        {
            var name = EntityToolUtil.ProtoName(craft.protoId);
            if (string.IsNullOrEmpty(name)) return false;
            var isCourier = name.IndexOf("配送", StringComparison.Ordinal) >= 0
                || name.IndexOf("Courier", StringComparison.OrdinalIgnoreCase) >= 0;
            if (type == SpatialTypes.DeliveryCourier) return isCourier;
            return !isCourier;
        }

        // ------------------------------------------------------------ 运输船

        /// <summary>
        /// 星际运输船没有全局池：每座物流站各自维护 workShipDatas。
        /// 因此用 (stationGid, shipIndex) 定位，stationGid 为 0 时取离玩家最近的站。
        /// </summary>
        private static void ResolveShip(MotionTarget target, int id)
        {
            var transport = GameMain.data != null ? GameMain.data.galacticTransport : null;
            if (transport == null || transport.stationPool == null)
                throw new SpatialException("星际物流尚未加载");

            var station = id > 0
                ? FindStation(transport, id)
                : NearestStationWithShips(transport);
            if (station == null) throw new SpatialException("找不到装载着运输船的物流站");
            var shipIndex = target.ShipIndex;
            if (shipIndex < 0) shipIndex = FirstWorkingShip(station);
            if (shipIndex < 0) throw new SpatialException("该物流站没有在飞的运输船");
            if (shipIndex >= station.workShipDatas.Length)
                throw new SpatialException($"运输船索引 #{shipIndex} 超出范围");

            var ship = station.workShipDatas[shipIndex];
            if (ship.itemId <= 0 && ship.stage <= 0)
                throw new SpatialException($"运输船 {station.gid}:{shipIndex} 已失效");

            target.Planet = GameMain.data.localPlanet;
            target.PlanetId = target.Planet != null ? target.Planet.id : 0;
            target.Star = GameMain.data.localStar;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.StationGid = station.gid;
            target.CraftId = shipIndex;
            target.World = ship.uPos;
            target.Velocity = PlanetFrame.ToVec3d(ship.uVel);
            target.OnPlanet = false;
            target.Name = "星际物流运输船";
        }

        private static StationComponent FindStation(GalacticTransport transport, int gid)
        {
            for (int i = 1; i < transport.stationCursor && i < transport.stationPool.Length; i++)
            {
                var station = transport.stationPool[i];
                if (station != null && station.gid == gid) return station;
            }
            return null;
        }

        private static StationComponent NearestStationWithShips(GalacticTransport transport)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return null;
            var origin = player.uPosition;
            StationComponent best = null;
            var bestSqr = double.MaxValue;
            for (int i = 1; i < transport.stationCursor && i < transport.stationPool.Length; i++)
            {
                var station = transport.stationPool[i];
                if (station == null || station.workShipDatas == null) continue;
                if (station.workShipCount <= 0) continue;
                var planet = GameMain.data.galaxy.PlanetById(station.planetId);
                if (planet == null) continue;
                var dx = planet.uPosition.x - origin.x;
                var dy = planet.uPosition.y - origin.y;
                var dz = planet.uPosition.z - origin.z;
                var sqr = dx * dx + dy * dy + dz * dz;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = station;
            }
            return best;
        }

        private static int FirstWorkingShip(StationComponent station)
        {
            var datas = station.workShipDatas;
            if (datas == null) return -1;
            for (int i = 0; i < datas.Length; i++)
                if (datas[i].itemId > 0 || datas[i].stage > 0) return i;
            return -1;
        }

        // ------------------------------------------------------------ 火箭

        private static void ResolveRocket(MotionTarget target, int id)
        {
            var star = GameMain.data != null ? GameMain.data.localStar : null;
            var sphere = DysonSpheres.Get(star);
            if (sphere == null) throw new SpatialException("当前恒星系还没有戴森球");
            var pool = sphere.rocketPool;
            if (pool == null) throw new SpatialException("戴森球火箭池尚未建立");

            if (id <= 0) id = NearestRocket(sphere);
            if (id <= 0) throw new SpatialException("找不到飞行中的小型运载火箭");
            if (id >= sphere.rocketCursor || id >= pool.Length)
                throw new SpatialException($"火箭 #{id} 不存在");
            var rocket = pool[id];
            if (rocket.id != id) throw new SpatialException($"火箭 #{id} 已失效");

            target.Star = star;
            target.StarId = star != null ? star.id : 0;
            target.RocketId = id;
            target.World = rocket.uPos;
            target.Velocity = PlanetFrame.ToVec3d(rocket.uVel);
            target.OnPlanet = false;
            target.Name = "小型运载火箭";
        }

        private static int NearestRocket(DysonSphere sphere)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var origin = player.uPosition;
            var best = 0;
            var bestSqr = double.MaxValue;
            var pool = sphere.rocketPool;
            for (int id = 1; id < sphere.rocketCursor && id < pool.Length; id++)
            {
                var rocket = pool[id];
                if (rocket.id != id) continue;
                var dx = rocket.uPos.x - origin.x;
                var dy = rocket.uPos.y - origin.y;
                var dz = rocket.uPos.z - origin.z;
                var sqr = dx * dx + dy * dy + dz * dz;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = id;
            }
            return best;
        }

        // ------------------------------------------------------------ 战斗

        private static void ResolveCombatDrone(MotionTarget target, int id)
        {
            var planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            var system = planet != null && planet.factory != null ? planet.factory.combatGroundSystem : null;
            if (system == null) throw new SpatialException("地面战斗系统尚未加载");

            var found = id > 0 ? FindUnit(system.units, id) : FirstUnit(system.units);
            if (found is null) throw new SpatialException("找不到地面战斗无人机");
            var unit = found.Value;

            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.UnitId = unit.id;
            var craft = FindCraft(system.factory.craftPool, unit.craftId);
            if (craft is null) throw new SpatialException("战斗无人机的 craft 已失效");
            target.World = PlanetFrame.LocalToWorld(planet, craft.Value.pos);
            target.Velocity = PlanetFrame.ToVec3d(craft.Value.vel);
            target.OnPlanet = true;
            target.Name = "战斗无人机";
        }

        private static void ResolveFleet(MotionTarget target, int id)
        {
            var data = GameMain.data;
            var planet = data != null ? data.localPlanet : null;
            var space = data != null && data.spaceSector != null ? data.spaceSector : null;
            var spaceSystem = space != null ? space.combatSpaceSystem : null;

            var found = id > 0 ? FindFleet(spaceSystem, id) : FirstFleet(spaceSystem);
            if (found is null)
            {
                var groundSystem = planet != null && planet.factory != null
                    ? planet.factory.combatGroundSystem : null;
                found = id > 0 ? FindFleet(groundSystem, id) : FirstFleet(groundSystem);
            }
            if (found is null) throw new SpatialException("找不到战舰");
            var fleet = found.Value;

            target.Planet = planet;
            target.PlanetId = planet != null ? planet.id : 0;
            target.Star = data != null ? data.localStar : null;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.FleetId = fleet.id;
            var craft = FindSpaceCraft(fleet.craftId);
            if (craft is null) throw new SpatialException("战舰的 craft 已失效");
            target.World = craft.Value.pos;
            target.Velocity = PlanetFrame.ToVec3d(craft.Value.vel);
            target.OnPlanet = false;
            target.Name = "战舰";
        }

        // ------------------------------------------------------------ 黑雾

        private static void ResolveDfSpaceUnit(MotionTarget target, int id)
        {
            var sector = GameMain.data != null ? GameMain.data.spaceSector : null;
            if (sector == null || sector.enemyPool == null)
                throw new SpatialException("宇宙黑雾尚未加载");

            var star = GameMain.data.localStar;
            if (id <= 0) id = NearestSpaceUnit(sector, star);
            if (id <= 0) throw new SpatialException("找不到太空黑雾单位");
            if (id >= sector.enemyCursor || id >= sector.enemyPool.Length)
                throw new SpatialException($"太空黑雾单位 #{id} 不存在");
            var enemy = sector.enemyPool[id];
            if (enemy.id != id || enemy.protoId <= 0)
                throw new SpatialException($"太空黑雾单位 #{id} 已失效");

            target.Star = star;
            target.StarId = star != null ? star.id : 0;
            target.EnemyId = id;
            target.World = enemy.pos;
            target.Velocity = PlanetFrame.ToVec3d(enemy.vel);
            target.OnPlanet = false;
            target.Name = EntityToolUtil.ProtoName(enemy.protoId);
        }

        private static int NearestSpaceUnit(SpaceSector sector, StarData star)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var origin = player.uPosition;
            var best = 0;
            var bestSqr = double.MaxValue;
            for (int id = 1; id < sector.enemyCursor && id < sector.enemyPool.Length; id++)
            {
                var enemy = sector.enemyPool[id];
                if (enemy.id != id || enemy.protoId <= 0 || !enemy.isSpace || !enemy.dynamic) continue;
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

        private static void ResolveDfGroundUnit(MotionTarget target, int id)
        {
            var planet = GameMain.data != null ? GameMain.data.localPlanet : null;
            var factory = planet != null ? planet.factory : null;
            if (factory == null || factory.enemyPool == null)
                throw new SpatialException("该行星的黑雾尚未加载");

            if (id <= 0) id = NearestGroundUnit(factory, planet);
            if (id <= 0) throw new SpatialException("找不到地面黑雾单位");
            if (id >= factory.enemyCursor || id >= factory.enemyPool.Length)
                throw new SpatialException($"地面黑雾单位 #{id} 不存在");
            var enemy = factory.enemyPool[id];
            if (enemy.id != id || enemy.protoId <= 0 || enemy.dfGBaseId <= 0)
                throw new SpatialException($"地面黑雾单位 #{id} 已失效");

            target.Planet = planet;
            target.PlanetId = planet.id;
            target.Star = planet.star;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.EnemyId = id;
            target.World = enemy.pos;
            target.Velocity = PlanetFrame.ToVec3d(enemy.vel);
            target.OnPlanet = true;
            target.Name = EntityToolUtil.ProtoName(enemy.protoId);
        }

        private static int NearestGroundUnit(PlanetFactory factory, PlanetData planet)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var origin = player.transform.position;
            var best = 0;
            var bestSqr = double.MaxValue;
            for (int id = 1; id < factory.enemyCursor && id < factory.enemyPool.Length; id++)
            {
                var enemy = factory.enemyPool[id];
                if (enemy.id != id || enemy.protoId <= 0 || enemy.dfGBaseId <= 0 || !enemy.dynamic) continue;
                var local = PlanetFrame.WorldToLocal(planet, enemy.pos);
                var dx = local.x - origin.x;
                var dz = local.z - origin.z;
                var sqr = dx * dx + dz * dz;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = id;
            }
            return best;
        }

        // ------------------------------------------------------------ 垃圾块

        private static void ResolveTrash(MotionTarget target, int id)
        {
            var system = GameMain.data != null ? GameMain.data.trashSystem : null;
            var container = system != null ? system.container : null;
            if (container == null || container.trashDataPool == null)
                throw new SpatialException("垃圾系统尚未加载");

            if (id <= 0) id = NearestTrash(container);
            if (id <= 0) throw new SpatialException("找不到垃圾块");
            if (id >= container.trashCursor || id >= container.trashDataPool.Length)
                throw new SpatialException($"垃圾块 #{id} 不存在");

            var trash = container.trashDataPool[id];
            if (trash.life <= 0 && trash.landPlanetId <= 0 && trash.uPos.sqrMagnitude < 1e-6)
                throw new SpatialException($"垃圾块 #{id} 已失效");

            target.TrashId = id;
            target.World = trash.uPos;
            target.Velocity = PlanetFrame.ToVec3d(trash.uVel);
            target.Planet = trash.nearPlanetId > 0
                ? GameMain.data.galaxy.PlanetById(trash.nearPlanetId) : null;
            target.PlanetId = target.Planet != null ? target.Planet.id : 0;
            target.Star = trash.nearStarId > 0 ? GameMain.data.galaxy.StarById(trash.nearStarId) : null;
            target.StarId = target.Star != null ? target.Star.id : 0;
            target.OnPlanet = false;
            target.Name = "垃圾块";
        }

        private static int NearestTrash(TrashContainer container)
        {
            var player = GameMain.mainPlayer;
            if (player == null) return 0;
            var origin = player.uPosition;
            var best = 0;
            var bestSqr = double.MaxValue;
            for (int id = 1; id < container.trashCursor && id < container.trashDataPool.Length; id++)
            {
                var trash = container.trashDataPool[id];
                if (trash.life <= 0 && trash.landPlanetId <= 0) continue;
                var dx = trash.uPos.x - origin.x;
                var dy = trash.uPos.y - origin.y;
                var dz = trash.uPos.z - origin.z;
                var sqr = dx * dx + dy * dy + dz * dz;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = id;
            }
            return best;
        }

        // ------------------------------------------------------------ 池子遍历辅助

        private static UnitComponent? FindUnit(DataPool<UnitComponent> pool, int id)
        {
            if (pool == null) return null;
            var buffer = pool.buffer;
            if (buffer == null || id <= 0 || id >= buffer.Length) return null;
            var unit = buffer[id];
            if (unit.id == id) return unit;
            return null;
        }

        private static UnitComponent? FirstUnit(DataPool<UnitComponent> pool)
        {
            if (pool == null) return null;
            var buffer = pool.buffer;
            if (buffer == null) return null;
            for (int id = 1; id < buffer.Length; id++)
                if (buffer[id].id == id) return buffer[id];
            return null;
        }

        private static FleetComponent? FindFleet(CombatSpaceSystem system, int id) =>
            system != null ? FindFleet(system.fleets, id) : null;

        private static FleetComponent? FindFleet(CombatGroundSystem system, int id) =>
            system != null ? FindFleet(system.fleets, id) : null;

        private static FleetComponent? FindFleet(DataPool<FleetComponent> pool, int id)
        {
            if (pool == null) return null;
            var buffer = pool.buffer;
            if (buffer == null || id <= 0 || id >= buffer.Length) return null;
            var fleet = buffer[id];
            if (fleet.id == id) return fleet;
            return null;
        }

        private static FleetComponent? FirstFleet(CombatSpaceSystem system) =>
            system != null ? FirstFleet(system.fleets) : null;

        private static FleetComponent? FirstFleet(CombatGroundSystem system) =>
            system != null ? FirstFleet(system.fleets) : null;

        private static FleetComponent? FirstFleet(DataPool<FleetComponent> pool)
        {
            if (pool == null) return null;
            var buffer = pool.buffer;
            if (buffer == null) return null;
            for (int id = 1; id < buffer.Length; id++)
                if (buffer[id].id == id) return buffer[id];
            return null;
        }

        private static CraftData? FindCraft(CraftData[] pool, int craftId)
        {
            if (pool == null || craftId <= 0 || craftId >= pool.Length) return null;
            var craft = pool[craftId];
            return craft.id == craftId ? craft : (CraftData?)null;
        }

        /// <summary>宇宙里的舰队/单位，craft 挂在 SpaceSector 上。</summary>
        private static CraftData? FindSpaceCraft(int craftId)
        {
            var sector = GameMain.data != null ? GameMain.data.spaceSector : null;
            return sector != null ? FindCraft(sector.craftPool, craftId) : null;
        }

        private static string Label(string type)
        {
            switch (type)
            {
                case SpatialTypes.ConstructionDrone: return "建设无人机";
                case SpatialTypes.DeliveryCourier: return "配送运输机";
                case SpatialTypes.StationDrone: return "物流运输机";
                default: return type;
            }
        }
    }
}
