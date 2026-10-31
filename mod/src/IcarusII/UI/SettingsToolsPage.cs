using System;
using System.Collections.Generic;
using IcarusII.Common.Protocol;
using IcarusII.I18n;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 设定页「工具」分区：完整工具目录 + 开关。
    ///
    /// 目录与开关都以 sidecar 为权威：每次改动都发 tools.set，回执带回新目录；
    /// 列表只是把回执渲染到 prefab 的 tool-row 模板，并按 group 分节。
    /// 关闭工具不影响权限档位。
    /// </summary>
    internal sealed class SettingsToolsPage
    {
        private static readonly string[] GroupOrder =
        {
            "game.read", "game.write", "kb", "blueprint", "script", "thirdparty", "other",
        };

        private sealed class RowBinding
        {
            public ToolCatalogEntryInfo Entry;
            public Text Name;
            public Text Id;
            public Text Access;
            public Image AccessPill;
            public Button Toggle;
            public UISwitch Switch;
        }

        private readonly SettingsPageView _view;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private List<ToolCatalogEntryInfo> _catalog = new List<ToolCatalogEntryInfo>();
        private string _group;
        private string _search = string.Empty;
        private string _legend;
        private string _status;
        private float _statusUntil;
        private float _nextRetry;
        private bool _loaded;

        public SettingsToolsPage(SettingsPageView view)
        {
            _view = view;
            _legend = view.ToolsLegend != null ? view.ToolsLegend.text : string.Empty;
            Wire();
            // 目录通常已随 ready 到达；没有就主动拉一次，切页时不再显示空表。
            EnsureLoaded();
        }

        private void Wire()
        {
            if (_view.ToolSearch != null)
                _view.ToolSearch.onValueChanged.AddListener(value =>
                {
                    _search = value ?? string.Empty;
                    Render();
                });
            BindChip(_view.ToolChipAll, null);
            BindChip(_view.ToolChipRead, "game.read");
            BindChip(_view.ToolChipWrite, "game.write");
            BindChip(_view.ToolChipKb, "kb");
            BindChip(_view.ToolChipBlueprint, "blueprint");
            BindChip(_view.ToolChipScript, "script");
            BindChip(_view.ToolChipThird, "thirdparty");
            if (_view.ToolsEnableAll != null) _view.ToolsEnableAll.onClick.AddListener(EnableAll);
        }

        private void BindChip(Button chip, string group)
        {
            if (chip == null) return;
            chip.onClick.AddListener(() => SetGroup(group));
        }

        public void EnsureLoaded()
        {
            if (_loaded) return;
            PullFromModelTab();
            if (_catalog.Count == 0)
            {
                // 老目录可能在 ready 里没带全；用一次 tools.set 把完整目录拉回来。
                if (!ModelSettingsTab.RequestToolsSet(_disabled))
                {
                    SetStatus(Strings.Get("settings.sessions.offline"), 3f);
                    return;
                }
            }
            _loaded = true;
            Render();
        }

        public void Tick()
        {
            if (_catalog.Count == 0 && Time.unscaledTime > _nextRetry)
            {
                _nextRetry = Time.unscaledTime + 2f;
                PullFromModelTab();
                if (_catalog.Count == 0 && ModelSettingsTab.RequestToolsSet(_disabled)) _loaded = true;
                if (_catalog.Count > 0) Render();
            }

            if (ModelSettingsTab.TakeToolsDirty())
            {
                PullFromModelTab();
                _loaded = true;
                Render();
            }
            var state = ModelSettingsTab.TakeToolsStateResult();
            if (state != null && state.Ok)
            {
                _disabled.Clear();
                foreach (var name in state.Disabled) _disabled.Add(name);
                if (state.ToolCatalog.Count > 0) _catalog = state.ToolCatalog;
                _loaded = true;
                Render();
                SetStatus(Strings.Get("settings.tools.saved"), 2f);
            }
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime > _statusUntil) ClearStatus();
        }

        private void PullFromModelTab()
        {
            var catalog = ModelSettingsTab.CurrentToolCatalog;
            if (catalog != null && catalog.Count > 0) _catalog = new List<ToolCatalogEntryInfo>(catalog);
            _disabled.Clear();
            var disabled = ModelSettingsTab.CurrentDisabledTools;
            if (disabled != null)
            {
                foreach (var name in disabled) _disabled.Add(name);
            }
        }

        private void SetGroup(string group)
        {
            _group = group;
            Render();
        }

        private void EnableAll()
        {
            _disabled.Clear();
            Save();
        }

        private void Toggle(ToolCatalogEntryInfo entry)
        {
            if (!_disabled.Remove(entry.Name)) _disabled.Add(entry.Name);
            Save();
        }

        private void Save()
        {
            var list = new List<string>(_disabled);
            Render();
            if (ModelSettingsTab.RequestToolsSet(list)) return;
            PullFromModelTab();
            Render();
            SetStatus(Strings.Get("settings.sessions.offline"), 3f);
        }

        private void Render()
        {
            if (_view.ToolsList == null) return;
            ClearChildren(_view.ToolsList);

            var shown = 0;
            foreach (var group in GroupOrder)
            {
                var entries = new List<ToolCatalogEntryInfo>();
                foreach (var entry in _catalog)
                {
                    if (GroupOf(entry) != group) continue;
                    if (!Matches(entry)) continue;
                    entries.Add(entry);
                }
                if (entries.Count == 0) continue;
                if (_view.ToolGapTemplate != null && shown > 0)
                    Instantiate(_view.ToolGapTemplate, _view.ToolsList, "tool-gap");
                AddGroupTitle(group, entries.Count);
                foreach (var entry in entries)
                {
                    RenderRow(entry);
                    shown++;
                }
            }
            if (shown == 0) AddEmptyRow(Strings.Get("settings.tools.empty"));

            RenderChips();
            if (_view.ToolsLegend != null && string.IsNullOrEmpty(_status))
                _view.ToolsLegend.text = $"{_legend}  {Strings.Get("settings.tools.count", _catalog.Count, _disabled.Count)}";
        }

        private bool Matches(ToolCatalogEntryInfo entry)
        {
            if (_group is not null && GroupOf(entry) != _group) return false;
            var query = (_search ?? string.Empty).Trim();
            if (query.Length == 0) return true;
            if (!string.IsNullOrEmpty(entry.Name)
                && entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(entry.Label)
                && entry.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string GroupOf(ToolCatalogEntryInfo entry)
        {
            var group = entry.Group;
            if (group == "game.read" || group == "game.write" || group == "kb"
                || group == "blueprint" || group == "script" || group == "thirdparty") return group;
            // 旧 sidecar 没有 group 字段：按风险粗分，至少不会全部堆到「其它」。
            if (string.IsNullOrEmpty(group)) return entry.Risk > 0 ? "game.write" : "game.read";
            return "other";
        }

        private void AddGroupTitle(string group, int count)
        {
            var template = _view.ToolGroupTitleTemplate;
            if (template == null) return;
            var node = Instantiate(template, _view.ToolsList, "group-title");
            var text = node.GetComponent<Text>();
            if (text != null) text.text = $"{GroupLabel(group)} · {count}";
        }

        private static string GroupLabel(string group)
        {
            switch (group)
            {
                case "game.read": return Strings.Get("settings.tools.group.read");
                case "game.write": return Strings.Get("settings.tools.group.write");
                case "kb": return Strings.Get("settings.tools.group.kb");
                case "blueprint": return Strings.Get("settings.tools.group.bp");
                case "script": return Strings.Get("settings.tools.group.script");
                case "thirdparty": return Strings.Get("settings.tools.group.third");
                default: return Strings.Get("settings.tools.group.other");
            }
        }

        private void RenderRow(ToolCatalogEntryInfo entry)
        {
            var template = _view.ToolRowTemplate;
            if (template == null) return;
            var node = Instantiate(template, _view.ToolsList, "tool-row");
            var enabled = !_disabled.Contains(entry.Name);
            var row = new RowBinding
            {
                Entry = entry,
                Name = FindText(node.transform, "info/name-row/name"),
                Id = FindText(node.transform, "info/id"),
                Access = FindText(node.transform, "info/name-row/access-pill/access"),
                AccessPill = FindComponent<Image>(node.transform, "info/name-row/access-pill"),
                Toggle = FindComponent<Button>(node.transform, "toggle"),
                Switch = FindComponent<UISwitch>(node.transform, "toggle"),
            };
            if (row.Name != null)
            {
                row.Name.text = string.IsNullOrEmpty(entry.Label) ? entry.Name : entry.Label;
                row.Name.color = new Color(1f, 1f, 1f, enabled ? 0.784f : 0.35f);
            }
            if (row.Id != null)
            {
                var builtin = string.IsNullOrEmpty(entry.Source) || entry.Source == "builtin";
                row.Id.text = builtin
                    ? entry.Name
                    : $"{entry.Name} · {Strings.Get("settings.tools.source", entry.SourceName ?? entry.Source)}";
                row.Id.color = new Color(1f, 1f, 1f, enabled ? 0.42f : 0.2f);
            }
            if (row.Access != null)
            {
                row.Access.text = AccessLabel(entry.Risk);
                row.Access.color = AccessColor(entry.Risk);
            }
            if (row.AccessPill != null)
            {
                var color = AccessColor(entry.Risk);
                row.AccessPill.color = new Color(color.r, color.g, color.b, enabled ? 0.16f : 0.07f);
            }
            if (row.Switch != null) row.Switch.SetToggleNoEvent(enabled);
            if (row.Toggle != null) row.Toggle.onClick.AddListener(() => Toggle(row.Entry));
        }

        private void RenderChips()
        {
            TintChip(_view.ToolChipAll, _group is null);
            TintChip(_view.ToolChipRead, _group == "game.read");
            TintChip(_view.ToolChipWrite, _group == "game.write");
            TintChip(_view.ToolChipKb, _group == "kb");
            TintChip(_view.ToolChipBlueprint, _group == "blueprint");
            TintChip(_view.ToolChipScript, _group == "script");
            TintChip(_view.ToolChipThird, _group == "thirdparty");
        }

        private static void TintChip(Button chip, bool on)
        {
            if (chip == null) return;
            if (chip.targetGraphic is Image image)
                image.color = on ? new Color(0.353f, 0.749f, 1f, 0.35f) : new Color(0f, 0f, 0f, 0.38f);
        }

        private static string AccessLabel(int risk)
        {
            if (risk >= 2) return Strings.Get("settings.tools.access.takeover");
            if (risk == 1) return Strings.Get("settings.tools.access.limited");
            return Strings.Get("settings.tools.access.readonly");
        }

        private static Color AccessColor(int risk)
        {
            if (risk >= 2) return new Color(0.99f, 0.59f, 0.37f, 1f);
            return new Color(0.35f, 0.75f, 1f, 1f);
        }

        private void SetStatus(string text, float seconds)
        {
            _status = text;
            _statusUntil = Time.unscaledTime + seconds;
            if (_view.ToolsLegend != null) _view.ToolsLegend.text = text;
        }

        private void ClearStatus()
        {
            _status = null;
            if (_view.ToolsLegend != null) _view.ToolsLegend.text = _legend;
        }

        private void AddEmptyRow(string text)
        {
            var template = _view.ToolRowTemplate;
            if (template == null) return;
            var node = Instantiate(template, _view.ToolsList, "tool-empty");
            var name = FindText(node.transform, "info/name-row/name");
            if (name != null) name.text = text;
            var id = FindText(node.transform, "info/id");
            if (id != null) id.text = string.Empty;
            var pill = node.transform.Find("info/name-row/access-pill");
            if (pill != null) pill.gameObject.SetActive(false);
            var toggle = node.transform.Find("toggle");
            if (toggle != null) toggle.gameObject.SetActive(false);
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

        private static Text FindText(Transform root, string path)
        {
            var node = root.Find(path);
            return node != null ? node.GetComponent<Text>() : null;
        }

        private static T FindComponent<T>(Transform root, string path) where T : Component
        {
            var node = root.Find(path);
            return node != null ? node.GetComponent<T>() : null;
        }
    }
}
