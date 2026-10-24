using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using IcarusII.Common.Json;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 一层撤销栈：写工具成功后压入，`game_undo_last_write` 弹出并执行。
    /// 只保留最近一次；换档/断线时清空，避免跨存档误撤销。
    /// </summary>
    internal static class WriteActionStack
    {
        private sealed class Entry
        {
            public string Description;
            public Action Undo;
        }

        private static Entry _last;

        public static void Push(string description, Action undo)
        {
            _last = new Entry { Description = description, Undo = undo };
        }

        public static bool TryUndo(out string description)
        {
            description = null;
            var entry = _last;
            if (entry == null) return false;
            _last = null;
            try
            {
                entry.Undo?.Invoke();
                description = entry.Description;
                return true;
            }
            catch
            {
                _last = entry; // 撤销失败：保留，允许重试/人工处理
                throw;
            }
        }

        public static void Clear() => _last = null;
        public static bool HasUndo => _last != null;
    }

    /// <summary>一次性执行的写任务；统一在 Tick 的主线程里跑。</summary>
    internal sealed class WriteJob : IGameToolJob
    {
        private readonly Func<GameToolJobResult> _run;
        private GameToolJobResult _result;
        private bool _done;

        public WriteJob(Func<GameToolJobResult> run)
        {
            _run = run;
        }

        public bool Step(int budgetMs)
        {
            if (_done) return true;
            _done = true;
            _result = _run();
            return true;
        }

        public GameToolJobResult Finish() => _result ?? new GameToolJobResult
        {
            Data = JsonValue.NewObject(),
        };
    }

    internal abstract class WriteToolBase : IGameToolHandler
    {
        public abstract string Name { get; }
        public virtual int TimeoutMs => 8000;

        public IGameToolJob Start(JsonValue args)
        {
            args = args != null && args.IsObject ? args : JsonValue.NewObject();
            return new WriteJob(() => Execute(args));
        }

        protected abstract GameToolJobResult Execute(JsonValue args);

        protected static GameToolJobResult Ok(string display, JsonValue extra = null)
        {
            var data = extra != null && extra.IsObject ? extra : JsonValue.NewObject();
            data.Set("display", display ?? string.Empty);
            return new GameToolJobResult { Data = data };
        }

        protected static Exception Fail(string message) => new InvalidOperationException(message);
    }

    internal sealed class FocusCameraTool : WriteToolBase
    {
        public override string Name => "game_focus_camera";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");

            var raw = (args["target"].AsString(null) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw)) throw Fail("target 不能为空");

            string kind = null;
            var value = raw;
            var colon = raw.IndexOf(':');
            if (colon > 0)
            {
                var prefix = raw.Substring(0, colon).Trim().ToLowerInvariant();
                if (prefix == "planet" || prefix == "star" || prefix == "entity" || prefix == "building")
                {
                    kind = prefix == "building" ? "entity" : prefix;
                    value = raw.Substring(colon + 1).Trim();
                }
            }
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw Fail("target 必须是目标 id，或 planet:/star:/entity: 前缀");

            var galaxy = GameMain.galaxy;
            var player = GameMain.mainPlayer;
            var currentPlanet = player.planetData;
            var entityPool = currentPlanet?.factory?.entityPool;
            var entityValid = entityPool != null && id < entityPool.Length && entityPool[id].id != 0;
            var planet = galaxy != null ? galaxy.PlanetById(id) : null;
            var star = galaxy != null ? galaxy.StarById(id) : null;

            if (kind == null)
            {
                // 同一数字可能既是行星又是建筑；优先非当前行星，其次当前行星实体。
                if (planet != null && (!entityValid || currentPlanet == null || planet.id != currentPlanet.id))
                    kind = "planet";
                else if (entityValid)
                    kind = "entity";
                else if (planet != null)
                    kind = "planet";
                else if (star != null)
                    kind = "star";
            }

            if (kind == "planet")
            {
                if (planet == null) throw Fail("找不到该行星");
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                if (uiGame == null) throw Fail("UIGame 尚未就绪");
                // 当前所在行星要聚焦“行星上某处”，先进行星视图，而不是星图。
                if (currentPlanet != null && planet.id == currentPlanet.id)
                {
                    var wasPlanetView = uiGame.globemap != null && uiGame.globemap.active;
                    if (!wasPlanetView)
                    {
                        uiGame.OpenGlobemap();
                        if (uiGame.globemap == null || !uiGame.globemap.active)
                            throw Fail("无法进入行星视图");
                    }
                    uiGame.globemap.MoveToViewTargetTwoStep(player.position, 60f);
                    WriteActionStack.Push("关闭行星视图", () =>
                    {
                        if (!wasPlanetView && uiGame.globemap != null) uiGame.ShutGlobemap();
                    });
                    return Ok($"已进入行星视图并聚焦 {planet.displayName}（#{planet.id}）");
                }
                var wasOpen = uiGame.starmap != null && uiGame.starmap.active;
                uiGame.OpenStarmap();
                if (uiGame.starmap == null) throw Fail("星图尚未就绪");
                uiGame.starmap.SetViewStar(planet.star, true);
                // SetViewStar 只定位星系；再用 StarmapCamera 把视点压到行星本身。
                var camera = uiGame.starmap.screenCameraController;
                if (camera == null) throw Fail("星图相机尚未就绪");
                var radius = planet.realRadius * 0.00025 * 6.0;
                camera.SetViewTarget(planet, null, null, null, VectorLF3.zero,
                    radius, radius, true, false);
                WriteActionStack.Push("关闭星图", () =>
                {
                    if (!wasOpen && uiGame.starmap != null) uiGame.starmap._Close();
                });
                return Ok($"已聚焦行星 {planet.displayName}（#{planet.id}）");
            }

            if (kind == "star")
            {
                if (star == null) throw Fail("找不到该星系");
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                if (uiGame == null) throw Fail("UIGame 尚未就绪");
                var wasOpen = uiGame.starmap != null && uiGame.starmap.active;
                uiGame.OpenStarmap();
                if (uiGame.starmap == null) throw Fail("星图尚未就绪");
                uiGame.starmap.SetViewStar(star, true);
                WriteActionStack.Push("关闭星图", () =>
                {
                    if (!wasOpen && uiGame.starmap != null) uiGame.starmap._Close();
                });
                return Ok($"已聚焦星系 {star.displayName}（#{star.id}）");
            }

            if (kind == "entity")
            {
                if (!entityValid) throw Fail("当前行星上找不到该建筑");
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                if (uiGame == null || uiGame.globemap == null) throw Fail("UIGame 尚未就绪");

                // 先进入行星视图，再用游戏自己的 PlanetPoser 把镜头放到目标上方；
                // 不直接改 Camera.onPreRender，避免镜头钻进星球内部。
                var wasPlanetView = uiGame.globemap.active;
                if (!wasPlanetView)
                {
                    uiGame.OpenGlobemap();
                    if (!uiGame.globemap.active) throw Fail("无法进入行星视图");
                }
                var entity = entityPool[id];
                var distance = Mathf.Max(20f, entity.pos.magnitude - currentPlanet.realRadius + 20f);
                uiGame.globemap.MoveToViewTargetTwoStep(entity.pos, distance);
                WriteActionStack.Push("关闭行星视图", () =>
                {
                    if (!wasPlanetView && uiGame.globemap != null) uiGame.ShutGlobemap();
                });
                return Ok($"已进入行星视图并聚焦建筑 #{id}；退出行星视图后注视自动结束");
            }

            throw Fail("target 类型不明确；请用 planet:/star:/entity: 前缀");
        }
    }

    internal sealed class PlaceMarkerTool : WriteToolBase
    {
        public override string Name => "game_place_marker";

        private static void SetMarkerVisual(MarkerComponent marker, float height, float radius)
        {
            // MarkerComponent.height/radius 只有 getter；用 backing field 设置默认可视高度。
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var h = typeof(MarkerComponent).GetField("<height>k__BackingField", flags);
            var r = typeof(MarkerComponent).GetField("<radius>k__BackingField", flags);
            h?.SetValue(marker, height);
            r?.SetValue(marker, radius);
        }

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame) throw Fail("当前不在游戏内");
            var data = GameMain.data;
            if (data == null || data.galacticDigital == null) throw Fail("银河数字系统尚未就绪");

            int planetId = args["planetId"].AsInt(0);
            var planet = planetId > 0
                ? GameMain.galaxy?.PlanetById(planetId)
                : GameMain.mainPlayer?.planetData;
            if (planet == null) throw Fail("行星不存在或尚未加载");

            var text = args["text"].AsString(null);
            if (string.IsNullOrWhiteSpace(text)) throw Fail("text 不能为空");

            // MarkerUIRenderer.PopulateMinimal 内部固定只有 2 个 quad 缓冲：
            // word 超过 2 个字符会直接抛 IndexOutOfRangeException。
            // 游戏原生标记的“简述”也只有两字，完整内容放 name。
            var brief = text.Trim();
            if (brief.Length > 2) brief = brief.Substring(0, 2);

            var marker = new MarkerComponent();
            marker.Reset();
            marker.astroId = planet.id;
            var lat = args["latitude"];
            var lon = args["longitude"];
            if (lat != null && lon != null && !lat.IsNull && !lon.IsNull)
            {
                var altitude = args["altitude"] != null ? args["altitude"].AsDouble(0) : 0.0;
                marker.pos = GameToolFormat.PositionFromLatLon(planet, lat.AsDouble(0), lon.AsDouble(0), altitude);
            }
            else
            {
                // 兼容旧调用；新工具链应优先用经纬度。
                var position = args["position"];
                if (position == null || !position.IsObject) throw Fail("需要 latitude+longitude，或兼容的 position{x,y,z}");
                marker.pos = new Vector3(
                    (float)position["x"].AsDouble(0),
                    (float)position["y"].AsDouble(0),
                    (float)position["z"].AsDouble(0));
            }
            marker.word = brief;
            marker.name = text;
            marker.icon = args["icon"].AsInt(0);
            marker.color = 0;
            SetMarkerVisual(marker, 12.5f, 1.5f);
            marker.visibility = EMarkerVisibility.LocalPlanet;
            marker.detailLevel = EMarkerDetailLevel.BriefInfo;

            var gid = data.galacticDigital.AddMarkerComponent(marker);
            if (gid <= 0) throw Fail("新增标记失败（可能已达到上限）");
            WriteActionStack.Push("删除刚新增的标记", () =>
            {
                var digital = GameMain.data?.galacticDigital;
                if (digital != null) digital.RemoveMarkerComponent(gid);
            });
            var note = brief == text.Trim() ? string.Empty : "（地图简述显示前两字，完整名称保存在标记详情）";
            return Ok($"已在 {planet.displayName} 新建标记「{text}」{note}");
        }
    }

    internal sealed class OpenPanelTool : WriteToolBase
    {
        public override string Name => "game_open_panel";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame) throw Fail("当前不在游戏内");
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (uiGame == null) throw Fail("UIGame 尚未就绪");
            var panel = (args["panel"].AsString("") ?? string.Empty).Trim().ToLowerInvariant();
            bool wasActive;
            Action close;
            string label;
            switch (panel)
            {
                case "inventory":
                    wasActive = uiGame.inventoryWindow != null && uiGame.inventoryWindow.active;
                    uiGame.OpenPlayerInventory(); close = () => uiGame.inventoryWindow?._Close(); label = "背包";
                    break;
                case "tech":
                    wasActive = uiGame.techTree != null && uiGame.techTree.active;
                    uiGame.OpenTechTree(); close = () => uiGame.techTree?._Close(); label = "科技树";
                    break;
                case "starmap":
                    wasActive = uiGame.starmap != null && uiGame.starmap.active;
                    uiGame.OpenStarmap(); close = () => uiGame.starmap?._Close(); label = "星图";
                    break;
                case "marker":
                    wasActive = uiGame.markerWindow != null && uiGame.markerWindow.active;
                    uiGame.OpenMarkerWindow(); close = () => uiGame.markerWindow?._Close(); label = "标记";
                    break;
                case "mecha":
                    wasActive = uiGame.mechaWindow != null && uiGame.mechaWindow.active;
                    uiGame.OpenMechaWindow(); close = () => uiGame.mechaWindow?._Close(); label = "机甲";
                    break;
                case "replicator":
                    wasActive = uiGame.replicator != null && uiGame.replicator.active;
                    uiGame.OpenReplicatorWindow(); close = () => uiGame.replicator?._Close(); label = "复制器";
                    break;
                case "statistics":
                    wasActive = uiGame.statWindow != null && uiGame.statWindow.active;
                    uiGame.OpenProductionWindow(); close = () => uiGame.statWindow?._Close(); label = "统计";
                    break;
                case "blueprint":
                    wasActive = uiGame.blueprintBrowser != null && uiGame.blueprintBrowser.active;
                    uiGame.OpenBlueprintBrowser();
                    close = () => uiGame.blueprintBrowser?._Close(); label = "蓝图";
                    break;
                default:
                    throw Fail("不支持的面板；可用 inventory/tech/starmap/marker/mecha/replicator/statistics/blueprint");
            }
            if (!wasActive && close != null) WriteActionStack.Push($"关闭{label}", close);
            return Ok($"已打开{label}面板");
        }
    }

    internal sealed class SetResearchTool : WriteToolBase
    {
        public override string Name => "game_set_research";
        public override int TimeoutMs => 8000;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame) throw Fail("当前不在游戏内");
            var history = GameMain.history;
            if (history == null || history.techQueue == null) throw Fail("GameHistoryData 尚未就绪");

            int techId = args["techId"].AsInt(0);
            string wanted = args["techId"].AsString(null);
            TechProto proto = techId > 0 ? LDB.techs?.Select(techId) : null;
            if (proto == null && !string.IsNullOrWhiteSpace(wanted) && LDB.techs?.dataArray != null)
            {
                foreach (var item in LDB.techs.dataArray)
                {
                    if (item == null) continue;
                    if (string.Equals(item.name, wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.ID.ToString(CultureInfo.InvariantCulture), wanted, StringComparison.Ordinal))
                    {
                        proto = item;
                        break;
                    }
                }
            }
            if (proto == null) throw Fail("找不到该科技");

            // 已完成科技不能再作为研究目标；未解锁但前置已满足的科技才是合法目标。
            if (history.TechUnlocked(proto.ID))
                throw Fail("该科技已完成，不需要设为研究目标");
            if (!history.HasPreTechUnlocked(proto.ID))
                throw Fail("该科技的前置科技尚未解锁");

            var queue = history.techQueue;
            var previousQueue = (int[])queue.Clone();
            var previousCurrent = history.currentTech;
            if (previousCurrent == proto.ID)
                return Ok($"当前研究目标已经是 {proto.name}（#{proto.ID}）");

            int index = Array.IndexOf(queue, proto.ID);
            if (index < 0)
            {
                int slot = Array.IndexOf(queue, 0);
                if (slot < 0) throw Fail("研究队列已满，无法加入该科技");
                Array.Copy(queue, 0, queue, 1, slot);
                queue[0] = proto.ID;
            }
            else if (index > 0)
            {
                var target = queue[index];
                Array.Copy(queue, 0, queue, 1, index);
                queue[0] = target;
            }

            // ResumeTechQueue 会走游戏自己的 AlterCurrentTech 校验（前置/已完成），
            // 不再用反射直接写 currentTech。
            history.ResumeTechQueue();
            if (history.currentTech != proto.ID)
                throw Fail("游戏拒绝切换研究目标（前置或队列状态不满足）");

            WriteActionStack.Push($"恢复研究目标 {previousCurrent}", () =>
            {
                var h = GameMain.history;
                if (h == null || h.techQueue == null) return;
                Array.Copy(previousQueue, h.techQueue, Math.Min(previousQueue.Length, h.techQueue.Length));
                if (previousCurrent > 0) h.ResumeTechQueue();
                else h.PauseTechQueue();
            });
            var queueNote = history.TechInQueue(proto.ID) ? "，已在研究队列" : "";
            return Ok($"研究目标已切换为 {proto.name}（#{proto.ID}）{queueNote}");
        }
    }

    internal sealed class UndoLastWriteTool : WriteToolBase
    {
        public override string Name => "game_undo_last_write";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!WriteActionStack.TryUndo(out var description))
                throw Fail("没有可撤销的写操作");
            return Ok($"已撤销：{description}");
        }
    }
}
