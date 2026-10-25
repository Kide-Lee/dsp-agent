using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 生产建筑写操作：改配方（单台/批量）。
    ///
    /// 游戏 UI 改配方前会先 `TakeBackItems_Assembler`，把机器内的原料/产物退回背包；
    /// 这里复用同一入口，避免 `SetRecipe` 直接重置 served/produced 导致物品消失。
    /// 批量只压一次撤销栈：撤销时逐台退回新配方物品并恢复旧配方。
    /// </summary>
    internal abstract class RecipeToolBase : WriteToolBase
    {
        protected const int MaxBatch = 20;

        protected sealed class Selection
        {
            public int BuildingId;
            public int RecipeId;
        }

        private sealed class Change
        {
            public int BuildingId;
            public int OldRecipeId;
            public int NewRecipeId;
            public string BuildingName;
        }

        protected abstract List<Selection> ReadSelections(JsonValue args);

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");

            var planet = GameToolFormat.ResolvePlanetStrict(args);
            if (planet == null || planet.factory == null)
                throw Fail("目标行星的工厂尚未加载");

            var player = GameMain.mainPlayer;
            if (player.mecha == null)
                throw Fail("机甲尚未就绪");

            var factory = planet.factory;
            var system = factory.factorySystem;
            if (system == null || system.assemblerPool == null)
                throw Fail("工厂装配系统尚未就绪");

            var selections = ReadSelections(args);
            if (selections == null || selections.Count == 0)
                throw Fail("没有要修改的建筑");

            // 先全部预检，再动手：避免改到一半才发现某台不是合成器/配方类型不匹配。
            var changes = new List<Change>();
            var seen = new HashSet<int>();
            foreach (var selection in selections)
            {
                if (selection.BuildingId <= 0)
                    throw Fail("buildingId 必须是正整数");
                if (selection.RecipeId < 0)
                    throw Fail("recipeId 不能为负数（0 表示清空配方）");
                if (!seen.Add(selection.BuildingId))
                    throw Fail("buildingId 重复：" + selection.BuildingId.ToString(CultureInfo.InvariantCulture));
                if (!EntityToolUtil.HasEntity(factory, selection.BuildingId))
                    throw Fail("找不到建筑 #" + selection.BuildingId.ToString(CultureInfo.InvariantCulture));

                var entity = factory.entityPool[selection.BuildingId];
                if (entity.id != selection.BuildingId || entity.protoId <= 0)
                    throw Fail("建筑 #" + selection.BuildingId.ToString(CultureInfo.InvariantCulture) + " 已失效");
                if (entity.assemblerId <= 0 || entity.assemblerId >= system.assemblerPool.Length)
                    throw Fail("建筑 #" + selection.BuildingId.ToString(CultureInfo.InvariantCulture) + " 不是可改配方的生产建筑");
                if (system.assemblerPool[entity.assemblerId].id != entity.assemblerId)
                    throw Fail("建筑 #" + selection.BuildingId.ToString(CultureInfo.InvariantCulture) + " 的装配组件已失效");

                var offset = entity.pos - player.position;
                if (offset.sqrMagnitude > player.mecha.buildArea * player.mecha.buildArea)
                    throw Fail("建筑 #" + selection.BuildingId.ToString(CultureInfo.InvariantCulture) + " 超出机甲建造范围");

                var assembler = system.assemblerPool[entity.assemblerId];
                if (selection.RecipeId > 0)
                    ValidateRecipe(factory, entity, selection.RecipeId);

                if (assembler.recipeId == selection.RecipeId) continue;
                changes.Add(new Change
                {
                    BuildingId = selection.BuildingId,
                    OldRecipeId = assembler.recipeId,
                    NewRecipeId = selection.RecipeId,
                    BuildingName = EntityToolUtil.ProtoName(entity.protoId),
                });
            }

            if (changes.Count == 0)
                return Ok("配方未变化");

            var applied = new List<Change>();
            try
            {
                foreach (var change in changes)
                {
                    ApplyRecipe(factory, player, change.BuildingId, change.NewRecipeId);
                    applied.Add(change);
                }
            }
            catch
            {
                // 极端情况下回滚已改的机器；已经退回背包的物品不再塞回，避免复制。
                for (int i = applied.Count - 1; i >= 0; i--)
                {
                    try { ApplyRecipe(factory, player, applied[i].BuildingId, applied[i].OldRecipeId); }
                    catch { /* 保留原始异常 */ }
                }
                throw;
            }

            WriteActionStack.Push(
                "恢复 " + changes.Count.ToString(CultureInfo.InvariantCulture) + " 台建筑的配方",
                () =>
                {
                    foreach (var change in changes)
                        ApplyRecipe(factory, player, change.BuildingId, change.OldRecipeId);
                });

            var rows = JsonValue.NewArray();
            foreach (var change in changes)
            {
                var row = JsonValue.NewObject();
                row.Set("buildingId", change.BuildingId);
                row.Set("buildingName", change.BuildingName);
                row.Set("fromRecipeId", change.OldRecipeId);
                row.Set("fromRecipeName", EntityToolUtil.RecipeName(change.OldRecipeId));
                row.Set("toRecipeId", change.NewRecipeId);
                row.Set("toRecipeName", EntityToolUtil.RecipeName(change.NewRecipeId));
                rows.Add(row);
            }

            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);
            data.Set("changed", changes.Count);
            data.Set("skipped", selections.Count - changes.Count);
            data.Set("items", rows);
            data.Set("undoable", true);
            data.Set("itemNote", "改配方前已把机器内原料/产物退回背包；撤销只恢复配方");
            return Ok("已更换 " + changes.Count.ToString(CultureInfo.InvariantCulture)
                + " 台建筑的配方（原料/产物已退回背包）", data);
        }

        private static void ValidateRecipe(PlanetFactory factory, EntityData entity, int recipeId)
        {
            var recipe = LDB.recipes != null ? LDB.recipes.Select(recipeId) : null;
            if (recipe == null)
                throw Fail("找不到配方 #" + recipeId.ToString(CultureInfo.InvariantCulture));

            var item = LDB.items != null ? LDB.items.Select(entity.protoId) : null;
            var prefab = item != null ? item.prefabDesc : null;
            var expected = prefab != null ? prefab.assemblerRecipeType : ERecipeType.None;
            if (expected == ERecipeType.None)
                throw Fail("建筑 #" + entity.id.ToString(CultureInfo.InvariantCulture) + " 不接受配方");
            if (recipe.Type != expected)
                throw Fail("配方 " + recipe.name + " 的类型与建筑不匹配（需要 "
                    + expected.ToString() + "，实际 " + recipe.Type.ToString() + "）");

            var history = GameMain.history;
            if (history == null || !history.RecipeUnlocked(recipeId))
                throw Fail("配方 " + recipe.name + " 尚未解锁");
        }

        private static void ApplyRecipe(PlanetFactory factory, Player player, int buildingId, int recipeId)
        {
            var entity = factory.entityPool[buildingId];
            if (entity.id != buildingId || entity.assemblerId <= 0)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 已失效");
            var system = factory.factorySystem;
            // AssemblerComponent 是 struct：必须 ref 取池元素，否则 SetRecipe 改的是副本。
            ref var assembler = ref system.assemblerPool[entity.assemblerId];
            if (assembler.id != entity.assemblerId)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 的装配组件已失效");

            // 与游戏 UI 的“重置配方”一致：先退料，再 SetRecipe。
            system.TakeBackItems_Assembler(player, entity.assemblerId);
            assembler.SetRecipe(recipeId, factory.entitySignPool);
        }
    }

    internal sealed class SetRecipeTool : RecipeToolBase
    {
        public override string Name => "game_set_recipe";

        protected override List<Selection> ReadSelections(JsonValue args)
        {
            if (!args.Has("buildingId") || !args.Has("recipeId"))
                throw Fail("需要 buildingId 和 recipeId");
            return new List<Selection>
            {
                new Selection
                {
                    BuildingId = args["buildingId"].AsInt(0),
                    RecipeId = args["recipeId"].AsInt(-1),
                },
            };
        }
    }

    internal sealed class SetAssemblerBatchTool : RecipeToolBase
    {
        public override string Name => "game_set_assembler_batch";

        protected override List<Selection> ReadSelections(JsonValue args)
        {
            var value = args["selections"];
            if (value == null || !value.IsArray || value.Count == 0)
                throw Fail("selections 必须是非空数组");
            if (value.Count > MaxBatch)
                throw Fail("selections 一次最多 " + MaxBatch.ToString(CultureInfo.InvariantCulture) + " 项");

            var list = new List<Selection>(value.Count);
            for (int i = 0; i < value.Count; i++)
            {
                var row = value[i];
                if (row == null || !row.IsObject)
                    throw Fail("selections[" + i.ToString(CultureInfo.InvariantCulture) + "] 必须是对象");
                if (!row.Has("buildingId") || !row.Has("recipeId"))
                    throw Fail("selections[" + i.ToString(CultureInfo.InvariantCulture) + "] 需要 buildingId 和 recipeId");
                list.Add(new Selection
                {
                    BuildingId = row["buildingId"].AsInt(0),
                    RecipeId = row["recipeId"].AsInt(-1),
                });
            }
            return list;
        }
    }
}
