using System;
using System.Text;

namespace IcarusII.Input
{
    /// <summary>
    /// 一个快捷键组合：Unity KeyCode 名字 + Ctrl / Alt / Shift。
    ///
    /// 刻意不引用 UnityEngine，这样离线测试（tests/ProtocolTests）可以直接编译本文件。
    /// 运行期把名字解析成 KeyCode 的工作在 <see cref="KeyChordInput"/>。
    /// </summary>
    public readonly struct KeyChord : IEquatable<KeyChord>
    {
        public readonly string Key;
        public readonly bool Ctrl;
        public readonly bool Alt;
        public readonly bool Shift;

        private KeyChord(string key, bool ctrl, bool alt, bool shift)
        {
            Key = key;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
        }

        /// <summary>空绑定：没有主键。配置里读不出来时用它表示"无效"。</summary>
        public static readonly KeyChord None = default;

        public bool IsNone => string.IsNullOrEmpty(Key);

        public static KeyChord Of(string key, bool ctrl = false, bool alt = false, bool shift = false)
        {
            var normalized = NormalizeKey(key);
            if (normalized is null) return None;
            return new KeyChord(normalized, ctrl, alt, shift);
        }

        /// <summary>解析 `Ctrl+Alt+Shift+Key`；空串或只有修饰键都返回 false。</summary>
        public static bool TryParse(string text, out KeyChord chord)
        {
            chord = None;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split('+');
            var ctrl = false;
            var alt = false;
            var shift = false;
            string key = null;
            foreach (var raw in parts)
            {
                var token = raw.Trim();
                if (token.Length == 0) return false;
                switch (token.ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL":
                        ctrl = true;
                        continue;
                    case "ALT":
                    case "OPTION":
                        alt = true;
                        continue;
                    case "SHIFT":
                        shift = true;
                        continue;
                }
                // 主键只允许出现一次。
                if (key is not null) return false;
                key = NormalizeKey(token);
                if (key is null) return false;
            }

            if (key is null) return false;
            chord = new KeyChord(key, ctrl, alt, shift);
            return true;
        }

        /// <summary>解析失败时回落到 fallback；配置读取用。</summary>
        public static KeyChord ParseOrDefault(string text, KeyChord fallback)
        {
            return TryParse(text, out var chord) ? chord : fallback;
        }

        /// <summary>配置文件里的规范形态：`Ctrl+Shift+Return`。</summary>
        public string Format()
        {
            if (IsNone) return string.Empty;
            var builder = new StringBuilder();
            if (Ctrl) builder.Append("Ctrl+");
            if (Alt) builder.Append("Alt+");
            if (Shift) builder.Append("Shift+");
            builder.Append(Key);
            return builder.ToString();
        }

        /// <summary>设置页里的显示形态：`Ctrl + Enter`。</summary>
        public string Display()
        {
            if (IsNone) return string.Empty;
            var builder = new StringBuilder();
            if (Ctrl) builder.Append("Ctrl + ");
            if (Alt) builder.Append("Alt + ");
            if (Shift) builder.Append("Shift + ");
            builder.Append(FriendlyKey(Key));
            return builder.ToString();
        }

        /// <summary>
        /// 该组合在普通输入框里按下时会不会先打出一个可打印字符。
        /// 会的话，聊天输入框必须在 onValidateInput 里把这个字符吞掉。
        /// </summary>
        public bool TryGetTypedChar(out char typed)
        {
            typed = '\0';
            if (IsNone) return false;
            // 带 Ctrl/Alt 的组合不会走字符输入；Shift 只影响大小写/符号。
            if (Ctrl || Alt) return false;
            return TryGetTypedChar(Key, Shift, out typed);
        }

        /// <summary>
        /// Unity KeyCode 名字 → 它输入的那个字符。纯逻辑，供离线测试与输入拦截共用。
        /// 非可打印键（Return/F1/方向键…）返回 false。
        /// </summary>
        public static bool TryGetTypedChar(string key, bool shift, out char typed)
        {
            typed = '\0';
            if (string.IsNullOrEmpty(key)) return false;
            var name = key.Trim();

            if (name.Length == 1)
            {
                var ch = name[0];
                if (ch >= 'A' && ch <= 'Z')
                {
                    typed = shift ? ch : char.ToLowerInvariant(ch);
                    return true;
                }
                if (ch >= '0' && ch <= '9')
                {
                    typed = ch;
                    return true;
                }
            }

            switch (name.ToUpperInvariant())
            {
                case "ALPHA0": typed = shift ? ')' : '0'; return true;
                case "ALPHA1": typed = shift ? '!' : '1'; return true;
                case "ALPHA2": typed = shift ? '@' : '2'; return true;
                case "ALPHA3": typed = shift ? '#' : '3'; return true;
                case "ALPHA4": typed = shift ? '$' : '4'; return true;
                case "ALPHA5": typed = shift ? '%' : '5'; return true;
                case "ALPHA6": typed = shift ? '^' : '6'; return true;
                case "ALPHA7": typed = shift ? '&' : '7'; return true;
                case "ALPHA8": typed = shift ? '*' : '8'; return true;
                case "ALPHA9": typed = shift ? '(' : '9'; return true;
                case "SPACE": typed = ' '; return true;
                case "PERIOD": typed = shift ? '>' : '.'; return true;
                case "COMMA": typed = shift ? '<' : ','; return true;
                case "SEMICOLON": typed = shift ? ':' : ';'; return true;
                case "SLASH": typed = shift ? '?' : '/'; return true;
                case "BACKSLASH": typed = shift ? '|' : '\\'; return true;
                case "QUOTE": typed = shift ? '"' : '\''; return true;
                case "LEFTBRACKET": typed = shift ? '{' : '['; return true;
                case "RIGHTBRACKET": typed = shift ? '}' : ']'; return true;
                case "BACKQUOTE": typed = shift ? '~' : '`'; return true;
                case "MINUS": typed = shift ? '_' : '-'; return true;
                case "EQUALS": typed = shift ? '+' : '='; return true;
                case "KEYPADPERIOD": typed = '.'; return true;
                case "KEYPADDIVIDE": typed = '/'; return true;
                case "KEYPADMULTIPLY": typed = '*'; return true;
                case "KEYPADMINUS": typed = '-'; return true;
                case "KEYPADPLUS": typed = '+'; return true;
                case "KEYPAD0": typed = '0'; return true;
                case "KEYPAD1": typed = '1'; return true;
                case "KEYPAD2": typed = '2'; return true;
                case "KEYPAD3": typed = '3'; return true;
                case "KEYPAD4": typed = '4'; return true;
                case "KEYPAD5": typed = '5'; return true;
                case "KEYPAD6": typed = '6'; return true;
                case "KEYPAD7": typed = '7'; return true;
                case "KEYPAD8": typed = '8'; return true;
                case "KEYPAD9": typed = '9'; return true;
            }
            if (name.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase)) return false;
            return false;
        }

