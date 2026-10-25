using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 存储建筑写操作：过滤器、容量上限。
    ///
    /// 只允许 Default/Filtered 存储（箱子/储液罐/物流站仓储）；燃料/弹药/战斗
    /// 存储有自己的类型约束，不能按普通过滤器改。改过滤器沿用游戏 UI 的
    /// `SetFilter` 路径：已占用且物品不同的格子跳过，不会把物品替换掉。
    /// </summary>
    internal abstract class StorageWriteToolBase : WriteToolBase
    {
        protected sealed class Target
        {
            public PlanetFactory Factory;
            public EntityData Entity;
            public StorageComponent Storage;
        }

        protected Target ResolveTarget(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null || planet.factory == null)
                throw Fail("目标行星的工厂尚未加载");
            return ResolveStorage(planet.factory, args["buildingId"].AsInt(0));
        }

        protected Target ResolveStorage(PlanetFactory factory, int buildingId)
        {
            if (buildingId <= 0) throw Fail("buildingId 必须是正整数");
            if (!EntityToolUtil.HasEntity(factory, buildingId))
                throw Fail("找不到建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture));
            var entity = factory.entityPool[buildingId];
            if (entity.id != buildingId || entity.storageId <= 0)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 不是存储建筑");
            if (factory.factoryStorage == null || factory.factoryStorage.storagePool == null ||
                entity.storageId >= factory.factoryStorage.storagePool.Length)
                throw Fail("存储组件尚未就绪");
            var storage = factory.factoryStorage.storagePool[entity.storageId];
            if (storage == null || storage.id != entity.storageId)
                throw Fail("存储组件已失效");
            var offset = entity.pos - GameMain.mainPlayer.position;
            if (offset.sqrMagnitude > GameMain.mainPlayer.mecha.buildArea * GameMain.mainPlayer.mecha.buildArea)
                throw Fail("建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 超出机甲建造范围");
            if (storage.type != EStorageType.Default && storage.type != EStorageType.Filtered)
                throw Fail("该存储类型不支持此操作（仅普通箱子/储液罐/物流站仓储）");
            return new Target { Factory = factory, Entity = entity, Storage = storage };
        }
    }

    internal sealed class StorageSetFilterTool : StorageWriteToolBase
    {
        public override string Name => "game_storage_set_filter";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            var target = ResolveTarget(args);
            if (!args.Has("itemId")) throw Fail("需要 itemId（0 = 清除过滤器）");
            var itemId = args["itemId"].AsInt(-1);
            if (itemId < 0) throw Fail("itemId 不能为负数");
            if (itemId > 0)
            {
                var item = LDB.items != null ? LDB.items.Select(itemId) : null;
                if (item == null) throw Fail("找不到物品 #" + itemId.ToString(CultureInfo.InvariantCulture));
            }

            var storage = target.Storage;
            var oldType = storage.type;
            var oldFilters = new int[storage.size];
            for (int i = 0; i < storage.size; i++)
                oldFilters[i] = storage.grids[i].filter;

            storage.type = itemId > 0 ? EStorageType.Filtered : EStorageType.Default;
            int changed = 0;
            for (int i = 0; i < storage.size; i++)
            {
                if (itemId > 0 && storage.grids[i].count > 0 &&
                    storage.grids[i].itemId > 0 && storage.grids[i].itemId != itemId)
                    continue; // 与游戏 UI 一致：已占用且物品不同的格子不覆盖
                var before = storage.grids[i].filter;
                storage.SetFilter(i, itemId);
                if (storage.grids[i].filter != before) changed++;
            }
            storage.NotifyStorageChange();

            WriteActionStack.Push("恢复存储 #" + target.Entity.id.ToString(CultureInfo.InvariantCulture) + " 的过滤器", () =>
            {
                if (storage.grids == null || storage.grids.Length != oldFilters.Length) return;
                for (int i = 0; i < storage.size; i++)
                    storage.SetFilter(i, oldFilters[i]);
                storage.type = oldType;
                storage.NotifyStorageChange();
            });

            var data = JsonValue.NewObject();
            data.Set("buildingId", target.Entity.id);
            data.Set("itemId", itemId);
            data.Set("itemName", itemId > 0 ? GameToolFormat.ItemName(itemId) : "清除");
            data.Set("changedGrids", changed);
            data.Set("slots", storage.size);
            data.Set("undoable", true);
            return Ok(itemId > 0
                ? "已把存储 #" + target.Entity.id.ToString(CultureInfo.InvariantCulture) + " 的过滤器设为 "
                  + GameToolFormat.ItemName(itemId) + "（" + changed.ToString(CultureInfo.InvariantCulture) + " 格）"
                : "已清除存储 #" + target.Entity.id.ToString(CultureInfo.InvariantCulture) + " 的过滤器", data);
        }
    }

    internal sealed class StorageSetLimitTool : StorageWriteToolBase
    {
        public override string Name => "game_storage_set_limit";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            var target = ResolveTarget(args);
            var storage = target.Storage;
            var limit = args["limit"].AsInt(-1);
            if (limit < 0 || limit > storage.size)
                throw Fail("limit 必须在 0.." + storage.size.ToString(CultureInfo.InvariantCulture) + " 之间");
            var oldBans = storage.bans;
            storage.SetBans(storage.size - limit);
            storage.NotifyStorageChange();

            WriteActionStack.Push("恢复存储 #" + target.Entity.id.ToString(CultureInfo.InvariantCulture) + " 的容量上限", () =>
            {
                storage.SetBans(oldBans);
                storage.NotifyStorageChange();
            });

            var data = JsonValue.NewObject();
            data.Set("buildingId", target.Entity.id);
            data.Set("limit", limit);
            data.Set("slots", storage.size);
            data.Set("bans", storage.bans);
            data.Set("undoable", true);
            return Ok("已把存储 #" + target.Entity.id.ToString(CultureInfo.InvariantCulture)
                + " 的容量上限设为 " + limit.ToString(CultureInfo.InvariantCulture)
                + "/" + storage.size.ToString(CultureInfo.InvariantCulture) + " 格", data);
        }
    }

    /// <summary>
    /// 在两个存储之间移动指定物品。先做目标容量/过滤预检，再 TakeItem + AddItem；
    /// 目标放不下时把剩余放回源存储。只支持 Default/Filtered 存储。
    /// </summary>
    internal sealed class StorageMoveTool : StorageWriteToolBase
    {
        public override string Name => "game_storage_move";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null)
                throw Fail("当前不在游戏内");
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null || planet.factory == null)
                throw Fail("目标行星的工厂尚未加载");
            var sourceId = args["sourceId"].AsInt(0);
            var targetId = args["targetId"].AsInt(0);
            if (sourceId == targetId) throw Fail("sourceId 和 targetId 不能相同");
            var source = ResolveStorage(planet.factory, sourceId);
            var target = ResolveStorage(planet.factory, targetId);
            var itemId = args["itemId"].AsInt(0);
            var count = args["count"].AsInt(0);
            if (itemId <= 0) throw Fail("itemId 必须是正整数");
            if (count <= 0) throw Fail("count 必须是正整数");

            var item = LDB.items != null ? LDB.items.Select(itemId) : null;
            if (item == null) throw Fail("找不到物品 #" + itemId.ToString(CultureInfo.InvariantCulture));
            var available = 0L;
            for (int i = 0; i < source.Storage.size; i++)
                if (source.Storage.grids[i].itemId == itemId) available += source.Storage.grids[i].count;
            if (available <= 0)
                throw Fail("源存储里没有物品 " + GameToolFormat.ItemName(itemId));

            // 计算目标能接收多少；过滤/类型不匹配的格子跳过。
            var free = 0L;
            for (int i = 0; i < target.Storage.size; i++)
            {
                var grid = target.Storage.grids[i];
                if (grid.itemId == itemId)
                {
                    free += Math.Max(0, grid.stackSize - grid.count);
                }
                else if (grid.itemId == 0)
                {
                    if (target.Storage.type == EStorageType.Filtered && grid.filter > 0 && grid.filter != itemId)
                        continue;
                    var stack = grid.stackSize > 0 ? grid.stackSize : item.StackSize;
                    free += Math.Max(0, stack);
                }
            }
            if (free <= 0)
                throw Fail("目标存储没有空间接收该物品（检查过滤器/容量上限）");
            if (free < count)
                throw Fail("目标存储空间不足：可接收 " + free.ToString(CultureInfo.InvariantCulture)
                    + "，请求 " + count.ToString(CultureInfo.InvariantCulture));

            var take = source.Storage.TakeItem(itemId, count, out var inc);
            if (take <= 0) throw Fail("源存储没有可移动的该物品");
            var added = target.Storage.AddItem(itemId, take, inc, out var remainInc);
            if (added < take)
            {
                var restored = source.Storage.AddItem(itemId, take - added, remainInc, out _);
                if (restored < take - added)
                    throw Fail("目标存储只接收了部分物品，且剩余无法放回源存储；请检查存储状态");
                added = take - (take - added);
            }
            source.Storage.NotifyStorageChange();
            target.Storage.NotifyStorageChange();

            WriteActionStack.Push("把 " + added.ToString(CultureInfo.InvariantCulture) + " 个 "
                + GameToolFormat.ItemName(itemId) + " 移回存储 #" + sourceId.ToString(CultureInfo.InvariantCulture), () =>
            {
                var back = target.Storage.TakeItem(itemId, added, out var backInc);
                if (back > 0) source.Storage.AddItem(itemId, back, backInc, out _);
                source.Storage.NotifyStorageChange();
                target.Storage.NotifyStorageChange();
            });

            var data = JsonValue.NewObject();
            data.Set("sourceId", sourceId);
            data.Set("targetId", targetId);
            data.Set("itemId", itemId);
            data.Set("itemName", GameToolFormat.ItemName(itemId));
            data.Set("requested", count);
            data.Set("moved", added);
            data.Set("remaining", Math.Max(0, count - added));
            data.Set("undoable", true);
            return Ok("已从存储 #" + sourceId.ToString(CultureInfo.InvariantCulture) + " 移动 "
                + added.ToString(CultureInfo.InvariantCulture) + " 个 " + GameToolFormat.ItemName(itemId)
                + " 到 #" + targetId.ToString(CultureInfo.InvariantCulture), data);
        }
    }

}

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 背包 ↔ 存储：放入/取出。走游戏自己的 package.TakeTailItems / TryAddItemToPackage +
    /// StorageComponent.AddItem/TakeItem；目标满时把剩余退回，不制造物品。
    /// </summary>
    internal sealed class StorageDepositTool : StorageWriteToolBase
    {
        public override string Name => "game_storage_deposit";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            var target = ResolveTarget(args);
            var itemId = args["itemId"].AsInt(0);
            var count = args["count"].AsInt(0);
            if (itemId <= 0) throw Fail("itemId 必须是正整数");
            if (count <= 0) throw Fail("count 必须是正整数");
            if (LDB.items == null || LDB.items.Select(itemId) == null)
                throw Fail("找不到物品 #" + itemId.ToString(CultureInfo.InvariantCulture));

            var player = GameMain.mainPlayer;
            var takeId = itemId;
            var takeCount = count;
            player.TakeItemFromPlayer(ref takeId, ref takeCount, out var inc, true, null);
            if (takeCount <= 0) throw Fail("背包里没有该物品");

            var added = target.Storage.AddItem(itemId, takeCount, inc, out var remainInc);
            if (added < takeCount)
            {
                var back = player.TryAddItemToPackage(itemId, takeCount - added, remainInc, false, target.Entity.id);
                if (back < takeCount - added)
                    throw Fail("存储已满，且剩余物品无法放回背包；请先整理背包");
            }
            target.Storage.NotifyStorageChange();

            var moved = added;
            WriteActionStack.Push("从存储 #" + target.Entity.id.ToString(CultureInfo.InvariantCulture)
                + " 取回 " + moved.ToString(CultureInfo.InvariantCulture) + " 个 "
                + GameToolFormat.ItemName(itemId), () =>
            {
                var take = target.Storage.TakeItem(itemId, moved, out var backInc);
                if (take > 0) player.TryAddItemToPackage(itemId, take, backInc, false, target.Entity.id);
                target.Storage.NotifyStorageChange();
            });

            var data = JsonValue.NewObject();
            data.Set("buildingId", target.Entity.id);
            data.Set("itemId", itemId);
            data.Set("itemName", GameToolFormat.ItemName(itemId));
            data.Set("requested", count);
            data.Set("stored", added);
            data.Set("remaining", takeCount - added);
            data.Set("undoable", true);
            return Ok("已把 " + added.ToString(CultureInfo.InvariantCulture) + " 个 "
                + GameToolFormat.ItemName(itemId) + " 放入存储 #"
                + target.Entity.id.ToString(CultureInfo.InvariantCulture), data);
        }
    }

    internal sealed class StorageWithdrawTool : StorageWriteToolBase
    {
        public override string Name => "game_storage_withdraw";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            var target = ResolveTarget(args);
            var itemId = args["itemId"].AsInt(0);
            var count = args["count"].AsInt(0);
            if (itemId <= 0) throw Fail("itemId 必须是正整数");
            if (count <= 0) throw Fail("count 必须是正整数");

            var taken = target.Storage.TakeItem(itemId, count, out var inc);
            if (taken <= 0) throw Fail("存储里没有该物品");
            var added = GameMain.mainPlayer.TryAddItemToPackage(itemId, taken, inc, false, target.Entity.id);
            if (added < taken)
            {
                var back = target.Storage.AddItem(itemId, taken - added, 0, out _);
                if (back < taken - added)
                    throw Fail("背包已满，且剩余物品无法放回存储；请先整理背包");
            }
            target.Storage.NotifyStorageChange();

            var moved = added;
            WriteActionStack.Push("把 " + moved.ToString(CultureInfo.InvariantCulture) + " 个 "
                + GameToolFormat.ItemName(itemId) + " 放回存储 #"
                + target.Entity.id.ToString(CultureInfo.InvariantCulture), () =>
            {
                var takeId = itemId;
                var takeCount = moved;
                GameMain.mainPlayer.TakeItemFromPlayer(ref takeId, ref takeCount, out var backInc, true, null);
                if (takeCount > 0) target.Storage.AddItem(itemId, takeCount, backInc, out _);
                target.Storage.NotifyStorageChange();
            });

            var data = JsonValue.NewObject();
            data.Set("buildingId", target.Entity.id);
            data.Set("itemId", itemId);
            data.Set("itemName", GameToolFormat.ItemName(itemId));
            data.Set("requested", count);
            data.Set("taken", added);
            data.Set("remaining", taken - added);
            data.Set("undoable", true);
            return Ok("已从存储 #" + target.Entity.id.ToString(CultureInfo.InvariantCulture)
                + " 取出 " + added.ToString(CultureInfo.InvariantCulture) + " 个 "
                + GameToolFormat.ItemName(itemId), data);
        }
    }
}
