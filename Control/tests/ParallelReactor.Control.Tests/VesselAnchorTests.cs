using System;
using System.Linq;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Vessel;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 釜体的坐标（PORT-PLAN §5 M4「VesselAnchorTests」；Core 的 Vessel/VesselAnchors）：viewBox 150 × 230 等比放进框里（xMidYMid meet）、
/// 液面 levelY（体积 / 釜容积，夹在 0.04 … 1，一位小数）、ui.REACTOR_ANCHORS 与 reactorAnchors 的换算、反应釜页标注框的锚点（stageLayout 的 A）。
/// 期望值按样稿 js/ui.js 的公式逐个算出（同一组输入在 JS 里给出同样的数）。与 Avalonia 类型的转换（矩阵等）在 App.Tests 的 VesselGeometryTests。
/// </summary>
public class VesselAnchorTests
{
    private static ModelProfile Model(string key) => ModelProfiles.Embedded.Get(key)!;

    [Fact]
    public void ViewBox_Is150By230()
    {
        Assert.Equal(150, VesselAnchors.ViewW);
        Assert.Equal(230, VesselAnchors.ViewH);
    }

    [Theory]
    [InlineData(104, 150, 0.6521739130434783, 3.0869565217391326, 0)]   // 陈列页 card 框
    [InlineData(180, 270, 1.173913043478261, 1.956521739130423, 0)]    // 陈列页 large 框
    [InlineData(36, 56, 0.24, 0, 0.4)]                                  // 总览 8 路卡片
    [InlineData(150, 230, 1, 0, 0)]
    public void Fit_UniformScale_Centered(double w, double h, double k, double x, double y)
    {
        var f = VesselFit.In(w, h);
        Assert.Equal(k, f.K, 9);
        Assert.Equal(x, f.X, 9);
        Assert.Equal(y, f.Y, 9);
        // 整幅图在框里、贴住较紧的一边
        var d = f.Drawing;
        Assert.True(d.X >= -1e-9 && d.Y >= -1e-9 && d.Right <= w + 1e-9 && d.Bottom <= h + 1e-9);
        Assert.True(Math.Abs(d.Width - w) < 1e-9 || Math.Abs(d.Height - h) < 1e-9);
    }

    [Fact]
    public void Fit_EmptyOrInfiniteBox_HasZeroScale()
    {
        Assert.Equal(0, VesselFit.In(0, 0).K);
        Assert.Equal(0, VesselFit.In(double.PositiveInfinity, 100).K);
        Assert.Equal(0, VesselFit.In(double.NaN, 100).K);
        Assert.Equal(default, VesselFit.In(0, 0).ToViewBox(new VesselPoint(5, 5)));
    }

    [Fact]
    public void Fit_ToBox_ToViewBox_RoundTrip()
    {
        var f = VesselFit.In(129, 198);
        foreach (var p in new[] { new VesselPoint(0, 0), new VesselPoint(150, 230), new VesselPoint(74, 12), new VesselPoint(132.5, 76.25) })
        {
            var q = f.ToBox(p);
            Assert.Equal(f.X + p.X * f.K, q.X, 9);
            Assert.Equal(f.Y + p.Y * f.K, q.Y, 9);
            var back = f.ToViewBox(q);
            Assert.Equal(p.X, back.X, 9);
            Assert.Equal(p.Y, back.Y, 9);
        }
        var r = f.ToBox(new VesselRect(29, 49, 100, 10));
        Assert.Equal(29 * 0.86, r.X, 9);
        Assert.Equal(49 * 0.86 + f.Y, r.Y, 9);
        Assert.Equal(86, r.Width, 9);
    }

    [Theory]
    // 没有型号：10 mL / 50 mL = 0.2 → 156
    [InlineData(null, null, null, 156)]
    // HT-PR8（50 mL 釜、溶液 ≤ 20 mL）：没有体积 → 10 mL → 156；20 mL → 134；0 → 夹到 0.04 → 173.6；超过釜容积 → 夹到 1 → 68
    [InlineData(null, 50.0, 20.0, 156)]
    [InlineData(20.0, 50.0, 20.0, 134)]
    [InlineData(0.0, 50.0, 20.0, 173.6)]
    [InlineData(60.0, 50.0, 20.0, 68)]
    // HT-PR4（100 / 60）：没有体积 → 30 mL → 145；45 mL → 128.5
    [InlineData(null, 100.0, 60.0, 145)]
    [InlineData(45.0, 100.0, 60.0, 128.5)]
    // HT-PR16（10 / 6）：4.2 mL → 131.8
    [InlineData(4.2, 10.0, 6.0, 131.8)]
    public void LevelY_SameAsMockLevelY(double? vol, double? cap, double? maxFill, double y)
    {
        Assert.Equal(y, VesselAnchors.LevelY(VesselAnchors.Level(vol, cap, maxFill)));
    }

