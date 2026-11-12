using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Vessel;

namespace ParallelReactor.Control.Controls.Vessel;

/// <summary>
/// 标注框的样式（样稿 js/pages/reactor.js stageLayout 的 MODES）：完整（标签行 + 读数 + 说明 + 目标 / 上限）→ 基本（不含附加行）→
/// 紧凑（标签行 + 读数，说明进悬停提示）→ 一行式（标签 + 读数）。
/// </summary>
public enum CalloutMode { Full, Base, Compact, Tight }

/// <summary>标注框在釜体的哪一侧：左列（SV / HT / TT，内容靠右）、右列（M / PT / LV，内容靠左）。</summary>
public enum CalloutSide { Left, Right }

/// <summary>
/// 一个标注框的内容（样稿 coView 的一项）：tag / label / 读数 / 单位 / 说明（+ 说明前的单色图标）/ 附加行（目标、上限）。
/// <see cref="Main"/> = 主读数（温度、压力：26 px，其余 24）；<see cref="Live"/> = 实时测量值（靠釜体一侧 4 px g-edge）；
/// <see cref="Err"/> = 超过上限（框不变红：附加行「上限 …」用 r-ink、去掉实时边）；<see cref="Touch"/> = 只能在触屏操作（手形图标）；
/// <see cref="HandVal"/> = 手形图标放在读数旁（说明行写了耗气量）；<see cref="TouchTip"/> = 手形图标的悬停说明；<see cref="Tip"/> = 整个标注的悬停说明。
/// 文字都是显示用的原文（读数已格式化，没有读数时「—」）。不可变：数据变了整个换掉（标注框按值比较，没变就不重排）。
/// </summary>
public sealed record CalloutContent(
    string Tag, string Label, string Value, string Unit, string Sub,
    string? Icon = null, string Extra = "", bool Touch = false, bool HandVal = false,
    bool Main = false, bool Live = false, bool Err = false, string? TouchTip = null, string? Tip = null)
{
    /// <summary>没有读数（「—」：t-dis 400，不带单位）。</summary>
    public bool Na => Value == Fmt.Dash;

    /// <summary>单位行有非 ASCII 字（「气体 A」「H₂」；℃ 除外）：14 px、左边多 2 px（样稿 cjkUnit / small.is-cjk）。</summary>
    public bool CjkUnit => IsCjkUnit(Unit);

    public static bool IsCjkUnit(string? u)
    {
        if (string.IsNullOrEmpty(u)) return false;
        foreach (var ch in u) if (ch > 0x7f && ch != '℃') return true;
        return false;
    }

    /// <summary>单位写出来（有读数且有单位）。</summary>
    public bool ShowsUnit => Unit.Length > 0 && !Na;

    /// <summary>手形图标在读数行（一行式 / 紧凑 / 说明行写了耗气量；样稿 handIn）。</summary>
    public bool HandInValueRow(CalloutMode mode) => Touch && (mode is CalloutMode.Tight or CalloutMode.Compact || HandVal);

    /// <summary>手形图标的悬停说明（样稿 handTip = touchTip || sub）。</summary>
    public string HandTip => string.IsNullOrEmpty(TouchTip) ? Sub : TouchTip!;

    /// <summary>
    /// 标注框的悬停提示（样稿 coHTML）：一行式 =「名称 读数 单位 · 说明 · 附加」；紧凑 =「名称 · 说明 · 附加」；完整 / 基本没有整框的提示
    /// （说明行自己带提示）—— 返回 null。
    /// </summary>
    public string? BoxTip(CalloutMode mode)
    {
        var tip = string.IsNullOrEmpty(Tip) ? Sub : Tip!;
        var extra = Extra.Length > 0 ? " · " + Extra : "";
        return mode switch
        {
            CalloutMode.Tight => Label + " " + Value + (ShowsUnit ? " " + Unit : "") + " · " + tip + extra,
            CalloutMode.Compact => Label + " · " + tip + extra,
            _ => null,
        };
    }

    /// <summary>说明行的悬停提示（样稿 .reactor-co-sub 的 title = tip || sub）。</summary>
    public string SubTip => string.IsNullOrEmpty(Tip) ? Sub : Tip!;
}

