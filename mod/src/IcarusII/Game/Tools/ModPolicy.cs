using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using IcarusII.Common.Json;
using IcarusII.Common.Protocol;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 每个第三方插件的开关与权限档位。
    ///
    /// 档案在 `BepInEx/config/icarus-ii/mods.json`：
    /// 未登记的插件默认关闭；权限默认「受限」——写操作仍走确认。
    /// 关闭的插件既不会被 mods_list 列出，也不会生成任何工具。
    /// </summary>
    internal static class ModPolicy
    {
        private const int SchemaVersion = 1;

        /// <summary>档位循环顺序：默认受限；点一下先降到只读，再点是接管，最后回受限。</summary>
        private static readonly string[] TierCycle =
        {
            Protocol.PermissionLimited,
            Protocol.PermissionReadonly,
            Protocol.PermissionTakeover,
        };

        private sealed class Entry
        {
            public bool Enabled;
            public string Permission = Protocol.PermissionLimited;
        }

        private static Dictionary<string, Entry> _entries;
        private static string _path;

        private static string PathFor()
        {
            if (_path is not null) return _path;
            var root = Path.Combine(Paths.ConfigPath ?? ".", "icarus-ii");
            _path = Path.Combine(root, "mods.json");
            return _path;
        }

        /// <summary>插件是否允许 AI 发现和操作。</summary>
        internal static bool IsEnabled(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            return EnsureLoaded().TryGetValue(guid, out var entry) && entry.Enabled;
        }

        /// <summary>该插件的写操作权限档位；未登记时按受限。</summary>
        internal static string PermissionFor(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return Protocol.PermissionLimited;
            return EnsureLoaded().TryGetValue(guid, out var entry) ? entry.Permission : Protocol.PermissionLimited;
        }

        internal static bool AnyEnabled()
        {
            foreach (var entry in EnsureLoaded().Values)
                if (entry.Enabled) return true;
            return false;
        }

        internal static void SetEnabled(string guid, bool enabled)
        {
            if (string.IsNullOrEmpty(guid)) return;
            var entries = EnsureLoaded();
            if (!entries.TryGetValue(guid, out var entry))
            {
                entry = new Entry();
                entries[guid] = entry;
            }
            if (entry.Enabled == enabled) return;
            entry.Enabled = enabled;
            Save();
        }

        /// <summary>按固定顺序切换档位并落盘，返回新档位。</summary>
        internal static string CyclePermission(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return Protocol.PermissionLimited;
            var entries = EnsureLoaded();
            if (!entries.TryGetValue(guid, out var entry))
            {
                entry = new Entry();
                entries[guid] = entry;
            }
            var index = Array.IndexOf(TierCycle, entry.Permission);
            entry.Permission = TierCycle[(index + 1 + TierCycle.Length) % TierCycle.Length];
            Save();
            return entry.Permission;
        }

        internal static void SetPermission(string guid, string permission)
        {
            if (string.IsNullOrEmpty(guid) || !Protocol.IsValidPermissionLevel(permission)) return;
            var entries = EnsureLoaded();
            if (!entries.TryGetValue(guid, out var entry))
            {
                entry = new Entry();
                entries[guid] = entry;
            }
            if (string.Equals(entry.Permission, permission, StringComparison.Ordinal)) return;
            entry.Permission = permission;
            Save();
        }

        internal static void Reload()
        {
            _entries = null;
        }

        private static Dictionary<string, Entry> EnsureLoaded()
        {
            if (_entries is not null) return _entries;
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            _entries = entries;
            try
            {
                var path = PathFor();
                if (!File.Exists(path)) return entries;
                var root = JsonValue.Parse(File.ReadAllText(path));
                if (root is null || !root.IsObject) return entries;
                foreach (var pair in root["mods"].Members)
                {
                    if (!pair.Value.IsObject) continue;
                    var entry = new Entry
                    {
                        Enabled = pair.Value["enabled"].AsBool(false),
                        Permission = pair.Value["permission"].AsString(Protocol.PermissionLimited),
                    };
                    if (!Protocol.IsValidPermissionLevel(entry.Permission))
                        entry.Permission = Protocol.PermissionLimited;
                    entries[pair.Key] = entry;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[mods] 读取 mods.json 失败：{ex.Message}");
            }
            return entries;
        }

        private static void Save()
        {
            try
            {
                var root = JsonValue.NewObject();
                root.Set("schema", SchemaVersion);
                var mods = JsonValue.NewObject();
                foreach (var pair in _entries)
                {
                    var item = JsonValue.NewObject();
                    item.Set("enabled", pair.Value.Enabled);
                    item.Set("permission", pair.Value.Permission);
                    mods.Set(pair.Key, item);
                }
                root.Set("mods", mods);

                var path = PathFor();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var temp = path + ".tmp";
                File.WriteAllText(temp, root.ToJson());
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[mods] 保存 mods.json 失败：{ex.Message}");
            }
        }
    }
}