    [Fact]
    public void LevelY_FromModelProfiles_AndNaNDefault()
    {
        Assert.Equal(156, VesselAnchors.LevelY(null, Model("PR8")));
        Assert.Equal(156, VesselAnchors.LevelY(null, Model("PR8-4")));
        Assert.Equal(145, VesselAnchors.LevelY(null, Model("PR4")));
        Assert.Equal(145, VesselAnchors.LevelY(null, Model("PR16")));
        Assert.Equal(134, VesselAnchors.LevelY(20, Model("PR8")));
        Assert.Equal(156, VesselAnchors.LevelY(null, (ModelProfile?)null));
        Assert.Equal(156, VesselAnchors.LevelY(double.NaN));
        Assert.Equal(VesselAnchors.LevelBottom - VesselAnchors.LevelSpan, VesselAnchors.LevelY(1));
        Assert.Equal(173.6, VesselAnchors.LevelY(-1));
    }

    [Fact]
    public void ReactorAnchors_ViewBoxValues()
    {
        Assert.Equal(new VesselPoint(74, 12), VesselAnchors.Of(VesselAnchor.Inlet, 134));
        Assert.Equal(new VesselPoint(107, 36), VesselAnchors.Of(VesselAnchor.Motor, 134));
        Assert.Equal(new VesselPoint(137, 76), VesselAnchors.Of(VesselAnchor.Pt, 134));
        Assert.Equal(new VesselPoint(10, 145), VesselAnchors.Of(VesselAnchor.Tt, 134));
        Assert.Equal(new VesselPoint(117, 150), VesselAnchors.Of(VesselAnchor.Jacket, 134));
        Assert.Equal(new VesselPoint(33, 110), VesselAnchors.Of(VesselAnchor.Heater, 134));
        Assert.Equal(new VesselPoint(110, 148), VesselAnchors.Of(VesselAnchor.Liquid, 134));   // 液面 + 14
        Assert.Equal(new VesselPoint(92, 180), VesselAnchors.Of(VesselAnchor.Paddle, 134));
        Assert.Equal(new VesselPoint(75, 224), VesselAnchors.Of(VesselAnchor.Outlet, 134));
    }

    [Fact]
    public void ReactorAnchors_InBox_SameAsMockReactorAnchors()
    {
        // ui.reactorAnchors：180 × 270 的框（k = 1.1739，x 偏 1.957），液面 134；一位小数
        var a = VesselAnchors.InBox(180, 270, 134);
        Assert.Equal(Enum.GetValues(typeof(VesselAnchor)).Length, a.Count);
        Assert.Equal(new VesselPoint(88.8, 14.1), a[VesselAnchor.Inlet]);
        Assert.Equal(new VesselPoint(127.6, 42.3), a[VesselAnchor.Motor]);
        Assert.Equal(new VesselPoint(162.8, 89.2), a[VesselAnchor.Pt]);
        Assert.Equal(new VesselPoint(13.7, 170.2), a[VesselAnchor.Tt]);
        Assert.Equal(new VesselPoint(139.3, 176.1), a[VesselAnchor.Jacket]);
        Assert.Equal(new VesselPoint(40.7, 129.1), a[VesselAnchor.Heater]);
        Assert.Equal(new VesselPoint(131.1, 173.7), a[VesselAnchor.Liquid]);
        Assert.Equal(new VesselPoint(110, 211.3), a[VesselAnchor.Paddle]);
        Assert.Equal(new VesselPoint(90, 263), a[VesselAnchor.Outlet]);
    }

    [Theory]
    [InlineData(134, 144)]     // 液面 + 10
    [InlineData(50, 70)]       // 夹到 70
    [InlineData(173.6, 176)]   // 夹到 176
    public void Callouts_SvHtTtMPtLv(double levelY, double lvY)
    {
        Assert.Equal(new VesselPoint(63.5, 9), VesselAnchors.Of(VesselCallout.Sv, levelY));
        Assert.Equal(new VesselPoint(33, 98), VesselAnchors.Of(VesselCallout.Ht, levelY));
        Assert.Equal(new VesselPoint(5, 144), VesselAnchors.Of(VesselCallout.Tt, levelY));
        Assert.Equal(new VesselPoint(107, 36.5), VesselAnchors.Of(VesselCallout.M, levelY));
        Assert.Equal(new VesselPoint(137, 76), VesselAnchors.Of(VesselCallout.Pt, levelY));
        Assert.Equal(new VesselPoint(111, lvY), VesselAnchors.Of(VesselCallout.Lv, levelY));
    }

    [Fact]
    public void AllAnchors_InsideViewBox()
    {
        foreach (var a in Enum.GetValues(typeof(VesselAnchor)).Cast<VesselAnchor>())
        {
            var p = VesselAnchors.Of(a, VesselAnchors.LevelY(0.5));
            Assert.InRange(p.X, 0, VesselAnchors.ViewW);
            Assert.InRange(p.Y, 0, VesselAnchors.ViewH);
        }
        foreach (var c in Enum.GetValues(typeof(VesselCallout)).Cast<VesselCallout>())
        {
            var p = VesselAnchors.Of(c, VesselAnchors.LevelY(0.5));
            Assert.InRange(p.X, 0, VesselAnchors.ViewW);
            Assert.InRange(p.Y, 0, VesselAnchors.ViewH);
        }
        Assert.Equal(96, VesselAnchors.VesselMinWidth);
    }
}
