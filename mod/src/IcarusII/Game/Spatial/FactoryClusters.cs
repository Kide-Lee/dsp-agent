using System;
using System.Collections.Generic;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>
    /// 工厂 = 同一片电网下、由传送带/分拣器连通起来的建筑群。
    ///
    /// 连通性取自游戏自己的连接表（entityConnPool + 传送带的 outputId/inputId、
    /// 分拣器的 pickTarget/insertTarget），不另建邻接模型，避免与游戏判定不一致。
    /// 结果按 (planetId, entityCursor) 缓存，建筑增减后自动失效。
    /// </summary>
    internal static class FactoryClusters
    {
        private sealed class Cache
        {
            public int PlanetId;
            public int EntityCursor;
            public Dictionary<int, List<int>> BySeed;
        }

        private static Cache _cache;

        /// <summary>返回种子建筑所在的连通分量；seedEntityId 为 0 时取该电网下最大的分量。</summary>
        public static List<int> Members(PlanetFactory factory, int seedEntityId, int networkId)
        {
            if (factory == null || factory.entityPool == null) return new List<int>();
            var map = Build(factory);
            if (seedEntityId > 0 && map.TryGetValue(seedEntityId, out var direct))
                return direct;

            if (networkId <= 0)
                return map.Count > 0 ? Largest(map) : new List<int>();

            // 电网给定时，取该电网下最大的分量：电网是"工厂"的上级容器。
            var best = new List<int>();
            foreach (var cluster in map.Values)
            {
                if (cluster.Count <= best.Count) continue;
                if (!TouchesNetwork(factory, cluster, networkId)) continue;
                best = cluster;
            }
            return best.Count > 0 ? best : (map.Count > 0 ? Largest(map) : new List<int>());
        }

        private static List<int> Largest(Dictionary<int, List<int>> map)
        {
            var best = new List<int>();
            foreach (var cluster in map.Values)
                if (cluster.Count > best.Count) best = cluster;
            return best;
        }

        private static bool TouchesNetwork(PlanetFactory factory, List<int> cluster, int networkId)
        {
            foreach (var id in cluster)
                if (RegionResolver.NetworkOf(factory, id) == networkId) return true;
            return false;
        }

        /// <summary>并查集：每个非空实体先自成一类，再按连接关系合并。</summary>
        private static Dictionary<int, List<int>> Build(PlanetFactory factory)
        {
            var cursor = factory.entityCursor;
            if (_cache != null && _cache.PlanetId == factory.planet.id && _cache.EntityCursor == cursor)
                return _cache.BySeed;

            var upper = Math.Min(cursor, factory.entityPool.Length);
            var parent = new int[upper];
            for (int i = 0; i < upper; i++) parent[i] = i;

            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                LinkConnections(factory, parent, id);
                LinkBelt(factory, parent, entity);
                LinkInserter(factory, parent, entity);
            }

            var groups = new Dictionary<int, List<int>>();
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                var root = Find(parent, id);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }
                list.Add(id);
            }

            var bySeed = new Dictionary<int, List<int>>();
            foreach (var entry in groups)
                foreach (var member in entry.Value)
                    bySeed[member] = entry.Value;

            _cache = new Cache
            {
                PlanetId = factory.planet.id,
                EntityCursor = cursor,
                BySeed = bySeed,
            };
            return bySeed;
        }

        /// <summary>游戏维护的建筑间连接表：传送带进出口、分拣器两端都在这里。</summary>
        private static void LinkConnections(PlanetFactory factory, int[] parent, int entityId)
        {
            var pool = factory.entityConnPool;
            const int conns = PlanetFactory.kMaxBuildingConn;
            if (pool == null || (long)entityId * conns + conns > pool.Length) return;
            var start = entityId * conns;
            for (int i = 0; i < conns; i++)
            {
                var other = pool[start + i];
                if (other > 0 && other < parent.Length) Union(parent, entityId, other);
            }
        }

        private static void LinkBelt(PlanetFactory factory, int[] parent, EntityData entity)
        {
            if (entity.beltId <= 0) return;
            var traffic = factory.cargoTraffic;
            if (traffic == null || traffic.beltPool == null) return;
            if (entity.beltId >= traffic.beltCursor || entity.beltId >= traffic.beltPool.Length) return;
            var belt = traffic.beltPool[entity.beltId];
            UnionIfValid(parent, entity.id, belt.outputId);
            UnionIfValid(parent, entity.id, belt.backInputId);
            UnionIfValid(parent, entity.id, belt.leftInputId);
            UnionIfValid(parent, entity.id, belt.rightInputId);
        }

        private static void LinkInserter(PlanetFactory factory, int[] parent, EntityData entity)
        {
            if (entity.inserterId <= 0) return;
            var system = factory.factorySystem;
            if (system == null || system.inserterPool == null) return;
            if (entity.inserterId >= system.inserterCursor || entity.inserterId >= system.inserterPool.Length)
                return;
            var inserter = system.inserterPool[entity.inserterId];
            UnionIfValid(parent, entity.id, inserter.pickTarget);
            UnionIfValid(parent, entity.id, inserter.insertTarget);
        }

        private static void UnionIfValid(int[] parent, int a, int b)
        {
            if (b <= 0 || b >= parent.Length) return;
            if (b == a) return;
            Union(parent, a, b);
        }

        private static int Find(int[] parent, int id)
        {
            var root = id;
            while (parent[root] != root) root = parent[root];
            while (parent[id] != root)
            {
                var next = parent[id];
                parent[id] = root;
                id = next;
            }
            return root;
        }

        private static void Union(int[] parent, int a, int b)
        {
            var rootA = Find(parent, a);
            var rootB = Find(parent, b);
            if (rootA == rootB) return;
            if (rootA < rootB) parent[rootB] = rootA;
            else parent[a] = rootB;
        }

        public static void Invalidate() => _cache = null;
    }
}
