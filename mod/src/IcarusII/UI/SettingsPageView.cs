using System;
using System.Collections.Generic;
using IcarusII.I18n;
using IcarusII.Common.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 设置页 prefab 的运行时句柄：左导航 + 六个分区。
    ///
    /// 布局、配色、层级全部来自 `Assets/IcarusII/SettingsPage.prefab`；
    /// 这里只做三件事：实例化、按节点名取引用、把文案与字体套上去。
    /// 分区切换只切 SetActive 与导航配色，不重建节点。
    /// </summary>
    internal sealed class SettingsPageView
    {
        /// <summary>bundle 里的资源名（见 IcarusIIBundleBuilder 的 addressableNames）。</summary>
        public const string PrefabAssetName = "IcarusIISettingsPage";

        public const string SectionGeneral = "general";
        public const string SectionKeys = "keys";
        public const string SectionModel = "model";
        public const string SectionSessions = "sessions";
        public const string SectionTools = "tools";
        public const string SectionAbout = "about";

        public RectTransform Root;
        public RectTransform ProviderList;
        public RectTransform ModelList;
        public RectTransform PresetHost;
        public InputField AddProviderField;
        public InputField AddModelField;
        public Text EditorTitle;
        public Image KeyDot;
        public InputField ApiKeyField;
        public InputField BaseUrlField;
        public Text ModelCount;
        public Text EditorHint;
        public Text Hint;
        public Text KeyTestStatus;
        public Button TestKeyButton;
        public Text TestKeyLabel;
        public Button CatalogButton;
        public Text CatalogLabel;
        public Button AddProviderButton;
        public Button AddModelButton;
        public Button DeleteProviderButton;
        public Button CatalogCloseButton;
        public RectTransform CatalogPopup;
        public RectTransform CatalogList;
        public Text CatalogTitle;
        public Text CatalogHint;
        public GameObject ProviderRowTemplate;
        public GameObject ModelRowTemplate;
        public GameObject CatalogRowTemplate;
        public GameObject EmptyRowTemplate;

        public Button PermissionReadonly;
        public Button PermissionLimited;
        public Button PermissionTakeover;
        public Button CopyDirButton;
        public Button CopyDiagnosticsButton;
        public Button CopyGroupButton;
        public Text DataDirValue;
        public Text PermissionStatus;

        /// <summary>玩家点了某个权限档位；参数是协议里的只读/受限/接管。</summary>
        public event Action<string> PermissionSelected;

        /// <summary>左导航的一个条目：按钮 + 强调条 + 文字 + 对应分区根。</summary>
        private sealed class NavEntry
        {
            public string Id;
            public Button Button;
            public Image Bar;
            public Text Label;
            public Image Background;
            public RectTransform Pane;
        }

        private readonly List<NavEntry> _nav = new List<NavEntry>();
        private string _current = SectionGeneral;

        /// <summary>当前显示的分区 id。</summary>
        public string CurrentSection => _current;

        private static readonly Color SelectedBg = new Color(0.353f, 0.749f, 1f, 0.13f);
        private static readonly Color SelectedLabel = new Color(1f, 1f, 1f, 1f);
        private static readonly Color IdleLabel = new Color(1f, 1f, 1f, 0.62f);
        private static readonly Color Accent = new Color(0.353f, 0.749f, 1f, 1f);
        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        /// <summary>
        /// 实例化 prefab 并绑定。prefab 缺失或缺节点时抛异常，由调用方回滚本次注入，
        /// 避免留下半套界面。
        /// </summary>
        public static SettingsPageView Create(Transform parent, Font font)
        {
            var prefab = WindowPrefab.LoadAsset<GameObject>(PrefabAssetName);
            if (prefab == null)
            {
                throw new InvalidOperationException(
                    $"bundle 里没有设置页 prefab（{PrefabAssetName}），请重新打 UI AssetBundle。");
            }

            var page = UnityEngine.Object.Instantiate(prefab, parent, false);
            page.name = "settings-page";
            var rt = page.GetComponent<RectTransform>();
            UiFactory.Stretch(rt);

            var view = new SettingsPageView { Root = rt };
            var missing = new List<string>();
            view.Bind(rt, missing);
            if (missing.Count > 0)
            {
                UnityEngine.Object.Destroy(page);
                throw new InvalidOperationException("设置页 prefab 缺节点：" + string.Join(", ", missing));
            }

            view.ApplyTexts(font);
            view.WireActions();
            view.Show(SectionGeneral);
            return view;
        }

        private void Bind(Transform root, List<string> missing)
        {
            var model = "content/pane-model";
            ProviderList = Bind<RectTransform>(root, model + "/page-body/provider-pane/provider-list/viewport/content", missing);
            ModelList = Bind<RectTransform>(root, model + "/page-body/editor-pane/model-list/viewport/content", missing);
            PresetHost = Bind<RectTransform>(root, model + "/page-body/provider-pane/provider-preset-row/preset-combo-host", missing);
            AddProviderField = Bind<InputField>(root, model + "/page-body/provider-pane/add-provider-row/input", missing);
            AddModelField = Bind<InputField>(root, model + "/page-body/editor-pane/add-model-row/input", missing);
            EditorTitle = Bind<Text>(root, model + "/page-body/editor-pane/editor-head/title", missing);
            KeyDot = Bind<Image>(root, model + "/page-body/editor-pane/editor-head/key-dot", missing);
            ApiKeyField = Bind<InputField>(root, model + "/page-body/editor-pane/api-key-row/api-key-input", missing);
            BaseUrlField = Bind<InputField>(root, model + "/page-body/editor-pane/base-url-row/base-url-input", missing);
            ModelCount = Bind<Text>(root, model + "/page-body/editor-pane/models-head/count", missing);
            EditorHint = Bind<Text>(root, model + "/page-body/editor-pane/editor-hint", missing);
            Hint = Bind<Text>(root, model + "/hint-row/hint", missing);
            KeyTestStatus = Bind<Text>(root, model + "/page-body/editor-pane/key-status", missing);

            TestKeyButton = Bind<Button>(root, model + "/page-body/editor-pane/api-key-row/test-button", missing);
            TestKeyLabel = Bind<Text>(root, model + "/page-body/editor-pane/api-key-row/test-button/label", missing);
            CatalogButton = Bind<Button>(root, model + "/page-body/editor-pane/add-model-row/catalog-button", missing);
            CatalogLabel = Bind<Text>(root, model + "/page-body/editor-pane/add-model-row/catalog-button/label", missing);
            AddProviderButton = Bind<Button>(root, model + "/page-body/provider-pane/add-provider-row/button", missing);
            AddModelButton = Bind<Button>(root, model + "/page-body/editor-pane/add-model-row/button", missing);
            DeleteProviderButton = Bind<Button>(root, model + "/page-body/editor-pane/editor-head/delete-button", missing);
            CatalogCloseButton = Bind<Button>(root, "model-catalog-popup/catalog-head/catalog-close-button", missing);

            CatalogPopup = Bind<RectTransform>(root, "model-catalog-popup", missing);
            CatalogList = Bind<RectTransform>(root, "model-catalog-popup/catalog-list/viewport/content", missing);
            CatalogTitle = Bind<Text>(root, "model-catalog-popup/catalog-head/title", missing);
            CatalogHint = Bind<Text>(root, "model-catalog-popup/catalog-hint-row/hint", missing);

            ProviderRowTemplate = BindNode(root, "templates/provider-row", missing);
            ModelRowTemplate = BindNode(root, "templates/model-row", missing);
            CatalogRowTemplate = BindNode(root, "templates/catalog-row", missing);
            EmptyRowTemplate = BindNode(root, "templates/empty-row", missing);

            var general = "content/pane-general/body/viewport/content/section/perm-row";
            PermissionReadonly = Bind<Button>(root, general + "/perm-readonly", missing);
            PermissionLimited = Bind<Button>(root, general + "/perm-limited", missing);
            PermissionTakeover = Bind<Button>(root, general + "/perm-takeover", missing);
            DataDirValue = Bind<Text>(root, "content/pane-general/body/viewport/content/section/version-card/kv-dir/path", missing);
            CopyDirButton = Bind<Button>(root, "content/pane-general/body/viewport/content/section/version-card/kv-dir/copy-dir-button", missing);
            CopyDiagnosticsButton = Bind<Button>(root, "content/pane-general/body/viewport/content/general-actions/copy-diagnostics-button", missing);
            CopyGroupButton = Bind<Button>(root, "content/pane-about/body/viewport/content/group-card/group-row/actions/copy-group-button", missing);
            PermissionStatus = Bind<Text>(root, "content/pane-general/body/viewport/content/section/head/permission-status", missing);

            AddNav(root, missing, SectionGeneral, "nav-general", "pane-general");
            AddNav(root, missing, SectionKeys, "nav-keys", "pane-keys");
            AddNav(root, missing, SectionModel, "nav-model", "pane-model");
            AddNav(root, missing, SectionSessions, "nav-sessions", "pane-sessions");
            AddNav(root, missing, SectionTools, "nav-tools", "pane-tools");
            AddNav(root, missing, SectionAbout, "nav-about", "pane-about");
        }

        private void AddNav(Transform root, List<string> missing, string id, string navName, string paneName)
        {
            var button = Bind<Button>(root, "nav/" + navName, missing);
            var bar = Bind<Image>(root, "nav/" + navName + "/bar", missing);
            var label = Bind<Text>(root, "nav/" + navName + "/label", missing);
            var pane = Bind<RectTransform>(root, "content/" + paneName, missing);
            if (button == null || pane == null) return;

            var background = button.targetGraphic as Image;
            if (background == null) background = button.GetComponent<Image>();
            if (background == null) missing.Add("nav/" + navName + "(Image)");

            _nav.Add(new NavEntry
            {
                Id = id,
                Button = button,
                Bar = bar,
                Label = label,
                Background = background,
                Pane = pane,
            });

            var target = id;
            button.onClick.AddListener(() => Show(target));
        }

        private void WireActions()
        {
            if (PermissionReadonly != null)
                PermissionReadonly.onClick.AddListener(() => SelectPermission(Protocol.PermissionReadonly));
            if (PermissionLimited != null)
                PermissionLimited.onClick.AddListener(() => SelectPermission(Protocol.PermissionLimited));
            if (PermissionTakeover != null)
                PermissionTakeover.onClick.AddListener(() => SelectPermission(Protocol.PermissionTakeover));

            if (CopyDirButton != null) CopyDirButton.onClick.AddListener(() => CopyText(DataDirValue != null ? DataDirValue.text : string.Empty));
            if (CopyDiagnosticsButton != null) CopyDiagnosticsButton.onClick.AddListener(() => CopyText(BuildDiagnostics()));
            if (CopyGroupButton != null) CopyGroupButton.onClick.AddListener(() => CopyText(GroupNumber));

            // 关于页的「复制链接」按按钮名映射到仓库地址。
            foreach (var button in Root.GetComponentsInChildren<Button>(true))
            {
                if (!button.gameObject.name.StartsWith("copy-", StringComparison.Ordinal)) continue;
                var key = button.gameObject.name.Substring("copy-".Length);
                if (CreditsLinks.TryGetValue(key, out var url))
                {
                    var target = url;
                    button.onClick.AddListener(() => CopyText(target));
                }
            }
        }

        /// <summary>点权限卡：先更新选中态，再让上层去落盘与发送。</summary>
        public void SelectPermission(string level)
        {
            SetPermissionLevel(level);
            PermissionSelected?.Invoke(level);
        }

        /// <summary>只更新三张卡的选中态，用于打开设置页时同步当前档位。</summary>
        public void SetPermissionLevel(string level)
        {
            TintPermission(PermissionReadonly, Protocol.PermissionReadonly, level);
            TintPermission(PermissionLimited, Protocol.PermissionLimited, level);
            TintPermission(PermissionTakeover, Protocol.PermissionTakeover, level);
            if (PermissionStatus != null)
            {
                PermissionStatus.text = Strings.Get("permission.status", PermissionName(level));
            }
        }

        private static string PermissionName(string level)
        {
            if (string.Equals(level, Protocol.PermissionReadonly, StringComparison.OrdinalIgnoreCase))
                return Strings.Get("permission.level.readonly");
            if (string.Equals(level, Protocol.PermissionTakeover, StringComparison.OrdinalIgnoreCase))
                return Strings.Get("permission.level.takeover");
            return Strings.Get("permission.level.limited");
        }

        private static void TintPermission(Button card, string id, string current)
        {
            if (card == null) return;
            var on = string.Equals(id, current, StringComparison.OrdinalIgnoreCase);
            if (card.targetGraphic != null) card.targetGraphic.color = on ? SelectedPermBg : Clear;
            var dot = card.transform.Find("radio");
            var dotImage = dot != null ? dot.GetComponent<Image>() : null;
            if (dotImage != null) dotImage.color = on ? Accent : IdleDot;
        }

        private static void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIUtility.systemCopyBuffer = text;
        }

        private static string BuildDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Icarus II 设置页 - 诊断信息");
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"游戏：{Application.version} / Unity {Application.unityVersion}");
            sb.AppendLine($"系统：{SystemInfo.operatingSystem}");
            sb.AppendLine($"数据目录：{(DataDirProvider != null ? DataDirProvider() : string.Empty)}");
            return sb.ToString();
        }

        /// <summary>由 ModelSettingsTab 注入，避免 UI 直接依赖 sidecar 启动器。</summary>
        public static Func<string> DataDirProvider;

        private const string GroupNumber = "1091497260";

        private static readonly Dictionary<string, string> CreditsLinks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "pi", "https://github.com/earendil-works/pi" },
            { "bepinex", "https://github.com/BepInEx/BepInEx" },
            { "node", "https://nodejs.org/" },
            { "ws", "https://github.com/websockets/ws" },
            { "typebox", "https://github.com/sinclairzx81/typebox" },
            { "quickjs", "https://github.com/quickjs-ng/quickjs" },
            { "dspcalc", "https://github.com/antian369/dsp-calc" },
            { "lucide", "https://lucide.dev/" },
            { "unityskills", "https://github.com/Unity-Technologies/skills" },
        };

        private static readonly Color SelectedPermBg = new Color(0.353f, 0.749f, 1f, 0.18f);
        private static readonly Color IdleDot = new Color(1f, 1f, 1f, 0.28f);

        /// <summary>切到某个分区：只切显隐与配色，不重建节点。</summary>
        public void Show(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var exists = false;
            foreach (var entry in _nav)
            {
                if (entry.Id == id) exists = true;
            }
            if (!exists) return;

            _current = id;
            foreach (var entry in _nav)
            {
                var on = entry.Id == id;
                if (entry.Pane != null) entry.Pane.gameObject.SetActive(on);
                if (entry.Background != null) entry.Background.color = on ? SelectedBg : Clear;
                if (entry.Bar != null) entry.Bar.color = on ? Accent : Clear;
                if (entry.Label != null) entry.Label.color = on ? SelectedLabel : IdleLabel;
            }
        }

        private static GameObject BindNode(Transform root, string path, List<string> missing)
        {
            var node = root.Find(path);
            if (node == null)
            {
                missing.Add(path);
                return null;
            }
            return node.gameObject;
        }

        private static T Bind<T>(Transform root, string path, List<string> missing) where T : Component
        {
            var node = root.Find(path);
            if (node == null)
            {
                missing.Add(path);
                return null;
            }
            var component = node.GetComponent<T>();
            if (component == null)
            {
                missing.Add($"{path}({typeof(T).Name})");
            }
            return component;
        }

        /// <summary>
        /// 字体由运行期按语言解析，prefab 里只放设计期字体；
        /// 文案同理走 Strings。
        /// </summary>
        private void ApplyTexts(Font font)
        {
            if (font != null)
            {
                foreach (var text in Root.GetComponentsInChildren<Text>(true))
                {
                    text.font = font;
                }
            }

            var model = "content/pane-model";
            SetText(model + "/page-body/provider-pane/provider-head/label", Strings.Get("model.provider.title"));
            SetText(model + "/page-body/provider-pane/preset-label", Strings.Get("model.provider.preset"));
            SetText(model + "/page-body/editor-pane/editor-head/delete-button/label", Strings.Get("model.provider.delete"));
            SetText(model + "/page-body/editor-pane/api-key-row/label", Strings.Get("model.api_key.label"));
            SetText(model + "/page-body/editor-pane/api-key-row/test-button/label", Strings.Get("model.test.button"));
            SetText(model + "/page-body/editor-pane/base-url-row/label", Strings.Get("model.base_url.label"));
            SetText(model + "/page-body/editor-pane/models-head/label", Strings.Get("model.label"));
            SetText(model + "/page-body/editor-pane/add-model-row/catalog-button/label", Strings.Get("model.catalog.button"));

            SetPlaceholder(AddProviderField, Strings.Get("model.provider.id.placeholder"));
            SetPlaceholder(AddModelField, Strings.Get("model.id.placeholder"));
            SetPlaceholder(ApiKeyField, Strings.Get("model.api_key.placeholder"));
            SetPlaceholder(BaseUrlField, Strings.Get("model.base_url.placeholder"));
        }

        private void SetText(string path, string text)
        {
            var node = Root.Find(path);
            var label = node != null ? node.GetComponent<Text>() : null;
            if (label != null) label.text = text;
        }

        private static void SetPlaceholder(InputField field, string text)
        {
            if (field != null && field.placeholder is Text placeholder) placeholder.text = text;
        }
    }
}
