using System;
using IcarusII.I18n;

namespace IcarusII.UI
{
    /// <summary>
    /// 工具列表的一项（来自 ready.payload.toolCatalog；老服务端用 tools + 本地短名兜底）。
    /// 只做展示/筛选，不参与工具执行。
    /// </summary>
    internal sealed class ToolMenuItem
    {
        public string Name;
        public string Label;
        public string Description;

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (Name is not null && Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (Label is not null && Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (Description is not null && Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }

    /// <summary>
    /// 工具名 → 聊天窗里显示的中文短名。
    ///
    /// 实时工具块（Plugin 收到 tool_call/tool_activity）和历史回放（save.bound.history
    /// 的 steps）必须用同一套名字，否则同一个工具在重开存档后会变成生硬的英文 id。
    /// 因此放在 UI 层，由 Plugin 和 ChatWindow 共用。
    /// </summary>
    public static class ToolNames
    {
        public static string Label(string name)
        {
            if (string.IsNullOrEmpty(name)) return Strings.Get("tool.name");
            switch (name)
            {
                case "game_get_overview": return Strings.Get("tool.name.game_get_overview");
                case "game_get_player": return Strings.Get("tool.name.game_get_player");
                case "game_get_planet": return Strings.Get("tool.name.game_get_planet");
                case "game_get_power": return Strings.Get("tool.name.game_get_power");
                case "game_get_research": return Strings.Get("tool.name.game_get_research");
                case "game_get_production": return Strings.Get("tool.name.game_get_production");
                case "game_get_entity": return Strings.Get("tool.name.game_get_entity");
                case "game_get_entities": return Strings.Get("tool.name.game_get_entities");
                case "game_get_progress": return Strings.Get("tool.name.game_get_progress");
                case "game_capture_view": return Strings.Get("tool.name.game_capture_view");
                case "game_analyze_area": return Strings.Get("tool.name.game_analyze_area");
                case "game_render_layout": return Strings.Get("tool.name.game_render_layout");
                case "game_copy_blueprint": return Strings.Get("tool.name.game_copy_blueprint");
                case "game_generate_blueprint": return Strings.Get("tool.name.game_generate_blueprint");
                case "game_trace_line": return Strings.Get("tool.name.game_trace_line");
                case "game_get_planets": return Strings.Get("tool.name.game_get_planets");
                case "game_get_logistics": return Strings.Get("tool.name.game_get_logistics");
                case "space_region": return Strings.Get("tool.name.space_region");
                case "space_point": return Strings.Get("tool.name.space_point");
                case "space_motion": return Strings.Get("tool.name.space_motion");
                // sidecar 本地工具（tool_activity 卡片用）
                case "kb_search": return Strings.Get("tool.name.kb_search");
                case "kb_get": return Strings.Get("tool.name.kb_get");
                case "kb_recipe_tree": return Strings.Get("tool.name.kb_recipe_tree");
                case "kb_plan": return Strings.Get("tool.name.kb_plan");
                case "run_script": return Strings.Get("tool.name.run_script");
                case "game_export_prototypes": return Strings.Get("tool.name.game_export_prototypes");
                case "blueprint_generate": return Strings.Get("tool.name.blueprint_generate");
                case "blueprint_save": return Strings.Get("tool.name.blueprint_save");
                case "blueprint_search": return Strings.Get("tool.name.blueprint_search");
                case "blueprint_info": return Strings.Get("tool.name.blueprint_info");
                case "blueprint_fs": return Strings.Get("tool.name.blueprint_fs");
                case "wiki_search": return Strings.Get("tool.name.wiki_search");
                case "wiki_page": return Strings.Get("tool.name.wiki_page");
                case "ask_user": return Strings.Get("tool.name.ask_user");
                case "ui_ask_user": return Strings.Get("tool.name.ui_ask_user");
                case "game_place_marker": return Strings.Get("tool.name.game_place_marker");
                case "game_open_panel": return Strings.Get("tool.name.game_open_panel");
                case "game_set_research": return Strings.Get("tool.name.game_set_research");
                case "game_drop_item": return Strings.Get("tool.name.game_drop_item");
                case "game_set_recipe": return Strings.Get("tool.name.game_set_recipe");
                case "game_set_assembler_batch": return Strings.Get("tool.name.game_set_assembler_batch");
                case "game_storage_set_filter": return Strings.Get("tool.name.game_storage_set_filter");
                case "game_storage_set_limit": return Strings.Get("tool.name.game_storage_set_limit");
                case "game_storage_move": return Strings.Get("tool.name.game_storage_move");
                case "game_storage_deposit": return Strings.Get("tool.name.game_storage_deposit");
                case "game_storage_withdraw": return Strings.Get("tool.name.game_storage_withdraw");
                case "game_feed_input": return Strings.Get("tool.name.game_feed_input");
                case "game_tether_add": return Strings.Get("tool.name.game_tether_add");
                case "game_tether_clear": return Strings.Get("tool.name.game_tether_clear");
                case "game_tether_list": return Strings.Get("tool.name.game_tether_list");
                case "game_take_output": return Strings.Get("tool.name.game_take_output");
                case "game_undo_last_write": return Strings.Get("tool.name.game_undo_last_write");
                default: return name;
            }
        }
    }
}
