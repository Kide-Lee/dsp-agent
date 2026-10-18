using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 游戏文字材质。
    ///
    /// 为什么必须有它：DSP 的字体图集是给 **`UI Ex/Text Alpha`** 着色器准备的。
    /// 该着色器带 5× 亮度（`_Multiplier = 5`）与 alpha 锐化曲线，并且用
    /// `texcoord.x >= 2` 的 UV 去 `_Global_IconSet_Tex` 里取图标。
    /// 默认的 `UI/Default` 材质拿到同一张图集时只会按普通 alpha 采样——
    /// 文字暗到几乎看不见，图标更是完全画不出来。
    ///
    /// 原生窗口的 Text 都显式挂了 `UI/Materials/widget-text-alpha-5x`（输入框用
    /// `-thick` 变体）。这里优先从 mod 自带 bundle 取（游戏本体那份材质是随
    /// 场景/prefab 打包的，`Resources.Load` 不一定拿得到）。
    /// </summary>
    internal static class GameTextStyle
    {
        private const string MaterialPath = "UI/Materials/widget-text-alpha-5x";
        private const string ThickMaterialPath = "UI/Materials/widget-text-alpha-5x-thick";
        private const string BundledMaterial = "widget-text-alpha-5x";
        private const string BundledThickMaterial = "widget-text-alpha-5x-thick";

        private static readonly Dictionary<string, Material> Cache = new Dictionary<string, Material>();
        private static bool _missingLogged;

        /// <summary>常规正文/标签材质（`_Sharp = 0.93`）。</summary>
        public static Material Material => Resolve(BundledMaterial, MaterialPath);

        /// <summary>输入框文字材质（`_Sharp = 0.5`，比常规稍厚，和原生输入框一致）。</summary>
        public static Material ThickMaterial => Resolve(BundledThickMaterial, ThickMaterialPath);

        private static Material Resolve(string bundledName, string resourcesPath)
        {
            Material cached;
            if (Cache.TryGetValue(resourcesPath, out cached)) return cached;

            // 1) mod 自带 bundle（最可靠：材质与它引用的 shader 都在包里）
            var material = WindowPrefab.LoadAsset<Material>(bundledName);
            if (!Usable(material)) material = null;

            // 2) 游戏 Resources（部分版本可能没有，落空也不影响）
            if (material == null)
            {
                material = Resources.Load<Material>(resourcesPath);
                if (!Usable(material)) material = null;
            }

            // 3) 从场景里的原生 Text 借
            if (material == null) material = BorrowFromScene();

            if (material != null)
            {
                Cache[resourcesPath] = material;
                Plugin.Log.LogInfo($"[UI] 文字材质 = {material.name}（{material.shader.name}）");
            }
            else if (!_missingLogged)
            {
                _missingLogged = true;
                Plugin.Log.LogWarning("[UI] 找不到游戏文字材质，退回 Unity 默认材质（文字可能几乎不可见）。");
            }
            return material;
        }

        /// <summary>材质可用：有 shader，且不是 Unity 的粉色错误 shader。</summary>
        private static bool Usable(Material material)
        {
            if (material == null || material.shader == null) return false;
            return material.shader.name != "Hidden/InternalErrorShader";
        }

        /// <summary>从场景里已有的原生 Text 借材质（前两条路都失败时的兜底）。</summary>
        private static Material BorrowFromScene()
        {
            try
            {
                foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
                {
                    if (text == null || text.material == null) continue;
                    if (!Usable(text.material)) continue;
                    if (text.material.name.IndexOf("widget-text", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return text.material;
                    }
                }
            }
            catch
            {
                // 取不到就返回 null，由调用方决定怎么办
            }
            return null;
        }

        /// <summary>
        /// 给新建的 Text 套上游戏文字材质。
        /// <paramref name="thick"/>=true 用输入框那份稍厚的材质。
        /// </summary>
        public static void Apply(Text text, bool thick = false)
        {
            if (text == null) return;
            var material = thick ? ThickMaterial : Material;
            if (material != null) text.material = material;
        }
    }
}
