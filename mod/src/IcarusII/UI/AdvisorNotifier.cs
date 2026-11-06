using System;
using System.Collections.Generic;
using System.Text;
using IcarusII.Game;
using IcarusII.I18n;
using IcarusII.Input;
using UnityEngine;

namespace IcarusII.UI
{
    /// <summary>
    /// 聊天窗关着的时候，用游戏原生顾问字幕把后台事件喊出来。
    ///
    /// 三条游戏约束决定了实现方式：
    /// 1. `UIAdvisorTip.RunAdvisorTip` 会按玩家的「顾问提示」开关拦掉 id>=4 的字幕，
    ///    而这三个提示必须无视该开关，所以只在面板真正取走文本的那一小段时间里临时打开开关；
    /// 2. 提示 ID 的 `MinLength` 必须是负数，否则 `UIAdvisorTip` 会把 2000000+ID 写进存档，
    ///    下一次同类提示就会被 `RequestAdvisorTip` 的历史检查静默吞掉；
    /// 3. 面板同一时刻只能显示一条，所以这里自己排队，等面板空出来再上。
    /// </summary>
    internal sealed class AdvisorNotifier
    {
        public const int ReplyTipId = 991001;
        public const int ConfirmTipId = 991002;
        public const int AskTipId = 991003;

        /// <summary>提示在面板上最多停留的秒数（写进 AdvisorTipProto.MaxLength）。</summary>
        private const float NoticeSeconds = 15f;

        /// <summary>交给面板后最多等多久算失败，避免顾问提示开关被一直顶成 true。</summary>
        private const float ShowTimeoutSeconds = 2f;

        private readonly HotkeyService _hotkey;
        private readonly List<Notice> _pending = new List<Notice>(3);

        private int _askedId;
        private float _askedAt;
        private int _shownId;
        private float _retryAt;
        private bool _levelSaved;
        private bool _savedLevel;

        private sealed class Notice
        {
            public int Id;
            public string Text;
        }

        public AdvisorNotifier(HotkeyService hotkey)
        {
            _hotkey = hotkey;
        }

        public void NotifyReply(string question, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            var topic = string.IsNullOrWhiteSpace(question)
                ? Strings.Get("advisor.notice.unknown_question")
                : question;
            Enqueue(ReplyTipId, AdvisorText.ComposeReply(
                Strings.Get("advisor.notice.reply"), topic, body, HotkeyMarkup()));
        }

        public void NotifyConfirm(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
            Enqueue(ConfirmTipId, AdvisorText.ComposeConfirm(
                Strings.Get("advisor.notice.confirm"), title, body, HotkeyMarkup()));
        }

        public void NotifyAsk(List<AskQuestion> questions)
        {
            if (questions == null || questions.Count == 0) return;
            var builder = new StringBuilder();
            foreach (var question in questions)
            {
                if (question == null || string.IsNullOrWhiteSpace(question.Prompt)) continue;
                if (builder.Length > 0) builder.Append('；');
                builder.Append(question.Prompt);
            }
            if (builder.Length == 0) return;
            Enqueue(AskTipId, AdvisorText.ComposeAsk(
                Strings.Get("advisor.notice.ask"), builder.ToString(), HotkeyMarkup()));
        }

        /// <summary>玩家开了聊天窗（或已经作答/拒绝）：把这一类提示撤掉。</summary>
        public void Dismiss(int id)
        {
            _pending.RemoveAll(n => n.Id == id);
            if (_shownId == id || _askedId == id)
            {
                // _askedId 这一支是「面板正在淡入、还没取走文本」，也要一起收掉，
                // 否则它会继续淡入并留下一条空提示（文本已经被门槛跳过了）。
                var tip = CurrentTip();
                if (tip != null && tip.active) tip.FadeOutAndStop();
                _shownId = 0;
            }
            if (_askedId == id)
            {
                _askedId = 0;
                RestoreLevel();
            }
        }

        /// <summary>开窗/换档/离开存档：清空所有提示。</summary>
        public void DismissAll()
        {
            _pending.Clear();
            var tip = CurrentTip();
            if (tip != null && tip.active && (_shownId != 0 || _askedId != 0)) tip.FadeOutAndStop();
            _shownId = 0;
            _askedId = 0;
            RestoreLevel();
        }

