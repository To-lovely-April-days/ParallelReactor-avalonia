using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using ParallelReactor.Control.Pages.Programs;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Pages.Programs;

/// <summary>预运行检查对话框的内容（PreflightDialogVm.Body）。</summary>
public partial class PreflightView : UserControl
{
    /// <summary>图标颜色：通过 g-dot、提醒 amb、不通过 r（样稿 .ckr.ok / .warn / .no）。</summary>
    public static readonly IValueConverter KindInk = new FuncValueConverter<CheckKind, IBrush>(k => k switch
    {
        CheckKind.Ok => Palette.GDotBrush,
        CheckKind.Warn => Palette.AmbBrush,
        _ => Palette.RBrush,
    });

    public PreflightView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
