using System;
using System.Collections.Generic;
using System.Reflection;
using System.Globalization;
using IcarusII.Common.Json;
using IcarusII.Game;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 建筑与进度查询（协议附录 B 的 `game.entity.v1` / `game.progress.v1`）。
    ///
    /// 两个共同的设计约束：
    /// 1. **实体 id 会被回收**（`PlanetFactory.entityRecycle`）：`entityPool[id].id != id`
    ///    就说明这个 id 已经指向别的建筑或已空，必须报 `stale` 而不是返回错数据；
    /// 2. 只读公开字段，不做 Harmony 补丁；所有访问都做空值与越界保护，
    ///    远程行星（factory 未加载）返回 `available=false` 而不是异常。
    /// </summary>
    internal static class EntityToolUtil
    {
        public static PlanetFactory ResolveFactory(JsonValue args, out PlanetData planet)
        {
            planet = GameToolFormat.ResolvePlanet(args);
            return planet != null ? planet.factory : null;
        }

        public static bool HasEntity(PlanetFactory factory, int entityId)
        {
            return factory != null
                && factory.entityPool != null
                && entityId > 0
                && entityId < factory.entityCursor
                && entityId < factory.entityPool.Length;
        }

        /// <summary>按组件索引判断建筑类别；顺序决定"一个实体带多种组件"时优先报哪个。</summary>
        public static string Category(EntityData e)
        {
            if (e.assemblerId > 0) return "assembler";
            if (e.minerId > 0) return "miner";
            if (e.inserterId > 0) return "inserter";
            if (e.beltId > 0) return "belt";
            if (e.stationId > 0) return "station";
            if (e.labId > 0) return "lab";
            if (e.fractionatorId > 0) return "fractionator";
            if (e.ejectorId > 0) return "ejector";
            if (e.siloId > 0) return "silo";
            if (e.spraycoaterId > 0) return "spray_coater";
            if (e.splitterId > 0) return "splitter";
            if (e.pilerId > 0) return "piler";
            if (e.monitorId > 0) return "monitor";
            if (e.storageId > 0) return "storage";
            if (e.tankId > 0) return "tank";
            if (e.powerGenId > 0) return "power_generator";
            if (e.powerNodeId > 0) return "power_node";
            if (e.powerAccId > 0) return "power_accumulator";
            if (e.powerExcId > 0) return "power_exchanger";
            if (e.dispenserId > 0) return "dispenser";
            if (e.beaconId > 0) return "beacon";
            if (e.speakerId > 0) return "speaker";
            if (e.warningId > 0) return "warning";
            if (e.turretId > 0) return "turret";
            if (e.fieldGenId > 0) return "field_generator";
            if (e.battleBaseId > 0) return "battle_base";
            if (e.constructionModuleId > 0) return "construction_module";
            if (e.combatModuleId > 0) return "combat_module";
            return "other";
        }

        public static string ProtoName(int protoId)
        {
            try
            {
                var proto = LDB.items != null ? LDB.items.Select(protoId) : null;
                if (proto != null && !string.IsNullOrEmpty(proto.name)) return proto.name;
            }
            catch { /* LDB 尚未加载 */ }
            return GameToolFormat.ItemName(protoId);
        }

        public static string RecipeName(int recipeId)
        {
            if (recipeId <= 0) return string.Empty;
            try
            {
                var proto = LDB.recipes != null ? LDB.recipes.Select(recipeId) : null;
                if (proto != null && !string.IsNullOrEmpty(proto.name)) return proto.name;
            }
            catch { /* ignore */ }
            return "#" + recipeId.ToString(CultureInfo.InvariantCulture);
        }

        public static double RecipeSeconds(int recipeId)
        {
            if (recipeId <= 0) return 0;
            try
            {
                var proto = LDB.recipes != null ? LDB.recipes.Select(recipeId) : null;
                if (proto != null && proto.TimeSpend > 0) return proto.TimeSpend / 60.0;
            }
            catch { /* ignore */ }
            return 0;
        }

        /// <summary>建筑功率：耗电与发电都按 kW 输出（与 M2 单位约定一致）。</summary>
        public static JsonValue PowerOf(PlanetFactory factory, EntityData entity)
        {
            var power = JsonValue.NewObject();
            var system = factory != null ? factory.powerSystem : null;
            if (system == null) return power;

            if (entity.powerConId > 0 && system.consumerPool != null &&
                entity.powerConId < system.consumerPool.Length)
            {
                var consumer = system.consumerPool[entity.powerConId];
                power.Set("consumerId", entity.powerConId);
                power.Set("networkId", consumer.networkId);
                power.Set("requiredKw", GameToolFormat.Round(GameToolFormat.Kw(consumer.requiredEnergy)));
                power.Set("workKw", GameToolFormat.Round(GameToolFormat.Kw(consumer.workEnergyPerTick)));
                power.Set("idleKw", GameToolFormat.Round(GameToolFormat.Kw(consumer.idleEnergyPerTick)));
            }

            if (entity.powerGenId > 0 && system.genPool != null &&
                entity.powerGenId < system.genPool.Length)
            {
                var gen = system.genPool[entity.powerGenId];
                power.Set("networkId", gen.networkId);
                power.Set("genKw", GameToolFormat.Round(GameToolFormat.Kw(gen.genEnergyPerTick)));
                power.Set("fuelKw", GameToolFormat.Round(GameToolFormat.Kw(gen.useFuelPerTick)));
                power.Set("photovoltaic", gen.photovoltaic);
                power.Set("wind", gen.wind);
                power.Set("geothermal", gen.geothermal);
            }

            if (entity.powerAccId > 0 && system.accPool != null &&
                entity.powerAccId < system.accPool.Length)
            {
                var acc = system.accPool[entity.powerAccId];
                power.Set("networkId", acc.networkId);
            }

            if (entity.powerNodeId > 0 && system.nodePool != null &&
                entity.powerNodeId < system.nodePool.Length)
            {
                power.Set("networkId", system.nodePool[entity.powerNodeId].networkId);
            }

            return power;
        }

        public static JsonValue Vec3(Vector3 value)
        {
            return GameToolFormat.Vec3(value);
        }
    }

    // ---------------------------------------------------------------- 单体建筑

    internal sealed class EntityTool : IGameToolHandler
    {
        public string Name => "game_get_entity";
        public int TimeoutMs => 6000;

        public IGameToolJob Start(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");

            var entityId = args["entityId"].AsInt(0);
            if (entityId <= 0) throw new ArgumentException("entityId 必须是正整数");

            var expectProtoId = args["expectProtoId"].AsInt(0);
            return new OneShotJob(() => Build(planet, entityId, expectProtoId));
        }

        private static GameToolJobResult Build(PlanetData planet, int entityId, int expectProtoId)
        {
            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);
            data.Set("entityId", entityId);

            var factory = planet.factory;
            if (factory == null || factory.entityPool == null)
            {
                data.Set("available", false);
                data.Set("reason", "factory_not_loaded");
                data.Set("display", "该行星的工厂尚未加载（远程行星只能看矿脉）");
                return new GameToolJobResult { Data = data };
            }

            if (!EntityToolUtil.HasEntity(factory, entityId))
            {
                data.Set("available", false);
                data.Set("reason", "not_found");
                data.Set("display", "没有这个 id 的建筑");
                return new GameToolJobResult { Data = data };
            }

            var entity = factory.entityPool[entityId];
            if (entity.id != entityId || entity.protoId <= 0)
            {
                data.Set("available", false);
                data.Set("reason", "empty_slot");
                data.Set("display", "该 id 目前没有建筑（可能已被拆除）");
                return new GameToolJobResult { Data = data };
            }

            data.Set("available", true);
            data.Set("protoId", entity.protoId);
            data.Set("protoName", EntityToolUtil.ProtoName(entity.protoId));
            data.Set("iconTag", GameToolFormat.ItemIconTag(entity.protoId));
            data.Set("category", EntityToolUtil.Category(entity));
            data.Set("pos", EntityToolUtil.Vec3(entity.pos));
            data.Set("yaw", GameToolFormat.Round(entity.rot.eulerAngles.y, 1));
            data.Set("power", EntityToolUtil.PowerOf(factory, entity));

            // 回收检测：调用方带了期望原型时，不匹配就必须显式告知，避免模型拿错数据。
            if (expectProtoId > 0 && expectProtoId != entity.protoId)
            {
                data.Set("stale", true);
                data.Set("expectProtoId", expectProtoId);
            }

            AppendCategoryDetail(data, factory, entity);
            AppendComponents(data, factory, entity);
            data.Set("display", BuildDisplay(data));
            return new GameToolJobResult { Data = data };
        }

        private static void AppendCategoryDetail(JsonValue data, PlanetFactory factory, EntityData entity)
        {
            var system = factory.factorySystem;

            if (entity.assemblerId > 0 && system != null && system.assemblerPool != null &&
                entity.assemblerId < system.assemblerPool.Length)
            {
                var assembler = system.assemblerPool[entity.assemblerId];
                var detail = JsonValue.NewObject();
                detail.Set("recipeId", assembler.recipeId);
                detail.Set("recipeName", EntityToolUtil.RecipeName(assembler.recipeId));
                detail.Set("recipeSeconds", EntityToolUtil.RecipeSeconds(assembler.recipeId));
                // assembler.speed 是 10000 为 1x 的内联比例。
                detail.Set("speedMultiplier", GameToolFormat.Round(assembler.speed / 10000.0, 2));
                var seconds = EntityToolUtil.RecipeSeconds(assembler.recipeId);
                detail.Set("craftsPerMin", seconds > 0
                    ? GameToolFormat.Round(3600.0 / seconds * (assembler.speed / 10000.0), 2)
                    : 0);
                detail.Set("cycleCount", assembler.cycleCount);
                detail.Set("extraCycleCount", assembler.extraCycleCount);
                detail.Set("working", assembler.time > 0);
                detail.Set("replicating", assembler.replicating);
                detail.Set("extraSpeed", assembler.extraSpeed);
                // 与 UIAssemblerWindow 同源：每台机器当前的每分钟产出（含增产剂加速）。
                double extraRatio = assembler.speedOverride > 0
                    ? 1.0 + assembler.extraSpeed * 0.1 / assembler.speedOverride
                    : 1.0;
                double perMachine = seconds > 0 ? 3600.0 / seconds * (assembler.speed / 10000.0) : 0;
                detail.Set("productPerMin", assembler.replicating
                    ? GameToolFormat.Round(perMachine * extraRatio, 2)
                    : 0);
                detail.Set("forceAccMode", assembler.forceAccMode);

                var recipe = assembler.recipeId > 0 && LDB.recipes != null
                    ? LDB.recipes.Select(assembler.recipeId) : null;
                var inputs = JsonValue.NewArray();
                var missing = false;
                if (recipe != null && recipe.Items != null)
                {
                    for (int i = 0; i < recipe.Items.Length; i++)
                    {
                        var itemId = recipe.Items[i];
                        if (itemId <= 0) continue;
                        var row = JsonValue.NewObject();
                        row.Set("itemId", itemId);
                        row.Set("name", EntityToolUtil.ProtoName(itemId));
                        row.Set("iconTag", GameToolFormat.ItemIconTag(itemId));
                        int served = assembler.served != null && i < assembler.served.Length ? assembler.served[i] : 0;
                        int needs = assembler.needs != null && i < assembler.needs.Length ? assembler.needs[i] : 0;
                        int incServed = assembler.incServed != null && i < assembler.incServed.Length ? assembler.incServed[i] : 0;
                        row.Set("count", served);
                        row.Set("needed", needs);
                        row.Set("incServed", incServed);
                        if (served < needs) missing = true;
                        inputs.Add(row);
                    }
                }
                detail.Set("inputs", inputs);

                var outputs = JsonValue.NewArray();
                if (recipe != null && recipe.Results != null)
                {
                    for (int i = 0; i < recipe.Results.Length; i++)
                    {
                        var itemId = recipe.Results[i];
                        if (itemId <= 0) continue;
                        var row = JsonValue.NewObject();
                        row.Set("itemId", itemId);
                        row.Set("name", EntityToolUtil.ProtoName(itemId));
                        row.Set("iconTag", GameToolFormat.ItemIconTag(itemId));
                        row.Set("count", assembler.produced != null && i < assembler.produced.Length ? assembler.produced[i] : 0);
                        outputs.Add(row);
                    }
                }
                detail.Set("outputs", outputs);

                var powerPct = NetworkSatisfactionPct(factory, ConsumerNetworkId(factory, entity));
                detail.Set("powerSatisfactionPct", powerPct);
                string status;
                if (assembler.time > 0) status = "working";
                else if (missing) status = "missing_input";
                else if (powerPct >= 0 && powerPct < 99) status = "no_power";
                else status = "idle";
                detail.Set("status", status);
                data.Set("assembler", detail);
            }

            if (entity.minerId > 0 && system != null && system.minerPool != null &&
                entity.minerId < system.minerPool.Length)
            {
                var miner = system.minerPool[entity.minerId];
                var detail = JsonValue.NewObject();
                detail.Set("productId", miner.productId);
                detail.Set("productName", EntityToolUtil.ProtoName(miner.productId));
                detail.Set("productCount", miner.productCount);
                detail.Set("period", miner.period);
                detail.Set("orePerMin", miner.period > 0
                    ? GameToolFormat.Round(miner.productCount * 3600.0 / miner.period, 1)
                    : 0);
                detail.Set("veinCount", miner.veinCount);
                detail.Set("totalVeinAmount", miner.totalVeinAmount);
                detail.Set("workstate", miner.workstate.ToString());
                data.Set("miner", detail);
            }

            if (entity.inserterId > 0 && system != null && system.inserterPool != null &&
                entity.inserterId < system.inserterPool.Length)
            {
                var inserter = system.inserterPool[entity.inserterId];
                var detail = JsonValue.NewObject();
                detail.Set("grade", (int)inserter.grade);
                detail.Set("bidirectional", inserter.bidirectional);
                detail.Set("stage", inserter.stage.ToString());
                detail.Set("filterItemId", inserter.filter);
                detail.Set("filterItemName", inserter.filter > 0 ? EntityToolUtil.ProtoName(inserter.filter) : string.Empty);
                detail.Set("pickTarget", inserter.pickTarget);
                detail.Set("insertTarget", inserter.insertTarget);
                detail.Set("pickTargetName", TargetName(factory, inserter.pickTarget));
                detail.Set("insertTargetName", TargetName(factory, inserter.insertTarget));
                detail.Set("handItemId", inserter.itemId);
                detail.Set("handItemName", inserter.itemId > 0 ? EntityToolUtil.ProtoName(inserter.itemId) : string.Empty);
                detail.Set("handItemCount", (int)inserter.itemCount);
                detail.Set("handItemInc", (int)inserter.itemInc);
                detail.Set("stackCount", inserter.stackCount);
                detail.Set("canStack", inserter.canStack);
                detail.Set("stackInput", (int)inserter.stackInput);
                detail.Set("stackOutput", (int)inserter.stackOutput);
                detail.Set("delay", inserter.delay);
                data.Set("inserter", detail);
            }

            if (entity.beltId > 0 && factory.cargoTraffic != null &&
                factory.cargoTraffic.beltPool != null &&
                entity.beltId < factory.cargoTraffic.beltPool.Length)
            {
                var belt = factory.cargoTraffic.beltPool[entity.beltId];
                var detail = JsonValue.NewObject();
                detail.Set("itemsPerSec", GameToolFormat.Round(belt.speed * 60.0 / CargoPath.kCargoLength, 2));
                detail.Set("segLength", belt.segLength);
                detail.Set("rangeFrom", belt.segIndex);
                detail.Set("rangeTo", belt.segIndex + belt.segLength - 1);
                detail.Set("nodeIndex", belt.segIndex + belt.segPivotOffset);
                detail.Set("pathId", belt.segPathId);
                var traffic = factory.cargoTraffic;
                if (traffic != null && traffic.pathPool != null && belt.segPathId > 0 &&
                    belt.segPathId < traffic.pathPool.Length && traffic.pathPool[belt.segPathId] != null)
                {
                    var path = traffic.pathPool[belt.segPathId];
                    detail.Set("pathCapacity", path.cargoCapacity);
                    detail.Set("pathNodeCount", path.belts != null ? path.belts.Count : 0);
                    int cargoCount = 0;
                    if (path.buffer != null)
                    {
                        for (int i = 0; i < path.pathLength; i++)
                        {
                            if (path.buffer[i] != CargoPath.kCargoHead) continue;
                            cargoCount++;
                            i += CargoPath.kCargoLength - 1;
                        }
                    }
                    detail.Set("pathCargoCount", cargoCount);
                    detail.Set("cargoItems", CargoItems(path, 8));
                }
                detail.Set("outputId", belt.outputId);
                detail.Set("backInputId", belt.backInputId);
                detail.Set("leftInputId", belt.leftInputId);
                detail.Set("rightInputId", belt.rightInputId);
                data.Set("belt", detail);
            }

            if (entity.storageId > 0 && factory.factoryStorage != null &&
                factory.factoryStorage.storagePool != null &&
                entity.storageId < factory.factoryStorage.storagePool.Length)
            {
                var storage = factory.factoryStorage.storagePool[entity.storageId];
                var storageDetail = JsonValue.NewObject();
                storageDetail.Set("slots", storage.grids != null ? storage.grids.Length : 0);
                storageDetail.Set("items", StorageItems(storage, 12));
                int total = 0;
                if (storage.grids != null)
                    for (int i = 0; i < storage.grids.Length; i++)
                        total += storage.grids[i].count;
                storageDetail.Set("totalCount", total);
                data.Set("storage", storageDetail);
            }

            if (entity.stationId > 0 && factory.transport != null &&
                factory.transport.stationPool != null &&
                entity.stationId < factory.transport.stationPool.Length)
            {
                var station = factory.transport.stationPool[entity.stationId];
                var detail = JsonValue.NewObject();
                detail.Set("isCollector", station.isCollector);
                detail.Set("isStellar", station.isStellar);
                detail.Set("warperCount", station.warperCount);
                detail.Set("warperMaxCount", station.warperMaxCount);
                detail.Set("idleDroneCount", station.idleDroneCount);
                detail.Set("workDroneCount", station.workDroneCount);
                detail.Set("idleShipCount", station.idleShipCount);
                detail.Set("workShipCount", station.workShipCount);
                detail.Set("deliveryDrones", station.deliveryDrones);
                detail.Set("deliveryShips", station.deliveryShips);
                detail.Set("warpEnableDistAu", GameToolFormat.Round(station.warpEnableDist / GalaxyData.AU, 1));
                detail.Set("energy", station.energy);
                detail.Set("energyMax", station.energyMax);
                detail.Set("chargeKw", GameToolFormat.Round(GameToolFormat.Kw(station.energyPerTick)));
                detail.Set("charging", station.energy < station.energyMax);
                detail.Set("storage", StationItems(station, 12));
                data.Set("station", detail);
            }
        }

        private static string TargetName(PlanetFactory factory, int entityId)
        {
            if (!EntityToolUtil.HasEntity(factory, entityId)) return string.Empty;
            var target = factory.entityPool[entityId];
            if (target.id != entityId || target.protoId <= 0) return string.Empty;
            return EntityToolUtil.ProtoName(target.protoId);
        }

        private static JsonValue StorageItems(StorageComponent storage, int max)
        {
            var items = JsonValue.NewArray();
            if (storage == null || storage.grids == null) return items;

            var counts = new Dictionary<int, int>();
            for (int i = 0; i < storage.grids.Length; i++)
            {
                var grid = storage.grids[i];
                if (grid.itemId <= 0 || grid.count <= 0) continue;
                int current;
                counts.TryGetValue(grid.itemId, out current);
                counts[grid.itemId] = current + grid.count;
            }

            var rows = new List<KeyValuePair<int, int>>(counts);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < rows.Count && i < max; i++)
            {
                var row = JsonValue.NewObject();
                row.Set("itemId", rows[i].Key);
                row.Set("name", EntityToolUtil.ProtoName(rows[i].Key));
                row.Set("iconTag", GameToolFormat.ItemIconTag(rows[i].Key));
                row.Set("count", rows[i].Value);
                items.Add(row);
            }
            return items;
        }

        private static JsonValue StationItems(StationComponent station, int max)
        {
            var items = JsonValue.NewArray();
            if (station == null || station.storage == null) return items;

            var rows = new List<StationStore>();
            for (int i = 0; i < station.storage.Length; i++)
            {
                var store = station.storage[i];
                if (store.itemId <= 0) continue;
                rows.Add(store);
            }
            rows.Sort((a, b) => b.count.CompareTo(a.count));

            for (int i = 0; i < rows.Count && i < max; i++)
            {
                var store = rows[i];
                var row = JsonValue.NewObject();
                row.Set("itemId", store.itemId);
                row.Set("name", EntityToolUtil.ProtoName(store.itemId));
                row.Set("iconTag", GameToolFormat.ItemIconTag(store.itemId));
                row.Set("count", store.count);
                row.Set("inTransit", store.inc);
                row.Set("max", store.max);
                row.Set("localOrder", store.localOrder);
                row.Set("remoteOrder", store.remoteOrder);
                row.Set("localLogic", store.localLogic.ToString());
                row.Set("remoteLogic", store.remoteLogic.ToString());
                items.Add(row);
            }
            return items;
        }

        /// <summary>
        /// 传送带线路上的货物聚合：解码 CargoPath.buffer 的 cargo 头，
        /// 和游戏传送带面板的“线路货物数”同源。
        /// </summary>
        private static JsonValue CargoItems(CargoPath path, int max)
        {
            var rows = JsonValue.NewArray();
            if (path == null || path.buffer == null || path.cargoContainer == null) return rows;
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < path.pathLength; i++)
            {
                if (path.buffer[i] != CargoPath.kCargoHead) continue;
                int index = i + (CargoPath.kCargoSign - path.buffer[i]);
                if (index + 4 >= path.pathLength) continue;
                int cargoId = (path.buffer[index + 1] - 1)
                    + (path.buffer[index + 2] - 1) * 100
                    + (path.buffer[index + 3] - 1) * 10000
                    + (path.buffer[index + 4] - 1) * 1000000;
                if (cargoId < 0 || cargoId >= path.cargoContainer.cargoPool.Length) continue;
                var cargo = path.cargoContainer.cargoPool[cargoId];
                if (cargo.item <= 0) continue;
                int current;
                counts.TryGetValue(cargo.item, out current);
                counts[cargo.item] = current + cargo.stack;
                i += CargoPath.kCargoLength - 1;
            }
            var list = new List<KeyValuePair<int, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < list.Count && i < max; i++)
            {
                var row = JsonValue.NewObject();
                row.Set("itemId", list[i].Key);
                row.Set("name", EntityToolUtil.ProtoName(list[i].Key));
                row.Set("iconTag", GameToolFormat.ItemIconTag(list[i].Key));
                row.Set("count", list[i].Value);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// 所有建筑的通用组件快照：按 EntityData 的组件 id 找到对应 pool，
        /// 反射导出公有字段（最多 30 个）。新增建筑类型不需要再改这里。
        /// </summary>
        private static void AppendComponents(JsonValue data, PlanetFactory factory, EntityData e)
        {
            var components = JsonValue.NewObject();
            var system = factory.factorySystem;
            var traffic = factory.cargoTraffic;
            var storage = factory.factoryStorage;
            var transport = factory.transport;
            var power = factory.powerSystem;
            var digital = factory.digitalSystem;
            if (system != null)
            {
                // assembler/miner/inserter/belt/storage/station 已有专门字段，避免重复占 payload。
                AddComponent(components, "fractionator", e.fractionatorId, system.fractionatorPool);
                AddComponent(components, "ejector", e.ejectorId, system.ejectorPool);
                AddComponent(components, "silo", e.siloId, system.siloPool);
                AddComponent(components, "lab", e.labId, system.labPool);
            }
            if (traffic != null)
            {
                AddComponent(components, "splitter", e.splitterId, traffic.splitterPool);
                AddComponent(components, "monitor", e.monitorId, traffic.monitorPool);
                AddComponent(components, "spraycoater", e.spraycoaterId, traffic.spraycoaterPool);
                AddComponent(components, "piler", e.pilerId, traffic.pilerPool);
            }
            if (storage != null)
            {
                AddComponent(components, "tank", e.tankId, storage.tankPool);
            }
            if (transport != null)
            {
                AddComponent(components, "dispenser", e.dispenserId, transport.dispenserPool);
            }
            if (power != null)
            {
                AddComponent(components, "power_consumer", e.powerConId, power.consumerPool);
                AddComponent(components, "power_generator", e.powerGenId, power.genPool);
                AddComponent(components, "power_accumulator", e.powerAccId, power.accPool);
                AddComponent(components, "power_exchanger", e.powerExcId, power.excPool);
                AddComponent(components, "power_node", e.powerNodeId, power.nodePool);
            }
            if (digital != null)
            {
                AddComponent(components, "speaker", e.speakerId, digital.speakerPool);
            }
            var defense = factory.defenseSystem;
            if (defense != null)
            {
                AddComponent(components, "turret", e.turretId, defense.turrets != null ? defense.turrets.buffer : null);
                AddComponent(components, "beacon", e.beaconId, defense.beacons != null ? defense.beacons.buffer : null);
                AddComponent(components, "field_generator", e.fieldGenId, defense.fieldGenerators != null ? defense.fieldGenerators.buffer : null);
                AddComponent(components, "battle_base", e.battleBaseId, defense.battleBases != null ? defense.battleBases.buffer : null);
            }
            if (components.Count > 0) data.Set("components", components);
        }

        private static void AddComponent(JsonValue parent, string kind, int id, Array pool)
        {
            if (id <= 0 || pool == null || id >= pool.Length) return;
            var component = pool.GetValue(id);
            if (component == null) return;
            var row = JsonValue.NewObject();
            row.Set("kind", kind);
            row.Set("poolId", id);
            row.Set("fields", DumpPublicFields(component));
            parent.Set(kind, row);
        }

        private static JsonValue DumpPublicFields(object component)
        {
            var fields = JsonValue.NewObject();
            var type = component.GetType();
            var infos = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            int emitted = 0;
            foreach (var info in infos)
            {
                if (emitted >= 30) break;
                object value;
                try { value = info.GetValue(component); }
                catch { continue; }
                if (value == null) continue;

                var t = value.GetType();
                if (value is bool b) { fields.Set(info.Name, b); emitted++; }
                else if (value is int i) { fields.Set(info.Name, i); emitted++; }
                else if (value is long l) { fields.Set(info.Name, l); emitted++; }
                else if (value is short s) { fields.Set(info.Name, (int)s); emitted++; }
                else if (value is byte by) { fields.Set(info.Name, (int)by); emitted++; }
                else if (value is float f) { fields.Set(info.Name, GameToolFormat.Round(f, 3)); emitted++; }
                else if (value is double d) { fields.Set(info.Name, GameToolFormat.Round(d, 3)); emitted++; }
                else if (value is string str) { fields.Set(info.Name, str); emitted++; }
                else if (t.IsEnum) { fields.Set(info.Name, value.ToString()); emitted++; }
                else if (value is Array array)
                {
                    fields.Set(info.Name + "_length", array.Length);
                    if (array.Length <= 12 && array.Rank == 1 &&
                        (array is int[] || array is short[] || array is byte[] || array is float[]))
                    {
                        var parts = new List<string>();
                        foreach (var item in array) parts.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                        fields.Set(info.Name, string.Join(",", parts));
                        emitted++;
                    }
                }
            }
            return fields;
        }

        private static int ConsumerNetworkId(PlanetFactory factory, EntityData entity)
        {
            var system = factory != null ? factory.powerSystem : null;
            if (system == null || system.consumerPool == null || entity.powerConId <= 0 ||
                entity.powerConId >= system.consumerPool.Length) return 0;
            return system.consumerPool[entity.powerConId].networkId;
        }

        private static double NetworkSatisfactionPct(PlanetFactory factory, int networkId)
        {
            var system = factory != null ? factory.powerSystem : null;
            if (system == null || system.netPool == null || networkId <= 0 ||
                networkId >= system.netPool.Length) return -1;
            var net = system.netPool[networkId];
            if (net == null || net.energyRequired <= 0) return 100;
            return GameToolFormat.Round(net.energyServed * 100.0 / net.energyRequired, 1);
        }

        private static string BuildDisplay(JsonValue data)
        {
            var name = data["protoName"].AsString("建筑");
            var category = data["category"].AsString("other");
            var text = name + "（id " + data["entityId"].AsInt(0) + "，" + category + "）";

            var assembler = data["assembler"];
            if (assembler != null && assembler.IsObject)
            {
                var recipe = assembler["recipeName"].AsString("");
                if (!string.IsNullOrEmpty(recipe)) text += " · 配方 " + recipe;
            }

            var power = data["power"];
            if (power != null && power.IsObject)
            {
                var required = power["requiredKw"].AsDouble(0);
                if (required > 0) text += " · 需电 " + GameToolFormat.PowerDisplay(required);
            }

            if (data["stale"].AsBool(false)) text += " · ⚠ id 已回收";
            return text;
        }
    }

    // ---------------------------------------------------------------- 建筑列表

    internal sealed class EntitiesTool : IGameToolHandler
    {
        public string Name => "game_get_entities";
        public int TimeoutMs => 10000;

        private const int MaxLimit = 200;

        public IGameToolJob Start(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");

            var offset = Math.Max(0, args["offset"].AsInt(0));
            var limit = args["limit"].AsInt(25);
            if (limit <= 0) limit = 25;
            if (limit > MaxLimit) limit = MaxLimit;

            var protoId = args["protoId"].AsInt(0);
            var recipeId = args["recipeId"].AsInt(0);
            var protoName = args["protoName"].AsString("");
            if (protoName != null) protoName = protoName.Trim();

            return new OneShotJob(() => Build(planet, offset, limit, protoId, recipeId, protoName));
        }

        private static GameToolJobResult Build(PlanetData planet, int offset, int limit,
            int protoId, int recipeId, string protoName)
        {
            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);

            var factory = planet.factory;
            if (factory == null || factory.entityPool == null)
            {
                data.Set("available", false);
                data.Set("rows", JsonValue.NewArray());
                data.Set("total", 0);
                data.Set("display", "该行星的工厂尚未加载");
                return new GameToolJobResult { Data = data };
            }

            var system = factory.factorySystem;
            var matches = new List<int>();
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);

            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                if (protoId > 0 && entity.protoId != protoId) continue;

                if (!string.IsNullOrEmpty(protoName))
                {
                    var name = EntityToolUtil.ProtoName(entity.protoId);
                    if (name.IndexOf(protoName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }

                if (recipeId > 0)
                {
                    if (entity.assemblerId <= 0 || system == null || system.assemblerPool == null ||
                        entity.assemblerId >= system.assemblerPool.Length) continue;
                    if (system.assemblerPool[entity.assemblerId].recipeId != recipeId) continue;
                }

                matches.Add(id);
            }

            var total = matches.Count;
            var end = Math.Min(total, offset + limit);
            var rows = JsonValue.NewArray();

            for (int i = offset; i < end; i++)
            {
                var id = matches[i];
                var entity = factory.entityPool[id];
                var row = JsonValue.NewObject();
                row.Set("id", id);
                row.Set("protoId", entity.protoId);
                row.Set("protoName", EntityToolUtil.ProtoName(entity.protoId));
                row.Set("iconTag", GameToolFormat.ItemIconTag(entity.protoId));
                row.Set("category", EntityToolUtil.Category(entity));
                row.Set("pos", EntityToolUtil.Vec3(entity.pos));
                row.Set("yaw", GameToolFormat.Round(entity.rot.eulerAngles.y, 1));

                if (entity.assemblerId > 0 && system != null && system.assemblerPool != null &&
                    entity.assemblerId < system.assemblerPool.Length)
                {
                    var recipe = system.assemblerPool[entity.assemblerId].recipeId;
                    row.Set("recipeId", recipe);
                    row.Set("recipeName", EntityToolUtil.RecipeName(recipe));
                }
                rows.Add(row);
            }

            data.Set("available", true);
            data.Set("rows", rows);
            data.Set("total", total);
            data.Set("offset", offset);
            data.Set("limit", limit);
            data.Set("hasMore", end < total);
            data.Set("display", "匹配 " + total + " 个建筑，本页 " + rows.Count + " 个"
                + (end < total ? "（还有更多，用 offset=" + end + " 继续）" : ""));

            return new GameToolJobResult
            {
                Data = data,
                Truncated = end < total,
                NextOffset = end < total ? end : -1,
            };
        }
    }

    // ---------------------------------------------------------------- 进度阶段

    internal sealed class ProgressTool : IGameToolHandler
    {
        public string Name => "game_get_progress";
        public int TimeoutMs => 6000;

        public IGameToolJob Start(JsonValue args)
        {
            return new OneShotJob(Build);
        }

        private static GameToolJobResult Build()
        {
            var data = JsonValue.NewObject();
            var gameData = GameMain.data;
            var history = gameData != null ? gameData.history : null;

            if (history == null)
            {
                data.Set("display", "进度数据尚未就绪");
                return new GameToolJobResult { Data = data };
            }

            var flags = JsonValue.NewObject();
            flags.Set("logisticsSystem", SafeFlag(() => history.logisticsSystemUnlocked));
            flags.Set("intraplanetaryStation", SafeFlag(() => history.intraplanetaryStationUnlocked));
            flags.Set("interstellarStation", SafeFlag(() => history.interstellarStationUnlocked));
            flags.Set("dispenser", SafeFlag(() => history.dispenserUnlocked));
            flags.Set("foundation", SafeFlag(() => history.foundationUnlocked));
            flags.Set("marker", SafeFlag(() => history.markerUnlocked));
            flags.Set("dysonSphereSystem", SafeFlag(() => history.dysonSphereSystemUnlocked));
            flags.Set("ultraPhoton", SafeFlag(() => history.ultraPhotonUnlocked));
            flags.Set("verticalConstruct", SafeFlag(() => history.verticalConstructUnlocked));
            flags.Set("combatDrone", SafeFlag(() => history.combatDroneUnlocked));
            flags.Set("combatShip", SafeFlag(() => history.combatShipUnlocked));
            data.Set("signals", flags);

            data.Set("stage", DeriveStage(flags));
            data.Set("stageNote", "阶段是按上面的信号派生的；回答时请说明依据，不要把阶段当官方设定");

            data.Set("unlockedTechs", CountUnlocked(history));
            data.Set("tutorials", history.tutorialUnlocked != null ? history.tutorialUnlocked.Count : 0);
            data.Set("currentTechId", history.currentTech);
            data.Set("currentTechName", GameToolFormat.TechName(history.currentTech));
            data.Set("techSpeed", history.techSpeed);

            var queue = JsonValue.NewArray();
            if (history.techQueue != null)
            {
                for (int i = 0; i < history.techQueue.Length && i < 8; i++)
                {
                    var id = history.techQueue[i];
                    if (id <= 0) continue;
                    var row = JsonValue.NewObject();
                    row.Set("id", id);
                    row.Set("name", GameToolFormat.TechName(id));
                    row.Set("iconTag", GameToolFormat.TechIconTag(id));
                    queue.Add(row);
                }
            }
            data.Set("queue", queue);

            AppendMilestones(data, gameData);

            var stage = data["stage"].AsString("early");
            data.Set("display", "阶段 " + StageLabel(stage)
                + " · 已解锁科技 " + data["unlockedTechs"].AsInt(0)
                + " · 教程 " + data["tutorials"].AsInt(0)
                + " · 里程碑 " + data["milestoneCount"].AsInt(0));

            return new GameToolJobResult { Data = data };
        }

        private static bool SafeFlag(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }

        private static string DeriveStage(JsonValue flags)
        {
            if (flags["dysonSphereSystem"].AsBool(false)) return "dyson_sphere";
            if (flags["interstellarStation"].AsBool(false)) return "interstellar";
            if (flags["logisticsSystem"].AsBool(false)) return "planetary_logistics";
            return "early";
        }

        private static string StageLabel(string stage)
        {
            switch (stage)
            {
                case "dyson_sphere": return "戴森球";
                case "interstellar": return "星际物流";
                case "planetary_logistics": return "行星内物流";
                default: return "前期";
            }
        }

        private static int CountUnlocked(GameHistoryData history)
        {
            if (history.techStates == null) return 0;
            var count = 0;
            foreach (var pair in history.techStates)
                if (pair.Value.unlocked) count++;
            return count;
        }

        private static void AppendMilestones(JsonValue data, GameData gameData)
        {
            var list = new List<KeyValuePair<long, int>>();
            try
            {
                var system = gameData != null ? gameData.milestoneSystem : null;
                var datas = system != null ? system.milestoneDatas : null;
                if (datas != null)
                {
                    foreach (var pair in datas)
                    {
                        if (pair.Value == null || pair.Value.unlockTick <= 0) continue;
                        list.Add(new KeyValuePair<long, int>(pair.Value.unlockTick, pair.Key));
                    }
                }
            }
            catch { /* ignore */ }

            list.Sort((a, b) => b.Key.CompareTo(a.Key));

            var recent = JsonValue.NewArray();
            for (int i = 0; i < list.Count && i < 5; i++)
            {
                var row = JsonValue.NewObject();
                row.Set("id", list[i].Value);
                row.Set("name", MilestoneName(list[i].Value));
                row.Set("unlockTick", list[i].Key);
                recent.Add(row);
            }

            data.Set("milestoneCount", list.Count);
            data.Set("recentMilestones", recent);
        }

        private static string MilestoneName(int id)
        {
            try
            {
                var proto = LDB.milestones != null ? LDB.milestones.Select(id) : null;
                if (proto != null && !string.IsNullOrEmpty(proto.name)) return proto.name;
            }
            catch { /* ignore */ }
            return "#" + id.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- 截图

    internal sealed class CaptureViewTool : IGameToolHandler
    {
        public string Name => "game_capture_view";
        public int TimeoutMs => 15000;

        private const int MaxWidth = 1600;
        private const int MaxHeight = 900;
        private const int JpegQuality = 80;
        private const int MaxImageBytes = 2 * 1024 * 1024;
        private const int MaxVisibleEntities = 60;

        public IGameToolJob Start(JsonValue args)
        {
            var width = args["width"].AsInt(1280);
            var height = args["height"].AsInt(720);
            if (width <= 0) width = 1280;
            if (height <= 0) height = 720;
            width = Math.Min(width, MaxWidth);
            height = Math.Min(height, MaxHeight);
            var includeEntities = args["includeEntities"].AsBool(true);
            var annotate = args["annotate"].AsBool(true);

            return new OneShotJob(() => Build(width, height, includeEntities, annotate, args));
        }

        private static GameToolJobResult Build(int width, int height, bool includeEntities, bool annotate, JsonValue args)
        {
            var data = JsonValue.NewObject();
            data.Set("width", width);
            data.Set("height", height);

            var gameData = GameMain.data;
            if (gameData == null)
            {
                data.Set("available", false);
                data.Set("display", "游戏尚未就绪");
                return new GameToolJobResult { Data = data };
            }

            // 可选：临时把相机切到俯视/自定义机位，拍完立刻还原。
            // 只对**当前行星、已流送的区块**有效——相机移动不会触发远处区块加载。
            Camera cam = GameCamera.main;
            Vector3 savedPos = Vector3.zero;
            Quaternion savedRot = Quaternion.identity;
            float savedFov = 60f;
            var view = args["view"].AsString("player");
            var cameraInfo = JsonValue.NewObject();
            cameraInfo.Set("view", view);
            if (cam != null)
            {
                savedPos = cam.transform.position;
                savedRot = cam.transform.rotation;
                savedFov = cam.fieldOfView;
                if (view == "topdown")
                {
                    var center = ResolveCaptureCenter(gameData, args, out var centerSource);
                    var heightMeters = Math.Max(10, Math.Min(150, args["cameraHeight"].AsDouble(60)));
                    var yaw = args["yaw"].AsDouble(0);
                    var fov = Math.Max(20, Math.Min(90, args["fov"].AsDouble(60)));
                    var up = center.sqrMagnitude > 0.0001f ? center.normalized : Vector3.up;
                    var forward = Vector3.Cross(up, Vector3.right);
                    if (forward.sqrMagnitude < 0.01f) forward = Vector3.Cross(up, Vector3.forward);
                    forward.Normalize();
                    cam.transform.position = center + up * (float)heightMeters;
                    cam.transform.rotation = Quaternion.LookRotation(-up, Quaternion.AngleAxis((float)yaw, up) * forward);
                    cam.fieldOfView = (float)fov;
                    cameraInfo.Set("center", EntityToolUtil.Vec3(center));
                    cameraInfo.Set("centerSource", centerSource);
                    cameraInfo.Set("height", GameToolFormat.Round(heightMeters, 1));
                    cameraInfo.Set("yaw", GameToolFormat.Round(yaw, 0));
                    cameraInfo.Set("fov", GameToolFormat.Round(fov, 0));
                }
            }

            // 复用游戏自己的截图路径：它把 cullingMask 设成 gameLayerMask（UI 不进画面），
            // 并把 PNG 字节放进 GameMain.data.screenShot。这里只做 PNG→JPEG 的压缩。
            try
            {
                GameCamera.CaptureScreenshot(width, height);
            }
            catch (Exception ex)
            {
                data.Set("available", false);
                data.Set("reason", "capture_failed");
                data.Set("display", "截图失败：" + ex.Message);
                return new GameToolJobResult { Data = data };
            }
            finally
            {
                if (cam != null && view == "topdown")
                {
                    cam.transform.position = savedPos;
                    cam.transform.rotation = savedRot;
                    cam.fieldOfView = savedFov;
                }
            }

            var png = gameData.screenShot;
            if (png == null || png.Length == 0)
            {
                data.Set("available", false);
                data.Set("reason", "empty_frame");
                data.Set("display", "没有取到画面（可能正在加载）");
                return new GameToolJobResult { Data = data };
            }

            var marks = CollectMarks(gameData, width, height);
            byte[] jpeg;
            int outWidth;
            int outHeight;
            if (!TryEncodeJpeg(png, width, height, annotate ? marks : null, out jpeg, out outWidth, out outHeight))
            {
                data.Set("available", false);
                data.Set("reason", "encode_failed");
                data.Set("display", "画面编码失败");
                return new GameToolJobResult { Data = data };
            }

            if (jpeg.Length > MaxImageBytes)
            {
                data.Set("available", false);
                data.Set("reason", "image_too_large");
                data.Set("bytes", jpeg.Length);
                data.Set("display", "截图过大（" + (jpeg.Length / 1024) + " KiB），请降低分辨率");
                return new GameToolJobResult { Data = data };
            }

            var image = JsonValue.NewObject();
            image.Set("mimeType", "image/jpeg");
            image.Set("data", Convert.ToBase64String(jpeg));
            image.Set("width", outWidth);
            image.Set("height", outHeight);

            data.Set("available", true);
            data.Set("bytes", jpeg.Length);
            var info = CameraInfo();
            if (cameraInfo.Count > 0)
            {
                foreach (var key in new[] { "view", "center", "centerSource", "height", "yaw", "fov" })
                {
                    var value = cameraInfo[key];
                    if (value != null && !value.IsNull) info.Set(key, value);
                }
            }
            data.Set("camera", info);
            if (includeEntities) data.Set("visibleEntities", MarkRows(marks, outWidth, outHeight));
            if (annotate)
            {
                data.Set("annotated", true);
                data.Set("legend", LegendRows(marks, outWidth, outHeight));
            }

            data.Set("display", "已截图 " + outWidth + "×" + outHeight + "（"
                + (jpeg.Length / 1024) + " KiB）");

            return new GameToolJobResult { Data = data, Image = image };
        }

        private static Vector3 ResolveCaptureCenter(GameData gameData, JsonValue args, out string source)
        {
            var entityId = args["entityId"].AsInt(0);
            var planet = gameData.localPlanet;
            if (entityId > 0 && planet != null && planet.factory != null
                && EntityToolUtil.HasEntity(planet.factory, entityId))
            {
                source = "entity";
                return planet.factory.entityPool[entityId].pos;
            }
            var center = args["center"];
            if (center != null && center.IsObject)
            {
                source = "explicit";
                return new Vector3(
                    (float)center["x"].AsDouble(0),
                    (float)center["y"].AsDouble(0),
                    (float)center["z"].AsDouble(0));
            }
            source = "player";
            return gameData.mainPlayer != null ? gameData.mainPlayer.position : Vector3.zero;
        }

        private static bool TryEncodeJpeg(byte[] png, int expectedWidth, int expectedHeight,
            List<VisibleMark> marks, out byte[] jpeg, out int width, out int height)
        {
            jpeg = null;
            width = expectedWidth;
            height = expectedHeight;

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(png)) return false;
                width = texture.width;
                height = texture.height;
                if (marks != null && marks.Count > 0) DrawMarks(texture, marks, width, height);
                texture.Apply(false, false);
                jpeg = texture.EncodeToJPG(JpegQuality);
                return jpeg != null && jpeg.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        private static JsonValue CameraInfo()
        {
            var info = JsonValue.NewObject();
            try
            {
                var camera = GameCamera.main;
                if (camera == null) return info;
                var position = camera.transform.position;
                info.Set("position", EntityToolUtil.Vec3(position));
                info.Set("forward", EntityToolUtil.Vec3(camera.transform.forward));
                info.Set("fov", GameToolFormat.Round(camera.fieldOfView, 1));
            }
            catch { /* ignore */ }
            return info;
        }

        /// <summary>
        /// 可见建筑投影：给模型"图上这个点对应哪个 id"的对应关系。
        /// 只投影屏幕内的建筑，按到相机的距离排序，最多 60 个。
        /// </summary>
        private sealed class VisibleMark
        {
            public int Id;
            public int ProtoId;
            public string Name;
            public string Category;
            public int X;
            public int Y;
            public float Distance;
        }

        /// <summary>
        /// 屏幕内最近的实体（视口坐标换算成截图像素）。截图标注与 visibleEntities 共用这份结果，
        /// 避免两处筛选条件不一致导致“图上的编号和图例对不上”。
        /// </summary>
        private static List<VisibleMark> CollectMarks(GameData gameData, int width, int height)
        {
            var marks = new List<VisibleMark>();
            var camera = GameCamera.main;
            var planet = gameData.localPlanet;
            if (camera == null || planet == null || planet.factory == null) return marks;

            var factory = planet.factory;
            if (factory.entityPool == null) return marks;

            var origin = camera.transform.position;
            var candidates = new List<KeyValuePair<float, int>>();
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;
                var viewport = camera.WorldToViewportPoint(entity.pos);
                if (viewport.z <= 0) continue;
                if (viewport.x < 0 || viewport.x > 1) continue;
                if (viewport.y < 0 || viewport.y > 1) continue;
                candidates.Add(new KeyValuePair<float, int>(Vector3.Distance(origin, entity.pos), id));
            }

            candidates.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < candidates.Count && i < MaxVisibleEntities; i++)
            {
                var id = candidates[i].Value;
                var entity = factory.entityPool[id];
                var viewport = camera.WorldToViewportPoint(entity.pos);
                marks.Add(new VisibleMark
                {
                    Id = id,
                    ProtoId = entity.protoId,
                    Name = EntityToolUtil.ProtoName(entity.protoId),
                    Category = EntityToolUtil.Category(entity),
                    X = (int)Math.Round(viewport.x * width),
                    Y = (int)Math.Round((1f - viewport.y) * height),
                    Distance = candidates[i].Key,
                });
            }
            return marks;
        }

        private static JsonValue MarkRows(List<VisibleMark> marks, int width, int height)
        {
            var rows = JsonValue.NewArray();
            foreach (var mark in marks)
            {
                var row = JsonValue.NewObject();
                row.Set("id", mark.Id);
                row.Set("protoId", mark.ProtoId);
                row.Set("protoName", mark.Name);
                row.Set("category", mark.Category);
                row.Set("x", mark.X);
                row.Set("y", mark.Y);
                row.Set("distance", GameToolFormat.Round(mark.Distance, 1));
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>只给前 20 个画编号框，避免图上一团花；图例序号与之对应。</summary>
        private static JsonValue LegendRows(List<VisibleMark> marks, int width, int height)
        {
            var rows = JsonValue.NewArray();
            for (int i = 0; i < marks.Count && i < 20; i++)
            {
                var mark = marks[i];
                var row = JsonValue.NewObject();
                row.Set("index", i + 1);
                row.Set("id", mark.Id);
                row.Set("protoName", mark.Name);
                row.Set("category", mark.Category);
                row.Set("x", mark.X);
                row.Set("y", mark.Y);
                rows.Add(row);
            }
            return rows;
        }

        // 3×5 点阵数字；比引 TextMesh 便宜，也不会把 UI 画进截图。
        private static readonly string[] DigitFont =
        {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111",
            "101101111001001", "111100111001111", "111100111101111", "111001001001001",
            "111101111101111", "111101111001111",
        };

        private static void DrawMarks(Texture2D texture, List<VisibleMark> marks, int width, int height)
        {
            var pixels = texture.GetPixels32();
            for (int i = 0; i < marks.Count && i < 20; i++)
            {
                var mark = marks[i];
                var color = ColorFor(mark.Category);
                const int half = 11;
                DrawBox(pixels, width, height, mark.X - half, mark.Y - half, mark.X + half, mark.Y + half, color);
                DrawNumber(pixels, width, height, mark.X - 6, mark.Y - half - 12, i + 1, 2, color);
            }
            texture.SetPixels32(pixels);
        }

        private static Color32 ColorFor(string category)
        {
            switch (category)
            {
                case "assembler": return new Color32(120, 255, 140, 255);
                case "station": return new Color32(255, 120, 255, 255);
                case "storage": return new Color32(255, 190, 90, 255);
                case "power_generator":
                case "power_node":
                case "power_accumulator": return new Color32(120, 190, 255, 255);
                case "inserter": return new Color32(255, 240, 110, 255);
                case "belt": return new Color32(110, 230, 230, 255);
                default: return new Color32(230, 230, 230, 255);
            }
        }

        private static void DrawBox(Color32[] pixels, int width, int height, int x0, int y0, int x1, int y1, Color32 color)
        {
            for (int x = x0; x <= x1; x++)
            {
                Put(pixels, width, height, x, y0, color);
                Put(pixels, width, height, x, y1, color);
            }
            for (int y = y0; y <= y1; y++)
            {
                Put(pixels, width, height, x0, y, color);
                Put(pixels, width, height, x1, y, color);
            }
        }

        private static void DrawNumber(Color32[] pixels, int width, int height, int x, int y, int value, int scale, Color32 color)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            foreach (var ch in text)
            {
                var digit = ch - '0';
                if (digit < 0 || digit > 9) continue;
                var glyph = DigitFont[digit];
                for (int gy = 0; gy < 5; gy++)
                {
                    for (int gx = 0; gx < 3; gx++)
                    {
                        if (glyph[gy * 3 + gx] != '1') continue;
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                                Put(pixels, width, height, x + gx * scale + sx, y + gy * scale + sy, color);
                    }
                }
                x += 4 * scale;
            }
        }

        private static void Put(Color32[] pixels, int width, int height, int x, int y, Color32 color)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            pixels[y * width + x] = color;
        }

    }

    // ---------------------------------------------------------------- 区域产线分析

    /// <summary>
    /// 按空间范围聚合产线：机器数、装机产能、功率、物流设施数量、涉及电网。
    ///
    /// 刻意只回答**装机口径**（速度×配方），不猜实际产出：实际产出要看
    /// `game_get_production` 的统计。两者混在一起最容易给出"看起来对但错"的数。
    /// 范围是行星局部 XZ 平面上的球形切分，靠近极点会与游戏网格有偏差。
    /// </summary>
    internal sealed class AnalyzeAreaTool : IGameToolHandler
    {
        public string Name => "game_analyze_area";
        public int TimeoutMs => 15000;

        private const double DefaultRadius = 60;
        private const double MinRadius = 5;
        private const double MaxRadius = 500;

        private sealed class RecipeAgg
        {
            public int Count;
            public double CraftsPerMin;
            public double PowerKw;
        }

        public IGameToolJob Start(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");

            var all = args["all"].AsBool(false);
            var radius = args["radius"].AsDouble(DefaultRadius);
            if (radius < MinRadius) radius = MinRadius;
            if (radius > MaxRadius) radius = MaxRadius;

            var seed = args["entityId"].AsInt(0);
            var includeEntities = args["includeEntities"].AsBool(false);
            var limit = args["limit"].AsInt(40);
            if (limit <= 0) limit = 40;
            if (limit > 100) limit = 100;

            var centerArg = args["center"];
            return new OneShotJob(() => Build(planet, all, radius, seed, centerArg,
                includeEntities, limit));
        }

        private static GameToolJobResult Build(PlanetData planet, bool all, double radius,
            int seed, JsonValue centerArg, bool includeEntities, int limit)
        {
            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);

            var factory = planet.factory;
            if (factory == null || factory.entityPool == null)
            {
                data.Set("available", false);
                data.Set("display", "该行星的工厂尚未加载");
                return new GameToolJobResult { Data = data };
            }

            var center = ResolveCenter(factory, seed, centerArg, out var centerSource);
            var area = JsonValue.NewObject();
            area.Set("mode", all ? "planet" : "sphere");
            area.Set("center", EntityToolUtil.Vec3(center));
            area.Set("centerSource", centerSource);
            if (!all) area.Set("radius", GameToolFormat.Round(radius, 1));
            data.Set("area", area);

            var byRecipe = new Dictionary<int, RecipeAgg>();
            var categories = new Dictionary<string, int>(StringComparer.Ordinal);
            var networks = new HashSet<int>();
            var listed = JsonValue.NewArray();
            var radiusSquared = radius * radius;

            long powerPerTick = 0;
            int buildings = 0;
            int listedCount = 0;
            int belts = 0;
            int inserters = 0;
            int stations = 0;
            int storages = 0;
            int powerConsumers = 0;

            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;

                if (!all)
                {
                    var dx = entity.pos.x - center.x;
                    var dz = entity.pos.z - center.z;
                    if (dx * dx + dz * dz > radiusSquared) continue;
                }

                buildings++;
                var category = EntityToolUtil.Category(entity);
                int categoryCount;
                categories.TryGetValue(category, out categoryCount);
                categories[category] = categoryCount + 1;

                if (entity.beltId > 0) belts++;
                if (entity.inserterId > 0) inserters++;
                if (entity.stationId > 0) stations++;
                if (entity.storageId > 0) storages++;

                // 功率：用 consumer.requiredEnergy（每 tick），和 game_get_power 同口径。
                var system = factory.powerSystem;
                if (entity.powerConId > 0 && system != null && system.consumerPool != null &&
                    entity.powerConId < system.consumerPool.Length)
                {
                    var consumer = system.consumerPool[entity.powerConId];
                    var required = consumer.requiredEnergy;
                    if (required > 0) powerPerTick += required;
                    if (consumer.networkId > 0) networks.Add(consumer.networkId);
                    powerConsumers++;
                }
                if (entity.powerGenId > 0 && system != null && system.genPool != null &&
                    entity.powerGenId < system.genPool.Length)
                {
                    var gen = system.genPool[entity.powerGenId];
                    if (gen.networkId > 0) networks.Add(gen.networkId);
                }

                if (entity.assemblerId > 0 && factory.factorySystem != null &&
                    factory.factorySystem.assemblerPool != null &&
                    entity.assemblerId < factory.factorySystem.assemblerPool.Length)
                {
                    var assembler = factory.factorySystem.assemblerPool[entity.assemblerId];
                    if (assembler.recipeId > 0)
                    {
                        RecipeAgg agg;
                        if (!byRecipe.TryGetValue(assembler.recipeId, out agg))
                        {
                            agg = new RecipeAgg();
                            byRecipe[assembler.recipeId] = agg;
                        }
                        agg.Count++;
                        var seconds = EntityToolUtil.RecipeSeconds(assembler.recipeId);
                        if (seconds > 0)
                            agg.CraftsPerMin += 60.0 / seconds * (assembler.speed / 10000.0);

                        if (entity.powerConId > 0 && system != null && system.consumerPool != null &&
                            entity.powerConId < system.consumerPool.Length)
                        {
                            agg.PowerKw += GameToolFormat.Kw(
                                system.consumerPool[entity.powerConId].requiredEnergy);
                        }
                    }
                }

                if (includeEntities && listedCount < limit)
                {
                    var row = JsonValue.NewObject();
                    row.Set("id", id);
                    row.Set("protoId", entity.protoId);
                    row.Set("protoName", EntityToolUtil.ProtoName(entity.protoId));
                    row.Set("category", category);
                    row.Set("pos", EntityToolUtil.Vec3(entity.pos));
                    if (entity.assemblerId > 0 && factory.factorySystem != null &&
                        factory.factorySystem.assemblerPool != null &&
                        entity.assemblerId < factory.factorySystem.assemblerPool.Length)
                    {
                        var recipeId = factory.factorySystem.assemblerPool[entity.assemblerId].recipeId;
                        if (recipeId > 0)
                        {
                            row.Set("recipeId", recipeId);
                            row.Set("recipeName", EntityToolUtil.RecipeName(recipeId));
                        }
                    }
                    listed.Add(row);
                    listedCount++;
                }
            }

            // 配方聚合排序：机器多的在前。
            var recipeRows = new List<KeyValuePair<int, RecipeAgg>>(byRecipe);
            recipeRows.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

            var recipes = JsonValue.NewArray();
            foreach (var pair in recipeRows)
            {
                var row = JsonValue.NewObject();
                row.Set("recipeId", pair.Key);
                row.Set("recipeName", EntityToolUtil.RecipeName(pair.Key));
                row.Set("buildings", pair.Value.Count);
                row.Set("installedCraftsPerMin", GameToolFormat.Round(pair.Value.CraftsPerMin, 2));
                row.Set("powerKw", GameToolFormat.Round(pair.Value.PowerKw));
                recipes.Add(row);
            }
            data.Set("byRecipe", recipes);

            var categoryObj = JsonValue.NewObject();
            var categoryKeys = new List<string>(categories.Keys);
            categoryKeys.Sort(StringComparer.Ordinal);
            foreach (var key in categoryKeys) categoryObj.Set(key, categories[key]);
            data.Set("categories", categoryObj);

            var totals = JsonValue.NewObject();
            totals.Set("buildings", buildings);
            totals.Set("powerConsumers", powerConsumers);
            totals.Set("powerKw", GameToolFormat.Round(GameToolFormat.Kw(powerPerTick)));
            totals.Set("belts", belts);
            totals.Set("inserters", inserters);
            totals.Set("stations", stations);
            totals.Set("storages", storages);
            data.Set("totals", totals);

            data.Set("powerNetworks", BuildNetworks(factory, networks));
            if (includeEntities) data.Set("entities", listed);

            data.Set("available", true);
            data.Set("assumptions", Assumptions(all));
            data.Set("display",
                (all ? "整星球" : "半径 " + GameToolFormat.Round(radius, 0) + "m")
                + " · 建筑 " + buildings
                + " · 装机功率 " + GameToolFormat.PowerDisplay(GameToolFormat.Kw(powerPerTick))
                + " · 配方 " + recipeRows.Count + " 种");

            return new GameToolJobResult { Data = data };
        }

        private static Vector3 ResolveCenter(PlanetFactory factory, int seed, JsonValue centerArg,
            out string source)
        {
            if (EntityToolUtil.HasEntity(factory, seed))
            {
                var entity = factory.entityPool[seed];
                if (entity.id == seed && entity.protoId > 0)
                {
                    source = "entity";
                    return entity.pos;
                }
            }

            if (centerArg != null && centerArg.IsObject)
            {
                source = "explicit";
                return new Vector3(
                    (float)centerArg["x"].AsDouble(0),
                    (float)centerArg["y"].AsDouble(0),
                    (float)centerArg["z"].AsDouble(0));
            }

            var player = GameMain.data != null ? GameMain.data.mainPlayer : null;
            if (player != null)
            {
                source = "player";
                return player.position;
            }

            source = "origin";
            return Vector3.zero;
        }

        private static JsonValue BuildNetworks(PlanetFactory factory, HashSet<int> ids)
        {
            var rows = JsonValue.NewArray();
            var system = factory != null ? factory.powerSystem : null;
            if (system == null || system.netPool == null) return rows;

            var sorted = new List<int>(ids);
            sorted.Sort();
            foreach (var networkId in sorted)
            {
                if (networkId <= 0 || networkId >= system.netPool.Length) continue;
                var network = system.netPool[networkId];
                if (network == null) continue;

                var row = JsonValue.NewObject();
                var required = GameToolFormat.Kw(network.energyRequired);
                var served = GameToolFormat.Kw(network.energyServed);
                row.Set("id", networkId);
                row.Set("requiredKw", GameToolFormat.Round(required));
                row.Set("servedKw", GameToolFormat.Round(served));
                row.Set("capacityKw", GameToolFormat.Round(GameToolFormat.Kw(network.energyCapacity)));
                row.Set("storedKwh", GameToolFormat.Round(GameToolFormat.Kwh(network.energyStored)));
                row.Set("satisfactionPct", required > 0
                    ? GameToolFormat.Round(served * 100.0 / required)
                    : 100);
                rows.Add(row);
            }
            return rows;
        }

        private static JsonValue Assumptions(bool all)
        {
            var list = JsonValue.NewArray();
            if (!all)
                list.Add("范围是行星局部 XZ 平面上的球形切分，靠近极点会与游戏网格有偏差。");
            list.Add("installedCraftsPerMin 是装机产能（速度 × 配方），不是实际产出；实际产出看 game_get_production。");
            list.Add("powerKw 是范围内耗电建筑的 requiredEnergy 汇总，可能与电网面板略有差异。");
            list.Add("只统计装配类机器；熔炉/化工厂等同属 assemblerPool，按各自配方类型统计。");
            return list;
        }
    }

    // ---------------------------------------------------------------- 文本布局图

    /// <summary>
    /// 把一片区域的建筑排布画成字符网格。
    ///
    /// 截图受镜头/分辨率影响，模型很难判断“布局对不对”；字符网格更省 token、更确定。
    /// 只表达占位与类别，不表达朝向细节；上=+Z，左=-X。
    /// </summary>
    internal sealed class RenderLayoutTool : IGameToolHandler
    {
        public string Name => "game_render_layout";
        public int TimeoutMs => 10000;

        private const int MaxWidth = 100;
        private const int MaxHeight = 40;
        private const string Chars = "MSBPV>=#";

        public IGameToolJob Start(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");
            return new OneShotJob(() => Build(planet, args));
        }

        private static GameToolJobResult Build(PlanetData planet, JsonValue args)
        {
            var factory = planet.factory;
            var data = JsonValue.NewObject();
            data.Set("planetId", planet.id);
            if (factory == null || factory.entityPool == null)
            {
                data.Set("available", false);
                data.Set("display", "该行星的工厂尚未加载");
                return new GameToolJobResult { Data = data };
            }

            double radius = args["radius"].AsDouble(40);
            if (radius < 4) radius = 4;
            if (radius > 400) radius = 400;
            double cellSize = args["cellSize"].AsDouble(0);
            if (cellSize <= 0) cellSize = Math.Max(2, radius * 2 / MaxWidth);

            int width = Math.Max(8, Math.Min(MaxWidth, (int)Math.Ceiling(radius * 2 / cellSize)));
            int height = Math.Max(6, Math.Min(MaxHeight, (int)Math.Ceiling(radius * 2 / cellSize)));

            var center = ResolveCenter(factory, args, out var centerSource);
            var originX = center.x - width * cellSize * 0.5;
            var originZ = center.z - height * cellSize * 0.5;

            var grid = new char[height, width];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    grid[y, x] = ' ';

            var counts = new Dictionary<char, int>();
            var upper = Math.Min(factory.entityCursor, factory.entityPool.Length);
            for (int id = 1; id < upper; id++)
            {
                var entity = factory.entityPool[id];
                if (entity.id != id || entity.protoId <= 0) continue;

                int col = (int)Math.Floor((entity.pos.x - originX) / cellSize);
                int row = (int)Math.Floor((entity.pos.z - originZ) / cellSize);
                if (col < 0 || col >= width || row < 0 || row >= height) continue;

                var ch = CharFor(factory, entity);
                if (Rank(ch) >= Rank(grid[row, col]))
                {
                    if (grid[row, col] != ' ')
                    {
                        int old;
                        counts.TryGetValue(grid[row, col], out old);
                        counts[grid[row, col]] = Math.Max(0, old - 1);
                    }
                    grid[row, col] = ch;
                    int count;
                    counts.TryGetValue(ch, out count);
                    counts[ch] = count + 1;
                }
            }

            var rows = JsonValue.NewArray();
            for (int y = 0; y < height; y++)
            {
                var line = new char[width];
                for (int x = 0; x < width; x++) line[x] = grid[y, x];
                rows.Add(new string(line));
            }

            var legend = JsonValue.NewArray();
            foreach (var ch in Chars)
            {
                int count;
                if (!counts.TryGetValue(ch, out count) || count <= 0) continue;
                var row = JsonValue.NewObject();
                row.Set("char", ch.ToString());
                row.Set("kind", KindOf(ch));
                row.Set("count", count);
                legend.Add(row);
            }

            data.Set("available", true);
            data.Set("grid", rows);
            data.Set("width", width);
            data.Set("height", height);
            data.Set("cellSize", GameToolFormat.Round(cellSize, 2));
            data.Set("origin", JsonValue.NewObject());
            data["origin"].Set("x", GameToolFormat.Round(originX, 1));
            data["origin"].Set("z", GameToolFormat.Round(originZ, 1));
            data["origin"].Set("centerSource", centerSource);
            data.Set("legend", legend);
            data.Set("orientation", "上=+Z，左=-X；每个字符代表一个 cellSize 米方格");
            data.Set("display", "布局图 " + width + "×" + height + " · 格子 " + GameToolFormat.Round(cellSize, 1) + "m");
            return new GameToolJobResult { Data = data };
        }

        private static Vector3 ResolveCenter(PlanetFactory factory, JsonValue args, out string source)
        {
            var entityId = args["entityId"].AsInt(0);
            if (entityId > 0 && entityId < factory.entityPool.Length)
            {
                var entity = factory.entityPool[entityId];
                if (entity.id == entityId && entity.protoId > 0)
                {
                    source = "entity";
                    return entity.pos;
                }
            }
            var center = args["center"];
            if (center != null && center.IsObject)
            {
                source = "explicit";
                return new Vector3(
                    (float)center["x"].AsDouble(0),
                    (float)center["y"].AsDouble(0),
                    (float)center["z"].AsDouble(0));
            }
            source = "player";
            var player = GameMain.data != null ? GameMain.data.mainPlayer : null;
            return player != null ? player.position : Vector3.zero;
        }

        private static char CharFor(PlanetFactory factory, EntityData entity)
        {
            if (entity.stationId > 0) return 'S';
            if (entity.assemblerId > 0) return 'M';
            if (entity.storageId > 0) return 'B';
            if (entity.powerGenId > 0) return 'P';
            if (entity.minerId > 0) return 'V';
            if (entity.inserterId > 0) return '>';
            if (entity.beltId > 0) return '=';
            return '#';
        }

        private static int Rank(char ch)
        {
            switch (ch)
            {
                case 'S': return 9;
                case 'M': return 8;
                case 'B': return 7;
                case 'P': return 6;
                case 'V': return 5;
                case '>': return 4;
                case '=': return 3;
                case '#': return 1;
                default: return 0;
            }
        }

        private static string KindOf(char ch)
        {
            switch (ch)
            {
                case 'S': return "物流站";
                case 'M': return "生产设备";
                case 'B': return "仓储";
                case 'P': return "发电";
                case 'V': return "采矿";
                case '>': return "分拣器";
                case '=': return "传送带";
                default: return "其它";
            }
        }
    }

    // ---------------------------------------------------------------- 存为蓝图

    /// <summary>
    /// 把指定的一批建筑存成蓝图文件。
    ///
    /// 不手写二进制格式：直接复用游戏的 `BlueprintUtils.GenerateBlueprintData`
    /// 与 `BlueprintData.SaveBlueprintData`，因此产物与游戏内复制出来的完全同源，
    /// 不会因为格式猜错而粘不出来。
    /// </summary>
    internal sealed class CopyBlueprintTool : IGameToolHandler
    {
        public string Name => "game_copy_blueprint";
        public int TimeoutMs => 20000;

        public IGameToolJob Start(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");
            return new OneShotJob(() => Build(planet, args));
        }

        private static GameToolJobResult Build(PlanetData planet, JsonValue args)
        {
            var data = JsonValue.NewObject();
            var factory = planet.factory;
            if (factory == null || factory.entityPool == null)
            {
                data.Set("saved", false);
                data.Set("error", "factory_not_loaded");
                return new GameToolJobResult { Data = data };
            }

            var idArray = args["entityIds"];
            var ids = new List<int>();
            if (idArray != null && idArray.IsArray)
            {
                for (int i = 0; i < idArray.Count && ids.Count < 500; i++)
                {
                    var id = idArray[i].AsInt(0);
                    if (id > 0 && EntityToolUtil.HasEntity(factory, id)) ids.Add(id);
                }
            }
            if (ids.Count == 0)
            {
                data.Set("saved", false);
                data.Set("error", "no_entities");
                data.Set("display", "没有可存的建筑；先用 game_analyze_area / game_get_entities 取 id");
                return new GameToolJobResult { Data = data };
            }

            var aux = planet.aux;
            if (aux == null)
            {
                data.Set("saved", false);
                data.Set("error", "aux_not_ready");
                return new GameToolJobResult { Data = data };
            }

            var bp = BlueprintData.CreateNew();
            BlueprintUtils.GenerateBlueprintData(bp, planet, aux, ids.ToArray(), ids.Count, 0f, null, false);
            if (BlueprintData.IsNullOrEmpty(bp))
            {
                data.Set("saved", false);
                data.Set("error", "empty_blueprint");
                return new GameToolJobResult { Data = data };
            }

            var requested = args["name"].AsString(null);
            var baseName = Sanitize(requested);
            if (string.IsNullOrEmpty(baseName)) baseName = "IcarusII-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var requestedDir = args["dir"].AsString(null);
            var rootRequested = requestedDir == "/" || requestedDir == "." || requestedDir == "./";
            var dirRel = rootRequested ? string.Empty : SafeDir(requestedDir);
            if (!string.IsNullOrEmpty(requestedDir) && !rootRequested && string.IsNullOrEmpty(dirRel))
            {
                data.Set("saved", false);
                data.Set("error", "invalid_dir");
                data.Set("display", "保存目录不合法；只能使用蓝图目录内的相对路径");
                return new GameToolJobResult { Data = data };
            }
            if (string.IsNullOrEmpty(requestedDir)) dirRel = "IcarusII";
            var root = GameConfig.blueprintFolder;
            var dir = string.IsNullOrEmpty(dirRel) ? root : System.IO.Path.Combine(root, dirRel);
            try
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                data.Set("saved", false);
                data.Set("error", "mkdir_failed: " + ex.Message);
                return new GameToolJobResult { Data = data };
            }

            var fileName = baseName + ".txt";
            var path = System.IO.Path.Combine(dir, fileName);
            for (int i = 2; i < 50 && System.IO.File.Exists(path); i++)
            {
                fileName = baseName + " (" + i.ToString(CultureInfo.InvariantCulture) + ").txt";
                path = System.IO.Path.Combine(dir, fileName);
            }

            Exception error;
            if (!bp.SaveBlueprintData(path, out error))
            {
                data.Set("saved", false);
                data.Set("error", error != null ? error.Message : "save_failed");
                return new GameToolJobResult { Data = data };
            }

            data.Set("saved", true);
            data.Set("source", "copied");
            data.Set("path", path);
            data.Set("relPath", string.IsNullOrEmpty(dirRel) ? fileName : dirRel + "/" + fileName);
            data.Set("dir", dir);
            data.Set("dirRel", string.IsNullOrEmpty(dirRel) ? string.Empty : dirRel);
            data.Set("fileName", fileName);
            data.Set("buildings", bp.buildings != null ? bp.buildings.Length : 0);
            data.Set("areas", bp.areas != null ? bp.areas.Length : 0);
            // 卡片元数据：复制出来的蓝图已经有完整 header，直接回传。
            data.Set("name", bp.shortDesc ?? string.Empty);
            data.Set("author", bp.author ?? string.Empty);
            data.Set("version", bp.customVersion ?? string.Empty);
            data.Set("attributes", bp.externalFields ?? string.Empty);
            data.Set("description", bp.desc ?? string.Empty);
            data.Set("iconItemId", bp.icon0);
            data.Set("width", bp.dragBoxSize_x);
            data.Set("height", bp.dragBoxSize_y);
            data.Set("display", "已存蓝图 " + fileName + " · 建筑 " + (bp.buildings != null ? bp.buildings.Length : 0)
                + " · 打开蓝图界面即可看到");
            return new GameToolJobResult { Data = data };
        }

        /// <summary>把工具参数里的相对目录清洗成安全路径；非法返回空串（退回根目录）。</summary>
        private static string SafeDir(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var normalized = raw.Replace('\\', '/').Trim();
            if (normalized.StartsWith("/") || normalized.Contains(":") || normalized.Contains("\0")) return string.Empty;
            var parts = new List<string>();
            foreach (var part in normalized.Split('/'))
            {
                if (string.IsNullOrEmpty(part) || part == ".") continue;
                if (part == "..") return string.Empty;
                var cleaned = Sanitize(part);
                if (string.IsNullOrEmpty(cleaned)) return string.Empty;
                parts.Add(cleaned);
            }
            return string.Join("/", parts);
        }

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var chars = new char[raw.Length];
            int n = 0;
            foreach (var ch in raw)
            {
                if (char.IsControl(ch)) continue;
                switch (ch)
                {
                    case '<': case '>': case ':': case '"':
                    case '/': case '\\': case '|': case '?': case '*':
                        chars[n++] = ' ';
                        break;
                    default:
                        chars[n++] = ch;
                        break;
                }
            }
            var text = new string(chars, 0, n).Trim().Trim('.');
            if (text.Length > 80) text = text.Substring(0, 80).Trim();
            return text;
        }
    }
}
