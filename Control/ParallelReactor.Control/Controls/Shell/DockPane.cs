using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 停靠面板（融合版 F2；样稿 pane(id, lab, cls, inner) / rail(id, lab, extra)）：白色面板 + 左侧 28 px 竖条（开关圆钮 + 竖排名字），右边是内容。
/// 样子（<see cref="Mode"/>）：停靠 = 竖条 + 内容；收起 = 只剩竖条（30 宽，竖条上显示 <see cref="RailExtra"/>，例如设备号小方块）；浮出 = 竖条 + 内容，浮出阴影
/// （位置和宽度由外面摆：ShellView 把浮出的面板跨列放、给固定宽度）。<see cref="IsRight"/>：右侧面板（检查器）的箭头方向反过来。
/// 外观在 Styles/Theme/Shell.axaml（类 col / float / right）。外框的设备面板、检查器用它；F3 的看板面板（通道 / 曲线 / 共用资源）也用它。
/// </summary>
public sealed class DockPane : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<DockPane, string?>(nameof(Title));
    public static readonly StyledProperty<PaneMode> ModeProperty = AvaloniaProperty.Register<DockPane, PaneMode>(nameof(Mode));
    public static readonly StyledProperty<bool> IsRightProperty = AvaloniaProperty.Register<DockPane, bool>(nameof(IsRight));
    public static readonly StyledProperty<object?> RailExtraProperty = AvaloniaProperty.Register<DockPane, object?>(nameof(RailExtra));
    public static readonly StyledProperty<ICommand?> ToggleCommandProperty = AvaloniaProperty.Register<DockPane, ICommand?>(nameof(ToggleCommand));
    public static readonly StyledProperty<string?> ToggleTipProperty = AvaloniaProperty.Register<DockPane, string?>(nameof(ToggleTip));

    /// <summary>竖条上的名字（「设备」「检查器」）。</summary>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public PaneMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public bool IsRight { get => GetValue(IsRightProperty); set => SetValue(IsRightProperty, value); }
    /// <summary>收起时竖条上多出来的东西（设备面板：每台设备一个设备号小方块）。</summary>
    public object? RailExtra { get => GetValue(RailExtraProperty); set => SetValue(RailExtraProperty, value); }
    public ICommand? ToggleCommand { get => GetValue(ToggleCommandProperty); set => SetValue(ToggleCommandProperty, value); }
    /// <summary>开关的提示（不给 = 「折叠「设备」」/「展开「设备」」）。</summary>
    public string? ToggleTip { get => GetValue(ToggleTipProperty); set => SetValue(ToggleTipProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty || change.Property == IsRightProperty || change.Property == TitleProperty) Apply();
    }

    private Button? _toggle;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Apply();
    }

    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _toggle = e.NameScope.Find<Button>("PART_Toggle");
        Apply();
    }

    private void Apply()
    {
        Classes.Set("col", Mode == PaneMode.Collapsed);
        Classes.Set("float", Mode == PaneMode.Float);
        Classes.Set("right", IsRight);
        // 竖条开关的箭头方向看这几个类（控件主题里不能跨两层模板选它，见 Styles/Theme/Shell.axaml 的 PrRailToggle）
        if (_toggle != null)
        {
            _toggle.Classes.Set("col", Mode == PaneMode.Collapsed);
            _toggle.Classes.Set("float", Mode == PaneMode.Float);
            _toggle.Classes.Set("right", IsRight);
        }
        SetCurrentValue(ToggleTipProperty, (Mode == PaneMode.Collapsed ? "展开「" : "折叠「") + Title + "」");
    }
}
