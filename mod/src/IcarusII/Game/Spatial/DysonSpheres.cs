using System;
using IcarusII.Common.Json;
using IcarusII.Game.Tools;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>
    /// 戴森球的访问入口。
    ///
    /// GameData.dysonSpheres 按 **star.index** 索引，不是 star.id：游戏侧
    /// CreateDysonSphere(starData.index) 与 dysonSpheres[i].starData.index 都是这么用的。
    /// 两者在星区里通常不相等，按 id 查会命中别的槽位、或越界拿到 null，
    /// 表现就是"明明有球却报没有戴森球"。
    /// </summary>
    internal static class DysonSpheres
    {
        /// <summary>按恒星 index 取球；未建球的槽位为 null。</summary>
        public static DysonSphere GetByIndex(int starIndex)
        {
            var data = GameMain.data;
            if (data == null || data.dysonSpheres == null) return null;
            if (starIndex < 0 || starIndex >= data.dysonSpheres.Length) return null;
            return data.dysonSpheres[starIndex];
        }

        public static DysonSphere Get(StarData star) =>
            star != null ? GetByIndex(star.index) : null;

        /// <summary>
        /// 按 starId 取球。调用方手上只有 id 时用这个，内部先换成 index 再查，
        /// 不要直接拿 id 当数组下标。
        /// </summary>
        public static DysonSphere GetByStarId(int starId)
        {
            var galaxy = GameMain.data != null ? GameMain.data.galaxy : null;
            var star = galaxy != null && starId > 0 ? galaxy.StarById(starId) : null;
            return star != null ? GetByIndex(star.index) : null;
        }

        /// <summary>太阳帆总数：所有轨道的帆池占用之和。</summary>
        public static int SailCount(DysonSphere sphere)
        {
            var swarm = sphere != null ? sphere.swarm : null;
            if (swarm == null || swarm.orbitCursor <= 0) return 0;
            var total = 0;
            for (int i = 1; i < swarm.orbitCursor && i < swarm.orbits.Length; i++)
            {
                var orbit = swarm.orbits[i];
                if (orbit.enabled) total += Math.Max(0, orbit.count);
            }
            return total;
        }

        /// <summary>统计各层的节点/框架/壳面与结构点、细胞点总量。</summary>
        public static (int Nodes, int Frames, int Shells, int StructurePoints, int CellPoints)
            StructureCounts(DysonSphere sphere)
        {
            var nodes = 0;
            var frames = 0;
            var shells = 0;
            var structurePoints = 0;
            var cellPoints = 0;
            if (sphere == null || sphere.layersIdBased == null) return (0, 0, 0, 0, 0);

            // layersIdBased 按层 id 索引，层可以被删除，id 不保证连续。
            // 用 LayerExist 判断这一层是否真的存在，而不是拿 layerCount 当上界。
            for (int i = 1; i < sphere.layersIdBased.Length; i++)
            {
                if (!sphere.LayerExist(i)) continue;
                var layer = sphere.layersIdBased[i];
                if (layer == null) continue;
                if (layer.nodePool != null)
                {
                    for (int n = 1; n < layer.nodeCursor && n < layer.nodePool.Length; n++)
                    {
                        if (layer.nodePool[n].id != n) continue;
                        nodes++;
                        structurePoints += Math.Max(0, layer.nodePool[n].sp);
                    }
                }
                if (layer.framePool != null)
                {
                    for (int f = 1; f < layer.frameCursor && f < layer.framePool.Length; f++)
                    {
                        if (layer.framePool[f].id != f) continue;
                        frames++;
                    }
                }
                if (layer.shellPool != null)
                {
                    for (int s = 1; s < layer.shellCursor && s < layer.shellPool.Length; s++)
                    {
                        var shell = layer.shellPool[s];
                        if (shell == null || shell.id != s) continue;
                        shells++;
                        cellPoints += Math.Max(0, shell.cellPoint);
                    }
                }
            }
            return (nodes, frames, shells, structurePoints, cellPoints);
        }

        /// <summary>
        /// 戴森球的静态信息：各层与太阳帆轨道的半径、倾角、升交点经度、颜色与启用状态。
        ///
        /// 颜色不在 SailOrbit 上，存在 DysonSwarm.orbitColorsHSVA；
        /// 层的倾角/升交点要从 orbitRotation 反推。
        /// </summary>
        public static JsonValue Information(DysonSphere sphere)
        {
            var obj = JsonValue.NewObject();
            if (sphere == null) return obj;

            var layers = JsonValue.NewArray();
            for (int i = 1; i < sphere.layersIdBased.Length; i++)
            {
                if (!sphere.LayerExist(i)) continue;
                var layer = sphere.layersIdBased[i];
                if (layer == null) continue;
                var item = JsonValue.NewObject();
                item.Set("id", layer.id);
                item.Set("orbitRadius", GameToolFormat.Round(layer.orbitRadius, 1));
                var (inclination, longitude) = OrbitAngles(layer.orbitRotation);
                item.Set("orbitInclination", GameToolFormat.Round(inclination, 3));
                item.Set("orbitLongitude", GameToolFormat.Round(longitude, 3));
                item.Set("nodeCount", CountLiving(layer.nodePool, layer.nodeCursor));
                item.Set("frameCount", CountLiving(layer.framePool, layer.frameCursor));
                item.Set("shellCount", CountLiving(layer.shellPool, layer.shellCursor));
                layers.Add(item);
            }
            obj.Set("layers", layers);

            var orbits = JsonValue.NewArray();
            var swarm = sphere.swarm;
            if (swarm?.orbits != null)
            {
                for (int i = 1; i < swarm.orbitCursor && i < swarm.orbits.Length; i++)
                {
                    var orbit = swarm.orbits[i];
                    if (orbit.count <= 0 && !orbit.enabled) continue;
                    var item = JsonValue.NewObject();
                    item.Set("id", orbit.id);
                    item.Set("radius", GameToolFormat.Round(orbit.radius, 1));
                    var (inclination, longitude) = OrbitAngles(orbit.rotation);
                    item.Set("orbitInclination", GameToolFormat.Round(inclination, 3));
                    item.Set("orbitLongitude", GameToolFormat.Round(longitude, 3));
                    item.Set("sailCount", Math.Max(0, orbit.count));
                    item.Set("enabled", orbit.enabled);
                    if (swarm.orbitColorsHSVA != null && i < swarm.orbitColorsHSVA.Length)
                    {
                        var color = swarm.orbitColorsHSVA[i];
                        var c = JsonValue.NewObject();
                        c.Set("h", GameToolFormat.Round(color.x, 4));
                        c.Set("s", GameToolFormat.Round(color.y, 4));
                        c.Set("v", GameToolFormat.Round(color.z, 4));
                        c.Set("a", GameToolFormat.Round(color.w, 4));
                        item.Set("colorHSVA", c);
                    }
                    orbits.Add(item);
                }
            }
            obj.Set("orbits", orbits);
            obj.Set("layerCount", sphere.layerCount);
            obj.Set("grossRadius", GameToolFormat.Round(sphere.grossRadius, 1));
            obj.Set("maxLayerCount", DysonSphere.kMaxLayerCount);
            return obj;
        }

        /// <summary>从姿态四元数反推轨道倾角与升交点经度（度）。轨道法线是旋转后的 up 轴。</summary>
        private static (double Inclination, double Longitude) OrbitAngles(Quaternion rotation)
        {
            var normal = rotation * Vector3.up;
            var inclination = Math.Acos(Math.Max(-1, Math.Min(1, normal.y))) * 180.0 / Math.PI;
            // 升交点：轨道面与赤道面交线方向
            var node = Vector3.Cross(Vector3.up, normal);
            if (node.sqrMagnitude < 1e-8f)
                return (inclination, 0);
            var longitude = Math.Atan2(node.x, -node.z) * 180.0 / Math.PI;
            return (inclination, longitude);
        }

        private static int CountLiving(DysonNode[] pool, int cursor)
        {
            if (pool == null) return 0;
            var count = 0;
            for (int i = 1; i < cursor && i < pool.Length; i++)
                if (pool[i].id == i) count++;
            return count;
        }

        private static int CountLiving(DysonFrame[] pool, int cursor)
        {
            if (pool == null) return 0;
            var count = 0;
            for (int i = 1; i < cursor && i < pool.Length; i++)
                if (pool[i].id == i) count++;
            return count;
        }

        private static int CountLiving(DysonShell[] pool, int cursor)
        {
            if (pool == null) return 0;
            var count = 0;
            for (int i = 1; i < cursor && i < pool.Length; i++)
            {
                var shell = pool[i];
                if (shell != null && shell.id == i) count++;
            }
            return count;
        }

        /// <summary>当前发电性能（每 tick 的千瓦折算），与 space_region op=power 同口径。</summary>
        public static (double SwarmKw, double LayerKw) GenKw(DysonSphere sphere)
        {
            if (sphere == null) return (0, 0);
            var swarm = sphere.energyGenCurrentTick_Swarm;
            var layers = sphere.energyGenCurrentTick_Layers;
            return (swarm * 60.0 / 1000.0, layers * 60.0 / 1000.0);
        }
    }
}