        /// <summary>捕获到的 KeyCode 名 → 配置里的规范名；只有别名会变。</summary>
        public static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var trimmed = key.Trim();
            switch (trimmed.ToUpperInvariant())
            {
                case "ENTER": return "Return";
                case "RETURN": return "Return";
                case "ESC": return "Escape";
                case "ESCAPE": return "Escape";
                case "SPACE": return "Space";
                case "SPACEBAR": return "Space";
                case "TAB": return "Tab";
                case "BACKSPACE": return "Backspace";
                case "DELETE": return "Delete";
                case "UP": return "UpArrow";
                case "DOWN": return "DownArrow";
                case "LEFT": return "LeftArrow";
                case "RIGHT": return "RightArrow";
                case "CONTROL": return "LeftControl";
                case "CTRL": return "LeftControl";
            }
            if (trimmed.Length == 1) return trimmed.ToUpperInvariant();
            if (trimmed.Length >= 2 && (trimmed[0] == 'f' || trimmed[0] == 'F'))
            {
                var digits = trimmed.Substring(1);
                var allDigits = digits.Length > 0;
                foreach (var ch in digits)
                {
                    if (ch < '0' || ch > '9') { allDigits = false; break; }
                }
                if (allDigits) return "F" + digits;
            }
            return trimmed;
        }

        /// <summary>显示用的键名；只做最常见的翻译，不认识的直接用 Unity 名。</summary>
        public static string FriendlyKey(string key)
        {
            switch (NormalizeKey(key))
            {
                case "Return": return "Enter";
                case "Escape": return "Esc";
                case "UpArrow": return "↑";
                case "DownArrow": return "↓";
                case "LeftArrow": return "←";
                case "RightArrow": return "→";
                case "LeftShift": return "Shift";
                case "RightShift": return "Shift";
                case "LeftControl": return "Ctrl";
                case "RightControl": return "Ctrl";
                case "LeftAlt": return "Alt";
                case "RightAlt": return "Alt";
                default: return NormalizeKey(key) ?? string.Empty;
            }
        }

        public bool Equals(KeyChord other)
        {
            return string.Equals(NormalizeKey(Key), NormalizeKey(other.Key), StringComparison.OrdinalIgnoreCase)
                && Ctrl == other.Ctrl
                && Alt == other.Alt
                && Shift == other.Shift;
        }

        public override bool Equals(object obj) => obj is KeyChord other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Key is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(NormalizeKey(Key));
                hash = (hash * 397) ^ (Ctrl ? 1 : 0);
                hash = (hash * 397) ^ (Alt ? 2 : 0);
                hash = (hash * 397) ^ (Shift ? 4 : 0);
                return hash;
            }
        }

        public static bool operator ==(KeyChord left, KeyChord right) => left.Equals(right);
        public static bool operator !=(KeyChord left, KeyChord right) => !left.Equals(right);

        public override string ToString() => Format();
    }
}
