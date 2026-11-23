using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ParallelReactor.Control.Views.Pages.Programs;

/// <summary>「新建程序」窗口的内容（NewProgramDialogVm；DialogHost 的 DataTemplate）。底栏左边的「程序名称」一栏也在 DialogHost 里（ProgNameFoot）。</summary>
public partial class NewProgramView : UserControl
{
    public NewProgramView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