        public void Tick()
        {
            var tip = CurrentTip();
            if (!GameRefs.IsInGame || tip == null)
            {
                _pending.Clear();
                _shownId = 0;
                _askedId = 0;
                RestoreLevel();
                return;
            }

            if (_askedId != 0)
            {
                var playing = tip.playingTip;
                if (playing != null && playing.ID == _askedId)
                {
                    // 面板已经取走文本：门槛已经消费掉，马上把玩家的设置还原。
                    _shownId = _askedId;
                    _askedId = 0;
                    RestoreLevel();
                }
                else if (Time.realtimeSinceStartup - _askedAt > ShowTimeoutSeconds)
                {
                    _askedId = 0;
                    RestoreLevel();
                }
                return;
            }

            if (_shownId != 0)
            {
                if (tip.active) return;
                _shownId = 0;
            }

            // 游戏的顾问字幕正在播：等它收掉，别把玩家的剧情提示顶掉。
            if (_pending.Count == 0 || tip.active) return;
            if (Time.realtimeSinceStartup < _retryAt) return;

            var notice = _pending[0];
            _pending.RemoveAt(0);
            if (!Show(tip, notice))
            {
                // 原型注入/播放失败（LDB 未就绪、面板被占用）：放回队首稍后重试，
                // 不能把通知静默丢掉——玩家会以为没有待确认的事。
                _pending.Insert(0, notice);
                _retryAt = Time.realtimeSinceStartup + 1f;
            }
        }

        private bool Show(UIAdvisorTip tip, Notice notice)
        {
            var proto = EnsureProto(notice.Id);
            if (proto == null) return false;

            proto.Script = notice.Text;
            proto.Preload();

            _savedLevel = UIAdvisorTip.showAdvisorTips;
            _levelSaved = true;
            UIAdvisorTip.showAdvisorTips = true;
            _askedId = notice.Id;
            _askedAt = Time.realtimeSinceStartup;

            try
            {
                tip.RunAdvisorTip(notice.Id);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[顾问] 提示显示失败：{ex.Message}");
                _askedId = 0;
                RestoreLevel();
                return false;
            }

            if (!tip.active)
            {
                _askedId = 0;
                RestoreLevel();
                return false;
            }
            return true;
        }

        private string HotkeyMarkup()
        {
            return AdvisorText.HotkeyMarkup(_hotkey != null ? _hotkey.Key.Format() : null);
        }

        private void Enqueue(int id, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _pending.RemoveAll(n => n.Id == id);
            _pending.Add(new Notice { Id = id, Text = text });
            while (_pending.Count > 3) _pending.RemoveAt(0);
        }

        private void RestoreLevel()
        {
            if (!_levelSaved) return;
            UIAdvisorTip.showAdvisorTips = _savedLevel;
            _levelSaved = false;
        }

        private static UIAdvisorTip CurrentTip()
        {
            try
            {
                var root = GameRefs.UiRoot;
                var game = root != null ? root.uiGame : null;
                return game != null ? game.advisorTip : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 往 LDB 里补一个提示原型。面板是按 ID 从 `LDB.advisorTips.Select` 取原型的，
        /// 所以只改 dataArray 不够，还要重建 ID→index 索引（OnAfterDeserialize 是公开入口）。
        ///
        /// 每次都重新扫一遍而不是缓存：切语言/重载存档后 LDB 可能被整体换掉，
        /// 缓存下来的对象会不在新表里，Select 直接返回 null、提示静默消失。
        /// </summary>
        private static AdvisorTipProto EnsureProto(int id)
        {
            try
            {
                var set = LDB.advisorTips;
                if (set is null || set.dataArray is null) return null;

                foreach (var existing in set.dataArray)
                {
                    if (existing is null || existing.ID != id) continue;
                    return existing;
                }

                var proto = new AdvisorTipProto
                {
                    Name = $"IcarusII-notice-{id}",
                    SID = $"IcarusII-notice-{id}",
                    ID = id,
                    Priority = 0,
                    // 负数让 UIAdvisorTip 跳过 2000000+ID 的历史写入，否则同类提示只出现一次。
                    MinLength = -1f,
                    MaxLength = NoticeSeconds,
                    DeterminatorName = string.Empty,
                    DeterminatorParams = Array.Empty<long>(),
                };

                var grown = new AdvisorTipProto[set.dataArray.Length + 1];
                Array.Copy(set.dataArray, grown, set.dataArray.Length);
                grown[set.dataArray.Length] = proto;
                set.dataArray = grown;
                set.OnAfterDeserialize();

                Plugin.Log.LogInfo($"[顾问] 已注入提示原型 {id}。");
                return proto;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[顾问] 注入提示原型失败：{ex.Message}");
                return null;
            }
        }
    }
}
