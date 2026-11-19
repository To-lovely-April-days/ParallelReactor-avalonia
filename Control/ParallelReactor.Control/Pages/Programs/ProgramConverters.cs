using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Pages.Programs;

/// <summary>程序页视图用的几个转换。</summary>
public static class ProgramConverters
{
    /// <summary>在跑的程序锁住时的原因（b:Pr.Reason）；没锁为 null。</summary>
    public static readonly IValueConverter LockReason = new FuncValueConverter<bool, string?>(l => l ? "运行中的程序不能直接修改 · 先复制为新程序" : null);

    /// <summary>「结束」那一行序号的悬停说明（固定在最后）；其它步骤为 null。</summary>
    public static readonly IValueConverter EndTip = new FuncValueConverter<bool, string?>(e => e ? "「结束」固定在最后，不能删、不能挪" : null);

    /// <summary>「运行中 N」：有在跑的写琥珀（样稿 .mxsum b.w），否则 t-1。</summary>
    public static readonly IValueConverter BusyInk = new FuncValueConverter<int, IBrush>(n => n > 0 ? Palette.AmbInkBrush : Palette.T1Brush);
}
