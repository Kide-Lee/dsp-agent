using System;
using System.Globalization;
using IcarusII.Common.Json;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 一层撤销栈：写工具成功后压入，`game_undo_last_write` 弹出并执行。
    /// 只保留最近一次；换档/断线时清空，避免跨存档误撤销。
    /// </summary>
    internal enum UndoResult
    {
        None,
        Undone,
        NotUndoable,
    }

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

        /// <summary>
        /// 记录一次已经发生、但无法一键撤销的写操作。
        /// 必须占用栈顶：否则 `game_undo_last_write` 会跳过它，去撤销更早的无关操作。
        /// </summary>
        public static void MarkNonUndoable(string description)
        {
            _last = new Entry { Description = description, Undo = null };
        }

        public static UndoResult TryUndo(out string description)
        {
            description = null;
            var entry = _last;
            if (entry == null) return UndoResult.None;
            _last = null;
            description = entry.Description;
            if (entry.Undo == null) return UndoResult.NotUndoable;
            try
            {
                entry.Undo();
                return UndoResult.Undone;
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

    /// <summary>
    /// 在玩家当前所在的行星放置定位标记，复用行星视图右键的 SetLocationTracking。
    /// 定位标记是 WarningSystem 的 LOCATION_TRACKING 数据：抵达附近或离开行星后由游戏自动移除。
    /// </summary>
    internal sealed class PlaceMarkerTool : WriteToolBase
    {
        public override string Name => "game_place_marker";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame) throw Fail("当前不在游戏内");
            var player = GameMain.mainPlayer;
            var planet = player?.planetData;
            if (planet == null || !planet.factoryLoaded) throw Fail("请先降落到行星表面");

            var requested = args["planetId"];
            var requestedId = requested != null ? requested.AsInt(0) : 0;
            if (requestedId > 0 && requestedId != planet.id)
                throw Fail("定位标记只能放在玩家当前所在的行星");

            var latValue = args["latitude"];
            var lonValue = args["longitude"];
            if (latValue == null || lonValue == null || latValue.IsNull || lonValue.IsNull)
                throw Fail("需要 latitude 与 longitude");
            var latitude = latValue.AsDouble(double.NaN);
            var longitude = lonValue.AsDouble(double.NaN);
            if (double.IsNaN(latitude) || double.IsNaN(longitude)) throw Fail("latitude/longitude 必须是数字");
            if (latitude < -90.0 || latitude > 90.0) throw Fail("latitude 必须在 -90..90");
            if (longitude < -180.0 || longitude > 180.0) throw Fail("longitude 必须在 -180..180");

            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (uiGame?.globemap == null) throw Fail("行星视图尚未就绪");

            var pos = GameToolFormat.PositionFromLatLon(planet, latitude, longitude, 0.0);
            if (planet.type != EPlanetType.Gas && planet.data != null)
            {
                var ground = planet.data.QueryModifiedHeight(pos);
                pos = pos.normalized * (ground + 0.2f);
            }

            uiGame.globemap.SetLocationTracking(planet.id, pos, WarningData.LOCATION_SIGNALID);
            var warningId = FindLocationWarningId();
            if (warningId <= 0) throw Fail("放置定位标记失败");

            WriteActionStack.Push("移除刚放置的定位标记", () =>
            {
                var warningSystem = GameMain.data?.warningSystem;
                if (warningSystem != null) warningSystem.RemoveWarningData(warningId);
            });

            return Ok($"已在 {planet.displayName} 放置定位标记（抵达附近或离开行星后消失）");
        }

        private static int FindLocationWarningId()
        {
            var warningSystem = GameMain.data?.warningSystem;
            if (warningSystem == null) return 0;
            for (int i = 1; i < warningSystem.warningCursor; i++)
            {
                var warning = warningSystem.warningPool[i];
                if (warning.id == i && warning.signalId == WarningData.LOCATION_SIGNALID && warning.state > 0)
                    return i;
            }
            return 0;
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
            {
                // ResumeTechQueue 可能已经改过队列/currentTech；失败时恢复现场，
                // 不能把“半切换”的研究队列留给存档。
                try
                {
                    Array.Copy(previousQueue, queue, Math.Min(previousQueue.Length, queue.Length));
                    if (previousCurrent > 0) history.ResumeTechQueue();
                    else history.PauseTechQueue();
                }
                catch { /* 游戏状态异常时保留原始错误 */ }
                throw Fail("游戏拒绝切换研究目标（前置或队列状态不满足）");
            }

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

    /// <summary>
    /// D1：把背包里的物品丢到当前行星地面。
    ///
    /// 用游戏自己的 `TakeItemFromPlayer(..., fromPackage:true, ...)` + `ThrowTrash(..., life:0)`：
    /// 前者会从背包尾部精确扣除并带回增产点数，后者生成可拾取的 Trash（`expire&lt;0` 永不过期）。
    /// **不压撤销栈**：真正撤销需要把地面上的 Trash 对象再移除，而 `ThrowTrash` 不返回 id；
    /// 设计确认卡已明确“需手动拾回”，避免复制物品。
    /// </summary>
    internal sealed class DropItemTool : WriteToolBase
    {
        public override string Name => "game_drop_item";

        /// <summary>一次最多 500：游戏 `AddTrash` 内部也按 500 堆上限循环。</summary>
        private const int MaxDrop = 500;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");
            if (GameMain.localPlanet == null)
                throw Fail("需要在行星表面才能丢弃物品");

            var player = GameMain.mainPlayer;
            if (player.sailing)
                throw Fail("飞行中不能丢弃物品，请先落地");
            if (player.package == null)
                throw Fail("背包尚未就绪");

            var proto = ResolveItem(args);
            if (proto == null) throw Fail("找不到该物品");
            if (proto.ID == ItemProto.kSoilPileId)
                throw Fail("土壤不能丢弃；请用沙盒/建造相关操作处理");

            var requested = args["count"].AsInt(0);
            if (requested <= 0) requested = 1;
            if (requested > MaxDrop)
                throw Fail("一次最多丢弃 " + MaxDrop.ToString(CultureInfo.InvariantCulture) + " 个");

            var available = player.package.GetItemCount(proto.ID);
            if (available <= 0)
                throw Fail("背包里没有 " + proto.name);

            var count = Math.Min(requested, available);
            var itemId = proto.ID;
            var itemInc = 0;
            player.TakeItemFromPlayer(ref itemId, ref count, out itemInc, true, null);
            if (itemId <= 0 || count <= 0)
                throw Fail("从背包取物失败（可能物品已被其他操作移走）");

            player.ThrowTrash(itemId, count, itemInc, 0, 0);
            WriteActionStack.MarkNonUndoable("丢弃 " + proto.name + " ×" + count.ToString(CultureInfo.InvariantCulture)
                + "（落在地面，需手动拾回）");

            var extra = JsonValue.NewObject();
            extra.Set("itemId", itemId);
            extra.Set("itemName", proto.name);
            extra.Set("count", count);
            extra.Set("requested", requested);
            extra.Set("undoable", false);
            extra.Set("undoNote", "已丢到地面，需手动拾回；不进入撤销栈");
            return Ok("已丢弃 " + proto.name + " ×" + count.ToString(CultureInfo.InvariantCulture)
                + "（可在附近拾回；不进入撤销栈）", extra);
        }

        private static ItemProto ResolveItem(JsonValue args)
        {
            int itemId = args["itemId"].AsInt(0);
            var wanted = args["itemId"].AsString(null);
            if (string.IsNullOrWhiteSpace(wanted)) wanted = args["itemName"].AsString(null);

            if (itemId > 0)
            {
                var byId = LDB.items != null ? LDB.items.Select(itemId) : null;
                if (byId != null) return byId;
            }

            if (string.IsNullOrWhiteSpace(wanted) || LDB.items == null || LDB.items.dataArray == null)
                return null;

            var text = wanted.Trim();
            foreach (var proto in LDB.items.dataArray)
            {
                if (proto == null) continue;
                if (string.Equals(proto.name, text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(proto.ID.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal))
                    return proto;
            }
            return null;
        }
    }

    internal sealed class UndoLastWriteTool : WriteToolBase
    {
        public override string Name => "game_undo_last_write";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame) throw Fail("当前不在游戏内");
            var result = WriteActionStack.TryUndo(out var description);
            if (result == UndoResult.None)
                throw Fail("没有可撤销的写操作");
            if (result == UndoResult.NotUndoable)
                throw Fail("最近一次写操作不可撤销：" + description);
            return Ok($"已撤销：{description}");
        }
    }
}
