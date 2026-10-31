using System;
using UnityEngine;

namespace IcarusII.Input
{
    /// <summary>
    /// <see cref="KeyChord"/> 的 Unity 适配层：名字 ↔ KeyCode、修饰键状态、捕获。
    /// 本文件可以引用 UnityEngine；纯解析/显示逻辑都在 KeyChord 里。
    /// </summary>
    internal static class KeyChordInput
    {
        private static KeyCode[] _allKeys;

        private static KeyCode[] AllKeys => _allKeys ??= (KeyCode[])Enum.GetValues(typeof(KeyCode));

        public static bool TryResolve(KeyChord chord, out KeyCode key)
        {
            key = KeyCode.None;
            if (chord.IsNone) return false;
            if (!Enum.TryParse(chord.Key, true, out key)) return false;
            return key != KeyCode.None;
        }

        public static bool IsModifier(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                case KeyCode.LeftCommand:
                case KeyCode.RightCommand:
                case KeyCode.LeftWindows:
                case KeyCode.RightWindows:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>当前修饰键状态是否与组合完全一致（多按/少按都不算）。</summary>
        public static bool ModifiersMatch(KeyChord chord)
        {
            var ctrl = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            var alt = UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            return ctrl == chord.Ctrl && alt == chord.Alt && shift == chord.Shift;
        }

        public static KeyChord CurrentChord(KeyCode key)
        {
            return KeyChord.Of(
                key.ToString(),
                UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl),
                UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt),
                UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift));
        }

        public static bool IsPressedThisFrame(KeyChord chord)
        {
            if (!TryResolve(chord, out var key)) return false;
            return UnityEngine.Input.GetKeyDown(key) && ModifiersMatch(chord);
        }

        /// <summary>本帧按下的第一个非修饰键（捕获用）；没有则返回 false。</summary>
        public static bool TryCapturePressed(out KeyChord chord)
        {
            chord = KeyChord.None;
            if (!UnityEngine.Input.anyKeyDown) return false;
            foreach (var key in AllKeys)
            {
                if (key == KeyCode.None || IsModifier(key)) continue;
                // 只接受键盘：鼠标/手柄按键不适合作为聊天快捷键。
                var name = key.ToString();
                if (name.StartsWith("Mouse", StringComparison.Ordinal)
                    || name.StartsWith("Joystick", StringComparison.Ordinal)) continue;
                if (!UnityEngine.Input.GetKeyDown(key)) continue;
                chord = CurrentChord(key);
                return true;
            }
            return false;
        }

        /// <summary>本帧是否按了 Esc（捕获时用来取消）。</summary>
        public static bool EscapePressedThisFrame => UnityEngine.Input.GetKeyDown(KeyCode.Escape);
    }
}
