using System;
using System.Collections.Generic;
using IcarusII.Common.Protocol;
using IcarusII.Game.Tools;
using IcarusII.I18n;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 设定页「工具」分区：完整工具目录 + 开关；`mod` 页签改为列出其他 mod。
    ///
    /// 目录与开关以 sidecar 为权威：每次改动都发 tools.set，回执带回新目录；
    /// 列表只是把回执渲染到 prefab 的 tool-row 模板，并按 group 分节。
    /// mod 页签直接读本机的 PluginInfos 与 ModPolicy，不经过 sidecar。
    /// </summary>
    internal sealed class SettingsToolsPage
    {
        private const string ModGroup = "thirdparty";

        private static readonly string[] GroupOrder =
        {
            "game.read", "game.write", "kb", "blueprint", "other",
        };

        private sealed class RowBinding
        {
            public ToolCatalogEntryInfo Entry;
            public Text Name;
            public Text Id;
            public Text Description;
            public Text Access;
            public Image AccessPill;
            public Button Toggle;
            public UISwitch Switch;
        }

        private sealed class ModEntry
        {
            public string Guid;
            public string Name;
            public string Version;
            public int ConfigCount;
            public bool LoadFailed;
            public bool Enabled;
            public string Permission;
            public AdapterStatus Adapter;
        }

        private readonly SettingsPageView _view;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private List<ToolCatalogEntryInfo> _catalog = new List<ToolCatalogEntryInfo>();
        private string _group;
        private string _search = string.Empty;
        private string _legend;
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
            BindChip(_view.ToolChipScript, "other");
            BindChip(_view.ToolChipThird, ModGroup);
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
            if (_group == ModGroup && _modVersion != ModManifest.Version) Render();

            var state = ModelSettingsTab.TakeToolsStateResult();
            if (state != null && state.Ok)
            {
                _disabled.Clear();
                foreach (var name in state.Disabled) _disabled.Add(name);
                if (state.ToolCatalog.Count > 0) _catalog = new List<ToolCatalogEntryInfo>(state.ToolCatalog);
                _loaded = true;
                Render();
                SetStatus(Strings.Get("settings.tools.saved"), 2f);
            }
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime > _statusUntil) ClearStatus();
        }

        private float _nextRetry;
        private string _status;
        private float _statusUntil;
        private int _modVersion = -1;

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
            if (_group == ModGroup)
            {
                EnableAllMods();
                return;
            }
            _disabled.Clear();
            Save();
        }

        private void EnableAllMods()
        {
            foreach (var info in ModBridge.AllPlugins())
            {
                var guid = info.Metadata.GUID;
                if (!string.IsNullOrEmpty(guid)) ModPolicy.SetEnabled(guid, true);
            }
            ModManifest.Reset();
            Plugin.Instance?.NotifyExtensionsChanged();
            Render();
        }

        private void Toggle(ToolCatalogEntryInfo entry)
        {
            if (!_disabled.Remove(entry.Name)) _disabled.Add(entry.Name);
            Save();
        }

        private void ToggleMod(string guid)
        {
            ModPolicy.SetEnabled(guid, !ModPolicy.IsEnabled(guid));
            ModManifest.Reset();
            Plugin.Instance?.NotifyExtensionsChanged();
            Render();
        }

        private void CycleModPermission(string guid)
        {
            ModPolicy.CyclePermission(guid);
            Render();
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

            if (_group == ModGroup)
            {
                RenderMods();
                RenderChips();
                return;
            }

            var shown = 0;
            foreach (var group in GroupOrder)
            {
                var entries = new List<ToolCatalogEntryInfo>();
                foreach (var entry in _catalog)
                {
                    var entryGroup = GroupOf(entry);
                    if (entryGroup == ModGroup || entryGroup != group) continue;
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
            {
                var visible = 0;
                var off = 0;
                foreach (var entry in _catalog)
                {
                    if (GroupOf(entry) == ModGroup) continue;
                    visible++;
                    if (_disabled.Contains(entry.Name)) off++;
                }
                _view.ToolsLegend.text = $"{_legend}  {Strings.Get("settings.tools.count", visible, off)}";
            }
        }

        private void RenderMods()
        {
            var mods = new List<ModEntry>();
            foreach (var info in ModBridge.AllPlugins())
            {
                var guid = info.Metadata.GUID ?? string.Empty;
                if (guid.Length == 0) continue;
                mods.Add(new ModEntry
                {
                    Guid = guid,
                    Name = info.Metadata.Name ?? guid,
                    Version = info.Metadata.Version?.ToString() ?? string.Empty,
                    ConfigCount = info.Instance?.Config is null ? 0 : ModBridge.Entries(info).Count,
                    LoadFailed = info.Instance is null,
                    Enabled = ModPolicy.IsEnabled(guid),
                    Permission = ModPolicy.PermissionFor(guid),
                    Adapter = ModManifest.StatusFor(guid),
                });
            }

            var enabled = 0;
            foreach (var mod in mods) if (mod.Enabled) enabled++;

            var shown = 0;
            foreach (var mod in mods)
            {
                if (!MatchesMod(mod)) continue;
                RenderModRow(mod);
                shown++;
            }
            if (shown == 0) AddEmptyRow(Strings.Get("settings.tools.mod.empty"));

            if (_view.ToolsLegend != null)
                _view.ToolsLegend.text =
                    $"{Strings.Get("settings.tools.mod.legend")}  " +
                    Strings.Get("settings.tools.mod.count", mods.Count, enabled);
            _modVersion = ModManifest.Version;
        }

        private bool Matches(ToolCatalogEntryInfo entry)
        {
            // 顶栏分类：分组筛选必须在这里生效；Render 只负责按 GroupOrder 分节。
            if (_group is not null && GroupOf(entry) != _group) return false;

            var query = (_search ?? string.Empty).Trim();
            if (query.Length == 0) return true;
            if (!string.IsNullOrEmpty(entry.Name)
                && entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(entry.Label)
                && entry.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private bool MatchesMod(ModEntry mod)
        {
            var query = (_search ?? string.Empty).Trim();
            if (query.Length == 0) return true;
            return (!string.IsNullOrEmpty(mod.Name)
                    && mod.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                || mod.Guid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GroupOf(ToolCatalogEntryInfo entry)
        {
            var group = entry.Group;
            if (group == "game.read" || group == "game.write" || group == "kb"
                || group == "blueprint" || group == ModGroup) return group;
            // 旧 sidecar 的 script/ask 与未知分组统一进「其他」。
            if (group == "script" || group == "ask" || group == "other") return "other";
            // 旧 sidecar 没有 group 字段：按风险粗分，至少不会全部堆到「其他」。
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
                case ModGroup: return Strings.Get("settings.tools.group.third");
                default: return Strings.Get("settings.tools.group.other");
            }
        }

        private void RenderRow(ToolCatalogEntryInfo entry)
        {
            var node = NewRow();
            if (node == null) return;
            var enabled = !_disabled.Contains(entry.Name);
            var row = BindRow(node);
            row.Entry = entry;
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
            if (row.Description != null)
            {
                row.Description.text = entry.Description ?? string.Empty;
                row.Description.gameObject.SetActive(!string.IsNullOrEmpty(row.Description.text));
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

        private void RenderModRow(ModEntry mod)
        {
            var node = NewRow();
            if (node == null) return;
            var row = BindRow(node);
            if (row.Name != null)
            {
                row.Name.text = mod.Name;
                row.Name.color = new Color(1f, 1f, 1f, mod.Enabled ? 0.784f : 0.35f);
            }
            if (row.Id != null)
            {
                var version = string.IsNullOrEmpty(mod.Version) ? string.Empty : $" · v{mod.Version}";
                row.Id.text = $"{mod.Guid}{version} · {mod.ConfigCount} 个配置项";
                row.Id.color = new Color(1f, 1f, 1f, mod.Enabled ? 0.42f : 0.2f);
            }
            if (row.Description != null)
            {
                row.Description.text = AdapterLabel(mod.Adapter);
                row.Description.gameObject.SetActive(true);
                row.Description.color = new Color(1f, 1f, 1f, mod.Enabled ? 0.55f : 0.3f);
            }
            if (row.Access != null)
            {
                row.Access.text = TierLabel(mod.Permission);
                row.Access.color = TierColor(mod.Permission);
            }
            if (row.AccessPill != null)
            {
                var color = TierColor(mod.Permission);
                row.AccessPill.color = new Color(color.r, color.g, color.b, mod.Enabled ? 0.16f : 0.07f);
            }
            var pillButton = FindComponent<Button>(node.transform, "info/name-row/access-pill");
            if (pillButton != null) pillButton.onClick.AddListener(() => CycleModPermission(mod.Guid));
            if (row.Switch != null) row.Switch.SetToggleNoEvent(mod.Enabled);
            if (row.Toggle != null) row.Toggle.onClick.AddListener(() => ToggleMod(mod.Guid));
        }

        private GameObject NewRow()
        {
            var template = _view.ToolRowTemplate;
            return template == null ? null : Instantiate(template, _view.ToolsList, "tool-row");
        }

        private static RowBinding BindRow(GameObject node)
        {
            return new RowBinding
            {
                Name = FindText(node.transform, "info/name-row/name"),
                Id = FindText(node.transform, "info/id"),
                Description = FindText(node.transform, "info/desc"),
                Access = FindText(node.transform, "info/name-row/access-pill/access"),
                AccessPill = FindComponent<Image>(node.transform, "info/name-row/access-pill"),
                Toggle = FindComponent<Button>(node.transform, "toggle"),
                Switch = FindComponent<UISwitch>(node.transform, "toggle"),
            };
        }

        private static string AdapterLabel(AdapterStatus status)
        {
            switch (status.Kind)
            {
                case AdapterKind.Builtin:
                    return Strings.Get("settings.tools.mod.adapter.builtin", status.ToolCount);
                case AdapterKind.Bundled:
                    return Strings.Get("settings.tools.mod.adapter.bundled", status.ToolCount);
                case AdapterKind.External:
                    return Strings.Get("settings.tools.mod.adapter.external", status.ToolCount);
                case AdapterKind.Ai:
                    return Strings.Get("settings.tools.mod.adapter.ai", status.ToolCount);
                case AdapterKind.VersionMismatch:
                    return Strings.Get("settings.tools.mod.adapter.mismatch");
                case AdapterKind.Invalid:
                    return Strings.Get("settings.tools.mod.adapter.invalid");
                case AdapterKind.Unavailable:
                    return Strings.Get("settings.tools.mod.adapter.unavailable");
                default:
                    return Strings.Get("settings.tools.mod.adapter.none");
            }
        }

        private static string TierLabel(string tier)
        {
            if (tier == Protocol.PermissionTakeover) return Strings.Get("settings.tools.access.takeover");
            if (tier == Protocol.PermissionReadonly) return Strings.Get("settings.tools.access.readonly");
            return Strings.Get("settings.tools.access.limited");
        }

        private static Color TierColor(string tier)
        {
            if (tier == Protocol.PermissionTakeover) return new Color(0.99f, 0.59f, 0.37f, 1f);
            if (tier == Protocol.PermissionReadonly) return new Color(0.72f, 0.72f, 0.72f, 1f);
            return new Color(0.35f, 0.75f, 1f, 1f);
        }

        private void RenderChips()
        {
            TintChip(_view.ToolChipAll, _group is null);
            TintChip(_view.ToolChipRead, _group == "game.read");
            TintChip(_view.ToolChipWrite, _group == "game.write");
            TintChip(_view.ToolChipKb, _group == "kb");
            TintChip(_view.ToolChipBlueprint, _group == "blueprint");
            TintChip(_view.ToolChipScript, _group == "other");
            TintChip(_view.ToolChipThird, _group == ModGroup);
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
            var node = NewRow();
            if (node == null) return;
            node.name = "tool-empty";
            var name = FindText(node.transform, "info/name-row/name");
            if (name != null) name.text = text;
            var id = FindText(node.transform, "info/id");
            if (id != null) id.text = string.Empty;
            var desc = FindText(node.transform, "info/desc");
            if (desc != null) desc.gameObject.SetActive(false);
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
