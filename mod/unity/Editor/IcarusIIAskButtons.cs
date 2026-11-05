using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.EditorTools
{
    /// <summary>
    /// 作答面板按钮组的补齐。
    ///
    /// 91f5ea5 重建 prefab 时 `btn-grop` 里的 `return-button` 丢了，而面板代码对缺失是静默容忍的
    /// （`_backButton` 为 null 就不显示），所以「返回上一题」消失了一年都没人报错，直到玩家发现。
    /// 这里从同组的 `skip-button` 复制一份：组件、配色、`UIButton` 的 tips 一次带走，然后按固定槽位摆好。
    /// 槽位从右往左：同意 -20、返回/拒绝 -90、跳过 -160；
    /// 第一题没有「返回」时运行期把「跳过」挪到 -90（见 AskPanel.PositionSkip），所以这里的 -160 是「跳过」的归位点。
    /// 幂等：已经存在时只校正几何/文案，不重复创建。
    /// </summary>
    public static class IcarusIIAskButtons
    {
        private const string PrefabPath = "Assets/IcarusII/ChatWindow.prefab";
        private const string ButtonGroup = "content-column/choose/voting-device/btn-grop";
        private const string TemplateName = "skip-button";
        private const string BackName = "return-button";
        private const string BackText = "返回";
        private const string BackTipTitle = "返回";
        private const string BackTipText = "返回上一题";
        /// <summary>按钮等距排在右下角：同意 -20、返回/拒绝 -90、跳过 -160。</summary>
        private const float BackOffsetX = -90f;
        private const float SkipOffsetX = -160f;

        [MenuItem("Icarus II/Add Ask Back Button (补回返回按钮)")]
        public static void FixFromMenu()
        {
            var ok = Ensure(PrefabPath);
            EditorUtility.DisplayDialog("Icarus II", ok ? "已补回 return-button" : "失败，见 Console", "好");
        }

        public static bool Ensure(string prefabPath)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                Debug.LogWarning("[IcarusII] 打不开 prefab，无法补 return-button：" + prefabPath);
                return false;
            }

            try
            {
                var group = root.transform.Find(ButtonGroup);
                if (group == null)
                {
                    Debug.LogWarning("[IcarusII] 找不到 " + ButtonGroup + "，无法补 return-button");
                    return false;
                }

                var template = group.Find(TemplateName);
                if (template == null)
                {
                    Debug.LogWarning("[IcarusII] 找不到模板按钮 " + TemplateName + "，无法补 return-button");
                    return false;
                }

                var back = group.Find(BackName);
                bool created = false;
                if (back == null)
                {
                    back = Object.Instantiate(template.gameObject, group).transform;
                    back.name = BackName;
                    // 设计态默认隐藏；运行期由 AskPanel 按「是否还有上一题」开关。
                    back.gameObject.SetActive(false);
                    created = true;
                }

                bool changed = created;
                changed |= Normalize(back);
                changed |= PositionSkip(template);
                if (!changed) return true;

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Debug.Log(created
                    ? "[IcarusII] btn-grop 缺少 return-button，已按 " + TemplateName + " 补一份（返回上一题）"
                    : "[IcarusII] return-button 已校正文案/几何");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>「跳过」的归位点是左侧槽位；第一题的临时位置由 AskPanel 在运行期给。</summary>
        private static bool PositionSkip(Transform skip)
        {
            var rect = (RectTransform)skip;
            var wanted = new Vector2(SkipOffsetX, rect.anchoredPosition.y);
            if ((rect.anchoredPosition - wanted).sqrMagnitude <= 0.01f) return false;
            rect.anchoredPosition = wanted;
            return true;
        }

        private static bool Normalize(Transform back)
        {
            bool changed = false;

            var rect = (RectTransform)back;
            var wanted = new Vector2(BackOffsetX, 0f);
            if ((rect.anchoredPosition - wanted).sqrMagnitude > 0.01f)
            {
                rect.anchoredPosition = wanted;
                changed = true;
            }

            var label = back.Find("text");
            var text = label != null ? label.GetComponent<Text>() : null;
            if (text != null && text.text != BackText)
            {
                text.text = BackText;
                changed = true;
            }

            var uiButton = back.GetComponent("UIButton");
            if (uiButton != null)
            {
                var so = new SerializedObject(uiButton);
                var tipTitle = so.FindProperty("tips.tipTitle");
                var tipText = so.FindProperty("tips.tipText");
                bool tipsChanged = false;
                if (tipTitle != null && tipTitle.stringValue != BackTipTitle)
                {
                    tipTitle.stringValue = BackTipTitle;
                    tipsChanged = true;
                }
                if (tipText != null && tipText.stringValue != BackTipText)
                {
                    tipText.stringValue = BackTipText;
                    tipsChanged = true;
                }
                if (tipsChanged)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    changed = true;
                }
            }

            return changed;
        }
    }
}
