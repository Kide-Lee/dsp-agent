using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VibeDsp.UI
{
    /// <summary>
    /// 找一个能显示中文的字体。
    ///
    /// 优先级（从最贴合游戏到最兜底）：
    ///  1. 从场景里已有的 UI Text 借 —— 游戏自己会按语言替换字体
    ///     （UnityEngine.UI.Youthcat.TextLayoutLocale 里的 LanguageFontPair），
    ///     所以借来的字体天然是"当前语言正确的那个"，且风格一致。
    ///  2. 操作系统里装了的中文字体（原生 Windows / Wine 都能取到）。
    ///  3. Unity 内置 Arial —— 不含中文，仅保证 UI 不崩。
    /// </summary>
    internal static class FontProvider
    {
        private static Font _cached;
        private static string _source;

        public static string Source => _source;

        /// <summary>需要中文（用于判断是否必须校验汉字覆盖）。</summary>
        private static bool NeedCjk => Game.GameRefs.IsChinese || true; // M0 界面恒为中文

        public static Font Resolve()
        {
            if (_cached != null) return _cached;

            var borrowed = BorrowFromGameUi();
            if (borrowed != null)
            {
                _cached = borrowed;
                _source = "borrowed-from-game-ui";
                return _cached;
            }

            var os = FindOsCjkFont();
            if (os != null)
            {
                _cached = os;
                _source = "os-font";
                return _cached;
            }

            var builtin = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _cached = builtin;
            _source = "builtin-arial(no-cjk)";
            return _cached;
        }

        public static void Invalidate()
        {
            _cached = null;
            _source = null;
        }

        // ---------------------------------------------------------------- 策略实现

        private static Font BorrowFromGameUi()
        {
            try
            {
                // FindObjectsOfTypeAll 会包含未激活对象，正是我们要的：
                // 游戏 UI 里一定有 Text，且其 font 已按语言设置好。
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                if (texts == null) return null;

                Font fallback = null;
                foreach (var t in texts)
                {
                    if (t == null || t.font == null) continue;
                    if (fallback == null) fallback = t.font;
                    if (CanRenderCjk(t.font)) return t.font;
                }
                return fallback;
            }
            catch
            {
                return null;
            }
        }

        private static readonly string[] PreferredOsFonts =
        {
            // Windows
            "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun", "宋体", "MS Gothic", "Malgun Gothic",
            // Linux（Steam Deck / 常见发行版）
            "Noto Sans CJK SC", "Noto Sans CJK", "Source Han Sans SC", "WenQuanYi Micro Hei",
            "WenQuanYi Zen Hei", "Droid Sans Fallback", "AR PL UMing CN",
            // macOS（不常见，但加上无害）
            "PingFang SC", "Hiragino Sans GB", "STHeiti", "Arial Unicode MS",
        };

        private static Font FindOsCjkFont()
        {
            HashSet<string> installed = null;
            try
            {
                var names = Font.GetOSInstalledFontNames();
                if (names != null) installed = new HashSet<string>(names);
            }
            catch
            {
                // 某些平台不支持枚举；退化为"直接尝试创建"。
            }

            foreach (var name in PreferredOsFonts)
            {
                if (installed != null && !installed.Contains(name)) continue;
                try
                {
                    var font = Font.CreateDynamicFontFromOSFont(name, 16);
                    if (font != null && CanRenderCjk(font)) return font;
                }
                catch
                {
                    // 试下一个
                }
            }
            return null;
        }

        private static bool CanRenderCjk(Font font)
        {
            if (font == null) return false;
            try
            {
                // '铁' / '球' —— 戴森球计划里必然出现的字
                return font.HasCharacter('铁') && font.HasCharacter('球');
            }
            catch
            {
                return false;
            }
        }
    }
}
