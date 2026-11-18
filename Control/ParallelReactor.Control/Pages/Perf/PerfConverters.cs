using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Pages.Perf;

/// <summary>性能测试页的转换器（颜色只取色板）。</summary>
public static class PerfConverters
{
    /// <summary>泄漏率超过阈值：报警红字。</summary>
    public static readonly IValueConverter BadInk = new FuncValueConverter<bool, IBrush>(bad => bad ? Palette.RInkBrush : Palette.T1Brush);

    /// <summary>结论：通过 = 运行绿字、不通过 = 报警红字、未测 = t-3。</summary>
    public static readonly IValueConverter VerdictInk = new FuncValueConverter<bool?, IBrush>(ok => ok == true ? Palette.GInkBrush : ok == false ? Palette.RInkBrush : Palette.T3Brush);

    /// <summary>结论：通过 / 不通过 600，未测常规。</summary>
    public static readonly IValueConverter VerdictWeight = new FuncValueConverter<bool, FontWeight>(na => na ? FontWeight.Normal : FontWeight.SemiBold);
}
