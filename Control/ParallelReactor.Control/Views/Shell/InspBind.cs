using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Shell;
using PrB = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 检查器按钮的绑定简写：<c>vs:InspBind.Act="{Binding SetSp}"</c> 把一个 <see cref="InspAct"/> 的文字、图标、禁用原因（b:Pr.Reason）、提示、命令
/// 一次接到按钮上，并跟着它的属性变化（每次刷新就地更新）。按钮的主题（次要 / 停止类 / 主要 / 单台急停）、尺寸类、可见性照常在 XAML 里写。
/// </summary>
public static class InspBind
{
    public static readonly AttachedProperty<InspAct?> ActProperty =
        AvaloniaProperty.RegisterAttached<Button, InspAct?>("Act", typeof(InspBind));

    public static InspAct? GetAct(Button b) => b.GetValue(ActProperty);
    public static void SetAct(Button b, InspAct? value) => b.SetValue(ActProperty, value);

    /// <summary>按钮当前跟着的那个 InspAct 的订阅（换 InspAct 时退订）。</summary>
    private static readonly AttachedProperty<Link?> LinkProperty =
        AvaloniaProperty.RegisterAttached<Button, Link?>("ActLink", typeof(InspBind));

    static InspBind()
    {
        ActProperty.Changed.AddClassHandler<Button>((b, e) =>
        {
            b.GetValue(LinkProperty)?.Dispose();
            b.SetValue(LinkProperty, e.NewValue is InspAct a ? new Link(b, a) : null);
        });
    }

    private sealed class Link
    {
        private readonly Button _b;
        private readonly InspAct _a;
        private string? _tip, _reason;
        private bool _first = true;

        public Link(Button b, InspAct a)
        {
            _b = b;
            _a = a;
            a.PropertyChanged += OnChanged;
            Apply();
        }

        public void Dispose() => _a.PropertyChanged -= OnChanged;

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Apply();

        private void Apply()
        {
            // 纯图标按钮（时间轴的运行 / 停止）：没有文字 → Content 留空（无障碍名取提示文字，见 Behaviors/PrProps）
            object? content = string.IsNullOrEmpty(_a.Label) ? null : _a.Label;
            if (!Equals(_b.Content, content)) _b.Content = content;
            if (PrB.GetIcon(_b) != _a.Icon) PrB.SetIcon(_b, _a.Icon);
            if (!ReferenceEquals(_b.Command, _a.Command)) _b.Command = _a.Command;
            // 提示：原因（Animation 优先级的锁图标提示）盖在它上面；只在变化时写本地值
            var tip = string.IsNullOrEmpty(_a.Tip) ? null : _a.Tip;
            if (_first || tip != _tip) { _tip = tip; ToolTip.SetTip(_b, tip); }
            if (_first || _a.Reason != _reason) { _reason = _a.Reason; PrB.SetReason(_b, _reason == null ? null : Theme.UiText.Keep(_reason)); }
            _first = false;
        }
    }
}
