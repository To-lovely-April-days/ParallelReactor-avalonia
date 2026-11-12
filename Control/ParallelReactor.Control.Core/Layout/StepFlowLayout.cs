using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Layout;

/// <summary>一个矩形（设计 px）。</summary>
public readonly record struct FlowRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>
/// 每行卡片数的结果（样稿 flowLayout）：每行几张、步骤卡宽、放大档（0 / 1 / 2）与倍数（1 / 1.15 / 1.3）、按放大后折算的画布设计宽度（含滚动条）。
/// </summary>
public readonly record struct StepFlowFit(int Per, double StepW, int Zoom, double Scale, double DesignWidth)
{
    /// <summary>每行一张：卡片 → 条件框 → 向下的连线（否则横向箭头 + 行间分隔线）。</summary>
    public bool Column => Per == 1;
}

/// <summary>
/// 画布里的一步（设计 px，相对画布（.reactor-flow）的边框盒左上角）：所在行 / 列；整列（卡片 + 18 + 条件框 44）、卡片、条件框（右移 22、与卡片同宽）、
/// 条件框上方的竖线（卡片底到条件框顶：x = 条件框左边 + 14，18 px 高，--line-strong）。
/// </summary>
public sealed record StepFlowItem(int Index, int Row, int Column, FlowRect Col, FlowRect Card, FlowRect Cond)
{
    /// <summary>条件框上方竖线的 x、上端、下端（base.css .pr-step-cond::before：left 14、top −19、高 18）。</summary>
    public (double X, double Top, double Bottom) Connector => (Cond.X + StepFlowLayout.ConnectorInset, Cond.Y - 19, Cond.Y - 1);
}

/// <summary>
/// 画布的版面（设计 px）：每一步、每行（y、高 = 行里最高的一列、宽 = 整行（每行都一样宽，同 flex 拉伸））、横向箭头（56 × 44，靠行底）、行间分隔线（每行多张时）、
/// 竖向连线（每行一张时，30 高）、尾部（安全联锁框，上边距 30）、画布的设计宽 / 高；<see cref="Scale"/> = 放大倍数（界面按它整体缩放画布）；
/// <see cref="ContentWidth"/> / <see cref="ContentHeight"/> = 放大后在滚动区里的实际尺寸，<see cref="OffsetX"/> = 水平居中的左边距（实际 px）。
/// </summary>
public sealed record StepFlowGeometry(
    StepFlowFit Fit,
    bool Preview,
    double Width,
    double Height,
    IReadOnlyList<StepFlowItem> Items,
    IReadOnlyList<FlowRect> Rows,
    IReadOnlyList<FlowRect> Arrows,
    IReadOnlyList<FlowRect> Dividers,
    IReadOnlyList<FlowRect> VLinks,
    FlowRect? Tail,
    double ViewportWidth)
{
    public double Scale => Fit.Scale;
    public double ContentWidth => Width * Scale;
    public double ContentHeight => Height * Scale;
    public double OffsetX => Math.Max(0, (ViewportWidth - ContentWidth) / 2);
}

/// <summary>滚动目标：新的 scrollTop / scrollLeft（实际 px）；null = 不用动。</summary>
public readonly record struct FlowScroll(double? Top, double? Left);

