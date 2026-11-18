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
}
