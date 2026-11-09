using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Layout;

/// <summary>
/// 总览一个设备分区的版面（样稿 overview.js bodyHTML / panelHTML + css/overview.css）。
/// <see cref="Tracks"/> / <see cref="Tiles"/>：卡片网格（紧凑卡固定 8 列、通道号与列一一对应，未安装的一段跨它自己的列数；其余每张卡一列、未安装合并成 96 px 窄列）；
/// <see cref="CardWidth"/> / <see cref="CardHeight"/>：卡片外框（同一分区的卡片一样大）；<see cref="CardContentWidth"/>：卡片内容宽（容器 ovc，卡片里的取舍都按它）；
/// <see cref="Height"/>：整个分区（标题栏 42 + 说明条 + 网格 + 底边 1，最后一个分区没有底边）。从未连上的设备没有网格，只有一条说明（<see cref="NeverConnected"/>）。
/// </summary>
public sealed record OverviewSection(
    CardKind Kind,
    bool ByEight,
    int Columns,
    int Rows,
    IReadOnlyList<GridTrack> Tracks,
    IReadOnlyList<GridTile> Tiles,
    double CardWidth,
    double CardContentWidth,
    double CardHeight,
    double GridHeight,
    bool Note,
    bool NeverConnected,
    bool Last,
    double Height,
    OverviewCard Card);

/// <summary>
/// 总览卡片按自身内容宽（容器 ovc）的取舍（css/overview.css:157-220）。
/// 釜体（宽 × 高，0 = 不画）、釜体与读数的间距、目标 / 目标单位、程序名、紧凑卡的标题换行（状态字到第二行）与程序行换行（进度条与文字分两行）、
/// PR4 宽卡第三行「搅拌 · 加热」的收紧。读数字号见 <see cref="Temp"/> / <see cref="Pressure"/>（只有长读数才缩，data-len 阶梯）。
/// 目标「放得下才显示」（fit-or-hide：整个让到看不见的第二行）由界面自己量；这里的 <see cref="Target"/> 只是按宽度整个不显示的那几档。
/// </summary>
public sealed record OverviewCard(
    CardKind Kind,
    double ContentWidth,
    double VesselWidth,
    double VesselHeight,
    double BodyGap,
    bool Target,
    bool TargetUnit,
    bool ProgramName,
    bool HeadWrap,
    bool ProgramWrap)
{
    /// <summary>画釜体。</summary>
    public bool Vessel => VesselWidth > 0;

    /// <summary>
    /// 温度读数（<paramref name="len"/> = 读数字符数，'199.9' → 5）：一排 / 宽卡 26 px（行高 32），紧凑卡 24（28）；紧凑卡的 5 位读数按宽度逐级缩小、最后去单位
    /// （css/overview.css:203-208：卡片 ≥ 120 px 时画釜体，读数列 = 宽 − 34）。
    /// </summary>
    public ReadingFont Temp(int len)
    {
        double w = ContentWidth;
        if (Kind != CardKind.Compact) return new ReadingFont(26, 32);
        double size = 24;
        bool unit = true;
        if (len == 5)
        {
            // 同一特异性、按源顺序：后面（更窄）的一档覆盖前面的
            if (w <= 94.9) size = 22;                       // overview.css:204
            if (w <= 89) size = 20;                         // :205
            if (w <= 83.4) unit = false;                    // :206
            if (w >= 120 && w <= 128.9) size = 22;          // :207
            if (w >= 120 && w <= 123) size = 20;            // :208
        }
        return new ReadingFont(size, 28, unit);
    }

    /// <summary>压力读数：一排 / 宽卡 20 px（行高 26），紧凑卡 18（22）；紧凑卡 5 位读数（「2.997」）18 → 17 → 16 → 去单位（css/overview.css:209-213）。</summary>
    public ReadingFont Pressure(int len)
    {
        double w = ContentWidth;
        if (Kind != CardKind.Compact) return new ReadingFont(20, 26);
        double size = 18;
        bool unit = true;
        if (len == 5)
        {
            if (w <= 88.2) size = 17;                       // overview.css:210
            if (w <= 85.4) size = 16;                       // :211
            if (w <= 83) unit = false;                      // :212
            if (w >= 120 && w <= 122.2) size = 17;          // :213
        }
        return new ReadingFont(size, 22, unit);
    }

    /// <summary>
    /// PR4 宽卡第三行「搅拌 1500 rpm · 加热 100 %」（只有宽卡有）：<paramref name="len"/> = 两个数字的总位数（overview.js auxHTML data-len）。
    /// 默认数字 18 px、两项间距 16、标签右边距 2；卡片 ≤ 214 px 或 7 位（4 位转速 + 3 位加热）且 ≤ 271 px 时 16 px / 10 / 0；7 位且 ≤ 199 px 时再去掉「rpm」。
    /// </summary>
    public OverviewAux Aux(int len)
    {
        double w = ContentWidth;
        bool tight = w <= 214 || (len == 7 && w <= 271);    // overview.css:163-167、215-219
        bool rpmUnit = !(len == 7 && w <= 199);              // :220
        return tight ? new OverviewAux(16, 10, 0, rpmUnit) : new OverviewAux(18, 16, 2, rpmUnit);
    }
}

