using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 运行中的缓慢呼吸（样稿 @keyframes pr-pulse：1.8 s，50 % 时 brightness(.96)）：所有在运行、可见、未冻结的进度条共用一个计时器（R10），
/// 没有订阅者时计时器停下。冻结（.frozen / Frozen.IsFrozen，截图）时不订阅，画面是静止的第 0 帧。
/// 变暗用 t-1 的透明度变体叠在填充上（令牌色 + alpha，色板合法）：最深时 α = 0.05 ≈ brightness(.96)。
/// 开销（R10，M1 复核）：透明度量化成 <see cref="Levels"/> 级、画刷预先建好（渲染时不分配）；10 帧/s 足够这么轻的变化；
/// 只重画实际可见的订阅者（父元素被隐藏的进度条不重画）；透明度没变的帧不重画。
/// </summary>
internal static class PulseClock
{
    public const double PeriodSec = 1.8, MaxAlpha = 0.05;
    public const int Levels = 16, FrameMs = 100;

    private static readonly HashSet<Visual> Subscribers = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly IBrush[] OverlayBrushes = BuildBrushes();
    private static DispatcherTimer? _timer;
    private static int _lastLevel = -1;

    private static IBrush[] BuildBrushes()
    {
        var a = new IBrush[Levels + 1];
        for (int i = 0; i <= Levels; i++) a[i] = new ImmutableSolidColorBrush(Palette.WithAlpha(Palette.T1Color, MaxAlpha * i / Levels));
        return a;
    }

    /// <summary>当前的叠加级别（0 → Levels → 0，ease-in-out）。</summary>
    private static int Level
    {
        get
        {
            var x = (1 - Math.Cos(2 * Math.PI * (Clock.Elapsed.TotalSeconds % PeriodSec) / PeriodSec)) / 2;
            return (int)Math.Round(x * Levels);
        }
    }

    /// <summary>当前的叠加画刷（t-1 + 当前透明度，预先建好的那一级）。</summary>
    public static IBrush Overlay => OverlayBrushes[Math.Clamp(Level, 0, Levels)];

    public static void Set(Visual v, bool on)
    {
        if (on ? !Subscribers.Add(v) : !Subscribers.Remove(v)) return;
        if (Subscribers.Count > 0 && _timer == null)
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(FrameMs), DispatcherPriority.Render, (_, _) => Tick());
            _timer.Start();
        }
        else if (Subscribers.Count == 0 && _timer != null)
        {
            _timer.Stop();
            _timer = null;
            _lastLevel = -1;
        }
    }

    private static void Tick()
    {
        var level = Level;
        if (level == _lastLevel) return;
        _lastLevel = level;
        foreach (var s in Subscribers)
            if (s.IsEffectivelyVisible) s.InvalidateVisual();
    }
}
