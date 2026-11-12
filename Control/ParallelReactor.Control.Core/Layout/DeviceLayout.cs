using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Layout;

/// <summary>设备页宽度类（device.js sizeRoot）：L = 工作区 ≥ 1100（下排三列：共用资源 ｜ 设备信息 ｜ 报警与事件），M = 更窄（下排两列：共用资源 ｜ 页签面板）。</summary>
public enum DeviceWidthClass { L, M }

/// <summary>设备页下排的面板（M 时「设备信息」与「报警与事件」合成一个页签面板 <see cref="Tabs"/>）。</summary>
public enum DevicePanel { Resources, Info, Log, Tabs }

/// <summary>窄工作区页签面板的当前页签（device.js infoTab）。</summary>
public enum DeviceLogTab { Info, Log }

/// <summary>釜体放哪儿：左边（默认横排）、上面（竖卡：够高够宽，或很窄但够高；放大的面板）、不画。</summary>
public enum VesselPlacement { Hidden, Left, Above }

/// <summary>
/// 设定值 / 目标小字（升温「→ 120 ℃」、反应「SP 120 ℃」、升压「→ 0.350 MPa」）的排法：
/// Inline = 读数列 ≥ 190 px，和读数同一行、放得下才显示（fit-or-hide）；OwnLine = 读数列更窄，换到读数下面单独一行（18 px 行高；
/// 卡片里放不下这一行时整行不显示，见 <see cref="DeviceCard.FitSubs"/>）；Hidden = 卡片矮（内容 &lt; 180）或紧凑卡，不显示（检查器 / 悬停提示里有）。
/// </summary>
public enum SubLine { Hidden, Inline, OwnLine }

/// <summary>
/// 设备页的反应釜网格（device.js gridOf）：卡片种类、列 / 行数、内边距、间距、未安装段数、窄紧凑卡（is-narrow：每张 &lt; 140 px，不画釜体、状态字第二行）、
/// 紧卡（is-tight：8 路一排每张 &lt; 150 px，内边距收紧）、卡片最小高度（放不下时卡片区滚动，卡片保持这个高度）。
/// </summary>
public sealed record DeviceGrid(CardKind Kind, int Cols, int Rows, double Pad, double Gap, int Runs, bool Narrow, bool Tight, double MinHeight);

/// <summary>下排的一块面板：宽 / 高（面板外框）、内容区的取舍（共用资源 ≤ 350：气路两列从标签处开始；设备信息 ≤ 339：「示例型号 · 参数为假设」只留图标；
/// 报警与事件内容高 ≤ 259：整块一起滚动，当前报警在最上面完整可见）。</summary>
public sealed record DeviceBottomPanel(DevicePanel Panel, double Width, double Height, bool Narrow, bool Short);

/// <summary>
/// 设备页的整体版面（device.js sizeRoot + html；css/device.css）：宽度类、是否页签、上排（反应釜面板）高度（null = 1fr：反应釜面板或下排关掉了）、
/// 卡片区是否纵向滚动、说明条数（断线 / 急停）、网格、卡片区的列与块、卡片外框尺寸与卡片内的取舍（<see cref="Card"/>）、下排各面板。
/// </summary>
public sealed record DevicePage(
    DeviceWidthClass W,
    bool Tabbed,
    double? TopRow,
    bool Scroll,
    int Bars,
    bool NeverConnected,
    DeviceGrid Grid,
    IReadOnlyList<GridTrack> Tracks,
    IReadOnlyList<GridTile> Tiles,
    double CardsHeight,
    double CardWidth,
    double CardHeight,
    DeviceCard Card,
    double BottomHeight,
    IReadOnlyList<DeviceBottomPanel> Bottom)
{
    /// <summary>样稿根元素的类名：'dw-l' / 'dw-m'。</summary>
    public string WidthClassName => W == DeviceWidthClass.L ? "dw-l" : "dw-m";
}

