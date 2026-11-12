using Avalonia;
using ParallelReactor.Control.Core.Vessel;

namespace ParallelReactor.Control.Controls.Vessel;

/// <summary>
/// Core 的釜体坐标（<see cref="VesselAnchors"/> / <see cref="VesselFit"/>，样稿 REACTOR_ANCHORS、levelY、fitReactors 的数值，PORT-PLAN §2.3）
/// 与 Avalonia 的 Point / Size / Rect / Matrix 之间的转换：<see cref="ReactorVessel"/> 画图、M5 反应釜页的引线标注用。
/// </summary>
public static class VesselGeometry
{
    /// <summary>viewBox 放进控件的框（<see cref="VesselFit.In"/>）。</summary>
    public static VesselFit FitIn(Size box) => VesselFit.In(box.Width, box.Height);

    /// <summary>viewBox → 框的矩阵（先缩放再平移）：在它下面按 viewBox 坐标画线描。</summary>
    public static Matrix ToMatrix(this VesselFit f) => Matrix.CreateScale(f.K, f.K) * Matrix.CreateTranslation(f.X, f.Y);

    /// <summary>viewBox 坐标 → 框里的坐标。</summary>
    public static Point ToBox(this VesselFit f, Point vb) => f.ToBox(new VesselPoint(vb.X, vb.Y)).ToPoint();

    /// <summary>viewBox 矩形 → 框里的矩形。</summary>
    public static Rect ToBox(this VesselFit f, Rect vb) => f.ToBox(new VesselRect(vb.X, vb.Y, vb.Width, vb.Height)).ToRect();

    /// <summary>框里的坐标 → viewBox 坐标（k = 0 时返回原点）。</summary>
    public static Point ToViewBox(this VesselFit f, Point p) => f.ToViewBox(new VesselPoint(p.X, p.Y)).ToPoint();

    public static Point ToPoint(this VesselPoint p) => new(p.X, p.Y);

    public static Rect ToRect(this VesselRect r) => new(r.X, r.Y, r.Width, r.Height);
}
