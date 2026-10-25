using System;
using UnityEditor;
using UnityEngine;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 统一 ChatWindow / AgentButton 的 UIButton.tips 文案。
    /// 只改 tips 字段，不动布局、颜色、按钮事件。
    /// </summary>
    public static class IcarusIITipFix
    {
        [MenuItem("Icarus II/Fix Button Tips")]
        public static void FixFromMenu()
        {
            var ok = FixAll();
            EditorUtility.DisplayDialog("Icarus II", ok ? "按钮 Tips 已更新" : "修复失败，见 Console", "好");
        }

        public static void FixBatch()
        {
            var ok = FixAll();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool FixAll()
        {
            bool ok = FixPrefab("Assets/IcarusII/ChatWindow.prefab");
            ok |= FixPrefab("Assets/IcarusII/button-agent.prefab");
            return ok;
        }

        private static bool FixPrefab(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) return false;
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var buttons = root.GetComponentsInChildren<UIButton>(true);
                for (int i = 0; i < buttons.Length; i++)
                    Apply(buttons[i]);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.Refresh();
                Debug.Log("[IcarusII] 已更新按钮 tips：" + path);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void Apply(UIButton button)
        {
            if (button == null) return;
            var p = Path(button.transform);
            var t = button.tips;

            if (Ends(p, "close-wnd-btn"))
                Set(ref t, 7, "关闭窗口", "关闭聊天窗口");
            else if (Ends(p, "setting-wnd-btn/menubtn"))
                Set(ref t, 3, "设置", "打开 Icarus II 设置");
            else if (Ends(p, "turn-row-template/delete"))
                Set(ref t, 7, "删除", "删除这一轮问答");
            else if (Ends(p, "footer/copy"))
                Set(ref t, 7, "复制", "复制这条回答");
            else if (Ends(p, "loading/header"))
                Set(ref t, 7, "运行详情", "展开或收起思考与工具调用");
            else if (Ends(p, "thinking-block/header"))
                Set(ref t, 7, "思考", "展开或收起这段思考");
            else if (Ends(p, "tools/tool-template/header"))
                Set(ref t, 7, "工具调用", "展开或收起这次工具调用");
            else if (Ends(p, "text-color-btn"))
                Set(ref t, 6, "文字颜色", "设置输入文字颜色");
            else if (Ends(p, "checkbox-btn"))
                Set(ref t, 6, "复选框", "插入一个待办复选框");
            else if (Ends(p, "icon-select-btn"))
                Set(ref t, 6, "图标", "插入游戏图标");
            else if (Ends(p, "tool-bar/icons/tool-btn"))
                Set(ref t, 6, "工具列表", "打开可用工具列表");
            else if (Ends(p, "tool-bar/send-button"))
                Set(ref t, 7, "发送", "发送消息（Enter）");
            else if (Ends(p, "input/editor/scroll-view/viewport/content/InputField/input"))
                Set(ref t, 7, "输入", "输入消息；Ctrl+Enter 换行");
            else if (Ends(p, "voting-device/scroll-view/viewport/content/InputField/input"))
                Set(ref t, 7, "输入答案", "输入自己的答案；Ctrl+Enter 换行");
            else if (Ends(p, "answer-input"))
                Set(ref t, 7, "自定义答案", "输入自己的答案");
            else if (Ends(p, "answer-input/input"))
                Set(ref t, 7, "输入答案", "输入自己的答案；Ctrl+Enter 换行");
            else if (Ends(p, "agree-button"))
                Set(ref t, 7, "同意", "允许这次写操作");
            else if (Ends(p, "skip-button"))
                Set(ref t, 7, "跳过", "跳过当前问题");
            else if (Ends(p, "reject-button"))
                Set(ref t, 7, "拒绝", "拒绝这次写操作");
            else if (Ends(p, "return-button"))
                Set(ref t, 7, "返回", "返回上一题");
            else if (Contains(p, "answer-option-"))
            {
                int number = 0;
                int dash = p.LastIndexOf("answer-option-", StringComparison.Ordinal);
                if (dash >= 0 && dash + 14 < p.Length) int.TryParse(p.Substring(dash + 14, 1), out number);
                Set(ref t, 7, number > 0 ? "选项 " + number : "选项", "选择这个答案");
            }
            else if (p == "button-agent")
                Set(ref t, 7, "Icarus II", "打开聊天窗口（R）");

            button.tips = t;
        }

        private static void Set(ref UIButton.TipSettings t, int corner, string title, string text)
        {
            t.corner = corner;
            t.tipTitle = title ?? string.Empty;
            t.tipText = text ?? string.Empty;
            t.level = 0;
            t.delay = 0.2f;
            t.width = 0;
            // 文本 tip 不需要物品/配方格式；清掉旧模板的复制蓝图占位。
            t.type = UIButton.ItemTipType.Other;
            t.itemId = 0;
            t.itemCount = 0;
            t.itemInc = 0;
        }

        private static bool Ends(string value, string suffix)
        {
            return value != null && value.EndsWith(suffix, StringComparison.Ordinal);
        }

        private static bool Contains(string value, string part)
        {
            return value != null && value.IndexOf(part, StringComparison.Ordinal) >= 0;
        }

        private static string Path(Transform t)
        {
            if (t == null) return string.Empty;
            var s = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                s = t.name + "/" + s;
            }
            return s;
        }
    }
}
