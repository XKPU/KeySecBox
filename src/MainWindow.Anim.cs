using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class MainWindow
{
    // 分类切换过渡：令牌 + 待切换标记，快速连续切换时取消上一段，避免动画失效/叠加
    private bool _dataReady;
    private int _scopeSeq;
    private bool _scopeSwapPending;  // 退场播完后待执行的数据切换 + 入场
    private int _scopeSwapSeq;
    private EntrySnap? _scopeSnap;     // 切换前的展示快照
    private List<EntryItem> _scopeTarget = new(); // 切换后的目标实例列表
    // 排序移动动画完成回调
    private Action? _moveAnimCompleted;

    // 分类切换前的条目快照（索引 + 相对列表顶部的像素偏移）
    private sealed class EntrySnap
    {
        public Dictionary<long, int> Idx = new();
        public Dictionary<long, double> Tops = new();
    }

    // 容器动画：定时器逐帧更新容器的 平移 / 透明度 / 行内文本透明度。
    // 切换分类：离场条目向右淡出、新条目从左侧淡入、停留条目位置不变则不动、位置变了则滑过去；
    // 排序移动 = UIElement.Translation 平移。
    private sealed class ContainerAnim
    {
        public FrameworkElement Fe = null!;
        public long DurationMs;
        public bool EaseIn;
        public bool Move;                  // 平移：排序/停留条目垂直滑动、出入场水平滑动
        public double FromX, ToX;          // 水平平移（新条目左侧淡入、离场条目向右淡出）
        public double FromY, ToY;          // 垂直平移（排序滑动、停留条目位置滑动）
        public bool Fade;                  // 整框透明度
        public float FromOpacity, ToOpacity;
    }
    private readonly List<ContainerAnim> _containerAnims = new();
    private DispatcherQueueTimer? _containerAnimTimer;
    private long _containerAnimStart;

    // 解锁后主界面入场：分类/条目列表淡入上滑
    private void PlayUnlockIntro()
    {
        long ms = _appConfig.AlignMsToFrames(_appConfig.UnlockIntroAnimMs);
        var anims = new List<ContainerAnim>
        {
            new ContainerAnim
            {
                Fe = CategoryList, Move = true, FromY = 16, ToY = 0,
                Fade = true, FromOpacity = 0f, ToOpacity = 1f, DurationMs = ms
            },
            new ContainerAnim
            {
                Fe = EntryList, Move = true, FromY = 16, ToY = 0,
                Fade = true, FromOpacity = 0f, ToOpacity = 1f, DurationMs = ms
            }
        };
        StartContainerAnimations(anims);
    }

    // 动画切换
    private void RefreshEntriesAnimated()
    {
        CancelScopeTransition(); // 快速连续切换：先停掉上一段动画
        int seq = _scopeSeq;
        var snap = CaptureEntrySnap();
        var target = BuildEntryList();   // 仅构造目标实例列表，暂不改动展示集合
        _scopeSnap = snap;
        _scopeTarget = target;

        var targetIds = new HashSet<long>(target.Select(x => x.Id));
        var goneIds = snap.Idx.Keys.Where(id => !targetIds.Contains(id)).ToList();

        if (goneIds.Count == 0)
        {
            FinishScopeSwap(seq);
            return;
        }

        // 退场：离场条目向右淡出（滑出自身宽度）；停留条目完全不动。
        long exitMs = _appConfig.AlignMsToFrames(_appConfig.ScopeExitAnimMs);
        var exitAnims = new List<ContainerAnim>();
        for (int i = 0; i < EntryList.Items.Count; i++)
        {
            if (EntryList.Items[i] is not EntryItem ent) continue;
            if (EntryList.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            if (!goneIds.Contains(ent.Id)) continue; // 停留条目不动
            double off = Math.Max(fe.ActualWidth, 60);
            exitAnims.Add(new ContainerAnim
            {
                Fe = fe,
                Move = true, FromX = 0, ToX = off,
                Fade = true, FromOpacity = 1f, ToOpacity = 0f,
                DurationMs = exitMs, EaseIn = true
            });
        }
        if (exitAnims.Count == 0)
        {
            FinishScopeSwap(seq);
            return;
        }
        StartContainerAnimations(exitAnims);

        // 退场播完后接着换数据 + 入场动画：由容器动画完成回调驱动，不另设计时器
        _scopeSwapPending = true;
        _scopeSwapSeq = seq;
    }

    // 退场已播完（容器动画完成回调）：就地换数据，再播入场。
    private void FinishScopeSwap(int seq)
    {
        if (seq != _scopeSeq) return;
        RefreshEntriesNow(_scopeTarget); // 复用动画开始前已构造的目标列表，不再二次重建
        BeginScopeEnter(seq);
    }

    private void BeginScopeEnter(int seq)
    {
        if (seq != _scopeSeq) return;

        var snap = _scopeSnap ?? new EntrySnap();
        long durMs = _appConfig.AlignMsToFrames(_appConfig.ScopeEnterAnimMs); // 对齐整数帧
        StopContainerAnimations();
        // 强制一次布局
        if (EntryList.Items.Count > 0) EntryList.UpdateLayout();
        ResetListVisuals(EntryList);
        var anims = new List<ContainerAnim>();
        for (int i = 0; i < EntryList.Items.Count; i++)
        {
            if (EntryList.Items[i] is not EntryItem ent) continue;
            if (EntryList.ContainerFromIndex(i) is not FrameworkElement fe) continue;

            if (snap.Idx.TryGetValue(ent.Id, out _))
            {
                // 停留条目：位置不变则完全不动；位置变了则从旧位置滑到新位置
                if (!snap.Tops.TryGetValue(ent.Id, out double oldTop) || double.IsNaN(oldTop)) continue;
                double delta = oldTop - TopInList(fe, EntryList);
                if (Math.Abs(delta) < 0.5) continue; // 位置不变，不动
                anims.Add(new ContainerAnim
                {
                    Fe = fe,
                    Move = true, FromY = delta, ToY = 0,
                    DurationMs = durMs,
                    EaseIn = false
                });
            }
            else
            {
                // 新分类独有条目：从左侧淡入到正常位置
                double off = Math.Max(fe.ActualWidth, 60);
                anims.Add(new ContainerAnim
                {
                    Fe = fe,
                    Move = true, FromX = -off, ToX = 0,
                    Fade = true, FromOpacity = 0f, ToOpacity = 1f,
                    DurationMs = durMs,
                    EaseIn = false
                });
            }
        }
        StartContainerAnimations(anims);
    }

    // 新增条目后刷新：旧条目原地不动，新条目从左侧淡入（复用容器动画，不依赖分类切换）
    private void RefreshEntriesWithIntro()
    {
        var snap = CaptureEntrySnap(); // 刷新前旧条目快照（当前展示集合）
        RefreshEntriesNow();
        if (EntryList.Items.Count == 0) return;
        EntryList.UpdateLayout(); // 保证新条目容器已实现
        long durMs = _appConfig.AlignMsToFrames(_appConfig.ScopeEnterAnimMs);
        var anims = new List<ContainerAnim>();
        for (int i = 0; i < EntryList.Items.Count; i++)
        {
            if (EntryList.Items[i] is not EntryItem ent) continue;
            if (snap.Idx.ContainsKey(ent.Id)) continue; // 旧条目不动
            if (EntryList.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            double off = Math.Max(fe.ActualWidth, 60);
            anims.Add(new ContainerAnim
            {
                Fe = fe,
                Move = true, FromX = -off, ToX = 0,
                Fade = true, FromOpacity = 0f, ToOpacity = 1f,
                DurationMs = durMs,
                EaseIn = false
            });
        }
        StartContainerAnimations(anims);
    }

    // 记录每个可见容器相对列表顶部的偏移
    private Dictionary<long, double> CaptureTops(ListViewBase list, Func<object, long> idOf)
    {
        var map = new Dictionary<long, double>();
        for (int i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (item != null && list.ContainerFromIndex(i) is FrameworkElement fe)
                map[idOf(item)] = TopInList(fe, list);
        }
        return map;
    }

    private Dictionary<long, double> CaptureEntryTops()
        => CaptureTops(EntryList, x => ((EntryItem)x).Id);
    private Dictionary<long, double> CaptureCategoryTops()
        => CaptureTops(CategoryList, x => ((CategoryItem)x).Id);

    private static double TopInList(FrameworkElement element, ListViewBase list)
        => element.TransformToVisual((UIElement)list).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

    // 切分类前的完整快照：全部条目 Id↔索引 + 可见容器的顶部偏移（容器未实现时为 NaN）
    private EntrySnap CaptureEntrySnap()
    {
        var snap = new EntrySnap();
        for (int i = 0; i < EntryList.Items.Count; i++)
        {
            if (EntryList.Items[i] is not EntryItem ent) continue;
            snap.Idx[ent.Id] = i;
            double top = double.NaN;
            if (EntryList.ContainerFromIndex(i) is FrameworkElement fe)
                top = TopInList(fe, EntryList);
            snap.Tops[ent.Id] = top;
        }
        return snap;
    }

    // 容器动画：定时器逐帧直接赋值 Opacity / Translation
    private static void ResetContainerVisual(FrameworkElement fe)
    {
        fe.Opacity = 1f;
        fe.Translation = Vector3.Zero;
    }

    private static void ResetListVisuals(ListViewBase list)
    {
        for (int i = 0; i < list.Items.Count; i++)
            if (list.ContainerFromIndex(i) is FrameworkElement fe) ResetContainerVisual(fe);
    }

    private void StopContainerAnimations()
    {
        if (_containerAnimTimer != null)
        {
            _containerAnimTimer.Stop();
            _containerAnimTimer.Tick -= OnContainerAnimTick;
        }
        _containerAnims.Clear();
    }

    private void StartContainerAnimations(List<ContainerAnim> anims)
    {
        StopContainerAnimations();
        _containerAnims.AddRange(anims);
        if (_containerAnims.Count == 0) return;

        _containerAnimStart = Environment.TickCount64;
        foreach (var a in _containerAnims)
        {
            if (a.Move) a.Fe.Translation = new Vector3((float)a.FromX, (float)a.FromY, 0f);
            if (a.Fade) a.Fe.Opacity = a.FromOpacity;
        }

        _containerAnimTimer ??= DispatcherQueue.CreateTimer();
        _containerAnimTimer.Tick -= OnContainerAnimTick;
        _containerAnimTimer.Tick += OnContainerAnimTick;
        int fps = Math.Max(1, _appConfig.FrameRate);
        _containerAnimTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        _containerAnimTimer.IsRepeating = true;
        _containerAnimTimer.Start();
    }

    private void OnContainerAnimTick(DispatcherQueueTimer sender, object args)
    {
        if (_containerAnims.Count == 0)
        {
            sender.Stop();
            return;
        }
        long now = Environment.TickCount64;
        long elapsed = now - _containerAnimStart;
        bool allDone = true;
        foreach (var a in _containerAnims)
        {
            double p = a.DurationMs <= 0
                ? 1
                : Math.Clamp((double)elapsed / a.DurationMs, 0, 1);
            if (p < 1) allDone = false;
            // 三次缓动用乘法展开
            double q = 1 - p;
            double t = a.EaseIn ? p * p * p : 1 - q * q * q;
            if (a.Move)
                a.Fe.Translation = new Vector3(
                    (float)(a.FromX + (a.ToX - a.FromX) * t),
                    (float)(a.FromY + (a.ToY - a.FromY) * t), 0f);
            if (a.Fade)
                a.Fe.Opacity = (float)(a.FromOpacity + (a.ToOpacity - a.FromOpacity) * t);
        }
        if (allDone)
        {
            sender.Stop();
            _containerAnims.Clear();
            if (_scopeSwapPending)
            {
                _scopeSwapPending = false;
                FinishScopeSwap(_scopeSwapSeq);
            }
            else if (_moveAnimCompleted != null)
            {
                var cb = _moveAnimCompleted;
                _moveAnimCompleted = null;
                cb.Invoke();
            }
        }
    }

    // 取消进行中的切换动画
    private void CancelScopeTransition()
    {
        _scopeSeq++;
        _scopeSwapPending = false;
        StopContainerAnimations();
        ResetListVisuals(EntryList);
        if (_moveAnimCompleted != null)
        {
            var cb = _moveAnimCompleted;
            _moveAnimCompleted = null;
            cb.Invoke();
        }
    }

    // 排序移动动画
    private void AnimateMove(ListViewBase list, Dictionary<long, double> oldTops,
        Func<object, long> idOf, Action? completed = null)
    {
        // 目标时长固定，启动时对齐到当前帧率的整数帧。
        long ms = _appConfig.AlignMsToFrames(_appConfig.SortMoveAnimMs);
        StopContainerAnimations();
        ResetListVisuals(list);

        var anims = new List<ContainerAnim>();
        for (int i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (item == null) continue;
            if (!oldTops.TryGetValue(idOf(item), out double oldTop) || double.IsNaN(oldTop)) continue;
            if (list.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            double delta = oldTop - TopInList(fe, list);
            if (Math.Abs(delta) < 0.5) continue;
            anims.Add(new ContainerAnim
            {
                Fe = fe,
                Move = true,
                FromY = delta,
                ToY = 0,
                DurationMs = ms,
                EaseIn = false
            });
        }
        _moveAnimCompleted = completed;
        if (anims.Count > 0)
            StartContainerAnimations(anims);
        else
        {
            // 无需动画，直接回调
            _moveAnimCompleted = null;
            completed?.Invoke();
        }
    }

    private void AnimateEntryMove(Dictionary<long, double> oldTops, Action? completed = null)
    {
        EntryList.UpdateLayout(); // 强制布局就绪，使采样到的新位置准确
        AnimateMove(EntryList, oldTops, x => ((EntryItem)x).Id, completed);
    }

    private void AnimateCategoryMove(Dictionary<long, double> oldTops)
    {
        CategoryList.UpdateLayout(); // 强制布局就绪，使采样到的新位置准确
        AnimateMove(CategoryList, oldTops, x => ((CategoryItem)x).Id);
    }
}
