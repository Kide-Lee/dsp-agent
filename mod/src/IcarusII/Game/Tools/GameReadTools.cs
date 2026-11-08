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

        /// <summary>
        /// 建筑/玩家的位置必须用“星系 + 行星 + 经纬度”表达：世界坐标会随行星
        /// 自转/公转变化，存档回放和跨工具引用都会失效。pos 是行星局部坐标。
        /// </summary>
        public static JsonValue Location(PlanetData planet, Vector3 pos)
        {
            var obj = JsonValue.NewObject();
            if (planet == null)
            {
                obj.Set("latitude", 0.0);
                obj.Set("longitude", 0.0);
                obj.Set("altitude", 0.0);
                return obj;
            }

            var star = planet.star;
            obj.Set("starId", star != null ? star.id : 0);
            obj.Set("starName", star != null ? (star.displayName ?? string.Empty) : string.Empty);
            obj.Set("planetId", planet.id);
            obj.Set("planetName", planet.displayName ?? string.Empty);

            var n = pos.sqrMagnitude > 0.000001f ? pos.normalized : Vector3.up;
            var latitude = Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg;
            var longitude = Mathf.Atan2(n.x, -n.z) * Mathf.Rad2Deg;
            obj.Set("latitude", Round(latitude, 3));
            obj.Set("longitude", Round(longitude, 3));
            obj.Set("altitude", Round(pos.magnitude - planet.realRadius, 1));

            try
            {
                Maths.GetLatitudeLongitude(pos, out var latd, out var latf,
                    out var logd, out var logf, out var north, out var south, out var west, out var east);
                obj.Set("latitudeText", latd.ToString("00", CultureInfo.InvariantCulture) + "°"
                    + latf.ToString("00", CultureInfo.InvariantCulture) + "′"
                    + (north ? "N" : (south ? "S" : "")));
                obj.Set("longitudeText", logd.ToString("000", CultureInfo.InvariantCulture) + "°"
                    + logf.ToString("00", CultureInfo.InvariantCulture) + "′"
                    + (east ? "E" : (west ? "W" : "")));
            }
            catch
            {
                // 坐标文本只是辅助；公式结果仍可用。
            }
            return obj;
        }

        /// <summary>经纬度 → 行星局部坐标；用于让输入参数也能接受经纬度。</summary>
        public static Vector3 PositionFromLatLon(PlanetData planet, double latitude, double longitude, double altitude)
        {
            var lat = latitude * Mathf.Deg2Rad;
            var lon = longitude * Mathf.Deg2Rad;
            var radius = (planet != null ? planet.realRadius : 0f) + (float)altitude;
            var x = Mathf.Cos((float)lat) * Mathf.Sin((float)lon);
            var y = Mathf.Sin((float)lat);
            var z = -Mathf.Cos((float)lat) * Mathf.Cos((float)lon);
            return new Vector3(x, y, z) * radius;
        }

        public static void SetLocation(JsonValue obj, PlanetData planet, Vector3 pos, string key = "location")
        {
            if (obj == null) return;
            obj.Set(key, Location(planet, pos));
        }

        /// <summary>
        /// 读取 center/position 参数：优先 latitude+longitude，旧 x/y/z 仅作兼容。
        /// </summary>
        public static bool TryReadPoint(PlanetData planet, JsonValue obj, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (obj == null || !obj.IsObject) return false;
            var lat = obj["latitude"];
            var lon = obj["longitude"];
            if (lat != null && lon != null && !lat.IsNull && !lon.IsNull)
            {
                var alt = obj["altitude"] != null ? obj["altitude"].AsDouble(0) : 0.0;
                pos = PositionFromLatLon(planet, lat.AsDouble(0), lon.AsDouble(0), alt);
                return true;
            }
            var x = obj["x"];
            var y = obj["y"];
            var z = obj["z"];
            if (x == null || y == null || z == null) return false;
            pos = new Vector3((float)x.AsDouble(0), (float)y.AsDouble(0), (float)z.AsDouble(0));
            return true;
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

        /// <summary>
        /// 写工具专用的严格解析：显式传了 planetId 就必须解析成功，不能静默回退到当前行星。
        /// 读工具仍用 ResolvePlanet 的兼容回退。
        /// </summary>
        public static PlanetData ResolvePlanetStrict(JsonValue args)
        {
            var raw = args["planetId"];
            if (raw != null && !raw.IsNull)
            {
                var id = raw.AsInt(0);
                if (id <= 0)
                    throw new ArgumentException("planetId 必须是正整数");
                var planet = GameMain.data != null && GameMain.data.galaxy != null
                    ? GameMain.data.galaxy.PlanetById(id)
                    : null;
                if (planet == null)
                    throw new ArgumentException("行星 #" + id.ToString(CultureInfo.InvariantCulture) + " 不存在");
                return planet;
            }
            var local = GameMain.data != null ? GameMain.data.localPlanet : null;
            if (local == null) throw new ArgumentException("当前没有载入行星");
            return local;
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
            if (player != null) GameToolFormat.SetLocation(playerObj, gameData.localPlanet, player.position);
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

}
