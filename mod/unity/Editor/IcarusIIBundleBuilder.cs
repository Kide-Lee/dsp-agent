using System.IO;
using UnityEditor;
using UnityEngine;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 把聊天窗 prefab（连同它用到的窗口贴图）打成一个 **Windows** AssetBundle。
    ///
    /// 为什么必须是 Windows：AssetBundle 是**分平台**的，bundle 头里带目标平台标记，
    /// 平台不匹配时 `AssetBundle.LoadFromFile` 会直接失败。游戏是
    /// `DSPGAME.exe`（Windows），所以这里固定 `StandaloneWindows64`，
    /// 即使编辑器跑在 Linux 上。
    ///
    /// **不需要切换 Active Build Target**：`BuildAssetBundles` 接受显式的
    /// `BuildTarget` 参数。切换 target 会触发整个工程重新导入（这工程 6.7GB），
    /// 代价极大且没有必要。前提是装了 `Windows Build Support` 模块
    /// （`IsBuildTargetSupported` 会告诉我们）。
    ///
    /// 批处理调用：
    ///   Unity -batchmode -quit -projectPath &lt;proj&gt;
    ///         -executeMethod IcarusII.EditorTools.IcarusIIBundleBuilder.Build
    ///         -logFile -
    /// 产物：&lt;project&gt;/Build/IcarusIIBundle/icarusii-ui
    /// </summary>
    public static class IcarusIIBundleBuilder
    {
        /// <summary>
        /// 打包用的窗口 prefab：**你在 Unity 里手工做的 `ChatWindow.prefab`**。
        /// 这份由你维护，我们只做加法（`IcarusIIWindowTransplant`）或核对结构；
        /// 生成器默认拒绝覆盖它。
        /// bundle 里的资源名仍然是 `IcarusIIWindow`（见 AssetName），mod 侧不用改。
        /// </summary>
        private const string PrefabPath = "Assets/IcarusII/ChatWindow.prefab";
        private const string SpriteDir = "Assets/Resources/UI/Textures/Sprites/Sci-fi/";
        private const string IconDir = "Assets/Resources/UI/Textures/Sprites/Icons/";
        private const string MaterialDir = "Assets/Resources/UI/Materials/";
        private const string OutputDir = "Build/IcarusIIBundle";
        private const string BundleName = "icarusii-ui";
        private const string AssetName = "IcarusIIWindow";

        [MenuItem("Icarus II/Build UI AssetBundle (Windows)")]
        public static void BuildFromMenu()
        {
            var ok = BuildInternal(out var path);
            EditorUtility.DisplayDialog("Icarus II",
                ok ? "已生成：\n" + path : "打包失败，见 Console", "好");
        }

        /// <summary>批处理入口：只打包，**不重建 prefab**（手工改的东西必须留着）。</summary>
        public static void BuildAll()
        {
            Build();
        }

        /// <summary>批处理入口：先给手工 prefab 做加法（幂等），再打包。</summary>
        public static void AddPartsAndBuild()
        {
            string message;
            if (!IcarusIIWindowTransplant.TransplantAt(PrefabPath, out message))
            {
                Fail("加聊天窗部件失败：" + message);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[IcarusII] " + message);
            Build();
        }

        /// <summary>批处理入口。</summary>
        public static void Build()
        {
            var ok = BuildInternal(out _);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool BuildInternal(out string producedPath)
        {
            producedPath = null;

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
            {
                return Fail("未安装 Windows Build Support 模块，无法打出 Windows 的 bundle。\n" +
                            "用 Unity Hub 安装：2022.3.62f3 → Add modules → Windows Build Support (Mono)");
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                return Fail("找不到 prefab：" + PrefabPath + "\n" +
                            "这份是手工维护的窗口 prefab；" +
                            "如果你要的是从头生成，用 IcarusIIWindowPrefabBuilder.BuildForced()。");
            }

            if (!IcarusIIChatIndexBuilder.Ensure(PrefabPath))
            {
                return Fail("chat-index prefab 结构构建失败：" + PrefabPath);
            }

            if (!IcarusIIPrefabLayoutFix.Ensure(PrefabPath))
            {
                return Fail("聊天窗布局固化失败：" + PrefabPath);
            }

            Directory.CreateDirectory(OutputDir);

            var build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                // 显式列出 prefab 用到的贴图与材质：它们位于 Resources 下、会被构建进播放器；
                // 这里再显式纳入 bundle，保证 prefab 的引用在加载后一定有效，
                // 不依赖 Unity 的隐式依赖收集。
                //
                // 贴图是机甲信息窗外框那几张；材质是原生窗口给 widget/text 用的
                // alpha 着色器（`UI Ex/Widget Alpha`、`UI Ex/Text Alpha`），
                // 不做 5× 亮度与 alpha 曲线校正的话边框与文字会和原生对不上。
                assetNames = new[]
                {
                    PrefabPath,
                    SpriteDir + "window-trsl-1.png",
                    SpriteDir + "window-trsl-1-sd.png",
                    SpriteDir + "window-border-1.png",
                    SpriteDir + "window-glass-1.png",
                    SpriteDir + "sci-fi-deco-1.png",
                    IconDir + "checkbox-on-2.png",
                    IconDir + "insert-icon-40.png",
                    // 关闭/菜单按钮（照抄游戏窗口的 btn-box）
                    SpriteDir + "hex-bg-30.png",
                    SpriteDir + "hex-border-up-30.png",
                    SpriteDir + "close-x-m.png",
                    SpriteDir + "hex-btn-r-bg-30.png",
                    SpriteDir + "hex-btn-l-bg-30.png",
                    IconDir + "popup-menu-entry-icon-0.png",
                    SpriteDir + "litter-panel-cc.png",
                    MaterialDir + "widget-alpha-5x.mat",
                    MaterialDir + "widget-text-alpha-5x.mat",
                    MaterialDir + "widget-text-alpha-5x-thick.mat",
                    MaterialDir + "trsl-panel-0.mat",
                    MaterialDir + "trsl-panel-3.mat",
                },
                addressableNames = new[]
                {
                    AssetName,
                    "window-trsl-1",
                    "window-trsl-1-sd",
                    "window-border-1",
                    "window-glass-1",
                    "sci-fi-deco-1",
                    "checkbox-on-2",
                    "insert-icon-40",
                    "hex-bg-30",
                    "hex-border-up-30",
                    "close-x-m",
                    "hex-btn-r-bg-30",
                    "hex-btn-l-bg-30",
                    "popup-menu-entry-icon-0",
                    "litter-panel-cc",
                    "widget-alpha-5x",
                    "widget-text-alpha-5x",
                    "widget-text-alpha-5x-thick",
                    "trsl-panel-0",
                    "trsl-panel-3",
                },
            };

            var manifest = BuildPipeline.BuildAssetBundles(
                OutputDir,
                new[] { build },
                // LZ4 分块压缩：加载快、体积小，AssetBundle 的常规选择
                BuildAssetBundleOptions.ChunkBasedCompression,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                return Fail("BuildAssetBundles 返回 null（见上方 Console 的具体错误）");
            }

            var path = Path.Combine(OutputDir, BundleName);
            if (!File.Exists(path))
            {
                return Fail("构建未报错但没有产物：" + path);
            }

            producedPath = path;
            var size = new FileInfo(path).Length;
            Debug.Log($"[IcarusII] AssetBundle 已生成：{path}（{size / 1024} KB, StandaloneWindows64）");
            Debug.Log($"[IcarusII] 包内 bundle：{string.Join(", ", manifest.GetAllAssetBundles())}");
            return true;
        }

        private static bool Fail(string message)
        {
            Debug.LogError("[IcarusII] " + message);
            return false;
        }
    }
}
