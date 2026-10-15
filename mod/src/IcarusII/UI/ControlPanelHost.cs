using System;
using BepInEx.Logging;
using IcarusII.I18n;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 把聊天窗嵌进游戏的「总控面板」（`UIControlPanelWindow`，原版按 `I` 打开）。
    ///
    /// 为什么能嵌：总控面板是**场景对象**而不是 prefab，它的结构由场景序列化决定，
    /// 运行时改不了它自己的数组。但它就是个普通 RectTransform 层级，
    /// **往它下面挂子物体是完全可以的**——这也正是「挂到场景对象下面」的做法。
    ///
    /// 布局（实机从编辑器读出）：
    ///   Control Panel Window  1470x842
    ///   ├── title-group   1470x50
    ///   ├── filter-group  1434x95   ← 标签行 main-type-buttons 在这里
    ///   ├── left-group     850x724  ← 物流列表
    ///   └── right-group    578x724  ← 三个检查器（站点/分拣器/标记）都占这一栏
    ///
    /// 因此：
    ///  - 对话放在 `right-group` 这一栏，与检查器**互斥显示**
    ///  - 标签行里 `tab-1..4` 位于 x = 0/122/244/366、宽 120，`x=488` 正好空着，
    ///    且 `mainTypeButtons[0]` 在原场景里是 null —— **克隆一个现有标签**放过去，
    ///    就能得到外观完全原生的第 5 个标签，且不用碰它的序列化数组。
    ///
    /// 任何一步找不到就整体降级（`IsAvailable = false`），调用方回退到独立窗口。
    /// </summary>
    internal sealed class ControlPanelHost
    {
        /// <summary>我们那个标签的 data 值。原版用 1..4，挑一个不会撞的。</summary>
        private const int AiTabData = 99;
        private const float NextTabX = 488f;

        private readonly ManualLogSource _log;
        private readonly UIControlPanelWindow _panel;
        private readonly RectTransform _panelRt;
        private readonly RectTransform _leftGroup;
        private readonly RectTransform _rightGroup;
        private readonly UIButton[] _tabs;

        private GameObject _chatGo;
        private GameObject _aiTabGo;
        private bool _aiActive;

        public bool IsAvailable { get; private set; }
        public bool AiActive => _aiActive;

        /// <summary>AI 视图与物流视图互换时触发（参数：AI 是否激活）。</summary>
        public event Action<bool> AiActiveChanged;

        private ControlPanelHost(ManualLogSource log, UIControlPanelWindow panel,
            RectTransform leftGroup, RectTransform rightGroup, UIButton[] tabs)
        {
            _log = log;
            _panel = panel;
            _panelRt = panel.GetComponent<RectTransform>();
            _leftGroup = leftGroup;
            _rightGroup = rightGroup;
            _tabs = tabs;
            IsAvailable = _panelRt != null && _rightGroup != null;
        }

        // ---------------------------------------------------------------- 创建

        public static ControlPanelHost TryCreate(UIRoot uiRoot, ManualLogSource log)
        {
            try
            {
                if (uiRoot == null || uiRoot.uiGame == null) return null;

                var panel = uiRoot.uiGame.controlPanelWindow;
                if (panel == null)
                {
                    log?.LogInfo("[总控面板] 拿不到 controlPanelWindow，界面将使用独立窗口");
                    return null;
                }

                var panelRt = panel.GetComponent<RectTransform>();
                if (panelRt == null) return null;

                var leftGroup = panelRt.Find("left-group") as RectTransform;
                var rightGroup = panelRt.Find("right-group") as RectTransform;
                if (rightGroup == null)
                {
                    log?.LogWarning("[总控面板] 找不到 right-group，界面将使用独立窗口");
                    return null;
                }

                UIButton[] tabs = null;
                try
                {
                    if (panel.filterPanel != null) tabs = panel.filterPanel.mainTypeButtons;
                }
                catch { /* 字段名变了就退化为"不加标签"，但仍可挂载 */ }

                var host = new ControlPanelHost(log, panel, leftGroup, rightGroup, tabs);
                host.BuildAiTab();
                host.WireLogisticsTabs();

                log?.LogInfo($"[总控面板] 已接入：面板 {panelRt.rect.width}x{panelRt.rect.height}" +
                             $" 右栏 {rightGroup.rect.width}x{rightGroup.rect.height}" +
                             $" 标签数={tabs?.Length ?? 0} AI标签={(host._aiTabGo != null ? "已创建" : "未创建")}");
                return host;
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[总控面板] 接入失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>克隆一个原生标签作为「AI」入口。克隆而不是新建，外观自动与原版一致。</summary>
        private void BuildAiTab()
        {
            if (_tabs == null) return;

            UIButton source = null;
            for (int i = _tabs.Length - 1; i >= 1; i--)
            {
                if (_tabs[i] != null) { source = _tabs[i]; break; }
            }
            if (source == null)
            {
                _log?.LogInfo("[总控面板] 没有可克隆的标签，跳过 AI 标签（仍可用快捷键切换）");
                return;
            }

            var srcRt = source.GetComponent<RectTransform>();
            var container = srcRt.parent;
            if (container == null) return;

            _aiTabGo = UnityEngine.Object.Instantiate(source.gameObject, container, false);
            _aiTabGo.name = "tab-ai";
            var rt = _aiTabGo.GetComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(NextTabX, srcRt.anchoredPosition.y);
            rt.sizeDelta = srcRt.sizeDelta;
            rt.localScale = Vector3.one;

            var btn = _aiTabGo.GetComponent<UIButton>();
            if (btn != null)
            {
                btn.data = AiTabData;
                // 原版是 event Action<int>，订阅即可（不要赋值，会顶掉别人的订阅）
                btn.onClick += OnTabClicked;
            }

            SetTabLabel(_aiTabGo, Strings.Get("tab.ai"));
        }

        /// <summary>把原版物流标签也接上：玩家点回物流时，我们把 AI 视图让出去。</summary>
        private void WireLogisticsTabs()
        {
            if (_tabs == null) return;
            for (int i = 1; i < _tabs.Length; i++)
            {
                if (_tabs[i] == null) continue;
                _tabs[i].onClick += OnTabClicked;
            }
        }

        private void OnTabClicked(int data)
        {
            // 原版标签 data 是 1..4；我们的标签是 AiTabData
            SetAiActive(data == AiTabData);
        }

        private static void SetTabLabel(GameObject tabGo, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var t in tabGo.GetComponentsInChildren<Text>(true))
            {
                t.text = text;
                return;   // 第一个 Text 就是标签文字
            }
        }

        // ---------------------------------------------------------------- 面板开关

        public bool PanelActive => _panel != null && _panel.active;

        /// <summary>聊天面板实例化时的父级（`InstallChat` 会再做对齐）。</summary>
        public Transform ChatParent => _panelRt;

        /// <summary>
        /// 开关总控面板。
        ///
        /// 原版的开关顺序是 `ShutAllFunctionWindow` → `ShutPlayerInventory` →
        /// `CloseEnemyBriefInfo` → `OpenControlPanelWindow`（见 `UIGame.On_I_Switch`），
        /// 这里照抄一遍，保证互斥行为与游戏一致。
        ///
        /// `preferAi` 为真时打开后切到 AI 视图——用于我们自己的快捷键，
        /// 让玩家有一个"直奔对话"的入口。
        /// </summary>
        public void TogglePanel(bool preferAi)
        {
            var uiGame = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
            if (uiGame == null) return;

            if (PanelActive)
            {
                uiGame.ShutControlPanelWindow();
                return;
            }

            uiGame.ShutAllFunctionWindow();
            uiGame.ShutPlayerInventory();
            uiGame.CloseEnemyBriefInfo();
            uiGame.OpenControlPanelWindow();

            if (preferAi) SetAiActive(true);
        }

        /// <summary>面板被游戏自己关掉时，把 AI 视图标记同步回去。</summary>
        public void SyncFromPanelState()
        {
            if (_chatGo == null) return;
            if (!PanelActive)
            {
                _aiActive = false;   // 下次打开时由 TogglePanel/标签重新决定
            }
        }

        // ---------------------------------------------------------------- 挂载聊天

        /// <summary>把聊天面板挂进总控面板，并把它对齐到右栏。</summary>
        public void InstallChat(GameObject chatGo)
        {
            if (!IsAvailable || chatGo == null) return;

            var rt = chatGo.GetComponent<RectTransform>();
            if (rt == null) return;

            rt.SetParent(_panelRt, false);
            rt.localScale = Vector3.one;
            rt.SetAsLastSibling();      // 盖在原版内容之上

            _chatGo = chatGo;

            // 未解锁物流时，左栏是空的（列表里一个对象都没有），
            // 此时把对话铺满整行，比让玩家对着一半空白好。
            SetAiActive(ShouldStartWithAi());
        }

        /// <summary>
        /// 开局默认视图：物流系统还没解锁时直接进 AI 视图，
        /// 因为那时总控面板里**只有** AI 这一样东西是有内容的。
        /// </summary>
        private static bool ShouldStartWithAi()
        {
            try
            {
                var h = GameMain.history;
                if (h == null) return true;
                return !(h.logisticsSystemUnlocked || h.markerUnlocked);
            }
            catch { return true; }
        }

        // ---------------------------------------------------------------- 视图切换

        /// <summary>
        /// 切换 AI 视图。
        ///
        /// **刻意不修改任何游戏自身的可见性状态**：不去 SetActive 那几个检查器，
        /// 也不去动左栏。我们只是把一块不透明的面板盖在右侧（未解锁物流时盖住整行），
        /// 切回物流时把它自己隐藏起来——底下的东西从未被动过，
        /// 也就不会和游戏自己的 `DetermineSelectionInspector()` / `currentInspector`
        /// 状态机打架。
        /// </summary>
        public void SetAiActive(bool active)
        {
            if (!IsAvailable) return;
            _aiActive = active;

            if (_chatGo != null)
            {
                ApplyChatRect(active && !LogisticsUnlocked());
                _chatGo.SetActive(active);
            }

            try { AiActiveChanged?.Invoke(active); }
            catch (Exception ex) { _log?.LogWarning($"[总控面板] 切换回调异常：{ex.Message}"); }
        }

        private static bool LogisticsUnlocked()
        {
            try
            {
                var h = GameMain.history;
                return h != null && (h.logisticsSystemUnlocked || h.markerUnlocked);
            }
            catch { return false; }
        }

        /// <summary>
        /// 对齐对话区。
        ///
        /// **必须用拉伸锚点**（`0,0 .. 1,1`）来表达"距父级四边的边距"。
        /// 先前这里直接抄 `<c>right-group</c>` 的锚点与 offsetMin/offsetMax，
        /// 并在全宽时把 `left-group.offsetMin.x` 混进来——那是错的：
        /// `offsetMin` 是**相对各自锚点**的，而 right-group 锚在右边界、
        /// left-group 锚在左边界，两者不在同一坐标系里。
        /// 后果是 sizeDelta.x 变成 -36（面板宽度为负，完全不可见）。
        ///
        /// 现在改成：先把两栏的边缘统一换算成"距父级边界的边距"，再用拉伸锚点写回。
        /// 这样两种情形（只占右栏 / 铺满整行）都只差一个左边距。
        /// </summary>
        private void ApplyChatRect(bool fullWidth)
        {
            if (_chatGo == null || _rightGroup == null || _panelRt == null) return;
            var rt = _chatGo.GetComponent<RectTransform>();
            if (rt == null) return;

            // right-group 锚在右上：它的 offset 直接就是距右/上/下边界的边距
            float marginRight = -_rightGroup.offsetMax.x;
            float marginTop = -_rightGroup.offsetMax.y;
            float marginBottom = _rightGroup.offsetMin.y;

            float marginLeft;
            if (fullWidth && _leftGroup != null)
            {
                // left-group 锚在左边界，offsetMin.x 就是它的左边距
                marginLeft = _leftGroup.offsetMin.x;
            }
            else
            {
                // 只占右栏：左边距 = 父级宽 - 右栏宽 - 右边距
                marginLeft = _panelRt.rect.width - _rightGroup.rect.width - marginRight;
            }

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(marginLeft, marginBottom);
            rt.offsetMax = new Vector2(-marginRight, -marginTop);

            _log?.LogInfo($"[总控面板] 对话区：{(fullWidth ? "铺满整行" : "仅右栏")} " +
                          $"左边距={marginLeft:0.#} 右边距={marginRight:0.#} " +
                          $"上={marginTop:0.#} 下={marginBottom:0.#} " +
                          $"⇒ {rt.rect.width:0.#}x{rt.rect.height:0.#}");
        }

        // ---------------------------------------------------------------- 每帧

        /// <summary>
        /// 面板被游戏关掉时（`ShutAllFunctionWindow` → `SetActive(false)`），
        /// 我们的对话作为子物体自动跟着隐藏，**对话内容不会丢**——
        /// 所以这里目前没有必须每帧做的事。保留以便将来接入标签选中态。
        /// </summary>
        public void Tick()
        {
            // 目前无需每帧同步：视图切换完全由标签点击与 InstallChat 驱动。
            // 保留此方法以便将来接入选中态高亮。
        }
    }
}
