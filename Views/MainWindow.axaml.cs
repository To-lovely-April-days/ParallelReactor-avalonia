using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ParallelReactor.Controls;
using ParallelReactor.ViewModels;
using System.Linq;

namespace ParallelReactor.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private SchematicControl? _schematic;
    private DispatcherTimer? _tickTimer;
    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _animTimer;
    private bool _closing;
    private double _phase;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _schematic = this.FindControl<SchematicControl>("Schematic");
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.SchematicInvalidated -= RefreshSchematic;
            _vm.ToastRequested -= ShowToast;
            _vm.TabSwitched -= OnTabSwitched;
        }
        _vm = DataContext as MainViewModel;
        if (_vm == null || _schematic == null) return;

        _schematic.Reactors = _vm.Reactors;
        _schematic.GasInlets = _vm.GasInlets;
        _schematic.VentOn = _vm.VentOn;
        _schematic.OnReactorClick = id => _vm.OpenDrawer(id);
        _schematic.OnValveClick = id => _vm.TryValve(id);
        _schematic.OnGasClick = i => _vm.ToggleGas(i);
        _schematic.OnVentClick = () => _vm.ToggleVent();

        _vm.SchematicInvalidated += RefreshSchematic;
        _vm.ToastRequested += ShowToast;
        _vm.TabSwitched += OnTabSwitched;
    }

    private void RefreshSchematic()
    {
        if (_schematic != null && _vm != null)
        {
            _schematic.VentOn = _vm.VentOn;
            _schematic.InvalidateVisual();
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // 实时数据 tick（1.2s，对应 HTML setInterval 节奏）
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _tickTimer.Tick += (_, _) => _vm?.Tick();
        _tickTimer.Start();

        // 时钟（1s）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _vm?.ClockTick();
        _clockTimer.Start();

        // 动画：33ms ≈ 30fps，Render 优先级保证稳定推进相位
        _animTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnAnimTick);
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_closing) return;
        _phase += 0.033; // 每帧累加约 33ms（秒）
        if (_schematic != null) _schematic.Phase = _phase;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _tickTimer?.Stop();
        _clockTimer?.Stop();
        _animTimer?.Stop();
    }

    // 点击遮罩关闭抽屉
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
        => _vm?.Drawer.CloseCommand.Execute(null);

    // 导航药丸高亮
    private void OnTabSwitched(string id)
    {
        var pills = this.FindControl<StackPanel>("Pills");
        if (pills == null) return;
        foreach (var child in pills.Children.OfType<Button>())
        {
            bool on = (child.CommandParameter as string) == id;
            if (on) child.Classes.Add("active");
            else child.Classes.Remove("active");
        }
    }

    // ============ Toast ============
    private void ShowToast(string kind, string msg)
    {
        var host = this.FindControl<StackPanel>("ToastHost");
        if (host == null) return;

        Color border = kind switch
        {
            "ok" => Color.Parse("#a3e635"),
            "warn" => Color.Parse("#f0a830"),
            _ => Color.Parse("#e0394c")
        };

        var toast = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21E1E23")),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(16, 11),
            Opacity = 0,
            Transitions = new Avalonia.Animation.Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(220)
                }
            },
            Child = new TextBlock
            {
                Text = msg,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#f6f6f8")),
                FontWeight = FontWeight.Medium
            }
        };
        host.Children.Add(toast);

        // 淡入（下一帧触发过渡）
        Dispatcher.UIThread.Post(() => toast.Opacity = 1, DispatcherPriority.Background);
        // 2.6s 后淡出移除
        var life = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        life.Tick += (_, _) =>
        {
            life.Stop();
            toast.Opacity = 0;
            var rm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            rm.Tick += (_, _) => { rm.Stop(); host.Children.Remove(toast); };
            rm.Start();
        };
        life.Start();
    }
}
