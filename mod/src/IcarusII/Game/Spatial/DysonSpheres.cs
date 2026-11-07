using System;

namespace IcarusII.Game.Spatial
{
    /// <summary>
    /// 戴森球的访问入口。
    /// GameData.dysonSpheres 按恒星 id 索引，未建球的槽位为 null。
    /// </summary>
    internal static class DysonSpheres
    {
        public static DysonSphere Get(int starId)
        {
            var data = GameMain.data;
            if (data == null || data.dysonSpheres == null) return null;
            if (starId <= 0 || starId >= data.dysonSpheres.Length) return null;
            return data.dysonSpheres[starId];
        }

        public static DysonSphere Get(StarData star) => star != null ? Get(star.id) : null;

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

            for (int i = 1; i < sphere.layerCount && i < sphere.layersIdBased.Length; i++)
            {
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

        /// <summary>当前发电性能（每 tick 的千瓦折算），与 game_get_power 同口径。</summary>
        public static (double SwarmKw, double LayerKw) GenKw(DysonSphere sphere)
        {
            if (sphere == null) return (0, 0);
            var swarm = sphere.energyGenCurrentTick_Swarm;
            var layers = sphere.energyGenCurrentTick_Layers;
            return (swarm * 60.0 / 1000.0, layers * 60.0 / 1000.0);
        }
    }
}