/// <summary>
/// 反应釜页「程序」画布的版面（样稿 js/pages/reactor.js flowLayout / zoomedW / zoomFits / flowHTML / autoScroll / condHTML 与 css/base.css .pr-step* /
/// css/reactor.css .reactor-flow / .reactor-vlink / .reactor-safe 的数字，纯函数；StepFlowLayoutTests 用样稿在 WebKit 里量到的版面逐个核对）。
/// <list type="bullet">
/// <item>每行卡片数：放得下几张（卡片 ≥ 214 px，卡片之间箭头 56，每行末尾留条件框右移的 22），再把各行均衡（5 步、最多 2 张 → 2 + 2 + 1；最多 4 张 → 3 + 2）；
///   每行一张时卡片尽量宽（≤ 300），多张时 214–280。<b>放大</b>（115 % / 130 %）按放大后的设计宽度重排（每行的卡片变少），从不横向滚动；
///   放大后一行放不下一张最窄的卡时才不能再放大（<see cref="ZoomFits"/>），面板变窄时回到 100 %（<see cref="ClampZoom"/>）。</item>
/// <item>画布：内边距 上 20（程序库预览 12）、左右 16、下 28；每行 = 列（卡片 + 18 + 条件框 44）+ 箭头；行之间：每行多张 = 分隔线（上 28、1 px、下 26），
///   每行一张 = 竖向连线 30；尾部安全联锁框上边距 30。卡片高度由界面量（字体决定），这里只排位置。</item>
/// <item>自动滚动 / 定位当前步（<see cref="ScrollTo"/>）：当前步（或中止的那一步）不在可见范围时滚到它（卡片上方留 16 × 倍数）；卡片 + 条件框只比可见区高一点（≤ 28 × 倍数）时
///   让条件框（当前步的进度）整个露出来。</item>
/// </list>
/// 单位：设计 px（DIP）；放大时界面把整个画布按 <see cref="StepFlowGeometry.Scale"/> 缩放（LayoutTransform），滚动用实际 px。
/// </summary>
public static class StepFlowLayout
{
    // ———— 尺寸常量（设计 px） ————
    /// <summary>箭头宽 56、条件框右移 22、画布左右内边距 16、滚动条 8（reactor.js ARROW_W / COND_OFF / PAD_X / SCROLL_W）。</summary>
    public const double ArrowW = 56, CondOff = 22, PadX = 16, ScrollW = 8;
    /// <summary>步骤卡宽：最窄 214（「3: 反应 · 当前 · 剩 142 min」与条件框完整放下）、每行多张最宽 280、每行一张最宽 300；组件陈列等不排版的地方默认 248（base.css）。</summary>
    public const double StepMin = 214, StepMax = 280, StepOneMax = 300, StepDefault = 248;
    /// <summary>画布内边距：上 20（程序库预览 12）、下 28（reactor.css .reactor-flow）。</summary>
    public const double PadTop = 20, PadTopPreview = 12, PadBottom = 28;
    /// <summary>卡片与条件框之间 18（.pr-step-col gap）；条件框 / 箭头高 44；箭头线从左 22 到右 4，箭头尖在右 2（7 × 8）。</summary>
    public const double ColGap = 18, CondH = 44, ArrowH = 44;
    /// <summary>条件框上方竖线距条件框左边 14（.pr-step-cond::before）；竖向连线在画布内容左边 36（.reactor-vlink::before，= 条件框左边 + 14），箭头尖在 32、高 7。</summary>
    public const double ConnectorInset = 14, VLinkLineX = 36, VLinkHeadX = 32;
    /// <summary>行间分隔线：上 28、线 1、下 26（.pr-step-divider）；竖向连线 30（.reactor-vlink）；尾部（安全联锁）上边距 30（.reactor-safe）。</summary>
    public const double DividerTop = 28, DividerH = 1, DividerBottom = 26, VLinkH = 30, TailTop = 30;
    /// <summary>自动滚动时卡片上方留 16；卡片 + 条件框比可见区高不超过 28 时让条件框整个露出来（都乘放大倍数）。</summary>
    public const double ScrollMargin = 16, ScrollSlack = 28;

    /// <summary>放大档：1 / 1.15 / 1.3（只放大不缩小，字号不低于基准）。</summary>
    public static readonly IReadOnlyList<double> Zooms = Array.AsReadOnly(new[] { 1.0, 1.15, 1.3 });

    /// <summary>放大档的倍数（越界按 1）。</summary>
    public static double ScaleOf(int zoom) => zoom >= 0 && zoom < Zooms.Count ? Zooms[zoom] : 1;

