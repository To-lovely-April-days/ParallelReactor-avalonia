using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Chart;

// ============================================================================
// 曲线的输入（样稿 ui.lineChart 的 opts，js/ui.js:696-704 的注释）：系列、纵轴、参考线、事件标记、横轴范围、空数据文字。
// 与 JS 的对应：points [[t, v], ...] → ChartPoint(T = 距 1970-01-01 UTC 的毫秒数（JS 的 Date 值）, V = 值；缺失 = NaN)；
// 颜色写令牌名（'--ch-t'），由界面按 Palette 取画笔（样稿是 PR.tok 取出的十六进制值）；可空字段 = JS 里没写（undefined）。
// 都是不可变记录：每次数据更新整个换掉（chart.update(opts) 的 C# 写法），布局由 ChartLayout.Compute 算一次、绘制时只读。
// ============================================================================

/// <summary>曲线上的一个点：<see cref="T"/> = JS Date 值（UTC 毫秒），<see cref="V"/> = 值（缺失 / 无效 = NaN，样稿 okv 判为假，路径在这里断开）。</summary>
public readonly record struct ChartPoint(double T, double V)
{
    /// <summary>样稿 okv：v != null &amp;&amp; isFinite(v)。</summary>
    public bool Ok => double.IsFinite(V);

    /// <summary>由时刻（Local / Unspecified 按本地，Utc 按 UTC）与可空值构造；null → NaN。</summary>
    public static ChartPoint At(DateTime t, double? v) => new(ChartTime.Ms(t), v ?? double.NaN);
}

/// <summary>JS 的 Date 值（UTC 毫秒）与 DateTime 之间的换算（曲线的横轴、刻度、悬停时间都用它）。</summary>
public static class ChartTime
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>测试用的时区（<see cref="UseZone"/>，只在当前的异步流里）；null = 系统时区。</summary>
    private static readonly System.Threading.AsyncLocal<TimeZoneInfo?> ZoneOverride = new();

    /// <summary>
    /// 在当前的异步流里按 <paramref name="zone"/> 换算本地时间（Dispose 恢复）。只给测试用：样稿向量是在某个时区生成的（刻度的 HH:MM、整点对齐都跟着时区），
    /// 在半点时区（阿德莱德、加尔各答）跑测试时按生成时的时区换算，结果才与样稿逐字相同。界面从不调用（一律系统时区，同样稿的浏览器）。
    /// </summary>
    public static IDisposable UseZone(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var prev = ZoneOverride.Value;
        ZoneOverride.Value = zone;
        return new ZoneScope(prev);
    }

    private sealed class ZoneScope : IDisposable
    {
        private readonly TimeZoneInfo? _prev;
        private bool _done;
        public ZoneScope(TimeZoneInfo? prev) => _prev = prev;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            ZoneOverride.Value = _prev;
        }
    }

    /// <summary>DateTime → UTC 毫秒（保留小数部分）。Kind = Local / Unspecified 按本地时间换算（同 JS 用本地日历字段建的 Date）。</summary>
    public static double Ms(DateTime t)
    {
        DateTime utc = t.Kind == DateTimeKind.Utc ? t
            : ZoneOverride.Value is { } z ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), z)
            : t.ToUniversalTime();
        return utc.Subtract(Epoch).Ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    /// <summary>UTC 毫秒 → 本地时间（JS new Date(ms)：毫秒的小数部分截掉）。</summary>
    public static DateTime Local(double ms)
    {
        var utc = Epoch.AddTicks((long)Math.Truncate(ms) * TimeSpan.TicksPerMillisecond);
        // 换了时区时给「不指定」种类（Fmt.Hm 遇到 Utc 种类会再按系统时区换一次）
        return ZoneOverride.Value is { } z ? DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, z), DateTimeKind.Unspecified) : utc.ToLocalTime();
    }

    /// <summary>横轴刻度文字（样稿 hmT）：本地 'HH:MM'。</summary>
    public static string Hm(double ms) => Format.Fmt.Hm(Local(ms));

    /// <summary>悬停提示的时间（样稿 PR.fmt.time）：本地 'HH:MM:SS'。</summary>
    public static string Time(double ms) => Format.Fmt.Time(Local(ms));
}

/// <summary>
/// 一条曲线（样稿 series 的一项）：名称、点、颜色令牌（null = 按序号取 --ch-s1 / --ch-s2 / --ch-s3）、纵轴（0 / 1）、线宽（JS <c>s.width || 2</c>）、
/// 虚线（'6 4'；null = 实线）、单位与悬停时的小数位、<see cref="NoEnd"/> = 末端不画圆点（设定值虚线）。
/// </summary>
public sealed record ChartSeries(string Name, IReadOnlyList<ChartPoint> Points)
{
    public string? Color { get; init; }
    public int Axis { get; init; }
    /// <summary>线宽；0 = 没写（按 2，同 JS 的 <c>s.width || 2</c>）。</summary>
    public double Width { get; init; }
    public string? Dash { get; init; }
    public string? Unit { get; init; }
    public int? Dec { get; init; }
    public bool NoEnd { get; init; }

