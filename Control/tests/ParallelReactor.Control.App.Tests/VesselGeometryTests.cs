using System;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.Vessel;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core 的釜体坐标（Core/Vessel/VesselAnchors，数值测试在 Control.Tests 的 VesselAnchorTests）与 Avalonia 类型之间的转换（Controls/Vessel/VesselGeometry）：
/// 控件的框 → VesselFit；画线描用的矩阵 = 先缩放再平移，与 Core 的 ToBox 逐点一致；ReactorVessel 的引线锚点 = Core 的锚点换算到控件的框里。
/// </summary>
public class VesselGeometryTests
{
    [Fact]
    public void FitIn_Matrix_ToBox_AgreeWithCore()
    {
        var f = VesselGeometry.FitIn(new Size(129, 198));
        Assert.Equal(VesselFit.In(129, 198), f);
        Assert.Equal(0.86, f.K, 9);
        foreach (var p in new[] { new Point(0, 0), new Point(150, 230), new Point(74, 12), new Point(132.5, 76.25) })
        {
            var q = f.ToBox(p);
            var core = f.ToBox(new VesselPoint(p.X, p.Y));
            Assert.Equal(core.X, q.X, 9);
            Assert.Equal(core.Y, q.Y, 9);
            var m = f.ToMatrix().Transform(p);
            Assert.Equal(q.X, m.X, 9);
            Assert.Equal(q.Y, m.Y, 9);
            var back = f.ToViewBox(q);
            Assert.Equal(p.X, back.X, 9);
            Assert.Equal(p.Y, back.Y, 9);
        }
        var r = f.ToBox(new Rect(29, 49, 100, 10));
        Assert.Equal(29 * 0.86, r.X, 9);
        Assert.Equal(49 * 0.86 + f.Y, r.Y, 9);
        Assert.Equal(86, r.Width, 9);
        Assert.Equal(new Rect(f.X, f.Y, 150 * f.K, 230 * f.K), f.Drawing.ToRect());
        Assert.Equal(0, VesselGeometry.FitIn(new Size(double.PositiveInfinity, 100)).K);
    }

    [Fact]
    public void AnchorsInControlBox_SameAsMockReactorAnchors()
    {
        // ReactorVessel.AnchorPoint = Fit.ToBox(VesselAnchors.Of(a, 液面)).ToPoint()；样稿 ui.reactorAnchors 是同一个数取一位小数
        var f = VesselGeometry.FitIn(new Size(180, 270));
        var mock = VesselAnchors.InBox(180, 270, 134);
        foreach (var a in Enum.GetValues(typeof(VesselAnchor)).Cast<VesselAnchor>())
        {
            var p = f.ToBox(VesselAnchors.Of(a, 134)).ToPoint();
            Assert.Equal(mock[a].X, p.X, 0.05);
            Assert.Equal(mock[a].Y, p.Y, 0.05);
        }
    }
}