/// <summary>
/// 设备页一张反应釜卡片的内部取舍（容器 dvcard = 卡片内容盒；读数列 = 容器 dvreads；css/device.css）。
/// 先给出与文字无关的部分（内边距、内容尺寸、标题 / 程序块的高度、主体高度、釜体位置与尺寸、读数列宽、设定值排法、宽卡的次要行、程序块里显示什么），
/// 读数字号按读数长度另算（<see cref="Temp"/> / <see cref="Pressure"/> / <see cref="StirValue"/> / <see cref="GasValue"/>）。
/// 标题行（RV 名 + 状态字）放不下时状态换到第二行（48 px）—— 那要量文字，界面量到后用 <see cref="DeviceLayout.Card"/> 的 headHeight 再算一次。
/// </summary>
public sealed record DeviceCard(
    CardKind Kind,
    bool Narrow,
    bool Tight,
    double PadLeft,
    double PadTop,
    double PadRight,
    double PadBottom,
    double Width,
    double Height,
    double Gap,
    double HeadHeight,
    bool HeadColumn,
    double ProgHeight,
    bool ProgNameLine,
    bool StepCount,
    bool NoneRemaining,
    bool NoneBar,
    double BodyHeight,
    VesselPlacement Vessel,
    double VesselWidth,
    double VesselHeight,
    bool FullVessel,
    double BodyGap,
    double ReadsWidth,
    SubLine Subs,
    bool StirRow,
    bool HeatRow,
    bool GasRow,
    bool KvNotes)
{
    /// <summary>
    /// 温度（主读数）：宽 / 一排卡 30 px（行高 36），读数列 ≤ 99 → 26（32）、≤ 89 → 24（30）、≤ 81 → 22（28）；紧凑卡 28（34），窄紧凑卡 24（27）。
    /// 5 位读数（'199.9'）再按读数列宽逐级缩小、最后去单位（device.css:345-350，行高跟宽度那一档）。<paramref name="len"/> = 读数字符数。
    /// </summary>
    public ReadingFont Temp(int len)
    {
        double r = ReadsWidth;
        double size, line;
        if (Kind == CardKind.Compact)
        {
            (size, line) = Narrow ? (24d, 27d) : (28d, 34d);                     // device.css:141、148-152、160
        }
        else
        {
            (size, line) = (30d, 36d);                                          // :59
            if (r <= 99) (size, line) = (26, 32);                               // :73
            if (r <= 89) (size, line) = (24, 30);                               // :74
            if (r <= 81) (size, line) = (22, 28);                               // :75
        }
        bool unit = true;
        if (len == 5)
        {
            // 各档特异性相同、按源顺序生效（更窄的一档写在后面）
            if (Kind != CardKind.Compact && r <= 111.8) size = 26;              // :345
            if (Kind == CardKind.Compact && !Narrow && r <= 106.3) size = 26;   // :346
            if (!Narrow && r <= 100.5) size = 24;                               // :347
            if (r <= 94.7) size = 22;                                           // :348
            if (r <= 88.9) size = 20;                                           // :349
            if (r <= 83.4) unit = false;                                        // :350
        }
        return new ReadingFont(size, line, unit);
    }

    /// <summary>
    /// 压力：宽 / 一排卡 20 px（行高 26），读数列 ≤ 89 → 18（24）、≤ 81 → 16（22）；紧凑卡 18（24），窄紧凑卡 16（19）。
    /// 5 位读数（'4.136'）：非紧凑卡 ≤ 93.2 → 18、非窄 ≤ 88.2 → 16、≤ 83.2 去单位（device.css:352-354）。
    /// </summary>
    public ReadingFont Pressure(int len)
    {
        double r = ReadsWidth;
        double size, line;
        if (Kind == CardKind.Compact)
        {
            (size, line) = Narrow ? (16d, 19d) : (18d, 24d);                     // device.css:142、161
        }
        else
        {
            (size, line) = (20d, 26d);                                          // :60
            if (r <= 89) (size, line) = (18, 24);                               // :74
            if (r <= 81) (size, line) = (16, 22);                               // :75
        }
        bool unit = true;
        if (len == 5)
        {
            if (Kind != CardKind.Compact && r <= 93.2) size = 18;               // :352
            if (!Narrow && r <= 88.2) size = 16;                                // :353
            if (r <= 83.2) unit = false;                                        // :354
        }
        return new ReadingFont(size, line, unit);
    }

    /// <summary>宽卡「搅拌」行的数字（16 px，行高 26）：4 位转速（'1500'）读数列 ≤ 114.8 → 15、≤ 112.5 去「rpm」（device.css:362-363）。</summary>
    public ReadingFont StirValue(int len)
    {
        double size = 16;
        bool unit = true;
        if (len == 4 && ReadsWidth <= 114.8) size = 15;
        if (len == 4 && ReadsWidth <= 112.5) unit = false;
        return new ReadingFont(size, 26, unit);
    }

    /// <summary>宽卡「加热」行的数字（16 px，没有长读数阶梯）。</summary>
    public ReadingFont HeatValue(int len) => new(16, 26);

    /// <summary>
    /// 宽卡「耗气」行的数字（16 px）：按数值字符数（'1.82' → 4 … '1234.50' → 7）各一套阶梯 16 → 15 → 14 → 去「mmol」（device.css:367-378）。
    /// </summary>
    public ReadingFont GasValue(int len)
    {
        (double a, double b, double c) = len switch
        {
            4 => (118.2, 116.3, 114.3),
            5 => (128.2, 125.7, 123.2),
            6 => (138.3, 135.2, 132.1),
            7 => (148.3, 144.6, 141.0),
            _ => (double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity),
        };
        double r = ReadsWidth, size = 16;
        bool unit = true;
        if (r <= a) size = 15;
        if (r <= b) size = 14;
        if (r <= c) unit = false;
        return new ReadingFont(size, 26, unit);
    }

    /// <summary>
    /// 单独成行的设定值 / 目标放不下时整行不显示（device.js fitSubs）：读数列的内容超出卡片主体就先藏一个、再藏另一个
    /// （升压中 / 等待进气先藏设定值，其余先藏目标）。<paramref name="hasSetpoint"/> / <paramref name="hasTarget"/> = 这张卡有没有那行小字
    /// （温度旁「→ 120 ℃ / SP 120 ℃」、压力旁「→ 0.350 MPa / 等待进气」）；<paramref name="pressFirst"/> = 状态是升压中 / 等待中。返回 (藏设定值, 藏目标)。
    /// 小字不显示（<see cref="Subs"/> = Hidden）时它们不占地方，但样稿照样量：主体本身就放不下读数时两行都标成藏起来（结果不可见，不影响版面）。
    /// </summary>
    public (bool HideSetpoint, bool HideTarget) FitSubs(bool hasSetpoint, bool hasTarget, bool pressFirst)
    {
        if (!hasSetpoint && !hasTarget) return (false, false);
        bool hideT = false, hideP = false;
        bool Over() => ReadsOverflow(hasSetpoint && !hideT, hasTarget && !hideP);
        if (!Over()) return (false, false);
        // 第一步：升压中 / 等待中先藏设定值，否则先藏目标；还超出再藏另一个
        if (pressFirst) hideT = true; else hideP = true;
        if (!Over()) return (hideT, hideP);
        if (pressFirst) hideP = true; else hideT = true;
        return (hideT, hideP);
    }

    /// <summary>
    /// 读数列内容的高度：温度行 + 压力行（行高跟读数列宽那一档；读数列 ≥ 190 时固定 36 + 26）+ 单独成行的小字（各 18）+ 宽卡的次要行（2 + 26 × 行数）。
    /// <paramref name="setpointLine"/> / <paramref name="targetLine"/> = 显示着的单独成行小字。
    /// </summary>
    public double ReadsHeight(bool setpointLine = false, bool targetLine = false)
    {
        double h = Subs == SubLine.Inline ? 36 + 26 : Temp(0).LineHeight + Pressure(0).LineHeight;   // device.css:358
        if (Subs == SubLine.OwnLine) h += (setpointLine ? 18 : 0) + (targetLine ? 18 : 0);           // :66
        if (Kind == CardKind.Wide) h += 2 + 26 * ((StirRow ? 1 : 0) + (HeatRow ? 1 : 0) + (GasRow ? 1 : 0));   // :77-78
        return h;
    }

    /// <summary>
    /// 竖卡（釜体在上）时釜体的高度：主体高 − 间距 − 读数列内容高（含显示着的单独成行小字），不小于最小高度（一排卡 56，宽卡 / 紧凑卡 40）。
    /// 横排时就是 <see cref="VesselHeight"/>。
    /// </summary>
    public double VesselHeightWith(bool setpointLine, bool targetLine)
    {
        if (Vessel != VesselPlacement.Above) return VesselHeight;
        return Math.Max(VesselMinHeight, BodyHeight - AboveGap - ReadsHeight(setpointLine, targetLine));
    }

    /// <summary>读数列超出卡片主体（device.js fitSubs over()：第一行顶 &lt; 主体顶 − 0.5 或最后一行底 &gt; 主体底 + 0.5）。</summary>
    public bool ReadsOverflow(bool setpointLine, bool targetLine)
    {
        double need = ReadsHeight(setpointLine, targetLine);
        // 竖卡：读数在下面（自然高度），釜体占剩下的、但不小于最小高度
        if (Vessel == VesselPlacement.Above) return need + AboveGap + VesselMinHeight > BodyHeight + 0.5;
        // 横排：读数列在主体里垂直居中（窄紧凑卡靠上）
        if (Narrow) return need > BodyHeight + 0.5;
        return (need - BodyHeight) / 2 > 0.5;
    }

    /// <summary>竖卡时釜体的最小高度：一排卡 56，宽卡 / 紧凑卡 40（device.css:112、132、172）。</summary>
    public double VesselMinHeight => Kind == CardKind.Row ? 56 : 40;

    /// <summary>竖卡时釜体与读数的间距：一排卡 8、宽卡 10、紧凑卡 6（device.css:111、131、171）。</summary>
    public double AboveGap => Kind switch { CardKind.Row => 8, CardKind.Wide => 10, _ => 6 };
}

