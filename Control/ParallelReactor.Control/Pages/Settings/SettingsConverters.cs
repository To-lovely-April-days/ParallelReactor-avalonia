using Avalonia.Data.Converters;
using ParallelReactor.Control.Controls.Pr;

namespace ParallelReactor.Control.Pages.Settings;

/// <summary>设置页的转换器。</summary>
public static class SettingsConverters
{
    /// <summary>在线 → 绿色标签；否则灰。</summary>
    public static readonly IValueConverter OkTone = new FuncValueConverter<bool, BadgeTone>(ok => ok ? BadgeTone.Ok : BadgeTone.None);
}
