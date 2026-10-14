using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using DSPAgent.Config;
using DSPAgent.I18n;

namespace DSPAgent.UI
{
    /// <summary>
    /// 聊天窗口。
    ///
    /// 设计取舍：
    ///  - 界面在首次进入游戏时**一次性构建**，之后只用 SetActive 开关，
    ///    不销毁重建（避免 GC 抖动与重建闪烁）。
    ///  - 整段聊天记录用**一个 Text** 承载（内部用 committed + streaming 两段拼接），
    ///    而不是每条消息一个 GameObject：M0 阶段更简单，且 UGUI 的
    ///    ContentSizeFitter 能自动撑高。消息卡片化留到 M1 的工具卡片需求出现时再做。
    ///  - 渲染节流到每帧最多一次：流式回复可能一帧收到多个 delta。
    /// </summary>
    internal sealed class ChatWindow
    {
        private const int MaxTranscriptChars = 120000;

        // 与游戏界面风格接近的配色
        private static readonly Color PanelBg = new Color(0.055f, 0.082f, 0.125f, 0.97f);
        private static readonly Color HeaderBg = new Color(0.098f, 0.157f, 0.235f, 1f);
        private static readonly Color InputBg = new Color(0.098f, 0.129f, 0.180f, 1f);
        private static readonly Color TextMain = new Color(0.878f, 0.918f, 0.961f, 1f);
        private static readonly Color TextDim = new Color(0.596f, 0.678f, 0.757f, 1f);
        private static readonly Color Accent = new Color(0.353f, 0.749f, 1f, 1f);

        private GameObject _root;
        private RectTransform _panel;
        private RectTransform _content;
        private Text _statusText;
        private Text _transcript;
        private ScrollRect _scroll;
        private InputField _input;

        private readonly List<string> _lines = new List<string>();
        private readonly StringBuilder _committed = new StringBuilder();
        private readonly StringBuilder _current = new StringBuilder();
        private readonly StringBuilder _renderScratch = new StringBuilder();

        private bool _dirty;
        private int _fontSize;
        private int _maxLines;

        /// <summary>打开窗口前 VFInput.inputing 的原值，关闭时还原。</summary>
        private bool _savedInputing;

        /// <summary>窗口打开期间需要持续压制游戏输入（见 Plugin.LateUpdate）。</summary>
        public bool WantsInputSuppressed => IsOpen;

        /// <summary>玩家提交了一条消息。参数为去除首尾空白后的文本。</summary>
        public event Action<string> Submitted;

        public bool IsOpen { get; private set; }

        /// <summary>GameObject 仍然存活（Unity 的 == null 对已销毁对象返回 true）。</summary>
        public bool Alive => _root != null;

        public bool Created => _root != null;

        /// <summary>
        /// 场景切换后 UIRoot 会重建，挂在它下面的窗口会一起被销毁。
        /// 调用方检测到 !Alive 时调用本方法清掉失效引用，下次 Create 会重新搭建。
        /// </summary>
        public void Reset()
        {
            _root = null;
            _panel = null;
            _content = null;
            _statusText = null;
            _transcript = null;
            _scroll = null;
            _input = null;
            _lines.Clear();
            _committed.Length = 0;
            _current.Length = 0;
            _dirty = false;
            IsOpen = false;
        }

        // ---------------------------------------------------------------- 构建

        public void Create(Transform parent, Font font, PluginConfig config)
        {
            if (_root != null) return;

            _fontSize = Mathf.RoundToInt(14f * Mathf.Max(0.5f, config.FontScale));
            _maxLines = Mathf.Max(50, config.MaxTranscriptLines);

            UiFactory.EnsureEventSystem();

            var rootRt = UiFactory.Node("DSPAgentChat", parent);
            UiFactory.Stretch(rootRt);
            _root = rootRt.gameObject;

            // 面板：右下角固定尺寸
            _panel = UiFactory.Node("Panel", rootRt);
            UiFactory.AnchoredTo(_panel,
                anchor: new Vector2(1f, 0f),
                pivot: new Vector2(1f, 0f),
                offset: new Vector2(-24f, 24f),
                size: new Vector2(config.PanelWidth, config.PanelHeight));
            UiFactory.AddPanel(_panel, PanelBg);

            BuildHeader(font);
            BuildTranscript(font);
            BuildInputBar(font);

            SetStatus(Strings.Get("status.disconnected"), TextDim);
            _root.SetActive(false);
        }

