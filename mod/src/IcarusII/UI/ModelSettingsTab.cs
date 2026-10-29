using System;
using System.Collections.Generic;
using System.Reflection;
using IcarusII.I18n;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;
using IcarusII.Common.Transport;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 往**游戏原生的设置窗口**（`UIOptionWindow`）里注入一个"模型"页签。
    ///
    /// M3 起这一页按 DSH Web UI 的模型页组织：
    ///   * 左栏是**供应商**列表（名称 + 密钥状态点），一个供应商一行；
    ///   * 右栏是当前供应商的编辑卡片：API 密钥、API 地址、模型列表（可增删）、
    ///     点模型行把它设为当前模型；
    ///   * 修改先留在草稿里，点窗口右下角原生的「应用设置」才提交给 sidecar。
    ///
    /// 注入机制本身没变：`UIOptionWindow` 的页签按钮/文字/内容根都是数组，
    /// 运行时扩一位、克隆一套按钮和内容根即可，不动游戏 prefab。
    /// </summary>
    internal static class ModelSettingsTab
    {
        private const float TabWidth = 160f;

        // ---- 原生设置页的排版基准（从 DSPGame.unity 的 Option Window 量的）----
        private const float PageLeft = 30f;
        private const float PageTop = 12f;

        private const string DefaultProvider = "deepseek";
        private const string DefaultModel = "deepseek-v4-flash";

        // 预设下拉框的第一项永远是「自定义…」：目录没拉到、目录里没有玩家要的供应商，
        // 都还有一条明确的出口（手填 ID / API 地址）。它的 ItemsData 用 -1 区分目录项。
        private const int CustomPresetData = -1;

        private static readonly List<ProviderCatalogEntry> _presetProviders = new List<ProviderCatalogEntry>();

        // 原生文字/控件取值：label 18px、#FFFFFFC8（200/255）；
        // 输入框底 #0000006E（110/255）；ComboBox 文本 16px。
        private static readonly Color LabelColor = new Color(1f, 1f, 1f, 200f / 255f);
        private static readonly Color DimColor = new Color(1f, 1f, 1f, 0.58f);
        private static readonly Color ControlBgColor = new Color(0f, 0f, 0f, 110f / 255f);
        private static readonly Color PlaceholderColor = new Color(1f, 1f, 1f, 0.5f);
        private static readonly Color HintColor = new Color(1f, 1f, 1f, 0.686f);
        private static readonly Color HintWarnColor = new Color(0.991f, 0.59f, 0.369f, 0.686f);
        private static readonly Color HintOkColor = new Color(0.45f, 0.90f, 0.55f, 0.686f);
        private static readonly Color CardBgColor = new Color(0f, 0f, 0f, 0.16f);
        private static readonly Color RowBgColor = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color RowSelectedColor = new Color(0.35f, 0.75f, 1f, 0.16f);
        private static readonly Color AccentColor = new Color(0.35f, 0.75f, 1f, 1f);
        private static readonly Color KeyOkColor = new Color(0.45f, 0.90f, 0.55f, 1f);
        private static readonly Color KeyMissingColor = new Color(1f, 0.59f, 0.37f, 1f);

        private static bool _installed;
        private static bool _applyHooked;
        private static UIOptionWindow _applyHookedWindow;
        private static int _index = -1;
        private static SidecarClient _client;
        private static bool _refreshPending;

        private static Font _font;

        // ---- 草稿（点「应用设置」前不落盘）----
        private sealed class ModelDraft
        {
            public string Id;
            public string Name;
        }

        private sealed class ProviderDraft
        {
            public string Id;
            public string Name;
            public bool HasKey;
            public string ApiKey = string.Empty;
            public string BaseUrl = string.Empty;
            public readonly List<ModelDraft> Models = new List<ModelDraft>();
            public bool IsActive;
        }

        private static readonly List<ProviderDraft> _providers = new List<ProviderDraft>();
        private static readonly HashSet<string> _removed = new HashSet<string>(StringComparer.Ordinal);
        private static string _selectedProvider = string.Empty;
        private static string _activeProvider = string.Empty;
        private static string _activeModel = string.Empty;
        private static bool _dirty;
        private static bool _suppressFieldEvents;

        // ---- 控件引用 ----
        private static RectTransform _providerList;
        private static RectTransform _modelList;
        private static Text _editorTitle;
        private static Image _keyDot;
        private static Text _modelCount;
        private static Text _editorHint;
        private static InputField _apiKeyField;
        private static InputField _baseUrlField;
        private static InputField _addProviderField;
        private static InputField _addModelField;
        private static Text _hint;
        private static UIComboBox _presetCombo;
        private static bool _suppressPresetEvent;
        private static bool _providerCatalogLoaded;
        private static volatile ProviderCatalogResult _providerCatalogResult;
        private static volatile bool _providerCatalogPending;
        private static string _providerCatalogRequestId;

        // ---- Key 检测 ----
        private static Button _testKeyButton;
        private static Text _testKeyButtonLabel;
        private static Text _keyTestStatus;
        private static volatile bool _testKeyPending;
        private static volatile string _testKeyRequestId;
        private static volatile ProviderTestResult _testKeyResult;
        private static string _testKeyProviderId = string.Empty;
        private static float _testKeyRequestedAt;

        // ---- 「获取可用模型」弹层 ----
        private static RectTransform _catalogPopup;
        private static RectTransform _catalogList;
        private static Text _catalogTitle;
        private static Text _catalogHint;
        private static Button _catalogButton;
        private static Text _catalogButtonLabel;
        private static readonly List<ModelInfo> _catalogModels = new List<ModelInfo>();
        private static string _catalogProviderId = string.Empty;
        private static volatile string _catalogRequestId;
        private static volatile ModelCatalogResult _catalogResult;
        private static volatile bool _catalogPending;
        private static float _catalogRequestedAt;

        /// <summary>由 Plugin 在 sidecar 客户端就绪后调用。</summary>
        public static void Bind(SidecarClient client)
        {
            _client = client;
            if (_client == null) return;

            // config.state 是在客户端接收线程上回调的；这里只置脏标记，
            // 真正的刷新留给主线程的 Watch()，避免跨线程碰 UnityEngine 对象。
            _client.ConfigChanged += _ => _refreshPending = true;
            // model.catalog / provider.catalog 的响应也在接收线程上；
            // 只解析并存结果，Watch() 主线程再落 UI。
            _client.Received -= OnClientReceived;
            _client.Received += OnClientReceived;
            _refreshPending = true;
        }

        private static int _watchTick;

        /// <summary>
        /// 每帧轮询：只要**设置窗口是打开状态**而我们的页签不在，就补上。
        ///
        /// 这样不依赖"从我们自己的按钮进入"——玩家从 Esc 菜单、主菜单设置进去也能看到"模型"页。
        /// 窗口被销毁重建后也会自动自愈。
        /// </summary>
        public static void Watch()
        {
            if (++_watchTick % 30 != 0) return;   // 每 30 帧检查一次，几乎零开销

            var uiRoot = Game.GameRefs.UiRoot;
            var window = uiRoot != null ? uiRoot.optionWindow : null;
            if (window == null || !window.gameObject.activeInHierarchy) return;

            ConsumeProviderCatalog();
            EnsureProviderCatalog();
            ConsumeCatalogResult();
            ConsumeTestKeyResult();

            if (IsInjectionAlive(window))
            {
                if (_refreshPending)
                {
                    // config.state 由接收线程标记，这里在主线程落回控件
                    _refreshPending = false;
                    RefreshFromConfig();
                }
                return;
            }
            Install(window);
        }

        /// <summary>打开设置窗口并切到"模型"页签。给聊天窗的 setting-wnd-btn 用。</summary>
        public static void Open(UIOptionWindow window)
        {
            if (window == null)
            {
                Plugin.Log.LogWarning("[模型页] 拿不到 UIOptionWindow，无法打开设置。");
                return;
            }

            try
            {
                Install(window);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[模型页] 打开时注入失败：" + ex.Message);
                return;
            }
            if (_index >= 0) SwitchTo(window, _index);
        }

        private static void Install(UIOptionWindow window)
        {
            // 设置窗口在加载过程中可能只创建了一半：数组还是 null。
            // 这时直接跳过，等 Watch 下一次（或玩家重新打开）再注入。
            if (window == null || window.tabButtons == null || window.tabButtons.Length == 0)
                return;

            // 幂等检查必须校验对象**还活着**：窗口重建后数组会回到原始长度，
            // 而 tabIndex 仍指向我们那一格 → SetTabIndex 每帧越界抛 NRE。
            if (_installed && IsInjectionAlive(window))
            {
                return;
            }
            _installed = false;
            _index = -1;

            // 备份原始数组：注入中途失败必须回滚，
            // 否则会留下"数组多一位但对象是 null"的状态 → 每帧 NRE（踩过）。
            var backupButtons = window.tabButtons;
            var backupTexts = window.tabTexts;
            var backupReverts = window.revertButtons;
            Component madeBtn = null;
            GameObject madeContent = null;
            var tweenersFieldRef = typeof(UIOptionWindow).GetField("tabTweeners",
                BindingFlags.Instance | BindingFlags.Public);
            var backupTweeners = tweenersFieldRef != null ? tweenersFieldRef.GetValue(window) : null;

            try
            {
                var count = window.tabButtons.Length;
                if (count == 0) { Plugin.Log.LogWarning("[模型页] 设置窗口没有任何页签，放弃注入。"); return; }

                var srcBtn = window.tabButtons[count - 1];
                var srcText = window.tabTexts != null && window.tabTexts.Length >= count ? window.tabTexts[count - 1] : null;
                // tabTweeners 的元素类型（Tweener）不在 mod 引用的程序集里，
                // 编译期看不到，所以整段用反射操作。
                var tweenerField = typeof(UIOptionWindow).GetField("tabTweeners",
                    BindingFlags.Instance | BindingFlags.Public);
                var tweeners = tweenerField != null ? tweenerField.GetValue(window) as Array : null;
                var srcGroup = tweeners != null && tweeners.Length >= count
                    ? tweeners.GetValue(count - 1) as Component
                    : null;
                if (srcBtn == null || srcGroup == null)
                {
                    Plugin.Log.LogWarning("[模型页] 页签模板缺失，放弃注入。");
                    return;
                }

                // 原生 `_OnUnregEvent` 会按 tabButtons.Length 去索引 revertButtons 且不判空，
                // 所以模型页必须占一格。模型页**不提供**「恢复本页设置」——它会把其它
                // 供应商的 key/地址一起删掉，语义太危险；这里只克隆一个隐藏占位按钮。
                var revertTemplate = FindRevertTemplate(srcGroup.transform);

                Plugin.Log.LogInfo("[模型页] 步骤1：克隆页签按钮");
                // 1) 按钮：克隆最后一个页签按钮，横移到下一格。
                //
                // 注意 x 的算法：原生按钮是 `20 + 160 * index`（第一个按钮左边留 20），
                // 而滑块是 `160 * index`——滑块比按钮宽 160、按钮在列内居中 20。
                // 所以新按钮 = 原按钮 x + 160，**不是** 160 * count（那样会少 20px，
                // 页签标题看起来就相对滑块偏左一点，实机踩过）。
                var btn = UnityEngine.Object.Instantiate(srcBtn, srcBtn.transform.parent);
                madeBtn = btn;
                btn.name = "tab-model-" + count;
                var btnRt = btn.GetComponent<RectTransform>();
                var srcRt = srcBtn.GetComponent<RectTransform>();
                var step = TabWidth;
                if (count >= 2 && window.tabButtons[count - 2] != null)
                {
                    var prevRt = window.tabButtons[count - 2].GetComponent<RectTransform>();
                    if (prevRt != null && srcRt != null)
                    {
                        var nativeStep = srcRt.anchoredPosition.x - prevRt.anchoredPosition.x;
                        if (Mathf.Abs(nativeStep) > 1f) step = nativeStep;
                    }
                }
                if (btnRt != null && srcRt != null)
                {
                    btnRt.anchoredPosition = new Vector2(srcRt.anchoredPosition.x + step, srcRt.anchoredPosition.y);
                    Plugin.Log.LogInfo(
                        $"[模型页] 页签按钮位置：模板 x={srcRt.anchoredPosition.x:0.#},y={srcRt.anchoredPosition.y:0.#}、" +
                        $"间距 {step:0.#} → 新按钮 x={btnRt.anchoredPosition.x:0.#}（滑块 x={TabWidth * count:0.#}）");
                }

                Plugin.Log.LogInfo("[模型页] 步骤2：处理页签文字");
                // 2) 文字：原生 tabTexts[i] 就是 tabButtons[i] 的子物体（铺满按钮、居中），
                // 所以克隆按钮会连文字一起拿到。如果哪天游戏改成独立节点，就克隆原生文字。
                Text label = btn.GetComponentInChildren<Text>(true);
                if (label == null && srcText != null)
                {
                    var textClone = UnityEngine.Object.Instantiate(srcText, btn.transform, false);
                    label = textClone;
                    var lrt = label.transform as RectTransform;
                    var srt = srcText.transform as RectTransform;
                    if (lrt != null && srt != null)
                    {
                        lrt.anchorMin = srt.anchorMin;
                        lrt.anchorMax = srt.anchorMax;
                        lrt.pivot = srt.pivot;
                        lrt.anchoredPosition = srt.anchoredPosition;
                        lrt.sizeDelta = srt.sizeDelta;
                    }
                }
                if (label != null)
                {
                    label.text = Strings.Get("model.label");
                    Plugin.Log.LogInfo(
                        $"[模型页] 页签文字对象 = {label.name}（parent={label.transform.parent?.name}、" +
                        $"是按钮子物体={label.transform.IsChildOf(btn.transform)}）");
                }
                else
                {
                    // 没有文字对象就别扩数组：否则 IsInjectionAlive 永远为 false，
                    // Watch 会每 30 帧重复注入，数组无限增长。
                    throw new InvalidOperationException("克隆的页签按钮里找不到 Text，放弃注入。");
                }

                Plugin.Log.LogInfo("[模型页] 步骤3：克隆内容根");
                // 3) 内容根：克隆最后一个页签的内容组，清空子物体，保留 Tweener
                var content = UnityEngine.Object.Instantiate(srcGroup.gameObject, srcGroup.transform.parent);
                madeContent = content;
                content.name = "model-tab-content";
                for (var i = content.transform.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.Destroy(content.transform.GetChild(i).gameObject);
                }
                var tweenerType = tweeners.GetType().GetElementType();
                var tweener = content.GetComponent(tweenerType);
                content.SetActive(false);

                Plugin.Log.LogInfo("[模型页] 步骤4：扩展 tabButtons/tabTexts/tabTweeners 数组");
                // 4) 数组各扩一位
                var next = count + 1;
                Array.Resize(ref window.tabButtons, next);
                window.tabButtons[count] = btn;

                Array.Resize(ref window.tabTexts, next);
                window.tabTexts[count] = label;

                var nextTweeners = Array.CreateInstance(tweenerType, next);
                Array.Copy(tweeners, nextTweeners, tweeners.Length);
                nextTweeners.SetValue(tweener, count);
                tweenerField.SetValue(window, nextTweeners);

                // 4.5) 兜底再写一次文字：克隆来的按钮可能自带一份 Text，
                // 上面那条路若拿到的是别的 Text，页签就会显示成"杂项"（踩过）。
                if (window.tabTexts != null && window.tabTexts.Length > count && window.tabTexts[count] != null)
                {
                    window.tabTexts[count].text = Strings.Get("model.label");
                    Plugin.Log.LogInfo("[模型页] 页签文字 = " + window.tabTexts[count].text);
                }
                else
                {
                    Plugin.Log.LogWarning("[模型页] 页签文字对象为空，页签会显示成模板文字。");
                }

                // 4.6) 克隆来的 `tab-text` 上挂着游戏的 `Localizer`（`OnEnable` 里
                // `Refresh()` 把文字改回 stringKey 的译文）。设置窗口是 SetActive 开关的，
                // 每次重新打开都会 OnEnable → 页签标题被改回模板那一页（例如「按键」）。
                // 我们的页签不参与本地化表，摘掉即可；位置/尺寸在克隆时已经照抄。
                StripLocalizers(window.tabTexts[count] != null ? window.tabTexts[count].transform : null);

                // 5) 点我们的页签 → 走游戏自己的 SetTabIndex（私有，反射调用）
                var target = count;
                btn.onClick += _ => SwitchTo(window, target);

                Plugin.Log.LogInfo("[模型页] 步骤5：构建页面控件");
                var revertSlot = BuildPage(content.transform, window, revertTemplate);

                // revertButtons 与 tabButtons 必须等长：原生 _OnUnregEvent 会
                // 用 tabButtons.Length 索引 revertButtons，少一格就会 IndexOutOfRange。
                // 模型页这一格放的是**隐藏**的占位按钮，不显示也不响应点击。
                if (window.revertButtons != null)
                {
                    if (window.revertButtons.Length == count)
                        Array.Resize(ref window.revertButtons, next);
                    if (window.revertButtons.Length > count)
                        window.revertButtons[count] = revertSlot;
                }
                Plugin.Log.LogInfo("[模型页] 步骤6：完成");

                _index = count;
                _installed = true;
                Plugin.Log.LogInfo($"[模型页] 已注入设置页签，索引 {_index}（原有 {count} 个）。");
            }
            catch (Exception ex)
            {
                // 回滚到原状：宁可没有"模型"页，也不能让设置窗口每帧报错
                try
                {
                    window.tabButtons = backupButtons;
                    window.tabTexts = backupTexts;
                    window.revertButtons = backupReverts;
                    if (tweenersFieldRef != null && backupTweeners != null)
                        tweenersFieldRef.SetValue(window, backupTweeners);
                    if (madeBtn != null) UnityEngine.Object.Destroy(madeBtn.gameObject);
                    if (madeContent != null) UnityEngine.Object.Destroy(madeContent);
                }
                catch { /* 回滚本身失败就没辙了 */ }

                _installed = false;
                _index = -1;
                Plugin.Log.LogWarning("[模型页] 注入失败（已回滚，设置窗口不受影响）：" + ex);
            }
        }

        /// <summary>在原生页签内容根里找「恢复本页设置」按钮，仅作为隐藏占位槽的克隆模板。</summary>
        private static Transform FindRevertTemplate(Transform contentRoot)        {
            if (contentRoot == null) return null;

            var direct = contentRoot.Find("revert-button");
            if (direct != null) return direct;

            // 名字若被本地化/改版，退化为按前缀找
            for (var i = 0; i < contentRoot.childCount; i++)
            {
                var child = contentRoot.GetChild(i);
                if (child.name.StartsWith("revert-button", StringComparison.Ordinal)) return child;
            }
            return null;
        }

        /// <summary>
        /// 摘掉克隆对象上的游戏 `Localizer`（按类型名找，不依赖游戏程序集里的类型名）。
        ///
        /// 模板页签的文字/位置在克隆时已经一并抄过来了，`Localizer.OnEnable`
        /// 再跑一次只会把文案改回模板页签的 `stringKey` 译文：
        /// 设置窗口是 SetActive 开关的，关一次再开就会触发 `OnEnable`，
        /// 那样「模型」页签标题会在第二次打开时变回「按键」之类。
        /// </summary>
        private static void StripLocalizers(Transform root)
        {
            if (root == null) return;
            try
            {
                var components = root.GetComponentsInChildren<Component>(true);
                for (var i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null) continue;
                    if (component.GetType().Name != "Localizer") continue;
                    UnityEngine.Object.Destroy(component);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[模型页] 摘除 Localizer 失败：" + ex.Message);
            }
        }

        /// <summary>我们注入的那一格是否还在（窗口可能被重建/销毁过）。</summary>
        private static bool IsInjectionAlive(UIOptionWindow window)
        {
            if (window == null || _index < 0) return false;

            var buttons = window.tabButtons;
            var texts = window.tabTexts;
            if (buttons == null || buttons.Length <= _index || buttons[_index] == null) return false;
            if (texts == null || texts.Length <= _index || texts[_index] == null) return false;

            var field = typeof(UIOptionWindow).GetField("tabTweeners",
                BindingFlags.Instance | BindingFlags.Public);
            var tweeners = field != null ? field.GetValue(window) as Array : null;
            if (tweeners == null || tweeners.Length <= _index) return false;
            return tweeners.GetValue(_index) != null;
        }

        private static void SwitchTo(UIOptionWindow window, int index)
        {
            try
            {
                var m = typeof(UIOptionWindow).GetMethod("SetTabIndex",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                m?.Invoke(window, new object[] { index, true });
                RefreshFromConfig();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[模型页] 切页签失败：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 页面

        private static UIButton BuildPage(Transform root, UIOptionWindow window, Transform revertTemplate)
        {
            _font = FontProvider.Resolve();

            // 克隆来的内容根可能是"按内容自适应"的小节点，必须铺满页签区域，
            // 否则整页会缩成中间一小条（踩过）。
            var rootRt = root as RectTransform;
            if (rootRt != null)
            {
                rootRt.anchorMin = Vector2.zero;
                rootRt.anchorMax = Vector2.one;
                rootRt.pivot = new Vector2(0.5f, 0.5f);
                rootRt.offsetMin = Vector2.zero;
                rootRt.offsetMax = Vector2.zero;
            }

            var vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 8f;
            // 底部留 60：原生窗口底部按钮区（全局「恢复默认设置」等）不能被正文压住。
            vlg.padding = new RectOffset((int)PageLeft, 24, (int)PageTop, 60);

            var head = UiFactory.Node("page-head", root);
            head.gameObject.AddComponent<LayoutElement>().preferredHeight = 32f;
            var title = AddText(head, Strings.Get("model.page.title"), 18, LabelColor, TextAnchor.MiddleLeft);
            UiFactory.Stretch(title.rectTransform, 0f, 0f, 0f, 0f);

            var body = UiFactory.Node("page-body", root);
            var bodyLe = body.gameObject.AddComponent<LayoutElement>();
            bodyLe.minHeight = 260f;
            bodyLe.flexibleHeight = 1f;
            var hlg = body.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.spacing = 12f;
            hlg.childAlignment = TextAnchor.UpperLeft;

            BuildProviderPane(body, window);
            BuildEditorPane(body);

            // 底部状态行：用原生设置页的字号/透明度，不像外挂调试文字。
            var hintRow = UiFactory.Node("hint-row", root);
            hintRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 24f;
            _hint = AddText(hintRow, string.Empty, 15, HintColor, TextAnchor.MiddleLeft);
            UiFactory.Stretch(_hint.rectTransform, 0f, 0f, 0f, 0f);

            var revertSlot = CreateHiddenRevertSlot(root, revertTemplate);
            HookApplyButton(window);
            BuildCatalogPopup(root);

            RefreshFromConfig(force: true);
            LogTextDiagnostics("密钥输入框", _apiKeyField != null ? _apiKeyField.textComponent : null);
            return revertSlot;
        }

        private static void BuildProviderPane(RectTransform body, UIOptionWindow window)
        {
            var pane = UiFactory.Node("provider-pane", body);
            var le = pane.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 190f;
            le.flexibleWidth = 0f;
            le.flexibleHeight = 1f;
            var bg = pane.gameObject.AddComponent<Image>();
            bg.color = CardBgColor;

            var vlg = pane.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.spacing = 6f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var head = UiFactory.Node("provider-head", pane);
            head.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
            var label = AddText(head, Strings.Get("model.provider.title"), 15, DimColor, TextAnchor.MiddleLeft);
            UiFactory.Stretch(label.rectTransform, 0f, 0f, 0f, 0f);

            _providerList = AddScrollList(pane, "provider-list", 80f);

            var presetLabel = AddText(pane, Strings.Get("model.provider.preset"), 12, DimColor, TextAnchor.MiddleLeft);
            presetLabel.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;
            var presetRow = AddRow(pane, "provider-preset-row", 28f, 0f);
            _presetCombo = CreatePresetCombo(window, presetRow);
            EnsureProviderCatalog();

            var addRow = AddRow(pane, "add-provider-row", 28f, 0f);
            _addProviderField = AddInput(addRow, Strings.Get("model.provider.id.placeholder"), false, 26f, 0f);
            _addProviderField.text = DefaultProvider;
            AddButton(addRow, "+", OnAddProvider, 28f, 26f, 16);

        }

        /// <summary>克隆原生设置页的下拉框，用于「预设供应商」。</summary>
        private static UIComboBox CreatePresetCombo(UIOptionWindow window, RectTransform parent)
        {
            UIComboBox template = window != null ? window.languageComp : null;
            if (template == null && window != null) template = window.resolutionComp;
            if (template == null && window != null) template = window.displayModeComp;
            if (template == null)
            {
                Plugin.Log.LogWarning("[模型页] 找不到原生下拉框模板，预设供应商只能用 ID 输入。");
                return null;
            }

            GameObject go = null;
            try
            {
                // 原生 UIComboBox 初始化时可能把自己的父节点 SetAsLastSibling；
                // 中间加一层 host，让它只能动 host，不会把「预设供应商」整行挪到
                // 「供应商 ID」下面。
                var host = UiFactory.Node("preset-combo-host", parent);
                UiFactory.Stretch(host, 0f, 0f, 0f, 0f);
                var hostLe = host.gameObject.AddComponent<LayoutElement>();
                hostLe.flexibleWidth = 1f;
                hostLe.preferredHeight = 26f;

                go = UnityEngine.Object.Instantiate(template.gameObject, host, false);
                go.name = "provider-preset-combo";
                go.SetActive(true);
                var combo = go.GetComponent<UIComboBox>();
                if (combo == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return null;
                }

                var rt = go.transform as RectTransform;
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0f, 0f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                le.preferredHeight = 26f;
                le.flexibleWidth = 1f;

                combo.translated = true;
                FillPresetCombo(combo);
                combo.UpdateItems();
                combo.itemIndex = 0;   // 「自定义…」；随后 RefreshEditor 会按当前供应商对齐
                combo.onItemIndexChange.AddListener(OnPresetChanged);

                if (_font != null)
                {
                    foreach (var text in go.GetComponentsInChildren<Text>(true))
                        text.font = _font;
                }

                // 预设下拉框在供应商栏底部：列表向下会伸到「恢复本页设置」按钮下面，
                // 改成从下拉框上沿向上展开。
                var comboRt = go.transform as RectTransform;
                var dropDown = go.transform.Find("Dropdown List ScrollBox") as RectTransform;
                if (comboRt != null && dropDown != null)
                {
                    dropDown.anchorMin = new Vector2(0f, 0f);
                    dropDown.anchorMax = new Vector2(1f, 0f);
                    dropDown.pivot = new Vector2(0.5f, 0f);
                    dropDown.sizeDelta = new Vector2(0f, 150f);
                    dropDown.anchoredPosition = new Vector2(0f, 26f);
                }
                return combo;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[模型页] 创建预设供应商下拉框失败：" + ex.Message);
                if (go != null) UnityEngine.Object.Destroy(go);
                return null;
            }
        }

        /// <summary>重建下拉项：「自定义…」固定第一，其余按 pi 目录顺序。</summary>
        private static void FillPresetCombo(UIComboBox combo)
        {
            if (combo == null) return;

            combo.Items.Clear();
            combo.ItemsData.Clear();
            combo.Items.Add(Strings.Get("model.provider.custom"));
            combo.ItemsData.Add(CustomPresetData);
            for (var i = 0; i < _presetProviders.Count; i++)
            {
                combo.Items.Add(_presetProviders[i].Name);
                combo.ItemsData.Add(i);
            }
        }

        private static void OnPresetChanged()
        {
            if (_suppressPresetEvent || _presetCombo == null) return;
            var index = _presetCombo.itemIndex;
            if (index < 0 || index >= _presetCombo.Items.Count) return;

            var data = index < _presetCombo.ItemsData.Count ? _presetCombo.ItemsData[index] : CustomPresetData;
            if (data == CustomPresetData || data < 0 || data >= _presetProviders.Count)
            {
                // 自定义：清掉目录带出来的 ID，把焦点交给下面的输入框等玩家自己填。
                if (_addProviderField != null)
                {
                    _addProviderField.text = string.Empty;
                    _addProviderField.ActivateInputField();
                }
                SetEditorHint(Strings.Get("model.provider.custom_hint"), false);
                return;
            }

            var id = _presetProviders[data].Id;
            if (_addProviderField != null) _addProviderField.text = id;
            var existing = _providers.Find((p) => p.Id == id);
            if (existing != null)
            {
                SelectProvider(id);
                SetEditorHint(Strings.Get("model.provider.exists", existing.Name), false);
            }
            else
            {
                SetEditorHint(Strings.Get("model.provider.picked", _presetProviders[data].Name), false);
            }
        }

        private static void BuildEditorPane(RectTransform body)
        {
            var pane = UiFactory.Node("editor-pane", body);
            var le = pane.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.flexibleHeight = 1f;
            var bg = pane.gameObject.AddComponent<Image>();
            bg.color = CardBgColor;

            var vlg = pane.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 6f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var head = AddRow(pane, "editor-head", 34f, 0f);
            _keyDot = AddDot(head, KeyMissingColor);
            _editorTitle = AddText(head, string.Empty, 18, LabelColor, TextAnchor.MiddleLeft);
            _editorTitle.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            AddButton(head, Strings.Get("model.provider.delete"), OnDeleteProvider, 46f, 24f, 14);

            var keyRow = AddRow(pane, "api-key-row", 30f, 0f);
            AddFixedLabel(keyRow, Strings.Get("model.api_key.label"), 70f);
            _apiKeyField = AddInput(keyRow, Strings.Get("model.api_key.placeholder"), true, 28f, 0f);
            _apiKeyField.onValueChanged.AddListener(OnApiKeyChanged);
            _testKeyButton = AddButton(keyRow, Strings.Get("model.test.button"), OnTestKey, 52f, 28f, 13);
            _testKeyButtonLabel = _testKeyButton != null
                ? _testKeyButton.GetComponentInChildren<Text>(true)
                : null;

            var keyStatus = AddText(pane, string.Empty, 12, DimColor, TextAnchor.MiddleLeft);
            keyStatus.horizontalOverflow = HorizontalWrapMode.Wrap;
            keyStatus.verticalOverflow = VerticalWrapMode.Truncate;
            keyStatus.gameObject.AddComponent<LayoutElement>().preferredHeight = 32f;
            _keyTestStatus = keyStatus;

            var urlRow = AddRow(pane, "base-url-row", 30f, 0f);
            AddFixedLabel(urlRow, Strings.Get("model.base_url.label"), 70f);
            _baseUrlField = AddInput(urlRow, Strings.Get("model.base_url.placeholder"), false, 28f, 0f);
            _baseUrlField.onValueChanged.AddListener(OnBaseUrlChanged);

            var modelsHead = AddRow(pane, "models-head", 26f, 0f);
            var modelsLabel = AddText(modelsHead, Strings.Get("model.label"), 15, DimColor, TextAnchor.MiddleLeft);
            modelsLabel.gameObject.AddComponent<LayoutElement>().preferredWidth = 52f;
            _modelCount = AddText(modelsHead, string.Empty, 13, DimColor, TextAnchor.MiddleRight);
            _modelCount.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            _modelList = AddScrollList(pane, "model-list", 90f);

            var addModelRow = AddRow(pane, "add-model-row", 28f, 0f);
            _addModelField = AddInput(addModelRow, Strings.Get("model.id.placeholder"), false, 26f, 300f);
            AddButton(addModelRow, "+", OnAddModel, 28f, 26f, 16);
            _catalogButton = AddButton(addModelRow, Strings.Get("model.catalog.button"), OnFetchModels, 112f, 26f, 13);
            _catalogButtonLabel = _catalogButton != null
                ? _catalogButton.GetComponentInChildren<Text>(true)
                : null;

            _editorHint = AddText(pane, string.Empty, 13, DimColor, TextAnchor.MiddleLeft);
            // 错误文案可能带 HTTP 状态与地址建议，允许折成两行，避免横向压到别的控件。
            _editorHint.horizontalOverflow = HorizontalWrapMode.Wrap;
            _editorHint.verticalOverflow = VerticalWrapMode.Truncate;
            _editorHint.gameObject.AddComponent<LayoutElement>().preferredHeight = 52f;
        }

        // ---------------------------------------------------------------- 获取可用模型

        private static void BuildCatalogPopup(Transform root)
        {
            var popup = UiFactory.Node("model-catalog-popup", root);
            popup.anchorMin = new Vector2(0.5f, 0.5f);
            popup.anchorMax = new Vector2(0.5f, 0.5f);
            popup.pivot = new Vector2(0.5f, 0.5f);
            popup.sizeDelta = new Vector2(640f, 480f);
            popup.anchoredPosition = Vector2.zero;
            var le = popup.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            var bg = popup.gameObject.AddComponent<Image>();
            bg.color = new Color(0.055f, 0.09f, 0.13f, 0.985f);
            _catalogPopup = popup;

            var head = UiFactory.Node("catalog-head", popup);
            head.anchorMin = new Vector2(0f, 1f);
            head.anchorMax = new Vector2(1f, 1f);
            head.pivot = new Vector2(0.5f, 1f);
            head.anchoredPosition = new Vector2(0f, -12f);
            head.sizeDelta = new Vector2(-32f, 38f);
            var hlg = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(16, 16, 0, 0);
            hlg.spacing = 8f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            _catalogTitle = AddText(head, Strings.Get("model.catalog.title"), 18, LabelColor, TextAnchor.MiddleLeft);
            _catalogTitle.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            AddButton(head, "×", () =>
            {
                if (_catalogPopup != null) _catalogPopup.gameObject.SetActive(false);
            }, 32f, 32f, 18);

            _catalogList = AddScrollList(popup, "catalog-list", 240f);
            var scrollRoot = _catalogList != null ? _catalogList.parent.parent as RectTransform : null;
            if (scrollRoot != null)
            {
                scrollRoot.anchorMin = new Vector2(0f, 0f);
                scrollRoot.anchorMax = new Vector2(1f, 1f);
                scrollRoot.offsetMin = new Vector2(16f, 52f);
                scrollRoot.offsetMax = new Vector2(-16f, -60f);
            }

            var hintRow = UiFactory.Node("catalog-hint-row", popup);
            hintRow.anchorMin = new Vector2(0f, 0f);
            hintRow.anchorMax = new Vector2(1f, 0f);
            hintRow.pivot = new Vector2(0.5f, 0f);
            hintRow.anchoredPosition = new Vector2(0f, 12f);
            hintRow.sizeDelta = new Vector2(-32f, 30f);
            _catalogHint = AddText(hintRow, string.Empty, 13, HintColor, TextAnchor.MiddleLeft);
            UiFactory.Stretch(_catalogHint.rectTransform, 16f, 0f, 16f, 0f);

            popup.gameObject.SetActive(false);
        }

        private static void OnFetchModels()
        {
            if (_catalogPending) return;
            var draft = SelectedDraft;
            if (draft == null) return;

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                SetEditorHint(Strings.Get("model.catalog.offline"), true);
                return;
            }
            if (!IsKnownProvider(draft.Id) && string.IsNullOrEmpty((draft.BaseUrl ?? string.Empty).Trim()))
            {
                SetEditorHint(Strings.Get("model.catalog.need_base_url"), true);
                return;
            }

            var requestId = _client.SendModelCatalog(
                draft.Id,
                string.IsNullOrEmpty(draft.BaseUrl) ? null : draft.BaseUrl.Trim(),
                string.IsNullOrEmpty(draft.ApiKey) ? null : draft.ApiKey.Trim());
            if (string.IsNullOrEmpty(requestId))
            {
                SetEditorHint(Strings.Get("model.catalog.not_ready"), true);
                return;
            }

            _catalogProviderId = draft.Id;
            _catalogRequestId = requestId;
            _catalogResult = null;
            _catalogPending = true;
            _catalogRequestedAt = Time.realtimeSinceStartup;
            SetCatalogButtonBusy(true);
            SetEditorHint(Strings.Get("model.catalog.loading", draft.Name), false);
        }

        /// <summary>接收线程：只解析并存结果，绝不碰 UnityEngine 对象。</summary>
        private static void OnClientReceived(string type, JsonValue payload, string replyTo)
        {
            if (type == Protocol.ModelCatalog)
            {
                if (string.IsNullOrEmpty(replyTo) || replyTo != _catalogRequestId) return;
                _catalogResult = ModelCatalogResult.Parse(payload);
                _catalogPending = false;
                return;
            }
            if (type == Protocol.ProviderCatalog)
            {
                if (string.IsNullOrEmpty(replyTo) || replyTo != _providerCatalogRequestId) return;
                _providerCatalogResult = ProviderCatalogResult.Parse(payload);
                _providerCatalogPending = false;
                return;
            }
            if (type == Protocol.ProviderTest)
            {
                if (string.IsNullOrEmpty(replyTo) || replyTo != _testKeyRequestId) return;
                _testKeyResult = ProviderTestResult.Parse(payload);
                _testKeyPending = false;
            }
        }

        /// <summary>主线程（Watch）：拉一次 pi 内置目录的供应商列表（只读、不联网）。</summary>
        private static void EnsureProviderCatalog()
        {
            if (_providerCatalogLoaded || _providerCatalogPending) return;
            if (_client == null || _client.State != ConnectionState.Ready) return;

            var requestId = _client.SendProviderCatalog();
            if (string.IsNullOrEmpty(requestId)) return;
            _providerCatalogRequestId = requestId;
            _providerCatalogPending = true;
        }

        /// <summary>主线程（Watch）：把后台拿到的供应商目录落到下拉框。</summary>
        private static void ConsumeProviderCatalog()
        {
            var result = _providerCatalogResult;
            if (result == null) return;
            _providerCatalogResult = null;
            _providerCatalogPending = false;
            _providerCatalogLoaded = true;
            _presetProviders.Clear();
            _presetProviders.AddRange(result.Providers);
            RebuildPresetCombo();
        }

        /// <summary>按当前目录重建下拉项；选中项按当前供应商对齐（目录里没有则停在「自定义…」）。</summary>
        private static void RebuildPresetCombo()
        {
            if (_presetCombo == null) return;
            _suppressPresetEvent = true;
            try
            {
                FillPresetCombo(_presetCombo);
                _presetCombo.UpdateItems();
                var draft = SelectedDraft;
                _presetCombo.itemIndex = PresetComboIndex(draft != null ? draft.Id : null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[模型页] 刷新预设供应商下拉框失败：" + ex.Message);
            }
            finally
            {
                _suppressPresetEvent = false;
            }
        }

        /// <summary>供应商 id → 下拉框下标（目录项整体后移一位；未知 / 空 = 0「自定义…」）。</summary>
        private static int PresetComboIndex(string id)
        {
            var presetIndex = IndexOfPreset(id);
            return presetIndex >= 0 ? presetIndex + 1 : 0;
        }

        private static int IndexOfPreset(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (var i = 0; i < _presetProviders.Count; i++)
            {
                if (string.Equals(_presetProviders[i].Id, id, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>主线程（Watch）：把后台拿到的目录落到弹层。</summary>
        private static void ConsumeCatalogResult()
        {
            if (_catalogPending && Time.realtimeSinceStartup - _catalogRequestedAt > 20f)
            {
                _catalogPending = false;
                SetCatalogButtonBusy(false);
                SetEditorHint(Strings.Get("model.catalog.timeout"), true);
                return;
            }

            var result = _catalogResult;
            if (result == null) return;
            _catalogResult = null;
            SetCatalogButtonBusy(false);

            // 请求期间玩家可能切了供应商：切回请求的那一个，避免结果落到错误的卡片。
            if (SelectedDraft == null || SelectedDraft.Id != _catalogProviderId)
            {
                var draft = _providers.Find((p) => p.Id == _catalogProviderId);
                if (draft == null) return;
                SelectProvider(draft.Id);
            }

            ShowCatalog(result);
            if (!string.IsNullOrEmpty(result.Error))
            {
                // 有些中转的 /models 不可用（404/限流/挂住），但聊天接口正常；
                // 提示玩家可以直接手填模型 ID，不要卡在弹层里。
                SetEditorHint(Strings.Get("model.catalog.failed", result.Error), true);
            }
            else
            {
                var fixedBase = ApplyResolvedBaseUrl(result.ResolvedBaseUrl);
                SetEditorHint(
                    fixedBase != null
                        ? Strings.Get("model.catalog.done_base_url", result.Models.Count, fixedBase)
                        : Strings.Get("model.catalog.done", result.Models.Count),
                    false);
            }
        }

        private static void OnTestKey()
        {
            if (_testKeyPending) return;
            var draft = SelectedDraft;
            if (draft == null) return;

            if (_client == null || _client.State != ConnectionState.Ready)
            {
                SetKeyTestStatus(Strings.Get("model.test.offline"), true);
                return;
            }
            if (string.IsNullOrEmpty(draft.ApiKey) && !draft.HasKey)
            {
                SetKeyTestStatus(Strings.Get("model.test.need_key"), true);
                return;
            }
            if (!IsKnownProvider(draft.Id) && string.IsNullOrEmpty((draft.BaseUrl ?? string.Empty).Trim()))
            {
                SetKeyTestStatus(Strings.Get("model.test.need_base_url"), true);
                return;
            }

            var requestId = _client.SendProviderTest(
                draft.Id,
                string.IsNullOrEmpty(draft.BaseUrl) ? null : draft.BaseUrl.Trim(),
                string.IsNullOrEmpty(draft.ApiKey) ? null : draft.ApiKey.Trim());
            if (string.IsNullOrEmpty(requestId))
            {
                SetKeyTestStatus(Strings.Get("model.test.not_ready"), true);
                return;
            }

            _testKeyProviderId = draft.Id;
            _testKeyRequestId = requestId;
            _testKeyResult = null;
            _testKeyPending = true;
            _testKeyRequestedAt = Time.realtimeSinceStartup;
            SetTestButtonBusy(true);
            SetKeyTestStatus(Strings.Get("model.test.loading"), false);
        }

        private static void ConsumeTestKeyResult()
        {
            if (_testKeyPending && Time.realtimeSinceStartup - _testKeyRequestedAt > 20f)
            {
                _testKeyPending = false;
                SetTestButtonBusy(false);
                SetKeyTestStatus(Strings.Get("model.test.timeout"), true);
                return;
            }

            var result = _testKeyResult;
            if (result == null) return;
            _testKeyResult = null;
            SetTestButtonBusy(false);

            if (SelectedDraft == null || SelectedDraft.Id != _testKeyProviderId)
            {
                var draft = _providers.Find((p) => p.Id == _testKeyProviderId);
                if (draft == null) return;
                SelectProvider(draft.Id);
            }

            if (result.Ok)
            {
                ApplyResolvedBaseUrl(result.ResolvedBaseUrl);
                SetKeyTestStatus(Strings.Get("model.test.ok", Mathf.Max(0, result.LatencyMs)), false);
                if (_keyDot != null) _keyDot.color = KeyOkColor;
            }
            else
            {
                SetKeyTestStatus(string.IsNullOrEmpty(result.Message) ? Strings.Get("model.test.failed") : result.Message, true);
                if (_keyDot != null) _keyDot.color = KeyMissingColor;
            }
        }

        /// <summary>
        /// 把 sidecar 带回的「聊天可用地址」写回草稿与输入框。
        ///
        /// 探测地址和聊天地址并不总是同一个：OpenAI 兼容端点可能缺 `/v1`，
        /// Anthropic 的 SDK 自己会补 `/v1/messages`（base 不能带 `/v1`），
        /// Gemini 适配器则要求 base 里带 `/v1beta`。不补全就会出现
        /// 「检测通过、发消息 404」。返回补全后的地址；无变化时返回 null。
        /// </summary>
        private static string ApplyResolvedBaseUrl(string resolved)
        {
            if (string.IsNullOrEmpty(resolved)) return null;
            var draft = SelectedDraft;
            if (draft == null) return null;
            // 玩家正在编辑地址时不要抢输入框；等他自己改完或重新检测。
            if (_baseUrlField != null && _baseUrlField.isFocused) return null;

            var current = (draft.BaseUrl ?? string.Empty).Trim();
            if (string.Equals(current.TrimEnd('/'), resolved.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return null;

            draft.BaseUrl = resolved;
            _dirty = true;
            if (_baseUrlField != null)
            {
                _suppressFieldEvents = true;
                try { _baseUrlField.text = resolved; }
                finally { _suppressFieldEvents = false; }
            }
            return resolved;
        }

        private static void SetTestButtonBusy(bool busy)
        {
            if (_testKeyButton != null) _testKeyButton.interactable = !busy;
            if (_testKeyButtonLabel != null)
                _testKeyButtonLabel.text = busy ? Strings.Get("model.test.busy") : Strings.Get("model.test.button");
        }

        private static void SetKeyTestStatus(string text, bool warn)
        {
            if (_keyTestStatus == null) return;
            _keyTestStatus.text = text ?? string.Empty;
            _keyTestStatus.color = warn ? HintWarnColor : HintOkColor;
        }

        private static void SetCatalogButtonBusy(bool busy)
        {
            if (_catalogButton != null) _catalogButton.interactable = !busy;
            if (_catalogButtonLabel != null)
                _catalogButtonLabel.text = busy ? Strings.Get("model.catalog.busy") : Strings.Get("model.catalog.button");
        }

        private static void ShowCatalog(ModelCatalogResult result)
        {
            if (_catalogPopup == null || result == null) return;

            _catalogModels.Clear();
            foreach (var model in result.Models)
                if (model != null && !string.IsNullOrEmpty(model.Id)) _catalogModels.Add(model);

            var draft = _providers.Find((p) => p.Id == _catalogProviderId) ?? SelectedDraft;
            _catalogTitle.text = Strings.Get("model.catalog.title_with",
                draft != null ? draft.Name : (result.Provider ?? string.Empty));
            _catalogPopup.gameObject.SetActive(true);
            _catalogPopup.SetAsLastSibling();

            if (!string.IsNullOrEmpty(result.Error))
            {
                ClearChildren(_catalogList);
                _catalogHint.text = result.Error;
                _catalogHint.color = HintWarnColor;
                return;
            }

            _catalogHint.color = HintColor;
            _catalogHint.text = _catalogModels.Count == 0
                ? Strings.Get("model.catalog.empty_hint")
                : Strings.Get("model.catalog.count_hint", _catalogModels.Count);
            RebuildCatalogRows();
        }

        private static void RebuildCatalogRows()
        {
            if (_catalogList == null) return;
            ClearChildren(_catalogList);
            var draft = _providers.Find((p) => p.Id == _catalogProviderId) ?? SelectedDraft;

            if (_catalogModels.Count == 0)
            {
                var empty = AddText(_catalogList, Strings.Get("model.catalog.empty"), 14, DimColor, TextAnchor.MiddleLeft);
                empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
                return;
            }

            foreach (var model in _catalogModels)
            {
                var item = model;
                var row = AddRow(_catalogList, "catalog-row", 30f, 4f);
                var label = AddText(row,
                    string.IsNullOrEmpty(item.Name) || item.Name == item.Id
                        ? item.Id
                        : item.Name + "  ·  " + item.Id,
                    14, LabelColor, TextAnchor.MiddleLeft);
                var labelLe = label.gameObject.AddComponent<LayoutElement>();
                labelLe.flexibleWidth = 1f;
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Truncate;

                var exists = draft != null && draft.Models.Exists((m) => m.Id == item.Id);
                if (exists)
                {
                    var added = AddText(row, Strings.Get("model.catalog.added"), 12, DimColor, TextAnchor.MiddleRight);
                    added.gameObject.AddComponent<LayoutElement>().preferredWidth = 52f;
                }
                else
                {
                    AddButton(row, Strings.Get("model.catalog.add"), () => AddCatalogModel(item), 64f, 24f, 13);
                }
            }
        }

        private static void AddCatalogModel(ModelInfo model)
        {
            if (model == null || string.IsNullOrEmpty(model.Id)) return;
            var draft = _providers.Find((p) => p.Id == _catalogProviderId) ?? SelectedDraft;
            if (draft == null) return;
            if (draft.Models.Exists((m) => m.Id == model.Id)) return;

            draft.Models.Add(new ModelDraft { Id = model.Id, Name = model.Name });
            _dirty = true;
            RebuildProviderList();
            if (SelectedDraft != null && SelectedDraft.Id == draft.Id) RebuildModelList();
            RebuildCatalogRows();
            SetEditorHint(Strings.Get("model.catalog.model_added",
                string.IsNullOrEmpty(model.Name) ? model.Id : model.Name), false);
        }

        private static void AddFixedLabel(Transform parent, string text, float width)
        {
            var label = AddText(parent, text, 15, LabelColor, TextAnchor.MiddleLeft);
            var le = label.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
        }

        private static Text AddText(Transform parent, string text, int size, Color color, TextAnchor align)
        {
            var rt = UiFactory.Node("text", parent);
            var label = UiFactory.AddLabel(rt, _font, size, color, align, gameMaterial: false);
            label.text = text;
            label.supportRichText = false;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        private static RectTransform AddRow(Transform parent, string name, float height, float pad)
        {
            var row = UiFactory.Node(name, parent);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;

            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset((int)pad, (int)pad, 0, 0);
            hlg.spacing = 6f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            return row;
        }

        private static RectTransform AddScrollList(Transform parent, string name, float minHeight)
        {
            var root = UiFactory.Node(name, parent);
            var le = root.gameObject.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;
            le.minHeight = minHeight;

            var bg = root.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.12f);
            bg.raycastTarget = true;

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 25f;
            scroll.inertia = true;

            var viewport = UiFactory.Node("viewport", root);
            UiFactory.Stretch(viewport, 2f, 2f, 2f, 2f);
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0f);
            vpImg.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = UiFactory.Node("content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(2, 2, 2, 2);
            vlg.spacing = 2f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
            return content;
        }

        private static Image AddDot(Transform parent, Color color)
        {
            var rt = UiFactory.Node("key-dot", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 8f;
            le.minWidth = 8f;
            le.preferredHeight = 8f;
            le.minHeight = 8f;
            var image = rt.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static InputField AddInput(Transform parent, string placeholder, bool password,
            float height, float preferredWidth)
        {
            var box = UiFactory.Node("input", parent);
            var le = box.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            if (preferredWidth > 0f)
            {
                le.preferredWidth = preferredWidth;
                le.minWidth = preferredWidth;
            }
            else
            {
                le.flexibleWidth = 1f;
            }

            var bg = box.gameObject.AddComponent<Image>();
            bg.color = ControlBgColor;
            bg.raycastTarget = true;
            box.gameObject.AddComponent<RectMask2D>();

            var textRt = UiFactory.Node("text", box);
            UiFactory.Stretch(textRt, 8f, 0f, 8f, 0f);
            var text = textRt.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 15;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;

            var phRt = UiFactory.Node("placeholder", box);
            UiFactory.Stretch(phRt, 8f, 0f, 8f, 0f);
            var ph = phRt.gameObject.AddComponent<Text>();
            ph.font = _font;
            ph.fontSize = 15;
            ph.color = PlaceholderColor;
            ph.alignment = TextAnchor.MiddleLeft;
            ph.horizontalOverflow = HorizontalWrapMode.Overflow;
            ph.verticalOverflow = VerticalWrapMode.Truncate;
            ph.text = placeholder;

            var input = box.gameObject.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = ph;
            input.targetGraphic = bg;
            input.lineType = InputField.LineType.SingleLine;
            if (password)
            {
                input.contentType = InputField.ContentType.Password;
                input.asteriskChar = '•';
            }
            return input;
        }

        private static Button AddButton(Transform parent, string label, Action action,
            float width, float height, int fontSize)
        {
            var go = UiFactory.Node("button", parent);
            var le = go.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
            le.preferredHeight = height;
            le.minHeight = height;

            var bg = go.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.38f);
            var button = go.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.84f, 0.90f, 1f, 1f);
            colors.pressedColor = new Color(0.68f, 0.78f, 0.95f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.4f);
            button.colors = colors;
            var nav = button.navigation;
            nav.mode = Navigation.Mode.None;
            button.navigation = nav;

            var textRt = UiFactory.Node("label", go);
            UiFactory.Stretch(textRt, 2f, 0f, 2f, 0f);
            var text = UiFactory.AddLabel(textRt, _font, fontSize, Color.white,
                TextAnchor.MiddleCenter, gameMaterial: false);
            text.text = label;
            text.supportRichText = false;
            text.raycastTarget = false;

            button.onClick.AddListener(() => action());
            return button;
        }

        private static void ClearChildren(RectTransform parent)
        {
            if (parent == null) return;
            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        private static void LogTextDiagnostics(string name, Text text)
        {
            if (text == null)
            {
                Plugin.Log.LogWarning($"[模型页] {name} 的 Text = null");
                return;
            }

            try
            {
                Plugin.Log.LogInfo(
                    $"[模型页] {name}：font={(text.font != null ? text.font.name : "null")} " +
                    $"size={text.fontSize} color={ColorUtility.ToHtmlStringRGBA(text.color)} " +
                    $"mat={(text.material != null ? text.material.name : "null")} " +
                    $"tex={(text.mainTexture != null ? text.mainTexture.name : "null")} " +
                    $"meshGen={text.useMeshGenerator} " +
                    $"preferred={text.preferredWidth:0}x{text.preferredHeight:0} " +
                    $"rect={text.rectTransform.rect.width:0}x{text.rectTransform.rect.height:0} " +
                    $"enabled={text.enabled} active={text.gameObject.activeInHierarchy}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[模型页] {name} 诊断失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 把「模型」页的保存挂到游戏原生的「应用设置」按钮上——
        /// 玩家点一次应用，游戏选项和我们的配置一起生效，不需要第二个按钮。
        /// </summary>
        private static void HookApplyButton(UIOptionWindow window)
        {
            // 窗口会被场景切换重建：只认"已经挂过的那个窗口实例"，
            // 不能只用一个 bool（否则新窗口的 applyButton 永远挂不上）。
            if (_applyHooked && _applyHookedWindow == window) return;

            try
            {
                var field = typeof(UIOptionWindow).GetField("applyButton",
                    BindingFlags.Instance | BindingFlags.Public);
                var apply = field != null ? field.GetValue(window) as UIButton : null;
                if (apply == null)
                {
                    Plugin.Log.LogWarning("[模型页] 找不到 applyButton，配置将无法保存。");
                    return;
                }

                apply.onClick += _ => Save();
                _applyHooked = true;
                _applyHookedWindow = window;
                Plugin.Log.LogInfo("[模型页] 已把配置保存挂到「应用设置」上。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[模型页] 挂接 applyButton 失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 给 `revertButtons` 数组占一个**隐藏**的槽位。
        ///
        /// 原生 `UIOptionWindow._OnUnregEvent` 会遍历 `tabButtons.Length` 并直接访问
        /// `revertButtons[i].onClick`，不判空；数组少一格或该格为 null 都会让设置窗口
        /// 在关闭时抛 NRE。所以这里只克隆一个不可见、不可点的按钮占位，不挂任何逻辑。
        /// </summary>
        private static UIButton CreateHiddenRevertSlot(Transform root, Transform template)
        {
            if (template == null)
            {
                // 模板缺失也必须给出一个真实对象：原生 _OnUnregEvent 不判空。
                Plugin.Log.LogWarning("[模型页] 原生页签里没有 revert-button，用空 UIButton 占位。");
                var holder = new GameObject("revert-slot-hidden", typeof(RectTransform));
                holder.transform.SetParent(root, false);
                holder.SetActive(false);
                return holder.AddComponent<UIButton>();
            }

            var clone = UnityEngine.Object.Instantiate(template, root, false);
            clone.name = "revert-slot-hidden";

            // 原生页面是手工摆的；这里挂在 VLG 下，必须让布局组跳过它，
            // 否则会被当成正文的一行。反正不可见，只保留 ignoreLayout 防御。
            var le = clone.GetComponent<LayoutElement>();
            if (le == null) le = clone.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;

            var button = clone.GetComponent<UIButton>();
            if (button != null && button.button != null)
            {
                // 原生 _OnRegEvent 仍会往这个槽位上挂 OnRevertButtonClick；
                // 但对象是 inactive + 不可交互，玩家点不到，也不会触发模型页逻辑。
                button.button.interactable = false;
            }
            clone.gameObject.SetActive(false);
            return button;
        }

        // ---------------------------------------------------------------- 数据

        private static ProviderDraft SelectedDraft
        {
            get
            {
                for (var i = 0; i < _providers.Count; i++)
                    if (_providers[i].Id == _selectedProvider) return _providers[i];
                return _providers.Count > 0 ? _providers[0] : null;
            }
        }

        private static void RefreshFromConfig(bool force = false)
        {
            _refreshPending = false;

            var cfg = _client != null ? _client.Config : null;
            if (cfg == null)
            {
                if (_providers.Count == 0) SeedDefaults();
                SetHint(Strings.Get("model.status.offline"), false);
                RebuildAll();
                return;
            }

            // 玩家正在改草稿时不覆盖；点「应用设置」后 config.state 回来再刷新。
            if (_dirty && !force) return;

            _providers.Clear();
            _removed.Clear();

            foreach (var provider in cfg.Providers)
            {
                if (provider == null || string.IsNullOrEmpty(provider.Id)) continue;
                var draft = new ProviderDraft
                {
                    Id = provider.Id,
                    Name = string.IsNullOrEmpty(provider.Name) ? provider.Id : provider.Name,
                    HasKey = provider.HasKey,
                    BaseUrl = provider.BaseUrl ?? string.Empty,
                    IsActive = provider.Id == cfg.Provider,
                };
                foreach (var model in provider.Models)
                {
                    if (model == null || string.IsNullOrEmpty(model.Id)) continue;
                    draft.Models.Add(new ModelDraft
                    {
                        Id = model.Id,
                        Name = string.IsNullOrEmpty(model.Name) ? model.Id : model.Name,
                    });
                }
                _providers.Add(draft);
            }

            // 老服务端 / 异常空列表：至少给一条可编辑的。
            if (_providers.Count == 0 && !string.IsNullOrEmpty(cfg.Provider))
            {
                var draft = new ProviderDraft
                {
                    Id = cfg.Provider,
                    Name = DisplayName(cfg.Provider),
                    HasKey = cfg.IsReady,
                    IsActive = true,
                };
                if (!string.IsNullOrEmpty(cfg.Model))
                    draft.Models.Add(new ModelDraft { Id = cfg.Model, Name = cfg.Model });
                _providers.Add(draft);
            }
            if (_providers.Count == 0) SeedDefaults();

            _activeProvider = cfg.Provider ?? string.Empty;
            _activeModel = cfg.Model ?? string.Empty;

            var keep = false;
            foreach (var provider in _providers)
                if (provider.Id == _selectedProvider) keep = true;
            if (!keep)
            {
                _selectedProvider = _providers[0].Id;
                foreach (var provider in _providers)
                    if (provider.Id == _activeProvider) _selectedProvider = provider.Id;
            }

            _dirty = false;
            _suppressFieldEvents = true;
            try { if (_apiKeyField != null) _apiKeyField.text = string.Empty; }
            finally { _suppressFieldEvents = false; }
            RebuildAll();

            var hint = cfg.IsReady
                ? Strings.Get("model.status.ready", _providers.Count)
                : (string.IsNullOrEmpty(cfg.Message) ? Strings.Get("model.status.need_key") : cfg.Message);
            SetHint(hint, cfg.IsReady);
        }

        private static void RebuildAll()
        {
            if (_providers.Count == 0) SeedDefaults();
            RebuildProviderList();
            RebuildModelList();
            RefreshEditor(resetInputs: true);
        }

        private static void RebuildProviderList()
        {
            ClearChildren(_providerList);
            if (_providers.Count == 0)
            {
                var empty = AddText(_providerList, Strings.Get("model.provider.empty"), 14, DimColor, TextAnchor.MiddleLeft);
                empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
                return;
            }

            foreach (var provider in _providers)
            {
                var draft = provider;
                var selected = draft.Id == _selectedProvider;
                var row = AddRow(_providerList, "provider-" + draft.Id, 30f, 6f);
                var bg = row.gameObject.AddComponent<Image>();
                bg.color = selected ? RowSelectedColor : RowBgColor;
                bg.raycastTarget = true;

                var button = row.gameObject.AddComponent<Button>();
                button.targetGraphic = bg;
                button.transition = Selectable.Transition.ColorTint;
                var colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(0.88f, 0.93f, 1f, 1f);
                colors.pressedColor = new Color(0.72f, 0.82f, 0.96f, 1f);
                button.colors = colors;
                var nav = button.navigation;
                nav.mode = Navigation.Mode.None;
                button.navigation = nav;
                button.onClick.AddListener(() => SelectProvider(draft.Id));

                var keyed = draft.HasKey || !string.IsNullOrEmpty(draft.ApiKey);
                AddDot(row, keyed ? KeyOkColor : KeyMissingColor);

                var name = AddText(row, draft.Name, 15,
                    selected ? Color.white : LabelColor, TextAnchor.MiddleLeft);
                name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
                name.horizontalOverflow = HorizontalWrapMode.Wrap;
                name.verticalOverflow = VerticalWrapMode.Truncate;

                var count = AddText(row, draft.Models.Count.ToString(), 12, DimColor, TextAnchor.MiddleRight);
                count.gameObject.AddComponent<LayoutElement>().preferredWidth = 18f;
            }
        }

        private static void RebuildModelList()
        {
            ClearChildren(_modelList);
            var draft = SelectedDraft;
            if (draft == null)
            {
                var empty = AddText(_modelList, Strings.Get("model.provider.missing"), 14, DimColor, TextAnchor.MiddleLeft);
                empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
                return;
            }

            if (draft.Models.Count == 0)
            {
                var empty = AddText(_modelList, Strings.Get("model.empty"), 14, DimColor, TextAnchor.MiddleLeft);
                empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
            }

            foreach (var model in draft.Models)
            {
                var item = model;
                var active = draft.Id == _activeProvider && item.Id == _activeModel;
                var row = AddRow(_modelList, "model-" + item.Id, 28f, 6f);
                var bg = row.gameObject.AddComponent<Image>();
                bg.color = active ? RowSelectedColor : RowBgColor;
                bg.raycastTarget = true;

                var button = row.gameObject.AddComponent<Button>();
                button.targetGraphic = bg;
                button.transition = Selectable.Transition.ColorTint;
                var colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(0.88f, 0.93f, 1f, 1f);
                colors.pressedColor = new Color(0.72f, 0.82f, 0.96f, 1f);
                button.colors = colors;
                var nav = button.navigation;
                nav.mode = Navigation.Mode.None;
                button.navigation = nav;
                button.onClick.AddListener(() => SetActiveModel(draft.Id, item.Id));

                var name = AddText(row, item.Name, 15,
                    active ? Color.white : LabelColor, TextAnchor.MiddleLeft);
                var nameLe = name.gameObject.AddComponent<LayoutElement>();
                nameLe.preferredWidth = 320f;
                name.horizontalOverflow = HorizontalWrapMode.Wrap;
                name.verticalOverflow = VerticalWrapMode.Truncate;

                if (active)
                {
                    var badge = AddText(row, Strings.Get("model.current"), 12, KeyOkColor, TextAnchor.MiddleRight);
                    badge.gameObject.AddComponent<LayoutElement>().preferredWidth = 28f;
                }

                AddButton(row, "×", () => RemoveModel(draft.Id, item.Id), 22f, 22f, 14);
            }

            if (_modelCount != null)
                _modelCount.text = Strings.Get("model.count", draft.Models.Count);
        }

        private static void RefreshEditor(bool resetInputs)
        {
            var draft = SelectedDraft;
            var has = draft != null;

            if (_editorTitle != null)
                _editorTitle.text = has ? draft.Name : Strings.Get("model.provider.none");

            if (_presetCombo != null)
            {
                // 目录里没有的自定义供应商停在「自定义…」，ID 由下方输入框自己维护。
                _suppressPresetEvent = true;
                try { _presetCombo.itemIndex = has ? PresetComboIndex(draft.Id) : 0; }
                finally { _suppressPresetEvent = false; }
            }

            if (_keyTestStatus != null && resetInputs)
            {
                _keyTestStatus.text = string.Empty;
                _keyTestStatus.color = DimColor;
            }

            if (_keyDot != null)
            {
                var keyed = has && (draft.HasKey || !string.IsNullOrEmpty(draft.ApiKey));
                _keyDot.color = keyed ? KeyOkColor : KeyMissingColor;
            }

            if (_apiKeyField != null)
            {
                var place = _apiKeyField.placeholder as Text;
                if (place != null)
                {
                    place.text = has && draft.HasKey
                        ? Strings.Get("model.api_key.placeholder_set")
                        : Strings.Get("model.api_key.placeholder");
                    place.color = PlaceholderColor;
                }
                if (resetInputs)
                {
                    _suppressFieldEvents = true;
                    try { _apiKeyField.text = has ? draft.ApiKey : string.Empty; }
                    finally { _suppressFieldEvents = false; }
                }
            }

            if (_baseUrlField != null)
            {
                if (resetInputs || !_baseUrlField.isFocused)
                {
                    _suppressFieldEvents = true;
                    try { _baseUrlField.text = has ? draft.BaseUrl : string.Empty; }
                    finally { _suppressFieldEvents = false; }
                }
            }

            if (_modelCount != null)
                _modelCount.text = has ? Strings.Get("model.count", draft.Models.Count) : string.Empty;

            if (_editorHint != null)
            {
                if (!has)
                {
                    _editorHint.text = Strings.Get("model.provider.missing");
                    _editorHint.color = HintWarnColor;
                }
                else if (draft.Models.Count == 0)
                {
                    _editorHint.text = Strings.Get("model.none_hint");
                    _editorHint.color = HintWarnColor;
                }
                else if (_dirty)
                {
                    _editorHint.text = Strings.Get("model.hint.save");
                    _editorHint.color = HintColor;
                }
                else
                {
                    _editorHint.text = Strings.Get("model.hint.select");
                    _editorHint.color = DimColor;
                }
            }
        }

        private static void SelectProvider(string id)
        {
            _selectedProvider = id;
            RebuildProviderList();
            RebuildModelList();
            RefreshEditor(resetInputs: true);
        }

        private static void OnAddProvider()
        {
            var id = (Trim(_addProviderField) ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(id))
            {
                SetEditorHint(Strings.Get("model.provider.id_required"), true);
                return;
            }
            if (!IsValidProviderId(id))
            {
                SetEditorHint(Strings.Get("model.provider.id_invalid"), true);
                return;
            }
            foreach (var provider in _providers)
            {
                if (provider.Id != id) continue;
                SelectProvider(id);
                SetEditorHint(Strings.Get("model.provider.duplicate"), true);
                return;
            }

            var draft = new ProviderDraft
            {
                Id = id,
                Name = DisplayName(id),
                HasKey = false,
                BaseUrl = string.Empty,
            };
            _providers.Add(draft);
            _removed.Remove(id);
            _selectedProvider = id;
            _dirty = true;
            if (_addProviderField != null) _addProviderField.text = string.Empty;
            RebuildAll();
            SetEditorHint(Strings.Get("model.provider.added"), false);
        }

        private static void OnDeleteProvider()
        {
            var draft = SelectedDraft;
            if (draft == null) return;
            if (_providers.Count <= 1)
            {
                SetEditorHint(Strings.Get("model.provider.keep_one"), true);
                return;
            }

            _removed.Add(draft.Id);
            _providers.Remove(draft);
            if (_activeProvider == draft.Id)
            {
                _activeProvider = _providers[0].Id;
                _activeModel = _providers[0].Models.Count > 0 ? _providers[0].Models[0].Id : string.Empty;
            }
            _selectedProvider = _providers[0].Id;
            _dirty = true;
            RebuildAll();
            SetHint(Strings.Get("model.provider.deleted"), false);
        }

        private static void OnAddModel()
        {
            var draft = SelectedDraft;
            if (draft == null) return;

            var id = Trim(_addModelField);
            if (string.IsNullOrEmpty(id))
            {
                SetEditorHint(Strings.Get("model.id.required"), true);
                return;
            }
            foreach (var model in draft.Models)
            {
                if (model.Id != id) continue;
                SetEditorHint(Strings.Get("model.duplicate"), true);
                return;
            }

            draft.Models.Add(new ModelDraft { Id = id, Name = id });
            if (draft.Id == _activeProvider && string.IsNullOrEmpty(_activeModel))
                _activeModel = id;
            _dirty = true;
            if (_addModelField != null) _addModelField.text = string.Empty;
            RebuildModelList();
            RefreshEditor(resetInputs: false);
            SetEditorHint(Strings.Get("model.added"), false);
        }

        private static void RemoveModel(string providerId, string modelId)
        {
            ProviderDraft draft = null;
            foreach (var provider in _providers)
                if (provider.Id == providerId) draft = provider;
            if (draft == null) return;

            draft.Models.RemoveAll(model => model.Id == modelId);
            if (providerId == _activeProvider && modelId == _activeModel)
            {
                _activeModel = draft.Models.Count > 0 ? draft.Models[0].Id : string.Empty;
            }
            _dirty = true;
            RebuildModelList();
            RefreshEditor(resetInputs: false);
        }

        private static void SetActiveModel(string providerId, string modelId)
        {
            _activeProvider = providerId;
            _activeModel = modelId;
            foreach (var provider in _providers)
                provider.IsActive = provider.Id == providerId;
            _dirty = true;
            RebuildProviderList();
            RebuildModelList();
            RefreshEditor(resetInputs: false);
            SetHint(Strings.Get("model.active"), false);
        }

        private static void OnApiKeyChanged(string value)
        {
            if (_suppressFieldEvents) return;
            var draft = SelectedDraft;
            if (draft == null) return;
            draft.ApiKey = value ?? string.Empty;
            _dirty = true;
            if (_keyTestStatus != null)
            {
                _keyTestStatus.text = string.Empty;
                _keyTestStatus.color = DimColor;
            }
            if (_keyDot != null)
                _keyDot.color = draft.HasKey || !string.IsNullOrEmpty(draft.ApiKey)
                    ? KeyOkColor
                    : KeyMissingColor;
        }

        private static void OnBaseUrlChanged(string value)
        {
            if (_suppressFieldEvents) return;
            var draft = SelectedDraft;
            if (draft == null) return;
            draft.BaseUrl = value ?? string.Empty;
            _dirty = true;
            if (_keyTestStatus != null)
            {
                _keyTestStatus.text = string.Empty;
                _keyTestStatus.color = DimColor;
            }
        }

        private static void Save()
        {
            if (_client == null)
            {
                SetHint(Strings.Get("model.save.offline"), false);
                return;
            }
            if (_providers.Count == 0)
            {
                SetHint(Strings.Get("model.save.no_provider"), false);
                return;
            }

            foreach (var provider in _providers)
            {
                if (provider.Models.Count > 0) continue;
                _selectedProvider = provider.Id;
                SelectProvider(provider.Id);
                SetEditorHint(Strings.Get("model.save.provider_no_model", provider.Name), true);
                return;
            }
            foreach (var provider in _providers)
            {
                if (IsKnownProvider(provider.Id)) continue;
                if (!string.IsNullOrEmpty((provider.BaseUrl ?? string.Empty).Trim())) continue;
                _selectedProvider = provider.Id;
                SelectProvider(provider.Id);
                SetEditorHint(Strings.Get("model.save.provider_no_url", provider.Name), true);
                return;
            }

            if (string.IsNullOrEmpty(_activeProvider) || string.IsNullOrEmpty(_activeModel))
            {
                var first = _providers[0];
                _activeProvider = first.Id;
                _activeModel = first.Models.Count > 0 ? first.Models[0].Id : string.Empty;
            }

            var providerConfig = JsonValue.NewObject();
            foreach (var provider in _providers)
            {
                var entry = JsonValue.NewObject();
                var key = provider.ApiKey != null ? provider.ApiKey.Trim() : string.Empty;
                if (key.Length > 0) entry.Set("apiKey", key);
                entry.Set("baseUrl",
                    string.IsNullOrEmpty(provider.BaseUrl) ? JsonValue.Null : JsonValue.Of(provider.BaseUrl.Trim()));

                var models = JsonValue.NewArray();
                foreach (var model in provider.Models)
                {
                    var item = JsonValue.NewObject();
                    item.Set("id", model.Id);
                    item.Set("name", string.IsNullOrEmpty(model.Name) ? model.Id : model.Name);
                    models.Add(item);
                }
                entry.Set("models", models);
                providerConfig.Set(provider.Id, entry);
            }

            var remove = JsonValue.NewArray();
            foreach (var id in _removed) remove.Add(id);

            var envId = _client.SendConfigSet(
                null, null, null, null, null,
                providerConfig, remove, _activeProvider, _activeModel);

            if (string.IsNullOrEmpty(envId))
            {
                SetHint(Strings.Get("model.save.not_sent"), false);
                Plugin.Log.LogWarning("[模型页] 配置未发送（客户端未就绪）。");
                return;
            }

            foreach (var provider in _providers)
            {
                if (!string.IsNullOrEmpty(provider.ApiKey)) provider.HasKey = true;
                provider.ApiKey = string.Empty;
            }
            _suppressFieldEvents = true;
            try { if (_apiKeyField != null) _apiKeyField.text = string.Empty; }
            finally { _suppressFieldEvents = false; }
            _dirty = false;
            SetHint(Strings.Get("model.save.submitted"), true);
            Plugin.Log.LogInfo(
                $"[模型页] 已提交 {_providers.Count} 个供应商，当前 {_activeProvider}/{_activeModel}。");
        }

        private static void SeedDefaults()
        {
            if (_providers.Count > 0) return;
            var draft = new ProviderDraft
            {
                Id = DefaultProvider,
                Name = "DeepSeek",
                HasKey = false,
                BaseUrl = string.Empty,
            };
            draft.Models.Add(new ModelDraft { Id = "deepseek-v4-flash", Name = "DeepSeek V4 Flash" });
            draft.Models.Add(new ModelDraft { Id = "deepseek-v4-pro", Name = "DeepSeek V4 Pro" });
            _providers.Add(draft);
            _selectedProvider = DefaultProvider;
            _activeProvider = DefaultProvider;
            _activeModel = DefaultModel;
        }

        private static string DisplayName(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case "deepseek": return "DeepSeek";
                case "openai": return "OpenAI";
                case "anthropic": return "Anthropic";
                case "google": return "Google";
                case "openrouter": return "OpenRouter";
                case "xai": return "xAI";
                case "groq": return "Groq";
                case "mistral": return "Mistral";
                case "cerebras": return "Cerebras";
                case "moonshotai": return "Moonshot";
                case "minimax": return "MiniMax";
                case "zai": return "Z.ai";
                case "together": return "Together";
                case "fireworks": return "Fireworks";
                case "nvidia": return "NVIDIA";
                case "github-copilot": return "GitHub Copilot";
                default: return id;
            }
        }

        private static bool IsKnownProvider(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case "deepseek":
                case "openai":
                case "anthropic":
                case "google":
                case "google-vertex":
                case "amazon-bedrock":
                case "openrouter":
                case "xai":
                case "groq":
                case "mistral":
                case "cerebras":
                case "nvidia":
                case "together":
                case "fireworks":
                case "moonshotai":
                case "moonshotai-cn":
                case "minimax":
                case "minimax-cn":
                case "zai":
                case "zai-coding-cn":
                case "github-copilot":
                case "vercel-ai-gateway":
                case "openai-codex":
                case "azure-openai-responses":
                case "qwen":
                case "qwen-token-plan":
                case "huggingface":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsValidProviderId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var c in id)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                         || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        private static string Trim(InputField field)
        {
            return field != null && field.text != null ? field.text.Trim() : null;
        }

        private static void SetHint(string text, bool ok)
        {
            if (_hint == null) return;
            _hint.text = text;
            _hint.color = ok ? HintOkColor : HintWarnColor;
        }

        private static void SetEditorHint(string text, bool warn)
        {
            if (_editorHint == null) return;
            _editorHint.text = text;
            _editorHint.color = warn ? HintWarnColor : HintColor;
        }
    }
}
