using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Pages;

/// <summary>主页视图的小转换器。</summary>
public static class HomeConverters
{
    /// <summary>设备健康 → 卡片左侧色条（报警红、正常运行绿、其余灰）。</summary>
    public static readonly IValueConverter HealthBar = new FuncValueConverter<DeviceHealth, IBrush>(h => h switch
    {
        DeviceHealth.Err => Palette.RBrush,
        DeviceHealth.Ok or DeviceHealth.Warn => Palette.GDotBrush,
        _ => Palette.NDotLineBrush,
    });

    /// <summary>总览卡片顶上的 3 px 状态条（样稿 .ovdev::before：报警 / 急停红、在跑绿、其余 n-dot-line）。</summary>
    public static readonly IValueConverter OvBar = new FuncValueConverter<Home.RunDot, IBrush>(s => s switch
    {
        Home.RunDot.Err => Palette.RBrush,
        Home.RunDot.Run => Palette.GDotBrush,
        _ => Palette.NDotLineBrush,
    });

    /// <summary>总览卡头的圆点：报警红、在跑绿、其余 n-dot。</summary>
    public static readonly IValueConverter OvDot = new FuncValueConverter<Home.RunDot, IBrush>(s => s switch
    {
        Home.RunDot.Err => Palette.RBrush,
        Home.RunDot.Run => Palette.GDotBrush,
        _ => Palette.NDotBrush,
    });

    /// <summary>总览指标「报警 n」的数：有报警 r，否则 t-1。</summary>
    public static readonly IValueConverter OvErrInk = new FuncValueConverter<bool, IBrush>(e => e ? Palette.RBrush : Palette.T1Brush);
}
