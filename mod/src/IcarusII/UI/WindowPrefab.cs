using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace IcarusII.UI
{
    /// <summary>
    /// 从随插件分发的 AssetBundle 里加载聊天窗与 Game Menu 入口按钮 prefab。
    ///
    /// 为什么是 AssetBundle 而不是"游戏内置 prefab"：mod 改不了已发布的游戏构建
    /// （`DSPGame.unity` 与 `Resources` 都是构建产物），所以 prefab 必须随 mod 自带。
    ///
    /// **平台必须匹配**：bundle 头里带目标平台标记，游戏是 Windows，
    /// 所以 bundle 必须是 `StandaloneWindows64`（见 mod/unity 下的打包脚本）。
    /// 平台不符时 `AssetBundle.LoadFromFile` 返回 null——这里会明确记一条警告，
    /// 而不是静默失败。
    ///
    /// 拿不到 bundle 时返回 null，调用方回退到代码构建的界面，
    /// 保证"少了一个资源文件"不会让窗口整个消失。
    /// </summary>
    internal static class WindowPrefab
    {
        private const string BundleFile = "icarusii-ui";
        private const string BundleSubDir = "ui";
        private const string AssetName = "IcarusIIWindow";

        /// <summary>Game Menu 右上角入口按钮的资源名（见 IcarusIIBundleBuilder）。</summary>
        private const string AgentButtonAssetName = "IcarusIIAgentButton";

        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("IcarusII.UI");
        private static bool _attempted;
        private static GameObject _prefab;
        private static AssetBundle _bundle;

        /// <summary>上一次加载的结果，用于日志与诊断。</summary>
        public static string Status { get; private set; } = "(未尝试)";

        public static GameObject Instantiate(Transform parent)
        {
            var prefab = Load();
            if (prefab == null) return null;

            var go = UnityEngine.Object.Instantiate(prefab, parent, false);
            go.name = "Panel";
            return go;
        }

        /// <summary>
        /// Game Menu 入口按钮 prefab。它和聊天窗在同一个 bundle 里，
        /// 但资源名不同，所以按名单独取（`Load()` 只缓存聊天窗 prefab）。
        /// </summary>
        public static GameObject LoadAgentButton()
        {
            return LoadAsset<GameObject>(AgentButtonAssetName);
        }

        /// <summary>
        /// 从同一个 bundle 里取资源（例如文字材质）。
        /// 游戏里 `Resources.Load("UI/Materials/widget-text-alpha-5x")` 不一定能拿到
        /// （那些材质是随场景/窗口 prefab 打包的，不在 resources.assets 里），
        /// 所以优先用 bundle 里那份。
        /// </summary>
        public static T LoadAsset<T>(string assetName) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(assetName)) return null;
            try
            {
                Load();   // 确保 bundle 已加载
                return _bundle != null ? _bundle.LoadAsset<T>(assetName) : null;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[UI] 从 bundle 加载 {assetName} 失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 精确按 Sprite 子资源名加载。bundle 里可能有同名纹理/多个 Sprite
        /// （如 checkbox-on 与 checkbox-on-2），不能只用 LoadAsset&lt;Sprite&gt;(name)。
        /// </summary>
        public static Sprite LoadSprite(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return null;
            try
            {
                Load();
                if (_bundle == null) return null;
                var sprites = _bundle.LoadAllAssets<Sprite>();
                if (sprites == null) return null;
                Sprite byName = null;
                foreach (var sprite in sprites)
                {
                    if (sprite == null) continue;
                    if (sprite.name == spriteName) return sprite;
                    if (byName == null && sprite.texture != null && sprite.texture.name == spriteName)
                        byName = sprite;
                }
                return byName;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[UI] 从 bundle 加载 Sprite {spriteName} 失败：{ex.Message}");
                return null;
            }
        }

        private static GameObject Load()
        {
            if (_attempted) return _prefab;
            _attempted = true;

            try
            {
                var path = ResolveBundlePath();
                if (path == null)
                {
                    Status = "未找到 bundle";
                    return null;
                }

                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    // 最常见的两种原因：文件损坏，或 bundle 的平台与游戏不符
                    Status = "bundle 加载失败（平台不匹配或文件损坏）";
                    Log.LogWarning($"[UI] {Status}：{path}");
                    return null;
                }

                _bundle = bundle;

                _prefab = bundle.LoadAsset<GameObject>(AssetName);
                if (_prefab == null)
                {
                    Status = $"bundle 内没有资源 {AssetName}";
                    Log.LogWarning($"[UI] {Status}：{path}");
                    bundle.Unload(false);
                    _bundle = null;
                    return null;
                }

                Status = $"已加载 {Path.GetFileName(path)}";
                Log.LogInfo($"[UI] {Status} → {AssetName}");
                return _prefab;
            }
            catch (Exception ex)
            {
                Status = "加载异常：" + ex.Message;
                Log.LogWarning($"[UI] {Status}");
                return null;
            }
        }

        private static string ResolveBundlePath()
        {
            foreach (var dir in CandidateDirs())
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var path = Path.Combine(dir, BundleSubDir, BundleFile);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static string[] CandidateDirs()
        {
            var dirs = new System.Collections.Generic.List<string>();

            // 插件所在目录（正常情况）
            try
            {
                var location = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location)) dirs.Add(Path.GetDirectoryName(location));
            }
            catch { /* 反射受限时忽略 */ }

            // BepInEx 约定路径，作为兜底
            try
            {
                dirs.Add(Path.Combine(Paths.PluginPath, "IcarusII"));
                dirs.Add(Paths.PluginPath);
            }
            catch { /* Paths 未就绪时忽略 */ }

            return dirs.ToArray();
        }
    }
}
