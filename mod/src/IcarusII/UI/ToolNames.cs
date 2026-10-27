using System;

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
        /// <summary>M4m：sidecar 的调试只读白名单标记；老目录缺省为 true。</summary>
        public bool Debuggable = true;

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (Name != null && Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (Label != null && Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (Description != null && Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
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
            if (string.IsNullOrEmpty(name)) return "工具";
            switch (name)
            {
                case "game_get_overview": return "存档概览";
                case "game_get_player": return "玩家状态";
                case "game_get_planet": return "行星状态";
                case "game_get_power": return "电力状态";
                case "game_get_research": return "研究状态";
                case "game_get_production": return "实际产出";
                case "game_get_entity": return "建筑详情";
                case "game_get_entities": return "建筑列表";
                case "game_get_progress": return "进度阶段";
                case "game_capture_view": return "截图";
                case "game_analyze_area": return "产线分析";
                case "game_render_layout": return "布局图";
                case "game_copy_blueprint": return "存为蓝图";
                case "game_generate_blueprint": return "生成蓝图";
                case "game_trace_line": return "产线拓扑";
                case "game_get_planets": return "星球一览";
                case "game_get_logistics": return "星际物流";
                // sidecar 本地工具（tool_activity 卡片用）
                case "kb_search": return "知识库检索";
                case "kb_get": return "知识库查询";
                case "kb_recipe_tree": return "配方树";
                case "kb_plan": return "量化计算";
                case "run_script": return "脚本";
                case "game_export_prototypes": return "建立知识库";
                case "blueprint_generate": return "生成蓝图";
                case "blueprint_save": return "保存蓝图";
                case "blueprint_search": return "搜索蓝图";
                case "blueprint_info": return "蓝图详情";
                case "blueprint_fs": return "蓝图文件";
                case "wiki_search": return "百科检索";
                case "wiki_page": return "百科页面";
                case "ask_user":
                case "ui_ask_user": return "向玩家提问";
                case "game_place_marker": return "新建标记";
                case "game_open_panel": return "打开面板";
                case "game_set_research": return "切换研究";
                case "game_drop_item": return "丢弃物品";
                case "game_set_recipe": return "更换配方";
                case "game_set_assembler_batch": return "批量配方";
                case "game_storage_set_filter": return "存储过滤";
                case "game_storage_set_limit": return "存储上限";
                case "game_storage_move": return "移库";
                case "game_storage_deposit": return "存入存储";
                case "game_storage_withdraw": return "从存储取出";
                case "game_feed_input": return "投料";
                case "game_tether_add": return "方位指示";
                case "game_tether_clear": return "清除方位";
                case "game_tether_list": return "方位列表";
                case "game_take_output": return "取成品";
                case "game_undo_last_write": return "撤销写操作";
                default: return name;
            }
        }
    }
}
