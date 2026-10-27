using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 把 AI 生成的目标注入原生 GoalSystem，直接复用左侧 UIGoalPanel。
    ///
    /// 目标定义存进唯一一个 Global TodoModule 的 content（随存档保存），
    /// GoalProto 在运行时重新生成；原生 GoalSystem 只保存 GoalData/队列。
    /// 触发器完全复用原生 GoalDeterminator，不做第二套 DSL。
    /// </summary>
    internal static class GameGoals
    {
        private const int CustomIdStart = 910000;
        private const string StorePrefix = "icarus2-goals:";
        private const string StoreTitle = "Icarus II 目标";

        [Serializable]
        private sealed class StoredGoal
        {
            public int groupId;
            public int goalId;
            public string group;
            public string title;
            public int templateId;
            public long[] parameters;
        }

        [Serializable]
        private sealed class StoredSet
        {
            public List<StoredGoal> goals = new List<StoredGoal>();
        }

        private static StoredSet _store;
        private static string _saveKey;
        private static bool _loaded;
        private static bool _needsApply;
        private static bool _revealPending;
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
                    _revealPending = false;
                    _notes = null;
                    _nextId = CustomIdStart;
                }
                if (!_loaded)
                {
                    _store = LoadStore();
                    _loaded = true;
                    _needsApply = _store != null && _store.goals.Count > 0;
                    // 读档后已有 AI 目标也要可见；goalLevel=Off 时原生面板默认折叠，
                    // 这里同样做一次瞬态展开，否则玩家会以为目标丢了。
                    _revealPending = _needsApply;
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
                if (_applied) RefreshNotes();
                if (_revealPending) RevealPanel();
            }
            catch (Exception ex)
            {
                if (Time.frameCount % 300 == 0)
                    Plugin.Log.LogWarning("目标系统更新失败：" + ex.Message);
            }
        }

        private static bool _applied;
        private static Dictionary<int, string> _notes;

        public static void Reset()
        {
            _saveKey = null;
            _loaded = false;
            _store = null;
            _applied = false;
            _needsApply = false;
            _revealPending = false;
            _notes = null;
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
                    row.Set("params", LongArray(p.DeterminatorParams));
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
                    row.Set("stage", gd != null ? (int)gd.stage : 0);
                    arr.Add(row);
                }
            }
            var data = JsonValue.NewObject();
            data.Set("goals", arr);
            data.Set("count", arr.Count);
            return data;
        }

        /// <summary>新增一组目标；返回可展示的结果。args 见 GameTodoAddTool。</summary>
        public static JsonValue Add(JsonValue args)
        {
            var goalsArg = args["goals"];
            if (goalsArg == null || goalsArg.Count == 0) throw new InvalidOperationException("goals 不能为空");
            var groupTitle = args["group"].AsString("游戏目标") ?? "游戏目标";
            var added = JsonValue.NewArray();
            if (_store == null) _store = new StoredSet();

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
                    parameters = ParseParams(paramsArg);
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
            // 玩家主动要求生成目标，就必须马上能看到。原生目标面板在“关闭/折叠”状态
            // 不会自动弹开，因此这里做一次瞬态展开（不写 goalLevel 偏好）。
            _revealPending = true;
            Plugin.Log.LogInfo("游戏目标已注入 +" + added.Count.ToString(CultureInfo.InvariantCulture) +
                               "，groupId=" + groupId.ToString(CultureInfo.InvariantCulture));
            var data = JsonValue.NewObject();
            data.Set("groupId", groupId);
            data.Set("added", added);
            data.Set("count", added.Count);
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
            // 删掉某组最后一个子目标时，组也要从原生队列收起。
            if (groupId > 0 && goalId > 0)
            {
                bool hasLeft = false;
                for (int i = 0; i < _store.goals.Count; i++)
                    if (_store.goals[i] != null && _store.goals[i].groupId == groupId) { hasLeft = true; break; }
                if (!hasLeft) HideGoal(groupId);
            }
            SaveStore();
            _needsApply = true;
            _applied = false;
            ApplyAll();
        }

        private static bool ApplyAll()
        {
            if (_store == null || _store.goals.Count == 0) return true;
            var data = GameMain.data;
            var system = data.goalSystem;
            var logic = GameMain.gameScenario != null ? GameMain.gameScenario.goalLogic : null;
            if (system == null || logic == null || !logic.isInit) return false;

            var groups = new Dictionary<int, string>();
            for (int i = 0; i < _store.goals.Count; i++)
            {
                var g = _store.goals[i];
                if (g == null) continue;
                if (!groups.ContainsKey(g.groupId)) groups[g.groupId] = g.group ?? "游戏目标";
            }

            foreach (var pair in groups)
                EnsureGroupProto(pair.Key, pair.Value);
            for (int i = 0; i < _store.goals.Count; i++)
                EnsureChildProto(_store.goals[i]);
            RebuildGoalTable();

            foreach (var pair in groups)
                EnsureGoalData(pair.Key, true);
            for (int i = 0; i < _store.goals.Count; i++)
                EnsureGoalData(_store.goals[i].goalId, false);

            EnsureQueue(groups);
            foreach (var pair in groups)
                EnsureDeterminator(pair.Key);
            for (int i = 0; i < _store.goals.Count; i++)
                EnsureDeterminator(_store.goals[i].goalId);
            foreach (var pair in groups)
                EnsureDeterminator(pair.Key);

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
        /// 玩家把“目标”设为关闭时，原生面板默认折叠成小圆点；AI 目标是玩家主动要的，
        /// 这里把面板瞬态展开（只改 stateOverwritten，不写 goalLevel 偏好）。
        /// 原生 DetermineVisiable 可能还没把 Off 折叠状态设好，所以允许下一帧再试。
        /// </summary>
        private static void RevealPanel()
        {
            try
            {
                if (GameMain.data == null || GameMain.data.gameDesc == null ||
                    GameMain.data.gameDesc.goalLevel != EGoalLevel.Off)
                {
                    _revealPending = false;
                    return;
                }
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                var panel = uiGame != null ? uiGame.goalPanel : null;
                if (panel == null || !panel.inited) return;
                if (panel.isEnxpand)
                {
                    _revealPending = false;
                    return;
                }
                if (!panel.isCollapse) return;
                panel.OnCollapseButtonClick(0);
                if (panel.isEnxpand)
                {
                    _revealPending = false;
                    Plugin.Log.LogInfo("目标面板处于关闭状态，已为 AI 目标瞬态展开");
                }
            }
            catch (Exception ex)
            {
                _revealPending = false;
                Plugin.Log.LogWarning("展开目标面板失败：" + ex.Message);
            }
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

        private static StoredSet LoadStore()
        {
            var todo = FindStoreTodo();
            if (todo == null || string.IsNullOrEmpty(todo.content) || !todo.content.StartsWith(StorePrefix, StringComparison.Ordinal))
                return new StoredSet();
            try
            {
                var root = JsonValue.Parse(todo.content.Substring(StorePrefix.Length));
                var set = new StoredSet();
                var arr = root["goals"];
                for (int i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    var g = new StoredGoal
                    {
                        groupId = item["groupId"].AsInt(0),
                        goalId = item["goalId"].AsInt(0),
                        group = item["group"].AsString("游戏目标"),
                        title = item["title"].AsString("游戏目标"),
                        templateId = item["templateId"].AsInt(0),
                        parameters = ParseParams(item["params"]),
                    };
                    if (g.goalId > 0 && g.templateId > 0) set.goals.Add(g);
                }
                return set;
            }
            catch { return new StoredSet(); }
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
            var root = JsonValue.NewObject();
            var arr = JsonValue.NewArray();
            if (_store != null)
            {
                for (int i = 0; i < _store.goals.Count; i++)
                {
                    var g = _store.goals[i];
                    if (g == null) continue;
                    var row = JsonValue.NewObject();
                    row.Set("groupId", g.groupId);
                    row.Set("goalId", g.goalId);
                    row.Set("group", g.group ?? string.Empty);
                    row.Set("title", g.title ?? string.Empty);
                    row.Set("templateId", g.templateId);
                    row.Set("params", LongArray(g.parameters));
                    arr.Add(row);
                }
            }
            root.Set("goals", arr);
            todo.content = StorePrefix + root.ToJson();
        }

        private static TodoModule FindStoreTodo()
        {
            var digital = GameMain.data != null ? GameMain.data.galacticDigital : null;
            if (digital == null || digital.todos == null || digital.todos.buffer == null) return null;
            for (int i = 1; i < digital.todos.cursor; i++)
            {
                var todo = digital.todos.buffer[i];
                if (todo != null && todo.ownerType == ETodoModuleOwnerType.Global &&
                    todo.content != null && todo.content.StartsWith(StorePrefix, StringComparison.Ordinal))
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

        private static long[] ParseParams(JsonValue arr)
        {
            var list = new long[arr.Count];
            for (int i = 0; i < arr.Count; i++) list[i] = arr[i].AsLong(0);
            return list;
        }

        private static JsonValue LongArray(long[] values)
        {
            var arr = JsonValue.NewArray();
            if (values != null) for (int i = 0; i < values.Length; i++) arr.Add(JsonValue.Of(values[i]));
            return arr;
        }
    }
}
