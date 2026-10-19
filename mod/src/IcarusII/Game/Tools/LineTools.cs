using System;
using System.Collections.Generic;
using IcarusII.Common.Json;
using IcarusII.Game;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 从任意一栋建筑出发，沿传送带/分拣器把相连的设备串出来（产线拓扑）。
    ///
    /// 只做**实体连接**：传送带的 output/back/left/right 与分拣器的 pick→insert。
    /// 不推断配方流向，也不跨越星际物流（那属于 game_get_logistics）。
    /// </summary>
    internal sealed class TraceLineTool : IGameToolHandler
    {
        public string Name => "game_trace_line";
        public int TimeoutMs => 15000;

        private sealed class Edge
        {
            public int From;
            public int To;
            public string Kind;
        }

        public IGameToolJob Start(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");
            return new OneShotJob(() => Build(planet, args));
        }

        private static GameToolJobResult Build(PlanetData planet, JsonValue args)
        {
            var data = JsonValue.NewObject();
            var factory = planet.factory;
            if (factory == null || factory.entityPool == null)
            {
                data.Set("available", false);
                data.Set("display", "该行星的工厂尚未加载");
                return new GameToolJobResult { Data = data };
            }

            var seed = args["entityId"].AsInt(0);
            if (!EntityToolUtil.HasEntity(factory, seed))
            {
                data.Set("available", false);
                data.Set("display", "seed entityId 无效");
                return new GameToolJobResult { Data = data };
            }

            var depthLimit = Math.Max(1, Math.Min(60, args["depth"].AsInt(20)));
            var maxNodes = Math.Max(2, Math.Min(300, args["maxNodes"].AsInt(200)));

            var adjacency = new Dictionary<int, List<Edge>>();
            var system = factory.factorySystem;
            if (system != null && system.inserterPool != null)
            {
                var upper = Math.Min(system.inserterCursor, system.inserterPool.Length);
                for (int i = 1; i < upper; i++)
                {
                    var ins = system.inserterPool[i];
                    if (ins.id == 0) continue;
                    Link(adjacency, ins.pickTarget, ins.insertTarget, "inserter");
                }
            }
            var traffic = factory.cargoTraffic;
            if (traffic != null && traffic.beltPool != null)
            {
                var upper = Math.Min(traffic.beltCursor, traffic.beltPool.Length);
                for (int i = 1; i < upper; i++)
                {
                    var belt = traffic.beltPool[i];
                    if (belt.id == 0) continue;
                    var entityId = belt.entityId;
                    Link(adjacency, entityId, belt.outputId, "belt");
                    Link(adjacency, entityId, belt.backInputId, "belt");
                    Link(adjacency, entityId, belt.leftInputId, "belt");
                    Link(adjacency, entityId, belt.rightInputId, "belt");
                }
            }

            var visited = new HashSet<int> { seed };
            var queue = new Queue<int>();
            var depths = new Dictionary<int, int> { { seed, 0 } };
            queue.Enqueue(seed);
            var edges = new List<Edge>();
            var truncated = false;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var currentDepth = depths[current];
                if (currentDepth >= depthLimit) continue;

                List<Edge> list;
                if (!adjacency.TryGetValue(current, out list)) continue;
                foreach (var edge in list)
                {
                    if (edge.From == current)
                    {
                        AddEdge(edges, edge);
                        if (visited.Add(edge.To))
                        {
                            depths[edge.To] = currentDepth + 1;
                            if (visited.Count >= maxNodes) { truncated = true; break; }
                            queue.Enqueue(edge.To);
                        }
                    }
                    else if (edge.To == current)
                    {
                        // 反向遍历：保留原方向，但沿反向继续找上游。
                        AddEdge(edges, edge);
                        if (visited.Add(edge.From))
                        {
                            depths[edge.From] = currentDepth + 1;
                            if (visited.Count >= maxNodes) { truncated = true; break; }
                            queue.Enqueue(edge.From);
                        }
                    }
                }
                if (truncated) break;
            }

            var nodes = JsonValue.NewArray();
            foreach (var id in visited)
            {
                var entity = factory.entityPool[id];
                var row = JsonValue.NewObject();
                row.Set("id", id);
                row.Set("protoId", entity.protoId);
                row.Set("protoName", EntityToolUtil.ProtoName(entity.protoId));
                row.Set("category", EntityToolUtil.Category(entity));
                row.Set("depth", depths[id]);
                row.Set("pos", EntityToolUtil.Vec3(entity.pos));
                nodes.Add(row);
            }

            var edgeRows = JsonValue.NewArray();
            var seen = new HashSet<string>();
            foreach (var edge in edges)
            {
                var key = edge.From.ToString() + ">" + edge.To.ToString() + ":" + edge.Kind;
                if (!seen.Add(key)) continue;
                var row = JsonValue.NewObject();
                row.Set("from", edge.From);
                row.Set("to", edge.To);
                row.Set("kind", edge.Kind);
                edgeRows.Add(row);
            }

            data.Set("available", true);
            data.Set("planetId", planet.id);
            data.Set("seed", seed);
            data.Set("depthLimit", depthLimit);
            data.Set("nodeCount", visited.Count);
            data.Set("edgeCount", edgeRows.Count);
            data.Set("truncated", truncated);
            data.Set("nodes", nodes);
            data.Set("edges", edgeRows);
            data.Set("display", "产线拓扑 · 节点 " + visited.Count + " · 连接 " + edgeRows.Count
                + (truncated ? " · 已截断" : ""));
            return new GameToolJobResult { Data = data };
        }

        private static void Link(Dictionary<int, List<Edge>> map, int from, int to, string kind)
        {
            if (from <= 0 || to <= 0 || from == to) return;
            List<Edge> list;
            if (!map.TryGetValue(from, out list))
            {
                list = new List<Edge>();
                map[from] = list;
            }
            list.Add(new Edge { From = from, To = to, Kind = kind });
        }

        private static void AddEdge(List<Edge> edges, Edge edge)
        {
            foreach (var existing in edges)
                if (existing.From == edge.From && existing.To == edge.To && existing.Kind == edge.Kind) return;
            edges.Add(edge);
        }
    }
}
