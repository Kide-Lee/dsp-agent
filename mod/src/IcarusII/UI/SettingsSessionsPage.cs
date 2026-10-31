using System;
using System.Collections.Generic;
using IcarusII.Common.Protocol;
using IcarusII.I18n;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 设定页「会话」分区：跨存档索引、搜索、只看当前存档、重命名/删除/导出。
    ///
    /// 索引来自 sidecar 的 conversations.indexed；本类只负责把结果渲染到
    /// prefab 里的 conv-group / conv-row 模板，并在写操作后重新拉取索引。
    /// </summary>
    internal sealed class SettingsSessionsPage
    {
        /// <summary>筛选后的一个存档分组；原始解析结果不可变，这里只带可见的会话子集。</summary>
        private sealed class GroupView
        {
            public string SaveKey;
            public string SaveName;
            public long LastSeen;
            public bool Current;
            public readonly List<ConversationIndexEntryInfo> Conversations = new List<ConversationIndexEntryInfo>();
        }

        private sealed class RowBinding
        {
            public GroupView Group;
            public ConversationIndexEntryInfo Info;
            public Text Title;
            public Button Export;
            public InputField RenameInput;
        }

        private readonly SettingsPageView _view;
        private readonly List<RowBinding> _rows = new List<RowBinding>();
        private ConversationsIndexedInfo _index;
        private bool _loading;
        private float _requestedAt;
        private int _timeouts;
        private bool _onlyCurrent;
        private string _search = string.Empty;
        private string _status;
        private float _statusUntil;
        private float _nextRetry;
        private RowBinding _renaming;

        public SettingsSessionsPage(SettingsPageView view)
        {
            _view = view;
            Wire();
            // 一进设置就拉索引：切到会话页时内容已经就绪，不在切页瞬间空一下。
            EnsureLoaded();
        }

        private void Wire()
        {
            if (_view.SessionSearch != null)
                _view.SessionSearch.onValueChanged.AddListener(value =>
                {
                    _search = value ?? string.Empty;
                    Render();
                });
            if (_view.SessionsOnlyCurrent != null)
                _view.SessionsOnlyCurrent.onClick.AddListener(() =>
                {
                    _onlyCurrent = !_onlyCurrent;
                    Render();
                });
        }

        /// <summary>切到本分区时调用：第一次进入就发索引请求。</summary>
        public void EnsureLoaded()
        {
            if (_index != null || _loading) return;
            RequestIndex();
        }

        public void RequestIndex()
        {
            if (!ModelSettingsTab.RequestConversationsIndex())
            {
                SetStatus(Strings.Get("settings.sessions.offline"), 3f);
                _loading = false;
                Render();
                return;
            }
            _loading = true;
            _requestedAt = Time.unscaledTime;
        }

        public void Tick()
        {
            // 请求已发出但 5 秒没有回执（断线/旧端）时重试，避免一直停在加载态。
            if (_loading && Time.unscaledTime - _requestedAt > 5f)
            {
                _loading = false;
                _timeouts++;
                // 连发两次都没有回执，基本可以断定对端不支持 conversations.index。
                if (_index is null && _timeouts >= 2) SetStatus(Strings.Get("settings.sessions.offline"), 4f);
            }
            if (_index is null && !_loading && Time.unscaledTime > _nextRetry)
            {
                _nextRetry = Time.unscaledTime + 2f;
                RequestIndex();
            }

            var result = ModelSettingsTab.TakeConversationsIndexResult();
            if (result != null)
            {
                _index = result;
                _loading = false;
                _timeouts = 0;
                Render();
            }
            if (ModelSettingsTab.TakeConversationsDirty()) RequestIndex();

            var export = ModelSettingsTab.TakeConversationExportResult();
            if (export != null) HandleExportResult(export);

            var actionError = ModelSettingsTab.TakeConversationActionError();
            if (!string.IsNullOrEmpty(actionError))
            {
                SetStatus(actionError == "busy"
                    ? Strings.Get("settings.sessions.delete.busy")
                    : Strings.Get("settings.sessions.delete.failed", actionError), 4f);
            }

            if (_renaming != null && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                VFInput.UseEscape();
                CancelRename();
            }

            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime > _statusUntil)
            {
                _status = null;
                RenderCount();
            }
        }

        private void HandleExportResult(ConversationsExportedInfo export)
        {
            if (export.Ok)
            {
                SetStatus(Strings.Get("settings.sessions.export.ok", export.Path ?? string.Empty), 5f);
                GUIUtility.systemCopyBuffer = export.Path;
            }
            else if (export.Error == "empty")
            {
                SetStatus(Strings.Get("settings.sessions.export.empty"), 3f);
            }
            else
            {
                SetStatus(Strings.Get("settings.sessions.export.failed", export.Error ?? "io_error"), 4f);
            }
        }

        private void SetStatus(string text, float seconds)
        {
            _status = text;
            _statusUntil = Time.unscaledTime + seconds;
            if (_view.SessionsCount != null) _view.SessionsCount.text = text;
        }

        private void Render()
        {
            if (_view.SessionsList == null) return;
            CancelRename();
            ClearChildren(_view.SessionsList);
            _rows.Clear();

            var visible = FilteredGroups();
            var conversationCount = 0;
            var saveCount = 0;
            foreach (var group in visible)
            {
                saveCount++;
                conversationCount += group.Conversations.Count;
                RenderGroup(group);
            }

            if (_view.SessionsCount != null && string.IsNullOrEmpty(_status))
                _view.SessionsCount.text = Strings.Get("settings.sessions.count", conversationCount, saveCount);
            if (_view.SessionsOnlyCurrent != null && _view.SessionsOnlyCurrent.targetGraphic is Image bg)
            {
                bg.color = _onlyCurrent
                    ? new Color(0.353f, 0.749f, 1f, 0.35f)
                    : new Color(0f, 0f, 0f, 0.38f);
            }
            if (saveCount == 0 && _index is not null)
                AddEmptyRow(Strings.Get("settings.sessions.empty"));
        }

        private void RenderCount()
        {
            if (_index is null || _view.SessionsCount == null) return;
            var visible = FilteredGroups();
            var conversationCount = 0;
            var saveCount = 0;
            foreach (var group in visible)
            {
                saveCount++;
                conversationCount += group.Conversations.Count;
            }
            _view.SessionsCount.text = Strings.Get("settings.sessions.count", conversationCount, saveCount);
        }

        private List<GroupView> FilteredGroups()
        {
            var result = new List<GroupView>();
            if (_index is null) return result;
            foreach (var group in _index.Saves)
            {
                if (_onlyCurrent && !group.Current) continue;
                var view = new GroupView
                {
                    SaveKey = group.SaveKey,
                    SaveName = group.SaveName ?? group.SaveKey,
                    LastSeen = group.LastSeen,
                    Current = group.Current,
                };
                foreach (var conversation in group.Conversations)
                {
                    if (!MatchesSearch(view, conversation)) continue;
                    view.Conversations.Add(conversation);
                }
                if (view.Conversations.Count > 0) result.Add(view);
            }
            return result;
        }

        private bool MatchesSearch(GroupView group, ConversationIndexEntryInfo conversation)
        {
            var query = (_search ?? string.Empty).Trim();
            if (query.Length == 0) return true;
            if (!string.IsNullOrEmpty(conversation.Title)
                && conversation.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(group.SaveName)
                && group.SaveName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void RenderGroup(GroupView group)
        {
            var template = _view.ConversationGroupTemplate;
            if (template == null) return;
            var header = Instantiate(template, _view.SessionsList, "conv-group");
            SetText(header.transform, "name", group.SaveName);
            var pill = header.transform.Find("current-pill");
            if (pill != null) pill.gameObject.SetActive(group.Current);
            SetText(header.transform, "save-key", ShortKey(group.SaveKey));
            SetText(header.transform, "meta",
                Strings.Get("settings.sessions.group.meta", group.Conversations.Count, FormatTime(group.LastSeen)));
            foreach (var conversation in group.Conversations) RenderRow(group, conversation);
        }

        private void RenderRow(GroupView group, ConversationIndexEntryInfo conversation)
        {
            var template = _view.ConversationRowTemplate;
            if (template == null) return;
            var node = Instantiate(template, _view.SessionsList, "conv-row");
            var row = new RowBinding
            {
                Group = group,
                Info = conversation,
                Title = FindText(node.transform, "title"),
                Export = FindButton(node.transform, "export-button"),
                RenameInput = node.transform.Find("rename-input")?.GetComponent<InputField>(),
            };
            if (row.Title != null)
            {
                var title = string.IsNullOrEmpty(conversation.Title)
                    ? Strings.Get("settings.sessions.untitled")
                    : conversation.Title;
                row.Title.text = title;
            }
            var pill = node.transform.Find("current-pill");
            if (pill != null)
            {
                pill.gameObject.SetActive(conversation.IsActive);
                var label = pill.Find("label")?.GetComponent<Text>();
                if (label != null) label.text = Strings.Get("settings.sessions.active");
            }
            var meta = FindText(node.transform, "meta");
            if (meta != null)
                meta.text = Strings.Get("settings.sessions.row.meta", conversation.MessageCount,
                    FormatTime(conversation.UpdatedAt));
            if (row.Export != null)
            {
                row.Export.interactable = conversation.MessageCount > 0;
                SetButtonLabel(row.Export, Strings.Get("settings.sessions.export"));
                row.Export.onClick.AddListener(() => Export(row));
            }
            var rename = FindButton(node.transform, "rename-button");
            if (rename != null)
            {
                SetButtonLabel(rename, Strings.Get("settings.sessions.rename"));
                rename.onClick.AddListener(() => BeginRename(row));
            }
            var delete = FindButton(node.transform, "delete-button");
            if (delete != null)
            {
                SetButtonLabel(delete, Strings.Get("settings.sessions.delete"));
                delete.onClick.AddListener(() => ConfirmDelete(row));
            }
            if (row.RenameInput != null)
                row.RenameInput.onEndEdit.AddListener(value => SubmitRename(row, value));
            _rows.Add(row);
        }

        private void AddEmptyRow(string text)
        {
            if (_view.ConversationGroupTemplate == null) return;
            var node = Instantiate(_view.ConversationGroupTemplate, _view.SessionsList, "conv-empty");
            SetText(node.transform, "name", text);
            SetText(node.transform, "save-key", string.Empty);
            SetText(node.transform, "meta", string.Empty);
        }

        private void Export(RowBinding row)
        {
            if (row.Info.MessageCount <= 0) return;
            if (!ModelSettingsTab.RequestConversationExport(row.Group.SaveKey, row.Info.Id))
            {
                SetStatus(Strings.Get("settings.sessions.offline"), 3f);
                return;
            }
            SetStatus(Strings.Get("settings.sessions.export") + "…", 2f);
        }

        private void BeginRename(RowBinding row)
        {
            CancelRename();
            if (row.RenameInput == null || row.Title == null) return;
            _renaming = row;
            row.RenameInput.text = row.Info.Title ?? string.Empty;
            row.RenameInput.gameObject.SetActive(true);
            row.Title.gameObject.SetActive(false);
            row.RenameInput.ActivateInputField();
            row.RenameInput.Select();
        }

        private void CancelRename()
        {
            if (_renaming is null) return;
            var row = _renaming;
            _renaming = null;
            if (row.RenameInput != null)
            {
                row.RenameInput.text = string.Empty;
                row.RenameInput.gameObject.SetActive(false);
            }
            if (row.Title != null) row.Title.gameObject.SetActive(true);
        }

        private void SubmitRename(RowBinding row, string value)
        {
            if (_renaming != row) return;
            var title = (value ?? string.Empty).Trim();
            CancelRename();
            if (title.Length == 0) return;
            if (!ModelSettingsTab.RequestConversationRename(row.Group.SaveKey, row.Info.Id, title))
            {
                SetStatus(Strings.Get("settings.sessions.offline"), 3f);
                return;
            }
            if (row.Title != null) row.Title.text = title;
            SetStatus(Strings.Get("settings.sessions.rename.done"), 2f);
            RequestIndex();
        }

        private void ConfirmDelete(RowBinding row)
        {
            var title = string.IsNullOrEmpty(row.Info.Title)
                ? Strings.Get("settings.sessions.untitled")
                : row.Info.Title;
            UIMessageBox.Show(
                Strings.Get("settings.sessions.delete.title"),
                Strings.Get("settings.sessions.delete.body", title),
                Strings.Get("settings.sessions.delete"),
                Strings.Get("settings.cancel"),
                UIMessageBox.QUESTION,
                () => Delete(row),
                null);
        }

        private void Delete(RowBinding row)
        {
            if (!ModelSettingsTab.RequestConversationDelete(row.Group.SaveKey, row.Info.Id))
            {
                SetStatus(Strings.Get("settings.sessions.offline"), 3f);
                return;
            }
            RequestIndex();
        }

        private static string ShortKey(string saveKey)
        {
            if (string.IsNullOrEmpty(saveKey)) return string.Empty;
            return saveKey.Length <= 8 ? saveKey : saveKey.Substring(0, 8);
        }

        private static string FormatTime(long unixMs)
        {
            if (unixMs <= 0) return "—";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("MM-dd HH:mm");
            }
            catch
            {
                return "—";
            }
        }

        private static GameObject Instantiate(GameObject template, RectTransform parent, string name)
        {
            var node = UnityEngine.Object.Instantiate(template, parent, false);
            node.name = name;
            node.SetActive(true);
            return node;
        }

        private static void ClearChildren(RectTransform parent)
        {
            for (var i = parent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
        }

        private static Text FindText(Transform root, string name)
        {
            var node = root.Find(name);
            return node != null ? node.GetComponent<Text>() : null;
        }

        private static Button FindButton(Transform root, string name)
        {
            var node = root.Find(name);
            return node != null ? node.GetComponent<Button>() : null;
        }

        private static void SetButtonLabel(Button button, string text)
        {
            var label = button.GetComponentInChildren<Text>(true);
            if (label != null) label.text = text;
        }

        private static void SetText(Transform root, string name, string value)
        {
            var text = FindText(root, name);
            if (text != null) text.text = value;
        }
    }
}
