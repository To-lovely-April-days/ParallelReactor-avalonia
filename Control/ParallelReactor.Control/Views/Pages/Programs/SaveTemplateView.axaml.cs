using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ParallelReactor.Control.Views.Pages.Programs;

/// <summary>「存为模板」窗口的内容（SaveTemplateDialogVm.Body）。</summary>
public partial class SaveTemplateView : UserControl
{
    public SaveTemplateView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