/// <summary>
/// 设备页的布局计算（PORT-PLAN §4.8 第 2 层、§5 M4「布局」）：样稿 device.js（gridOf / sizeRoot / fitSubs）与 css/device.css 的数字规则，纯函数。
/// 输入：设备页根元素的宽 / 高（= 工作区；只看父元素给出的尺寸）、型号（卡片种类、通道数、装机）、状态（在线、连上过、急停）、面板的放大 / 关闭。
/// <para>与样稿的差异（演示数据碰不到的情况，按更合理的样子排）：</para>
/// <list type="bullet">
/// <item>不连续装机（如 RV1–2、5–6）的一排 / 宽卡：列按 DOM 顺序给（卡片 1fr、每段未安装 140 px）—— 样稿把 140 px 的列都放在最后，卡片会落进窄列。</item>
/// <item>紧凑卡的未安装段跨它自己的列数（同总览，通道号与列一一对应）—— 样稿设备页只给它一格。</item>
/// </list>
/// </summary>
public static class DeviceLayout
{
    // ———— 尺寸常量（设计 px；device.js:31-33 与 css/device.css） ————
    /// <summary>面板标题栏 42、说明条（断线 / 急停）45、下排至少留 156（device.js:33）。</summary>
    public const double Head = 42, Bar = 45, MinBottom = 156;
    /// <summary>未安装窄列（device.js:32 UN_W）。</summary>
    public const double UntileWidth = 140;
    /// <summary>下排三列 / 两列的分界（device.js:410）。</summary>
    public const double WideMin = 1100;
    /// <summary>上排默认占 52 %（device.js:422）。</summary>
    public const double TopShare = 0.52;
    /// <summary>从未连上：上排只有一条说明（device.js:418：HEAD + 110）。</summary>
    public const double NeverTop = Head + 110;
    /// <summary>差几 px 时卡片自己的余量够用，不滚动（device.js:426）。</summary>
    public const double ScrollSlack = 8;
    /// <summary>事件列表最多显示的条数（device.js:31 EV_MAX）。</summary>
    public const int EventMax = 60;
    /// <summary>
    /// 卡片区滚动时竖向滚动条占的宽度（device.css:27 ::-webkit-scrollbar 8 px）：样稿的卡片因此窄一点（4 列每张 −2 px）。
    /// 界面若用浮在内容上的滚动条，就给卡片区右边留出这 8 px，卡片才和样稿一样宽。窄 / 紧的判断仍按整个宽度（gridOf 用根元素宽）。
    /// </summary>
    public const double ScrollbarWidth = 8;