/// <summary>宽卡第三行的字号与间距：数字字号、两项之间的间距、标签与数字之间多出的右边距、第一项（转速）的单位是否显示。</summary>
public readonly record struct OverviewAux(double ValueFont, double Gap, double LabelMargin, bool RpmUnit);

/// <summary>
/// 整个总览页（overview.js paint）：各分区、所有分区的总高、要不要顶部报警条（内容超出一屏且有未确认的错误报警）、
/// 要不要温度趋势（没有报警条且下方剩余 ≥ 200 px；M5 才有曲线控件，现在只给判断）、滚动区的高度（报警条在滚动区外面）、窄工作区按优先级去掉的分区头内容。
/// 有温度趋势时最后一个分区下面还有趋势面板，它的底边回来（<see cref="Sections"/> 里已改好，<see cref="Used"/> 也加上了）。
/// </summary>
public sealed record OverviewPage(
    double Width,
    double Height,
    IReadOnlyList<OverviewSection> Sections,
    double Used,
    bool AlarmStrip,
    bool Trend,
    double ScrollHeight,
    bool HideSession,
    bool HideModel);

/// <summary>
/// 总览的布局计算（PORT-PLAN §4.8 第 2 层、§5 M4「布局」）：样稿 overview.js（cardKind / bodyHTML / paint）与 css/overview.css 的数字规则，纯函数。
/// 输入：总览根元素的宽 / 高（= 工作区；只看父元素给出的尺寸）、每台设备的型号（通道数、装机）与状态（连上过、要不要说明条）。
/// <para>与样稿一致的几处（真机会碰到、演示数据碰不到的版面也照样稿的规则推）：</para>
/// <list type="bullet">
/// <item>不连续装机（如 RV1–2、5–6）：一排 / 宽卡按 DOM 顺序给列（卡片 1fr、每段未安装 96 px），同样稿 bodyHTML 的 cols 列表。</item>
/// <item>紧凑卡：未安装段不跨过 8 列的边界（样稿 bodyHTML 的 by8 循环条件）。</item>
/// </list>
/// </summary>
public static class OverviewLayout
{
    // ———— 尺寸常量（设计 px） ————
    /// <summary>面板标题栏（base.css:84 --head-h；分区头同）。</summary>
    public const double Head = 42;
    /// <summary>网格左右内边距 16、上下 8、卡片间距 10（overview.css:71）。</summary>
    public const double GridPadX = 16, GridPadY = 8, Gap = 10;
    /// <summary>未安装窄列（overview.css:9 --untile-w）。</summary>
    public const double UntileWidth = 96;
    /// <summary>说明条（断线 / 急停 / 首次连接）：高 44，上边距 12；从未连上时下边距也是 12（overview.css:56-68）。</summary>
    public const double NoteHeight = 44, NoteMargin = 12;
    /// <summary>分区底边 1 px（最后一个没有，overview.css:17-18）。</summary>
    public const double SectionBorder = 1;
    /// <summary>顶部报警条高（overview.css:224）。</summary>
    public const double AlarmStripHeight = 46;
    /// <summary>温度趋势至少要剩下的高度（overview.js:267 TREND_MIN）。</summary>
    public const double TrendMin = 200;
    /// <summary>工作区 ≤ 900 时分区头不显示「记录中」，≤ 820 时不显示型号（overview.css:261-266）。</summary>
    public const double HideSessionMax = 900, HideModelMax = 820;
    /// <summary>紧凑卡固定 8 列（overview.js:186）。</summary>
    public const int CompactColumns = 8;

