using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace IcarusII.UI
{
    /// <summary>
    /// 列表容器的过渡动画：挂在带 VerticalLayoutGroup + ContentSizeFitter 的滚动内容节点上。
    /// 行高过渡与子项换位共用一条 ease-out 曲线，时长可在 prefab 里调。
    /// 同一个 key 的动画互相替换，中途切换会先落到终态，避免留下半开半合的列表。
    /// </summary>
    public sealed class ListMotion : MonoBehaviour
    {
        /// <summary>一条要过渡高度、带 LayoutElement 的行。</summary>
        public readonly struct Row
        {
            public Row(LayoutElement element, float height)
            {
                Element = element;
                Height = height;
            }

            public LayoutElement Element { get; }
            public float Height { get; }
        }

        /// <summary>一个要从起始 Y 滑到布局当前位置的子项。</summary>
        public readonly struct Move
        {
            public Move(RectTransform rect, float fromY)
            {
                Rect = rect;
                FromY = fromY;
            }

            public RectTransform Rect { get; }
            public float FromY { get; }
        }

        /// <summary>行高从 0 长到本身高度（或反向压扁）的时长（秒）。</summary>
        public float RowDuration = 0.16f;
        /// <summary>子项滑动到新位置的时长（秒）。</summary>
        public float ReorderDuration = 0.2f;
        /// <summary>位移小于这个距离就不做换位动画（像素）。</summary>
        private const float ReorderThreshold = 0.5f;

        private sealed class Tween
        {
            public string Key;
            public float Duration;
            public float Elapsed;
            public Action<float> Step;
            public Action Done;
        }

        private readonly List<Tween> _tweens = new List<Tween>();
        private VerticalLayoutGroup _layout;
        private ContentSizeFitter _fitter;

        private void Awake()
        {
            _layout = GetComponent<VerticalLayoutGroup>();
            _fitter = GetComponent<ContentSizeFitter>();
        }

        /// <summary>
        /// 逐行过渡高度：reveal 为 true 从 0 长到 Height，false 从 Height 压到 0。
        /// 行的遮罩会裁掉文字，容器高度由布局自己跟着算。
        /// </summary>
        public void AnimateRows(string key, IReadOnlyList<Row> rows, bool reveal, Action done = null)
        {
            if (rows == null || rows.Count == 0)
            {
                done?.Invoke();
                return;
            }

            Finish(key);
            foreach (var row in rows)
            {
                if (row.Element == null) continue;
                row.Element.minHeight = 0f;
                row.Element.preferredHeight = reveal ? 0f : row.Height;
            }
            Rebuild();
            StartTween(key, RowDuration,
                progress =>
                {
                    foreach (var row in rows)
                    {
                        if (row.Element == null) continue;
                        row.Element.preferredHeight = reveal
                            ? Mathf.LerpUnclamped(0f, row.Height, progress)
                            : Mathf.LerpUnclamped(row.Height, 0f, progress);
                    }
                    Rebuild();
                },
                () =>
                {
                    foreach (var row in rows)
                    {
                        if (row.Element == null) continue;
                        row.Element.minHeight = row.Height;
                        row.Element.preferredHeight = row.Height;
                    }
                    Rebuild();
                    done?.Invoke();
                });
        }

        /// <summary>
        /// 子项换位：把每个候选从其 From 滑到布局算出的当前位置。
        /// 动画期间冻结容器高度并临时接管布局，结束后交还并强制重排。
        /// </summary>
        public void AnimateReorder(string key, IReadOnlyList<Move> candidates, Action done = null)
        {
            if (candidates == null) return;
            FinishAll();
            var content = (RectTransform)transform;
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            var moves = new List<(RectTransform Rect, float From, float To)>();
            foreach (var candidate in candidates)
            {
                if (candidate.Rect == null) continue;
                var to = candidate.Rect.anchoredPosition.y;
                if (Mathf.Abs(candidate.FromY - to) < ReorderThreshold) continue;
                moves.Add((candidate.Rect, candidate.FromY, to));
            }
            if (moves.Count == 0) return;

            // 先冻结滚动内容的当前高度：关掉 layout 后 ContentSizeFitter 会把高度算成 0，
            // 滚动区会瞬间塌陷。高度冻结、位置由这里手动驱动，结束后再交还。
            var frozenHeight = content.rect.height;
            var frozenWidth = content.sizeDelta.x;
            if (_fitter != null) _fitter.enabled = false;
            if (_layout != null) _layout.enabled = false;
            content.sizeDelta = new Vector2(frozenWidth, frozenHeight);
            foreach (var move in moves)
                move.Rect.anchoredPosition = new Vector2(move.Rect.anchoredPosition.x, move.From);

            StartTween(key, ReorderDuration,
                progress =>
                {
                    foreach (var move in moves)
                    {
                        if (move.Rect == null) continue;
                        move.Rect.anchoredPosition = new Vector2(move.Rect.anchoredPosition.x,
                            Mathf.LerpUnclamped(move.From, move.To, progress));
                    }
                },
                () =>
                {
                    if (_layout != null) _layout.enabled = true;
                    if (_fitter != null) _fitter.enabled = true;
                    LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                    done?.Invoke();
                });
        }

        /// <summary>把某个 key 的动画直接推到终态（含回调），保证布局已经复位。</summary>
        public void Finish(string key)
        {
            for (int i = _tweens.Count - 1; i >= 0; i--)
            {
                if (_tweens[i].Key != key) continue;
                var tween = _tweens[i];
                _tweens.RemoveAt(i);
                tween.Step?.Invoke(1f);
                tween.Done?.Invoke();
            }
        }

        /// <summary>关窗、清空列表、重建列表前调用：所有动画落到终态，避免残影和失控的布局开关。</summary>
        public void FinishAll()
        {
            if (_tweens.Count == 0) return;
            var pending = _tweens.ToArray();
            _tweens.Clear();
            foreach (var tween in pending)
            {
                tween.Step?.Invoke(1f);
                tween.Done?.Invoke();
            }
        }

        private void Update()
        {
            if (_tweens.Count == 0) return;
            var deltaTime = Time.unscaledDeltaTime;
            for (int i = _tweens.Count - 1; i >= 0; i--)
            {
                if (i >= _tweens.Count) continue;
                var tween = _tweens[i];
                tween.Elapsed += deltaTime;
                var progress = Mathf.Clamp01(tween.Elapsed / tween.Duration);
                tween.Step?.Invoke(EaseOutCubic(progress));
                if (progress < 1f) continue;
                _tweens.RemoveAt(i);
                tween.Done?.Invoke();
            }
        }

        private void StartTween(string key, float duration, Action<float> step, Action done)
        {
            Finish(key);
            _tweens.Add(new Tween
            {
                Key = key,
                Duration = Mathf.Max(0.01f, duration),
                Step = step,
                Done = done,
            });
        }

        /// <summary>ease-out cubic：起步快、收尾稳，和游戏自己的面板动画观感一致。</summary>
        private static float EaseOutCubic(float progress)
        {
            return 1f - Mathf.Pow(1f - progress, 3f);
        }

        private void Rebuild()
        {
            LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
        }
    }
}
