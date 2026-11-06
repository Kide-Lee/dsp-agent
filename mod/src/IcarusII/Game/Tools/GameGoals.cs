using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;
using IcarusII.I18n;
using UnityEngine;
using static IcarusII.Game.GoalStore;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 把 AI 生成的目标注入原生 GoalSystem，直接复用左侧 UIGoalPanel。
    ///
    /// 目标定义和完成/跳过状态都存进唯一一个 Global TodoModule 的 content（随存档保存），
    /// GoalProto 在运行时重新生成；原生 GoalSystem 的 GoalData 在读档时会丢掉尚未注入 Proto 的条目，
    /// 所以完成/跳过状态以存档里的 stage/manualIgnore 为准，已收起的目标不再注入。
    /// 触发器完全复用原生 GoalDeterminator，不做第二套 DSL。
    /// </summary>
    internal static class GameGoals
    {
        private const int CustomIdStart = 910000;
        private const string StoreTitle = "Icarus II 目标";

        private static GoalStoreState _store;
        private static string _saveKey;
        private static bool _loaded;
        private static bool _needsApply;
        private static int _nextId = CustomIdStart;

        public static void Tick()
        {
            try
            {
                if (!GameMain.isRunning || GameMain.data == null || GameMain.data.goalSystem == null)
                {
                    Reset();
                    return;
                }
                var key = GameRefs.CurrentSaveKey();
                if (string.IsNullOrEmpty(key)) return;

                if (key != _saveKey)
                {
                    _saveKey = key;
                    _loaded = false;
                    _applied = false;
                    _notes = null;
                    _nextId = CustomIdStart;
                    _sandboxInitFailedFor = null;
                }
                if (!_loaded)
                {
                    _store = LoadStore();
                    _loaded = true;
                    _needsApply = _store != null && _store.goals.Count > 0;
                    EnsureNextId();
                }
                if (_notes == null) _notes = new Dictionary<int, string>();
                if (_needsApply || !_applied)
                {
                    if (ApplyAll())
                    {
                        _needsApply = false;
                        _applied = true;
                    }
                }
                if (_applied)
                {
                    EnsureGoalLogic();
                    SyncNativeState();
                    RefreshNotes();
                }
            }
            catch (Exception ex)
            {
                if (Time.frameCount % 300 == 0)
                    Plugin.Log.LogWarning("目标系统更新失败：" + ex.Message);
            }
        }

        private static bool _applied;
        private static Dictionary<int, string> _notes;
        /// <summary>同步原生状态时复用，避免每帧分配。</summary>
        private static readonly HashSet<int> SyncGroups = new HashSet<int>();
        /// <summary>沙盒里补初始化失败过的存档；同一存档不再重试，避免每帧刷日志。</summary>
        private static string _sandboxInitFailedFor;

        /// <summary>队列里是否还有未完成的 AI 目标分组（含待办组）；目标面板据此决定是否接管“自由探索”。</summary>
        public static bool HasActiveAiGoals()
        {
            if (!_applied || _store == null) return false;
            if (!_store.HasActiveGoals && (_store.todos == null || _store.todos.Count == 0)) return false;

            var data = GameMain.data;
            var system = data != null ? data.goalSystem : null;
            if (system == null || system.goalDatas == null || system.goalQueue == null) return false;

            if (IsGroupActive(system, TodoGroupId)) return true;

            for (int i = 0; i < _store.goals.Count; i++)
            {
                var goal = _store.goals[i];
                if (goal == null || goal.IsDismissed) continue;
                if (IsGroupActive(system, goal.groupId)) return true;
            }
            return false;
        }

        /// <summary>
        /// 把原生面板里的完成/跳过写回存档；读档后 ApplyAll 只注入未收起的目标，靠这份状态阻止目标再次出现。
        /// </summary>
        private static void SyncNativeState()
        {
            if (_store == null || GameMain.data == null || GameMain.data.goalSystem == null) return;
            var system = GameMain.data.goalSystem;
            bool changed = false;
            SyncGroups.Clear();
            for (int i = 0; i < _store.goals.Count; i++)
            {
                var goal = _store.goals[i];
                if (goal == null || goal.IsDismissed) continue;
                SyncGroups.Add(goal.groupId);
                var data = system.GetGoalDataById(goal.goalId);
                if (data == null) continue;
                changed |= _store.SyncGoal(goal.goalId, (int)data.stage, data.isManualIgnore);
            }
            foreach (int groupId in SyncGroups)
            {
                var group = system.GetGoalDataById(groupId);
                if (group == null) continue;
                changed |= _store.SyncGroup(groupId, (int)group.stage, group.isManualIgnore) > 0;
            }
            if (changed) SaveStore();
        }

        private static bool IsGroupActive(GoalSystem system, int groupId)
        {
            var group = system.GetGoalDataById(groupId);
            return group != null && group.stage == EGoalStage.Enabled && system.IsGoalInQueue(groupId);
        }

        /// <summary>
        /// 沙盒存档里原生不会初始化 GoalLogic；存在 AI 目标时补一次，让判断器和面板活过来。
        /// 同一个存档失败后不再重试，避免每帧重建。
        /// </summary>
        private static void EnsureGoalLogic()
        {
            if (!GoalPanelOverride.SandboxEnabled) return;

            var scenario = GameMain.gameScenario;
            var logic = scenario != null ? scenario.goalLogic : null;
            if (logic == null || logic.isInit) return;
            if (!HasActiveAiGoals()) return;
            if (_sandboxInitFailedFor == _saveKey) return;

            try
            {
                logic.Init(GameMain.data);
            }
            catch (Exception ex)
            {
                _sandboxInitFailedFor = _saveKey;
                Plugin.Log.LogWarning("沙盒存档启用目标系统失败：" + ex.Message);
                return;
            }

            if (!logic.isInit)
            {
                _sandboxInitFailedFor = _saveKey;
                Plugin.Log.LogWarning("沙盒存档启用目标系统失败：GoalLogic.Init 未生效");
                return;
            }

            Plugin.Log.LogInfo("沙盒存档已启用目标系统：显示 AI 目标");
        }

        public static void Reset()
        {
            _saveKey = null;
            _loaded = false;
            _store = null;
            _applied = false;
            _needsApply = false;
            _notes = null;
            _sandboxInitFailedFor = null;
        }

        public static JsonValue Templates()
        {
            var arr = JsonValue.NewArray();
            var goals = LDB.goals != null ? LDB.goals.dataArray : null;
            if (goals != null)
            {
                for (int i = 0; i < goals.Length; i++)
                {
                    var p = goals[i];
                    if (p == null || p.ID <= 0 || p.ID >= CustomIdStart) continue;
                    if (string.IsNullOrEmpty(p.DeterminatorName)) continue;
                    if (string.Equals(p.DeterminatorName, "GD_Group", StringComparison.Ordinal) ||
                        p.DeterminatorName.StartsWith("GD_Group", StringComparison.Ordinal)) continue;
                    var row = JsonValue.NewObject();
                    row.Set("id", p.ID);
                    row.Set("name", p.name != null ? p.name : p.Name);
                    row.Set("determinator", p.DeterminatorName);
                    row.Set("level", (int)p.Level);
                    row.Set("parentId", p.ParentId);
                    row.Set("params", GoalStoreState.LongArray(p.DeterminatorParams));
                    arr.Add(row);
                }
            }
            var data = JsonValue.NewObject();
            data.Set("templates", arr);
            data.Set("count", arr.Count);
            return data;
        }

        public static JsonValue List()
        {
            var arr = JsonValue.NewArray();
            if (_store != null && GameMain.data != null && GameMain.data.goalSystem != null)
            {
                for (int i = 0; i < _store.goals.Count; i++)
                {
                    var g = _store.goals[i];
                    if (g == null) continue;
                    var gd = GameMain.data.goalSystem.GetGoalDataById(g.goalId);
                    var row = JsonValue.NewObject();
                    row.Set("groupId", g.groupId);
                    row.Set("goalId", g.goalId);
                    row.Set("group", g.group ?? string.Empty);
                    row.Set("title", g.title ?? string.Empty);
                    row.Set("templateId", g.templateId);
                    row.Set("current", gd != null ? gd.currentValue : 0);
                    row.Set("target", gd != null ? gd.targetValue : 0);
                    // 重开后已完成/已跳过的目标不再注入，原生 GoalData 不存在，阶段只能从存档读。
                    row.Set("stage", gd != null ? (int)gd.stage : g.stage);
                    arr.Add(row);
                }

                if (_store.todos != null)
                {
                    for (int i = 0; i < _store.todos.Count; i++)
                    {
                        var t = _store.todos[i];
                        if (t == null || string.IsNullOrEmpty(t.text)) continue;
                        var gd = GameMain.data.goalSystem.GetGoalDataById(t.id);
                        var row = JsonValue.NewObject();
                        row.Set("groupId", TodoGroupId);
                        row.Set("goalId", t.id);
                        row.Set("group", TodoGroupTitle());
                        row.Set("title", t.text);
                        row.Set("templateId", 0);
                        row.Set("current", gd != null ? gd.currentValue : 0);
                        row.Set("target", gd != null ? gd.targetValue : 0);
                        row.Set("stage", gd != null ? (int)gd.stage : 0);
                        row.Set("todo", true);
                        arr.Add(row);
                    }
                }
            }
            var data = JsonValue.NewObject();
            data.Set("goals", arr);
            data.Set("count", arr.Count);
            AppendGoalPanelState(data);
            return data;
        }

        /// <summary>新增一组目标；返回可展示的结果。args 见 GameTodoAddTool。</summary>
        public static JsonValue Add(JsonValue args)
        {
            var goalsArg = args["goals"];
            if (goalsArg == null || goalsArg.Count == 0) throw new InvalidOperationException("goals 不能为空");
            var groupTitle = args["group"].AsString(Strings.Get("goal.default.group")) ?? Strings.Get("goal.default.group");
            var added = JsonValue.NewArray();
            if (_store == null) _store = new GoalStoreState();

            int groupId = 0;
            for (int i = 0; i < _store.goals.Count; i++)
                if (_store.goals[i] != null && _store.goals[i].group == groupTitle)
                { groupId = _store.goals[i].groupId; break; }
            if (groupId <= 0) groupId = _nextId++;

            for (int i = 0; i < goalsArg.Count; i++)
            {
                var item = goalsArg[i];
                int templateId = item["templateId"].AsInt(0);
                var template = LDB.goals.Select(templateId);
                if (template == null)
                    throw new InvalidOperationException("找不到目标模板 #" + templateId.ToString(CultureInfo.InvariantCulture));
                if (IsGroupDeterminator(template.DeterminatorName))
                    throw new InvalidOperationException("模板 #" + templateId.ToString(CultureInfo.InvariantCulture) + " 是分组，不能直接添加");

                var title = item["title"].AsString(null);
                if (string.IsNullOrEmpty(title)) title = template.name;
                long[] parameters = CopyParams(template.DeterminatorParams);
                var paramsArg = item["params"];
                if (paramsArg != null && paramsArg.Count > 0)
                    parameters = GoalStoreState.ParseParams(paramsArg);
                int itemId = item["itemId"].AsInt(0);
                int count = item["count"].AsInt(0);
                if (itemId > 0 && parameters.Length >= 1) parameters[0] = itemId;
                if (count > 0 && parameters.Length >= 2) parameters[1] = count;
                if (itemId > 0 && !string.IsNullOrEmpty(template.name))
                {
                    var proto = LDB.items != null ? LDB.items.Select(itemId) : null;
                    if (proto != null && !string.IsNullOrEmpty(proto.iconTagString) &&
                        title.IndexOf('\\') < 0)
                        title = proto.iconTagString + title;
                }

                var g = new StoredGoal
                {
                    groupId = groupId,
                    goalId = _nextId++,
                    group = groupTitle,
                    title = title,
                    templateId = templateId,
                    parameters = parameters,
                };
                _store.goals.Add(g);
                var row = JsonValue.NewObject();
                row.Set("groupId", g.groupId);
                row.Set("goalId", g.goalId);
                row.Set("title", g.title);
                added.Add(row);
            }
            SaveStore();
            _needsApply = true;
            _applied = false;
            if (ApplyAll())
            {
                _needsApply = false;
                _applied = true;
            }
            // 玩家主动要求生成目标，日志里留一条便于实机追溯。
            Plugin.Log.LogInfo("游戏目标已注入 +" + added.Count.ToString(CultureInfo.InvariantCulture) +
                               "，groupId=" + groupId.ToString(CultureInfo.InvariantCulture));
            var data = JsonValue.NewObject();
            data.Set("groupId", groupId);
            data.Set("added", added);
            data.Set("count", added.Count);
            AppendGoalPanelState(data);
            return data;
        }

        public static void Remove(int groupId, int goalId)
        {
            if (_store == null) return;
            bool removed = false;
            var removedGroups = new HashSet<int>();
            for (int i = _store.goals.Count - 1; i >= 0; i--)
            {
                var g = _store.goals[i];
                if (g == null) continue;
                if ((goalId > 0 && g.goalId == goalId) || (goalId <= 0 && groupId > 0 && g.groupId == groupId))
                {
                    HideGoal(g.goalId);
                    if (goalId <= 0) removedGroups.Add(g.groupId);
                    _store.goals.RemoveAt(i);
                    removed = true;
                }
            }
            if (!removed) return;
            // 整组删除时子目标记录已经清空，但组本身没有 StoredGoal；单独从原生队列/面板隐藏。
            if (goalId <= 0 && groupId > 0) HideGoal(groupId);
            foreach (var id in removedGroups) HideGoal(id);
            // 删掉某组最后一个子目标时，组也要从原生队列收起；已收起的兄弟目标不会重新注入，不算“还在”。
            if (groupId > 0 && goalId > 0)
            {
                bool hasLeft = false;
                for (int i = 0; i < _store.goals.Count; i++)
                {
                    var left = _store.goals[i];
                    if (left != null && !left.IsDismissed && left.groupId == groupId) { hasLeft = true; break; }
                }
                if (!hasLeft) HideGoal(groupId);
            }
            SaveStore();
            _needsApply = true;
            _applied = false;
            ApplyAll();
        }

        private static bool ApplyAll()
        {
            if (_store == null) return true;
            // 已完成/已跳过的目标只保留定义，不再注入原生目标系统，否则重开游戏会再次弹出。
            var active = _store.ActiveGoals();
            if (active.Count == 0 && (_store.todos == null || _store.todos.Count == 0))
                return true;
            var data = GameMain.data;
            var system = data.goalSystem;
            var logic = GameMain.gameScenario != null ? GameMain.gameScenario.goalLogic : null;
            if (system == null || logic == null) return false;

            if (active.Count > 0)
            {
                var groups = new Dictionary<int, string>();
                for (int i = 0; i < active.Count; i++)
                {
                    var g = active[i];
                    if (g == null) continue;
                    if (!groups.ContainsKey(g.groupId)) groups[g.groupId] = g.group ?? Strings.Get("goal.default.group");
                }

                foreach (var pair in groups)
                    EnsureGroupProto(pair.Key, pair.Value);
                for (int i = 0; i < active.Count; i++)
                    EnsureChildProto(active[i]);
                RebuildGoalTable();

                foreach (var pair in groups)
                    EnsureGoalData(pair.Key, true);
                for (int i = 0; i < active.Count; i++)
                    EnsureGoalData(active[i].goalId, false);

                EnsureQueue(groups);
                if (logic.isInit)
                {
                    foreach (var pair in groups)
                        EnsureDeterminator(pair.Key);
                    for (int i = 0; i < active.Count; i++)
                        EnsureDeterminator(active[i].goalId);
                    foreach (var pair in groups)
                        EnsureDeterminator(pair.Key);
                }
            }

            EnsureTodoGroup();
            return true;
        }

        private static void EnsureGroupProto(int id, string title)
        {
            var existing = LDB.goals.Select(id);
            if (existing != null)
            {
                existing.Name = title;
                existing.name = title;
                // AI 目标由玩家主动要求生成，不受原生“目标等级”筛选限制。
                // Off 是最低级，IsLevelPass(Off) 对 Off/Key/Full 全部成立。
                existing.Level = EGoalLevel.Off;
                existing.ParentId = 0;
                existing.DeterminatorName = "GD_Group";
                return;
            }
            var p = new GoalProto
            {
                ID = id,
                SID = "ICARUSII_GOAL_GROUP_" + id.ToString(CultureInfo.InvariantCulture),
                Name = title,
                Level = EGoalLevel.Off,
                ParentId = 0,
                DeterminatorName = "GD_Group",
                DeterminatorParams = new long[0],
                Childs = new int[0],
            };
            AddProto(p);
        }

        private static void EnsureChildProto(StoredGoal g)
        {
            if (g == null) return;
            var template = LDB.goals.Select(g.templateId);
            if (template == null) return;
            var p = LDB.goals.Select(g.goalId);
            if (p == null)
            {
                p = CloneProto(template, g.goalId);
                AddProto(p);
            }
            p.Name = g.title;
            p.name = g.title;
            p.ParentId = g.groupId;
            p.Level = EGoalLevel.Off;
            p.Childs = new int[0];
            p.DeterminatorParams = CopyParams(g.parameters);
        }

        private static GoalProto CloneProto(GoalProto src, int id)
        {
            return new GoalProto
            {
                ID = id,
                SID = "ICARUSII_GOAL_" + id.ToString(CultureInfo.InvariantCulture),
                Name = src.Name,
                TooltipText = src.TooltipText,
                Level = EGoalLevel.Off,
                QueueJumpPriority = src.QueueJumpPriority,
                ParentId = 0,
                NeedCombatMode = src.NeedCombatMode,
                DisplayParams = src.DisplayParams != null ? (double[])src.DisplayParams.Clone() : null,
                DeterminatorName = src.DeterminatorName,
                DeterminatorParams = CopyParams(src.DeterminatorParams),
                IgnoreParamsLevel1 = CopyParams(src.IgnoreParamsLevel1),
                IgnoreParamsLevel2 = CopyParams(src.IgnoreParamsLevel2),
                PatchIgnoreParams = CopyParams(src.PatchIgnoreParams),
                PatchCompleteParams = CopyParams(src.PatchCompleteParams),
                OnLoadIgnoreParams = CopyParams(src.OnLoadIgnoreParams),
                EnterQueueIgnoreParams = CopyParams(src.EnterQueueIgnoreParams),
                Childs = new int[0],
            };
        }

        private static void AddProto(GoalProto p)
        {
            var old = LDB.goals.dataArray;
            var next = new GoalProto[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = p;
            LDB.goals.dataArray = next;
        }

        private static void RebuildGoalTable()
        {
            try { LDB.goals.OnAfterDeserialize(); } catch { }
        }

        private static void EnsureGoalData(int id, bool isGroup)
        {
            var system = GameMain.data.goalSystem;
            var data = system.GetGoalDataById(id);
            if (data == null)
            {
                data = new GoalData(id);
                // 新注入的 GoalData 必须走一次 OnPatch 判定，之后由 EnsureDeterminator 调 OnLoad。
                data.isPatched = false;
                system.goalDatas[id] = data;
            }
            data.isGroup = isGroup;
            if (isGroup && (data.stage == EGoalStage.Completed || data.stage == EGoalStage.Ignored))
            {
                // 往已经收起的组里追加新目标时，组必须重新激活；
                // 旧判断器可能已经 isFinished，必须丢掉重建，否则 DetermineByChild 不会再跑。
                data.stage = EGoalStage.Enabled;
                data.isManualIgnore = false;
                data.SetProgress(0, 0);
                DropDeterminator(id);
            }
            if (data.stage == EGoalStage.Disabled) data.stage = EGoalStage.Enabled;
            if (data.displayingState == null || data.displayingState.Length <= (int)EGoalLevel.Full)
                data.displayingState = new bool[(int)EGoalLevel.Full + 1];
            for (int i = 0; i < data.displayingState.Length; i++) data.displayingState[i] = true;
        }

        private static void EnsureQueue(Dictionary<int, string> groups)
        {
            var system = GameMain.data.goalSystem;
            if (system.goalQueue == null) system.goalQueue = new int[16];
            foreach (var pair in groups)
            {
                int existing = -1;
                for (int i = 0; i < system.queueCursor; i++)
                    if (system.goalQueue[i] == pair.Key) { existing = i; break; }

                // 原生 UIGoalPanel 最多只显示 kMaxShowEntry(2) 个分组，且按队列顺序取。
                // AI 目标是玩家当前要求的东西，必须排到队首，否则会被前面两个原生分组挤掉。
                if (existing == 0) continue;
                if (existing < 0)
                {
                    if (system.queueCursor >= system.goalQueue.Length)
                    {
                        var next = new int[system.goalQueue.Length * 2];
                        Array.Copy(system.goalQueue, next, system.goalQueue.Length);
                        system.goalQueue = next;
                    }
                    for (int i = system.queueCursor; i > 0; i--)
                        system.goalQueue[i] = system.goalQueue[i - 1];
                    system.goalQueue[0] = pair.Key;
                    system.queueCursor++;
                }
                else
                {
                    for (int i = existing; i > 0; i--)
                        system.goalQueue[i] = system.goalQueue[i - 1];
                    system.goalQueue[0] = pair.Key;
                }
            }
        }

        private static void EnsureDeterminator(int id)
        {
            var logic = GameMain.gameScenario != null ? GameMain.gameScenario.goalLogic : null;
            if (logic == null || !logic.isInit) return;

            if (logic.determinatorIndices != null && logic.determinatorIndices.TryGetValue(id, out int existingIndex))
            {
                var existing = logic.determinators != null &&
                               existingIndex >= 0 && existingIndex < logic.determinators.Length
                    ? logic.determinators[existingIndex] : null;
                if (existing != null && existing.inited)
                {
                    // 原生 OnGoalStageChanged 会在我们设 stage=Enabled 时同步建好判断器，
                    // 但它只调 Init，不会补 OnPatch/OnLoad —— GD_UnlockTech 这类判断器的
                    // targetValue 只在 DetermineOnLoad 里设置，漏掉就会 0/1 永远不显示。
                    DetermineInitial(existing);
                    return;
                }
            }

            var proto = LDB.goals.Select(id);
            if (proto == null || string.IsNullOrEmpty(proto.DeterminatorName)) return;
            var det = GoalDeterminator.asm.CreateInstance(proto.DeterminatorName) as GoalDeterminator;
            if (det == null) return;
            det.Init(GameMain.data, id, proto.DeterminatorParams);
            if (!det.inited) return;
            if (logic.determinators == null) logic.determinators = new GoalDeterminator[16];
            if (logic.determinatorIndices == null) logic.determinatorIndices = new Dictionary<int, int>();
            if (logic.determinatorCursor >= logic.determinators.Length)
            {
                var next = new GoalDeterminator[logic.determinators.Length * 2];
                Array.Copy(logic.determinators, next, logic.determinators.Length);
                logic.determinators = next;
            }
            int index = logic.determinatorCursor++;
            logic.determinators[index] = det;
            logic.determinatorIndices[id] = index;
            DetermineInitial(det);
        }

        private static void DropDeterminator(int id)
        {
            var logic = GameMain.gameScenario != null ? GameMain.gameScenario.goalLogic : null;
            if (logic == null || logic.determinatorIndices == null || logic.determinators == null) return;
            if (!logic.determinatorIndices.TryGetValue(id, out int index)) return;
            if (index >= 0 && index < logic.determinators.Length && logic.determinators[index] != null)
            {
                logic.determinators[index].Free();
                logic.determinators[index] = null;
            }
            logic.determinatorIndices.Remove(id);
        }

        private static void DetermineInitial(GoalDeterminator det)
        {
            if (det == null || !det.inited) return;
            try
            {
                var system = GameMain.data != null ? GameMain.data.goalSystem : null;
                var data = system != null ? system.GetGoalDataById(det.protoId) : null;
                if (data != null && !data.isPatched)
                {
                    det.DetermineOnPatch();
                    data.isPatched = true;
                }
                det.DetermineOnLoad();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("目标判断器初始化失败 #" + det.protoId.ToString(CultureInfo.InvariantCulture) +
                                      "：" + ex.Message);
            }
        }

        /// <summary>
        /// 玩家把“目标建议”选成“自由探索”(EGoalLevel.Off) 且模组没有接管显示时，
        /// 原生 UIGoalPanel 不会展示任何目标；此时返回标记，由模型提醒玩家打开目标显示。
        /// </summary>
        private static void AppendGoalPanelState(JsonValue data)
        {
            var desc = GameMain.data != null ? GameMain.data.gameDesc : null;
            if (desc == null || desc.goalLevel != EGoalLevel.Off) return;
            if (GoalPanelOverride.PanelShowsInOffMode()) return;
            data.Set("goalPanelOff", true);
        }

        /// <summary>
        /// 轮内待办：整份列表替换原生面板第一组“AI 待办”。
        /// 条目按文本复用目标 id，已完成的条目保持 Completed，玩家跳过的条目保持 Ignored。
        /// </summary>
        public static JsonValue WriteTodo(JsonValue args)
        {
            if (GameMain.data == null || GameMain.data.goalSystem == null)
                throw new InvalidOperationException("目标系统尚未就绪");

            var items = args["todos"];
            int count = items != null ? items.Count : 0;
            if (count > TodoItemLimit)
                throw new InvalidOperationException($"待办最多 {TodoItemLimit.ToString(CultureInfo.InvariantCulture)} 条");

            if (_store == null) _store = new GoalStoreState();
            var old = _store.todos ?? new List<StoredTodo>();
            var byText = new Dictionary<string, StoredTodo>(StringComparer.Ordinal);
            for (int i = 0; i < old.Count; i++)
            {
                var t = old[i];
                if (t == null || string.IsNullOrEmpty(t.text)) continue;
                if (!byText.ContainsKey(t.text)) byText[t.text] = t;
            }

            var next = new List<StoredTodo>(count);
            var used = new HashSet<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                var text = (item["content"].AsString(null) ?? string.Empty).Trim();
                if (text.Length == 0) throw new InvalidOperationException("待办内容不能为空");
                if (text.Length > 200) throw new InvalidOperationException("待办内容最多 200 字");
                if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
                    throw new InvalidOperationException("待办内容不能换行");
                if (!seen.Add(text))
                    throw new InvalidOperationException("待办内容不能重复：" + text);

                int id = 0;
                if (byText.TryGetValue(text, out var existing) && existing.id > 0 && !used.Contains(existing.id))
                    id = existing.id;
                if (id <= 0) id = NextTodoId(used);
                used.Add(id);

                next.Add(new StoredTodo
                {
                    id = id,
                    text = text,
                    status = NormalizeTodoStatus(item["status"].AsString(null)),
                });
            }

            _store.todos = next;
            EnsureTodoGroup();
            // 待办只写原生面板，不写存档：回合结束/换档/退出即消失。
            _needsApply = false;
            _applied = true;
            return TodoResult();
        }

        private static int NextTodoId(HashSet<int> used)
        {
            for (int i = 0; i < TodoItemLimit; i++)
            {
                int id = TodoItemIdStart + i;
                if (!used.Contains(id)) return id;
            }
            throw new InvalidOperationException("待办数量超过上限");
        }

        /// <summary>把 store 里的待办同步成原生组与子目标；空列表时隐藏整组。</summary>
        private static void EnsureTodoGroup()
        {
            if (_store == null || GameMain.data == null || GameMain.data.goalSystem == null) return;
            var system = GameMain.data.goalSystem;
            if (_store.todos == null) _store.todos = new List<StoredTodo>();

            if (_store.todos.Count == 0)
            {
                var existing = system.GetGoalDataById(TodoGroupId);
                if (existing != null)
                {
                    existing.stage = EGoalStage.Disabled;
                    existing.SetProgress(0, 0);
                }
                RemoveGroupFromQueue(TodoGroupId);
                return;
            }

            var groupProto = EnsureTodoGroupProto();
            var live = new List<int>(_store.todos.Count);
            for (int i = 0; i < _store.todos.Count; i++)
            {
                var t = _store.todos[i];
                if (t == null || string.IsNullOrEmpty(t.text)) continue;
                EnsureTodoItemProto(t);
                live.Add(t.id);
            }
            groupProto.Childs = live.ToArray();
            RebuildGoalTable();

            EnsureGoalData(TodoGroupId, true);
            int target = 0;
            int done = 0;
            for (int i = 0; i < _store.todos.Count; i++)
            {
                var t = _store.todos[i];
                if (t == null || string.IsNullOrEmpty(t.text)) continue;
                EnsureGoalData(t.id, false);
                var data = system.GetGoalDataById(t.id);
                if (data == null) continue;

                bool completed = string.Equals(t.status, TodoStatusCompleted, StringComparison.Ordinal);
                if (completed)
                {
                    data.isManualIgnore = false;
                    data.SetProgress(1, 1);
                    data.stage = EGoalStage.Completed;
                    target++;
                    done++;
                    continue;
                }
                if (data.stage == EGoalStage.Ignored && data.isManualIgnore)
                {
                    data.SetProgress(0, 1);
                    continue;
                }
                data.isManualIgnore = false;
                data.SetProgress(0, 1);
                data.stage = EGoalStage.Enabled;
                target++;
            }

            for (int i = 0; i < TodoItemLimit; i++)
            {
                int id = TodoItemIdStart + i;
                if (live.Contains(id)) continue;
                var stale = system.GetGoalDataById(id);
                if (stale == null) continue;
                stale.stage = EGoalStage.Disabled;
                stale.SetProgress(0, 0);
            }

            var group = system.GetGoalDataById(TodoGroupId);
            if (group == null) return;
            group.SetProgress(done, target);
            if (target == 0) group.stage = EGoalStage.Ignored;
            else group.stage = done >= target ? EGoalStage.Completed : EGoalStage.Enabled;

            if (group.stage != EGoalStage.Enabled)
            {
                RemoveGroupFromQueue(TodoGroupId);
                return;
            }

            EnsureQueue(new Dictionary<int, string> { { TodoGroupId, TodoGroupTitle() } });
            var logic = GameMain.gameScenario != null ? GameMain.gameScenario.goalLogic : null;
            if (logic != null && logic.isInit) EnsureDeterminator(TodoGroupId);
        }

        private static GoalProto EnsureTodoGroupProto()
        {
            var p = LDB.goals.Select(TodoGroupId);
            if (p == null)
            {
                p = new GoalProto
                {
                    ID = TodoGroupId,
                    SID = "ICARUSII_TODO_GROUP",
                    Level = EGoalLevel.Off,
                    ParentId = 0,
                    DeterminatorName = "GD_Group",
                    DeterminatorParams = new long[0],
                    DisplayParams = new double[0],
                    Childs = new int[0],
                };
                AddProto(p);
            }
            p.Name = TodoGroupTitle();
            p.name = TodoGroupTitle();
            p.Level = EGoalLevel.Off;
            p.ParentId = 0;
            p.DeterminatorName = "GD_Group";
            p.DeterminatorParams = new long[0];
            p.NeedCombatMode = 0;
            return p;
        }

        private static void EnsureTodoItemProto(StoredTodo t)
        {
            var p = LDB.goals.Select(t.id);
            if (p == null)
            {
                p = new GoalProto
                {
                    ID = t.id,
                    SID = "ICARUSII_TODO_" + t.id.ToString(CultureInfo.InvariantCulture),
                    Level = EGoalLevel.Off,
                    ParentId = TodoGroupId,
                    DeterminatorName = string.Empty,
                    DeterminatorParams = new long[0],
                    DisplayParams = new double[0],
                    Childs = new int[0],
                };
                AddProto(p);
            }
            p.Name = t.text;
            p.name = t.text;
            p.Level = EGoalLevel.Off;
            p.ParentId = TodoGroupId;
            p.DeterminatorName = string.Empty;
            p.DeterminatorParams = new long[0];
            p.Childs = new int[0];
        }

        private static string TodoGroupTitle() => Strings.Get("goal.todo.group");

        private static void RemoveGroupFromQueue(int id)
        {
            var system = GameMain.data != null ? GameMain.data.goalSystem : null;
            if (system == null || system.goalQueue == null) return;
            for (int i = 0; i < system.queueCursor; i++)
            {
                if (system.goalQueue[i] != id) continue;
                for (int j = i; j + 1 < system.queueCursor; j++)
                    system.goalQueue[j] = system.goalQueue[j + 1];
                system.goalQueue[system.queueCursor - 1] = 0;
                system.queueCursor--;
                return;
            }
        }

        private static JsonValue TodoResult()
        {
            var arr = JsonValue.NewArray();
            int pending = 0;
            int inProgress = 0;
            int completed = 0;
            int skipped = 0;
            var system = GameMain.data != null ? GameMain.data.goalSystem : null;
            if (_store != null && _store.todos != null)
            {
                for (int i = 0; i < _store.todos.Count; i++)
                {
                    var t = _store.todos[i];
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    var data = system != null ? system.GetGoalDataById(t.id) : null;
                    bool isSkipped = data != null && data.stage == EGoalStage.Ignored && data.isManualIgnore;
                    string status = isSkipped ? TodoStatusSkipped : t.status;
                    if (isSkipped) skipped++;
                    else if (string.Equals(status, TodoStatusCompleted, StringComparison.Ordinal)) completed++;
                    else if (string.Equals(status, TodoStatusInProgress, StringComparison.Ordinal)) inProgress++;
                    else pending++;

                    var row = JsonValue.NewObject();
                    row.Set("id", t.id);
                    row.Set("content", t.text);
                    row.Set("status", status);
                    arr.Add(row);
                }
            }

            var counts = JsonValue.NewObject();
            counts.Set("pending", pending);
            counts.Set("inProgress", inProgress);
            counts.Set("completed", completed);
            counts.Set("skipped", skipped);

            var result = JsonValue.NewObject();
            result.Set("group", TodoGroupTitle());
            result.Set("todos", arr);
            result.Set("count", arr.Count);
            result.Set("counts", counts);
            AppendGoalPanelState(result);
            return result;
        }

        private static void HideGoal(int id)
        {
            if (GameMain.data == null || GameMain.data.goalSystem == null) return;
            var system = GameMain.data.goalSystem;
            var data = system.GetGoalDataById(id);
            if (data != null)
            {
                data.stage = EGoalStage.Ignored;
                data.SetProgress(0, 0);
                if (data.displayingState != null)
                    for (int i = 0; i < data.displayingState.Length; i++) data.displayingState[i] = false;
            }
            if (system.goalQueue == null) return;
            for (int i = 0; i < system.queueCursor; i++)
            {
                if (system.goalQueue[i] != id) continue;
                for (int j = i; j + 1 < system.queueCursor; j++) system.goalQueue[j] = system.goalQueue[j + 1];
                system.goalQueue[system.queueCursor - 1] = 0;
                system.queueCursor--;
                break;
            }
        }

        private static void RefreshNotes()
        {
            if (_store == null || _notes == null) return;
            for (int i = 0; i < _store.goals.Count; i++)
            {
                var g = _store.goals[i];
                if (g == null) continue;
                var gd = GameMain.data.goalSystem.GetGoalDataById(g.goalId);
                if (gd != null) _notes[g.goalId] = g.title;
            }
        }

        private static GoalStoreState LoadStore()
        {
            var todo = FindStoreTodo();
            return GoalStoreState.Parse(todo != null ? todo.content : null);
        }

        private static void SaveStore()
        {
            var todo = FindStoreTodo();
            if (todo == null)
            {
                var digital = GameMain.data != null ? GameMain.data.galacticDigital : null;
                if (digital == null) return;
                todo = digital.AddTodoModule(ETodoModuleOwnerType.Global, 0);
                todo.title = StoreTitle;
            }
            todo.content = _store != null ? _store.ToContent() : new GoalStoreState().ToContent();
        }

        private static TodoModule FindStoreTodo()
        {
            var digital = GameMain.data != null ? GameMain.data.galacticDigital : null;
            if (digital == null || digital.todos == null || digital.todos.buffer == null) return null;
            for (int i = 1; i < digital.todos.cursor; i++)
            {
                var todo = digital.todos.buffer[i];
                if (todo != null && todo.ownerType == ETodoModuleOwnerType.Global &&
                    todo.content != null && todo.content.StartsWith(GoalStore.Prefix, StringComparison.Ordinal))
                    return todo;
            }
            return null;
        }

        private static void EnsureNextId()
        {
            if (_store == null) return;
            for (int i = 0; i < _store.goals.Count; i++)
            {
                var g = _store.goals[i];
                if (g == null) continue;
                if (g.groupId >= _nextId) _nextId = g.groupId + 1;
                if (g.goalId >= _nextId) _nextId = g.goalId + 1;
            }
        }

        private static bool IsGroupDeterminator(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                (name == "GD_Group" || name.StartsWith("GD_Group", StringComparison.Ordinal));
        }

        private static long[] CopyParams(long[] src)
        {
            return src != null ? (long[])src.Clone() : new long[0];
        }
    }
}
