using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 一次性工具：把设置页 prefab 重排成「左导航 + 六个分区」。
    ///
    /// 字号与配色**照抄现有设置页**（改造前的 SettingsPage）：
    ///   页标题 18 / 分区与字段标签 15 / 说明 13 / 计数徽标 12；
    ///   正文 #FFFFFFC8、弱化 #FFFFFF94、按钮底 #00000061、卡片底 #00000029。
    /// 不要再用 11px 这类自定义尺度——放在原生窗口里会明显小一号。
    ///
    /// 幂等：重跑会先清掉旧的 `shell`，把 page-body / hint-row 抢回根节点再删，
    /// 所以不会把模型页弄丢。
    /// </summary>
    public static class IcarusIISettingsPageBuilder
    {
        private const string PrefabPath = "Assets/IcarusII/SettingsPage.prefab";
        private const string QrTexturePath = "Assets/IcarusII/Resources/qq-group.png";
        private const float QrWidth = 220f;
        private const float QrTextureWidth = 1284f;
        private const float QrTextureHeight = 2286f;

        private const float NavWidth = 190f;
                private const float FootHeight = 38f;
        private const float PadX = 22f;
        private const float RowHeight = 40f;

        // ---- 字号（与旧设置页一致）
        private const int TitleSize = 18;
        private const int SectionSize = 15;
        private const int LabelSize = 15;
        private const int HintSize = 13;
        private const int SmallSize = 12;
        private const int ButtonSize = 14;

        // ---- 配色（取自旧设置页与原生窗口）
        private static readonly Color LabelColor = new Color(1f, 1f, 1f, 200f / 255f);   // #FFFFFFC8
        private static readonly Color DimColor = new Color(1f, 1f, 1f, 0x94 / 255f);     // #FFFFFF94
        private static readonly Color SmallColor = new Color(1f, 1f, 1f, 0x6B / 255f);
        private static readonly Color HintColor = new Color(1f, 1f, 1f, 0xAF / 255f);
        private static readonly Color AccentColor = new Color(0.353f, 0.749f, 1f, 1f);
        private static readonly Color SelectedBg = new Color(0.353f, 0.749f, 1f, 0.13f);
        private static readonly Color CardBg = new Color(0f, 0f, 0f, 0x29 / 255f);
        private static readonly Color RowBg = new Color(1f, 1f, 1f, 0x0A / 255f);
        private static readonly Color InputBg = new Color(0f, 0f, 0f, 0x6E / 255f);
        private static readonly Color ButtonBg = new Color(0f, 0f, 0f, 0x61 / 255f);
        private static readonly Color FootBg = new Color(0f, 0f, 0f, 0x24 / 255f);
        private static readonly Color NavBg = new Color(0f, 0f, 0f, 0.12f);
        private static readonly Color Separator = new Color(1f, 1f, 1f, 0.055f);
        private static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);
        private static readonly Color OkColor = new Color(0.451f, 0.902f, 0.549f, 1f);

        [MenuItem("Tools/Icarus II/重建设置页骨架")]
        public static void BuildFromMenu()
        {
            Build();
        }

        public static void Build()
        {
            var root = OpenStage();
            if (root == null)
            {
                Debug.LogError("[IcarusII] 打不开设置页 prefab。");
                return;
            }

            var rootRt = root.GetComponent<RectTransform>();
            var oldLayout = root.GetComponent<VerticalLayoutGroup>();
            if (oldLayout != null) Object.DestroyImmediate(oldLayout);

            var pageBody = FindDeep(root.transform, "page-body") as RectTransform;
            var hintRow = FindDeep(root.transform, "hint-row") as RectTransform;
            var pageHead = FindDeep(root.transform, "page-head");
            var shell = FindDeep(root.transform, "shell");
            var templates = FindDeep(root.transform, "templates");
            // 先把模型页与状态行抢回根节点，**再**删旧的 nav/content/shell——
            // 上一版把 page-body 放在 content/pane-model 下，删 content 会把它一起带走（踩过）。
            if (pageBody != null) pageBody.SetParent(root.transform, false);
            if (hintRow != null) hintRow.SetParent(root.transform, false);
            if (shell != null) Object.DestroyImmediate(shell.gameObject);
            // 重跑时清掉上一次生成的模板，避免同一模板堆叠多份。
            if (templates != null)
            {
                for (var i = templates.childCount - 1; i >= 0; i--)
                {
                    var name = templates.GetChild(i).name;
                    if (name == "conv-group" || name == "conv-row" || name == "tool-row")
                    {
                        Object.DestroyImmediate(templates.GetChild(i).gameObject);
                    }
                }
            }

            // 上一版把 nav / content 直接挂在根下（不是 shell 的子节点），重跑前也要清掉，
            // 否则会堆出第二套导航。
            foreach (var stale in new[] { "nav", "content" })
            {
                var existing = root.transform.Find(stale);
                if (existing != null) Object.DestroyImmediate(existing.gameObject);
            }

            BuildNav(rootRt);
            var content = BuildContent(rootRt);
            BuildGeneralPane(content);
            BuildKeysPane(content);
            BuildModelPane(content, pageBody, hintRow);
            BuildSessionsPane(content, templates);
            BuildToolsPane(content, templates);
            BuildAboutPane(content);

            if (pageHead != null) Object.DestroyImmediate(pageHead.gameObject);

            ActivateOnly(content, "pane-general");

            EditorUtility.SetDirty(root);
            bool saved;
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out saved);
            Debug.Log("[IcarusII] 设置页骨架已重建：" + PrefabPath + " saved=" + saved);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------ 骨架

        private static GameObject OpenStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null && stage.assetPath == PrefabPath)
            {
                return stage.prefabContentsRoot;
            }
            AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null ? stage.prefabContentsRoot : null;
        }

        private static void BuildNav(RectTransform root)
        {
            var nav = Node("nav", root);
            nav.anchorMin = new Vector2(0f, 0f);
            nav.anchorMax = new Vector2(0f, 1f);
            nav.pivot = new Vector2(0f, 0.5f);
            nav.offsetMin = Vector2.zero;
            nav.offsetMax = new Vector2(NavWidth, 0f);
            AddImage(nav, NavBg);

            var vlg = nav.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 10, 10);
            vlg.spacing = 2f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            AddNavItem(nav, "nav-general", "通用");
            AddNavItem(nav, "nav-keys", "快捷键");
            AddNavItem(nav, "nav-model", "模型");
            AddNavItem(nav, "nav-sessions", "会话");
            AddNavItem(nav, "nav-tools", "工具");
            AddNavItem(nav, "nav-about", "关于");
        }

        private static void AddNavItem(RectTransform nav, string name, string label)
        {
            var item = Node(name, nav);
            item.gameObject.AddComponent<LayoutElement>().preferredHeight = RowHeight;
            var bg = AddImage(item, Transparent);
            var button = item.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;

            var bar = Node("bar", item);
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(0f, 1f);
            bar.pivot = new Vector2(0f, 0.5f);
            bar.offsetMin = new Vector2(0f, 8f);
            bar.offsetMax = new Vector2(2f, -8f);
            AddImage(bar, Transparent);

            var text = AddLabel(item, label, LabelSize, LabelColor, TextAnchor.MiddleLeft);
            Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(16f, 0f);
        }

        private static RectTransform BuildContent(RectTransform root)
        {
            var content = Node("content", root);
            Stretch(content);
            content.offsetMin = new Vector2(NavWidth, 0f);
            return content;
        }

        // ------------------------------------------------------------ 分区外壳

        private static RectTransform CreatePane(RectTransform content, string name)
        {
            var pane = Node(name, content);
            Stretch(pane);
            var vlg = pane.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 0f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            // 分区标题/副标题不再显示：左导航已经标出当前位置，正文直接从分区内容开始。
            return pane;
        }

        private static RectTransform ScrollBody(RectTransform pane, float topPadding, float spacing)
        {
            var body = Node("body", pane);
            var le = body.gameObject.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;
            le.minHeight = 200f;
            AddImage(body, Transparent);

            var scroll = body.gameObject.AddComponent<ScrollRect>();
            var viewport = Node("viewport", body);
            Stretch(viewport);
            AddImage(viewport, Transparent);

            var content = Node("content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset((int)PadX, (int)PadX, (int)topPadding, 20);
            vlg.spacing = spacing;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            return content;
        }

        private static RectTransform StackBody(RectTransform pane, float topPadding, float spacing)
        {
            var body = Node("body", pane);
            body.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
            var vlg = body.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset((int)PadX, (int)PadX, (int)topPadding, 16);
            vlg.spacing = spacing;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;
            return body;
        }

        // ------------------------------------------------------------ 通用

        private static void BuildGeneralPane(RectTransform content)
        {
            var pane = CreatePane(content, "pane-general");
            var body = ScrollBody(pane, 12f, 18f);

            var perms = Section(body, "默认权限", "permission-status", "当前：受限");
            var row = Node("perm-row", perms);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 104f;
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            AddPermissionCard(row, "perm-readonly", "只读", "所有写操作都要你确认，模型只能看。");
            AddPermissionCard(row, "perm-limited", "受限", "低风险写操作直接放行，改变存档的操作仍需确认。");
            AddPermissionCard(row, "perm-takeover", "接管", "所有写操作直接执行，不再询问。");

            AddNote(perms, "perm-note",
                "与聊天窗右下角的「权限」下拉是同一个值；回答进行中修改，从下一回合开始生效。");

            var version = Section(body, "版本");
            var card = AddCard(version, "version-card");
            var vlg = card.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 0f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            AddKvRow(card, "kv-plugin", "插件", "Icarus II 1.0.0", "BepInEx", "5.4.17", true);
            AddKvRow(card, "kv-sidecar", "sidecar", "1.0.0", "协议", "v1", true);
            AddKvRow(card, "kv-game", "游戏", "0.10.30.3100", "Unity", "2022.3.62f3c1", true);

            var dirRow = AddRow(card, "kv-dir", RowHeight);
            AddKvLabel(dirRow, "数据目录", 72f);
            var dirValue = AddKvValue(dirRow, "%APPDATA%\\icarus-ii");
            dirValue.gameObject.name = "path";
            AddButton(dirRow, "copy-dir-button", "复制路径", 96f, 28f);

            var actions = Node("general-actions", body);
            actions.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;
            var ahlg = actions.gameObject.AddComponent<HorizontalLayoutGroup>();
            ahlg.spacing = 8f;
            ahlg.childControlWidth = true;
            ahlg.childControlHeight = true;
            ahlg.childForceExpandWidth = false;
            ahlg.childForceExpandHeight = true;
            AddButton(actions, "copy-diagnostics-button", "复制诊断信息", 140f, 28f);
            var actionsNote = AddLabel(actions, "提问题时把诊断信息一起发出来最快", HintSize, SmallColor, TextAnchor.MiddleRight);
            var actionsLe = actionsNote.gameObject.AddComponent<LayoutElement>();
            actionsLe.flexibleWidth = 1f;
            actionsLe.preferredWidth = 10f;
        }

        private static void AddPermissionCard(RectTransform row, string name, string title, string desc)
        {
            var card = Node(name, row);
            var le = card.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minHeight = 104f;
            var bg = AddImage(card, CardBg);
            var button = card.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;

            // 卡片内容用绝对定位：以前用「标题行 HLG + 说明 VLG」嵌套，
            // 标题被压成了 0 宽，游戏里只剩圆点和说明。
            var dot = Node("radio", card);
            Place(dot, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -12f), new Vector2(10f, 10f), new Vector2(0f, 1f));
            AddImage(dot, new Color(1f, 1f, 1f, 0.28f));

            var titleText = AddLabel(card, title, LabelSize, LabelColor, TextAnchor.MiddleLeft);
            titleText.gameObject.name = "title";
            Place(titleText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(30f, -4f), new Vector2(-42f, 24f), new Vector2(0f, 1f));

            var descText = AddLabel(card, desc, HintSize, DimColor, TextAnchor.UpperLeft);
            descText.horizontalOverflow = HorizontalWrapMode.Wrap;
            Place(descText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -34f), new Vector2(-24f, 56f), new Vector2(0f, 1f));
        }

        private static void AddKvRow(RectTransform card, string name, string k1, string v1, string k2, string v2, bool separator)
        {
            var row = AddRow(card, name, RowHeight);
            if (separator) AddSeparator(row);
            AddKvLabel(row, k1, 72f);
            AddKvValue(row, v1);
            AddKvLabel(row, k2, 64f);
            AddKvValue(row, v2);
        }

        private static RectTransform AddRow(RectTransform card, string name, float height)
        {
            var row = Node(name, card);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(14, 14, 0, 0);
            hlg.spacing = 10f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            return row;
        }

        private static void AddSeparator(RectTransform row)
        {
            var sep = Node("separator", row);
            sep.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            sep.anchorMin = new Vector2(0f, 0f);
            sep.anchorMax = new Vector2(1f, 0f);
            sep.pivot = new Vector2(0.5f, 0f);
            sep.offsetMin = Vector2.zero;
            sep.offsetMax = new Vector2(0f, 1f);
            AddImage(sep, Separator);
        }

        private static void AddKvLabel(RectTransform row, string text, float width)
        {
            var label = AddLabel(row, text, HintSize, DimColor, TextAnchor.MiddleLeft);
            var le = label.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.flexibleWidth = 0f;
        }

        private static Text AddKvValue(RectTransform row, string text)
        {
            var value = AddLabel(row, text, LabelSize, LabelColor, TextAnchor.MiddleLeft);
            var le = value.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.preferredWidth = 10f;
            return value;
        }

        // ------------------------------------------------------------ 快捷键

        // 以下数值来自 DSPGame.unity 里 UIKeyEntry 模板的实测值：
        // 行高 30、行距 36；根节点就是功能名文本（18px）；按键列 450、设置按键列 800。
        private const float KeyRowHeight = 30f;
        private const float KeyColName = 30f;
        private const float KeyColKey = 450f;
        private const float KeyColInput = 800f;
        private const float KeyColDefault = 930f;
        private const float KeyColClear = 1020f;

        private static readonly Color KeyInputBg = new Color(0x7C / 255f, 0xA6 / 255f, 0xBE / 255f, 0x77 / 255f);
        private static readonly Color KeyAuxText = new Color(0x96 / 255f, 0x96 / 255f, 0x96 / 255f, 1f);
        private static readonly Color KeyConflictText = new Color(1f, 1f, 1f, 0xC7 / 255f);

        private static void BuildKeysPane(RectTransform content)
        {
            var pane = CreatePane(content, "pane-keys");
            var body = StackBody(pane, 6f, 0f);

            var table = Node("key-table", body);
            table.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
            var vlg = table.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            AddKeyTableHeader(table);
            AddKeyTableRow(table, "row-open", "打开 / 关闭聊天窗", "R");
            AddKeyTableRow(table, "row-send", "发送消息", "Enter");
            AddKeyTableRow(table, "row-newline", "换行", "Ctrl + Enter");

            var footer = Node("keys-footer", body);
            footer.gameObject.AddComponent<LayoutElement>().preferredHeight = 40f;
            var fhlg = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            fhlg.spacing = 8f;
            fhlg.childControlWidth = true;
            fhlg.childControlHeight = true;
            fhlg.childForceExpandWidth = false;
            fhlg.childForceExpandHeight = false;
            fhlg.childAlignment = TextAnchor.MiddleLeft;
            AddButton(footer, "reset-button", "恢复本页设置", 150f, 30f);
        }

        private static void AddKeyTableHeader(RectTransform table)
        {
            var row = Node("key-head", table);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = KeyRowHeight;
            AddColumnLabel(row, "功能", KeyColName, 200f);
            AddColumnLabel(row, "按键", KeyColKey, 200f);
            AddColumnLabel(row, "设置按键", KeyColInput, 200f);
        }

        private static void AddColumnLabel(RectTransform row, string text, float x, float width)
        {
            var label = AddLabel(row, text, HintSize, DimColor, TextAnchor.MiddleLeft);
            Place(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(x, 0f), new Vector2(width, KeyRowHeight), new Vector2(0f, 0.5f));
        }

        private static void AddKeyTableRow(RectTransform table, string name, string label, string key)
        {
            var row = Node(name, table);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = KeyRowHeight;

            var nameText = AddLabel(row, label, TitleSize, LabelColor, TextAnchor.MiddleLeft);
            Place(nameText.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(KeyColName, 0f), new Vector2(400f, KeyRowHeight), new Vector2(0f, 0.5f));

            var keyText = AddLabel(row, key, TitleSize, LabelColor, TextAnchor.MiddleLeft);
            keyText.gameObject.name = "key-label";
            Place(keyText.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(KeyColKey, 0f), new Vector2(200f, KeyRowHeight), new Vector2(0f, 0.5f));

            // 设置按键列：和游戏一样是一块浅色输入区，捕获时在区里显示「等待按键响应」。
            var input = Node("input", row);
            Place(input, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(KeyColInput, 0f), new Vector2(120f, KeyRowHeight), new Vector2(0f, 0.5f));
            var inputImage = AddImage(input, KeyInputBg);
            var inputButton = input.gameObject.AddComponent<Button>();
            inputButton.targetGraphic = inputImage;
            inputButton.transition = Selectable.Transition.ColorTint;

            var waiting = AddLabel(input, "等待按键响应", 14, KeyAuxText, TextAnchor.MiddleLeft);
            waiting.gameObject.name = "waiting-text";
            Stretch(waiting.rectTransform, 6f, 2f, 6f, 2f);
            waiting.gameObject.SetActive(false);

            AddPlacedButton(row, "default-button", "设为默认", new Vector2(KeyColDefault, 0f), 80f, KeyRowHeight, 14, KeyAuxText);
            AddPlacedButton(row, "clear-button", "取消按键", new Vector2(KeyColClear, 0f), 80f, KeyRowHeight, 14, KeyAuxText);

            var conflict = AddLabel(row, string.Empty, 14, KeyConflictText, TextAnchor.MiddleRight);
            conflict.gameObject.name = "conflict-text";
            Place(conflict.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-14f, 0f), new Vector2(230f, KeyRowHeight), new Vector2(1f, 0.5f));
        }

        private static Button AddPlacedButton(RectTransform parent, string name, string label, Vector2 position,
            float width, float height, int fontSize, Color textColor)
        {
            var rt = Node(name, parent);
            Place(rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), position, new Vector2(width, height), new Vector2(0f, 0.5f));
            var img = AddImage(rt, Transparent);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            button.transition = Selectable.Transition.ColorTint;

            var text = AddLabel(rt, label, fontSize, textColor, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
            return button;
        }

        // ------------------------------------------------------------ 模型

        private static void BuildModelPane(RectTransform content, RectTransform pageBody, RectTransform hintRow)
        {
            var pane = CreatePane(content, "pane-model");
            if (pageBody != null)
            {
                pageBody.SetParent(pane, false);
                Stretch(pageBody);
                var le = pageBody.GetComponent<LayoutElement>();
                if (le == null) le = pageBody.gameObject.AddComponent<LayoutElement>();
                le.minHeight = 260f;
                le.flexibleHeight = 1f;
            }
            if (hintRow != null)
            {
                hintRow.SetParent(pane, false);
                var le = hintRow.GetComponent<LayoutElement>();
                if (le == null) le = hintRow.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 32f;
                le.minHeight = 32f;
                le.flexibleHeight = 0f;
                var hintText = hintRow.GetComponentInChildren<Text>(true);
                if (hintText != null) hintText.fontSize = HintSize;
            }
        }

        // ------------------------------------------------------------ 会话

        private static void BuildSessionsPane(RectTransform content, Transform templates)
        {
            var pane = CreatePane(content, "pane-sessions");
            var body = StackBody(pane, 12f, 10f);

            var toolbar = Node("toolbar", body);
            // 固定高度必须显式写 flexibleHeight=0：
            // 工具栏里的控件是 forceExpandHeight，flexible 会顺着布局组传上来，
            // 不写 0 的话它会被撑成半个 body（踩过）。
            var toolbarLe = toolbar.gameObject.AddComponent<LayoutElement>();
            toolbarLe.preferredHeight = 32f;
            toolbarLe.minHeight = 32f;
            toolbarLe.flexibleHeight = 0f;
            var hlg = toolbar.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            AddInput(toolbar, "search", "搜索会话标题", 240f, 30f);
            AddButton(toolbar, "only-current", "只看当前存档", 128f, 30f);
            var spacer = Node("spacer", toolbar);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var count = AddLabel(toolbar, "18 个会话 · 3 个存档", HintSize, SmallColor, TextAnchor.MiddleRight);
            count.gameObject.AddComponent<LayoutElement>().preferredWidth = 200f;

            var list = ScrollList(body, "list");
            var empty = AddLabel(list, "还没有会话", LabelSize, SmallColor, TextAnchor.MiddleCenter);
            empty.gameObject.name = "empty";
            empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 48f;

            if (templates != null)
            {
                BuildConvGroupTemplate(templates);
                BuildConvRowTemplate(templates);
            }
        }

        private static void BuildConvGroupTemplate(Transform templates)
        {
            var group = Node("conv-group", templates as RectTransform);
            group.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;
            AddImage(group, RowBg);

            var hlg = group.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(10, 10, 0, 0);
            hlg.spacing = 8f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var name = AddLabel(group, "我的档", LabelSize, LabelColor, TextAnchor.MiddleLeft);
            name.gameObject.name = "name";
            name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var key = AddLabel(group, "3f2a91c4", SmallSize, SmallColor, TextAnchor.MiddleLeft);
            key.gameObject.name = "save-key";
            key.gameObject.AddComponent<LayoutElement>().preferredWidth = 110f;
            var meta = AddLabel(group, "3 个会话 · 10-31 14:02", SmallSize, SmallColor, TextAnchor.MiddleRight);
            meta.gameObject.name = "meta";
            meta.gameObject.AddComponent<LayoutElement>().preferredWidth = 190f;
        }

        private static void BuildConvRowTemplate(Transform templates)
        {
            var row = Node("conv-row", templates as RectTransform);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = RowHeight;
            AddImage(row, RowBg);

            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(10, 10, 0, 0);
            hlg.spacing = 8f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var title = AddLabel(row, "铁矿产线怎么优化", LabelSize, LabelColor, TextAnchor.MiddleLeft);
            title.gameObject.name = "title";
            title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var meta = AddLabel(row, "12 条 · 14:02", SmallSize, SmallColor, TextAnchor.MiddleRight);
            meta.gameObject.name = "meta";
            meta.gameObject.AddComponent<LayoutElement>().preferredWidth = 120f;
            AddButton(row, "export-button", "导出", 64f, 26f);
            AddButton(row, "rename-button", "重命名", 76f, 26f);
            AddButton(row, "delete-button", "删除", 64f, 26f);
        }

        // ------------------------------------------------------------ 工具

        private static void BuildToolsPane(RectTransform content, Transform templates)
        {
            var pane = CreatePane(content, "pane-tools");
            var body = StackBody(pane, 12f, 8f);

            var toolbar = Node("toolbar", body);
            var toolbarLe = toolbar.gameObject.AddComponent<LayoutElement>();
            toolbarLe.preferredHeight = 30f;
            toolbarLe.minHeight = 30f;
            toolbarLe.flexibleHeight = 0f;
            var hlg = toolbar.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 6f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            AddInput(toolbar, "search", "搜索工具名或 id", 210f, 30f);
            AddButton(toolbar, "chip-all", "全部", 56f, 28f);
            AddButton(toolbar, "chip-read", "读取", 64f, 28f);
            AddButton(toolbar, "chip-write", "写入", 64f, 28f);
            AddButton(toolbar, "chip-third", "第三方", 76f, 28f);
            var spacer = Node("spacer", toolbar);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            AddButton(toolbar, "enable-all", "全部启用", 96f, 28f);

            var legend = AddLabel(body,
                "标记表示该工具在哪个权限档位下会被直接放行：只读 = 任何档位都执行，受限 = 受限/接管档位直接执行，接管 = 只有接管档位直接执行。",
                HintSize, SmallColor, TextAnchor.MiddleLeft);
            legend.horizontalOverflow = HorizontalWrapMode.Wrap;
            legend.gameObject.AddComponent<LayoutElement>().preferredHeight = 36f;

            var list = ScrollList(body, "list");
            var empty = AddLabel(list, "还没有工具数据", LabelSize, SmallColor, TextAnchor.MiddleCenter);
            empty.gameObject.name = "empty";
            empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 48f;

            if (templates != null) BuildToolRowTemplate(templates);
        }

        private static void BuildToolRowTemplate(Transform templates)
        {
            var row = Node("tool-row", templates as RectTransform);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 46f;
            AddImage(row, RowBg);

            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(10, 10, 0, 0);
            hlg.spacing = 8f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var info = Node("info", row);
            info.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var infoVlg = info.gameObject.AddComponent<VerticalLayoutGroup>();
            infoVlg.spacing = 2f;
            infoVlg.childControlWidth = true;
            infoVlg.childControlHeight = true;
            infoVlg.childForceExpandWidth = true;
            infoVlg.childForceExpandHeight = false;
            infoVlg.childAlignment = TextAnchor.MiddleLeft;

            var nameRow = Node("name-row", info);
            nameRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;
            var nhlg = nameRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            nhlg.spacing = 8f;
            nhlg.childControlWidth = true;
            nhlg.childControlHeight = true;
            nhlg.childForceExpandWidth = false;
            nhlg.childForceExpandHeight = false;

            var dot = Node("dot", nameRow);
            var dotLe = dot.gameObject.AddComponent<LayoutElement>();
            dotLe.preferredWidth = 8f;
            dotLe.preferredHeight = 8f;
            AddImage(dot, OkColor);

            var name = AddLabel(nameRow, "游戏总览", LabelSize, LabelColor, TextAnchor.MiddleLeft);
            name.gameObject.name = "name";
            name.gameObject.AddComponent<LayoutElement>().preferredWidth = 150f;

            var badge = AddLabel(nameRow, "只读", SmallSize, AccentColor, TextAnchor.MiddleLeft);
            badge.gameObject.name = "access";
            badge.gameObject.AddComponent<LayoutElement>().preferredWidth = 60f;

            var id = AddLabel(info, "game_get_overview", SmallSize, SmallColor, TextAnchor.MiddleLeft);
            id.gameObject.name = "id";
            id.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;

            var state = AddLabel(row, "开", HintSize, DimColor, TextAnchor.MiddleRight);
            state.gameObject.name = "state";
            state.gameObject.AddComponent<LayoutElement>().preferredWidth = 32f;

            var toggle = Node("toggle", row);
            var toggleLe = toggle.gameObject.AddComponent<LayoutElement>();
            toggleLe.preferredWidth = 56f;
            toggleLe.preferredHeight = 26f;
            AddImage(toggle, ButtonBg);
            var toggleText = AddLabel(toggle, "开关", SmallSize, LabelColor, TextAnchor.MiddleCenter);
            Stretch(toggleText.rectTransform);
        }

        // ------------------------------------------------------------ 关于

        private static void BuildAboutPane(RectTransform content)
        {
            var pane = CreatePane(content, "pane-about");
            var body = ScrollBody(pane, 16f, 14f);

            var license = AddCard(body, "license-card");
            CardVlg(license, 14, 12, 6);
            AddFixedLabel(license, "title", "Icarus II", SectionSize, LabelColor, 22f);
            AddFixedLabel(license, "license", "MIT License · © 2026 icarus-ii contributors", HintSize, DimColor, 20f);
            AddFixedLabel(license, "no-assets",
                "本项目不包含《戴森球计划》的任何游戏资源或程序集，需要正版游戏与 BepInEx。", HintSize, DimColor, 40f);

            var credits = AddCard(body, "credits-card");
            CardVlg(credits, 14, 10, 0);
            AddFixedLabel(credits, "title", "引用的项目", SectionSize, LabelColor, 24f);
            AddCreditRow(credits, "pi", "pi", "Agent 运行时", "MIT", "https://github.com/earendil-works/pi");
            AddCreditRow(credits, "bepinex", "BepInEx", "mod 加载器（不随包分发）", "LGPL-2.1", "https://github.com/BepInEx/BepInEx");
            AddCreditRow(credits, "node", "Node.js", "sidecar 运行时（内嵌）", "MIT", "https://nodejs.org/");
            AddCreditRow(credits, "ws", "ws", "WebSocket 服务端", "MIT", "https://github.com/websockets/ws");
            AddCreditRow(credits, "typebox", "typebox", "工具参数 Schema", "MIT", "https://github.com/sinclairzx81/typebox");
            AddCreditRow(credits, "quickjs", "QuickJS", "run_script 沙箱", "MIT", "https://github.com/quickjs-ng/quickjs");
            AddCreditRow(credits, "dspcalc", "dsp-calc", "蓝图布局算法与数据", "Mulan PSL v2", "https://github.com/antian369/dsp-calc");
            AddCreditRow(credits, "lucide", "Lucide", "部分界面图标", "ISC", "https://lucide.dev/");
            AddCreditRow(credits, "unityskills", "Unity Skills", "开发期 uGUI 规范", "Unity Companion", "https://github.com/Unity-Technologies/skills");
            AddFixedLabel(credits, "more", "完整清单见仓库 THIRD-PARTY.md", SmallSize, SmallColor, 20f);

            var group = AddCard(body, "group-card");
            var groupVlg = CardVlg(group, 14, 12, 10);
            groupVlg.childAlignment = TextAnchor.UpperCenter;
            // 不能让它按容器宽度拉伸，否则二维码会被拉成满宽（1658×392 那种）。
            groupVlg.childForceExpandWidth = false;

            // 标题和「引用的项目」等卡片标题保持一致：占满一行、左对齐。
            var groupTitle = AddFixedLabel(group, "title", "加入交流群", SectionSize, LabelColor, 22f);
            groupTitle.alignment = TextAnchor.MiddleLeft;
            groupTitle.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var groupNumber = AddFixedLabel(group, "number", "QQ 群 1091497260", 16, LabelColor, 24f);
            groupNumber.alignment = TextAnchor.MiddleCenter;

            // 二维码是竖版海报（1284×2286），按比例放大并居中。
            var qr = Node("qr", group);
            var qrLe = qr.gameObject.AddComponent<LayoutElement>();
            qrLe.preferredWidth = QrWidth;
            qrLe.preferredHeight = QrWidth * (QrTextureHeight / (float)QrTextureWidth);
            qrLe.flexibleWidth = 0f;
            var raw = qr.gameObject.AddComponent<RawImage>();
            raw.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(QrTexturePath);
            raw.color = Color.white;
            raw.raycastTarget = false;

            var copyRow = Node("copy-row", group);
            copyRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
            var copyHlg = copyRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            copyHlg.spacing = 8f;
            copyHlg.childControlWidth = true;
            copyHlg.childControlHeight = true;
            copyHlg.childForceExpandWidth = false;
            copyHlg.childForceExpandHeight = false;
            copyHlg.childAlignment = TextAnchor.MiddleCenter;
            AddButton(copyRow, "copy-group-button", "复制群号", 120f, 30f);
        }

        private static void AddCreditRow(RectTransform card, string name, string title, string use, string license, string url)
        {
            var row = Node("row-" + name, card);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 34f;
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var nameText = AddLabel(row, title, LabelSize, LabelColor, TextAnchor.MiddleLeft);
            nameText.gameObject.AddComponent<LayoutElement>().preferredWidth = 118f;
            var useText = AddLabel(row, use, HintSize, DimColor, TextAnchor.MiddleLeft);
            useText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var licenseText = AddLabel(row, license, SmallSize, SmallColor, TextAnchor.MiddleLeft);
            licenseText.gameObject.AddComponent<LayoutElement>().preferredWidth = 130f;
            AddButton(row, "copy-" + name, "复制链接", 84f, 26f);
        }

        // ------------------------------------------------------------ 小部件

        private static RectTransform Section(RectTransform parent, string title)
        {
            return Section(parent, title, null, null);
        }

        private static RectTransform Section(RectTransform parent, string title, string statusName, string statusText)
        {
            var section = Node("section", parent);
            var vlg = section.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            var head = Node("head", section);
            head.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;
            var hlg = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var titleText = AddLabel(head, title, SectionSize, DimColor, TextAnchor.MiddleLeft);
            titleText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            if (statusText != null)
            {
                var status = AddLabel(head, statusText, HintSize, AccentColor, TextAnchor.MiddleRight);
                status.gameObject.name = statusName;
                status.gameObject.AddComponent<LayoutElement>().preferredWidth = 220f;
            }
            return section;
        }

        private static RectTransform Card(RectTransform parent, string name)
        {
            var card = Node(name, parent);
            AddImage(card, CardBg);
            var vlg = card.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 0f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;
            return card;
        }

        private static VerticalLayoutGroup CardVlg(RectTransform card, int left, int top, int spacing)
        {
            var vlg = card.GetComponent<VerticalLayoutGroup>();
            if (vlg == null) vlg = card.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(left, left, top, top);
            vlg.spacing = spacing;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;
            return vlg;
        }

        private static void AddNote(RectTransform parent, string name, string text)
        {
            var note = Node(name, parent);
            note.gameObject.AddComponent<LayoutElement>().preferredHeight = 36f;
            AddImage(note, new Color(1f, 1f, 1f, 0.03f));
            var label = AddLabel(note, text, HintSize, DimColor, TextAnchor.MiddleLeft);
            Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(10f, 0f);
            label.rectTransform.offsetMax = new Vector2(-10f, 0f);
        }

        private static RectTransform AddCard(RectTransform parent, string name)
        {
            var card = Node(name, parent);
            AddImage(card, CardBg);
            return card;
        }

        private static Text AddFixedLabel(RectTransform parent, string name, string text, int size, Color color, float height)
        {
            var label = AddLabel(parent, text, size, color, TextAnchor.MiddleLeft);
            label.gameObject.name = name;
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            return label;
        }

        private static void AddButton(RectTransform parent, string name, string label, float width, float height)
        {
            var rt = Node(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = height;
            le.flexibleWidth = 0f;
            var img = AddImage(rt, ButtonBg);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            button.transition = Selectable.Transition.ColorTint;

            var text = AddLabel(rt, label, ButtonSize, LabelColor, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
        }

        private static void AddInput(RectTransform parent, string name, string placeholder, float width, float height)
        {
            var rt = Node(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = height;
            le.flexibleWidth = 0f;

            AddImage(rt, InputBg);

            var field = rt.gameObject.AddComponent<InputField>();
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 0;

            var text = AddLabel(rt, string.Empty, LabelSize, LabelColor, TextAnchor.MiddleLeft);
            text.gameObject.name = "text";
            Stretch(text.rectTransform, 8f, 0f, 8f, 0f);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;

            var ph = AddLabel(rt, placeholder, LabelSize, new Color(1f, 1f, 1f, 0.5f), TextAnchor.MiddleLeft);
            ph.gameObject.name = "placeholder";
            Stretch(ph.rectTransform, 8f, 0f, 8f, 0f);
            ph.horizontalOverflow = HorizontalWrapMode.Overflow;

            field.textComponent = text;
            field.placeholder = ph;
        }

        private static RectTransform ScrollList(RectTransform parent, string name)
        {
            var list = Node(name, parent);
            list.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
            AddImage(list, Transparent);

            var scroll = list.gameObject.AddComponent<ScrollRect>();
            var viewport = Node("viewport", list);
            Stretch(viewport);
            AddImage(viewport, Transparent);

            var content = Node("content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            return content;
        }

        // ------------------------------------------------------------ 基础工具

        private static RectTransform Node(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        private static void Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 anchoredPosition, Vector2 sizeDelta, Vector2 pivot)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = sizeDelta;
        }

        private static Image AddImage(RectTransform rt, Color color)
        {
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = null;
            image.color = color;
            image.type = Image.Type.Simple;
            image.raycastTarget = true;
            return image;
        }

        private static Text AddLabel(RectTransform parent, string text, int size, Color color, TextAnchor anchor)
        {
            var rt = Node("label", parent);
            return AddText(rt, text, size, color, anchor);
        }

        private static Text AddText(RectTransform rt, string text, int size, Color color, TextAnchor anchor)
        {
            var label = rt.gameObject.AddComponent<Text>();
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = anchor;
            label.raycastTarget = false;
            // 游戏字体（SAIRASB：16 号行高 25）比设计期的 LegacyRuntime 高，
            // 绝对定位的文本框一旦比行高矮，Truncate 会把整行裁掉（踩过：分区标题整块消失）。
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.font = BuiltinFont;
            return label;
        }

        private static Font _builtinFont;

        /// <summary>
        /// 只做占位：运行期 SettingsPageView 会把整棵树的字体换成游戏当前语言字体。
        /// 不能留空——修改版 TextGenerator 在 font == null 时算高度会 NRE。
        /// </summary>
        private static Font BuiltinFont
        {
            get
            {
                if (_builtinFont == null) _builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _builtinFont;
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        private static void ActivateOnly(RectTransform content, string keep)
        {
            foreach (Transform child in content)
            {
                child.gameObject.SetActive(child.name == keep);
            }
        }
    }
}
