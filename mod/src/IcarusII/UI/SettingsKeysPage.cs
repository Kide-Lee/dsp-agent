using System;
using System.Collections.Generic;
using IcarusII.Config;
using IcarusII.I18n;
using IcarusII.Input;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 设定页「快捷键」分区：显示当前绑定、捕获新按键、恢复默认。
    ///
    /// 布局与控件全部来自 prefab；这里只写文案、切显隐、接事件。
    /// 捕获期间由 <see cref="Tick"/> 每帧轮询，10 秒无输入自动取消。
    /// </summary>
    internal sealed class SettingsKeysPage
    {
        private const float CaptureTimeoutSeconds = 10f;

        private readonly SettingsPageView _view;
        private string _capturing;
        private float _captureStartedAt;

        public SettingsKeysPage(SettingsPageView view)
        {
            _view = view;
            Wire();
            Refresh();
        }

        public bool IsCapturing => !string.IsNullOrEmpty(_capturing);

        private void Wire()
        {
            foreach (var row in _view.KeyRows)
            {
                var action = row.Action;
                if (row.InputButton != null)
                    row.InputButton.onClick.AddListener(() => BeginCapture(action));
                if (row.DefaultButton != null)
                    row.DefaultButton.onClick.AddListener(() => ApplyDefault(action));
                if (row.ClearButton != null)
                    row.ClearButton.onClick.AddListener(CancelCapture);
            }
            if (_view.KeysResetButton != null)
                _view.KeysResetButton.onClick.AddListener(ResetAll);
        }

        /// <summary>重新读配置并刷新三行的键名。</summary>
        public void Refresh()
        {
            foreach (var row in _view.KeyRows)
            {
                var chord = CurrentBinding(row.Action);
                if (row.KeyLabel != null) row.KeyLabel.text = chord.Display();
                if (row.WaitingText != null) row.WaitingText.gameObject.SetActive(false);
                if (row.KeyLabel != null) row.KeyLabel.gameObject.SetActive(true);
            }
            CancelCapture();
        }

        public void Tick()
        {
            if (!IsCapturing) return;

            if (Time.unscaledTime - _captureStartedAt > CaptureTimeoutSeconds)
            {
                CancelCapture();
                return;
            }
            if (KeyChordInput.EscapePressedThisFrame)
            {
                VFInput.UseEscape();
                CancelCapture();
                return;
            }
            if (!KeyChordInput.TryCapturePressed(out var chord)) return;
            Apply(_capturing, chord);
        }

        private static KeyChord CurrentBinding(string action)
        {
            return Plugin.Instance?.CurrentKeyBinding(action) ?? KeyChord.None;
        }

        private static KeyChord DefaultBinding(string action)
        {
            switch (action)
            {
                case PluginConfig.KeyActionSend: return PluginConfig.DefaultSend;
                case PluginConfig.KeyActionNewline: return PluginConfig.DefaultNewline;
                default: return PluginConfig.DefaultHotkey;
            }
        }

        private static string ActionName(string action)
        {
            switch (action)
            {
                case PluginConfig.KeyActionSend: return Strings.Get("settings.keys.action.send");
                case PluginConfig.KeyActionNewline: return Strings.Get("settings.keys.action.newline");
                default: return Strings.Get("settings.keys.action.hotkey");
            }
        }

        private void BeginCapture(string action)
        {
            CancelCapture();
            _capturing = action;
            _captureStartedAt = Time.unscaledTime;
            var row = FindRow(action);
            if (row == null) return;
            if (row.KeyLabel != null) row.KeyLabel.gameObject.SetActive(false);
            if (row.WaitingText != null)
            {
                row.WaitingText.text = Strings.Get("settings.keys.capturing");
                row.WaitingText.gameObject.SetActive(true);
            }
            SetConflict(row, string.Empty);
        }

        private void CancelCapture()
        {
            if (IsCapturing)
            {
                var row = FindRow(_capturing);
                if (row != null)
                {
                    if (row.WaitingText != null) row.WaitingText.gameObject.SetActive(false);
                    if (row.KeyLabel != null) row.KeyLabel.gameObject.SetActive(true);
                }
            }
            _capturing = null;
        }

        private void ApplyDefault(string action)
        {
            Apply(action, DefaultBinding(action));
        }

        private void Apply(string action, KeyChord chord)
        {
            var row = FindRow(action);
            var conflict = Plugin.Instance?.KeyConflict(action, chord);
            if (!string.IsNullOrEmpty(conflict))
            {
                if (row != null)
                {
                    if (row.WaitingText != null) row.WaitingText.gameObject.SetActive(false);
                    if (row.KeyLabel != null) row.KeyLabel.gameObject.SetActive(true);
                }
                if (row != null) SetConflict(row, Strings.Get("settings.keys.conflict", ActionName(conflict)));
                _capturing = null;
                return;
            }

            var applied = Plugin.Instance?.ApplyKeyBindingFromSettings(action, chord) == true;
            _capturing = null;
            if (!applied) return;
            Refresh();
            if (row != null) SetConflict(row, Strings.Get("settings.keys.saved", chord.Display()));
        }

        private void ResetAll()
        {
            Plugin.Instance?.ResetKeyBindingsFromSettings();
            Refresh();
            foreach (var row in _view.KeyRows) SetConflict(row, string.Empty);
        }

        private SettingsPageView.KeyRowBinding FindRow(string action)
        {
            foreach (var row in _view.KeyRows)
            {
                if (row.Action == action) return row;
            }
            return null;
        }

        private static void SetConflict(SettingsPageView.KeyRowBinding row, string text)
        {
            if (row.ConflictText == null) return;
            row.ConflictText.text = text;
            row.ConflictText.color = string.IsNullOrEmpty(text)
                ? new Color(1f, 1f, 1f, 0.78f)
                : new Color(0.99f, 0.78f, 0.39f, 1f);
        }
    }
}
