using System;
using System.Globalization;
using IcarusII.Common.Json;

namespace IcarusII.Game.Tools
{
    /// <summary>读取原生 GoalProto 模板，供模型选择判断器/参数。</summary>
    internal sealed class GameTodoTemplatesTool : IGameToolHandler
    {
        public string Name => "game_todo_templates";
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new WriteJob(() =>
        {
            if (!GameRefs.IsInGame) throw new InvalidOperationException("当前不在游戏内");
            return new GameToolJobResult { Data = GameGoals.Templates() };
        });
    }

    internal sealed class GameTodoListTool : IGameToolHandler
    {
        public string Name => "game_todo_list";
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new WriteJob(() =>
        {
            if (!GameRefs.IsInGame) throw new InvalidOperationException("当前不在游戏内");
            GameGoals.Tick();
            return new GameToolJobResult { Data = GameGoals.List() };
        });
    }

    /// <summary>新增目标：直接复用原生 GoalProto 模板和 GoalDeterminator。</summary>
    internal sealed class GameTodoAddTool : WriteToolBase
    {
        public override string Name => "game_todo_add";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.data == null) throw Fail("当前不在游戏内");
            GameGoals.Tick();
            try
            {
                var data = GameGoals.Add(args);
                WriteActionStack.MarkNonUndoable("新增游戏目标");
                return Ok("已新增 " + data["count"].AsInt(0).ToString(CultureInfo.InvariantCulture) + " 个游戏目标", data);
            }
            catch (Exception ex)
            {
                throw Fail(ex.Message);
            }
        }
    }

    /// <summary>删除 AI 创建的目标；只动自定义 910000+ 范围。</summary>
    internal sealed class GameTodoRemoveTool : WriteToolBase
    {
        public override string Name => "game_todo_remove";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.data == null) throw Fail("当前不在游戏内");
            int groupId = args["groupId"].AsInt(0);
            int goalId = args["goalId"].AsInt(0);
            if (groupId <= 0 && goalId <= 0) throw Fail("groupId 和 goalId 至少提供一个");
            GameGoals.Tick();
            GameGoals.Remove(groupId, goalId);
            WriteActionStack.MarkNonUndoable("删除游戏目标");
            return Ok("已删除游戏目标");
        }
    }
}