    /// <summary>卡片种类（device.js:42 kindOf）：wide4 → 宽卡，grid16 → 紧凑卡，其余一排卡（不看装机数，与总览不同）。</summary>
    public static CardKind CardKindOf(ModelProfile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Layout switch { LayoutKind.Wide4 => CardKind.Wide, LayoutKind.Grid16 => CardKind.Compact, _ => CardKind.Row };
    }

    /// <summary>
    /// 网格（device.js:140 gridOf）：<paramref name="width"/> = 设备页宽度。
    /// 紧凑卡：8 列、行数 = ⌈通道数 / 8⌉、内边距 12、间距 8；每张 &lt; 140 px 为窄卡（最小高 112，否则 136）。
    /// 其余：内边距 16、间距 10、列数 = 已安装数；8 路一排、没有未安装、每张 &lt; 112 px 时折成两排（列数 = ⌈n / 2⌉）；每张 &lt; 150 px 为紧卡
    /// （最小高 188，否则宽卡 168、一排卡 164）。
    /// </summary>
    public static DeviceGrid GridOf(ModelProfile model, double width)
    {
        ArgumentNullException.ThrowIfNull(model);
        var kind = CardKindOf(model);
        int nInst = model.Installed.Count;
        int runs = model.UninstalledRuns.Count;
        if (kind == CardKind.Compact)
        {
            const int cols = 8;
            const double pad = 12, gap = 8;
            double cw = (width - 2 * pad - (cols - 1) * gap) / cols;
            bool narrow = cw < 140;
            return new DeviceGrid(kind, cols, (model.ChannelCount + cols - 1) / cols, pad, gap, runs, narrow, false, narrow ? 112 : 136);
        }
        {
            const double pad = 16, gap = 10;
            int cols = nInst, rows = 1;
            if (kind == CardKind.Row && runs == 0 && nInst > 4 && (width - 2 * pad - (nInst - 1) * gap) / nInst < 112) { cols = (nInst + 1) / 2; rows = 2; }
            double cw = cols > 0 ? (width - 2 * pad - (cols - 1) * gap - runs * (UntileWidth + gap)) / cols : 0;
            bool tight = cw < 150;
            return new DeviceGrid(kind, cols, rows, pad, gap, runs, false, tight, tight ? 188 : kind == CardKind.Wide ? 168 : 164);
        }
    }

