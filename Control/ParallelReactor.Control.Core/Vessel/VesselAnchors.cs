using System;
using System.Collections.Generic;
using System.Globalization;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Vessel;

/// <summary>大号釜体的引线锚点（样稿 ui.REACTOR_ANCHORS 的键）。</summary>
public enum VesselAnchor { Inlet, Motor, Pt, Tt, Jacket, Heater, Liquid, Paddle, Outlet }

/// <summary>反应釜页标注框的锚点（样稿 js/pages/reactor.js stageLayout 的 A：SV 进气阀、HT 加热、TT 温度、M 搅拌、PT 压力、LV 液位）。</summary>
public enum VesselCallout { Sv, Ht, Tt, M, Pt, Lv }

/// <summary>一个点：viewBox 单位（150 × 230 的线描坐标），或换算到框里之后的设计 px。</summary>
public readonly record struct VesselPoint(double X, double Y);

/// <summary>一个矩形（viewBox 单位或框里的设计 px）。</summary>
public readonly record struct VesselRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// 釜体 viewBox（150 × 230）放进一个框里的换算（样稿 preserveAspectRatio="xMidYMid meet"）：等比缩放 <see cref="K"/>，居中偏移 <see cref="X"/> / <see cref="Y"/>。
/// 框 = 釜体控件的尺寸（设计 px，外框缩放之前；同样稿 fitReactors / reactorAnchors 除以 PR.shell.scale）。
/// 界面里用 Avalonia 的 Point / Rect / Matrix 时经 App 的 Controls/Vessel/VesselGeometry 转换。
/// </summary>
public readonly record struct VesselFit(double K, double X, double Y)
{
    /// <summary>viewBox 放进 <paramref name="width"/> × <paramref name="height"/> 的框：k = min(宽 / 150, 高 / 230)，多出的一边两侧平分。空框或无限大的框时 k = 0。</summary>
    public static VesselFit In(double width, double height)
    {
        double w = Math.Max(0, width), h = Math.Max(0, height);
        if (double.IsInfinity(w) || double.IsInfinity(h) || double.IsNaN(w) || double.IsNaN(h)) return new(0, 0, 0);
        double k = Math.Min(w / VesselAnchors.ViewW, h / VesselAnchors.ViewH);
        return new(k, (w - VesselAnchors.ViewW * k) / 2, (h - VesselAnchors.ViewH * k) / 2);
    }

    /// <summary>viewBox 坐标 → 框里的坐标。</summary>
    public VesselPoint ToBox(VesselPoint vb) => new(X + vb.X * K, Y + vb.Y * K);

    /// <summary>viewBox 矩形 → 框里的矩形。</summary>
    public VesselRect ToBox(VesselRect vb) => new(X + vb.X * K, Y + vb.Y * K, vb.Width * K, vb.Height * K);

    /// <summary>框里的坐标 → viewBox 坐标（k = 0 时返回原点）。</summary>
    public VesselPoint ToViewBox(VesselPoint p) => K <= 0 ? default : new((p.X - X) / K, (p.Y - Y) / K);

    /// <summary>整幅线描在框里占的矩形（150k × 230k）。</summary>
    public VesselRect Drawing => new(X, Y, VesselAnchors.ViewW * K, VesselAnchors.ViewH * K);
}

/// <summary>
/// 反应釜线描的坐标（MOCK js/ui.js：VB_W / VB_H、levelY、REACTOR_ANCHORS、reactorAnchors；js/pages/reactor.js stageLayout 的引线锚点 A；PORT-PLAN §2.3、§5 M4）。
/// 纯数值，不碰渲染（Control.Tests 的 VesselAnchorTests 直接测）。App 的 ReactorVessel 用它画液面、给 M5 反应釜页的引线标注算锚点。
/// </summary>
public static class VesselAnchors
{
    /// <summary>viewBox 150 × 230（样稿 VB_W / VB_H）。</summary>
    public const double ViewW = 150, ViewH = 230;

    /// <summary>液面：y = 178 − 液位 × 110（液位夹在 0.04 … 1：满 = 68，最低 = 173.6）。</summary>
    public const double LevelBottom = 178, LevelSpan = 110, MinLevel = 0.04;