    /// <summary>
    /// 卡片种类（overview.js:33 cardKind）：型号 wide4 → 宽卡；grid16 → 紧凑卡；row8 → 已安装 ≤ 4 路用一排卡，更多用紧凑卡。
    /// </summary>
    public static CardKind CardKindOf(ModelProfile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Layout switch
        {
            LayoutKind.Wide4 => CardKind.Wide,
            LayoutKind.Grid16 => CardKind.Compact,
            _ => model.Installed.Count <= 4 ? CardKind.Row : CardKind.Compact,
        };
    }

    /// <summary>
    /// 一个设备分区。<paramref name="width"/> = 总览宽度（分区与总览同宽）；<paramref name="everOnline"/> = 连上过（false：只有一条说明，没有卡片）；
    /// <paramref name="note"/> = 分区里有一条说明（断线或急停，overview.js noteHTML）；<paramref name="last"/> = 最后一个分区（没有底边）；
    /// <paramref name="anyProgram"/> = 这个分区里有带进度条的卡片（只影响紧凑卡程序行换行时的高度：进度条 + 文字 34，只有「无程序」20）。
    /// </summary>
    public static OverviewSection Section(ModelProfile model, double width, bool everOnline = true, bool note = false, bool last = false, bool anyProgram = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        var kind = CardKindOf(model);
        double border = last ? 0 : SectionBorder;
        if (!everOnline)
        {
            // 从未连上（刚添加）：没有任何数据 —— 只放说明，不画一排空卡片（overview.js:185，.overview-note.is-only）
            var none = Card(kind, 0);
            return new OverviewSection(kind, false, 0, 0, Array.Empty<GridTrack>(), Array.Empty<GridTile>(), 0, 0, 0, 0, true, true, last,
                Head + NoteMargin + NoteHeight + NoteMargin + border, none);
        }
        bool by8 = kind == CardKind.Compact;
        var slots = model.Channels;
        // 先排出 DOM 顺序的块（卡片 / 合并的未安装段），同 overview.js:189-197
        var blocks = new List<(int From, int To, bool Installed, int Span)>();
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Installed) { blocks.Add((slots[i].N, slots[i].N, true, 1)); continue; }
            int j = i;
            while (j + 1 < slots.Count && !slots[j + 1].Installed && (!by8 || (j + 1) / CompactColumns == i / CompactColumns)) j++;
            blocks.Add((slots[i].N, slots[j].N, false, by8 ? j - i + 1 : 1));
            i = j;
        }

        double inner = width - 2 * GridPadX;
        var tracks = new List<GridTrack>();
        var tiles = new List<GridTile>();
        int cols, rows;
        double cardW;
        if (by8)
        {
            cols = CompactColumns;
            cardW = (inner - (cols - 1) * Gap) / cols;
            for (int c = 0; c < cols; c++) tracks.Add(new GridTrack(false, cardW));
            rows = 0;
            // 自动排布：每块占 Span 列，排不下就换行（与 CSS grid 的 auto-placement 相同：块从不跨过 8 的边界，所以就是按通道号对齐的列）
            int col = 0, row = 0;
            foreach (var b in blocks)
            {
                if (col + b.Span > cols) { row++; col = 0; }
                tiles.Add(new GridTile(b.From, b.To, b.Installed, col, row, b.Span, 0, 0, 0, 0));
                col += b.Span;
                rows = row + 1;
            }
        }
        else
        {
            int nCards = blocks.Count(b => b.Installed), nUn = blocks.Count - nCards;
            cols = blocks.Count;
            rows = cols > 0 ? 1 : 0;
            cardW = nCards > 0 ? (inner - (cols - 1) * Gap - nUn * UntileWidth) / nCards : 0;
            for (int c = 0; c < blocks.Count; c++)
            {
                var b = blocks[c];
                tracks.Add(new GridTrack(!b.Installed, b.Installed ? cardW : UntileWidth));
                tiles.Add(new GridTile(b.From, b.To, b.Installed, c, 0, 1, 0, 0, 0, 0));
            }
        }

        var card = Card(kind, cardW - CardChromeX(kind));
        double cardH = CardHeight(card, anyProgram);
        // 位置：列宽累加 + 间距；行高 = 卡片高（一行里的卡片一样高）
        var xs = new double[tracks.Count + 1];
        xs[0] = GridPadX;
        for (int c = 0; c < tracks.Count; c++) xs[c + 1] = xs[c] + tracks[c].Width + Gap;
        for (int t = 0; t < tiles.Count; t++)
        {
            var g = tiles[t];
            double x = xs[g.Column], w = xs[g.Column + g.Span] - Gap - x;
            tiles[t] = g with { X = x, Y = GridPadY + g.Row * (cardH + Gap), Width = w, Height = cardH };
        }
        double gridH = rows > 0 ? 2 * GridPadY + rows * cardH + (rows - 1) * Gap : 2 * GridPadY;
        double noteH = note ? NoteMargin + NoteHeight : 0;
        return new OverviewSection(kind, by8, cols, rows, tracks, tiles, cardW, card.ContentWidth, cardH, gridH, note, false, last,
            Head + noteH + gridH + border, card);
    }

    /// <summary>
    /// 卡片按内容宽（容器 ovc）的取舍（css/overview.css:157-198）。<paramref name="contentWidth"/> = 卡片外框宽 − 左右内边距 − 边框（<see cref="CardChromeX"/>）。
    /// </summary>
    public static OverviewCard Card(CardKind kind, double contentWidth)
    {
        double w = contentWidth;
        double vw, vh, gap;
        switch (kind)
        {
            case CardKind.Wide:
                // 宽卡 44 × 76（overview.css:113）；≤ 251 不画（:176-178）
                (vw, vh) = w <= 251 ? (0d, 0d) : (44d, 76d);
                gap = 12;
                break;
            case CardKind.Row:
                // 一排卡 36 × 56（:96）；≤ 199 缩成 22 × 36、间距 10（:168-172）；≤ 149 不画（:173-175）
                (vw, vh) = w <= 149 ? (0d, 0d) : w <= 199 ? (22d, 36d) : (36d, 56d);
                gap = w <= 199 ? 10 : 12;
                break;
            default:
                // 紧凑卡 26 × 44、间距 8（:121-122）；≤ 119 不画（:192-194）
                (vw, vh) = w <= 119 ? (0d, 0d) : (26d, 44d);
                gap = 8;
                break;
        }
        bool target = kind switch
        {
            CardKind.Compact => false,             // 紧凑卡的目标只在悬停提示和检查器里（:127）
            CardKind.Row => !(w <= 199),           // :169
            _ => true,
        };
        bool targetUnit = !(w <= 239);             // :159-161（先去目标的单位）
        bool name = kind switch
        {
            CardKind.Compact => false,             // :141（「无程序」照样显示，见 OverviewCard 说明）
            CardKind.Row => !(w <= 189),           // :179-182
            _ => !(w <= 214),                      // :183-186
        };
        bool headWrap = kind == CardKind.Compact && w <= 117;     // :188-191
        bool progWrap = kind == CardKind.Compact && w <= 104;     // :195-198
        return new OverviewCard(kind, w, vw, vh, gap, target, targetUnit, name, headWrap, progWrap);
    }

    /// <summary>卡片左右内边距 + 边框：一排 / 宽卡 14 + 14 + 2，紧凑卡 9 + 9 + 2（overview.css:75-84）。</summary>
    public static double CardChromeX(CardKind kind) => kind == CardKind.Compact ? 20 : 30;

    /// <summary>
    /// 卡片的自然高度（css/overview.css：内边距 + 边框 + 标题行 + 主体（釜体与读数取高者）+ 上下外边距 + 程序行）：
    /// 一排卡 144，宽卡 168，紧凑卡 120；紧凑卡窄时标题换行 +20、程序行换行（进度条 10 + 4 + 文字 20 = 34，只有「无程序」时 20）。
    /// </summary>
    public static double CardHeight(OverviewCard card, bool anyProgram = true)
    {
        ArgumentNullException.ThrowIfNull(card);
        switch (card.Kind)
        {
            case CardKind.Wide:
                // 24 + 2 + 标题 22 + 8 + max(76, 32 + 26 + 24) + 10 + 程序行 20
                return 24 + 2 + 22 + 8 + Math.Max(card.VesselHeight, 32 + 26 + 24) + 10 + 20;
            case CardKind.Row:
                return 24 + 2 + 22 + 8 + Math.Max(card.VesselHeight, 32 + 26) + 10 + 20;
            default:
                double head = card.HeadWrap ? 40 : 20;
                double prog = card.ProgramWrap ? (anyProgram ? 10 + 4 + 20 : 20) : 18;
                return 18 + 2 + head + 6 + Math.Max(card.VesselHeight, 28 + 22) + 6 + prog;
        }
    }

    /// <summary>
    /// 整页（overview.js paint）：<paramref name="hasStripItems"/> = 有未确认的错误报警（AlarmStrip.Build(...).Any，急停那条不算）。
    /// <paramref name="used"/>：各分区实际高度之和；不给时用 <see cref="OverviewSection.Height"/> 之和（界面可以传量到的实际高度，规则相同）。
    /// <paramref name="drawsTrend"/>：界面真的画温度趋势面板（M5 才有曲线控件；M4 传 false —— <see cref="OverviewPage.Trend"/> 照样给出「样稿这时会画趋势」，
    /// 但最后一个分区不因趋势面板加回底边）。
    /// </summary>
    public static OverviewPage Page(double width, double height, IReadOnlyList<OverviewSection> sections, bool hasStripItems, double? used = null, bool drawsTrend = true)
    {
        ArgumentNullException.ThrowIfNull(sections);
        double u = used ?? sections.Sum(s => s.Height);
        bool strip = ShowAlarmStrip(u, height, hasStripItems);
        bool trend = ShowTrend(u, height, sections.Count, strip);
        var list = sections;
        if (trend && drawsTrend && sections.Count > 0 && sections[^1].Last)
        {
            // 温度趋势面板在最后一个分区下面：那个分区不再是滚动区的最后一个子元素，底边回来（overview.css:17-18）
            var copy = sections.ToList();
            copy[^1] = copy[^1] with { Last = false, Height = copy[^1].Height + SectionBorder };
            list = copy;
            if (used is null) u += SectionBorder;
        }
        return new OverviewPage(width, height, list, u, strip, trend, height - (strip ? AlarmStripHeight : 0), width <= HideSessionMax, width <= HideModelMax);
    }

    /// <summary>
    /// 顶部报警条（overview.js:328）：有未确认的错误报警，且各分区的总高超出总览高度（&gt; 高度 + 1）。报警条在滚动区外面，不盖住分区。
    /// </summary>
    public static bool ShowAlarmStrip(double used, double height, bool hasStripItems) => hasStripItems && used > height + 1;

    /// <summary>温度趋势（overview.js:329）：有设备、没有报警条、下方剩余 ≥ 200 px。曲线控件在 M5，M4 只给判断。</summary>
    public static bool ShowTrend(double used, double height, int devices, bool alarmStrip) => devices > 0 && !alarmStrip && height - used >= TrendMin;

    /// <summary>
    /// 一组设备的整页：按顺序给每台算分区（最后一台没有底边），再算报警条 / 温度趋势。
    /// <paramref name="devices"/>：每台的型号、连上过、要不要说明条（断线或急停）。
    /// </summary>
    public static OverviewPage Compute(double width, double height, IReadOnlyList<(ModelProfile Model, bool EverOnline, bool Note)> devices, bool hasStripItems, bool drawsTrend = true)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var sections = new List<OverviewSection>(devices.Count);
        for (int i = 0; i < devices.Count; i++)
            sections.Add(Section(devices[i].Model, width, devices[i].EverOnline, devices[i].Note, last: i == devices.Count - 1));
        return Page(width, height, sections, hasStripItems, drawsTrend: drawsTrend);
    }
}
