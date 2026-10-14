using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
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

        // ---- 装饰边框（叠在底衬之上的那层，机体窗为 window-border-1）
        public bool HasFrame;
        public Sprite FrameSprite;
        public Color FrameColor;
        public Image.Type FrameType = Image.Type.Sliced;

        // ---- 玻璃高光层（可选，很淡）
        public bool HasGlass;
        public Sprite GlassSprite;
        public Color GlassColor;

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
                DumpCandidates(reference, log);

                var images = reference.GetComponentsInChildren<Image>(true);
                var texts = reference.GetComponentsInChildren<Text>(true);

                if (images == null || images.Length == 0)
                {
                    log?.LogInfo($"[样式] {reference.name} 下没有 Image，放弃捕获。");
                    return false;
                }

                // ---- 图层挑选
                //
                // 实测（机体信息窗）结构是**同尺寸多图层叠加**：
                //   shadow          window-trsl-1-sd 760x502 a=0.31  ← 投影
                //   panel-bg        window-trsl-1    760x502 a=1.00  ← 底衬（不透明）
                //   panel-bg/border window-border-1  760x502 a=0.29  ← 装饰边框
                //   panel-bg/glass  window-glass-1   760x502 a=0.15  ← 玻璃高光
                //
                // 正确做法是「先取窗口级尺寸的图层，再按名字分类」。
                // 上一版按名字打分抢面积，结果"名字含 frame"的小图 GUIFrame_thin
                // 拿到 150 分，把真正的 760x502 外框挤掉了。
                float maxArea = 0f;
                foreach (var img in images)
                {
                    if (img == null || img.sprite == null || img.rectTransform == null) continue;
                    var rr = img.rectTransform.rect;
                    maxArea = Mathf.Max(maxArea, Mathf.Abs(rr.width * rr.height));
                }

                var layers = new List<Image>();
                foreach (var img in images)
                {
                    if (img == null || img.sprite == null || img.rectTransform == null) continue;
                    var rr = img.rectTransform.rect;
                    if (Mathf.Abs(rr.width * rr.height) < maxArea * 0.9f) continue;
                    if (img.type != Image.Type.Sliced) continue;
                    layers.Add(img);
                }

                Image bg = null, frame = null, glass = null;
                foreach (var img in layers)
                {
                    var hay = (img.sprite.name + " " + img.gameObject.name).ToLowerInvariant();
                    bool isShadow = hay.Contains("shadow") || img.sprite.name.EndsWith("-sd");
                    bool isBorder = img.gameObject.name == "border" || hay.Contains("border");
                    bool isGlass = hay.Contains("glass");

                    if (isShadow) continue;
                    if (isBorder) { if (frame == null) frame = img; }
                    else if (isGlass) { if (glass == null) glass = img; }
                    else if (bg == null || img.color.a > bg.color.a) bg = img;  // 底衬取最不透明的
                }

                if (bg == null && layers.Count > 0) bg = layers[0];

                if (bg != null)
                {
                    HasPanel = true;
                    PanelSprite = bg.sprite;
                    PanelColor = bg.color;
                    PanelType = bg.type;
                    PanelFillCenter = bg.fillCenter;
                }

                if (frame != null)
                {
                    HasFrame = true;
                    FrameSprite = frame.sprite;
                    FrameColor = frame.color;
                    FrameType = frame.type;
                }

                if (glass != null)
                {
                    HasGlass = true;
                    GlassSprite = glass.sprite;
                    GlassColor = glass.color;
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

                // 强调色（发言者标签、活动状态）：优先从装饰边框的 RGB 派生
                //（那正是游戏用来画亮边的颜色），否则用主文本色提亮——
                // 总之不引入与游戏无关的自定义色。
                if (HasFrame)
                    Accent = Brighten(new Color(FrameColor.r, FrameColor.g, FrameColor.b, 1f), 1.9f);
                else
                    Accent = Brighten(TextMain, 1.35f);

                Source = $"{reference.name}(images={images.Length}, texts={(texts?.Length ?? 0)})";
                log?.LogInfo(
                    $"[样式] 已从 {Source} 捕获：panel={(HasPanel ? PanelSprite?.name ?? "no-sprite" : "无")}" +

                    $" frame={(HasFrame ? FrameSprite?.name ?? "no-sprite" : "无")}" +
                    $" glass={(HasGlass ? "有" : "无")}" +
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

        /// <summary>
        /// 标题栏。机体信息窗**没有**独立标题栏——「机体信息」四个字直接画在面板上，
        /// 顶部右侧只有窗口按钮。所以这里固定做成全透明，让底衬与边框透出来，
        /// 而不是自己加一条实心色带（那是初版的做法，观感不像游戏原生面板）。
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
                image.color = Lighten(PanelColor, 0.8f);
            }
        }

        /// <summary>
        /// 给候选外框贴图打分。启发式来自实测：机体信息窗里面积最大的
        /// 贴图是 window-trsl-1-sd（半透明底衬），真正的装饰外框是别的。
        /// </summary>
        private static int ScoreFrameSprite(Image img)
        {
            var spriteName = img.sprite != null ? img.sprite.name : string.Empty;
            var goName = img.gameObject.name;
            var hay = (spriteName + " " + goName).ToLowerInvariant();

            int score = 0;
            if (hay.Contains("frame")) score += 100;
            if (hay.Contains("border")) score += 80;
            if (hay.Contains("title")) score += 30;

            // 半透明底衬：明确降权（这是上次踩的坑）
            if (hay.Contains("trsl") || hay.Contains("trans")) score -= 120;
            if (img.color.a < 0.98f) score -= 40;

            // 九宫格边框类贴图通常是 Sliced
            if (img.type == Image.Type.Sliced) score += 20;
            if (img.sprite != null && img.sprite.border != Vector4.zero) score += 30;

            if (hay.Contains("window") || hay.Contains("panel")) score += 10;
            return score;
        }

        /// <summary>
        /// 把窗口里"面积最大的若干 Image"与根的直接子物体打进日志，
        /// 用于在外框/标题栏挑选错误时快速定位该选哪一张。
        /// </summary>
        private static void DumpCandidates(GameObject reference, BepInEx.Logging.ManualLogSource log)
        {
            if (log == null) return;
            try
            {
                var sb = new System.Text.StringBuilder();
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
                    if (img == null || img.sprite == null) continue;
                    var rt = img.rectTransform;
                    if (rt == null) continue;
                    var r = rt.rect;
                    float area = Mathf.Abs(r.width * r.height);
                    var b = img.sprite.border;
                    list.Add((area, string.Format(
                        "{0} | sprite={1} {2:0}x{3:0} border=({4:0},{5:0},{6:0},{7:0}) type={8} a={9:0.00} score={10}",
                        Path(img.transform, reference.transform), img.sprite.name,
                        r.width, r.height, b.x, b.y, b.z, b.w, img.type, img.color.a,
                        ScoreFrameSprite(img))));
                }
                list.Sort((x, y) => y.area.CompareTo(x.area));

                log.LogInfo($"[样式] 候选贴图（按面积前 {Mathf.Min(12, list.Count)} 个，格式：路径 | 贴图 尺寸 边框 类型 透明度 评分）：");
                for (int i = 0; i < list.Count && i < 12; i++) log.LogInfo("[样式]   " + list[i].text);
            }
            catch (Exception ex)
            {
                log.LogWarning($"[样式] 候选清单导出失败：{ex.Message}");
            }
        }

        private static string Path(Transform node, Transform root)
        {
            var sb = new System.Text.StringBuilder(node.name);
            var t = node.parent;
            int guard = 0;
            while (t != null && t != root && guard++ < 6) { sb.Insert(0, t.name + "/"); t = t.parent; }
            return sb.ToString();
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