    /// <summary>没有型号时的默认体积 / 釜容积（样稿 levelY：10 mL / 50 mL）。</summary>
    public const double DefaultVolumeMl = 10, DefaultCapacityMl = 50;

    /// <summary>
    /// 液位（体积 / 釜容积，未夹取）。样稿 levelY：体积缺失时取型号的溶液上限 × 0.5（没有型号时 10 mL），釜容积取型号的（没有型号时 50 mL）。
    /// 真机没有液位（HMI 不报体积，§3）：一律按「溶液上限的一半」画。
    /// </summary>
    public static double Level(double? volumeMl, double? capacityMl, double? maxFillMl)
    {
        bool model = capacityMl is > 0;
        double vol = volumeMl ?? (model ? (maxFillMl ?? 0) * 0.5 : DefaultVolumeMl);
        double max = model ? capacityMl!.Value : DefaultCapacityMl;
        return vol / max;
    }

    /// <summary>同上，型号取自 <see cref="ModelProfile"/>（可为 null）。</summary>
    public static double Level(double? volumeMl, ModelProfile? model) =>
        Level(volumeMl, model?.Vessel.VolumeMl, model?.Vessel.MaxFillMl);

    /// <summary>液面的 y（viewBox 单位，一位小数，同样稿 +(…).toFixed(1)）。液位 NaN 按默认（10 / 50）。</summary>
    public static double LevelY(double level)
    {
        if (double.IsNaN(level)) level = DefaultVolumeMl / DefaultCapacityMl;
        var y = LevelBottom - Math.Clamp(level, MinLevel, 1) * LevelSpan;
        return Round1(y);
    }

    /// <summary>液面的 y：按体积 / 型号（样稿 levelY(ch, dev)）。</summary>
    public static double LevelY(double? volumeMl, ModelProfile? model) => LevelY(Level(volumeMl, model));

    /// <summary>ui.REACTOR_ANCHORS（viewBox 坐标）；液体的 y 取液面 + 14（样稿 reactorAnchors）。</summary>
    public static VesselPoint Of(VesselAnchor a, double levelY) => a switch
    {
        VesselAnchor.Inlet => new(74, 12),
        VesselAnchor.Motor => new(107, 36),
        VesselAnchor.Pt => new(137, 76),
        VesselAnchor.Tt => new(10, 145),
        VesselAnchor.Jacket => new(117, 150),
        VesselAnchor.Heater => new(33, 110),
        VesselAnchor.Liquid => new(110, levelY + 14),
        VesselAnchor.Paddle => new(92, 180),
        VesselAnchor.Outlet => new(75, 224),
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    /// <summary>反应釜页标注框的锚点（viewBox 坐标，stageLayout 的 A）；LV 的 y = 液面 + 10，夹在 70 … 176。</summary>
    public static VesselPoint Of(VesselCallout c, double levelY) => c switch
    {
        VesselCallout.Sv => new(63.5, 9),
        VesselCallout.Ht => new(33, 98),
        VesselCallout.Tt => new(5, 144),
        VesselCallout.M => new(107, 36.5),
        VesselCallout.Pt => new(137, 76),
        VesselCallout.Lv => new(111, Math.Clamp(levelY + 10, 70, 176)),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>反应釜页：标注框的位置要让出的釜体最小宽度（stageLayout 的 VESSEL_MIN_W，≈ 147 px 高）。</summary>
    public const double VesselMinWidth = 96;

    /// <summary>
    /// 样稿 ui.reactorAnchors：全部锚点换算成框里的 px（一位小数）。框 = 釜体控件的尺寸（<paramref name="width"/> × <paramref name="height"/>），
    /// 缩放 k 见 <see cref="VesselFit.In"/>。
    /// </summary>
    public static IReadOnlyDictionary<VesselAnchor, VesselPoint> InBox(double width, double height, double levelY)
    {
        var fit = VesselFit.In(width, height);
        var d = new Dictionary<VesselAnchor, VesselPoint>();
        foreach (VesselAnchor a in Enum.GetValues(typeof(VesselAnchor)))
        {
            var p = fit.ToBox(Of(a, levelY));
            d[a] = new VesselPoint(Round1(p.X), Round1(p.Y));
        }
        return d;
    }

    private static double Round1(double v) => double.Parse(JsNumber.ToFixed(v, 1), NumberStyles.Float, CultureInfo.InvariantCulture);
}