    /// <summary>
    /// 整页（device.js:405 sizeRoot + html + css）。上排：默认 52 %；卡片放不下时按需要加高（下排至少 156）；还放不下（差 &gt; 8 px）时卡片保持最小高度、卡片区滚动；
    /// 反应釜面板放大时不滚动（卡片铺满放大的面板）；从未连上 = 42 + 110；反应釜面板或整个下排关掉时 1fr。
    /// </summary>
    public static DevicePage Compute(DeviceLayoutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        double w = input.Width, h = input.Height;
        var W = w >= WideMin ? DeviceWidthClass.L : DeviceWidthClass.M;
        var g = GridOf(input.Model, w);
        int bars = (input.Online ? 0 : 1) + (input.Estop ? 1 : 0);
        bool bottomOff = W == DeviceWidthClass.L ? input.ResClosed && input.InfoClosed && input.LogClosed : input.ResClosed && input.TabsClosed;
        double? top;
        bool scroll = false;
        if (input.RvClosed || bottomOff) top = null;
        else if (!input.EverOnline) top = NeverTop;
        else
        {
            double need = Head + bars * Bar + 2 * g.Pad + g.Rows * g.MinHeight + (g.Rows - 1) * g.Gap + 2;
            double baseH = JsRound(h * TopShare);
            double t = Math.Max(baseH, Math.Min(need, h - MinBottom));
            scroll = !input.RvMax && need - t > ScrollSlack;
            top = t;
        }

        // 反应釜面板的高度：放大时铺满整页；1fr 时铺满（或关掉了）；否则上排
        double panelH = input.RvMax ? h : input.RvClosed ? 0 : top ?? h;
        double cardsH = Math.Max(0, panelH - Head - (input.EverOnline ? bars * Bar : 0));
        var (tracks, tiles, cardW, cardH) = Cards(input.Model, g, w, cardsH, scroll);
        var card = Card(g.Kind, cardW, cardH, g.Narrow, g.Tight, input.RvMax);

        // 下排：L 三列 1 : 1 : 1.2，M 两列 1 : 1.3，列间 1 px；有面板关掉时其余平分（device.css:210-213）
        double bottomH = input.RvClosed ? h : top is double tr ? Math.Max(0, h - tr - 1) : 0;
        var panels = new List<(DevicePanel P, double Fr)>();
        if (W == DeviceWidthClass.L)
        {
            if (!input.ResClosed) panels.Add((DevicePanel.Resources, 1));
            if (!input.InfoClosed) panels.Add((DevicePanel.Info, 1));
            if (!input.LogClosed) panels.Add((DevicePanel.Log, 1.2));
        }
        else
        {
            if (!input.ResClosed) panels.Add((DevicePanel.Resources, 1));
            if (!input.TabsClosed) panels.Add((DevicePanel.Tabs, 1.3));
        }
        bool anyClosed = W == DeviceWidthClass.L ? panels.Count < 3 : panels.Count < 2;
        var bottom = new List<DeviceBottomPanel>();
        if (panels.Count > 0 && bottomH > 0)
        {
            double sum = anyClosed ? panels.Count : panels.Sum(p => p.Fr);
            double avail = w - (panels.Count - 1);
            foreach (var p in panels)
            {
                double pw = avail * (anyClosed ? 1 : p.Fr) / sum;
                bottom.Add(Bottom(p.P, pw, bottomH, input.LogTab));
            }
        }
        return new DevicePage(W, W == DeviceWidthClass.M, top, scroll, bars, !input.EverOnline, g, tracks, tiles, cardsH, cardW, cardH, card, bottomH, bottom);
    }