    /// <summary>放大档的文字（画布工具左边，100 % 时不显示）：'115 %' / '130 %'。</summary>
    public static string ZoomText(int zoom) => Math.Round(ScaleOf(zoom) * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %";

    /// <summary>
    /// 放大后画布可用的设计宽度（样稿 zoomedW）：外宽 <paramref name="outerWidth"/>（滚动区的 offsetWidth，含滚动条）按放大倍数折算：floor((w − 8) / z) + 8。
    /// </summary>
    public static double ZoomedWidth(double outerWidth, int zoom) => Math.Floor((outerWidth - ScrollW) / ScaleOf(zoom)) + ScrollW;

    /// <summary>放大到这一档后还放得下每行一张最窄的卡（样稿 zoomFits）：(214 + 22 + 32) × z ≤ 外宽 − 8。外宽未知（0）时算放得下。</summary>
    public static bool ZoomFits(double outerWidth, int zoom) => outerWidth <= 0 || (StepMin + CondOff + 2 * PadX) * ScaleOf(zoom) <= outerWidth - ScrollW;

    /// <summary>面板变窄（还原放大的面板等）：放大后放不下就回到 100 %（样稿 measure）。</summary>
    public static int ClampZoom(double outerWidth, int zoom) => zoom != 0 && !ZoomFits(outerWidth, zoom) ? 0 : Math.Clamp(zoom, 0, Zooms.Count - 1);

    /// <summary>
    /// 「放大画布」按钮不能点的原因（样稿 progPanel 的 reason）：已是最大 / 画布太窄；能点为 null。「缩小画布」见 <see cref="ZoomOutReason"/>。
    /// </summary>
    public static string? ZoomInReason(double outerWidth, int zoom) =>
        zoom >= Zooms.Count - 1 ? "已是最大 130 %" : !ZoomFits(outerWidth, zoom + 1) ? "画布太窄 · 放大后一行放不下一张步骤卡" : null;

    /// <summary>「缩小画布」不能点的原因：已是 100 %；能点为 null。</summary>
    public static string? ZoomOutReason(int zoom) => zoom <= 0 ? "已是 100 %（字号不再缩小）" : null;

    /// <summary>
    /// 每行卡片数与卡片宽（样稿 flowLayout(zoomedW(w, z), n)）：<paramref name="outerWidth"/> = 滚动区外宽（含滚动条，始终给滚动条留 8 px，出现 / 消失时不重排）；
    /// <paramref name="steps"/> = 步骤数；<paramref name="zoom"/> = 放大档（放不下时按 <see cref="ClampZoom"/> 回到 0）。
    /// </summary>
    public static StepFlowFit Fit(double outerWidth, int steps, int zoom = 0)
    {
        int z = ClampZoom(outerWidth, zoom);
        double w = ZoomedWidth(outerWidth, z);
        double avail = w - 2 * PadX - ScrollW;
        int per = (int)Math.Max(1, Math.Floor((avail - CondOff + ArrowW) / (StepMin + ArrowW)));
        per = Math.Min(per, Math.Max(1, steps));
        int rows = (int)Math.Ceiling(Math.Max(1, steps) / (double)per);
        per = Math.Max(1, (int)Math.Ceiling(steps / (double)rows));
        double stepW = per == 1
            ? Math.Clamp(avail - CondOff, StepMin, StepOneMax)
            : Math.Clamp(Math.Floor((avail - CondOff - (per - 1) * ArrowW) / per), StepMin, StepMax);
        return new StepFlowFit(per, stepW, z, ScaleOf(z), w);
    }

    /// <summary>
    /// 排位置。<paramref name="cardHeights"/> = 每一步的卡片高度（界面按 <see cref="StepFlowFit.StepW"/> 量；少给的按最后一个）；
    /// <paramref name="tailHeight"/> = 尾部（安全联锁框，含上方 26 内边距与 1 px 线）的高度，没有尾部（程序库预览）为 null；
    /// <paramref name="viewportWidth"/> = 滚动区的可见宽度（clientWidth，水平居中用）；<paramref name="preview"/> = 程序库的步骤预览（上内边距 12）。
    /// </summary>
    public static StepFlowGeometry Arrange(StepFlowFit fit, IReadOnlyList<double> cardHeights, double? tailHeight, double viewportWidth, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(cardHeights);
        int n = cardHeights.Count, per = Math.Max(1, fit.Per);
        double stepW = fit.StepW;
        double rowW = per * stepW + (per - 1) * ArrowW + CondOff;
        var items = new List<StepFlowItem>(n);
        var rows = new List<FlowRect>();
        var arrows = new List<FlowRect>();
        var dividers = new List<FlowRect>();
        var vlinks = new List<FlowRect>();
        double y = preview ? PadTopPreview : PadTop;
        int rowCount = (n + per - 1) / per;
        for (int r = 0; r < rowCount; r++)
        {
            int i0 = r * per, i1 = Math.Min(n, i0 + per);
            double rowH = 0;
            for (int i = i0; i < i1; i++) rowH = Math.Max(rowH, cardHeights[i] + ColGap + CondH);
            for (int i = i0; i < i1; i++)
            {
                int j = i - i0;
                double x = PadX + j * (stepW + ArrowW);
                double ch = cardHeights[i];
                items.Add(new StepFlowItem(i, r, j, new FlowRect(x, y, stepW, ch + ColGap + CondH), new FlowRect(x, y, stepW, ch),
                    new FlowRect(x + CondOff, y + ch + ColGap, stepW, CondH)));
                if (j > 0) arrows.Add(new FlowRect(x - ArrowW, y + rowH - ArrowH, ArrowW, ArrowH));
            }
            rows.Add(new FlowRect(PadX, y, rowW, rowH));
            y += rowH;
            if (r == rowCount - 1) break;
            if (per == 1) { vlinks.Add(new FlowRect(PadX, y, rowW, VLinkH)); y += VLinkH; }
            else { dividers.Add(new FlowRect(PadX, y + DividerTop, rowW, DividerH)); y += DividerTop + DividerH + DividerBottom; }
        }
        FlowRect? tail = null;
        if (tailHeight is double th)
        {
            y += TailTop;
            tail = new FlowRect(PadX, y, rowW, th);
            y += th;
        }
        y += PadBottom;
        return new StepFlowGeometry(fit, preview, PadX + rowW + PadX, y, items.AsReadOnly(), rows.AsReadOnly(), arrows.AsReadOnly(),
            dividers.AsReadOnly(), vlinks.AsReadOnly(), tail, viewportWidth);
    }

    /// <summary>
    /// 自动滚动 / 「定位当前步」（样稿 autoScroll）：第 <paramref name="index"/> 步（当前步或中止的那一步）的整列不在可见范围
    /// （<paramref name="scrollTop"/> … + <paramref name="viewportHeight"/>）时滚到它：卡片上方留 16 × 倍数；整列只比可见区高一点（≤ 28 × 倍数）时让条件框整个露出来。
    /// <paramref name="force"/>（定位 / 放大 / 适应）时总是滚。结果夹在 [0, 内容高 − 可见高] 里（同浏览器）。水平：左边不在可见范围（或 force）时滚到「左边 − 16」。
    /// <paramref name="scrollLeft"/> / <paramref name="viewportWidth"/> 用于水平判断（画布一般不横向滚动）。
    /// </summary>
    public static FlowScroll ScrollTo(StepFlowGeometry g, int index, double viewportHeight, double scrollTop, bool force, double scrollLeft = 0, double? viewportWidth = null)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (index < 0 || index >= g.Items.Count) return force ? new FlowScroll(0, null) : default;
        double z = g.Scale;
        var col = g.Items[index].Col;
        double top = col.Y * z, bottom = top + col.H * z;
        if (!force && top >= scrollTop && bottom <= scrollTop + viewportHeight) return default;
        double over = bottom - top + ScrollMargin * z - viewportHeight;
        double t = over > 0 && over <= ScrollSlack * z ? top - ScrollMargin * z + over : top - ScrollMargin * z;
        double maxTop = Math.Max(0, g.ContentHeight - viewportHeight);
        t = Math.Clamp(t, 0, maxTop);
        double vw = viewportWidth ?? g.ViewportWidth;
        double left = g.OffsetX + col.X * z;
        double? l = null;
        if (force || left < scrollLeft || left + col.W * z > scrollLeft + vw)
            l = Math.Clamp(left - 16, 0, Math.Max(0, Math.Max(g.ContentWidth, vw) - vw));
        return new FlowScroll(t, l);
    }

