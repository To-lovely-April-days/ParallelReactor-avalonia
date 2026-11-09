using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 文档页签条（PORT-PLAN §2.1「文档页签栏」的 DocTabStrip）：一个 ItemsControl，排版面板固定是 <see cref="TabOverflowPanel"/>
/// （放不下时从右往左收进「›」，总览和当前页签始终显示）。面板量出的隐藏集合经 <see cref="HiddenChanged"/> 交给外面
/// （TabsVm.SetHidden：「›」上的个数、溢出菜单的「未显示」）。页签本身的样子由 ItemTemplate 给（PrDocTab）。
/// </summary>
public sealed class DocTabStrip : ItemsControl
{
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

    /// <summary>隐藏集合变了（页签 Id）。</summary>
    public event Action<IReadOnlyCollection<string>>? HiddenChanged;

    /// <summary>当前隐藏的页签 Id（还没排过时为空）。</summary>
    public IReadOnlyCollection<string> Hidden => _panel?.Hidden ?? Array.Empty<string>();

    /// <summary>当前页签变了等：重新排一次（当前页签要始终可见）。</summary>
    public void Relayout() => _panel?.InvalidateMeasure();

    private void Hook()
    {
        if (ItemsPanelRoot is not TabOverflowPanel p || ReferenceEquals(p, _panel)) return;
        if (_panel != null) _panel.HiddenChanged -= OnHidden;
        _panel = p;
        _panel.HiddenChanged += OnHidden;
        OnHidden(_panel.Hidden);
    }

    private void OnHidden(IReadOnlyCollection<string> ids) => HiddenChanged?.Invoke(ids);
}