/// <summary>量字宽：（文字, 字号 px, 是否 600）→ 前进宽度（px）。样稿 ui.textWidth：数字按「0」量（页面数字等宽）——<see cref="StageLayout"/> 自己替换好再问。</summary>
public delegate double TextWidth(string text, double size, bool semiBold);

/// <summary>一种标注样式的框参数（样稿 BOX）：上下内边距、面板内边距、框间距、引线连到框顶下面多少（conn）。</summary>
public readonly record struct CalloutBoxSpec(double Py, double Pad, double Gap, double Conn);

/// <summary>一个标注框的位置（设计 px，相对釜体面板内容区左上角）。</summary>
public readonly record struct StageBox(VesselCallout Key, CalloutSide Side, double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>一个点（设计 px）。</summary>
public readonly record struct StagePoint(double X, double Y);

/// <summary>
/// 一条引线（样稿 leads）：折线的点（锚点 → 横 → 竖 → 横到框边；锚点与框的连接点同高时只有一段横线）+ 锚点上的小圆点。
/// </summary>
public sealed record StageLead(VesselCallout Key, IReadOnlyList<StagePoint> Points, StagePoint Dot);

/// <summary>
/// 釜体面板的版面（样稿 stageLayout 的返回值）：标注样式、读数字号（主 / 次）、釜体的框（<see cref="Vx"/> / <see cref="Vy"/> / <see cref="Vw"/> / <see cref="Vh"/>，
/// 150 : 230 等比）、六个标注框、六条引线。空版面（面板太小）<see cref="Empty"/> = true。
/// </summary>
public sealed record StageResult(
    bool Empty, CalloutMode Mode, double FsMain, double FsSub,
    double Vx, double Vy, double Vw, double Vh,
    IReadOnlyList<StageBox> Boxes, IReadOnlyList<StageLead> Leads)
{
    public static readonly StageResult None = new(true, CalloutMode.Tight, 24, 22, 0, 0, 0, 0, Array.Empty<StageBox>(), Array.Empty<StageLead>());

    /// <summary>viewBox → 釜体框的缩放（vh / 230）。</summary>
    public double K => Vh / VesselAnchors.ViewH;

    public StageBox? Box(VesselCallout key)
    {
        foreach (var b in Boxes) if (b.Key == key) return b;
        return null;
    }
}

/// <summary>
/// 反应釜页「釜体」页签的版面计算（样稿 js/pages/reactor.js 的 coH / coInner / stageLayout，数值逐个照抄）：纯函数，不碰界面（App.Tests 直接测）。
/// <list type="bullet">
/// <item>标注样式按面板高度<b>和宽度</b>选（第 3 轮）：完整 → 基本 → 紧凑 → 一行式，取第一个高度放得下、且两列标注之间还给釜体留
///   ≥ <see cref="VesselAnchors.VesselMinWidth"/>（96 px）的；都留不出来时取高度放得下的样式里釜体最宽的那个 —— 窄面板里标注框不会把釜体压小再盖住它。</item>
/// <item>框宽 = 内容（按实际字体量）+ 内边距 8 + 12（釜体一侧多 4 px 给实时边）+ 边框 2，按 4 px 取整（读数跳动时框不抖）。</item>
/// <item>釜体高 = min(面板高 − 2 × 内边距, 两列之间可用宽 × 230 / 150, 520)，至少 90；整组（左列 · 引线 · 釜体 · 引线 · 右列）水平居中，引线 14–56 px。</item>
/// <item>标注按锚点高度排序、向下避让、再从底往上夹进面板；引线从锚点横出、竖到框的连接点、再横到框边（同高时一段横线）。</item>
/// </list>
/// 所有阈值只看父元素给的面板尺寸（R4）。字宽由调用方给（App：UiFont 的 TextLayout；测试：假的量法），数字按「0」量（同样稿 ui.textWidth）。
/// </summary>
public static class StageLayout
{
    /// <summary>引线最短（样稿 minLead）。</summary>
    public const double MinLead = 14, MaxLead = 56;

    /// <summary>行高（样稿 ROW）：标签行 20、读数 30、说明 20、一行式 28。</summary>
    public const double RowHead = 20, RowVal = 30, RowSub = 20, RowTight = 28;

    /// <summary>面板太小（宽或高 &lt; 120）时不画（样稿 stageHTML）。</summary>
    public const double MinStage = 120;

    /// <summary>样式的试用顺序。</summary>
    public static readonly IReadOnlyList<CalloutMode> Modes = new[] { CalloutMode.Full, CalloutMode.Base, CalloutMode.Compact, CalloutMode.Tight };

    /// <summary>左列（进气阀、加热、温度）与右列（搅拌、压力、液位），样稿 SIDES。</summary>
    public static readonly IReadOnlyList<VesselCallout> LeftKeys = new[] { VesselCallout.Sv, VesselCallout.Ht, VesselCallout.Tt };
    public static readonly IReadOnlyList<VesselCallout> RightKeys = new[] { VesselCallout.M, VesselCallout.Pt, VesselCallout.Lv };

    public static CalloutSide SideOf(VesselCallout key) => key is VesselCallout.Sv or VesselCallout.Ht or VesselCallout.Tt ? CalloutSide.Left : CalloutSide.Right;

    /// <summary>样稿 BOX：full / base { py 7, pad 10, gap 8, conn 43 }、compact { 5, 6, 6, 41 }、tight { 4, 6, 6, 19 }。</summary>
    public static CalloutBoxSpec Box(CalloutMode mode) => mode switch
    {
        CalloutMode.Compact => new(5, 6, 6, 41),
        CalloutMode.Tight => new(4, 6, 6, 19),
        _ => new(7, 10, 8, 43),
    };

    /// <summary>读数字号（样稿 cols：主读数 26 / 其余 24；一行式 24 / 22）。</summary>
    public static (double Main, double Sub) FontSizes(CalloutMode mode) => mode == CalloutMode.Tight ? (24, 22) : (26, 24);

    /// <summary>标注框高（含 1 px 边框，样稿 coH）。</summary>
    public static double Height(CalloutContent v, CalloutMode mode)
    {
        var b = Box(mode);
        return mode switch
        {
            CalloutMode.Tight => 2 + 2 * b.Py + RowTight,
            CalloutMode.Compact => 2 + 2 * b.Py + RowHead + RowVal,
            _ => 2 + 2 * b.Py + RowHead + RowVal + RowSub + (mode == CalloutMode.Full && v.Extra.Length > 0 ? RowSub : 0),
        };
    }

    /// <summary>读数行的间距（样稿 .reactor-co-val gap：3，一行式 5）。</summary>
    public static double ValueGap(CalloutMode mode) => mode == CalloutMode.Tight ? 5 : 3;

    /// <summary>样稿 ui.textWidth 的数字规则：数字一律按「0」量（页面数字等宽，读数跳动时预留的宽度也够）。</summary>
    public static string Digits0(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool any = false;
        foreach (var ch in s) if (ch is >= '0' and <= '9') { any = true; break; }
        if (!any) return s;
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++) if (a[i] is >= '0' and <= '9') a[i] = '0';
        return new string(a);
    }

    private static double W(TextWidth tw, string text, double size, bool bold = false) => text.Length == 0 ? 0 : tw(Digits0(text), size, bold);

    /// <summary>
    /// 标注框内容宽度（样稿 coInner，与 reactor.css 的间距一致）：标签行 / 读数行 / 说明行取最宽，另留 2 px 余量。
    /// <paramref name="vfs"/> = 读数字号（主 / 次读数各自的）。
    /// </summary>
    public static double Inner(CalloutContent v, CalloutMode mode, double vfs, TextWidth tw)
    {
        bool na = v.Na;
        bool tight = mode == CalloutMode.Tight;
        double gap = ValueGap(mode);
        double tagW = W(tw, v.Tag, 14, true) + 12;                                   // 14 px 600 + 内边距 10 + 边框 2
        bool cjk = v.CjkUnit;
        double unitW = v.Unit.Length > 0 && !na ? gap + W(tw, v.Unit, cjk ? 14 : 13) + (cjk ? 2 : 0) : 0;
        double handW = v.Touch && (tight || mode == CalloutMode.Compact || v.HandVal) ? gap + 19 : 0;   // 手形图标 15 + 左右 2
        double valW = W(tw, v.Value, vfs, !na) + unitW + handW;
        if (tight) return Math.Ceiling(valW + gap + tagW + 2);
        double w = Math.Max(tagW + 6 + W(tw, v.Label, 14), valW);
        if (mode != CalloutMode.Compact) w = Math.Max(w, (v.Icon != null ? 18 : 0) + W(tw, v.Sub, 14));
        if (mode == CalloutMode.Full && v.Extra.Length > 0) w = Math.Max(w, W(tw, v.Extra, 14, true));
        return Math.Ceiling(w + 2);
    }

    /// <summary>一列标注的框宽（样稿 cols.colW：内容最宽的那个 + 22，按 4 px 向上取整）。</summary>
    public static double ColumnWidth(IReadOnlyDictionary<VesselCallout, CalloutContent> v, IReadOnlyList<VesselCallout> keys, CalloutMode mode, TextWidth tw)
    {
        var (fsMain, fsSub) = FontSizes(mode);
        double max = 0;
        foreach (var k in keys)
        {
            var c = v[k];
            max = Math.Max(max, Inner(c, mode, c.Main ? fsMain : fsSub, tw));
        }
        return Math.Ceiling((max + 22) / 4) * 4;
    }

    /// <summary>一种样式需要的面板高度（样稿 need：较高的一列 = 框高之和 + 2 × 框间距 + 2 × 内边距）。</summary>
    public static double Need(IReadOnlyDictionary<VesselCallout, CalloutContent> v, CalloutMode mode)
    {
        var b = Box(mode);
        double side(IReadOnlyList<VesselCallout> keys) => keys.Sum(k => Height(v[k], mode)) + 2 * b.Gap + 2 * b.Pad;
        return Math.Max(side(LeftKeys), side(RightKeys));
    }

    /// <summary>两列标注之间留给釜体（含两条最短引线之外）的宽度（样稿 cols.room）。</summary>
    public static double Room(double width, double wL, double wR, CalloutMode mode) => width - 2 * Math.Max(Box(mode).Pad, 10) - wL - wR - 2 * MinLead;

    /// <summary>
    /// 选样式（样稿 stageLayout 前半）：高度放得下的样式里，第一个两列之间还能给釜体留 ≥ 96 px 的；都不行时取釜体最宽的那个；高度都放不下时一行式。
    /// </summary>
    public static CalloutMode PickMode(double width, double height, IReadOnlyDictionary<VesselCallout, CalloutContent> v, TextWidth tw)
    {
        var fit = Modes.Where(md => Need(v, md) <= height).ToList();
        double room(CalloutMode md) => Room(width, ColumnWidth(v, LeftKeys, md, tw), ColumnWidth(v, RightKeys, md, tw), md);
        foreach (var md in fit)
            if (room(md) >= VesselAnchors.VesselMinWidth) return md;
        if (fit.Count == 0) return CalloutMode.Tight;
        var best = fit[0];
        for (int i = 1; i < fit.Count; i++) if (room(fit[i]) > room(best)) best = fit[i];
        return best;
    }

    /// <summary>
    /// 整个版面（样稿 stageLayout）。<paramref name="width"/> / <paramref name="height"/> = 釜体面板内容区（父元素给的尺寸）；
    /// <paramref name="levelY"/> = 液面的 y（viewBox 单位，LV 引线锚点跟着液面走）。面板太小（&lt; 120）返回 <see cref="StageResult.None"/>。
    /// </summary>
    public static StageResult Compute(double width, double height, IReadOnlyDictionary<VesselCallout, CalloutContent> v, double levelY, TextWidth tw)
    {
        if (!(width >= MinStage) || !(height >= MinStage) || double.IsInfinity(width) || double.IsInfinity(height)) return StageResult.None;
        var mode = PickMode(width, height, v, tw);
        var B = Box(mode);
        var (fsMain, fsSub) = FontSizes(mode);
        double wL = ColumnWidth(v, LeftKeys, mode, tw), wR = ColumnWidth(v, RightKeys, mode, tw);
        double pad = Math.Max(B.Pad, 10);
        double maxVw = Math.Max(64, width - 2 * pad - wL - wR - 2 * MinLead);
        double vh = Math.Max(90, Math.Min(Math.Min(height - 2 * B.Pad, maxVw * VesselAnchors.ViewH / VesselAnchors.ViewW), 520));
        double vw = vh * VesselAnchors.ViewW / VesselAnchors.ViewH, k = vh / VesselAnchors.ViewH;
        double lead = Math.Clamp((width - 2 * pad - wL - wR - vw) / 2, MinLead, MaxLead);
        double x0 = Math.Max(pad, JsNumber.Round((width - (wL + lead + vw + lead + wR)) / 2));
        double xL = x0, vx = JsNumber.Round(x0 + wL + lead), xR = Math.Min(width - pad - wR, JsNumber.Round(vx + vw + lead));
        double vy = JsNumber.Round((height - vh) / 2);

        var boxes = new List<StageBox>(6);
        var leads = new List<StageLead>(6);
        foreach (var side in new[] { CalloutSide.Left, CalloutSide.Right })
        {
            var keys = side == CalloutSide.Left ? LeftKeys : RightKeys;
            double w = side == CalloutSide.Left ? wL : wR;
            // 按锚点高度排序（稳定排序：同高时保持 SIDES 的顺序，同 JS Array.prototype.sort）
            var items = keys.Select((key, i) =>
            {
                var a = VesselAnchors.Of(key, levelY);
                return (Key: key, Order: i, H: Height(v[key], mode), Ax: vx + a.X * k, Ay: vy + a.Y * k, Top: 0.0);
            }).OrderBy(it => it.Ay).ThenBy(it => it.Order).ToArray();
            double y = B.Pad;
            for (int j = 0; j < items.Length; j++)
            {
                items[j].Top = Math.Max(items[j].Ay - B.Conn, y);
                y = items[j].Top + items[j].H + B.Gap;
            }
            double lo = height - B.Pad;
            for (int j = items.Length - 1; j >= 0; j--)
            {
                items[j].Top = Math.Min(items[j].Top, lo - items[j].H);
                lo = items[j].Top - B.Gap;
            }
            for (int idx = 0; idx < items.Length; idx++)
            {
                var it = items[idx];
                double top = JsNumber.Round(Math.Max(B.Pad, it.Top));
                double left = side == CalloutSide.Left ? xL : xR;
                boxes.Add(new StageBox(it.Key, side, left, top, w, it.H));
                double cy = top + B.Conn + 0.5;
                double edge = side == CalloutSide.Left ? left + w : left;
                double ax = Fixed1(it.Ax), ay = Fixed1(it.Ay);
                double off = Math.Min(10 + idx * 6, Math.Abs(ax - edge) / 2);
                double ex = side == CalloutSide.Left ? edge + off : edge - off;
                bool straight = Math.Abs(cy - ay) < 1.5;
                var pts = straight
                    ? new[] { new StagePoint(ax, cy), new StagePoint(edge, cy) }
                    : new[] { new StagePoint(ax, ay), new StagePoint(Fixed1(ex), ay), new StagePoint(Fixed1(ex), cy), new StagePoint(edge, cy) };
                leads.Add(new StageLead(it.Key, pts, new StagePoint(ax, straight ? cy : ay)));
            }
        }
        // 输出顺序同样稿：左列（SV / HT / TT）再右列（M / PT / LV）
        boxes.Sort((a, b) => Order(a.Key).CompareTo(Order(b.Key)));
        return new StageResult(false, mode, fsMain, fsSub, vx, vy, vw, vh, boxes, leads);
    }

    private static int Order(VesselCallout k) => (int)k;

    /// <summary>JS 的 +x.toFixed(1)。</summary>
    private static double Fixed1(double x) => double.Parse(JsNumber.ToFixed(x, 1), NumberStyles.Float, CultureInfo.InvariantCulture);
}
