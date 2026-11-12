using Avalonia.Controls;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// Fluent 资源覆盖（PORT-PLAN §4.6）：强调色 → 紫色令牌，内容字体 → UiFont，内容字号 → 15。
/// Fluent 仍是弹出层、ComboBox、ToolTip、ContextMenu 等的底层设施；这些覆盖让它们不再出现系统蓝。
/// 必须合并进 Application.Resources：Fluent 的派生画刷（AccentButtonBackground、CheckBoxCheckBackgroundFillChecked …）在应用级解析，
/// 只放进某个窗口的 Resources 时 SystemAccentColor 变了、这些画刷仍是系统蓝（M1 实测）。
/// M2 起在 App.Initialize 里全局合并（旧界面的强调色也随之变成紫色，旧视图在 M7 重做）。
/// </summary>
public static class FluentOverrides
{
    public static ResourceDictionary Create() => new()
    {
        // 强调色：a 为基准，越暗越接近 a-ink，越亮越接近 a-soft（全部是色板令牌）
        ["SystemAccentColor"] = Palette.AColor,
        ["SystemAccentColorDark1"] = Palette.ADColor,
        ["SystemAccentColorDark2"] = Palette.AInkColor,
        ["SystemAccentColorDark3"] = Palette.AInkColor,
        ["SystemAccentColorLight1"] = Palette.ATabColor,
        ["SystemAccentColorLight2"] = Palette.ALineColor,
        ["SystemAccentColorLight3"] = Palette.ASoftColor,
        // 内容字体 / 字号（Fluent 控件模板里 ContentPresenter、TextBox 等的默认值）
        ["ContentControlThemeFontFamily"] = UiFonts.Create(),
        ["ControlContentThemeFontSize"] = 15d,
    };
}
