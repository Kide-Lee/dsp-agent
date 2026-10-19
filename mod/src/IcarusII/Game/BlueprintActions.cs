using System;
using System.IO;
using IcarusII.Common.Protocol;
using IcarusII.I18n;
using UnityEngine;

namespace IcarusII.Game
{
    /// <summary>
    /// 蓝图卡片的两个游戏内动作：查看详情 / 进入粘贴模式。
    ///
    /// 为什么集中在这里：卡片的 UI 在 <see cref="IcarusII.UI.ChatWindow"/>，
    /// 但真正碰游戏 API 的只有这几个调用。集中在 <c>Game/</c> 下便于游戏更新时
    /// 按错误行定位（与 <see cref="GameRefs"/> 的目的一致）。
    ///
    /// 已对 0.10.30.3100 的 Assembly-CSharp 核实：
    ///   - <c>UIRoot.instance.uiGame.OpenBlueprintBrowser()</c>
    ///   - <c>UIBlueprintBrowser.SetCurrentDirectory(string)</c>
    ///   - <c>UIBlueprintInspector.SetBlueprint(BlueprintData, string)</c> + <c>_Open()</c>
    ///   - <c>PlayerController.OpenBlueprintPasteMode(BlueprintData, string, bool)</c>
    /// </summary>
    internal static class BlueprintActions
    {
        /// <summary>
        /// 卡片里的路径可能来自 sidecar（Linux 路径），而游戏在 Wine 里看到的是
        /// Windows 路径。两边约定同一个「蓝图目录」，所以**优先用 fileName 重新拼**，
        /// 只有拼出来的文件不存在时才退回原始 path。
        /// </summary>
        public static string ResolvePath(BlueprintCardInfo card)
        {
            if (card == null) return null;

            var name = SafeFileName(card.FileName);
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(card.Path))
                name = SafeFileName(Path.GetFileName(card.Path));

            if (!string.IsNullOrEmpty(name))
            {
                var inGameDir = Path.Combine(GameConfig.blueprintFolder, name);
                if (File.Exists(inGameDir)) return inGameDir;
                if (!string.IsNullOrEmpty(card.Path) && File.Exists(card.Path)) return card.Path;
                return inGameDir;   // 交给调用方报"文件不存在"，路径也更有意义
            }

            return string.IsNullOrEmpty(card.Path) ? null : card.Path;
        }

        public static bool TryInspect(BlueprintCardInfo card, out string error)
        {
            error = null;
            if (!GameRefs.IsInGame)
            {
                error = Strings.Get("blueprint.card.not_in_game");
                return false;
            }

            if (!TryLoad(card, out var blueprint, out error)) return false;

            var path = ResolvePath(card);
            var root = UIRoot.instance;
            var uiGame = root != null ? root.uiGame : null;
            var browser = uiGame != null ? uiGame.blueprintBrowser : null;
            if (browser == null)
            {
                error = Strings.Get("blueprint.card.open_failed");
                return false;
            }

            // 打开浏览器（已打开时 _Open 是幂等的；戴森球编辑器打开时游戏会拒绝）。
            if (!browser.active) uiGame.OpenBlueprintBrowser();
            if (!browser.active)
            {
                error = Strings.Get("blueprint.card.open_failed");
                return false;
            }

            var dir = Path.GetDirectoryName(path);
            browser.SetCurrentDirectory(string.IsNullOrEmpty(dir) ? GameConfig.blueprintFolder : dir);
            if (browser.inspector == null)
            {
                error = Strings.Get("blueprint.card.open_failed");
                return false;
            }

            // 先关掉另一种 inspector（文件夹详情），避免两块叠在一起。
            if (browser.boolInspector != null) browser.boolInspector._Close();
            // SetBlueprint 只在 active 时 Refresh；先 _Close 再 _Open，保证一定刷新。
            browser.inspector._Close();
            browser.inspector.SetBlueprint(blueprint, path);
            browser.inspector._Open();
            return true;
        }

        public static bool TryPaste(BlueprintCardInfo card, out string error)
        {
            error = null;
            if (!GameRefs.IsInGame)
            {
                error = Strings.Get("blueprint.card.not_in_game");
                return false;
            }

            // 未解锁 / 不在可建造状态时游戏只会静默 return，玩家会以为按钮坏了。
            if (GameMain.history == null || GameMain.history.blueprintLimit == 0
                || !VFInput.readyToEnterBlueprintMode)
            {
                error = Strings.Get("blueprint.card.paste_unavailable");
                return false;
            }

            var player = GameMain.mainPlayer;
            var controller = player != null ? player.controller : null;
            if (controller == null)
            {
                error = Strings.Get("blueprint.card.paste_unavailable");
                return false;
            }

            if (!TryLoad(card, out var blueprint, out error)) return false;

            var path = ResolvePath(card);
            var globe = UIGame.viewMode == EViewMode.Globe;
            if (globe)
            {
                // 与游戏内蓝图浏览器双击的行为一致：先切回行星蓝图模式。
                if (GameCamera.instance != null) GameCamera.instance.SyncBlueprintModeByPlanetMode();
                var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                if (uiGame != null && uiGame.globemap != null) uiGame.globemap.CloseImmediate();
            }

            controller.OpenBlueprintPasteMode(blueprint, path, !globe);

            // 游戏在 gas 星球 / 未解锁时会静默失败；用 blueprintMode 确认真的进了粘贴态。
            var actionBuild = controller.actionBuild;
            if (actionBuild == null || actionBuild.blueprintMode != EBlueprintMode.Paste)
            {
                error = Strings.Get("blueprint.card.paste_failed");
                return false;
            }
            return true;
        }

        private static bool TryLoad(BlueprintCardInfo card, out BlueprintData blueprint, out string error)
        {
            blueprint = null;
            error = null;

            var path = ResolvePath(card);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = Strings.Get("blueprint.card.no_file", path ?? card?.FileName ?? "?");
                return false;
            }

            try
            {
                blueprint = BlueprintData.CreateFromFile(path);
            }
            catch (DirectoryNotFoundException)
            {
                blueprint = null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[蓝图卡] 读取 {path} 失败：{ex.Message}");
                blueprint = null;
            }

            if (BlueprintData.IsNullOrEmpty(blueprint))
            {
                error = Strings.Get("blueprint.card.bad_file");
                return false;
            }
            return true;
        }

        /// <summary>只接受纯文件名，防止协议里的 fileName 带路径分隔符越出蓝图目录。</summary>
        private static string SafeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var name = Path.GetFileName(raw);
            if (string.IsNullOrEmpty(name) || name == "." || name == "..") return null;
            return name;
        }
    }
}
