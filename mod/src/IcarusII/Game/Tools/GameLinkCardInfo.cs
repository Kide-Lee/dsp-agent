using System;
using System.Globalization;
using System.Text;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 建筑/星球卡片的“运行时快照”。数值口径尽量照抄对应的游戏 UI 窗口：
    /// 卡片打开时只采样一次，避免每帧刷新；窗口重新打开会再采样。
    /// </summary>
    internal static class GameLinkCardInfo
    {
        public static string Describe(PlanetFactory factory, EntityData entity)
        {
            if (factory == null || entity.id <= 0) return string.Empty;
            try
            {
                switch (EntityToolUtil.Category(entity))
                {
                    case "assembler": return Assembler(factory, entity);
                    case "miner": return Miner(factory, entity);
                    case "belt": return Belt(factory, entity);
                    case "inserter": return Inserter(factory, entity);
                    case "splitter": return Splitter(factory, entity);
                    case "monitor": return Monitor(factory, entity);
                    case "spray_coater": return Spray(factory, entity);
                    case "piler": return Piler(factory, entity);
                    case "tank": return Tank(factory, entity);
                    case "storage":
                    case "battle_base": return Storage(factory, entity);
                    case "dispenser": return Dispenser(factory, entity);
                    case "station": return Station(factory, entity);
                    case "power_generator": return PowerGenerator(factory, entity);
                    case "power_node":
                    case "power_accumulator":
                    case "power_exchanger":
                    case "beacon": return PowerNode(factory, entity);
                    case "turret": return Turret(factory, entity);
                    case "field_generator": return FieldGenerator(factory, entity);
                    case "ejector": return Ejector(factory, entity);
                    case "silo": return Silo(factory, entity);
                    case "lab": return Lab(factory, entity);
                    case "fractionator": return Fractionator(factory, entity);
                    default: return "建筑 #" + entity.id.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                return "建筑 #" + entity.id.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static void ResolveIcons(PlanetFactory factory, EntityData entity, GameTether.Target target)
        {
            if (factory == null || target == null) return;
            try
            {
                var item = LDB.items != null ? LDB.items.Select(entity.protoId) : null;
                if (item != null) target.MainIcon = item.iconSprite;

                int iconItem = 0;
                switch (EntityToolUtil.Category(entity))
                {
                    case "assembler":
                        if (entity.assemblerId > 0 && factory.factorySystem != null &&
                            entity.assemblerId < factory.factorySystem.assemblerPool.Length)
                        {
                            int recipeId = factory.factorySystem.assemblerPool[entity.assemblerId].recipeId;
                            var recipe = recipeId > 0 && LDB.recipes != null ? LDB.recipes.Select(recipeId) : null;
                            if (recipe != null) target.RecipeIcon = recipe.iconSprite;
                        }
                        break;
                    case "lab":
                        if (entity.labId > 0 && factory.factorySystem != null &&
                            entity.labId < factory.factorySystem.labPool.Length)
                            iconItem = RecipeItem(factory.factorySystem.labPool[entity.labId].recipeId);
                        break;
                    case "fractionator":
                        if (entity.fractionatorId > 0 && factory.factorySystem != null &&
                            entity.fractionatorId < factory.factorySystem.fractionatorPool.Length)
                            iconItem = factory.factorySystem.fractionatorPool[entity.fractionatorId].productId;
                        break;
                    case "miner":
                        if (entity.minerId > 0 && factory.factorySystem != null &&
                            entity.minerId < factory.factorySystem.minerPool.Length)
                        {
                            var miner = factory.factorySystem.minerPool[entity.minerId];
                            iconItem = miner.productId;
                            if (iconItem <= 0 && miner.veins != null && miner.veins.Length > 0 &&
                                factory.veinPool != null && miner.veins[0] > 0 && miner.veins[0] < factory.veinPool.Length)
                                iconItem = factory.veinPool[miner.veins[0]].productId;
                        }
                        break;
                    case "inserter":
                        if (entity.inserterId > 0 && factory.factorySystem != null &&
                            entity.inserterId < factory.factorySystem.inserterPool.Length)
                        {
                            var ins = factory.factorySystem.inserterPool[entity.inserterId];
                            iconItem = ins.filter > 0 ? ins.filter : ins.itemId;
                        }
                        break;
                    case "splitter":
                        if (entity.splitterId > 0 && factory.cargoTraffic != null &&
                            entity.splitterId < factory.cargoTraffic.splitterPool.Length)
                            iconItem = factory.cargoTraffic.splitterPool[entity.splitterId].outFilter;
                        break;
                    case "monitor":
                        if (entity.monitorId > 0 && factory.cargoTraffic != null &&
                            entity.monitorId < factory.cargoTraffic.monitorPool.Length)
                            iconItem = factory.cargoTraffic.monitorPool[entity.monitorId].cargoFilter;
                        break;
                    case "spray_coater":
                        if (entity.spraycoaterId > 0 && factory.cargoTraffic != null &&
                            entity.spraycoaterId < factory.cargoTraffic.spraycoaterPool.Length)
                            iconItem = factory.cargoTraffic.spraycoaterPool[entity.spraycoaterId].incItemId;
                        break;
                    case "dispenser":
                        if (entity.dispenserId > 0 && factory.transport != null &&
                            entity.dispenserId < factory.transport.dispenserPool.Length)
                            iconItem = factory.transport.dispenserPool[entity.dispenserId].filter;
                        break;
                    case "turret":
                        if (entity.turretId > 0 && factory.defenseSystem != null &&
                            entity.turretId < factory.defenseSystem.turrets.buffer.Length)
                            iconItem = factory.defenseSystem.turrets.buffer[entity.turretId].itemId;
                        break;
                    case "ejector":
                        if (entity.ejectorId > 0 && factory.factorySystem != null &&
                            entity.ejectorId < factory.factorySystem.ejectorPool.Length)
                            iconItem = factory.factorySystem.ejectorPool[entity.ejectorId].bulletId;
                        break;
                    case "silo":
                        if (entity.siloId > 0 && factory.factorySystem != null &&
                            entity.siloId < factory.factorySystem.siloPool.Length)
                            iconItem = factory.factorySystem.siloPool[entity.siloId].bulletId;
                        break;
                    case "belt":
                        iconItem = BeltItem(factory, entity);
                        break;
                    case "tank":
                        if (entity.tankId > 0 && factory.factoryStorage != null &&
                            entity.tankId < factory.factoryStorage.tankPool.Length)
                            iconItem = factory.factoryStorage.tankPool[entity.tankId].fluidId;
                        break;
                    case "storage":
                    case "battle_base":
                        iconItem = FirstStorageItem(factory, entity);
                        break;
                }
                if (iconItem > 0 && LDB.items != null)
                {
                    var proto = LDB.items.Select(iconItem);
                    if (proto != null) target.RecipeIcon = proto.iconSprite;
                }
            }
            catch { }
        }

        private static int RecipeItem(int recipeId)
        {
            if (recipeId <= 0 || LDB.recipes == null) return 0;
            var recipe = LDB.recipes.Select(recipeId);
            return recipe != null ? recipe.Items[0] : 0;
        }

        private static int BeltItem(PlanetFactory factory, EntityData entity)
        {
            if (entity.beltId <= 0 || factory.cargoTraffic == null) return 0;
            var belt = factory.cargoTraffic.beltPool[entity.beltId];
            var path = factory.cargoTraffic.GetCargoPath(belt.segPathId);
            if (path == null) return 0;
            int pivot = belt.segIndex + belt.segPivotOffset;
            for (int i = 0; i < path.pathLength; i++)
            {
                int index = (pivot + i) % path.pathLength;
                if (path.buffer[index] == CargoPath.kCargoHead)
                {
                    if (path.GetCargoAtIndex(index, out Cargo cargo, out _, out _) && cargo.item > 0)
                        return cargo.item;
                }
            }
            return 0;
        }

        private static int FirstStorageItem(PlanetFactory factory, EntityData entity)
        {
            if (entity.storageId <= 0 || factory.factoryStorage == null) return 0;
            var st = factory.factoryStorage.storagePool[entity.storageId];
            if (st == null || st.grids == null) return 0;
            for (int i = 0; i < st.grids.Length; i++)
                if (st.grids[i].itemId > 0) return st.grids[i].itemId;
            return 0;
        }

        private static float PowerRatio(PlanetFactory factory, int pcId)
        {
            if (factory.powerSystem == null || pcId <= 0 || pcId >= factory.powerSystem.consumerPool.Length)
                return 0f;
            var consumer = factory.powerSystem.consumerPool[pcId];
            int netId = consumer.networkId;
            if (netId <= 0 || netId >= factory.powerSystem.netPool.Length) return 0f;
            var net = factory.powerSystem.netPool[netId];
            return net != null ? (float)net.consumerRatio : 0f;
        }

        private static string PowerLine(PlanetFactory factory, int pcId, out float ratio)
        {
            ratio = PowerRatio(factory, pcId);
            long served = 0;
            if (factory.powerSystem != null && pcId > 0 && pcId < factory.powerSystem.consumerPool.Length)
                served = (long)(factory.powerSystem.consumerPool[pcId].requiredEnergy * 60L * ratio + 0.5);
            return ratio <= PowerSystem.kOffPower ? "未供电" : Watt(served) + "|" + Pct(ratio);
        }

        private static string Pct(float ratio) => ((int)(ratio * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

        private static string Watt(long watt)
        {
            if (watt >= 1000000000L) return (watt / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "GW";
            if (watt >= 1000000L) return (watt / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "MW";
            if (watt >= 1000L) return (watt / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "kW";
            return watt.ToString(CultureInfo.InvariantCulture) + "W";
        }

        private static string Energy(long joule)
        {
            if (joule >= 1000000000L) return (joule / 1000000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "GJ";
            if (joule >= 1000000L) return (joule / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "MJ";
            if (joule >= 1000L) return (joule / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "kJ";
            return joule.ToString(CultureInfo.InvariantCulture) + "J";
        }

        private static string Assembler(PlanetFactory factory, EntityData entity)
        {
            if (entity.assemblerId <= 0 || factory.factorySystem == null ||
                entity.assemblerId >= factory.factorySystem.assemblerPool.Length) return string.Empty;
            ref var a = ref factory.factorySystem.assemblerPool[entity.assemblerId];
            if (a.recipeExecuteData == null || a.recipeExecuteData.timeSpend <= 0) return string.Empty;
            float ratio = PowerRatio(factory, a.pcId);
            long served = 0;
            if (factory.powerSystem != null && a.pcId > 0 && a.pcId < factory.powerSystem.consumerPool.Length)
                served = (long)(factory.powerSystem.consumerPool[a.pcId].requiredEnergy * 60L * ratio + 0.5);
            float period = (float)((double)a.recipeExecuteData.timeSpend / 600000.0);
            float perMin = period > 0f ? 60f / period : 0f;
            float speed = (float)(0.0001 * a.speedOverride * ratio);
            if (!a.replicating) speed = 0f;
            perMin *= speed < 0.00001f || a.speedOverride < 0.0001f
                ? 0f
                : speed * (1f + a.extraSpeed * 0.1f / a.speedOverride);
            bool productive = a.recipeExecuteData.productive && !a.forceAccMode;
            double inc = 0;
            if (a.speed > 0) inc = productive ? (double)(a.extraSpeed * 10.0) / a.speed : (double)(a.speedOverride * 100.0) / a.speed - 100.0;
            string status;
            if (a.replicating) status = ratio == 1f ? "正常运转" : ratio > PowerSystem.kOffPower ? "电力不足" : "停止运转";
            else if (a.time >= a.recipeExecuteData.timeSpend) status = "产物堆积";
            else
            {
                bool lack = false;
                var needs = a.recipeExecuteData.requireCounts;
                for (int i = 0; i < needs.Length; i++)
                    if (i >= a.served.Length || a.served[i] < needs[i]) { lack = true; break; }
                status = lack ? "缺少原材料" : ratio == 1f ? "待机" : ratio > PowerSystem.kOffPower ? "电力不足" : "停止运转";
            }
            string power = ratio <= PowerSystem.kOffPower ? "未供电" : Watt(served) + "|" + Pct(ratio);
            return string.Format(CultureInfo.InvariantCulture,
                "生产速度: {0:0.0}/min\n电力: {1}\n增产效果: +{2:0.0}% {3}\n状态: {4}",
                perMin, power, inc, productive ? "额外产出" : "加速生产", status);
        }

        private static string Miner(PlanetFactory factory, EntityData entity)
        {
            if (entity.minerId <= 0 || factory.factorySystem == null) return string.Empty;
            ref var m = ref factory.factorySystem.minerPool[entity.minerId];
            if (m.period <= 0) return string.Empty;
            float ratio = PowerRatio(factory, m.pcId);
            float period = (float)((double)m.period / 600000.0);
            float perMin = period > 0f ? 60f / period : 0f;
            float speed = (float)(0.0001 * m.speed * m.speedDamper * ratio * GameMain.history.miningSpeedScale);
            if (m.type == EMinerType.Vein) speed *= m.veinCount;
            if (m.workstate <= 0 || ratio < PowerSystem.kOffPower) speed = 0f;
            perMin *= speed;
            string status = m.workstate <= 0 ? "待机" : ratio < PowerSystem.kOffPower ? "电力不足" : "正常运转";
            if (m.veinCount <= 0) status = "缺少矿脉";
            string power = PowerLine(factory, m.pcId, out _);
            return string.Format(CultureInfo.InvariantCulture,
                "生产速度: {0:0.0}/min\n电力: {1}\n状态: {2}", perMin, power, status);
        }

        private static string Belt(PlanetFactory factory, EntityData entity)
        {
            if (entity.beltId <= 0 || factory.cargoTraffic == null) return string.Empty;
            var belt = factory.cargoTraffic.beltPool[entity.beltId];
            var path = factory.cargoTraffic.GetCargoPath(belt.segPathId);
            int nodes = 0, cargoCount = 0;
            if (path != null)
            {
                nodes = path.belts != null ? path.belts.Count : 0;
                for (int i = 0; i < path.pathLength; i++)
                    if (path.buffer[i] == CargoPath.kCargoHead) { cargoCount++; i += CargoPath.kCargoLength - 1; }
            }
            int pivot = belt.segIndex + belt.segPivotOffset;
            return string.Format(CultureInfo.InvariantCulture,
                "线路: #{0}\n线路节点数: {1}\n线路货物数: {2}\n区间: [{3},{4}]@{5}",
                belt.segPathId, nodes, cargoCount, belt.segIndex, belt.segIndex + belt.segLength - 1, pivot);
        }

        private static string Inserter(PlanetFactory factory, EntityData entity)
        {
            if (entity.inserterId <= 0 || factory.factorySystem == null) return string.Empty;
            ref var ins = ref factory.factorySystem.inserterPool[entity.inserterId];
            string power = PowerLine(factory, ins.pcId, out _);
            string status;
            switch (ins.stage)
            {
                case EInserterStage.Picking: status = ins.careNeeds ? "等待货物" : "待机中"; break;
                case EInserterStage.Sending: status = "运送中"; break;
                case EInserterStage.Inserting: status = "等待卸货"; break;
                case EInserterStage.Returning: status = ins.bidirectional ? "运送中" : "返回中"; break;
                default: status = "待机中"; break;
            }
            if (ins.pickTarget == 0 && ins.insertTarget != 0) status = "输入端失效";
            else if (ins.insertTarget == 0 && ins.pickTarget != 0) status = "输出端失效";
            else if (ins.insertTarget == 0 && ins.pickTarget == 0) status = "连接失效";
            var proto = LDB.items != null ? LDB.items.Select(entity.protoId) : null;
            int stt = proto != null && proto.prefabDesc != null && proto.prefabDesc.inserterSTT > 0 ? proto.prefabDesc.inserterSTT : 200000;
            double length = (double)ins.stt / stt;
            double rtt = ins.stt > 0 ? 300000.0 / ins.stt : 0.0;
            return string.Format(CultureInfo.InvariantCulture,
                "电力: {0}\n状态: {1}\n长度: {2:0.#}格\n速度: {3:0.00}往返/秒",
                power, status, length, rtt);
        }

        private static string Splitter(PlanetFactory factory, EntityData entity)
        {
            if (entity.splitterId <= 0 || factory.cargoTraffic == null) return string.Empty;
            ref var sp = ref factory.cargoTraffic.splitterPool[entity.splitterId];
            var sb = new StringBuilder(160);
            int[] inputs = { sp.input0, sp.input1, sp.input2, sp.input3 };
            int[] outputs = { sp.output0, sp.output1, sp.output2, sp.output3 };
            for (int i = 0; i < 4; i++)
            {
                bool input = inputs[i] > 0;
                bool output = outputs[i] > 0;
                string kind = input && output ? "输入/输出" : input ? "输入" : output ? "输出" : "未连接";
                if (input && sp.inPriority) kind = "输入优先";
                else if (output && sp.outPriority) kind = "输出优先";
                if (i > 0) sb.Append('\n');
                sb.Append(i + 1).Append("号接口. ").Append(kind);
            }
            return sb.ToString();
        }

        private static string Monitor(PlanetFactory factory, EntityData entity)
        {
            if (entity.monitorId <= 0 || factory.cargoTraffic == null) return string.Empty;
            ref var mo = ref factory.cargoTraffic.monitorPool[entity.monitorId];
            double flow = mo.cargoFlow;
            string condition = mo.targetCargoBytes > 0 && flow >= mo.targetCargoBytes ? "满足" : "不满足";
            return string.Format(CultureInfo.InvariantCulture,
                "每分钟流量: {0:0.0}\n目标流量: {1:0.0} 个\n监测条件: {2}",
                flow, mo.targetCargoBytes, condition);
        }

        private static string Spray(PlanetFactory factory, EntityData entity)
        {
            if (entity.spraycoaterId <= 0 || factory.cargoTraffic == null) return string.Empty;
            ref var sp = ref factory.cargoTraffic.spraycoaterPool[entity.spraycoaterId];
            string power = PowerLine(factory, sp.pcId, out float ratio);
            string status = ratio < PowerSystem.kOffPower ? "电力不足" : sp.incCount > 0 ? "正常运转" : "缺少增产剂";
            return string.Format(CultureInfo.InvariantCulture,
                "电力: {0}\n状态: {1}\n储量: {2}", power, status, sp.incCount);
        }

        private static string Storage(PlanetFactory factory, EntityData entity)
        {
            if (entity.storageId <= 0 || factory.factoryStorage == null) return string.Empty;
            var st = factory.factoryStorage.storagePool[entity.storageId];
            if (st == null || st.grids == null) return string.Empty;
            var sb = new StringBuilder(160);
            int shown = 0;
            for (int i = 0; i < st.grids.Length && shown < 5; i++)
            {
                int itemId = st.grids[i].itemId;
                if (itemId <= 0) continue;
                var proto = LDB.items != null ? LDB.items.Select(itemId) : null;
                if (proto == null) continue;
                if (shown > 0) sb.Append(' ');
                sb.Append(proto.iconTagString).Append("<sub>").Append(st.grids[i].count).Append("</sub>");
                shown++;
            }
            if (shown == 0) return "空栏位";
            return sb.ToString();
        }

        private static string Dispenser(PlanetFactory factory, EntityData entity)
        {
            if (entity.dispenserId <= 0 || factory.transport == null) return string.Empty;
            ref var d = ref factory.transport.dispenserPool[entity.dispenserId];
            var proto = LDB.items != null ? LDB.items.Select(entity.protoId) : null;
            string power = Energy(d.energy) + "|" + Watt(d.energyPerTick * 60L);
            int maxCourier = d.workCourierDatas != null ? d.workCourierDatas.Length : 0;
            return string.Format(CultureInfo.InvariantCulture,
                "向伊卡洛斯回收 - 回收所有类型\n电力: {0}\n配送运输机数量: {1}/{2}",
                power, d.idleCourierCount + d.workCourierCount, maxCourier);
        }

        private static string Station(PlanetFactory factory, EntityData entity)
        {
            if (entity.stationId <= 0 || factory.transport == null) return string.Empty;
            var st = factory.transport.stationPool[entity.stationId];
            if (st == null || st.storage == null) return string.Empty;
            var sb = new StringBuilder(220);
            int shown = 0;
            for (int i = 0; i < st.storage.Length && shown < 4; i++)
            {
                var s = st.storage[i];
                if (s.itemId <= 0) { if (i == 0) sb.Append("空栏位\n"); continue; }
                var proto = LDB.items != null ? LDB.items.Select(s.itemId) : null;
                if (proto == null) continue;
                if (shown > 0) sb.Append('\n');
                sb.Append(proto.iconTagString).Append(' ').Append(s.count).Append('/').Append(s.max);
                if (s.localLogic == ELogisticStorage.Demand) sb.Append(" 本地需求");
                else if (s.localLogic == ELogisticStorage.Supply) sb.Append(" 本地供应");
                if (s.remoteLogic == ELogisticStorage.Supply) sb.Append(" 星际供应");
                else if (s.remoteLogic == ELogisticStorage.Demand) sb.Append(" 星际需求");
                shown++;
            }
            if (shown == 0) return "空栏位";
            return sb.ToString();
        }

        private static string PowerGenerator(PlanetFactory factory, EntityData entity)
        {
            if (entity.powerGenId <= 0 || factory.powerSystem == null) return string.Empty;
            ref var g = ref factory.powerSystem.genPool[entity.powerGenId];
            long outW = g.capacityCurrentTick * 60L;
            if (g.fuelMask > 0 && g.currentStrength == 0f) outW = 0;
            string load;
            if (g.networkId > 0 && g.networkId < factory.powerSystem.netPool.Length && factory.powerSystem.netPool[g.networkId] != null)
            {
                var net = factory.powerSystem.netPool[g.networkId];
                double use = net.consumerRatio == 1 ? net.generaterRatio : net.consumerRatio > 0.1 ? 1 / net.consumerRatio : 10;
                load = use >= 10 ? "无法负荷" : ((int)(use * 100)).ToString(CultureInfo.InvariantCulture) + "%";
            }
            else load = "0%";
            string status = g.useFuelPerTick > 0 && g.curFuelId == 0 && g.fuelEnergy == 0 && g.fuelId == 0 ? "需要燃料" : "正常运转";
            return string.Format(CultureInfo.InvariantCulture, "功率: {0}\n电网负荷: {1}\n状态: {2}", Watt(outW), load, status);
        }

        private static string PowerNode(PlanetFactory factory, EntityData entity)
        {
            if (factory.powerSystem == null) return string.Empty;
            int netId = 0;
            if (entity.beaconId > 0 && factory.defenseSystem != null &&
                entity.beaconId < factory.defenseSystem.beacons.buffer.Length)
            {
                int pnId = factory.defenseSystem.beacons.buffer[entity.beaconId].pnId;
                if (pnId > 0 && pnId < factory.powerSystem.nodePool.Length)
                    netId = factory.powerSystem.nodePool[pnId].networkId;
            }
            if (netId == 0 && entity.powerNodeId > 0 && entity.powerNodeId < factory.powerSystem.nodePool.Length)
                netId = factory.powerSystem.nodePool[entity.powerNodeId].networkId;
            else if (entity.powerAccId > 0 && entity.powerAccId < factory.powerSystem.accPool.Length)
                netId = factory.powerSystem.accPool[entity.powerAccId].networkId;
            else if (entity.powerExcId > 0 && entity.powerExcId < factory.powerSystem.excPool.Length)
                netId = factory.powerSystem.excPool[entity.powerExcId].networkId;
            if (netId <= 0 || netId >= factory.powerSystem.netPool.Length) return "电网 #0";
            var net = factory.powerSystem.netPool[netId];
            if (net == null) return "电网 #" + netId.ToString(CultureInfo.InvariantCulture);
            return string.Format(CultureInfo.InvariantCulture,
                "电网 #{0}\n发电性能: {1}\n耗电需求: {2}\n实际发电: {3}",
                netId, Watt(net.energyCapacity * 60), Watt(net.energyRequired * 60), Watt(net.energyServed * 60));
        }

        private static string Turret(PlanetFactory factory, EntityData entity)
        {
            if (entity.turretId <= 0 || factory.defenseSystem == null) return string.Empty;
            var turret = factory.defenseSystem.turrets.buffer[entity.turretId];
            float ratio = PowerRatio(factory, turret.pcId);
            string power = PowerLine(factory, turret.pcId, out _);
            string status = turret.itemCount == 0 && turret.bulletCount == 0 && turret.itemBulletCount == 0
                ? "缺少弹药" : ratio < PowerSystem.kOffPower ? "电力不足" : turret.isWorking ? "交战中" : "待机";
            double rate = 0;
            var proto = LDB.items != null ? LDB.items.Select(entity.protoId) : null;
            if (proto != null && proto.prefabDesc != null)
            {
                var p = proto.prefabDesc;
                double denom = p.turretRoundInterval + p.turretMuzzleInterval * (p.turretMuzzleCount - 1f);
                if (denom > 0) rate = p.turretROF * p.turretMuzzleCount * 60.0 / denom;
            }
            return string.Format(CultureInfo.InvariantCulture,
                "电力: {0}\n状态: {1}\n射速: {2:0.##}/s", power, status, rate);
        }

        private static string FieldGenerator(PlanetFactory factory, EntityData entity)
        {
            if (entity.fieldGenId <= 0 || factory.defenseSystem == null) return string.Empty;
            var f = factory.defenseSystem.fieldGenerators.buffer[entity.fieldGenId];
            string power = PowerLine(factory, f.pcId, out _);
            double holding = f.fieldHolding * 100.0;
            double fill = factory.planetATField != null ? factory.planetATField.globeFillRatio * 100.0 : holding;
            return string.Format(CultureInfo.InvariantCulture,
                "电力: {0}\n支撑力: {1:0.00}%\n全球填充度: {2:0.00}%", power, holding, fill);
        }

        private static string Ejector(PlanetFactory factory, EntityData entity)
        {
            if (entity.ejectorId <= 0 || factory.factorySystem == null) return string.Empty;
            ref var e = ref factory.factorySystem.ejectorPool[entity.ejectorId];
            string power = PowerLine(factory, e.pcId, out float ratio);
            string status = e.runtimeOrbitId == 0 ? "无可用轨道" : e.bulletCount == 0 ? "缺少弹射物" : ratio < PowerSystem.kOffPower ? "电力不足" : e.direction != 0 ? "发射中" : "待机";
            double perMin = e.chargeSpend + e.coldSpend > 0 ? 60.0 / (e.chargeSpend + e.coldSpend) * 600000.0 : 0;
            perMin *= Cargo.accTableMilli[e.incLevel] + 1.0;
            if (e.boost) perMin *= EjectorComponent.BOOST_SCALE;
            return string.Format(CultureInfo.InvariantCulture, "电力: {0}\n状态: {1}\n射速: {2:0.0}/分钟", power, status, perMin);
        }

        private static string Silo(PlanetFactory factory, EntityData entity)
        {
            if (entity.siloId <= 0 || factory.factorySystem == null) return string.Empty;
            ref var s = ref factory.factorySystem.siloPool[entity.siloId];
            string power = PowerLine(factory, s.pcId, out float ratio);
            string status = !s.hasNode ? "无可用轨道" : s.bulletCount == 0 ? "缺少火箭" : ratio < PowerSystem.kOffPower ? "电力不足" : s.direction != 0 ? (s.direction > 0 ? "充能中" : "冷却中") : "待机";
            double perMin = s.chargeSpend + s.coldSpend > 0 ? 60.0 / (s.chargeSpend + s.coldSpend) * 600000.0 : 0;
            perMin *= Cargo.accTableMilli[s.incLevel] + 1.0;
            if (s.boost) perMin *= SiloComponent.BOOST_SCALE;
            return string.Format(CultureInfo.InvariantCulture, "电力: {0}\n状态: {1}\n射速: {2:0.0}/分钟", power, status, perMin);
        }

        private static string Piler(PlanetFactory factory, EntityData entity)
        {
            if (entity.pilerId <= 0 || factory.cargoTraffic == null) return string.Empty;
            ref var p = ref factory.cargoTraffic.pilerPool[entity.pilerId];
            string power = PowerLine(factory, p.pcId, out float ratio);
            string status = ratio < PowerSystem.kOffPower ? "电力不足" : p.isWorkForward ? "堆叠中" : "拆包中";
            return string.Format(CultureInfo.InvariantCulture, "电力: {0}\n状态: {1}", power, status);
        }

        private static string Tank(PlanetFactory factory, EntityData entity)
        {
            if (entity.tankId <= 0 || factory.factoryStorage == null) return string.Empty;
            ref var t = ref factory.factoryStorage.tankPool[entity.tankId];
            var proto = t.fluidId > 0 && LDB.items != null ? LDB.items.Select(t.fluidId) : null;
            string label = proto != null ? proto.iconTagString + proto.name : "空";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} 储量: {1}/{2}", label, t.fluidCount, t.fluidCapacity);
        }

        private static string Lab(PlanetFactory factory, EntityData entity)
        {
            if (entity.labId <= 0 || factory.factorySystem == null) return string.Empty;
            ref var l = ref factory.factorySystem.labPool[entity.labId];
            var recipe = l.recipeId > 0 && LDB.recipes != null ? LDB.recipes.Select(l.recipeId) : null;
            float ratio = PowerRatio(factory, l.pcId);
            string power = PowerLine(factory, l.pcId, out _);
            double perMin = 0;
            if (recipe != null && recipe.TimeSpend > 0)
            {
                double period = recipe.TimeSpend / 600000.0;
                perMin = 60.0 / period * (0.0001 * l.speed * ratio) * (1 + l.extraSpeed * 0.1 / Math.Max(1, l.speed));
            }
            string status = ratio < PowerSystem.kOffPower ? "电力不足" : l.replicating ? "正常运转" : "待机";
            return string.Format(CultureInfo.InvariantCulture, "生产速度: {0:0.0}/min\n电力: {1}\n状态: {2}", perMin, power, status);
        }

        private static string Fractionator(PlanetFactory factory, EntityData entity)
        {
            if (entity.fractionatorId <= 0 || factory.factorySystem == null) return string.Empty;
            ref var f = ref factory.factorySystem.fractionatorPool[entity.fractionatorId];
            string power = PowerLine(factory, f.pcId, out float ratio);
            string status = ratio < PowerSystem.kOffPower ? "电力不足" : f.isWorking ? "正常运转" : "待机";
            string product = f.productId > 0 ? GameToolFormat.ItemName(f.productId) : "无";
            return string.Format(CultureInfo.InvariantCulture, "电力: {0}\n状态: {1}\n产物: {2}", power, status, product);
        }
    }
}
