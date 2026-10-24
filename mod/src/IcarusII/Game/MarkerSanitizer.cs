using System;
using BepInEx.Logging;

namespace IcarusII.Game
{
    /// <summary>
    /// 兼容旧存档里被写坏的标记：MarkerUIRenderer.PopulateMinimal 只有 2 个
    /// quad 缓冲，word 超过 2 个字符会在游戏主循环里抛 IndexOutOfRangeException。
    /// 进入存档后把已有超长 word 截断，并把原文保留到 name。
    /// </summary>
    internal static class MarkerSanitizer
    {
        public static int FixAll(ManualLogSource log)
        {
            try
            {
                var digital = GameMain.data?.galacticDigital;
                if (digital?.markerPool == null) return -1;
                var limit = Math.Min(digital.markerCursor, digital.markerPool.Length);
                var fixedCount = 0;
                for (int i = 1; i < limit; i++)
                {
                    var marker = digital.markerPool[i];
                    if (marker == null || string.IsNullOrEmpty(marker.word) || marker.word.Length <= 2)
                        continue;
                    if (string.IsNullOrEmpty(marker.name)) marker.name = marker.word;
                    marker.word = marker.word.Substring(0, 2);
                    fixedCount++;
                }
                if (fixedCount > 0)
                    log?.LogWarning($"[marker] 已修正 {fixedCount} 个超长地图简述，避免 MarkerUIRenderer 崩溃");
                return fixedCount;
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[marker] 修正旧标记失败：{ex.Message}");
                return 0;
            }
        }
    }
}
