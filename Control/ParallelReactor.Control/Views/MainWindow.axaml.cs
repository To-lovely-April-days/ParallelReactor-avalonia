using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.ViewModels;

namespace ParallelReactor.Control.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is MainViewModel vm) { vm.Escape(); e.Handled = true; }
        };
    }
}
