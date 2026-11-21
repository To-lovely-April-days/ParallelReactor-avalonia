using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Pages.Alarms;

/// <summary>报警页视图的小转换器：级别 → 图标（样稿 alInd：报警 err-f 红 r-btn、提醒 warn-f 琥珀、事件 info-f t-3）。</summary>
public static class AlarmConverters
{
    public static readonly IValueConverter LevelIcon = new FuncValueConverter<AlarmLogLevel, string>(l => l switch
    {
        AlarmLogLevel.Hi => "err-f",
        AlarmLogLevel.Lo => "warn-f",
        _ => "info-f",
    });

    public static readonly IValueConverter LevelBrush = new FuncValueConverter<AlarmLogLevel, IBrush>(l => l switch
    {
        AlarmLogLevel.Hi => Palette.RBtnBrush,
        AlarmLogLevel.Lo => Palette.AmbBrush,
        _ => Palette.T3Brush,
    });
}

/// <summary>详情表的值：粗体（600）/ 红色（r-ink，活动中）。</summary>
public static class AlarmFieldConverters
{
    public static readonly IValueConverter Weight = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);
    public static readonly IValueConverter Ink = new FuncValueConverter<bool, IBrush>(b => b ? Palette.RInkBrush : Palette.T1Brush);
}
