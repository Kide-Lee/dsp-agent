using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
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
        private static string _detail;

        public static string Source => _source;

        /// <summary>选中该字体的依据（写进日志便于诊断）。</summary>
        public static string Detail => _detail;

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
                // FindObjectsOfTypeAll 同时覆盖已实例化对象与未实例化的 prefab 资源，
                // 所以这里统计到的是"整个 UI 的设计期用字情况"。
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                if (texts == null) return null;

                // 不能只取"第一个能画汉字的字体"：DIN 这类拉丁字体经过 OS 字体回退
                // 也会声称 HasCharacter('铁') == true，结果正文被设成 DIN（实测踩到）。
                // 改成统计"哪个字体被最多**含中文**的文本在用"——等价于问
                // "游戏自己拿哪个字体排中文"，这才是正文字体。
                var total = new Dictionary<Font, int>();
                var cjk = new Dictionary<Font, int>();

                foreach (var t in texts)
                {
                    if (t == null || t.font == null) continue;

                    int n;
                    total.TryGetValue(t.font, out n);
                    total[t.font] = n + 1;

                    if (!ContainsCjk(t.text)) continue;
                    cjk.TryGetValue(t.font, out n);
                    cjk[t.font] = n + 1;
                }

                // 正文优先用 Regular 字重：SAIRASB 是 SemiBold，排长段对话偏粗、
                // 和游戏正文（物品描述、提示文本）不是一个观感。有 SAIRAR 就用它。
                foreach (var kv in cjk)
                {
                    if (kv.Key != null && kv.Key.name == "SAIRAR" && kv.Value > 0)
                    {
                        int usedIn;
                        total.TryGetValue(kv.Key, out usedIn);
                        _detail = $"{kv.Key.name}（{kv.Value} 处中文文本在用，总计 {usedIn} 处；正文用 Regular）";
                        return kv.Key;
                    }
                }

                Font best = null;
                var bestCount = 0;

                foreach (var kv in cjk)
                {
                    if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
                }
                if (best != null)
                {
                    int usedIn;
                    total.TryGetValue(best, out usedIn);
                    _detail = $"{best.name}（{bestCount} 处中文文本在用，总计 {usedIn} 处）";
                    return best;
                }

                // 没有任何中文文本可统计（英文环境）：退到用得最多的那个
                foreach (var kv in total)
                {
                    if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
                }
                if (best != null) _detail = $"{best.name}（无中文样本，取使用最多者，{bestCount} 处）";
                return best;
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s)
            {
                if (c > 0x2E80) return true;
            }
            return false;
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
                // '铁' / '球' —— 戴森球计划里必然出现的字。
                // 注意：这个 API 会连系统回退字体一起查，所以 DIN 之类纯拉丁字体
                // 也会返回 true。它只用在校验"操作系统里挑出来的中文字体"，
                // 游戏内字体的挑选不走这里（见 BorrowFromGameUi 的用量统计）。
                return font.HasCharacter('铁') && font.HasCharacter('球');
            }
            catch
            {
                return false;
            }
        }
    }
}
