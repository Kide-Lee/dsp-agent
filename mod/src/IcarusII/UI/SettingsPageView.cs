using System;
using System.Collections.Generic;
using IcarusII.I18n;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 「模型」设置页 prefab 的运行时句柄。
    ///
    /// 布局、配色、层级全部来自 `Assets/IcarusII/SettingsPage.prefab`；
    /// 这里只做三件事：实例化、按节点名取引用、把文案与字体套上去。
    /// 改布局请改 prefab，不要回到这里加节点。
    /// </summary>
    internal sealed class SettingsPageView
    {
        /// <summary>bundle 里的资源名（见 IcarusIIBundleBuilder 的 addressableNames）。</summary>
        public const string PrefabAssetName = "IcarusIISettingsPage";

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
            return view;
        }

        private void Bind(Transform root, List<string> missing)
        {
            ProviderList = Bind<RectTransform>(root, "page-body/provider-pane/provider-list/viewport/content", missing);
            ModelList = Bind<RectTransform>(root, "page-body/editor-pane/model-list/viewport/content", missing);
            PresetHost = Bind<RectTransform>(root, "page-body/provider-pane/provider-preset-row/preset-combo-host", missing);
            AddProviderField = Bind<InputField>(root, "page-body/provider-pane/add-provider-row/input", missing);
            AddModelField = Bind<InputField>(root, "page-body/editor-pane/add-model-row/input", missing);
            EditorTitle = Bind<Text>(root, "page-body/editor-pane/editor-head/title", missing);
            KeyDot = Bind<Image>(root, "page-body/editor-pane/editor-head/key-dot", missing);
            ApiKeyField = Bind<InputField>(root, "page-body/editor-pane/api-key-row/api-key-input", missing);
            BaseUrlField = Bind<InputField>(root, "page-body/editor-pane/base-url-row/base-url-input", missing);
            ModelCount = Bind<Text>(root, "page-body/editor-pane/models-head/count", missing);
            EditorHint = Bind<Text>(root, "page-body/editor-pane/editor-hint", missing);
            Hint = Bind<Text>(root, "hint-row/hint", missing);
            KeyTestStatus = Bind<Text>(root, "page-body/editor-pane/key-status", missing);

            TestKeyButton = Bind<Button>(root, "page-body/editor-pane/api-key-row/test-button", missing);
            TestKeyLabel = Bind<Text>(root, "page-body/editor-pane/api-key-row/test-button/label", missing);
            CatalogButton = Bind<Button>(root, "page-body/editor-pane/add-model-row/catalog-button", missing);
            CatalogLabel = Bind<Text>(root, "page-body/editor-pane/add-model-row/catalog-button/label", missing);
            AddProviderButton = Bind<Button>(root, "page-body/provider-pane/add-provider-row/button", missing);
            AddModelButton = Bind<Button>(root, "page-body/editor-pane/add-model-row/button", missing);
            DeleteProviderButton = Bind<Button>(root, "page-body/editor-pane/editor-head/delete-button", missing);
            CatalogCloseButton = Bind<Button>(root, "model-catalog-popup/catalog-head/catalog-close-button", missing);

            CatalogPopup = Bind<RectTransform>(root, "model-catalog-popup", missing);
            CatalogList = Bind<RectTransform>(root, "model-catalog-popup/catalog-list/viewport/content", missing);
            CatalogTitle = Bind<Text>(root, "model-catalog-popup/catalog-head/title", missing);
            CatalogHint = Bind<Text>(root, "model-catalog-popup/catalog-hint-row/hint", missing);

            ProviderRowTemplate = BindNode(root, "templates/provider-row", missing);
            ModelRowTemplate = BindNode(root, "templates/model-row", missing);
            CatalogRowTemplate = BindNode(root, "templates/catalog-row", missing);
            EmptyRowTemplate = BindNode(root, "templates/empty-row", missing);
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
        /// 文案同理走 Strings，prefab 里的中文仅方便在 Editor 里看布局。
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

            SetText("page-head/title", Strings.Get("model.page.title"));
            SetText("page-body/provider-pane/provider-head/label", Strings.Get("model.provider.title"));
            SetText("page-body/provider-pane/preset-label", Strings.Get("model.provider.preset"));
            SetText("page-body/editor-pane/editor-head/delete-button/label", Strings.Get("model.provider.delete"));
            SetText("page-body/editor-pane/api-key-row/label", Strings.Get("model.api_key.label"));
            SetText("page-body/editor-pane/api-key-row/test-button/label", Strings.Get("model.test.button"));
            SetText("page-body/editor-pane/base-url-row/label", Strings.Get("model.base_url.label"));
            SetText("page-body/editor-pane/models-head/label", Strings.Get("model.label"));
            SetText("page-body/editor-pane/add-model-row/catalog-button/label", Strings.Get("model.catalog.button"));

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
