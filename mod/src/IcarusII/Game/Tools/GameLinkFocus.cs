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
                if (target.Kind == "building") return FocusBuilding(target);
                if (target.Kind == "vein") return FocusVein(target);
                if (target.Kind == "planet") return FocusPlanet(target);
            }
            catch { /* 切场景/UI 重建瞬间忽略 */ }
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
            uiGame.OpenStarmap();
            if (uiGame.starmap == null) return false;
            uiGame.starmap.SetViewStar(planet.star, true);
            var camera = uiGame.starmap.screenCameraController;
            if (camera == null) return false;
            var radius = planet.realRadius * 0.00025 * 6.0;
            camera.SetViewTarget(planet, null, null, null, VectorLF3.zero,
                radius, radius, true, false);
            return true;
        }
    }
}
