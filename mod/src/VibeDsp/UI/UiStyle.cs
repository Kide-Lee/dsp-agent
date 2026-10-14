using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VibeDsp.UI
{
    /// <summary>
    /// 从游戏自己的 UI 窗口"抄"一份视觉样式。
    ///
    /// 为什么这么做：机体信息窗（C）等面板的外框贴图来自 UI 预制体，
    /// 程序集里没有可直接引用的资源字段（UIMechaWindow 的 182 个字段里
    /// 没有背景 Image）。与其自己调一组近似色，不如**在运行时把游戏窗口上
    /// Image / Text 的真实 sprite、颜色、字号、九宫格设置读出来**，
    /// 套到我们的控件上——这样观感与游戏原生面板一致，且游戏换皮时自动跟随。
    ///
    /// 全部为启发式挑选，失败时回退到内置配色（见 ChatWindow 的默认值），
    /// 并在日志里说明选中了什么。
    /// </summary>
    internal sealed class UiStyle
    {
        // ---- 面板外框
        public bool HasPanel;
        public Sprite PanelSprite;
        public Color PanelColor;
        public Image.Type PanelType = Image.Type.Simple;
        public bool PanelFillCenter = true;

        // ---- 标题栏
        public bool HasHeader;
        public Sprite HeaderSprite;
        public Color HeaderColor;
        public Image.Type HeaderType = Image.Type.Simple;

        // ---- 输入区背景
        public bool HasInput;
        public Sprite InputSprite;
        public Color InputColor;
        public Image.Type InputType = Image.Type.Simple;

        // ---- 文本
        public bool HasText;
        public Font Font;
        public int BaseFontSize = 14;
        public Color TextMain = new Color(0.878f, 0.918f, 0.961f, 1f);
        public Color TextDim = new Color(0.596f, 0.678f, 0.757f, 1f);
        public Color Accent = new Color(0.353f, 0.749f, 1f, 1f);

        public string Source = "(未捕获)";

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
            if (reference == null)
            {
                log?.LogInfo("[样式] 尚未找到可参考的游戏窗口，稍后重试。");
                return false;
            }

            try
            {
                var images = reference.GetComponentsInChildren<Image>(true);
                var texts = reference.GetComponentsInChildren<Text>(true);

                if (images == null || images.Length == 0)
                {
                    log?.LogInfo($"[样式] {reference.name} 下没有 Image，放弃捕获。");
                    return false;
                }

                // ---- 面板外框：面积最大的、且确实带 sprite 的那个 Image
                Image panel = null;
                float panelArea = 0f;
                Rect panelRect = default;
                foreach (var img in images)
                {
                    if (img == null || img.sprite == null) continue;
                    var rt = img.rectTransform;
                    if (rt == null) continue;
                    var r = rt.rect;
                    float area = Mathf.Abs(r.width * r.height);
                    if (area > panelArea)
                    {
                        panelArea = area;
                        panel = img;
                        panelRect = r;
                    }
                }

                if (panel != null)
                {
                    HasPanel = true;
                    PanelSprite = panel.sprite;
                    PanelColor = panel.color;
                    PanelType = panel.type;
                    PanelFillCenter = panel.fillCenter;
                }

                // ---- 标题栏：位于面板顶部 25% 内、高度不足面板 1/3 的最大 Image
                if (panel != null && panelRect.height > 0f)
                {
                    float panelTop = panelRect.yMax;
                    Image header = null;
                    float headerArea = 0f;
                    foreach (var img in images)
                    {
                        if (img == null || img == panel || img.sprite == null) continue;
                        var rt = img.rectTransform;
                        if (rt == null) continue;
                        var r = rt.rect;
                        if (Mathf.Abs(r.height) > panelRect.height * 0.34f) continue;
                        if (r.center.y < panelTop - panelRect.height * 0.25f) continue;

                        float area = Mathf.Abs(r.width * r.height);
                        if (area > headerArea)
                        {
                            headerArea = area;
                            header = img;
                        }
                    }

                    if (header != null)
                    {
                        HasHeader = true;
                        HeaderSprite = header.sprite;
                        HeaderColor = header.color;
                        HeaderType = header.type;
                    }
                }

                // ---- 输入框背景：窗口里若有 InputField，直接用它的 targetGraphic
                var inputField = reference.GetComponentInChildren<InputField>(true);
                if (inputField != null && inputField.targetGraphic is Image inputImage && inputImage.sprite != null)
                {
                    HasInput = true;
                    InputSprite = inputImage.sprite;
                    InputColor = inputImage.color;
                    InputType = inputImage.type;
                }

                // ---- 文本：取窗口内最常用的字号与颜色作为正文字号/颜色
                if (texts != null && texts.Length > 0)
                {
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

                        // Color 不能直接做字典键（浮点相等不可靠），量化到 8 位
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

                    // 次常用颜色当作"次要文本"（状态栏等），没有就用主色的 70% 亮度
                    int secondKey = -1, secondCount = 0;
                    foreach (var kv in colorCount)
                        if (kv.Key != bestColor && kv.Value > secondCount) { secondCount = kv.Value; secondKey = kv.Key; }
                    TextDim = secondKey >= 0 && colorValue.TryGetValue(secondKey, out var dimColor)
                        ? dimColor
                        : new Color(TextMain.r * 0.75f, TextMain.g * 0.75f, TextMain.b * 0.75f, 1f);
                }

                // 强调色（发言者标签、活动状态）：优先用标题栏颜色的提亮版，
                // 否则用主文本色提亮——总之不引入与游戏无关的自定义色。
                if (HasHeader && HeaderColor.a > 0.01f)
                    Accent = Brighten(HeaderColor, 1.9f);
                else
                    Accent = Brighten(TextMain, 1.35f);

                Source = $"{reference.name}(images={images.Length}, texts={(texts?.Length ?? 0)})";
                log?.LogInfo(
                    $"[样式] 已从 {Source} 捕获：panel={(HasPanel ? PanelSprite?.name ?? "no-sprite" : "无")}" +
                    $" header={(HasHeader ? HeaderSprite?.name ?? "no-sprite" : "无")}" +
                    $" input={(HasInput ? "有" : "无")} fontSize={BaseFontSize}" +
                    $" panelType={(HasPanel ? PanelType.ToString() : "-")}");
                return true;
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[样式] 捕获失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 挑一个"长得像标准面板"的游戏窗口作为参考。
        /// 优选用机体信息窗（就是用户指定的目标观感）。
        /// </summary>
        private static GameObject FindReferenceWindow(UIRoot uiRoot)
        {
            if (uiRoot == null) return null;
            var uiGame = uiRoot.uiGame;
            if (uiGame == null) return null;

            try
            {
                if (uiGame.mechaWindow != null) return uiGame.mechaWindow.gameObject;
            }
            catch { /* 字段缺失时继续尝试其它窗口 */ }

            try
            {
                if (uiGame.statWindow != null) return uiGame.statWindow.gameObject;
            }
            catch { }

            try
            {
                if (uiGame.inventoryWindow != null) return uiGame.inventoryWindow.gameObject;
            }
            catch { }

            try
            {
                if (uiGame.techTree != null) return uiGame.techTree.gameObject;
            }
            catch { }

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

        public void ApplyHeader(Image image)
        {
            if (image == null) return;
            if (HasHeader && HeaderSprite != null)
            {
                image.sprite = HeaderSprite;
                image.type = HeaderType;
            }
            else if (HasPanel && PanelSprite != null)
            {
                // 没有独立标题栏贴图时，用外框贴图 + 稍亮的颜色，观感也接近
                image.sprite = PanelSprite;
                image.type = PanelType;
            }
            image.color = HasHeader ? HeaderColor : Lighten(PanelColor, 1.35f);
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
                image.color = Lighten(PanelColor, 0.8f);
            }
        }

        /// <summary>提亮并保持不透明；用于在游戏配色内派生强调色。</summary>
        private static Color Brighten(Color c, float factor)
        {
            // 灰度色（如白/浅灰标题栏）提亮会溢出，改为往青色偏移一点点，
            // 这样在深色面板上仍然是"高亮"而不是纯白。
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max > 0.85f)
                return new Color(Mathf.Clamp01(c.r * 0.75f + 0.15f), Mathf.Clamp01(c.g * 0.85f + 0.15f), 1f, 1f);

            return new Color(
                Mathf.Clamp01(c.r * factor),
                Mathf.Clamp01(c.g * factor),
                Mathf.Clamp01(c.b * factor),
                1f);
        }

        private static Color Lighten(Color c, float factor)
        {
            return new Color(
                Mathf.Clamp01(c.r * factor),
                Mathf.Clamp01(c.g * factor),
                Mathf.Clamp01(c.b * factor),
                c.a);
        }
    }
}
