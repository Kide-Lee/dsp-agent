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
        private const string PrefabPath = "Assets/IcarusII/IcarusIIWindow.prefab";
        private const string SpriteDir = "Assets/Resources/UI/Textures/Sprites/Sci-fi/";
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

        /// <summary>批处理入口：prefab 不存在时才生成，然后打包。</summary>
        public static void BuildAll()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                Debug.Log("[IcarusII] prefab 不存在，先按脚本生成");
                IcarusIIWindowPrefabBuilder.Build();
            }
            Build();
        }

        /// <summary>批处理入口：强制重建 prefab 后再打包（会覆盖你在 Unity 里的手改）。</summary>
        public static void RebuildAll()
        {
            Debug.Log("[IcarusII] 强制重建 prefab");
            IcarusIIWindowPrefabBuilder.Build();
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
                return Fail("找不到 prefab：" + PrefabPath + "\n请先运行 Icarus II/Rebuild Window Prefab");
            }

            Directory.CreateDirectory(OutputDir);

            var build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                // 显式列出贴图：它们位于 Resources 下、会被构建进播放器；
                // 这里再显式纳入 bundle，保证 prefab 的贴图引用在加载后一定有效，
                // 不依赖 Unity 的隐式依赖收集。
                assetNames = new[]
                {
                    PrefabPath,
                    SpriteDir + "window-trsl-1.png",
                    SpriteDir + "window-trsl-1-sd.png",
                    SpriteDir + "window-border-1.png",
                    SpriteDir + "window-glass-1.png",
                },
                addressableNames = new[]
                {
                    AssetName,
                    "window-trsl-1",
                    "window-trsl-1-sd",
                    "window-border-1",
                    "window-glass-1",
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
