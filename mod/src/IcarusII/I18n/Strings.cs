using System.Collections.Generic;
using IcarusII.Game;

namespace IcarusII.I18n
{
    /// <summary>
    /// 界面文案。M0 只有中文，但所有文案都走查表，
    /// 后续加语言只需补一个字典（design.md §11）。
    /// </summary>
    internal static class Strings
    {
        private static readonly Dictionary<string, string> ZhCn = new Dictionary<string, string>
        {
            ["title"] = "工业机甲终端",
            ["status.connecting"] = "连接中…",
            ["status.handshaking"] = "握手中…",
            ["status.ready"] = "就绪",
            ["status.regenerating"] = "重新推理…",
            ["status.thinking"] = "思考中…",
            ["status.tools"] = "调用工具…",
            ["status.compacting"] = "整理上下文中…",
            ["status.undoing"] = "撤销压缩…",
            ["status.backoff"] = "重连中…",
            ["status.disconnected"] = "未连接",
            ["input.placeholder"] = "按 Enter 或点击此处输入…",
            // 工具列表（点击 tool-btn 或输入 / 时出现）
            ["tool.menu.title"] = "工具",
            ["tool.menu.hint"] = "输入 / 筛选 · 选择后本轮强制调用",
            ["tool.menu.footer"] = "↑↓ 选择 · Enter 插入 · Esc 关闭",
            ["tool.menu.empty"] = "没有匹配的工具",
            ["tool.menu.loading"] = "正在连接 sidecar…",
            ["tool.menu.required"] = "本轮强制调用：{0}",
            // 交互问答（choose）
            ["ask.next"] = "下一个",
            ["ask.submit"] = "提交",
            ["ask.waiting"] = "等待你的回答…",
            ["confirm.waiting"] = "等待你的确认…",
            ["key.placeholder"] = "粘贴 DeepSeek API Key 后回车…",
            ["key.prompt"] =
                "尚未配置 API Key。请把 DeepSeek 的 API Key 粘贴到下面并回车。\n" +
                "输入内容不会显示在屏幕上，也不会写入聊天记录；它只保存在本机 sidecar 的数据目录里。",
            ["key.saved"] = "API Key 已保存，可以开始提问了。",
            ["key.invalid"] = "API Key 被服务端拒绝，请检查后重新输入。",
            ["key.unreachable"] = "无法连通 DeepSeek，请检查网络后重试。",
            ["status.needkey"] = "待配置",
            ["save.bound"] = "已绑定存档「{0}」的对话（恢复 {1} 条历史）。",
            ["save.bound.nocount"] = "已绑定存档「{0}」的对话。",
            ["save.new"] = "已为存档「{0}」开启新对话。",
            ["save.unbound"] = "已离开存档，对话暂时解绑。",
            ["save.sandbox"] =
                "该存档已启用游戏沙盒工具（该存档的成就已关闭）。AI 的沙盒专属工具只在此时可用。",
            ["sandbox.required"] = "当前存档未启用游戏沙盒工具，该操作被拒绝。",
            ["welcome"] = "我是伊卡洛斯·贰。可以问我《戴森球计划》的机制、产线排布、或者卡住了该先修什么。",
            ["chat.index.empty"] = "暂无提问",
            ["chat.index.delete.tip"] = "删除",
            ["chat.index.undo.compaction.tip"] = "撤销压缩",
            ["chat.compaction.label"] = "自动压缩上下文",
            ["chat.compaction.empty"] = "（本次压缩没有生成摘要）",
            ["chat.compaction.undone"] = "已撤销压缩；如果上下文再次超限，会重新压缩。",
            ["chat.compaction.failed"] = "撤销压缩失败（{0}）。",
            ["hint.disconnected"] = "未连接到 sidecar。请检查 BepInEx 日志，或改用 LaunchMode=connectOnly 自行启动。",
            ["hint.conflict"] = "提示：{0}",
            ["settings.tip.title"] = "设置",
            ["settings.tip.text"] = "打开设置，切换模型 / 配置 API 密钥",
            ["system"] = "系统",
            ["you"] = "你",
            ["agent"] = "助手",

            // 思考块 / 工具块（输出区里的可折叠条目）
            ["thinking.title"] = "思考",
            ["thinking.title.streaming"] = "思考中…",
            ["thinking.level.off"] = "关闭",
            ["thinking.level.high"] = "高",
            ["thinking.level.max"] = "最高",
            // loading 分组头：思考/工具运行中 → 已完成
            ["loading.running"] = "终端运行中…",
            ["loading.done"] = "已完成",
            ["tool.title"] = "工具 · {0}",
            ["tool.name"] = "工具",
            ["tool.running"] = "读取中…",
            ["tool.ok"] = "完成",
            ["tool.failed"] = "失败",
            ["tool.args"] = "参数",
            ["tool.result"] = "结果",

            // 助手回复底部（时间 + 复制）
            ["reply.time"] = "游戏时间 {0}",
            ["reply.copy"] = "复制",
            ["reply.link.opening"] = "正在浏览器中打开链接…",
            ["reply.link.open_failed"] = "无法打开浏览器，链接已复制到剪贴板",

            // 蓝图卡片（协议附录 A.9）
            ["blueprint.card.name"] = "蓝图",
            ["blueprint.card.view"] = "查看",
            ["blueprint.card.paste"] = "使用",
            ["blueprint.card.meta.created"] = "创建 {0}",
            ["blueprint.card.no_description"] = "暂无介绍",
            ["blueprint.card.thumb"] = "蓝图",
            ["blueprint.card.unknown_author"] = "未知作者",
            ["blueprint.card.unknown_date"] = "未知日期",
            ["blueprint.card.unknown_version"] = "未标注",
            ["blueprint.card.source.generated"] = "生成",
            ["blueprint.card.source.downloaded"] = "下载",
            ["blueprint.card.source.copied"] = "复制",
            ["blueprint.card.source.written"] = "保存",
            ["blueprint.card.meta.rate"] = "产量 {0}/min",
            ["blueprint.card.meta.buildings"] = "{0} 台",
            ["blueprint.card.meta.size"] = "{0}×{1}",
            ["blueprint.card.meta.author"] = "作者 {0}",
            ["blueprint.card.meta.version"] = "版本 {0}",
            ["blueprint.card.meta.license"] = "许可 {0}",
            ["blueprint.card.no_file"] = "找不到蓝图文件：{0}",
            ["link.card.locate"] = "定位",
            ["link.card.located"] = "已定位",
            ["blueprint.card.bad_file"] = "蓝图文件无法读取或已损坏。",
            ["blueprint.card.open_failed"] = "无法打开蓝图浏览器（当前界面不允许或蓝图界面未就绪）。",
            ["blueprint.card.paste_unavailable"] = "尚未解锁蓝图粘贴，或当前状态不允许粘贴。",
            ["blueprint.card.paste_failed"] = "粘贴模式未启动（可能未解锁蓝图、或当前星球不能建造）。",
            ["blueprint.card.not_in_game"] = "不在游戏内，无法操作蓝图。",
            ["model.label"] = "模型",
            ["model.none"] = "未配置",
            ["model.switched"] = "模型：{0}",
        };

        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["zh-CN"] = ZhCn,
            };

        public static string Get(string key, params object[] args)
        {
            var table = ResolveTable();
            if (!table.TryGetValue(key, out var value))
            {
                // 回退到中文，再回退到 key 本身（便于暴露缺失项）
                if (!ZhCn.TryGetValue(key, out value)) value = key;
            }
            return args != null && args.Length > 0 ? string.Format(value, args) : value;
        }

        private static Dictionary<string, string> ResolveTable()
        {
            var code = GameRefs.LanguageCode;
            if (!string.IsNullOrEmpty(code) && Tables.TryGetValue(code, out var table))
                return table;
            return ZhCn;
        }
    }
}
