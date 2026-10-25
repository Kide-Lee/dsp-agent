using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 生产建筑写操作：取成品、拆除。
    ///
    /// 取成品复用 UIAssemblerWindow 的 `produced[]` + `TryAddItemToPackage` 路径，
    /// 保留配方；拆除复用 `PlanetFactory.DismantleFinally`，由游戏完成退料/清理。
    /// 拆除不可撤销，确认卡必须明确；批量只弹一次确认。
    /// </summary>
    internal sealed class TakeOutputTool : WriteToolBase
    {
        public override string Name => "game_take_output";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null || planet.factory == null)
                throw Fail("目标行星的工厂尚未加载");
            var factory = planet.factory;
            var player = GameMain.mainPlayer;
            var buildingId = args["buildingId"].AsInt(0);
            if (buildingId <= 0) throw Fail("buildingId 必须是正整数");
            if (!EntityToolUtil.HasEntity(factory, buildingId))
                throw Fail("找不到建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture));
            var entity = factory.entityPool[buildingId];
            if (entity.id != buildingId || entity.assemblerId <= 0)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 不是生产建筑");
            var system = factory.factorySystem;
            if (system == null || system.assemblerPool == null || entity.assemblerId >= system.assemblerPool.Length)
                throw Fail("装配系统尚未就绪");
            var offset = entity.pos - player.position;
            if (offset.sqrMagnitude > player.mecha.buildArea * player.mecha.buildArea)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 超出机甲建造范围");

            ref var assembler = ref system.assemblerPool[entity.assemblerId];
            if (assembler.id != entity.assemblerId || assembler.recipeId <= 0 || assembler.recipeExecuteData == null)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 当前没有配方");
            var products = assembler.recipeExecuteData.products;
            if (products == null || products.Length == 0)
                throw Fail("该配方没有产物");

            var rows = JsonValue.NewArray();
            int totalAdded = 0;
            int totalRemaining = 0;
            for (int i = 0; i < products.Length && i < assembler.produced.Length; i++)
            {
                var count = assembler.produced[i];
                if (count <= 0) continue;
                var itemId = products[i];
                var added = player.TryAddItemToPackage(itemId, count, 0, false, entity.id);
                if (added > 0) assembler.produced[i] = count - added;
                totalAdded += Math.Max(0, added);
                totalRemaining += Math.Max(0, count - Math.Max(0, added));
                var row = JsonValue.NewObject();
                row.Set("itemId", itemId);
                row.Set("name", GameToolFormat.ItemName(itemId));
                row.Set("taken", Math.Max(0, added));
                row.Set("remaining", Math.Max(0, count - Math.Max(0, added)));
                rows.Add(row);
            }
            if (totalAdded <= 0)
                throw Fail("背包无法再放入该建筑的成品（可能已满）");

            var data = JsonValue.NewObject();
            data.Set("buildingId", entity.id);
            data.Set("taken", totalAdded);
            data.Set("remaining", totalRemaining);
            data.Set("items", rows);
            data.Set("undoable", false);
            var suffix = totalRemaining > 0 ? "，背包满，剩余 " + totalRemaining.ToString(CultureInfo.InvariantCulture) : "";
            return Ok("已取出建筑 #" + entity.id.ToString(CultureInfo.InvariantCulture)
                + " 的成品 " + totalAdded.ToString(CultureInfo.InvariantCulture) + " 个" + suffix, data);
        }
    }

    internal sealed class DismantleTool : WriteToolBase
    {
        public override string Name => "game_dismantle";
        private const int MaxBatch = 50;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null || planet.factory == null)
                throw Fail("目标行星的工厂尚未加载");
            var factory = planet.factory;
            var player = GameMain.mainPlayer;
            var values = args["buildingIds"];
            if (values == null || !values.IsArray || values.Count == 0)
                throw Fail("buildingIds 必须是非空数组");
            if (values.Count > MaxBatch)
                throw Fail("一次最多拆除 " + MaxBatch.ToString(CultureInfo.InvariantCulture) + " 个建筑");

            var ids = new List<int>(values.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < values.Count; i++)
            {
                var id = values[i].AsInt(0);
                if (id <= 0) throw Fail("buildingIds 必须都是正整数");
                if (!seen.Add(id)) throw Fail("buildingIds 重复：" + id.ToString(CultureInfo.InvariantCulture));
                if (!EntityToolUtil.HasEntity(factory, id))
                    throw Fail("找不到建筑 #" + id.ToString(CultureInfo.InvariantCulture));
                var entity = factory.entityPool[id];
                if (entity.id != id) throw Fail("建筑 #" + id.ToString(CultureInfo.InvariantCulture) + " 已失效");
                var offset = entity.pos - player.position;
                if (offset.sqrMagnitude > player.mecha.buildArea * player.mecha.buildArea)
                    throw Fail("建筑 #" + id.ToString(CultureInfo.InvariantCulture) + " 超出机甲建造范围");
                ids.Add(id);
            }

            var rows = JsonValue.NewArray();
            foreach (var id in ids)
            {
                var protoId = 0;
                factory.DismantleFinally(player, id, ref protoId);
                var row = JsonValue.NewObject();
                row.Set("buildingId", id);
                row.Set("protoId", protoId);
                row.Set("name", EntityToolUtil.ProtoName(protoId));
                rows.Add(row);
            }

            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);
            data.Set("dismantled", ids.Count);
            data.Set("items", rows);
            data.Set("undoable", false);
            return Ok("已拆除 " + ids.Count.ToString(CultureInfo.InvariantCulture) + " 个建筑（不可撤销）", data);
        }
    }
    /// <summary>
    /// 从背包直接把原料放进生产建筑的输入缓冲（served/incServed）。
    /// 这条路径不经过分拣器/传送带，玩家确认卡会明确说明；只填配方需要的物品。
    /// </summary>
    internal sealed class FeedInputTool : WriteToolBase
    {
        public override string Name => "game_feed_input";
        private const int MaxFeed = 500;

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null || planet.factory == null)
                throw Fail("目标行星的工厂尚未加载");
            var factory = planet.factory;
            var player = GameMain.mainPlayer;
            var buildingId = args["buildingId"].AsInt(0);
            var itemId = args["itemId"].AsInt(0);
            var count = args["count"].AsInt(0);
            if (buildingId <= 0) throw Fail("buildingId 必须是正整数");
            if (itemId <= 0) throw Fail("itemId 必须是正整数");
            if (count <= 0 || count > MaxFeed) throw Fail("count 必须在 1.." + MaxFeed.ToString(CultureInfo.InvariantCulture));
            if (!EntityToolUtil.HasEntity(factory, buildingId))
                throw Fail("找不到建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture));
            var entity = factory.entityPool[buildingId];
            if (entity.id != buildingId || entity.assemblerId <= 0)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 不是生产建筑");
            var system = factory.factorySystem;
            if (system == null || system.assemblerPool == null || entity.assemblerId >= system.assemblerPool.Length)
                throw Fail("装配系统尚未就绪");
            var offset = entity.pos - player.position;
            if (offset.sqrMagnitude > player.mecha.buildArea * player.mecha.buildArea)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 超出机甲建造范围");

            ref var assembler = ref system.assemblerPool[entity.assemblerId];
            if (assembler.id != entity.assemblerId || assembler.recipeId <= 0 || assembler.recipeExecuteData == null)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 当前没有配方");
            var recipe = LDB.recipes != null ? LDB.recipes.Select(assembler.recipeId) : null;
            if (recipe == null || recipe.Items == null)
                throw Fail("当前配方没有输入");
            var inputIndex = -1;
            for (int i = 0; i < recipe.Items.Length; i++)
                if (recipe.Items[i] == itemId) { inputIndex = i; break; }
            if (inputIndex < 0)
                throw Fail("该配方不需要 " + GameToolFormat.ItemName(itemId) + "；需要的输入见 recipe.Items");
            if (assembler.served == null || assembler.incServed == null || inputIndex >= assembler.served.Length)
                throw Fail("装配组件输入缓冲尚未就绪");

            var takeId = itemId;
            var takeCount = count;
            player.TakeItemFromPlayer(ref takeId, ref takeCount, out var inc, true, null);
            if (takeCount <= 0) throw Fail("背包里没有该物品");

            assembler.served[inputIndex] += takeCount;
            assembler.incServed[inputIndex] += inc;
            var added = takeCount;
            WriteActionStack.Push("从生产建筑 #" + entity.id.ToString(CultureInfo.InvariantCulture)
                + " 取回 " + added.ToString(CultureInfo.InvariantCulture) + " 个 "
                + GameToolFormat.ItemName(itemId), () =>
            {
                ref var current = ref system.assemblerPool[entity.assemblerId];
                if (current.id != entity.assemblerId || current.served == null || inputIndex >= current.served.Length)
                    return;
                var take = Math.Min(added, current.served[inputIndex]);
                current.served[inputIndex] -= take;
                if (current.incServed != null && inputIndex < current.incServed.Length)
                    current.incServed[inputIndex] = Math.Max(0, current.incServed[inputIndex] - inc);
                if (take > 0) player.TryAddItemToPackage(itemId, take, inc, false, entity.id);
            });

            var data = JsonValue.NewObject();
            data.Set("buildingId", entity.id);
            data.Set("itemId", itemId);
            data.Set("itemName", GameToolFormat.ItemName(itemId));
            data.Set("fed", added);
            data.Set("served", assembler.served[inputIndex]);
            data.Set("undoable", true);
            return Ok("已把 " + added.ToString(CultureInfo.InvariantCulture) + " 个 "
                + GameToolFormat.ItemName(itemId) + " 放进生产建筑 #"
                + entity.id.ToString(CultureInfo.InvariantCulture) + " 的输入缓冲", data);
        }
    }

}
