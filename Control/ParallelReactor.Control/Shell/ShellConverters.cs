using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shell;

/// <summary>外框视图里用的几个小转换器。</summary>
public static class ShellConverters
{
    /// <summary>true → r-ink-2（未确认的计数），false → t-1。</summary>
    public static readonly IValueConverter RedIfTrue = new FuncValueConverter<bool, IBrush>(v => v ? Palette.RInk2Brush : Palette.T1Brush);
}