    /// <summary>实际线宽：没写为 2。</summary>
    public double LineWidth => Width != 0 && !double.IsNaN(Width) ? Width : 2;
}

/// <summary>
/// 一条纵轴（样稿 yAxes 的一项）：标题（'温度 ℃'）、单位（决定默认最小跨度 <see cref="ChartTicks.MinSpanOf"/>）、固定下限 / 上限、刻度小数位、
/// 最小跨度（平稳的读数不被放大成「地震图」）、最多刻度数（给了就按它逐级减少刻度，见 <see cref="ChartTicks.Fit"/>）、标题颜色令牌。
/// </summary>
public sealed record ChartAxis
{
    public string? Label { get; init; }
    public string? Unit { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int? Dec { get; init; }
    public double? MinSpan { get; init; }
    /// <summary>最多刻度数；null 或 0 = 没写（JS 的 <c>!ax.maxTicks</c>）。</summary>
    public int? MaxTicks { get; init; }
    public string? Color { get; init; }
}

/// <summary>
/// 一条水平参考线（样稿 refLines 的一项）：纵轴、值、线色令牌（null = --ch-t-sp）、文字色令牌（null = 线色）、是否虚线（JS <c>dash === false</c> 才是实线）、
/// 文字、是否把这个值算进纵轴范围（JS <c>fit !== false</c>）。
/// </summary>
public sealed record ChartRefLine(int Axis, double V)
{
    public string? Color { get; init; }
    public string? LabelColor { get; init; }
    public bool Dash { get; init; } = true;
    public string? Label { get; init; }
    public bool Fit { get; init; } = true;
}

/// <summary>一条竖向事件标记（样稿 markers 的一项）：时刻（UTC 毫秒）、文字、线色令牌（null = --ch-mark）、文字色令牌（null 时按样稿：线色是 --ch-mark 就用 --a-d）。</summary>
public sealed record ChartMarker(double T, string? Label)
{
    public string? Color { get; init; }
    public string? LabelColor { get; init; }
}

/// <summary>
/// 一张曲线图的全部输入（样稿 ui.lineChart 的 opts）。<see cref="XMin"/> / <see cref="XMax"/> 为 null 时取各系列的首尾点；<see cref="XLabel"/> 给了时下边距 50（否则 32）；
/// <see cref="TipGapMs"/>：悬停时离系列首尾多远还算在系列里（null = 30 s）。
/// </summary>
public sealed record ChartOptions
{
    public IReadOnlyList<ChartSeries> Series { get; init; } = Array.Empty<ChartSeries>();
    public IReadOnlyList<ChartAxis> YAxes { get; init; } = Array.Empty<ChartAxis>();
    public IReadOnlyList<ChartRefLine> RefLines { get; init; } = Array.Empty<ChartRefLine>();
    public IReadOnlyList<ChartMarker> Markers { get; init; } = Array.Empty<ChartMarker>();
    public double? XMin { get; init; }
    public double? XMax { get; init; }
    public string? XLabel { get; init; }
    public string? EmptyText { get; init; }
    public double? TipGapMs { get; init; }
}

/// <summary>样稿曲线用到的颜色令牌名（界面用 Palette 的令牌表取画笔）。</summary>
public static class ChartTokens
{
    public const string Temp = "--ch-t", TempSp = "--ch-t-sp", Pressure = "--ch-p", PressureSp = "--ch-p-sp";
    public const string S1 = "--ch-s1", S2 = "--ch-s2", S3 = "--ch-s3";
    public const string Mark = "--ch-mark", Limit = "--ch-limit";
    /// <summary>上限线的文字（--ch-limit 在曲线底色上只有 4.3:1，文字用更深的 --r-ink）。</summary>
    public const string LimitInk = "--r-ink";
    /// <summary>标记线是 --ch-mark 时的文字色（--ch-mark 在曲线底色上只有 3.6:1）。</summary>
    public const string MarkInk = "--a-d";
    /// <summary>悬停圆点的填充。</summary>
    public const string Card = "--bg-card";

    /// <summary>没写颜色的系列按序号轮流取（样稿 SERIES_TOKENS）。</summary>
    public static readonly IReadOnlyList<string> Auto = Array.AsReadOnly(new[] { S1, S2, S3 });
}
