using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ParallelReactor.Controls;

public partial class DataView : UserControl
{
    public DataView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