    /// <summary>
    /// 自动滚动的键（样稿 scrollKey）：设备 / 反应釜 / 程序 / 当前步 / 状态 / 每行张数 / 放大档都没变时不再自动滚（不和用户的滚动抢）。
    /// </summary>
    public static string ScrollKey(int k, int n, string? programId, int stepIndex, string status, int per, int zoom) =>
        k + "/" + n + "/" + (programId ?? "") + "/" + stepIndex + "/" + status + "/" + per + "/" + zoom;

    /// <summary>
    /// 条件框的文字取舍（样稿 condHTML）：放不下时先把「结束条件」写成「条件」，再把条件值 18 → 16 px，最后只留条件值 —— 读数始终完整。
    /// 放得下 = （标签宽 + 10）+ 值宽（600）+ 40 ≤ 卡宽（框内宽 = 卡宽 − 内边距 28 − 边框 2，另留 8）。
    /// <paramref name="measure"/>(文字, 字号, 字重) = 文字宽度（界面用同一字体量；数字按等宽）。返回（显示的标签（可能为空）, 值用 16 px）。
    /// </summary>
    public static (string Label, bool Small) CondFit(string label, string value, double stepW, Func<string, double, int, double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        bool Fits(string lb, double fs) => (lb.Length > 0 ? measure(lb, 14, 400) + 10 : 0) + measure(value ?? "", fs, 600) + 40 <= stepW;
        string l = label ?? "";
        bool small = false;
        if (!Fits(l, 18) && l == "结束条件") l = "条件";
        if (!Fits(l, 18)) small = true;
        if (small && !Fits(l, 16)) l = "";
        return (l, small);
    }

    /// <summary>
    /// 组件陈列「步骤卡片」分区的每行张数（样稿 placeholder.js flowPer）：可用宽 = 宽 − 2 × 24 − 2 × 16 − 2 − 2 − 8，每张 248 + 箭头 56，1–4 张（不超过步骤数），再把各行均衡。
    /// </summary>
    public static int GalleryPerRow(double width, int steps)
    {
        double avail = width - 2 * 24 - 2 * 16 - 2 - 2 - 8;
        int per = (int)Math.Clamp(Math.Floor(avail / (StepDefault + ArrowW)), 1, Math.Max(1, Math.Min(4, steps)));
        return (int)Math.Ceiling(steps / Math.Ceiling(steps / (double)per));
    }
}
