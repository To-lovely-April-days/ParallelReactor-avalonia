using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Layout;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Views.Pages.Reactor;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 c2-ui-2：反应釜页面板网格的边按<b>设备像素</b>对齐（ReactorGridPanel.Snap(矩形, 渲染缩放)）。以前按整 DIP 取整，Avalonia 排版时
/// （Layoutable.ArrangeCore：位置 RoundLayoutValue 银行家舍入、尺寸 RoundLayoutSizeUp 向上取整）再取一次整，125 % 下约 10 % 的网格尺寸分隔线没了（0 px），
/// 150 % / 175 % 下有的变成 2 px。这里按 Avalonia 自己的取整函数算出面板落在哪些设备像素上，扫网格的宽高 × 关掉的面板：
/// 100 % / 125 % / 150 % / 175 % 下每条分隔线都正好 1 个设备像素（200 % 是 2 个），面板不出网格、贴着网格的边；100 % 时与以前逐位相同（截图不变）。
/// 纯函数，不启动 Avalonia。
/// </summary>
public class M5ReviewGridSnapTests
{
    private static readonly ReactorPanel[] All = { ReactorPanel.Program, ReactorPanel.Vessel, ReactorPanel.Props, ReactorPanel.Curves };

    /// <summary>以前的取整（整 DIP、四条边各自「.5 进位」）：100 % 时新的取整必须与它逐位相同。</summary>
    private static double OldEdge(double v) => Math.Floor(Math.Round(v * 64) / 64 + 0.5);

    private static Rect OldSnap(Rect r) =>
        r.Width <= 0 || r.Height <= 0 ? r : new Rect(OldEdge(r.X), OldEdge(r.Y), Math.Max(0, OldEdge(r.Right) - OldEdge(r.X)), Math.Max(0, OldEdge(r.Bottom) - OldEdge(r.Y)));

    /// <summary>子元素按 <paramref name="snapped"/> 排版后真正占的设备像素（同 ArrangeCore：位置 RoundLayoutValue、尺寸 RoundLayoutSizeUp；Stretch、没有外边距）。</summary>
    private static (double L, double T, double R, double B) Device(Rect snapped, double s)
    {
        double x = LayoutHelper.RoundLayoutValue(snapped.X, s), y = LayoutHelper.RoundLayoutValue(snapped.Y, s);
        var size = LayoutHelper.RoundLayoutSizeUp(snapped.Size, s, s);
        return (Math.Round(x * s, 6), Math.Round(y * s, 6), Math.Round((x + size.Width) * s, 6), Math.Round((y + size.Height) * s, 6));
    }

    private static IEnumerable<ReactorPanel[]> ClosedSets()
    {
        for (int m = 0; m < 16; m++) yield return All.Where((_, i) => (m & (1 << i)) != 0).ToArray();
    }

    /// <summary>检查一个网格（宽高 = 设备像素 / 缩放，同 Avalonia 给网格自己的尺寸）：分隔线、出界、贴边。返回问题（空 = 没有）。</summary>
    private static IEnumerable<string> Check(int wp, int hp, double s, ReactorPanel[] closed)
    {
        double w = wp / s, h = hp / s;
        var g = ReactorPageLayout.Compute(w, h, closed);
        var px = new List<(ReactorPanel P, FlowRect R, (double L, double T, double R, double B) D)>();
        foreach (var (p, r) in g.Panels) px.Add((p, r, Device(ReactorGridPanel.Snap(new Rect(r.X, r.Y, r.W, r.H), s), s)));
        string Where() => $"{s:0.##}× 网格 {wp}×{hp} px（关掉 {(closed.Length == 0 ? "无" : string.Join("、", closed))}）";
        double want = ReactorGridPanel.DividerPx(s);
        foreach (var a in px)
        {
            var d = a.D;
            if (d.L % 1 != 0 || d.T % 1 != 0 || d.R % 1 != 0 || d.B % 1 != 0) yield return $"{Where()}：{a.P} 不在整设备像素上 {d}";
            if (d.L < 0 || d.T < 0 || d.R > wp || d.B > hp) yield return $"{Where()}：{a.P} 出了网格 {d}";
            // 贴着网格边的面板（网格坐标在 0 / 宽 / 高 上）照样贴着
            if (a.R.X == 0 && d.L != 0) yield return $"{Where()}：{a.P} 左边离开网格 {d.L}";
            if (a.R.Y == 0 && d.T != 0) yield return $"{Where()}：{a.P} 上边离开网格 {d.T}";
            if (Math.Abs(a.R.X + a.R.W - w) < 1e-6 && d.R != wp) yield return $"{Where()}：{a.P} 右边 {d.R} ≠ {wp}";
            if (Math.Abs(a.R.Y + a.R.H - h) < 1e-6 && d.B != hp) yield return $"{Where()}：{a.P} 下边 {d.B} ≠ {hp}";
            foreach (var b in px)
            {
                if (a.P == b.P) continue;
                // 左右相邻（中间 1 DIP 的竖分隔线）/ 上下相邻（横分隔线）
                bool lr = Math.Abs(b.R.X - (a.R.X + a.R.W) - ReactorPageLayout.Gap) < 1e-6 && b.R.Y < a.R.Y + a.R.H && a.R.Y < b.R.Y + b.R.H;
                bool tb = Math.Abs(b.R.Y - (a.R.Y + a.R.H) - ReactorPageLayout.Gap) < 1e-6 && b.R.X < a.R.X + a.R.W && a.R.X < b.R.X + b.R.W;
                if (lr && b.D.L - d.R != want) yield return $"{Where()}：{a.P} | {b.P} 分隔线 {b.D.L - d.R} px（应 {want}）";
                if (tb && b.D.T - d.B != want) yield return $"{Where()}：{a.P} / {b.P} 分隔线 {b.D.T - d.B} px（应 {want}）";
            }
        }
    }

