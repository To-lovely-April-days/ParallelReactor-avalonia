using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace ParallelReactor.Controls;

public partial class OnScreenKeyboard : UserControl
{
    private Border? _dragHandle;
    private Border? _panel;
    private readonly TranslateTransform _move = new();
    private bool _dragging;
    private Point _start;
    private double _startX, _startY;

    public OnScreenKeyboard()
    {
        AvaloniaXamlLoader.Load(this);
        _dragHandle = this.FindControl<Border>("DragHandle");
        _panel = this.FindControl<Border>("Panel");
        if (_panel != null) _panel.RenderTransform = _move;

        if (_dragHandle != null)
        {
            _dragHandle.PointerPressed += OnHandlePressed;
            _dragHandle.PointerMoved += OnHandleMoved;
            _dragHandle.PointerReleased += OnHandleReleased;
        }

        // DataContext 变化（打开不同字段）时把位置复位到中央
        this.DataContextChanged += (s, e) => { _move.X = 0; _move.Y = 0; };
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(this);
        _startX = _move.X;
        _startY = _move.Y;
        e.Pointer.Capture(_dragHandle);
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _move.X = _startX + (p.X - _start.X);
        _move.Y = _startY + (p.Y - _start.Y);
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
