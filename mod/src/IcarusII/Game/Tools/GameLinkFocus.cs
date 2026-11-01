using System;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 链接卡片“点整张卡聚焦”的入口：
    ///  - 建筑：星球模式，聚焦当前行星上的建筑；建筑在外星无法聚焦，返回 false。
    ///  - 星球：星图模式，聚焦该行星。
    /// 不写入撤销栈，也不改变 tether；只是移动视角。
    /// </summary>
    internal static class GameLinkFocus
    {
        public static bool Focus(GameTether.Target target)
        {
            if (target == null) return false;
            try
            {
                Plugin.Log.LogInfo($"[link] 聚焦 {target.Kind} planet={target.PlanetId} target={target.TargetId}");
                if (target.Kind == "building") return FocusBuilding(target);
                if (target.Kind == "vein") return FocusVein(target);
                if (target.Kind == "planet") return FocusPlanet(target);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[link] 聚焦 {target.Kind} 失败：{ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }

        private static bool FocusBuilding(GameTether.Target target)
        {
            var player = GameMain.mainPlayer;
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (player == null || uiGame == null) return false;
            var planet = player.planetData;
            if (planet == null || planet.id != target.PlanetId) return false; // 外星建筑：无效果
            var factory = planet.factory;
            if (factory == null || !EntityToolUtil.HasEntity(factory, target.TargetId)) return false;
            if (uiGame.globemap == null) return false;
            if (!uiGame.globemap.active)
            {
                uiGame.OpenGlobemap();
                if (!uiGame.globemap.active) return false;
            }
            var entity = factory.entityPool[target.TargetId];
            float distance = Mathf.Max(20f, entity.pos.magnitude - planet.realRadius + 20f);
            uiGame.globemap.MoveToViewTargetTwoStep(entity.pos, distance);
            return true;
        }

        private static bool FocusVein(GameTether.Target target)
        {
            var player = GameMain.mainPlayer;
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (player == null || uiGame == null) return false;
            var planet = player.planetData;
            if (planet == null || planet.id != target.PlanetId || planet.factory == null) return false;
            var factory = planet.factory;
            if (factory.veinPool == null || target.TargetId <= 0 || target.TargetId >= factory.veinPool.Length)
                return false;
            var vein = factory.veinPool[target.TargetId];
            if (vein.id != target.TargetId || uiGame.globemap == null) return false;
            if (!uiGame.globemap.active)
            {
                uiGame.OpenGlobemap();
                if (!uiGame.globemap.active) return false;
            }
            float distance = Mathf.Max(20f, vein.pos.magnitude - planet.realRadius + 20f);
            uiGame.globemap.MoveToViewTargetTwoStep(vein.pos, distance);
            return true;
        }

        private static bool FocusPlanet(GameTether.Target target)
        {
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            var galaxy = GameMain.galaxy;
            if (uiGame == null || galaxy == null) return false;
            var planet = galaxy.PlanetById(target.PlanetId);
            if (planet == null) return false;

            Plugin.Log.LogInfo($"[link] 打开星图前 starmap={(uiGame.starmap == null ? "null" : "有")}");
            uiGame.OpenStarmap();
            if (uiGame.starmap == null) return false;
            Plugin.Log.LogInfo($"[link] 星图已开 inited={uiGame.starmap.inited} active={uiGame.starmap.active}");

            uiGame.starmap.SetViewStar(planet.star, true);
            Plugin.Log.LogInfo($"[link] SetViewStar 完成 star={planet.star?.id ?? -1}");

            var camera = uiGame.starmap.screenCameraController;
            if (camera == null) return false;
            var radius = planet.realRadius * 0.00025 * 6.0;
            if (!(radius > 0)) return uiGame.starmap.active;
            camera.SetViewTarget(planet, null, null, null, VectorLF3.zero,
                radius, radius, true, false);
            Plugin.Log.LogInfo($"[link] SetViewTarget 完成 radius={radius:F3}");
            return true;
        }
    }
}