    [Fact]
    public void Dividers_AreExactlyOneDevicePixel_At100To175Percent()
    {
        var bad = new List<string>();
        int n = 0;
        foreach (double s in new[] { 1.0, 1.25, 1.5, 1.75 })
        {
            Assert.Equal(1, ReactorGridPanel.DividerPx(s));
            // 宽：每个设备像素（不关面板），关掉面板的组合每 7 px 一个；高：几个典型值
            foreach (int hp in new[] { 600, 641, 712, 752, 913, 1100 }.Select(x => (int)Math.Round(x * s)))
                for (int wp = (int)Math.Ceiling(900 * s); wp <= (int)(2600 * s); wp++)
                    foreach (var closed in ClosedSets())
                    {
                        if (closed.Length > 0 && wp % 7 != 0) continue;
                        n++;
                        bad.AddRange(Check(wp, hp, s, closed));
                        if (bad.Count > 12) break;
                    }
            // 高：每个设备像素（宽版式与页签版式各一个宽）
            foreach (int wp in new[] { 1226, 1526, 1866, 2496 }.Select(x => (int)Math.Round(x * s)))
                for (int hp = (int)Math.Ceiling(480 * s); hp <= (int)(1200 * s); hp++)
                {
                    n++;
                    bad.AddRange(Check(wp, hp, s, Array.Empty<ReactorPanel>()));
                    if (bad.Count > 12) break;
                }
        }
        Assert.True(n > 20000, "网格太少：" + n);
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(12)));
    }

    /// <summary>200 %（Retina）：1 DIP 的分隔线是 2 个设备像素（floor(缩放)，不是发丝线）。</summary>
    [Fact]
    public void Dividers_AtTwoX_AreTwoDevicePixels()
    {
        Assert.Equal(2, ReactorGridPanel.DividerPx(2));
        Assert.Equal(2, ReactorGridPanel.DividerPx(2.5));
        Assert.Equal(1, ReactorGridPanel.DividerPx(0.75));
        var bad = new List<string>();
        for (int wp = 1800; wp <= 5200; wp += 3)
            foreach (int hp in new[] { 1200, 1504, 1825 })
                bad.AddRange(Check(wp, hp, 2, Array.Empty<ReactorPanel>()));
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(12)));
    }

    /// <summary>100 %：与以前的整 DIP 取整逐位相同（反应釜页的截图一张不变）。</summary>
    [Fact]
    public void At100Percent_SameAsBefore()
    {
        int n = 0;
        foreach (double h in new[] { 600.0, 641.5, 700.25, 752.0, 752.5, 913.5, 1100.0 })
            for (double w = 900; w <= 2600; w += 0.25)
                foreach (var closed in ClosedSets())
                {
                    if (closed.Length > 0 && w % 3 != 0) continue;
                    foreach (var r in ReactorPageLayout.Compute(w, h, closed).Panels.Values)
                    {
                        var rect = new Rect(r.X, r.Y, r.W, r.H);
                        var a = ReactorGridPanel.Snap(rect, 1);
                        var b = OldSnap(rect);
                        Assert.True(a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Width.Equals(b.Width) && a.Height.Equals(b.Height), $"{w}×{h}：{a} ≠ 以前的 {b}");
                        Assert.Equal(a, ReactorGridPanel.Snap(rect));
                        n++;
                    }
                }
        Assert.True(n > 50000, "矩形太少：" + n);
        // 整个网格的矩形（放大的面板 / 未安装的说明）：网格自己的尺寸已经在设备像素上（125 % 下 1532 × 890 px），原样不动
        Assert.Equal(new Rect(0, 0, 1225.6, 712), ReactorGridPanel.Snap(new Rect(0, 0, 1225.6, 712), 1.25));
    }
}
