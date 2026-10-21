using System;

namespace IcarusII.UI
{
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
                default: return name;
            }
        }
    }
}
