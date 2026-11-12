using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 文档页签条（PORT-PLAN §2.1「文档页签栏」的 DocTabStrip）：一个 ItemsControl，排版面板固定是 <see cref="TabOverflowPanel"/>
/// （放不下时从右往左收进「›」，总览和当前页签始终显示）。面板量出的隐藏集合经 <see cref="HiddenChanged"/> 交给外面
/// （TabsVm.SetHidden：「›」上的个数、溢出菜单的「未显示」）。页签本身的样子由 ItemTemplate 给（PrDocTab）。
/// <see cref="MoreButton"/> = 页签栏右端的「›」：面板按「›」不写个数时的宽度决定收起哪些（隐藏集合跟上一次排成什么样无关）。
/// </summary>
public sealed class DocTabStrip : ItemsControl
{
    public static readonly StyledProperty<Avalonia.Controls.Control?> MoreButtonProperty =
        AvaloniaProperty.Register<DocTabStrip, Avalonia.Controls.Control?>(nameof(MoreButton));

    private TabOverflowPanel? _panel;

    static DocTabStrip()
    {
        ItemsPanelProperty.OverrideDefaultValue<DocTabStrip>(new FuncTemplate<Panel?>(() => new TabOverflowPanel()));
    }

    public DocTabStrip()
    {
        ClipToBounds = true;
        ContainerPrepared += (_, _) => Hook();
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    /// <summary>页签栏右端的「›」（个数写在它的 b:Pr.Count 上）。</summary>
    public Avalonia.Controls.Control? MoreButton { get => GetValue(MoreButtonProperty); set => SetValue(MoreButtonProperty, value); }

    /// <summary>隐藏集合变了（页签 Id）。</summary>
    public event Action<IReadOnlyCollection<string>>? HiddenChanged;

    /// <summary>当前隐藏的页签 Id（还没排过时为空）。</summary>
    public IReadOnlyCollection<string> Hidden => _panel?.Hidden ?? Array.Empty<string>();

    /// <summary>当前页签变了等：重新排一次（当前页签要始终可见）。</summary>
    public void Relayout() => _panel?.InvalidateMeasure();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MoreButtonProperty && _panel != null)
        {
            _panel.More = MoreButton;
            _panel.InvalidateMeasure();
        }
    }

    private void Hook()
    {
        if (ItemsPanelRoot is not TabOverflowPanel p || ReferenceEquals(p, _panel)) return;
        if (_panel != null) _panel.HiddenChanged -= OnHidden;
        _panel = p;
        _panel.More = MoreButton;
        _panel.HiddenChanged += OnHidden;
        OnHidden(_panel.Hidden);
    }

    private void OnHidden(IReadOnlyCollection<string> ids) => HiddenChanged?.Invoke(ids);
}
