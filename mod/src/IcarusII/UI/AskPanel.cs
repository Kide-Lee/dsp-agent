using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using IcarusII.I18n;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// `choose/voting-device` 的作答面板：逐题展示、返回上一题、跳过、单选/多选。
    ///
    /// 静态节点全部来自 prefab；本组件只做绑定、显隐、填字和状态切换。
    /// 选项 hover/选中/勾选全部由 prefab 上的原生 Toggle + UIToggle 控制；
    /// mod 只设置 isOn 这个逻辑状态，不直接改 Sprite/颜色。
    /// 多选时每个选项数字前显示复选框，并把 Toggle 从 ToggleGroup 里摘出来。
    /// </summary>
    internal sealed class AskPanel : MonoBehaviour
    {
        private const int Unset = -2;
        private const int Custom = -1;

        private sealed class OptionRow
        {
            public GameObject Go;
            public Text Number;
            public Text Label;
            public Toggle Toggle;
            public Image Checkbox;
        }

        private sealed class QuestionState
        {
            public readonly HashSet<int> Selected = new HashSet<int>();
            public string Custom = string.Empty;
            public bool CustomSelected;
            public bool Skipped;
        }

        private readonly List<OptionRow> _rows = new List<OptionRow>(AskArgs.MaxOptions);
        private readonly List<QuestionState> _states = new List<QuestionState>();
        private readonly HashSet<int> _selectedOptions = new HashSet<int>();
        private List<AskQuestion> _questions = new List<AskQuestion>();

        private Text _questionText;
        private Text _numberText;
        private RectTransform _optionRoot;
        private RectTransform _customRow;
        private Text _customNumber;
        private InputField _customInput;
        private Image _customCheckbox;
        private Toggle _customToggle;
        private Text _submitLabel;
        private Button _submitButton;
        private Button _skipButton;
        private Button _backButton;
        private Button _rejectButton;
        private Text _rejectLabel;
        private GameObject _explainRoot;
        private Text _explainText;
        private ScrollRect _explainScroll;
        private ToggleGroup _toggleGroup;



        private bool _bound;
        private bool _showing;
        private bool _suppressToggle;
        private bool _suppressCustom;
        private bool _suppressCustomToggle;
        private bool _imeWasComposing;
        private InputField.OnValidateInput _previousCustomValidator;
        private bool _multiSelect;
        private bool _customSelected;
        private bool _reviewMode;
        private bool _confirmMode;
        private int _index;

        public event Action<List<AskAnswer>> Completed;
        public event Action Cancelled;

        /// <summary>权限确认 UI 的选择结果；当前只做界面钩子，尚未接游戏权限逻辑。</summary>
        public event Action<bool> ConfirmationChosen;

        public bool IsConfirming => _confirmMode;

        public bool IsShowing => _showing;
        public int CurrentIndex => _index;
        public int QuestionCount => _questions.Count;

        public bool Bind(Transform chooseRoot)
        {
            if (_bound) return true;
            if (chooseRoot == null) return false;

            _questionText = FindText(chooseRoot, "voting-device/question/text");
            _numberText = FindText(chooseRoot, "voting-device/question/count");
            _optionRoot = chooseRoot.Find("voting-device/answer-grop") as RectTransform;
            if (_questionText == null || _optionRoot == null)
            {
                Plugin.Log.LogWarning("[ask] choose 缺少 question/text 或 answer-grop");
                return false;
            }

            _toggleGroup = _optionRoot.GetComponent<ToggleGroup>();
            if (_toggleGroup != null) _toggleGroup.allowSwitchOff = true;

            _rows.Clear();
            for (int i = 1; i <= AskArgs.MaxOptions; i++)
            {
                var rowTf = _optionRoot.Find("answer-option-" + i) as RectTransform;
                if (rowTf == null)
                {
                    Plugin.Log.LogWarning("[ask] 缺少 answer-option-" + i);
                    return false;
                }

                var hit = rowTf.Find("hit");
                var checkbox = rowTf.Find("checkbox");
                var row = new OptionRow
                {
                    Go = rowTf.gameObject,
                    Number = FindText(rowTf, "number"),
                    Label = FindText(rowTf, "label"),
                    Toggle = hit != null ? hit.GetComponent<Toggle>() : null,
                    Checkbox = checkbox != null ? checkbox.GetComponent<Image>() : null,
                };
                if (row.Toggle == null)
                {
                    Plugin.Log.LogWarning("[ask] answer-option-" + i + " 缺少 Toggle");
                    return false;
                }
                int index = i - 1;
                row.Toggle.onValueChanged.AddListener(on => OnOptionChanged(index, on));
                _rows.Add(row);
            }

            _customRow = _optionRoot.Find("answer-input") as RectTransform;
            if (_customRow == null)
            {
                Plugin.Log.LogWarning("[ask] 缺少 answer-input");
                return false;
            }
            _customNumber = FindText(_customRow, "number");
            var inputTf = _customRow.Find("input");
            _customInput = inputTf != null ? inputTf.GetComponent<InputField>() : null;
            var customHit = _customRow.Find("hit");
            var customCheckbox = _customRow.Find("checkbox");
            _customCheckbox = customCheckbox != null ? customCheckbox.GetComponent<Image>() : null;
            _customToggle = customHit != null ? customHit.GetComponent<Toggle>() : null;
            if (_customToggle == null || customHit.GetComponent<UIToggle>() == null
                || _customRow.Find("selected-bg") == null)
            {
                Plugin.Log.LogWarning("[ask] answer-input 缺少 hit/Toggle/UIToggle/selected-bg");
                return false;
            }
            if (_customInput == null || _customInput.lineType != InputField.LineType.MultiLineNewline)
            {
                Plugin.Log.LogWarning("[ask] answer-input/input 必须是 MultiLineNewline 的 InputField");
                return false;
            }
            _customToggle.onValueChanged.AddListener(OnCustomToggleChanged);
            _customInput.onValueChanged.AddListener(OnCustomChanged);
            _previousCustomValidator = _customInput.onValidateInput;
            _customInput.onValidateInput = ValidateCustomChar;

            _submitButton = FindButton(chooseRoot, "voting-device/btn-grop/agree-button");
            _skipButton = FindButton(chooseRoot, "voting-device/btn-grop/skip-button");
            _backButton = FindButton(chooseRoot, "voting-device/btn-grop/return-button");
            _rejectButton = FindButton(chooseRoot, "voting-device/btn-grop/reject-button");
            _submitLabel = FindText(chooseRoot, "voting-device/btn-grop/agree-button/text");
            _rejectLabel = FindText(chooseRoot, "voting-device/btn-grop/reject-button/text");
            _explainRoot = chooseRoot.Find("voting-device/explain") != null
                ? chooseRoot.Find("voting-device/explain").gameObject
                : null;
            var explainScroll = chooseRoot.Find("voting-device/explain/scroll-view");
            if (explainScroll != null)
            {
                _explainScroll = explainScroll.GetComponent<ScrollRect>();
                var explainContent = explainScroll.Find("viewport/content");
                _explainText = explainContent != null ? explainContent.GetComponent<Text>() : null;
            }
            if (_submitButton != null) _submitButton.onClick.AddListener(SubmitCurrent);
            if (_skipButton != null) _skipButton.onClick.AddListener(SkipCurrent);
            if (_backButton != null) _backButton.onClick.AddListener(Back);
            if (_rejectButton != null) _rejectButton.onClick.AddListener(RejectCurrent);

            _bound = true;
            Hide();
            return true;
        }

        public void Show(List<AskQuestion> questions)
        {
            if (!_bound)
            {
                Plugin.Log.LogWarning("[ask] AskPanel 尚未绑定");
                return;
            }
            if (questions == null || questions.Count == 0) return;

            _questions = questions;
            _states.Clear();
            for (int i = 0; i < _questions.Count; i++) _states.Add(new QuestionState());
            _index = 0;
            _reviewMode = false;
            _confirmMode = false;
            _showing = true;
            gameObject.SetActive(true);
            if (_customInput != null)
                ChatWindow.RebindInputTextMaterial(_customInput.textComponent, "作答面板");
            ApplyQuestion(0, restoreFocus: false);
        }

        public void Hide()
        {
            if (_customInput != null) _customInput.DeactivateInputField();
            _showing = false;
            _reviewMode = false;
            _confirmMode = false;
            _imeWasComposing = false;
            _selectedOptions.Clear();
            _customSelected = false;
            UpdateCustomCheckbox();
            if (_explainRoot != null) _explainRoot.SetActive(false);
            _optionRoot.gameObject.SetActive(true);
            if (_skipButton != null) _skipButton.gameObject.SetActive(true);
            if (_rejectButton != null) _rejectButton.gameObject.SetActive(false);
            gameObject.SetActive(false);
        }

        public void Cancel()
        {
            if (!_showing) return;
            if (_confirmMode)
            {
                _confirmMode = false;
                Hide();
                ConfirmationChosen?.Invoke(false);
                return;
            }
            Hide();
            Cancelled?.Invoke();
        }

        public void SubmitCurrent()
        {
            if (!_showing) return;
            if (_confirmMode)
            {
                Confirm(true);
                return;
            }
            if (_reviewMode)
            {
                Complete();
                return;
            }
            var state = _states[_index];
            var customText = _customInput != null ? (_customInput.text ?? string.Empty).Trim() : string.Empty;

            if (_selectedOptions.Count == 0 && !_customSelected)
            {
                if (_questions[_index].AllowCustom) FocusCustom();
                return;
            }
            if (_customSelected && customText.Length == 0)
            {
                if (_selectedOptions.Count == 0)
                {
                    FocusCustom();
                    return;
                }
                _customSelected = false;
                UpdateCustomCheckbox();
            }

            SaveCurrent();
            if (_index < _questions.Count - 1)
            {
                _index++;
                ApplyQuestion(_index, restoreFocus: true);
            }
            else
            {
                ShowReview();
            }
        }

        public void SkipCurrent()
        {
            if (!_showing) return;
            if (_confirmMode)
            {
                Confirm(false);
                return;
            }
            if (_reviewMode)
            {
                Back();
                return;
            }
            var state = _states[_index];
            state.Skipped = true;
            state.Selected.Clear();
            state.Custom = string.Empty;
            state.CustomSelected = false;

            if (_index < _questions.Count - 1)
            {
                _index++;
                ApplyQuestion(_index, restoreFocus: true);
            }
            else
            {
                ShowReview();
            }
        }

        public void Back()
        {
            if (!_showing || _confirmMode) return;
            if (_reviewMode)
            {
                _reviewMode = false;
                _index = _questions.Count - 1;
                ApplyQuestion(_index, restoreFocus: true);
                return;
            }
            if (_index <= 0) return;
            SaveCurrent();
            _index--;
            ApplyQuestion(_index, restoreFocus: true);
        }

        private void ApplyQuestion(int index, bool restoreFocus)
        {
            var question = _questions[index];
            var state = _states[index];
            _index = index;
            _reviewMode = false;
            _confirmMode = false;
            _multiSelect = question.MultiSelect;
            if (_explainRoot != null) _explainRoot.SetActive(false);
            _optionRoot.gameObject.SetActive(true);
            if (_skipButton != null) _skipButton.gameObject.SetActive(true);
            if (_rejectButton != null) _rejectButton.gameObject.SetActive(false);
            _questionText.text = question.Prompt ?? string.Empty;
            if (_numberText != null)
                _numberText.text = (index + 1) + " / " + _questions.Count;
            // 最后一题后面还有总结轮，所以所有问题页都显示「下一个」；
            // 只有 ShowReview 的最终页显示「提交」。
            if (_submitLabel != null) _submitLabel.text = Strings.Get("ask.next");

            int count = Mathf.Min(question.Options.Count, AskArgs.MaxOptions);
            ConfigureMultiSelectVisuals(count);
            ClearToggles();

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool visible = i < count;
                row.Go.SetActive(visible);
                if (!visible) continue;
                if (row.Number != null) row.Number.text = (i + 1).ToString();
                if (row.Label != null) row.Label.text = question.Options[i].Label ?? string.Empty;
            }

            bool showCustom = question.AllowCustom;
            _customRow.gameObject.SetActive(showCustom);
            if (_customNumber != null)
                _customNumber.text = (showCustom ? count + 1 : 0).ToString();

            if (_customInput != null)
            {
                // OnDisable（隐藏选项区/关窗）会把逐字符颜色清成 0，
                // 0 指向调色板 0 号色；不重种的话下一次 Append 会越界抛异常。
                ChatWindow.SeedInputCharColors(_customInput, "作答", 512);
                SetCustomText(state.Custom);
                _customInput.DeactivateInputField();
            }
            _imeWasComposing = false;
            _customSelected = state.CustomSelected && showCustom;
            UpdateCustomCheckbox();

            foreach (var option in state.Selected)
            {
                if (option < 0 || option >= count) continue;
                _selectedOptions.Add(option);
                SetToggle(option, true);
            }

            if (_backButton != null) _backButton.gameObject.SetActive(index > 0);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_optionRoot);
            Canvas.ForceUpdateCanvases();
            if (restoreFocus && _customSelected) FocusCustom();
        }

        private void ConfigureMultiSelectVisuals(int optionCount)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Checkbox != null) row.Checkbox.gameObject.SetActive(_multiSelect);
                if (row.Toggle != null) row.Toggle.group = _multiSelect ? null : _toggleGroup;

            }

            if (_customCheckbox != null) _customCheckbox.gameObject.SetActive(_multiSelect);
            if (_customToggle != null) _customToggle.group = _multiSelect ? null : _toggleGroup;
        }

        /// <summary>
        /// 所有问题处理完后进入最后一轮：explain 汇总全部问答，隐藏跳过，提交才真正完成。
        /// </summary>
        private void ShowReview()
        {
            if (_explainRoot == null || _explainText == null)
            {
                Complete();
                return;
            }
            _reviewMode = true;
            _confirmMode = false;
            _optionRoot.gameObject.SetActive(false);
            _explainRoot.SetActive(true);
            _questionText.text = Strings.Get("ask.review.title");
            if (_numberText != null) _numberText.text = Strings.Get("confirm.step");
            if (_submitLabel != null) _submitLabel.text = Strings.Get("ask.submit");
            if (_submitButton != null) _submitButton.gameObject.SetActive(true);
            if (_skipButton != null) _skipButton.gameObject.SetActive(false);
            if (_rejectButton != null) _rejectButton.gameObject.SetActive(false);
            if (_backButton != null) _backButton.gameObject.SetActive(_questions.Count > 1);
            _explainText.text = BuildReviewText();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_explainText.rectTransform);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_explainRoot.transform);
            Canvas.ForceUpdateCanvases();
            if (_explainScroll != null) _explainScroll.verticalNormalizedPosition = 1f;
        }

        /// <summary>
        /// 写操作确认 UI：question 放确认标题，explain 放详情，右下角红拒绝/蓝同意。
        /// Plugin 负责把它接到 confirm_request / confirm_result 与一次性令牌。
        /// </summary>
        public void ShowConfirmation(string title, string details)
        {
            if (!_bound) return;
            _reviewMode = false;
            _confirmMode = true;
            _showing = true;
            gameObject.SetActive(true);
            _optionRoot.gameObject.SetActive(false);
            if (_explainRoot != null) _explainRoot.SetActive(true);
            _questionText.text = title ?? string.Empty;
            if (_numberText != null) _numberText.text = Strings.Get("confirm.step");
            if (_explainText != null) _explainText.text = details ?? string.Empty;
            if (_submitLabel != null) _submitLabel.text = Strings.Get("confirm.agree");
            if (_submitButton != null) _submitButton.gameObject.SetActive(true);
            if (_rejectLabel != null) _rejectLabel.text = Strings.Get("confirm.reject");
            if (_backButton != null) _backButton.gameObject.SetActive(false);
            if (_rejectButton != null) _rejectButton.gameObject.SetActive(true);
            if (_skipButton != null) _skipButton.gameObject.SetActive(false);
            if (_explainText != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_explainText.rectTransform);
            if (_explainRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_explainRoot.transform);
            Canvas.ForceUpdateCanvases();
            if (_explainScroll != null) _explainScroll.verticalNormalizedPosition = 1f;
        }

        private string BuildReviewText()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _questions.Count; i++)
            {
                var q = _questions[i];
                var state = _states[i];
                if (i > 0) sb.Append('\n');
                sb.Append(i + 1).Append(". ").Append(q.Prompt ?? string.Empty).Append("  →  ");
                var answers = new List<string>();
                var optionIndexes = new List<int>(state.Selected);
                optionIndexes.Sort();
                foreach (var option in optionIndexes)
                {
                    if (option >= 0 && option < q.Options.Count) answers.Add(q.Options[option].Label);
                }
                if (state.CustomSelected && !string.IsNullOrEmpty(state.Custom)) answers.Add(state.Custom);
                if (state.Skipped || answers.Count == 0) sb.Append(Strings.Get("ask.review.skip"));
                else sb.Append(string.Join("、", answers));
            }
            return sb.ToString();
        }

        private void RejectCurrent()
        {
            if (_confirmMode) Confirm(false);
        }

        private void Confirm(bool accepted)
        {
            if (!_confirmMode) return;
            _confirmMode = false;
            Hide();
            ConfirmationChosen?.Invoke(accepted);
        }

        private void SaveCurrent()
        {
            if (_index < 0 || _index >= _states.Count) return;
            var state = _states[_index];
            state.Selected.Clear();
            foreach (var option in _selectedOptions) state.Selected.Add(option);
            state.Custom = _customInput != null ? (_customInput.text ?? string.Empty).Trim() : string.Empty;
            state.CustomSelected = _customSelected;
            if (state.Selected.Count > 0 || state.CustomSelected) state.Skipped = false;
        }

        private void Complete()
        {
            var answers = new List<AskAnswer>(_questions.Count);
            for (int i = 0; i < _questions.Count; i++)
            {
                var question = _questions[i];
                var state = _states[i];
                var answer = new AskAnswer
                {
                    Index = i,
                    Prompt = question.Prompt,
                    Skipped = state.Skipped,
                };

                var indexes = new List<int>(state.Selected);
                indexes.Sort();
                foreach (var optionIndex in indexes)
                {
                    if (optionIndex < 0 || optionIndex >= question.Options.Count) continue;
                    var label = question.Options[optionIndex].Label;
                    answer.Answers.Add(label);
                    answer.OptionIndexes.Add(optionIndex);
                }
                if (state.CustomSelected && !string.IsNullOrEmpty(state.Custom))
                {
                    answer.Answers.Add(state.Custom);
                    answer.OptionIndexes.Add(-1);
                    answer.Custom = true;
                }

                answer.Skipped = answer.Skipped || answer.Answers.Count == 0;
                if (!answer.Skipped)
                {
                    answer.Answer = string.Join("、", answer.Answers);
                    answer.OptionIndex = answer.Answers.Count == 1 ? answer.OptionIndexes[0] : -1;
                }
                answers.Add(answer);
            }

            Hide();
            Completed?.Invoke(answers);
        }

        private void OnOptionChanged(int index, bool isOn)
        {
            if (_suppressToggle || !_showing) return;
            if (_multiSelect)
            {
                if (isOn) _selectedOptions.Add(index);
                else _selectedOptions.Remove(index);
                return;
            }

            if (isOn)
            {
                _selectedOptions.Clear();
                _selectedOptions.Add(index);
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (i == index) continue;
                    _rows[i].Toggle.isOn = false;
                }
                if (_customInput != null)
                {
                    SetCustomText(string.Empty);
                    _customInput.DeactivateInputField();
                }
                _customSelected = false;
                UpdateCustomCheckbox();
            }
            else
            {
                _selectedOptions.Remove(index);
            }
        }

        private void OnCustomChanged(string value)
        {
            if (!_showing || _suppressCustom || _confirmMode || _reviewMode) return;
            _customSelected = !string.IsNullOrEmpty(value);
            UpdateCustomCheckbox();
            if (!_customSelected) return;
            if (!_multiSelect) ClearToggles();
        }

        /// <summary>
        /// 自定义行整行可点：hit 上的 Toggle 负责选中态，选中后把焦点交给输入框。
        /// 程序化同步 isOn 时用 _suppressCustomToggle 挡住，避免打字过程中反复 ActivateInputField。
        /// </summary>
        private void OnCustomToggleChanged(bool isOn)
        {
            if (_suppressCustomToggle || !_showing || _confirmMode || _reviewMode) return;
            _customSelected = isOn;
            if (!isOn) return;
            if (!_multiSelect) ClearToggles();
            FocusCustom();
        }

        private void Update()
        {
            if (!_showing) return;

            if (_customInput != null && _customInput.isFocused)
            {
                if (!_customSelected)
                {
                    _customSelected = true;
                    UpdateCustomCheckbox();
                }
                if (!_multiSelect && _selectedOptions.Count > 0) ClearToggles();

                // 输入法组字中的回车是“选候选词”，不能提交。
                // 组字在 EventSystem 里提交的顺序不固定，所以上一帧还在组字时也跳过。
                bool composing = !string.IsNullOrEmpty(_customInput.compositionString);
                if (EnterDown() && !composing && !_imeWasComposing)
                {
                    SubmitCurrent();
                    return;
                }
                _imeWasComposing = composing;
                return;
            }

            _imeWasComposing = false;
            if (EnterDown())
            {
                SubmitCurrent();
                return;
            }

            // 确认页没有普通问题；汇总页也不该响应数字选择。
            // 首次确认时 _questions 为空，直接索引会抛 ArgumentOutOfRangeException。
            if (_confirmMode || _reviewMode || _questions == null || _questions.Count == 0)
                return;
            if (_index < 0 || _index >= _questions.Count)
                return;

            int count = _questions[_index].Options.Count;
            for (int i = 1; i <= AskArgs.MaxOptions; i++)
            {
                if (!DigitDown(i)) continue;
                if (i <= count) SelectOption(i - 1);
                else if (i == count + 1 && _questions[_index].AllowCustom) FocusCustom();
                break;
            }
        }

        private void SelectOption(int index)
        {
            if (index < 0 || index >= _questions[_index].Options.Count) return;
            if (_multiSelect)
            {
                var row = _rows[index];
                row.Toggle.isOn = !row.Toggle.isOn;
            }
            else
            {
                ClearToggles();
                SetToggle(index, true);
                _selectedOptions.Add(index);
                SetCustomText(string.Empty);
                _customSelected = false;
                UpdateCustomCheckbox();
            }
            if (_customInput != null) _customInput.DeactivateInputField();
        }

        private void SetCustomText(string value)
        {
            if (_customInput == null) return;
            _suppressCustom = true;
            try
            {
                _customInput.text = value ?? string.Empty;
                var pos = _customInput.text != null ? _customInput.text.Length : 0;
                _customInput.caretPosition = pos;
                _customInput.selectionAnchorPosition = pos;
                _customInput.selectionFocusPosition = pos;
                try { _customInput.CalculateColorText(true); } catch { /* 旧版输入框没有此方法 */ }
                _customInput.ForceLabelUpdate();
            }
            finally
            {
                _suppressCustom = false;
            }
        }

        private void FocusCustom()
        {
            if (_customInput == null) return;
            if (!_multiSelect) ClearToggles();
            _customSelected = true;
            _imeWasComposing = false;
            UpdateCustomCheckbox();
            var es = EventSystem.current;
            if (es != null) es.SetSelectedGameObject(_customInput.gameObject);
            _customInput.ActivateInputField();
            _customInput.Select();
        }

        private void ClearToggles()
        {
            _suppressToggle = true;
            try
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    _rows[i].Toggle.isOn = false;
                }
                _selectedOptions.Clear();
            }
            finally
            {
                _suppressToggle = false;
            }
        }

        private void SetToggle(int index, bool on)
        {
            if (index < 0 || index >= _rows.Count) return;
            _suppressToggle = true;
            try
            {
                _rows[index].Toggle.isOn = on;
            }
            finally
            {
                _suppressToggle = false;
            }
        }

        private void UpdateCustomCheckbox()
        {
            // 显隐/勾选动画由 hit 上的原生 Toggle + UIToggle 负责；
            // mod 只同步 isOn 这个状态。
            if (_customToggle == null || _customToggle.isOn == _customSelected) return;
            _suppressCustomToggle = true;
            try
            {
                _customToggle.isOn = _customSelected;
            }
            finally
            {
                _suppressCustomToggle = false;
            }
        }

        /// <summary>
        /// 自定义答案按单行显示，但输入框必须用 MultiLineNewline：
        /// 单行 InputField 会把回车当成 Finishing 并失焦，输入法还在组字时选词就会被中断。
        /// 换行字符在这里拦掉，视觉上仍是单行。
        /// </summary>
        private char ValidateCustomChar(string current, int charIndex, char added)
        {
            if (_previousCustomValidator is not null)
            {
                var chained = _previousCustomValidator(current, charIndex, added);
                if (chained == '\0' || chained != added) return chained;
            }
            if (added == '\n' || added == '\r' || added == '\t') return '\0';
            return added;
        }

        private static bool EnterDown()
        {
            return UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter);
        }

        private static bool DigitDown(int digit)
        {
            if (UnityEngine.Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha0 + digit))) return true;
            if (UnityEngine.Input.GetKeyDown((KeyCode)((int)KeyCode.Keypad0 + digit))) return true;
            return false;
        }

        private static Text FindText(Transform root, string path)
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<Text>() : null;
        }

        private static Button FindButton(Transform root, string path)
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<Button>() : null;
        }
    }
}
