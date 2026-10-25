using System;
using System.Collections.Generic;
using System.Globalization;
using IcarusII.Common.Json;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// “机甲 ↔ 目标”的方位指示：
    ///  - 行星/恒星：Player.navigation.indicatorAstroId（星图“方位指示”同源）；
    ///  - 建筑：跨行星时用原生导航线指向目标行星；落到该行星后切换为自绘 LineGizmo，
    ///    从机甲连到建筑。全部是瞬态数据，换档/断线/插件卸载时移除。
    /// </summary>
    internal static class GameTether
    {
        private const int MaxTargets = 20;

        /// <summary>一张方位指示卡需要的最小数据；名字一律来自游戏，不做显示层改写。</summary>
        public sealed class Target
        {
            public string Kind;
            public int TargetId;
            public int PlanetId;
            public string Name;
            public string Subtitle;
            public string Description;
            public string IconLabel;
            public Sprite MainIcon;
            public Sprite RecipeIcon;

            public string Key => Kind + ":" + PlanetId + ":" + TargetId;
        }

        private sealed class Entry
        {
            public Target Info;
            public LineGizmo Line;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static int _navAstroId;
        private static bool _navOwned;
        private static Target _lastAdded;

        public static Target LastAdded => _lastAdded;

        public static event Action<Target> Added;
        public static event Action Changed;

        public static bool HasAny => Entries.Count > 0;

        public static bool Contains(Target target)
        {
            if (target == null) return false;
            return Entries.Exists(e => e.Info.Key == target.Key);
        }

        public static List<Target> Snapshot()
        {
            var result = new List<Target>(Entries.Count);
            foreach (var entry in Entries) result.Add(entry.Info);
            return result;
        }

        public static void Clear()
        {
            foreach (var entry in Entries)
            {
                try { entry.Line?.Close(); } catch { /* 换档/退出时可能已销毁 */ }
            }
            Entries.Clear();
            try
            {
                var nav = GameMain.mainPlayer != null ? GameMain.mainPlayer.navigation : null;
                if (nav != null && _navOwned && nav.indicatorAstroId == _navAstroId)
                    nav.indicatorAstroId = 0;
            }
            catch { /* ignore */ }
            _navAstroId = 0;
            _navOwned = false;
            try { Changed?.Invoke(); } catch { /* UI 可能已销毁 */ }
        }

        /// <summary>矿脉卡片与建筑共用 tether：本星球画线，跨行星走原生导航。</summary>
        public static bool AddVein(PlanetData planet, int veinId, out string error)
        {
            error = null;
            try
            {
                if (planet == null || planet.factory == null || planet.factory.veinPool == null ||
                    veinId <= 0 || veinId >= planet.factory.veinPool.Length)
                { error = "找不到矿脉 #" + veinId.ToString(CultureInfo.InvariantCulture); return false; }
                var vein = planet.factory.veinPool[veinId];
                if (vein.id != veinId) { error = "矿脉已失效"; return false; }
                var proto = LDB.veins != null ? LDB.veins.Select((int)vein.type) : null;
                var n = vein.pos.sqrMagnitude > 0.000001f ? vein.pos.normalized : Vector3.up;
                float lat = Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg;
                float lon = Mathf.Atan2(n.x, -n.z) * Mathf.Rad2Deg;
                string title = proto != null ? proto.name : GameToolFormat.VeinName(vein.type);
                var target = new Target
                {
                    Kind = "vein",
                    TargetId = veinId,
                    PlanetId = planet.id,
                    Name = title,
                    Subtitle = "（储量 " + vein.amount.ToString(CultureInfo.InvariantCulture) + "，纬度 " +
                        lat.ToString("0.00", CultureInfo.InvariantCulture) + "°，经度 " +
                        lon.ToString("0.00", CultureInfo.InvariantCulture) + "°）",
                    Description = (planet.star != null ? planet.star.displayName : string.Empty) + " · " + planet.displayName,
                    IconLabel = "#" + veinId.ToString(CultureInfo.InvariantCulture),
                    MainIcon = proto != null ? proto.iconSprite : null,
                };
                if (Entries.Exists(e => e.Info.Key == target.Key))
                { error = "该矿脉已经在方位指示中"; return false; }
                if (Entries.Count >= MaxTargets)
                { error = "方位指示最多 " + MaxTargets + " 个，请先清除一些"; return false; }
                Entries.Add(new Entry { Info = target });
                _lastAdded = target;
                try { Added?.Invoke(target); Changed?.Invoke(); } catch { }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool AddPlanet(PlanetData planet, out string error)
        {
            error = null;
            try
            {
                if (GameMain.mainPlayer == null || GameMain.mainPlayer.navigation == null)
                { error = "机甲导航尚未就绪"; return false; }
                Sprite planetIcon = null;
                try
                {
                    var theme = LDB.themes != null ? LDB.themes.Select(planet.theme) : null;
                    if (theme != null && theme.uiIconSprite != null && theme.uiIconSprite.Length > 0)
                        planetIcon = theme.uiIconSprite[planet.style % theme.uiIconSprite.Length];
                }
                catch { /* 主题资源未加载 */ }
                string memo = null;
                try
                {
                    var factory = planet.factory;
                    if (factory != null && factory.digitalSystem != null && factory.digitalSystem.planetTodo != null)
                        memo = factory.digitalSystem.planetTodo.content;
                }
                catch { /* 未初始化 */ }
                var target = new Target
                {
                    Kind = "planet",
                    TargetId = planet.id,
                    PlanetId = planet.id,
                    Name = planet.displayName,
                    Subtitle = planet.star != null ? planet.star.displayName : string.Empty,
                    Description = string.IsNullOrEmpty(memo) ? "暂无备忘录" : memo,
                    IconLabel = string.IsNullOrEmpty(planet.typeString) ? planet.displayName : planet.typeString,
                    MainIcon = planetIcon,
                };
                if (Entries.Exists(e => e.Info.Key == target.Key))
                { error = "该星球已经在方位指示中"; return false; }
                if (Entries.Count >= MaxTargets)
                { error = "方位指示最多 " + MaxTargets + " 个，请先清除一些"; return false; }
                Entries.Add(new Entry { Info = target });
                _lastAdded = target;
                try { Added?.Invoke(target); Changed?.Invoke(); } catch { }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool AddBuilding(PlanetFactory factory, PlanetData planet, int entityId, out string error)
        {
            error = null;
            try
            {
                if (factory == null || !EntityToolUtil.HasEntity(factory, entityId))
                { error = "找不到建筑 #" + entityId.ToString(CultureInfo.InvariantCulture); return false; }
                var entity = factory.entityPool[entityId];
                if (entity.id != entityId) { error = "建筑已失效"; return false; }
                Sprite buildingIcon = null;
                Sprite recipeIcon = null;
                try
                {
                    var item = LDB.items != null ? LDB.items.Select(entity.protoId) : null;
                    if (item != null) buildingIcon = item.iconSprite;
                    if (entity.assemblerId > 0 && factory.factorySystem != null &&
                        entity.assemblerId < factory.factorySystem.assemblerPool.Length)
                    {
                        int recipeId = factory.factorySystem.assemblerPool[entity.assemblerId].recipeId;
                        var recipe = recipeId > 0 && LDB.recipes != null ? LDB.recipes.Select(recipeId) : null;
                        if (recipe != null) recipeIcon = recipe.iconSprite;
                    }
                }
                catch { /* 图标资源未加载 */ }
                var target = new Target
                {
                    Kind = "building",
                    TargetId = entityId,
                    PlanetId = planet.id,
                    Name = EntityToolUtil.ProtoName(entity.protoId),
                    Subtitle = planet.displayName,
                    Description = GameLinkCardInfo.Describe(factory, entity),
                    IconLabel = "#" + entityId.ToString(CultureInfo.InvariantCulture),
                    MainIcon = buildingIcon,
                    RecipeIcon = recipeIcon,
                };
                if (entity.assemblerId <= 0)
                    GameLinkCardInfo.ResolveIcons(factory, entity, target);
                if (Entries.Exists(e => e.Info.Key == target.Key))
                { error = "该建筑已经在方位指示中"; return false; }
                if (Entries.Count >= MaxTargets)
                { error = "方位指示最多 " + MaxTargets + " 个，请先清除一些"; return false; }
                Entries.Add(new Entry { Info = target });
                _lastAdded = target;
                try { Added?.Invoke(target); Changed?.Invoke(); } catch { }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>卡片按钮取消/重新连线用：记忆目标不需要先加载工厂。</summary>
        public static bool ReAdd(Target target)
        {
            if (target == null || Contains(target) || Entries.Count >= MaxTargets) return false;
            Entries.Add(new Entry { Info = target });
            try { Added?.Invoke(target); Changed?.Invoke(); } catch { }
            return true;
        }

        public static bool Remove(Target target)
        {
            if (target == null) return false;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Info.Key != target.Key) continue;
                try { Entries[i].Line?.Close(); } catch { }
                Entries.RemoveAt(i);
                try
                {
                    var nav = GameMain.mainPlayer != null ? GameMain.mainPlayer.navigation : null;
                    if (nav != null && _navOwned && nav.indicatorAstroId == targetPlanetAstro(target))
                        nav.indicatorAstroId = 0;
                }
                catch { }
                _navAstroId = 0;
                _navOwned = false;
                try { Changed?.Invoke(); } catch { }
                return true;
            }
            return false;
        }

        /// <summary>聊天窗打开时刷新卡片快照：名称/副标题/描述/图标各采样一次。</summary>
        public static void RefreshInfo(Target target)
        {
            if (target == null) return;
            try
            {
                if (target.Kind == "planet")
                {
                    var planet = GameMain.galaxy != null ? GameMain.galaxy.PlanetById(target.PlanetId) : null;
                    if (planet == null) return;
                    target.Name = planet.displayName;
                    target.Subtitle = planet.star != null ? planet.star.displayName : string.Empty;
                    target.Description = PlanetMemo(planet);
                    var theme = LDB.themes != null ? LDB.themes.Select(planet.theme) : null;
                    if (theme != null && theme.uiIconSprite != null && theme.uiIconSprite.Length > 0)
                        target.MainIcon = theme.uiIconSprite[planet.style % theme.uiIconSprite.Length];
                    return;
                }
                if (target.Kind == "vein")
                {
                    var vp = GameMain.galaxy != null ? GameMain.galaxy.PlanetById(target.PlanetId) : null;
                    if (vp != null) ApplyVein(target, vp, target.TargetId);
                    return;
                }
                if (target.Kind != "building") return;
                var p = GameMain.galaxy != null ? GameMain.galaxy.PlanetById(target.PlanetId) : null;
                var factory = p != null ? p.factory : null;
                if (factory == null || !EntityToolUtil.HasEntity(factory, target.TargetId)) return;
                var entity = factory.entityPool[target.TargetId];
                target.Name = EntityToolUtil.ProtoName(entity.protoId);
                target.Subtitle = p.displayName;
                target.Description = GameLinkCardInfo.Describe(factory, entity);
                GameLinkCardInfo.ResolveIcons(factory, entity, target);
            }
            catch { }
        }

        private static void ApplyVein(Target target, PlanetData planet, int veinId)
        {
            var factory = planet != null ? planet.factory : null;
            if (factory == null || factory.veinPool == null || veinId <= 0 || veinId >= factory.veinPool.Length) return;
            var vein = factory.veinPool[veinId];
            if (vein.id != veinId) return;
            var proto = LDB.veins != null ? LDB.veins.Select((int)vein.type) : null;
            var n = vein.pos.sqrMagnitude > 0.000001f ? vein.pos.normalized : Vector3.up;
            float lat = Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lon = Mathf.Atan2(n.x, -n.z) * Mathf.Rad2Deg;
            target.Kind = "vein";
            target.TargetId = veinId;
            target.PlanetId = planet.id;
            target.Name = proto != null ? proto.name : GameToolFormat.VeinName(vein.type);
            target.Subtitle = "（储量 " + vein.amount.ToString(CultureInfo.InvariantCulture) + "，纬度 " +
                lat.ToString("0.00", CultureInfo.InvariantCulture) + "°，经度 " +
                lon.ToString("0.00", CultureInfo.InvariantCulture) + "°）";
            target.Description = (planet.star != null ? planet.star.displayName : string.Empty) + " · " + planet.displayName;
            target.IconLabel = "#" + veinId.ToString(CultureInfo.InvariantCulture);
            target.MainIcon = proto != null ? proto.iconSprite : null;
        }

        private static string PlanetMemo(PlanetData planet)
        {
            try
            {
                var factory = planet.factory;
                if (factory != null && factory.digitalSystem != null && factory.digitalSystem.planetTodo != null)
                {
                    var text = factory.digitalSystem.planetTodo.content;
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch { }
            return "暂无备忘录";
        }

        /// <summary>历史回放的 link 卡只有 ID；按当前存档资源补图标，找不到就不显示。</summary>
        public static void ResolveIcons(Target target)
        {
            if (target == null) return;
            try
            {
                if (target.Kind == "planet")
                {
                    var planet = GameMain.galaxy != null ? GameMain.galaxy.PlanetById(target.PlanetId) : null;
                    var theme = planet != null && LDB.themes != null ? LDB.themes.Select(planet.theme) : null;
                    if (theme != null && theme.uiIconSprite != null && theme.uiIconSprite.Length > 0)
                        target.MainIcon = theme.uiIconSprite[planet.style % theme.uiIconSprite.Length];
                    return;
                }
                if (target.Kind == "vein")
                {
                    var vp = GameMain.galaxy != null ? GameMain.galaxy.PlanetById(target.PlanetId) : null;
                    if (vp != null) ApplyVein(target, vp, target.TargetId);
                    return;
                }
                if (target.Kind != "building") return;
                var factory = GameMain.galaxy != null && GameMain.galaxy.PlanetById(target.PlanetId) != null
                    ? GameMain.galaxy.PlanetById(target.PlanetId).factory : null;
                if (factory == null || !EntityToolUtil.HasEntity(factory, target.TargetId)) return;
                var entity = factory.entityPool[target.TargetId];
                GameLinkCardInfo.ResolveIcons(factory, entity, target);
            }
            catch { /* 资源未加载 */ }
        }

        /// <summary>
        /// 按游戏 UIAssemblerWindow 的口径生成建筑卡描述：
        /// 生产速度、电力（实际供给/比例）、增产效果、状态。
        /// 没有装配器的建筑返回普通建筑 id，避免伪造数据。
        /// </summary>
        private static string BuildAssemblerDescription(PlanetFactory factory, EntityData entity)
        {
            if (factory == null || factory.factorySystem == null || factory.powerSystem == null ||
                entity.assemblerId <= 0 || entity.assemblerId >= factory.factorySystem.assemblerPool.Length)
                return "建筑 #" + entity.id.ToString(CultureInfo.InvariantCulture);

            ref var asm = ref factory.factorySystem.assemblerPool[entity.assemblerId];
            if (asm.recipeExecuteData == null || asm.recipeExecuteData.timeSpend <= 0)
                return "建筑 #" + entity.id.ToString(CultureInfo.InvariantCulture);

            float powerRatio = 0f;
            long servedWatt = 0;
            try
            {
                var consumer = factory.powerSystem.consumerPool[asm.pcId];
                int netId = consumer.networkId;
                var net = factory.powerSystem.netPool[netId];
                powerRatio = net != null && netId > 0 ? (float)net.consumerRatio : 0f;
                servedWatt = (long)(consumer.requiredEnergy * 60L * powerRatio + 0.5);
            }
            catch { }

            string power = powerRatio <= PowerSystem.kOffPower
                ? "未供电"
                : FormatWatt(servedWatt) + "|" + ((int)(powerRatio * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

            float period = (float)((double)asm.recipeExecuteData.timeSpend / 600000.0);
            float productPerMin = period > 0f ? 60f / period : 0f;
            float speed = (float)(0.0001 * asm.speedOverride * powerRatio);
            if (!asm.replicating) speed = 0f;
            productPerMin *= speed < 0.00001f ? 0f : speed * (1f + asm.extraSpeed * 0.1f / asm.speedOverride);

            bool productive = asm.recipeExecuteData.productive && !asm.forceAccMode;
            double inc = 0.0;
            if (asm.speed > 0)
                inc = productive ? (double)(asm.extraSpeed * 10.0) / asm.speed
                                 : (double)(asm.speedOverride * 100.0) / asm.speed - 100.0;

            string status;
            if (asm.replicating)
                status = powerRatio == 1f ? "正常运转" : powerRatio > PowerSystem.kOffPower ? "电力不足" : "停止运转";
            else if (asm.time >= asm.recipeExecuteData.timeSpend)
                status = "产物堆积";
            else
            {
                bool lack = false;
                var needs = asm.recipeExecuteData.requireCounts;
                for (int i = 0; i < needs.Length; i++)
                {
                    if (i >= asm.served.Length || asm.served[i] < needs[i]) { lack = true; break; }
                }
                if (lack) status = "缺少原材料";
                else status = powerRatio == 1f ? "待机" : powerRatio > PowerSystem.kOffPower ? "电力不足" : "停止运转";
            }

            return string.Format(CultureInfo.InvariantCulture,
                "生产速度: {0:0.0}/min\n电力: {1}\n增产效果: +{2:0.0}% {3}\n状态: {4}",
                productPerMin, power, inc, productive ? "额外产出" : "加速生产", status);
        }

        private static string FormatWatt(long watt)
        {
            if (watt >= 1000000L) return (watt / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "MW";
            if (watt >= 1000L) return (watt / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "kW";
            return watt.ToString(CultureInfo.InvariantCulture) + "W";
        }

        private static int targetPlanetAstro(Target target)
        {
            try
            {
                var planet = GameMain.galaxy != null ? GameMain.galaxy.PlanetById(target.PlanetId) : null;
                return planet != null ? planet.astroId : 0;
            }
            catch { return 0; }
        }

        private static LineGizmo CreateLine(Vector3 start, Vector3 end)
        {
            var line = LineGizmo.Create(1, start, end);
            line.autoRefresh = true;
            line.multiplier = 1.5f;
            line.alphaMultiplier = 0.3f;
            line.width = 0.6f;
            line.color = Configs.builtin.gizmoColors != null && Configs.builtin.gizmoColors.Length > 4
                ? Configs.builtin.gizmoColors[4] : Color.cyan;
            line.spherical = false;
            line.Open();
            return line;
        }

        /// <summary>
        /// 多目标：本星球的建筑各画一条线；跨行星目标由原生 navigation 指向其中一个
        /// 目标行星（游戏本身一次只支持一个导航朝向）。落到某颗目标行星后，该行星上的
        /// 所有建筑都切换为自绘线。
        /// </summary>
        public static void Update()
        {
            try
            {
                var player = GameMain.mainPlayer;
                if (player == null) return;
                var localPlanet = GameMain.localPlanet;
                int localAstroId = localPlanet != null ? localPlanet.astroId : 0;
                bool removed = false;

                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    var entry = Entries[i];
                    if (entry.Info.Kind != "building" && entry.Info.Kind != "vein") continue;
                    if (entry.Info.PlanetId != player.planetId || localPlanet == null || localPlanet.factory == null)
                    {
                        if (entry.Line != null) { entry.Line.Close(); entry.Line = null; }
                        continue;
                    }
                    Vector3 end;
                    if (entry.Info.Kind == "building")
                    {
                        if (!EntityToolUtil.HasEntity(localPlanet.factory, entry.Info.TargetId))
                        {
                            if (entry.Line != null) entry.Line.Close();
                            Entries.RemoveAt(i);
                            removed = true;
                            continue;
                        }
                        end = localPlanet.factory.entityPool[entry.Info.TargetId].pos;
                    }
                    else
                    {
                        if (localPlanet.factory.veinPool == null ||
                            entry.Info.TargetId >= localPlanet.factory.veinPool.Length ||
                            localPlanet.factory.veinPool[entry.Info.TargetId].id != entry.Info.TargetId)
                        {
                            if (entry.Line != null) entry.Line.Close();
                            Entries.RemoveAt(i);
                            removed = true;
                            continue;
                        }
                        end = localPlanet.factory.veinPool[entry.Info.TargetId].pos;
                    }
                    if (entry.Line == null) entry.Line = CreateLine(player.position, end);
                    if (!entry.Line.gameObject.activeSelf) entry.Line.Open();
                    entry.Line.startPoint = player.position;
                    entry.Line.endPoint = end;
                }
                if (removed) { try { Changed?.Invoke(); } catch { } }

                // 原生导航一次只能指向一个星球：优先最新添加的、不在当前星球的星球/建筑目标。
                int desiredAstro = 0;
                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    var entry = Entries[i];
                    var astro = targetPlanetAstro(entry.Info);
                    if (astro <= 0 || astro == localAstroId) continue;
                    desiredAstro = astro;
                    break;
                }

                var nav = player.navigation;
                if (nav == null) return;
                if (desiredAstro > 0)
                {
                    if (nav.indicatorAstroId != desiredAstro)
                    {
                        nav.indicatorAstroId = desiredAstro;
                        _navAstroId = desiredAstro;
                        _navOwned = true;
                    }
                }
                else if (_navOwned && nav.indicatorAstroId == _navAstroId)
                {
                    nav.indicatorAstroId = 0;
                    _navAstroId = 0;
                    _navOwned = false;
                }
            }
            catch { /* 切换场景瞬间忽略 */ }
        }

        public static JsonValue List()
        {
            var rows = JsonValue.NewArray();
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                var row = JsonValue.NewObject();
                row.Set("kind", entry.Info.Kind);
                row.Set("targetId", entry.Info.TargetId);
                row.Set("planetId", entry.Info.PlanetId);
                row.Set("name", entry.Info.Name);
                rows.Add(row);
            }
            var data = JsonValue.NewObject();
            data.Set("active", rows.Count > 0);
            data.Set("items", rows);
            data.Set("maxTargets", MaxTargets);
            return data;
        }

        /// <summary>
        /// 清理旧版本/异常数据：DASHBOARD_TRACKING 的 objectId 必须是有效 statPlan，
        /// 否则 UIWarningWindow.OnLateUpdate 会 NRE。换档进入时调用一次。
        /// </summary>
        public static void SanitizeInvalidWarnings()
        {
            try
            {
                var data = GameMain.data;
                var warningSystem = data != null ? data.warningSystem : null;
                var charts = data != null && data.statistics != null ? data.statistics.charts : null;
                var plans = charts != null ? charts.statPlans : null;
                if (warningSystem == null || warningSystem.warningPool == null) return;
                for (int id = 1; id < warningSystem.warningPool.Length; id++)
                {
                    var warning = warningSystem.warningPool[id];
                    if (warning.id != id) continue;
                    if (warning.factoryId != WarningData.DASHBOARD_TRACKING ||
                        warning.signalId != WarningData.DASHBOARD_SIGNALID) continue;
                    if (plans == null || plans.buffer == null || warning.objectId <= 0 ||
                        warning.objectId >= plans.buffer.Length || plans.buffer[warning.objectId] == null)
                        warningSystem.RemoveWarningData(id);
                }
            }
            catch { /* 进入存档早期系统尚未就绪 */ }
        }
    }

    internal sealed class TetherAddTool : WriteToolBase
    {
        public override string Name => "game_tether_add";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null) throw Fail("当前不在游戏内");
            var kind = (args["kind"].AsString("") ?? string.Empty).Trim().ToLowerInvariant();
            if (kind != "planet" && kind != "building" && kind != "vein")
                throw Fail("kind 只能是 planet、building 或 vein");
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw Fail("找不到行星");

            if (kind == "planet")
            {
                if (!GameTether.AddPlanet(planet, out var error)) throw Fail(error);
                var data = JsonValue.NewObject();
                var target = GameTether.LastAdded;
                data.Set("kind", "planet");
                data.Set("planetId", planet.id);
                data.Set("name", target != null ? target.Name : planet.displayName);
                data.Set("subtitle", target != null ? target.Subtitle : string.Empty);
                data.Set("description", target != null ? target.Description : string.Empty);
                data.Set("iconLabel", target != null ? target.IconLabel : string.Empty);
                return Ok("已建立机甲到行星「" + planet.displayName + "」的方位指示", data);
            }

            if (kind == "vein")
            {
                int veinId = args["veinId"].AsInt(0);
                if (veinId <= 0) throw Fail("veinId 必须是正整数");
                if (planet.factory == null) throw Fail("该行星的工厂尚未加载");
                if (!GameTether.AddVein(planet, veinId, out var veinError)) throw Fail(veinError);
                var vdata = JsonValue.NewObject();
                var vtarget = GameTether.LastAdded;
                vdata.Set("kind", "vein");
                vdata.Set("planetId", planet.id);
                vdata.Set("veinId", veinId);
                vdata.Set("name", vtarget != null ? vtarget.Name : string.Empty);
                vdata.Set("subtitle", vtarget != null ? vtarget.Subtitle : string.Empty);
                vdata.Set("description", vtarget != null ? vtarget.Description : string.Empty);
                vdata.Set("iconLabel", vtarget != null ? vtarget.IconLabel : string.Empty);
                return Ok("已建立机甲到矿脉 #" + veinId.ToString(CultureInfo.InvariantCulture) + " 的方位指示", vdata);
            }

            if (planet.factory == null) throw Fail("该行星的工厂尚未加载");
            var buildingId = args["buildingId"].AsInt(0);
            if (buildingId <= 0) throw Fail("buildingId 必须是正整数");
            if (!GameTether.AddBuilding(planet.factory, planet, buildingId, out var buildError))
                throw Fail(buildError);
            var data2 = JsonValue.NewObject();
            var built = GameTether.LastAdded;
            data2.Set("kind", "building");
            data2.Set("planetId", planet.id);
            data2.Set("buildingId", buildingId);
            data2.Set("name", built != null ? built.Name : string.Empty);
            data2.Set("subtitle", built != null ? built.Subtitle : planet.displayName);
            data2.Set("description", built != null ? built.Description : string.Empty);
            data2.Set("iconLabel", built != null ? built.IconLabel : "#" + buildingId.ToString(CultureInfo.InvariantCulture));
            return Ok("已建立机甲到建筑 #" + buildingId.ToString(CultureInfo.InvariantCulture) + " 的方位指示", data2);
        }
    }

    internal sealed class TetherClearTool : WriteToolBase
    {
        public override string Name => "game_tether_clear";

        protected override GameToolJobResult Execute(JsonValue args)
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null) throw Fail("当前不在游戏内");
            GameTether.Clear();
            return Ok("已清除所有方位指示");
        }
    }

    internal sealed class TetherListTool : IGameToolHandler
    {
        public string Name => "game_tether_list";
        public int TimeoutMs => 5000;

        public IGameToolJob Start(JsonValue args) => new WriteJob(() =>
        {
            if (!GameRefs.IsInGame || GameMain.mainPlayer == null) throw new InvalidOperationException("当前不在游戏内");
            var data = GameTether.List();
            data.Set("display", data["items"].Count > 0 ? "当前方位指示 " + data["items"].Count + " 个" : "当前没有方位指示");
            return new GameToolJobResult { Data = data };
        });
    }
}
