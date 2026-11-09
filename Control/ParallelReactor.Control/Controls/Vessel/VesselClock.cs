using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;

namespace ParallelReactor.Control.Controls.Vessel;

/// <summary>
/// 釜体动画共用的计时器（R10；同 Controls/Pr/PulseClock）：只有需要动（搅拌桨、气泡、大号釜体的超压脉冲）、已上树、可见、未冻结的釜体订阅；
/// 没有订阅者时计时器停下。20 帧/s，只重画实际可见的订阅者。冻结（Theme/Frozen：截图、freeze=1）时不订阅，
/// 画面是样稿不加 .pr-anim 时的静止样子（桨不缩、气泡在原位不透明、红圈 scale .7 透明度 .9）。每一帧的值见 <see cref="VesselMotion"/>。
/// </summary>
internal static class VesselClock
{
    public const int FrameMs = 50;

    private static readonly HashSet<Visual> Subscribers = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;

    /// <summary>自程序启动以来的秒数（所有釜体同相，同样稿 .pr-anim 一起加上去的动画）。</summary>
    public static double Now => Clock.Elapsed.TotalSeconds;

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
        }
    }

    private static void Tick()
    {
        foreach (var s in Subscribers)
            if (s.IsEffectivelyVisible) s.InvalidateVisual();
    }
}

/// <summary>
/// 釜体动画的关键帧（样稿 base.css）：搅拌桨 @keyframes pr-paddle 1.6 s linear（scaleX 1 → 0.25 → 1，绕桨的中心）、
/// 气泡 pr-bubble 2.4 s ease-in（上移 44、透明度 .95 → 0；三个气泡的 animation-delay 0 / 0.8 / 1.6 s）、
/// 超压红圈的脉冲 pr-ring 1.2 s ease-out（scale .7 → 1.9、透明度 .9 → 0）。纯函数（可测）。
/// </summary>
public static class VesselMotion
{
    public const double PaddleSec = 1.6, BubbleSec = 2.4, PulseSec = 1.2;

    /// <summary>三个气泡的 animation-delay（秒）。</summary>
    public static readonly IReadOnlyList<double> BubbleDelay = new[] { 0, 0.8, 1.6 };

    /// <summary>pr-paddle：0 % / 100 % scaleX(1)、50 % scaleX(.25)，linear。</summary>
    public static double PaddleScale(double t)
    {
        double p = Frac(t / PaddleSec);
        return p < 0.5 ? 1 - 0.75 * (p * 2) : 0.25 + 0.75 * (p * 2 - 1);
    }

    /// <summary>pr-bubble 的一帧：(上移量 viewBox 单位，向上为负, 透明度)。动画还没开始（t &lt; delay）时是静止的样子：(0, 1)。</summary>
    public static (double Dy, double Opacity) Bubble(double t, int i)
    {
        double d = t - BubbleDelay[i];
        if (d < 0) return (0, 1);
        double p = Ease(Frac(d / BubbleSec), 0.42, 0, 1, 1);   // ease-in
        return (-44 * p, 0.95 * (1 - p));
    }

    /// <summary>pr-ring 的一帧：(scale, 透明度)，ease-out。</summary>
    public static (double Scale, double Opacity) Pulse(double t)
    {
        double p = Ease(Frac(t / PulseSec), 0, 0, 0.58, 1);    // ease-out
        return (0.7 + 1.2 * p, 0.9 * (1 - p));
    }

    /// <summary>冻结时脉冲圈的样子（样稿 .rv-pt-pulse 的静态样式：scale(.7)、透明度 .9）。</summary>
    public static readonly (double Scale, double Opacity) PulseStill = (0.7, 0.9);

    private static double Frac(double x) => x - Math.Floor(x);

    /// <summary>CSS cubic-bezier(x1, y1, x2, y2) 在进度 x 处的值（二分求曲线参数）。</summary>
    public static double Ease(double x, double x1, double y1, double x2, double y2)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lo = 0, hi = 1, s = x;
        for (int i = 0; i < 30; i++)
        {
            s = (lo + hi) / 2;
            double bx = Bez(s, x1, x2);
            if (Math.Abs(bx - x) < 1e-7) break;
            if (bx < x) lo = s; else hi = s;
        }
        return Bez(s, y1, y2);
    }

    private static double Bez(double s, double p1, double p2)
    {
        double u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }
}
