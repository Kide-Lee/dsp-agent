using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 聊天窗的外观来源。
    ///
    /// 经源码核对（`dsp-game/Assets/Scripts`）后，这里不再靠"从活窗口猜"，
    /// 而是分成两个确定性来源：
    ///
    /// 1. **贴图**：直接按已知资源路径加载。
    ///    机体信息窗由三层同尺寸九宫格叠出来：
    ///      shadow   → window-trsl-1-sd   （投影）
    ///      panel-bg → window-trsl-1      （底衬）
    ///      border   → window-border-1    （装饰边框，带切角）
    ///      glass    → window-glass-1     （玻璃高光）
    ///    资源在 `Assets/Resources/UI/Textures/Sprites/Sci-fi/`，
    ///    运行时路径即 `UI/Textures/Sprites/Sci-fi/<name>`。
    ///    它们是**纯白的单图**（实测像素为 (255,255,255,α)，无 spritePackingTag、未进图集），
    ///    九宫格边界在 .meta 的 spriteBorder 里（236,42,57,57）。
    ///
    /// 2. **颜色**：贴图是白的，所以配色**全部**来自 `Image.color`；
    ///    而颜色只存在于场景二进制里（这三张贴图在 C# 代码中**一次都没被引用**，
    ///    是场景直接赋给 Image 的）。因此只能从活的机体信息窗上读——
    ///    好在结构已知且稳定，**按节点名直接取**即可，不需要打分挑选。
    ///
    /// 任一来源失败时回退到旧的启发式，再失败才用内置兜底色，
    /// 保证任何情况下窗口都能建出来。
    /// </summary>
    internal sealed class UiStyle
    {
        // ---- 面板底衬（兜底色；正常情况下会被活窗口的真实颜色覆盖）
        public bool HasPanel;
        public Sprite PanelSprite;
        public Color PanelColor = new Color(0.10f, 0.15f, 0.22f, 1f);
        public Image.Type PanelType = Image.Type.Sliced;
        public bool PanelFillCenter = true;

        // ---- 装饰边框（叠在底衬之上）
        public bool HasFrame;
        public Sprite FrameSprite;
        public Color FrameColor = new Color(1f, 1f, 1f, 0.29f);
        public Image.Type FrameType = Image.Type.Sliced;

        // ---- 玻璃高光（很淡）
        public bool HasGlass;
        public Sprite GlassSprite;
        public Color GlassColor = new Color(1f, 1f, 1f, 0.15f);

        // ---- 输入区背景
        public bool HasInput;
        public Sprite InputSprite;
        public Color InputColor;
        public Image.Type InputType = Image.Type.Sliced;

        // ---- 文本
        public bool HasText;
        public Font Font;
        public int BaseFontSize = 14;
        public Color TextMain = new Color(0.878f, 0.918f, 0.961f, 1f);
        public Color TextDim = new Color(0.596f, 0.678f, 0.757f, 1f);
        public Color Accent = new Color(0.353f, 0.749f, 1f, 1f);

        public string Source = "(未捕获)";

        // ---------------------------------------------------------------- 已知常量

        private const string SciFiDir = "UI/Textures/Sprites/Sci-fi/";
        private const string SpriteShadow = "window-trsl-1-sd";
        private const string SpriteBackdrop = "window-trsl-1";
        private const string SpriteBorder = "window-border-1";
        private const string SpriteGlass = "window-glass-1";

        /// <summary>机体信息窗各层的节点名（与场景结构一致）。</summary>
        private const string NodePanelBg = "panel-bg";
        private const string NodeBorder = "border";
        private const string NodeGlass = "glass";

        private static UiStyle _current;

        public static UiStyle Current
        {
            get
            {
                if (_current == null) _current = new UiStyle();
                return _current;
            }
        }

        public static void Reset() => _current = null;

        /// <summary>
        /// 尝试捕获。可重复调用：游戏窗口在主菜单可能还没实例化，
        /// 失败时返回 false，调用方在之后的帧重试。
        /// </summary>
        public static bool TryCapture(UIRoot uiRoot, BepInEx.Logging.ManualLogSource log)
        {
            var style = new UiStyle();
            if (!style.Capture(uiRoot, log)) return false;
            _current = style;
            return true;
        }

        private bool Capture(UIRoot uiRoot, BepInEx.Logging.ManualLogSource log)
        {
            var reference = FindReferenceWindow(uiRoot);

            // 1) 贴图：按路径直接加载，不依赖活窗口
            var spritesOk = LoadKnownSprites(log);

            // 2) 颜色：从活窗口按名字取（贴图是白的，颜色只在这里）
            var colorsOk = reference != null && CaptureColorsByName(reference, log);

            // 3) 任一来源不完整 → 退回启发式
            if (!spritesOk || !colorsOk)
            {
                if (reference != null)
                {
                    log?.LogInfo($"[样式] 确定性来源不完整（贴图={spritesOk} 颜色={colorsOk}），改用启发式兜底。");
                    DumpCandidates(reference, log);
                    HeuristicCapture(reference, log);
                }
                else
                {
                    log?.LogWarning("[样式] 找不到参考窗口，且贴图加载失败，将使用内置兜底配色。");
                }
            }

            CaptureText(reference, log);

            // 输入区没有原生控件可抄（机体窗里没有 InputField），用底衬贴图 + 略暗的颜色
            HasInput = HasPanel && PanelSprite != null;
            InputSprite = PanelSprite;
            InputType = PanelType;
            InputColor = Darken(PanelColor, 0.78f);

            if (!HasPanel) return false;

            Source = $"sprites={(spritesOk ? "direct" : "heuristic")} colors={(colorsOk ? "by-name" : "fallback")}" +
                     $" ref={(reference != null ? reference.name : "none")}";
            log?.LogInfo(
                $"[样式] 已捕获：{Source} panel={PanelSprite?.name ?? "无"}" +
                $" frame={FrameSprite?.name ?? "无"} glass={(HasGlass ? GlassSprite?.name : "无")}" +
                $" fontSize={BaseFontSize} panelRGBA={Fmt(PanelColor)} frameRGBA={Fmt(FrameColor)}");
            return true;
        }

        // ---------------------------------------------------------------- 来源 1：贴图

        private bool LoadKnownSprites(BepInEx.Logging.ManualLogSource log)
        {
            var backdrop = Resources.Load<Sprite>(SciFiDir + SpriteBackdrop);
            var border = Resources.Load<Sprite>(SciFiDir + SpriteBorder);
            var glass = Resources.Load<Sprite>(SciFiDir + SpriteGlass);

            if (backdrop == null)
            {
                log?.LogWarning($"[样式] 加载失败：{SciFiDir}{SpriteBackdrop}（游戏版本可能改了资源路径）");
                return false;
            }

            HasPanel = true;
            PanelSprite = backdrop;
            PanelType = Image.Type.Sliced;
            PanelFillCenter = true;

            if (border != null)
            {
                HasFrame = true;
                FrameSprite = border;
                FrameType = Image.Type.Sliced;
            }

            if (glass != null)
            {
                HasGlass = true;
                GlassSprite = glass;
            }

            return border != null;
        }

        // ---------------------------------------------------------------- 来源 2：颜色

        /// <summary>
        /// 按已知节点名取颜色。机体信息窗的结构稳定：
        ///   Mecha Window/panel-bg        ← 底衬
        ///   Mecha Window/panel-bg/border ← 装饰边框
        ///   Mecha Window/panel-bg/glass  ← 玻璃
        ///   Mecha Window/shadow          ← 投影
        /// 按名字取比"按面积打分挑选"可靠得多——后者曾选中 GUIFrame_thin 这种小图。
        /// </summary>
        private bool CaptureColorsByName(GameObject reference, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var root = reference.transform;
                var panelBg = root.Find(NodePanelBg);
                if (panelBg == null)
                {
                    log?.LogInfo($"[样式] {reference.name} 下没有 {NodePanelBg} 节点，颜色改用启发式。");
                    return false;
                }

                var bgImage = panelBg.GetComponent<Image>();
                if (bgImage == null) return false;

                PanelColor = bgImage.color;
                HasPanel = true;
                if (bgImage.sprite != null) PanelSprite = bgImage.sprite;

                var border = panelBg.Find(NodeBorder);
                if (border != null)
                {
                    var img = border.GetComponent<Image>();
                    if (img != null)
                    {
                        HasFrame = true;
                        FrameColor = img.color;
                        if (FrameSprite == null) FrameSprite = img.sprite;
                    }
                }

                var glass = panelBg.Find(NodeGlass);
                if (glass != null)
                {
                    var img = glass.GetComponent<Image>();
                    if (img != null)
                    {
                        HasGlass = true;
                        GlassColor = img.color;
                        if (GlassSprite == null) GlassSprite = img.sprite;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[样式] 按名字取色失败：{ex.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------- 来源 3：启发式兜底

        /// <summary>
        /// 旧路径：按面积与名字猜测各层。只在确定性来源失效时使用，
        /// 因此刻意保持简单，不再追求"猜得准"。
        /// </summary>
        private void HeuristicCapture(GameObject reference, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var images = reference.GetComponentsInChildren<Image>(true);

                float maxArea = 0f;
                foreach (var img in images)
                {
                    if (img == null || img.sprite == null || img.rectTransform == null) continue;
                    var r = img.rectTransform.rect;
                    maxArea = Mathf.Max(maxArea, Mathf.Abs(r.width * r.height));
                }

                foreach (var img in images)
                {
                    if (img == null || img.sprite == null || img.rectTransform == null) continue;
                    var r = img.rectTransform.rect;
                    if (Mathf.Abs(r.width * r.height) < maxArea * 0.9f) continue;
                    if (img.type != Image.Type.Sliced) continue;

                    var hay = (img.sprite.name + " " + img.gameObject.name).ToLowerInvariant();
                    if (hay.Contains("shadow") || img.sprite.name.EndsWith("-sd")) continue;

                    if (img.gameObject.name == NodeBorder || hay.Contains("border"))
                    {
                        if (!HasFrame) { HasFrame = true; FrameSprite = img.sprite; FrameColor = img.color; FrameType = img.type; }
                    }
                    else if (hay.Contains("glass"))
                    {
                        if (!HasGlass) { HasGlass = true; GlassSprite = img.sprite; GlassColor = img.color; }
                    }
                    else if (!HasPanel || img.color.a > PanelColor.a)
                    {
                        HasPanel = true;
                        PanelSprite = img.sprite;
                        PanelColor = img.color;
                        PanelType = img.type;
                        PanelFillCenter = img.fillCenter;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[样式] 启发式兜底失败：{ex.Message}");
            }
        }

        // ---------------------------------------------------------------- 文本

        private void CaptureText(GameObject reference, BepInEx.Logging.ManualLogSource log)
        {
            if (reference == null) return;

            try
            {
                var texts = reference.GetComponentsInChildren<Text>(true);
                if (texts == null || texts.Length == 0) return;

                var sizeCount = new Dictionary<int, int>();
                var colorCount = new Dictionary<int, int>();
                var colorValue = new Dictionary<int, Color>();
                Font font = null;

                foreach (var t in texts)
                {
                    if (t == null) continue;
                    if (font == null && t.font != null) font = t.font;

                    sizeCount.TryGetValue(t.fontSize, out var sc);
                    sizeCount[t.fontSize] = sc + 1;

                    var c = t.color;
                    int key = (Mathf.RoundToInt(c.r * 255) << 24) | (Mathf.RoundToInt(c.g * 255) << 16)
                            | (Mathf.RoundToInt(c.b * 255) << 8) | Mathf.RoundToInt(c.a * 255);
                    colorCount.TryGetValue(key, out var cc);
                    colorCount[key] = cc + 1;
                    colorValue[key] = c;
                }

                int bestSize = 0, bestSizeCount = 0;
                foreach (var kv in sizeCount)
                    if (kv.Value > bestSizeCount) { bestSizeCount = kv.Value; bestSize = kv.Key; }

                int bestColor = 0, bestColorCount = 0;
                foreach (var kv in colorCount)
                    if (kv.Value > bestColorCount) { bestColorCount = kv.Value; bestColor = kv.Key; }

                if (font != null) { Font = font; HasText = true; }
                if (bestSize > 0) BaseFontSize = bestSize;

                if (colorCount.Count > 0 && colorValue.TryGetValue(bestColor, out var mainColor))
                {
                    TextMain = mainColor;
                    HasText = true;
                }

                int secondKey = -1, secondCount = 0;
                foreach (var kv in colorCount)
                    if (kv.Key != bestColor && kv.Value > secondCount) { secondCount = kv.Value; secondKey = kv.Key; }
                TextDim = secondKey >= 0 && colorValue.TryGetValue(secondKey, out var dimColor)
                    ? dimColor
                    : new Color(TextMain.r * 0.75f, TextMain.g * 0.75f, TextMain.b * 0.75f, 1f);

                // 强调色从装饰边框的 RGB 派生（那正是游戏画亮边用的颜色）
                Accent = HasFrame
                    ? Brighten(new Color(FrameColor.r, FrameColor.g, FrameColor.b, 1f), 1.9f)
                    : Brighten(TextMain, 1.35f);
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[样式] 文本样式捕获失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 挑参考窗口。优先机体信息窗（就是目标观感），拿不到再依次退到统计窗/背包窗/科技树。
        /// </summary>
        private static GameObject FindReferenceWindow(UIRoot uiRoot)
        {
            if (uiRoot == null) return null;
            var uiGame = uiRoot.uiGame;
            if (uiGame == null) return null;

            try { if (uiGame.mechaWindow != null) return uiGame.mechaWindow.gameObject; } catch { }
            try { if (uiGame.statWindow != null) return uiGame.statWindow.gameObject; } catch { }
            try { if (uiGame.inventoryWindow != null) return uiGame.inventoryWindow.gameObject; } catch { }
            try { if (uiGame.techTree != null) return uiGame.techTree.gameObject; } catch { }
            return null;
        }

        // ---------------------------------------------------------------- 应用

        public void ApplyPanel(Image image)
        {
            if (image == null) return;
            if (HasPanel && PanelSprite != null)
            {
                image.sprite = PanelSprite;
                image.type = PanelType;
                image.fillCenter = PanelFillCenter;
            }
            image.color = PanelColor;
        }

        /// <summary>
        /// 标题栏。机体信息窗**没有**独立标题栏——「机体信息」四个字直接画在面板上，
        /// 顶部右侧只有窗口按钮。所以这里固定全透明，让底衬与边框透出来。
        /// </summary>
        public static void ApplyHeader(Image image)
        {
            if (image == null) return;
            image.sprite = null;
            image.color = new Color(0f, 0f, 0f, 0f);
        }

        /// <summary>装饰边框层：叠在底衬之上的那张 border 贴图（带切角与亮边）。</summary>
        public void ApplyFrame(Image image)
        {
            if (image == null) return;
            if (!HasFrame || FrameSprite == null)
            {
                image.sprite = null;
                image.color = new Color(0f, 0f, 0f, 0f);
                return;
            }
            image.sprite = FrameSprite;
            image.type = FrameType;
            image.color = FrameColor;
        }

        public void ApplyGlass(Image image)
        {
            if (image == null) return;
            if (!HasGlass || GlassSprite == null)
            {
                image.sprite = null;
                image.color = new Color(0f, 0f, 0f, 0f);
                return;
            }
            image.sprite = GlassSprite;
            image.type = Image.Type.Sliced;
            image.color = GlassColor;
        }

        public void ApplyInput(Image image)
        {
            if (image == null) return;
            if (HasInput && InputSprite != null)
            {
                image.sprite = InputSprite;
                image.type = InputType;
                image.color = InputColor;
            }
            else
            {
                image.color = Darken(PanelColor, 0.8f);
            }
        }

        // ---------------------------------------------------------------- 工具

        private static Color Darken(Color c, float factor)
        {
            return new Color(c.r * factor, c.g * factor, c.b * factor, c.a);
        }

        /// <summary>提亮并保持不透明；灰度色提亮会溢出，改为往青白偏移。</summary>
        private static Color Brighten(Color c, float factor)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max > 0.85f)
                return new Color(Mathf.Clamp01(c.r * 0.75f + 0.15f), Mathf.Clamp01(c.g * 0.85f + 0.15f), 1f, 1f);

            return new Color(
                Mathf.Clamp01(c.r * factor),
                Mathf.Clamp01(c.g * factor),
                Mathf.Clamp01(c.b * factor),
                1f);
        }

        private static string Fmt(Color c)
        {
            return $"({c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###})";
        }

        /// <summary>把窗口里面积最大的若干 Image 打进日志，便于启发式失效时定位。</summary>
        private static void DumpCandidates(GameObject reference, BepInEx.Logging.ManualLogSource log)
        {
            if (log == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("[样式] 根的直接子物体：");
                for (int i = 0; i < reference.transform.childCount && i < 24; i++)
                {
                    var c = reference.transform.GetChild(i);
                    sb.Append(c.name).Append('(').Append(c.gameObject.activeSelf ? '+' : '-').Append(") ");
                }
                log.LogInfo(sb.ToString());

                var list = new List<(float area, string text)>();
                foreach (var img in reference.GetComponentsInChildren<Image>(true))
                {
                    if (img == null || img.sprite == null || img.rectTransform == null) continue;
                    var r = img.rectTransform.rect;
                    float area = Mathf.Abs(r.width * r.height);
                    var b = img.sprite.border;
                    list.Add((area, string.Format(
                        "{0} | sprite={1} {2:0}x{3:0} border=({4:0},{5:0},{6:0},{7:0}) type={8} color={9}",
                        Path(img.transform, reference.transform), img.sprite.name,
                        r.width, r.height, b.x, b.y, b.z, b.w, img.type, Fmt(img.color))));
                }
                list.Sort((x, y) => y.area.CompareTo(x.area));

                log.LogInfo($"[样式] 候选贴图（按面积前 {Mathf.Min(8, list.Count)} 个）：");
                for (int i = 0; i < list.Count && i < 8; i++) log.LogInfo("[样式]   " + list[i].text);
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[样式] 候选清单导出失败：{ex.Message}");
            }
        }

        private static string Path(Transform node, Transform root)
        {
            var sb = new StringBuilder(node.name);
            var t = node.parent;
            int guard = 0;
            while (t != null && t != root && guard++ < 6) { sb.Insert(0, t.name + "/"); t = t.parent; }
            return sb.ToString();
        }
    }
}