    /// <summary>
    /// 下排面板内容区的取舍：共用资源内容宽（面板 − 32）≤ 350 → 气路两列从标签处开始（device.css:261）；设备信息内容宽 ≤ 339 → 型号旁的说明只留图标（:275）；
    /// 报警与事件内容高（面板 − 42）≤ 259 → 整块一起滚动（:294）。页签面板按当前页签算。
    /// </summary>
    public static DeviceBottomPanel Bottom(DevicePanel panel, double width, double height, DeviceLogTab tab = DeviceLogTab.Info)
    {
        var p = panel == DevicePanel.Tabs ? (tab == DeviceLogTab.Log ? DevicePanel.Log : DevicePanel.Info) : panel;
        bool narrow = p switch
        {
            DevicePanel.Resources => width - 32 <= 350,
            DevicePanel.Info => width - 32 <= 339,
            _ => false,
        };
        bool shortLog = p == DevicePanel.Log && height - Head <= 259;
        return new DeviceBottomPanel(panel, width, height, narrow, shortLog);
    }

    /// <summary>
    /// 卡片区的列与块：紧凑卡 8 列（未安装段跨它自己的列数）；其余按 DOM 顺序：卡片 1fr、未安装段 140 px（8 路折两排时没有未安装）。
    /// 卡片高：滚动时 = 最小高度；否则卡片区平分（minmax(0, 1fr)）。
    /// </summary>
    private static (List<GridTrack> Tracks, List<GridTile> Tiles, double CardW, double CardH) Cards(ModelProfile m, DeviceGrid g, double width, double cardsH, bool scroll)
    {
        var tracks = new List<GridTrack>();
        var tiles = new List<GridTile>();
        double inner = width - 2 * g.Pad - (scroll ? ScrollbarWidth : 0);
        var slots = m.Channels;
        bool by8 = g.Kind == CardKind.Compact;
        var blocks = new List<(int From, int To, bool Installed, int Span)>();
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Installed) { blocks.Add((slots[i].N, slots[i].N, true, 1)); continue; }
            int j = i;
            while (j + 1 < slots.Count && !slots[j + 1].Installed && (!by8 || (j + 1) / 8 == i / 8)) j++;
            blocks.Add((slots[i].N, slots[j].N, false, by8 ? j - i + 1 : 1));
            i = j;
        }
        double cardW;
        if (by8)
        {
            cardW = (inner - (g.Cols - 1) * g.Gap) / g.Cols;
            for (int c = 0; c < g.Cols; c++) tracks.Add(new GridTrack(false, cardW));
            int col = 0, row = 0;
            foreach (var b in blocks)
            {
                if (col + b.Span > g.Cols) { row++; col = 0; }
                tiles.Add(new GridTile(b.From, b.To, b.Installed, col, row, b.Span, 0, 0, 0, 0));
                col += b.Span;
            }
        }
        else if (g.Rows > 1)
        {
            // 8 路一排折成两排（只在没有未安装时）：每排 cols 张
            cardW = (inner - (g.Cols - 1) * g.Gap) / g.Cols;
            for (int c = 0; c < g.Cols; c++) tracks.Add(new GridTrack(false, cardW));
            for (int i = 0; i < blocks.Count; i++)
                tiles.Add(new GridTile(blocks[i].From, blocks[i].To, true, i % g.Cols, i / g.Cols, 1, 0, 0, 0, 0));
        }
        else
        {
            int nUn = blocks.Count(b => !b.Installed);
            int nCards = blocks.Count - nUn;
            cardW = nCards > 0 ? (inner - (blocks.Count - 1) * g.Gap - nUn * UntileWidth) / nCards : 0;
            for (int c = 0; c < blocks.Count; c++)
            {
                var b = blocks[c];
                tracks.Add(new GridTrack(!b.Installed, b.Installed ? cardW : UntileWidth));
                tiles.Add(new GridTile(b.From, b.To, b.Installed, c, 0, 1, 0, 0, 0, 0));
            }
        }
        int rows = Math.Max(1, g.Rows);
        double share = (cardsH - 2 * g.Pad - (rows - 1) * g.Gap) / rows;
        double cardH = scroll ? Math.Max(g.MinHeight, share) : Math.Max(0, share);
        var xs = new double[tracks.Count + 1];
        xs[0] = g.Pad;
        for (int c = 0; c < tracks.Count; c++) xs[c + 1] = xs[c] + tracks[c].Width + g.Gap;
        for (int t = 0; t < tiles.Count; t++)
        {
            var x = tiles[t];
            double left = xs[x.Column], wdt = xs[Math.Min(tracks.Count, x.Column + x.Span)] - g.Gap - left;
            tiles[t] = x with { X = left, Y = g.Pad + x.Row * (cardH + g.Gap), Width = wdt, Height = cardH };
        }
        return (tracks, tiles, cardW, cardH);
    }

    /// <summary>
    /// 一张卡片的内部取舍（容器 dvcard / dvreads；css/device.css:29-175）。<paramref name="outerWidth"/> / <paramref name="outerHeight"/> = 卡片外框（网格给的格子）；
    /// <paramref name="narrow"/> / <paramref name="tight"/> = 网格的 is-narrow / is-tight；<paramref name="maximized"/> = 反应釜面板放大（紧凑卡画完整釜体）；
    /// <paramref name="headHeight"/> = 量到的标题行高度（状态字换到第二行时 48；不给时 24，窄紧凑卡 38）。
    /// </summary>
    public static DeviceCard Card(CardKind kind, double outerWidth, double outerHeight, bool narrow = false, bool tight = false, bool maximized = false, double? headHeight = null)
    {
        bool compact = kind == CardKind.Compact;
        narrow = narrow && compact;
        // 内边距（上 右 下 左）：一排卡 12 16 13 16（:31）、宽卡 12 18 13 16（:123）、紧凑卡 8 10 10 10（:137）、窄紧凑卡 6 9 7 9（:154）、紧卡 10 12 11 12（:167）
        (double pt, double pr, double pb, double pl) = kind switch
        {
            CardKind.Compact when narrow => (6d, 9d, 7d, 9d),
            CardKind.Compact => (8d, 10d, 10d, 10d),
            _ when tight => (10d, 12d, 11d, 12d),
            CardKind.Wide => (12d, 18d, 13d, 16d),
            _ => (12d, 16d, 13d, 16d),
        };
        double W = Math.Max(0, outerWidth - 2 - pl - pr), H = Math.Max(0, outerHeight - 2 - pt - pb);
        double gap = narrow ? 3 : compact ? 6 : 10;                               // :30、137、154
        bool headColumn = narrow;                                                 // :155（状态永远在第二行）
        double head = headHeight ?? (narrow ? 20 + 18 : 24);                      // :43-44、156-157

        // 程序块（:88-106）：两行 = 边线 1 + 上内边距 9 + 20 + 6 + 20；内容矮于 240（或紧凑卡）只留进度条一行（上内边距 8，紧凑卡 6）；窄紧凑卡只有 8 px 的进度条
        bool nameLine = !compact && !(H <= 239);                                  // :106、145
        double prog = narrow ? 8 : nameLine ? 1 + 9 + 20 + 6 + 20 : 1 + (compact ? 6 : 8) + 20;
        bool stepCount = !(W <= 168);                                             // :99 第 k/n 步
        bool noneRemaining = narrow ? false : !(kind != CardKind.Compact && H >= 240);   // :101、162
        bool noneBar = !compact || narrow;                                        // :102-103

        double body = Math.Max(0, H - head - gap - prog - gap);

        // 釜体（:47-175）
        VesselPlacement place = VesselPlacement.Left;
        double maxFrac = 0.36, fixedW = 0, bodyGap = 14;
        switch (kind)
        {
            case CardKind.Row:
                if (H >= 200 && (W >= 190 || W <= 129)) place = VesselPlacement.Above;     // :110-120
                else if (W >= 130 && W <= 189) maxFrac = 0.26;                              // :109
                if (place == VesselPlacement.Left && tight && H <= 199) place = VesselPlacement.Hidden;   // :168
                break;
            case CardKind.Wide:
                maxFrac = 0.32;                                                             // :128
                if (H >= 420) place = VesselPlacement.Above;                                // :130-134
                break;
            default:
                bodyGap = 10;                                                               // :138
                fixedW = W >= 150 && H >= 150 ? 46 : 34;                                    // :139、148-152
                if (H >= 220) place = VesselPlacement.Above;                                // :170-175（窄紧凑卡此时也画）
                else if (narrow || W <= 149) place = VesselPlacement.Hidden;                // :140、158
                break;
        }
        // 紧凑卡在竖排时：窄卡的「不画」被 :173 改回来；非窄卡宽 ≤ 149 时照样不画（:140 的选择器特异性更低但 :170 不改 display）
        if (kind == CardKind.Compact && place == VesselPlacement.Above && !narrow && W <= 149) place = VesselPlacement.Hidden;

        double vw = 0, vh = 0, reads = W;
        SubLine subs;
        bool stirRow = false, heatRow = false, gasRow = false;
        if (kind == CardKind.Wide)
        {
            stirRow = !(H <= 139);                                                // :127
            heatRow = !(H <= 199);                                                // :126
            gasRow = !(H <= 259);                                                 // :125
        }
        switch (place)
        {
            case VesselPlacement.Left:
                if (kind == CardKind.Compact) { vw = fixedW; vh = body; }
                else { vh = body; vw = Math.Min(body * 150 / 230, maxFrac * W); }
                reads = Math.Max(0, W - vw - bodyGap);
                break;
            case VesselPlacement.Above:
                vw = W;
                reads = W;
                break;
        }
        // 设定值 / 目标小字（:63-72、144、356-360）：读数列 ≤ 189 换行；≥ 190 同一行放得下才显示（189–190 之间样稿同一行省略，按同一行算）
        if (compact || H <= 179) subs = SubLine.Hidden;
        else subs = reads <= 189 ? SubLine.OwnLine : SubLine.Inline;
        bool kvNotes = kind == CardKind.Wide && !(reads <= 150);                  // :85

        var card = new DeviceCard(kind, narrow, tight && !compact, pl, pt, pr, pb, W, H, gap, head, headColumn, prog, nameLine, stepCount,
            noneRemaining, noneBar, body, place, vw, vh, compact && maximized, bodyGap, reads, subs, stirRow, heatRow, gasRow, kvNotes);
        // 竖排：釜体高 = 主体 − 间距 − 读数（不含单独成行的小字；带小字时用 VesselHeightWith）
        if (place == VesselPlacement.Above) card = card with { VesselHeight = card.VesselHeightWith(false, false) };
        return card;
    }

    /// <summary>JS Math.round（.5 向上）。</summary>
    private static double JsRound(double v) => Math.Floor(v + 0.5);
}

