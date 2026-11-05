using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 生成聊天窗的 prefab（`Assets/IcarusII/IcarusIIWindow.prefab`）。
    ///
    /// 为什么要有这个脚本而不是手搭：prefab 是**二进制** Unity 资源，
    /// 手搭出来的东西无法 review、也无法复现。用脚本生成可以入库、可重跑、
    /// 且在 prefab 被误改时能重建。生成之后你依然可以在 Unity 里可视化微调，
    /// 微调结果会保存在 prefab 里（脚本只在显式调用时覆盖）。
    ///
    /// ## 外观来源：**复制游戏原生窗口，不自己设计**
    ///
    /// 这里不再"照着感觉做一套样式"（那套已弃用），而是把游戏里两个原生窗口的
    /// 实际节点**原样复制**过来。数值来自打开 `Assets/Scenes/DSPGame.unity` 后的现场结构，
    /// 每一行都在下面注明了来源节点，改动前请先核对一遍：
    ///
    /// ### 一、外框：机甲信息窗
    /// 场景路径 `UI Root/Overlay Canvas/In Game/Windows/Mecha Window`
    ///
    /// 节点          贴图                    类型    颜色        材质
    /// shadow        window-trsl-1-sd       Sliced  #00000050   Default UI Material
    /// panel-bg      window-trsl-1          Sliced  #002C37FF   trsl-panel-3 → 见下
    /// drag-trigger  无（纯点击区）           Sliced  #00000000   Default UI Material
    /// border        window-border-1        Sliced  #A7E9FF4B   widget-alpha-5x
    /// glass         window-glass-1         Sliced  #61D8FF25   widget-alpha-5x
    /// deco ×3       sci-fi-deco-1          Simple  #61D8FF22   widget-alpha-5x
    /// title-text    —                      Text    #969696FF   widget-text-alpha-5x
    ///
    /// 几点细节（与现场一致，别"顺手修正"）：
    ///
    /// * `glass` 与三个 `deco` 在原窗口里就是 **inactive** 的，这里照样复制成未激活，
    ///   否则窗口会比原生多一层高光和三个装饰角。
    /// * `panel-bg` 在原窗口用的是 `trsl-panel-3`（第三方 Translucent Image 的
    ///   `UI/TranslucentImage` 着色器，靠相机提供的 `_BlurTex` 做背景模糊）。
    ///   mod 的窗口挂在**自建 Screen Space Overlay 画布**上，没有模糊源，
    ///   套那个材质只会得到一块灰色，所以这里用贴图 + 颜色的默认 UI 材质
    ///   ——颜色本身是 #002C37FF（不透明），观感即原生面板的底色。
    /// * `title-text` 是**绝对定位**的（anchor(0,1) pivot(0,1) pos(46,-24) size 145×36），
    ///   因为原窗口左边缘有一圈很宽的切角装饰。标题沿用这个位置。
    ///
    /// ### 二、输入框：信标窗的待办框
    /// 场景路径 `UI Root/Overlay Canvas/In Game/Windows/Marker Window/to-do-list`
    /// （就是全息信标窗里那个"待办事项"输入区，整块搬过来）
    ///
    /// 节点                             说明
    /// to-do-list (= 我们的 Input)       整块容器，原生 168×70
    /// editor                            拉伸填满
    /// tool-bar      高 24，贴底          Image 无贴图 Simple #0000008F
    /// icons         88×24，贴左          Image 透明；里面三个工具图标（见下）
    /// scroll-view   贴 tool-bar 上方     ScrollRect + Image 无贴图 Sliced #0000008F
    /// viewport                           Mask + 内置 UIMask 贴图（Show Mask Graphic=false）
    /// content                           普通 RectTransform，高 40
    /// InputField    content 的顶对齐子节点（运行期随输入文字增高，就是滚动内容）
    /// input                             挂 InputField + Image（无贴图 #FFFFFF00）
    /// Placeholder   Text '待办事项' SAIRASB 14 #FFFFFF74 左上
    /// Text          Text ''        SAIRAR  14 #FFFFFF52 左上，材质 widget-text-alpha-5x-thick
    /// Scrollbar Vertical                右侧 6 宽，Image #000000DE + Scrollbar → Handle #FFFFFF8F
    ///
    /// 三个工具图标（`icons` 下，原生 28×28，图标 20×20，颜色 #FFFFFF66）：
    /// `icon-select-btn`(insert-icon-40) / `checkbox-btn`(checkbox-on-2) / `text-color-btn`(文字 'A')。
    /// 它们在原窗口里是插入图标 / 插入复选框 / 选文字颜色的按钮，
    /// 聊天窗**只保留外观**（Button/UIButton 全部剥掉，raycastTarget 关掉让点击落到输入框），
    /// 否则会出现"点了没反应"的死按钮。
    ///
    /// InputField 的取值照抄：MultiLineNewline + Standard、上限 2000、
    /// 光标闪烁 0.85 宽 1、customCaret=true 光标 #FFFFFFFF、选中色 #1E6565FF。
    /// **多行**这点很重要：mod 侧靠游戏改写版 InputField 的 `OnNewLine` 事件把回车当发送
    /// （见 `ChatWindow.OnInputNewLine`），所以这里的 lineType 不能改。
    ///
    /// ### 三、字体是**占位**，运行期必定被换掉
    /// 原生窗口的字体是 SAIRAB / SAIRASB（Saira，**不含中文**），游戏靠
    /// `Localizer` 按语言替换字体。聊天窗内容是中文，所以这里只烘焙
    /// **字号、颜色、对齐**，字体字段填内置 Arial 当占位——
    /// mod 侧 `ChatWindow` 会**无条件**把它换成游戏当前语言的字体
    /// （`FontProvider` / `UiStyle`）。
    ///
    /// 为什么不留空：UGUI 的修改版 `TextGenerator` 在 `font == null` 时算
    /// `preferredHeight` 会抛 NullReferenceException，而正文上有
    /// ContentSizeFitter，一进编辑器就刷屏。
    /// 代价：在 Unity 编辑器里中文显示为方框，属已知取舍。
    ///
    /// ## 节点结构（mod 侧 `ChatWindow` 按名字/路径查找，改名要同步改 mod）
    ///
    ///   IcarusIIWindow                根（无 Image；mod 用它做 Panel 定位）
    ///   ├── shadow                    Image
    ///   ├── panel-bg                  Image（面板底衬）
    ///   │   ├── drag-trigger          Image（透明，raycastTarget，点窗口即聚焦输入框）
    ///   │   ├── border                Image（原窗口的 border）
    ///   │   ├── glass                 Image（未激活）
    ///   │   └── deco / deco (1) / deco (2)   Image（未激活）
    ///   ├── Header                    Image（全透明）
    ///   │   ├── Title                 Text
    ///   │   └── Status                Text
    ///   ├── Scroll                    ScrollRect
    ///   │   └── Viewport              Mask + 透明 Image（raycastTarget，滚动用）
    ///   │       └── Content           Text（正文控件挂在 Content 上）+ ContentSizeFitter
    ///   └── InputBar
    ///       └── Input                 信标待办框整块（结构见"二、输入框"）
    ///           └── …/InputField/input
    ///               ├── Placeholder   Text
    ///               └── Text          Text（正文控件；mod 按 InputField 找）
    ///
    /// 内容三块（Header / Scroll / InputBar）与 panel-bg **同级**，
    /// 和原生机甲信息窗里 information / fuel-group… 的位置一致。
    ///
    /// **Canvas 不在这里**：它依赖游戏 UI Root 的 CanvasScaler 设置，且必须是
    /// 顶层 DontDestroyOnLoad 对象，所以仍由 mod 在运行时创建，本 prefab 挂进去。
    /// </summary>
    public static class IcarusIIWindowPrefabBuilder
    {
        private const string PrefabDir = "Assets/IcarusII";
        private const string PrefabPath = PrefabDir + "/IcarusIIWindow.prefab";
        private const string SpriteDir = "Assets/Resources/UI/Textures/Sprites/Sci-fi/";
        private const string IconDir = "Assets/Resources/UI/Textures/Sprites/Icons/";
        private const string MaterialDir = "Assets/Resources/UI/Materials/";

        // 与原生机甲信息窗同尺寸
        private const float PanelWidth = 760f;
        private const float PanelHeight = 502f;

        // ---- 机甲信息窗的取值（逐项抄自现场）
        private static readonly Color ShadowColor = Rgba(0x00, 0x00, 0x00, 0x50);
        private static readonly Color PanelColor = Rgba(0x00, 0x2C, 0x37, 0xFF);
        private static readonly Color BorderColor = Rgba(0xA7, 0xE9, 0xFF, 0x4B);
        private static readonly Color GlassColor = Rgba(0x61, 0xD8, 0xFF, 0x25);
        private static readonly Color DecoColor = Rgba(0x61, 0xD8, 0xFF, 0x22);
        private static readonly Color TitleColor = Rgba(0x96, 0x96, 0x96, 0xFF);

        // ---- 输入框的值（抄自信标窗 to-do-list）
        private static readonly Color InputBoxColor = Rgba(0x00, 0x00, 0x00, 0x8F);      // scroll-view / tool-bar 底
        private static readonly Color InputBgColor = Rgba(0xFF, 0xFF, 0xFF, 0x00);       // InputField 自己的 targetGraphic
        private static readonly Color InputTextColor = Rgba(0xFF, 0xFF, 0xFF, 0x52);     // input-text
        private static readonly Color InputPlaceholderColor = Rgba(0xFF, 0xFF, 0xFF, 0x74); // Placeholder
        private static readonly Color InputCaretColor = Rgba(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color InputSelectionColor = Rgba(0x1E, 0x65, 0x65, 0xFF);
        private static readonly Color ScrollbarBgColor = Rgba(0x00, 0x00, 0x00, 0xDE);
        private static readonly Color ScrollbarHandleColor = Rgba(0xFF, 0xFF, 0xFF, 0x8F);
        private static readonly Color ToolIconColor = Rgba(0xFF, 0xFF, 0xFF, 0x66);

        // ---- 正文（取自机体信息窗里常见的正文色；运行期会被 UiStyle 覆盖）
        private static readonly Color TranscriptColor = Rgba(0xFF, 0xFF, 0xFF, 0xA6);
        private static readonly Color StatusColor = Rgba(0x96, 0x96, 0x96, 0xFF);

        /// <summary>占位字体：内置 Arial（不含中文，运行期必被 mod 换掉）。</summary>
        private static Font BuiltinFont
        {
            get { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
        }

        private const int TitleFontSize = 18;      // title-text
        private const int BodyFontSize = 14;       // 与输入框同字号
        private const int StatusFontSize = 14;

        // ---- 输入区尺寸（全部来自 to-do-list 现场取值）
        private const float InputHeight = 70f;      // 原生 to-do-list 高
        private const float InputBarHeight = 100f;  // 与 ChatWindow.prefab 的 input 高度一致（不再运行期增高）
        private const float InputInnerMargin = 8f;
        private const float ToolBarHeight = 24f;    // tool-bar
        private const float ToolIconsWidth = 88f;   // icons
        private const float ToolButtonSize = 28f;
        private const float ToolIconSize = 20f;
        private const float ScrollBarWidth = 6f;
        private const float ContentMargin = 20f;
        private const float ContentTop = 72f;       // 让开标题（标题到 y=-60）
        private const float ContentBottom = 142f;  // 40 底边 + 100 input + 2 间隙，与 prefab 一致

        [MenuItem("Icarus II/Rebuild Window Prefab (会覆盖手改)")]
        public static void BuildFromMenu()
        {
            if (!ConfirmOverwrite()) return;
            BuildForced();
            EditorUtility.DisplayDialog("Icarus II", "已重建 " + PrefabPath, "好");
        }

        /// <summary>
        /// **会整棵覆盖** `PrefabPath`。手工改过 prefab 时不要调它——
        /// 那种情况用 `IcarusIIWindowTransplant`（只做加法）。
        ///
        /// 这里默认加了闸：目标已存在就直接拒绝，必须显式调 `BuildForced()`。
        /// （2026-10-16 出过一次事故：这个脚本把一个手工改过的 prefab 整棵冲掉了。）
        /// </summary>
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                Debug.LogError(
                    "[IcarusII] 拒绝覆盖已有的 prefab：" + PrefabPath + "\n" +
                    "  如果你的窗口是手工维护的，用 Icarus II/Add Chat Parts To Prefab（只做加法）。\n" +
                    "  确实要整棵重建时，调 IcarusIIWindowPrefabBuilder.BuildForced()。");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            BuildForced();
        }

        /// <summary>整棵重建（会覆盖）。调用前请确认目标可以丢。</summary>
        public static void BuildForced()
        {
            if (!AssetDatabase.IsValidFolder(PrefabDir))
            {
                AssetDatabase.CreateFolder("Assets", "IcarusII");
            }

            var root = BuildHierarchy();

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);

            if (prefab == null)
            {
                Debug.LogError("[IcarusII] prefab 保存失败：" + PrefabPath);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[IcarusII] 已生成 prefab：" + PrefabPath);
        }

        private static bool ConfirmOverwrite()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null) return true;
            return EditorUtility.DisplayDialog("Icarus II",
                "整棵重建会**覆盖**现有 prefab（手工改动会丢）：\n" + PrefabPath +
                "\n\n如果这份 prefab 是你手工维护的，请改用 Icarus II/Add Chat Parts To Prefab。\n继续覆盖？",
                "覆盖重建", "取消");
        }

        private static GameObject BuildHierarchy()
        {
            // 根节点刻意不挂 Image：原生机甲信息窗的根也没有图，底衬在 panel-bg 上。
            // 这样 shadow 才能作为**靠前的同级子物体**画在底衬下面（UGUI 先画父再画子）。
            var rootRt = Node("IcarusIIWindow", null);
            var root = rootRt.gameObject;
            rootRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            // ── 外框：机甲信息窗的同尺寸九宫格（shadow / panel-bg / border / glass）──
            var shadow = Node("shadow", rootRt);
            Stretch(shadow);
            var shadowImg = shadow.gameObject.AddComponent<Image>();
            shadowImg.sprite = LoadSprite("window-trsl-1-sd");
            shadowImg.type = Image.Type.Sliced;
            shadowImg.color = ShadowColor;
            shadowImg.raycastTarget = false;

            var panel = Node("panel-bg", rootRt);
            Stretch(panel);
            var panelImg = panel.gameObject.AddComponent<Image>();
            panelImg.sprite = LoadSprite("window-trsl-1");
            panelImg.type = Image.Type.Sliced;
            panelImg.color = PanelColor;
            panelImg.raycastTarget = false;   // 点击由 drag-trigger 接（与原生一致）

            // 原窗口用 drag-trigger 接住拖拽/点击；这里同一个位置用来"点窗口即聚焦输入框"
            var trigger = Node("drag-trigger", panel);
            trigger.anchorMin = Vector2.zero;
            trigger.anchorMax = Vector2.one;
            trigger.pivot = new Vector2(0.5f, 0.5f);
            trigger.offsetMin = new Vector2(15f, 12f);
            trigger.offsetMax = new Vector2(-15f, -15f);
            var triggerImg = trigger.gameObject.AddComponent<Image>();
            triggerImg.color = Rgba(0, 0, 0, 0);
            triggerImg.raycastTarget = true;

            var border = Node("border", panel);
            Stretch(border);
            var borderImg = border.gameObject.AddComponent<Image>();
            borderImg.sprite = LoadSprite("window-border-1");
            borderImg.type = Image.Type.Sliced;
            borderImg.color = BorderColor;
            borderImg.material = LoadMaterial("widget-alpha-5x");
            borderImg.raycastTarget = false;

            var glass = Node("glass", panel);
            Stretch(glass);
            var glassImg = glass.gameObject.AddComponent<Image>();
            glassImg.sprite = LoadSprite("window-glass-1");
            glassImg.type = Image.Type.Sliced;
            glassImg.color = GlassColor;
            glassImg.material = LoadMaterial("widget-alpha-5x");
            glassImg.raycastTarget = false;
            glass.gameObject.SetActive(false);          // 原生就是关的

            AddDeco(panel, "deco", -300f);
            AddDeco(panel, "deco (1)", -235f);
            AddDeco(panel, "deco (2)", -170f);

            // 内容和原生机甲信息窗一样，是 panel-bg 的**同级**（挂在根下、画在底衬/边框之上）：
            // 原生窗口的 information / fuel-group / fighter-group… 就是 panel-bg 的兄弟节点。
            BuildHeader(rootRt);
            BuildCloseButton(panel);
            BuildTranscript(rootRt);
            BuildInputBar(rootRt);

            return root;
        }

        /// <summary>顶部装饰角：原生 deco（64×16，贴右上，未激活）。</summary>
        private static void AddDeco(RectTransform parent, string name, float x)
        {
            var deco = Node(name, parent);
            deco.anchorMin = new Vector2(1f, 1f);
            deco.anchorMax = new Vector2(1f, 1f);
            deco.pivot = new Vector2(0.5f, 0.5f);
            deco.anchoredPosition = new Vector2(x, -20f);
            deco.sizeDelta = new Vector2(64f, 16f);

            var img = deco.gameObject.AddComponent<Image>();
            img.sprite = LoadSprite("sci-fi-deco-1");
            img.type = Image.Type.Simple;
            img.color = DecoColor;
            img.material = LoadMaterial("widget-alpha-5x");
            img.raycastTarget = false;
            deco.gameObject.SetActive(false);           // 原生就是关的
        }

        /// <summary>
        /// 标题区。原生机甲信息窗没有独立标题栏，「机体信息」四个字直接画在面板上
        /// （`panel-bg/title-text`），所以这里的 Header 只是一层全透明容器：
        /// 既保持 mod 的 `Header/Title`、`Header/Status` 契约，又不遮住底衬和边框。
        /// </summary>
        /// <summary>
        /// 标题区（`IcarusIIWindowTransplant` 也复用它）。
        /// withTitle=false 时只建 Header + Status：给"外框和标题都已经手工做好"的 prefab 用，
        /// 免得再塞一个重复的 Title 进去。
        /// </summary>
        /// <summary>
        /// 右上角的关闭按钮：整块照抄游戏里**只有单个关闭按钮**的窗口
        /// （通讯器 / 复制器 / 矿机 / Window Template…）的 `panel-bg/btn-box`：
        ///
        ///   btn-box [40x30, anchor(1,1), pos(-50,-30)]  TranslucentImage(hex-bg-30, #002C37FF, trsl-panel-0)
        ///     ├─ close-wnd-btn [40x30]  Image(hex-bg-30, #00000032, widget-alpha-5x) + Button
        ///     │    ├─ x-icon [16x16]  Image(close-x-m, #6CDAFF88, widget-alpha-5x)
        ///     │    └─ col    [28x24]  Image(无贴图, #00000000, ray=true)   ← 真正的命中区
        ///     └─ btn-border [stretch]  Image(hex-border-up-30, #A7E9FF4B, widget-alpha-5x)
        ///
        /// 容器和原生一样用 `TranslucentImage` + `trsl-panel-0`：运行期它自己会从
        /// Main Camera 的 `TranslucentImageSource` 取模糊贴图，和窗口面板同一套机制。
        ///
        /// 点击行为由 mod 运行期接（prefab 引用不了 mod 的类型）。
        /// </summary>
        public static void BuildCloseButton(RectTransform panelBg)
        {
            var box = Node("btn-box", panelBg);
            box.anchorMin = new Vector2(1f, 1f);
            box.anchorMax = new Vector2(1f, 1f);
            box.pivot = new Vector2(0.5f, 0.5f);
            box.anchoredPosition = new Vector2(-50f, -30f);
            box.sizeDelta = new Vector2(40f, 30f);

            var boxGraphic = box.gameObject.AddComponent<TranslucentImage>();
            boxGraphic.sprite = LoadSprite("hex-bg-30");
            boxGraphic.color = Rgba(0x00, 0x2C, 0x37, 0xFF);
            boxGraphic.material = LoadMaterial("trsl-panel-0");
            boxGraphic.type = Image.Type.Sliced;
            boxGraphic.raycastTarget = false;

            var close = Node("close-wnd-btn", box);
            close.anchorMin = new Vector2(0.5f, 0.5f);
            close.anchorMax = new Vector2(0.5f, 0.5f);
            close.pivot = new Vector2(0.5f, 0.5f);
            close.anchoredPosition = Vector2.zero;
            close.sizeDelta = new Vector2(40f, 30f);

            var closeImg = close.gameObject.AddComponent<Image>();
            closeImg.sprite = LoadSprite("hex-bg-30");
            closeImg.color = Rgba(0x00, 0x00, 0x00, 0x32);
            closeImg.material = LoadMaterial("widget-alpha-5x");
            closeImg.type = Image.Type.Sliced;
            closeImg.raycastTarget = false;

            // 原生这里是游戏自己的 UIButton（带悬停动效）；我们只留 UGUI Button，
            // 行为在运行期由 mod 接上。
            var button = close.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = closeImg;

            var xIcon = Node("x-icon", close);
            xIcon.anchorMin = new Vector2(0.5f, 0.5f);
            xIcon.anchorMax = new Vector2(0.5f, 0.5f);
            xIcon.pivot = new Vector2(0.5f, 0.5f);
            xIcon.anchoredPosition = Vector2.zero;
            xIcon.sizeDelta = new Vector2(16f, 16f);
            var xImg = xIcon.gameObject.AddComponent<Image>();
            xImg.sprite = LoadSprite("close-x-m");
            xImg.color = Rgba(0x6C, 0xDA, 0xFF, 0x88);
            xImg.material = LoadMaterial("widget-alpha-5x");
            xImg.type = Image.Type.Simple;
            xImg.raycastTarget = false;

            var col = Node("col", close);
            col.anchorMin = new Vector2(0.5f, 0.5f);
            col.anchorMax = new Vector2(0.5f, 0.5f);
            col.pivot = new Vector2(0.5f, 0.5f);
            col.anchoredPosition = Vector2.zero;
            col.sizeDelta = new Vector2(28f, 24f);
            var colImg = col.gameObject.AddComponent<Image>();
            colImg.color = Rgba(0x00, 0x00, 0x00, 0x00);
            colImg.material = LoadMaterial("widget-alpha-5x");
            colImg.type = Image.Type.Simple;
            colImg.raycastTarget = true;

            var border = Node("btn-border", box);
            border.anchorMin = Vector2.zero;
            border.anchorMax = Vector2.one;
            border.pivot = new Vector2(0.5f, 0.5f);
            border.anchoredPosition = Vector2.zero;
            border.sizeDelta = Vector2.zero;
            var borderImg = border.gameObject.AddComponent<Image>();
            borderImg.sprite = LoadSprite("hex-border-up-30");
            borderImg.color = Rgba(0xA7, 0xE9, 0xFF, 0x4B);
            borderImg.material = LoadMaterial("widget-alpha-5x");
            borderImg.type = Image.Type.Sliced;
            borderImg.raycastTarget = false;
        }

        public static void BuildHeader(RectTransform parent)
        {
            BuildHeader(parent, true);
        }

        public static void BuildHeader(RectTransform parent, bool withTitle)
        {
            var header = Node("Header", parent);
            TopBar(header, ContentTop);
            var img = header.gameObject.AddComponent<Image>();
            img.color = Rgba(0, 0, 0, 0);
            img.raycastTarget = false;

            if (withTitle)
            {
                // 位置与原生 title-text 完全一致（anchor/pivot/pos/size）
                var title = Node("Title", header);
                title.anchorMin = new Vector2(0f, 1f);
                title.anchorMax = new Vector2(0f, 1f);
                title.pivot = new Vector2(0f, 1f);
                title.anchoredPosition = new Vector2(46f, -24f);
                title.sizeDelta = new Vector2(145f, 36f);
                var titleText = AddLabel(title, TitleFontSize, TitleColor, TextAnchor.MiddleLeft);
                titleText.text = "工业机甲终端";
            }

            var status = Node("Status", header);
            status.anchorMin = new Vector2(1f, 1f);
            status.anchorMax = new Vector2(1f, 1f);
            status.pivot = new Vector2(1f, 1f);
            // 右边缘停在 x=-78：右上角留给 btn-box 关闭按钮（它占 -70..-30）
            status.anchoredPosition = new Vector2(-78f, -24f);
            status.sizeDelta = new Vector2(220f, 36f);
            var statusText = AddLabel(status, StatusFontSize, StatusColor, TextAnchor.MiddleRight);
            statusText.text = string.Empty;
        }

        public static void BuildTranscript(RectTransform parent)
        {
            var scroll = Node("Scroll", parent);
            Stretch(scroll, left: ContentMargin, top: ContentTop, right: ContentMargin, bottom: ContentBottom);

            var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 30f;

            var viewport = Node("Viewport", scroll);
            Stretch(viewport);
            // 透明但收射线的底：滚轮/拖拽命中它以后会冒泡到 Scroll 上的 ScrollRect，
            // 否则正文区没有任何 Graphic，拖动与滚轮都到不了 ScrollRect。
            var viewportHit = viewport.gameObject.AddComponent<Image>();
            viewportHit.color = Rgba(0, 0, 0, 0);
            viewportHit.raycastTarget = true;
            IcarusIIMaskUtil.EnsureStencilMask(viewport);

            var content = Node("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = new Vector2(0f, -4f);
            content.sizeDelta = new Vector2(-20f, 100f);

            // 正文控件挂在 Content 自身（mod 的 _transcript 就指向它）
            var transcript = AddLabel(content, BodyFontSize, TranscriptColor, TextAnchor.UpperLeft);
            transcript.horizontalOverflow = HorizontalWrapMode.Wrap;
            transcript.verticalOverflow = VerticalWrapMode.Overflow;
            transcript.supportRichText = true;

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
        }

        /// <summary>
        /// 输入区：**整块照抄信标窗的待办框** `Marker Window/to-do-list`（取值见类注释）。
        ///
        /// 原生节点名除了正文那个 `input-text`（按 mod 契约改名成 `Text`）
        /// 全部保留：editor / tool-bar / icons / scroll-view / viewport /
        /// content / InputField / input / Placeholder / Scrollbar Vertical。
        /// </summary>
        public static void BuildInputBar(RectTransform parent)
        {
            var bar = Node("InputBar", parent);
            BottomBar(bar, InputBarHeight);

            // 对应原生 to-do-list（原生 168×70、锚在窗口顶；这里贴底、横向拉满）
            var input = Node("Input", bar);
            Stretch(input, left: ContentMargin, top: InputInnerMargin, right: ContentMargin, bottom: InputInnerMargin);

            var editor = Node("editor", input);
            Stretch(editor);

            BuildToolBar(editor);

            // 原生 tool-bar 高 24，scroll-view 的 offMin 是 (0,25)
            var scrollView = BuildInputScrollView(editor);
            scrollView.SetAsFirstSibling();
        }

        /// <summary>底部工具条：原生三个按钮只保留外观（Button/UIButton 全部剥掉）。</summary>
        private static void BuildToolBar(RectTransform parent)
        {
            var toolBar = Node("tool-bar", parent);
            BottomBar(toolBar, ToolBarHeight);
            var bg = toolBar.gameObject.AddComponent<Image>();
            bg.sprite = null;
            bg.type = Image.Type.Simple;
            bg.color = InputBoxColor;                  // #0000008F
            bg.raycastTarget = false;

            var icons = Node("icons", toolBar);
            icons.anchorMin = new Vector2(0f, 0.5f);
            icons.anchorMax = new Vector2(0f, 0.5f);
            icons.pivot = new Vector2(0f, 0.5f);
            icons.anchoredPosition = Vector2.zero;
            icons.sizeDelta = new Vector2(ToolIconsWidth, ToolBarHeight);
            var iconsBg = icons.gameObject.AddComponent<Image>();
            iconsBg.color = Rgba(0xFF, 0xFF, 0xFF, 0x00);
            // 原生这里收射线（按钮要能点）；按钮剥掉以后关掉，让点击落到输入框/整窗聚焦
            iconsBg.raycastTarget = false;

            AddToolIcon(icons, "icon-select-btn", 5f, "insert-icon-40", null);
            AddToolIcon(icons, "checkbox-btn", 33f, "checkbox-on-2", null);
            AddToolIcon(icons, "text-color-btn", 61f, null, "A");
        }

        /// <summary>
        /// 工具条上的一个图标位（原生 28×28、图标 20×20、位置 ap(x,0)）。
        /// spriteName 与 label 二选一：原生两个是贴图，文字颜色那个是一颗 'A'。
        /// </summary>
        private static void AddToolIcon(RectTransform parent, string name, float x, string spriteName, string label)
        {
            var btn = Node(name, parent);
            btn.anchorMin = new Vector2(0f, 0.5f);
            btn.anchorMax = new Vector2(0f, 0.5f);
            btn.pivot = new Vector2(0f, 0.5f);
            btn.anchoredPosition = new Vector2(x, 0f);
            btn.sizeDelta = new Vector2(ToolButtonSize, ToolButtonSize);

            var icon = Node("icon", btn);
            icon.anchorMin = new Vector2(0.5f, 0.5f);
            icon.anchorMax = new Vector2(0.5f, 0.5f);
            icon.pivot = new Vector2(0.5f, 0.5f);
            icon.anchoredPosition = Vector2.zero;
            icon.sizeDelta = new Vector2(ToolIconSize, ToolIconSize);

            if (!string.IsNullOrEmpty(spriteName))
            {
                var img = icon.gameObject.AddComponent<Image>();
                img.sprite = LoadIconSprite(spriteName);
                img.type = Image.Type.Simple;
                img.color = ToolIconColor;             // #FFFFFF66
                img.raycastTarget = false;
            }
            else
            {
                var txt = AddLabel(icon, 16, ToolIconColor, TextAnchor.MiddleCenter, null);
                txt.text = label;
                txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                txt.verticalOverflow = VerticalWrapMode.Overflow;
            }
        }

        /// <summary>
        /// 滚动区：遮罩视口 + 多行 InputField + 右侧细滚动条，全部照抄原生 to-do-list。
        /// 返回 scroll-view 节点。
        /// </summary>
        private static RectTransform BuildInputScrollView(RectTransform parent)
        {
            var scrollView = Node("scroll-view", parent);
            Stretch(scrollView, bottom: ToolBarHeight + 1f);     // 原生 offMin(0,25)
            var bg = scrollView.gameObject.AddComponent<Image>();
            bg.sprite = null;
            bg.type = Image.Type.Sliced;
            bg.color = InputBoxColor;                  // #0000008F
            bg.raycastTarget = true;

            var scrollRect = scrollView.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;   // 原生 Clamped
            scrollRect.inertia = false;                // 原生 inertia=False
            scrollRect.decelerationRate = 0.135f;
            scrollRect.elasticity = 0.1f;
            scrollRect.scrollSensitivity = 20f;

            var viewport = Node("viewport", scrollView);
            Stretch(viewport, right: ScrollBarWidth);  // 原生 offMax(-6,0)
            viewport.pivot = new Vector2(0f, 1f);      // 原生 pivot(0,1)
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.sprite = LoadBuiltinMaskSprite();    // 内置 UIMask，只当遮罩模板，不显示
            vpImg.type = Image.Type.Sliced;
            vpImg.color = Color.white;
            vpImg.raycastTarget = true;
            var mask = viewport.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;              // 原生 Show Mask Graphic = false

            var content = Node("content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 63f);  // 固定为输入栏可视高度；多行由 ScrollRect 滚动

            var fieldHolder = Node("InputField", content);
            fieldHolder.anchorMin = new Vector2(0f, 1f);
            fieldHolder.anchorMax = new Vector2(1f, 1f);
            fieldHolder.pivot = new Vector2(0.5f, 1f);
            fieldHolder.anchoredPosition = Vector2.zero;
            fieldHolder.sizeDelta = new Vector2(-8f, 63f);    // 固定高度；不再由运行期改尺寸

            var input = Node("input", fieldHolder);
            Stretch(input);
            var fieldBg = input.gameObject.AddComponent<Image>();
            fieldBg.sprite = null;
            fieldBg.type = Image.Type.Simple;
            fieldBg.color = InputBgColor;              // #FFFFFF00（原生就是全透明）
            fieldBg.raycastTarget = true;

            var placeholderHolder = Node("Placeholder", input);
            Stretch(placeholderHolder);
            var placeholder = AddLabel(placeholderHolder, BodyFontSize, InputPlaceholderColor, TextAnchor.UpperLeft, null);
            placeholder.text = "待办事项";              // 运行期被 mod 换成聊天占位文案
            placeholder.supportRichText = true;        // 原生 rich=True
            placeholder.horizontalOverflow = HorizontalWrapMode.Wrap;
            placeholder.verticalOverflow = VerticalWrapMode.Truncate;

            var textHolder = Node("Text", input);      // 原生叫 input-text
            Stretch(textHolder);
            var text = AddLabel(textHolder, BodyFontSize, InputTextColor, TextAnchor.UpperLeft);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.lineSpacing = 0.92f;                  // 原生 0.92
            text.material = LoadMaterial("widget-text-alpha-5x-thick");

            var field = input.gameObject.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.targetGraphic = fieldBg;
            // 原生就是多行：回车由 mod 挂在 OnNewLine 上接管（当发送键）
            field.lineType = InputField.LineType.MultiLineNewline;
            field.contentType = InputField.ContentType.Standard;
            field.characterLimit = 2000;               // 原生 2000
            field.caretBlinkRate = 0.85f;
            field.caretWidth = 1;
            field.customCaretColor = true;              // 原生 customCaret=True
            field.caretColor = InputCaretColor;
            field.selectionColor = InputSelectionColor;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.verticalScrollbar = BuildScrollbar(scrollView);
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scrollRect.verticalScrollbarSpacing = 0f;
            return scrollView;
        }

        /// <summary>右侧细滚动条（原生 6 宽，Handle 2 宽、上下各 3 的留白）。</summary>
        private static Scrollbar BuildScrollbar(RectTransform parent)
        {
            var bar = Node("Scrollbar Vertical", parent);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 1f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(ScrollBarWidth, 0f);
            var barImg = bar.gameObject.AddComponent<Image>();
            barImg.sprite = null;
            barImg.type = Image.Type.Sliced;
            barImg.color = ScrollbarBgColor;           // #000000DE
            barImg.raycastTarget = true;

            var area = Node("Sliding Area", bar);
            Stretch(area);

            var handle = Node("Handle", area);
            Stretch(handle, left: 2f, top: 3f, right: 2f, bottom: 3f);   // 原生 sd=(-4,-6)
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.sprite = null;
            handleImg.type = Image.Type.Sliced;
            handleImg.color = ScrollbarHandleColor;    // #FFFFFF8F
            handleImg.raycastTarget = true;

            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImg;       // 原生 targetGraphic=Handle
            scrollbar.numberOfSteps = 0;
            scrollbar.size = 1f;
            scrollbar.value = 0f;
            scrollbar.transition = Selectable.Transition.ColorTint;
            return scrollbar;
        }

        // ---------------------------------------------------------------- 工具

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

        private static void TopBar(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, height);
        }

        private static void BottomBar(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, height);
        }

        private static Text AddLabel(RectTransform rt, int size, Color color, TextAnchor anchor)
        {
            return AddLabel(rt, size, color, anchor, LoadMaterial("widget-text-alpha-5x"));
        }

        /// <summary>material 传 null 表示用默认 UI 材质（原生有几处就是这样）。</summary>
        private static Text AddLabel(RectTransform rt, int size, Color color, TextAnchor anchor, Material material)
        {
            var text = rt.gameObject.AddComponent<Text>();
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.material = material;
            // 字体填**内置**字体只为了避免占位：原生字体 SAIRAB/SAIRASB 不含中文，
            // 中文要靠 mod 在运行期换成游戏当前语言的字体（`FontProvider` / `UiStyle`，
            // 且 mod 是**无条件覆盖**的，prefab 里填什么都会被换掉）。
            // 不能留空：UGUI 的修改版 TextGenerator 在 font == null 时算 preferredHeight
            // 会抛 NullReferenceException（ContentSizeFitter 一触发就刷屏）。
            text.font = BuiltinFont;
            return text;
        }

        private static Sprite LoadSprite(string fileName)
        {
            var path = SpriteDir + fileName + ".png";
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogWarning("[IcarusII] 找不到贴图：" + path);
            }
            return sprite;
        }

        /// <summary>图标目录（信标待办框工具条上的两张图在这里）。</summary>
        private static Sprite LoadIconSprite(string fileName)
        {
            var path = IconDir + fileName + ".png";
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogWarning("[IcarusII] 找不到图标：" + path);
            }
            return sprite;
        }

        /// <summary>UGUI 内置的遮罩图（原生 viewport 用的就是它）。</summary>
        private static Sprite LoadBuiltinMaskSprite()
        {
            var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd");
            if (sprite == null)
            {
                Debug.LogWarning("[IcarusII] 取不到内置 UIMask 贴图");
            }
            return sprite;
        }

        private static Material LoadMaterial(string fileName)
        {
            var path = MaterialDir + fileName + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Debug.LogWarning("[IcarusII] 找不到材质：" + path);
            }
            return material;
        }

        private static Color Rgba(int r, int g, int b, int a)
        {
            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }
    }
}
