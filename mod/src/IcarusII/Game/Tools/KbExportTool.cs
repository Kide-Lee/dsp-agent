using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IcarusII.Common.Json;
using IcarusII.Game;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// M3 知识库导出：把 LDB 的关键原型集分块交给 sidecar。
    ///
    /// 为什么分块/分帧：LDB 有 175 物品 + 162 配方 + 315 科技 + 664 模型，
    /// 一次性序列化会在主线程造成明显卡顿。这里每帧只做 budgetMs 指定的量，
    /// sidecar 按 offset/limit 连续调用。
    ///
    /// 这个工具是 sidecar 内部调用（tool_call.internal=true），玩家看不到工具卡。
    /// </summary>
    internal sealed class ExportPrototypesTool : IGameToolHandler
    {
        public string Name => "game_export_prototypes";
        public int TimeoutMs => 15000;

        private const int MaxLimit = 500;

        public IGameToolJob Start(JsonValue args)
        {
            args = args != null && args.IsObject ? args : JsonValue.NewObject();
            var set = args["set"].AsString("");
            if (!IsSupportedSet(set))
                throw new ArgumentException("set 必须是 items/recipes/techs/models/veins/signals 之一");

            var total = Total(set);
            var offset = Math.Max(0, args["offset"].AsInt(0));
            if (offset > total) offset = total;
            var limit = args["limit"].AsInt(200);
            if (limit <= 0) limit = 200;
            if (limit > MaxLimit) limit = MaxLimit;

            return new ExportJob(set, offset, limit, total);
        }

        private static bool IsSupportedSet(string set)
        {
            return set == "items" || set == "recipes" || set == "techs"
                || set == "models" || set == "veins" || set == "signals";
        }

        private static int Total(string set)
        {
            try
            {
                switch (set)
                {
                    case "items": return LDB.items != null && LDB.items.dataArray != null ? LDB.items.dataArray.Length : 0;
                    case "recipes": return LDB.recipes != null && LDB.recipes.dataArray != null ? LDB.recipes.dataArray.Length : 0;
                    case "techs": return LDB.techs != null && LDB.techs.dataArray != null ? LDB.techs.dataArray.Length : 0;
                    case "models": return LDB.models != null && LDB.models.dataArray != null ? LDB.models.dataArray.Length : 0;
                    case "veins": return LDB.veins != null && LDB.veins.dataArray != null ? LDB.veins.dataArray.Length : 0;
                    case "signals": return LDB.signals != null && LDB.signals.dataArray != null ? LDB.signals.dataArray.Length : 0;
                    default: return 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        private sealed class ExportJob : IGameToolJob
        {
            private readonly string _set;
            private readonly int _offset;
            private readonly int _limit;
            private readonly int _total;
            private readonly int _end;
            private readonly List<JsonValue> _rows = new List<JsonValue>(200);
            private int _cursor;

            public ExportJob(string set, int offset, int limit, int total)
            {
                _set = set;
                _offset = offset;
                _limit = limit;
                _total = total;
                _end = Math.Min(total, offset + limit);
                _cursor = offset;
            }

            public bool Step(int budgetMs)
            {
                var watch = Stopwatch.StartNew();
                while (_cursor < _end)
                {
                    _rows.Add(BuildRow(_set, _cursor));
                    _cursor++;
                    if (watch.ElapsedMilliseconds >= budgetMs) break;
                }
                return _cursor >= _end || _rows.Count >= _limit;
            }

            public GameToolJobResult Finish()
            {
                var data = JsonValue.NewObject();
                data.Set("schemaVersion", 1);
                data.Set("gameVersion", GameRefs.GameVersion);
                data.Set("locale", GameRefs.LanguageCode);
                data.Set("set", _set);
                data.Set("offset", _offset);
                data.Set("limit", _limit);
                data.Set("total", _total);
                var done = _cursor >= _total;
                data.Set("done", done);

                var rows = JsonValue.NewArray();
                foreach (var row in _rows) rows.Add(row);
                data.Set("rows", rows);

                return new GameToolJobResult
                {
                    Data = data,
                    Truncated = !done,
                    NextOffset = done ? -1 : _cursor,
                };
            }
        }

        // ------------------------------------------------------------ 行序列化

        private static JsonValue BuildRow(string set, int index)
        {
            switch (set)
            {
                case "items": return BuildItem(index);
                case "recipes": return BuildRecipe(index);
                case "techs": return BuildTech(index);
                case "models": return BuildModel(index);
                case "veins": return BuildVein(index);
                case "signals": return BuildSignal(index);
                default: return Minimal(set, index, "未知集合");
            }
        }

        private static JsonValue BuildItem(int index)
        {
            var arr = LDB.items != null ? LDB.items.dataArray : null;
            if (arr == null || index >= arr.Length) return Minimal("item", index, "越界");
            var p = arr[index];
            if (p == null) return Minimal("item", index, "空原型");

            var row = JsonValue.NewObject();
            row.Set("id", p.ID);
            row.SetIfNotNull("sid", p.SID);
            row.Set("name", Clean(p.name ?? p.Name));
            row.SetIfNotNull("description", Clean(p.description));
            row.Set("type", (int)p.Type);
            row.Set("subId", p.SubID);
            row.Set("stackSize", p.StackSize);
            row.Set("grade", p.Grade);
            row.Set("isFluid", p.IsFluid);
            row.Set("isEntity", p.IsEntity);
            row.Set("canBuild", p.CanBuild);
            row.Set("buildInGas", p.BuildInGas);
            row.Set("gridIndex", p.GridIndex);
            row.SetIfNotNull("iconTag", p.IconTag);
            row.SetIfNotNull("miningFrom", Clean(p.miningFrom));
            row.SetIfNotNull("produceFrom", Clean(p.produceFrom));
            row.Set("modelIndex", p.ModelIndex);
            row.Set("modelCount", p.ModelCount);
            row.Set("unlockKey", p.UnlockKey);
            row.Set("preTechOverride", p.PreTechOverride);
            row.Set("productive", p.Productive);

            try
            {
                var main = p.maincraft;
                if (main != null && main.ID > 0) row.Set("mainRecipeId", main.ID);
                var hand = p.handcraft;
                if (hand != null && hand.ID > 0) row.Set("handRecipeId", hand.ID);
            }
            catch { /* 未 Preload 完时忽略 */ }

            try
            {
                var desc = p.prefabDesc;
                if (desc != null)
                {
                    var building = JsonValue.NewObject();
                    building.Set("assemblerSpeed", desc.assemblerSpeed);
                    // kb_plan 用它把配方映射到机器；没有这个字段就只能按 1x 估算。
                    building.Set("assemblerRecipeType", (int)desc.assemblerRecipeType);
                    building.Set("workEnergyPerTick", desc.workEnergyPerTick);
                    building.Set("minerPeriod", desc.minerPeriod);
                    building.Set("beltSpeed", desc.beltSpeed);
                    building.Set("storageCol", desc.storageCol);
                    building.Set("storageRow", desc.storageRow);
                    building.Set("stationMaxEnergyAcc", desc.stationMaxEnergyAcc);
                    building.Set("isCollectStation", desc.isCollectStation);
                    row.Set("building", building);
                }
            }
            catch { /* PrefabDesc 可能尚未加载 */ }

            return row;
        }

        private static JsonValue BuildRecipe(int index)
        {
            var arr = LDB.recipes != null ? LDB.recipes.dataArray : null;
            if (arr == null || index >= arr.Length) return Minimal("recipe", index, "越界");
            var p = arr[index];
            if (p == null) return Minimal("recipe", index, "空原型");

            var row = JsonValue.NewObject();
            row.Set("id", p.ID);
            row.SetIfNotNull("sid", p.SID);
            row.Set("name", Clean(p.name ?? p.Name));
            row.SetIfNotNull("description", Clean(p.description));
            row.Set("type", (int)p.Type);
            row.Set("handcraft", p.Handcraft);
            row.Set("explicit", p.Explicit);
            row.Set("timeSpend", p.TimeSpend);
            row.Set("timeSeconds", p.TimeSpend / 60.0);
            row.Set("inputs", Counts(p.Items, p.ItemCounts));
            row.Set("outputs", Counts(p.Results, p.ResultCounts));
            row.Set("nonProductive", p.NonProductive);
            row.SetIfNotNull("iconTag", p.IconTag);
            return row;
        }

        private static JsonValue BuildTech(int index)
        {
            var arr = LDB.techs != null ? LDB.techs.dataArray : null;
            if (arr == null || index >= arr.Length) return Minimal("tech", index, "越界");
            var p = arr[index];
            if (p == null) return Minimal("tech", index, "空原型");

            var row = JsonValue.NewObject();
            row.Set("id", p.ID);
            row.SetIfNotNull("sid", p.SID);
            row.Set("name", Clean(p.name ?? p.Name));
            row.SetIfNotNull("description", Clean(p.description));
            row.SetIfNotNull("conclusion", Clean(p.conclusion));
            row.Set("level", p.Level);
            row.Set("maxLevel", p.MaxLevel);
            row.Set("preTechs", Ints(p.PreTechs));
            row.Set("preTechsImplicit", Ints(p.PreTechsImplicit));
            row.Set("items", Ints(p.Items));
            row.Set("itemPoints", Ints(p.ItemPoints));
            row.Set("hashNeeded", p.HashNeeded);
            row.Set("unlockRecipes", Ints(p.UnlockRecipes));
            row.Set("unlockFunctions", Ints(p.UnlockFunctions));
            row.Set("unlockValues", Doubles(p.UnlockValues));
            row.Set("published", p.Published);
            row.Set("isHiddenTech", p.IsHiddenTech);
            row.Set("isObsolete", p.IsObsolete);
            row.SetIfNotNull("iconTag", p.IconTag);
            try
            {
                var text = p.UnlockFunctionText(new StringBuilder());
                if (!string.IsNullOrEmpty(text)) row.Set("unlockText", Clean(text));
            }
            catch { /* 依赖 GameMain 的个别分支可能未就绪 */ }
            return row;
        }

        private static JsonValue BuildModel(int index)
        {
            var arr = LDB.models != null ? LDB.models.dataArray : null;
            if (arr == null || index >= arr.Length) return Minimal("model", index, "越界");
            var p = arr[index];
            if (p == null) return Minimal("model", index, "空原型");

            var row = JsonValue.NewObject();
            row.Set("id", p.ID);
            row.SetIfNotNull("sid", p.SID);
            string name = null;
            try { name = p.displayName; } catch { }
            row.Set("name", Clean(string.IsNullOrEmpty(name) ? ("模型 #" + index.ToString(CultureInfo.InvariantCulture)) : name));
            row.Set("objectType", (int)p.ObjectType);
            row.SetIfNotNull("prefabPath", p.PrefabPath);
            return row;
        }

        private static JsonValue BuildVein(int index)
        {
            var arr = LDB.veins != null ? LDB.veins.dataArray : null;
            if (arr == null || index >= arr.Length) return Minimal("vein", index, "越界");
            var p = arr[index];
            if (p == null) return Minimal("vein", index, "空原型");

            var row = JsonValue.NewObject();
            row.Set("id", p.ID);
            row.SetIfNotNull("sid", p.SID);
            row.Set("name", Clean(p.name ?? p.Name));
            row.SetIfNotNull("description", Clean(p.description));
            row.Set("miningItem", p.MiningItem);
            row.Set("miningTime", p.MiningTime);
            row.SetIfNotNull("iconTag", p.IconTag);
            return row;
        }

        private static JsonValue BuildSignal(int index)
        {
            var arr = LDB.signals != null ? LDB.signals.dataArray : null;
            if (arr == null || index >= arr.Length) return Minimal("signal", index, "越界");
            var p = arr[index];
            if (p == null) return Minimal("signal", index, "空原型");

            var row = JsonValue.NewObject();
            row.Set("id", p.ID);
            row.SetIfNotNull("sid", p.SID);
            row.Set("name", Clean(p.name ?? p.Name));
            row.SetIfNotNull("iconTag", p.IconTag);
            return row;
        }

        private static JsonValue Minimal(string kind, int index, string reason)
        {
            var row = JsonValue.NewObject();
            row.Set("id", index);
            row.Set("name", "（" + kind + " #" + index.ToString(CultureInfo.InvariantCulture) + " 导出失败：" + reason + "）");
            return row;
        }

        /// <summary>去掉游戏富文本/图标短码/按键占位符，避免模型把标记当事实。</summary>
        internal static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var s = value
                .Replace("\\r\\n", "\n")
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\u00a0", " ")
                .Replace("\u2009", " ")
                .Replace("\u2005", " ")
                .Replace("\u200b", "");
            s = Regex.Replace(s, "<[^>]+>", string.Empty);
            s = Regex.Replace(s, @"\\+[A-Za-z0-9_\-]+;", string.Empty);
            s = Regex.Replace(s, @"\$LKEY_\d+;", "[按键]");
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        private static JsonValue Ints(int[] values)
        {
            var arr = JsonValue.NewArray();
            if (values != null)
                for (int i = 0; i < values.Length; i++) arr.Add(values[i]);
            return arr;
        }

        private static JsonValue Doubles(double[] values)
        {
            var arr = JsonValue.NewArray();
            if (values != null)
                for (int i = 0; i < values.Length; i++) arr.Add(values[i]);
            return arr;
        }

        private static JsonValue Counts(int[] ids, int[] counts)
        {
            var arr = JsonValue.NewArray();
            if (ids == null) return arr;
            for (int i = 0; i < ids.Length; i++)
            {
                var row = JsonValue.NewObject();
                row.Set("id", ids[i]);
                row.Set("count", counts != null && i < counts.Length ? counts[i] : 1);
                arr.Add(row);
            }
            return arr;
        }
    }
}