/// <summary>
/// 设备页布局的输入：根元素（工作区）宽 / 高、型号；状态（在线、连上过、急停）；面板的关闭 / 放大（视图 › 关闭 / 放大面板）；
/// 窄工作区页签面板当前的页签（只影响下排面板内容区的取舍）。
/// </summary>
public sealed record DeviceLayoutInput(double Width, double Height, ModelProfile Model)
{
    public bool Online { get; init; } = true;
    public bool EverOnline { get; init; } = true;
    public bool Estop { get; init; }
    /// <summary>反应釜面板关掉了。</summary>
    public bool RvClosed { get; init; }
    /// <summary>反应釜面板放大了。</summary>
    public bool RvMax { get; init; }
    public bool ResClosed { get; init; }
    public bool InfoClosed { get; init; }
    public bool LogClosed { get; init; }
    /// <summary>窄工作区的「设备信息 / 报警与事件」页签面板关掉了。</summary>
    public bool TabsClosed { get; init; }
    public DeviceLogTab LogTab { get; init; } = DeviceLogTab.Info;
}

/// <summary>
/// 窄工作区「设备信息 / 报警与事件」页签的自动选择（device.js syncTab）：第一次绘制时有未确认的错误报警（急停那条也算）就打开「报警与事件」，否则「设备信息」；
/// 之后只在<b>新的</b>错误报警（确认号没见过）出现、且用户还没自己点过页签时才切到「报警与事件」—— 确认报警从不改变页签。
/// 每打开一次设备页用一个新实例（样稿 render 时清空）。
/// </summary>
public sealed class DeviceLogTabRule
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private DeviceLogTab? _tab;

    /// <summary>当前页签（还没定时是「设备信息」）。</summary>
    public DeviceLogTab Tab => _tab ?? DeviceLogTab.Info;

    /// <summary>用户自己点过页签（之后新的报警也不替用户换页签；页签上有红点）。</summary>
    public bool UserPicked { get; private set; }

    /// <summary>每次绘制前调用：<paramref name="unackedErrIds"/> = 未确认的错误报警（AlarmBook.UnackedErrIds()）。返回当前页签。</summary>
    public DeviceLogTab Sync(IEnumerable<string> unackedErrIds)
    {
        ArgumentNullException.ThrowIfNull(unackedErrIds);
        var ids = unackedErrIds.ToArray();
        bool fresh = ids.Any(id => !_seen.Contains(id));
        foreach (var id in ids) _seen.Add(id);
        if (_tab is null) _tab = ids.Length > 0 ? DeviceLogTab.Log : DeviceLogTab.Info;
        else if (fresh && !UserPicked) _tab = DeviceLogTab.Log;
        return Tab;
    }

    /// <summary>用户点了页签。</summary>
    public void Pick(DeviceLogTab tab)
    {
        UserPicked = true;
        _tab = tab;
    }

    /// <summary>从头来（样稿每打开一次设备页都是新的 render：页签还没定、用户没点过、见过的报警清空）。</summary>
    public void Reset()
    {
        _seen.Clear();
        _tab = null;
        UserPicked = false;
    }
}
