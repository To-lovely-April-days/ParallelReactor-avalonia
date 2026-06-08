using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ParallelReactor.Controls;

public partial class AlarmView : UserControl
{
    public AlarmView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