        private void BuildHeader(Font font)
        {
            var header = UiFactory.Node("Header", _panel);
            UiFactory.TopBar(header, 34f);
            UiFactory.AddPanel(header, HeaderBg);

            var titleRt = UiFactory.Node("Title", header);
            UiFactory.Stretch(titleRt, left: 12f, top: 0f, right: 260f, bottom: 0f);
            var title = UiFactory.AddLabel(titleRt, font, _fontSize + 2, Accent, TextAnchor.MiddleLeft);
            title.text = Strings.Get("title");

            var statusRt = UiFactory.Node("Status", header);
            UiFactory.Stretch(statusRt, left: 200f, top: 0f, right: 12f, bottom: 0f);
            _statusText = UiFactory.AddLabel(statusRt, font, _fontSize - 1, TextDim, TextAnchor.MiddleRight);
            _statusText.text = string.Empty;
        }

        private void BuildTranscript(Font font)
        {
            var scrollRt = UiFactory.Node("Scroll", _panel);
            UiFactory.Stretch(scrollRt, left: 8f, top: 34f + 6f, right: 8f, bottom: 38f + 6f);

            _scroll = scrollRt.gameObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;

            var viewport = UiFactory.Node("Viewport", scrollRt);
            UiFactory.Stretch(viewport);
            // RectMask2D 比 Mask 轻，且不需要额外材质
            viewport.gameObject.AddComponent<RectMask2D>();

            _content = UiFactory.Node("Content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = new Vector2(0f, -4f);
            _content.sizeDelta = new Vector2(-20f, 100f);

            _transcript = UiFactory.AddLabel(_content, font, _fontSize, TextMain, TextAnchor.UpperLeft);
            _transcript.horizontalOverflow = HorizontalWrapMode.Wrap;
            _transcript.verticalOverflow = VerticalWrapMode.Overflow;

            var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport = viewport;
            _scroll.content = _content;
        }

        private void BuildInputBar(Font font)
        {
            var bar = UiFactory.Node("InputBar", _panel);
            UiFactory.BottomBar(bar, 38f);

            var inputRt = UiFactory.Node("Input", bar);
            UiFactory.Stretch(inputRt, left: 8f, top: 3f, right: 8f, bottom: 5f);
            var bg = UiFactory.AddPanel(inputRt, InputBg);

            var textRt = UiFactory.Node("Text", inputRt);
            UiFactory.Stretch(textRt, left: 8f, top: 0f, right: 8f, bottom: 0f);
            var text = UiFactory.AddLabel(textRt, font, _fontSize, TextMain, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;

            var placeholderRt = UiFactory.Node("Placeholder", inputRt);
            UiFactory.Stretch(placeholderRt, left: 8f, top: 0f, right: 8f, bottom: 0f);
            var placeholder = UiFactory.AddLabel(placeholderRt, font, _fontSize, TextDim, TextAnchor.MiddleLeft);
            placeholder.supportRichText = false;
            placeholder.horizontalOverflow = HorizontalWrapMode.Overflow;
            placeholder.text = Strings.Get("input.placeholder");

            _input = inputRt.gameObject.AddComponent<InputField>();
            _input.textComponent = text;
            _input.placeholder = placeholder;
            _input.targetGraphic = bg;
            _input.lineType = InputField.LineType.SingleLine;
            _input.characterLimit = 2000;
            _input.onEndEdit.AddListener(OnInputEndEdit);
        }

        // ---------------------------------------------------------------- 开关

        public void SetOpen(bool open)
        {
            if (_root == null) return;

            IsOpen = open;
            _root.SetActive(open);

            if (open)
            {
                // 让游戏停止响应移动/建造输入。记录原值以便还原。
                _savedInputing = Game.GameRefs.SetTextInputActive(true);
                UiFactory.EnsureEventSystem();
                if (_input != null)
                {
                    _input.ActivateInputField();
                    _input.Select();
                }
                ScrollToBottom();
            }
            else
            {
                if (_input != null)
                {
                    _input.DeactivateInputField();
                    _input.text = string.Empty;
                }
                // 还原游戏的输入标记
                Game.GameRefs.SetTextInputActive(_savedInputing);
            }
        }

        // ---------------------------------------------------------------- 内容

        public void SetStatus(string text, Color? color = null)
        {
            if (_statusText == null) return;
            _statusText.text = text ?? string.Empty;
            _statusText.color = color ?? TextDim;
        }

        /// <summary>追加一条完整消息行。</summary>
        public void AppendLine(string speaker, string text)
        {
            if (text == null) return;

            var prefix = string.IsNullOrEmpty(speaker)
                ? string.Empty
                : $"<color=#{ColorUtility.ToHtmlStringRGB(Accent)}>{Escape(speaker)}</color>  ";

            PushLine(prefix + Escape(text));
        }

        /// <summary>追加一行系统提示（灰色）。</summary>
        public void AppendSystem(string text)
        {
            if (text == null) return;
            PushLine($"<color=#{ColorUtility.ToHtmlStringRGB(TextDim)}>{Escape(text)}</color>");
        }

        /// <summary>开始一段流式回复；后续 delta 会拼到同一行。</summary>
        public void BeginStream(string speaker)
        {
            var prefix = string.IsNullOrEmpty(speaker)
                ? string.Empty
                : $"<color=#{ColorUtility.ToHtmlStringRGB(Accent)}>{Escape(speaker)}</color>  ";
            _current.Length = 0;
            _current.Append(prefix);
            _dirty = true;
        }

        public void AppendStreamDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _current.Append(Escape(delta));
            _dirty = true;
        }

        public void EndStream()
        {
            if (_current.Length > 0) PushLine(_current.ToString());
            _current.Length = 0;
            _dirty = true;
        }

        public void ClearTranscript()
        {
            _lines.Clear();
            _committed.Length = 0;
            _current.Length = 0;
            _dirty = true;
        }

        private void PushLine(string line)
        {
            // 先把正在流式的行落盘，保证顺序正确
            if (_current.Length > 0)
            {
                _lines.Add(_current.ToString());
                _current.Length = 0;
            }
            _lines.Add(line);
            RebuildCommitted();
            _dirty = true;
        }

        private void RebuildCommitted()
        {
            // 超出行数上限时丢弃最早的行
            while (_lines.Count > _maxLines) _lines.RemoveAt(0);

            _committed.Length = 0;
            foreach (var line in _lines)
            {
                _committed.Append(line).Append('\n');
            }

            if (_committed.Length > MaxTranscriptChars)
            {
                // 极端情况：整体裁剪，保留尾部
                var overflow = _committed.Length - MaxTranscriptChars;
                _committed.Remove(0, overflow);
            }
        }

        // ---------------------------------------------------------------- 每帧

        public void Tick()
        {
            if (_root == null || !_root.activeSelf) return;

            // VFInput 的状态每帧由游戏输入系统重算，单次赋值可能被覆盖，
            // 因此窗口打开期间每帧重申一次；关闭时由 SetOpen 还原原值。
            if (IsOpen) Game.GameRefs.SetTextInputActive(true);

            if (_dirty)
            {
                _dirty = false;
                Render();
            }
        }

        private void Render()
        {
            if (_transcript == null) return;

            _renderScratch.Length = 0;
            _renderScratch.Append(_committed);
            if (_current.Length > 0) _renderScratch.Append(_current);
            _transcript.text = _renderScratch.ToString();

            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            if (_scroll == null) return;
            // 0 = 底部。即使本帧布局还没刷新，下一帧再设一次即可收敛。
            _scroll.verticalNormalizedPosition = 0f;
        }

        // ---------------------------------------------------------------- 输入

        private void OnInputEndEdit(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            // onEndEdit 在失焦时也会触发，只有回车才算提交
            var enter = UnityEngine.Input.GetKeyDown(KeyCode.Return)
                     || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter);
            if (!enter) return;

            var text = value.Trim();
            if (_input != null)
            {
                _input.text = string.Empty;
                _input.ActivateInputField();
            }

            try
            {
                Submitted?.Invoke(text);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"提交消息失败：{ex}");
            }
        }

        /// <summary>把用户输入里可能破坏富文本的字符转义掉。</summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // 只处理会破坏 <color>/<b> 标签的尖括号；中括号等无需处理。
            return s.IndexOf('<') < 0 ? s : s.Replace("<", "<\u200B");
        }
    }
}
