using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 检查器（样稿 renderInspector）：标题栏 + 可滚动的正文（Shell/Inspector 的三种正文视图模型，按 DataTemplate 显示）。
/// 换了对象（InspectorVm.BodyKey 变）时滚动回到顶部（样稿 data-key 换了整块重画）；「设置」链接请求聚焦共用转速时，
/// 把那个数值框滚到可见处并聚焦（样稿 inspFocus）。
/// </summary>
public partial class InspectorView : UserControl
{
    private InspectorVm? _vm;

    public InspectorView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) { _vm.PropertyChanged -= OnVm; _vm.FocusRequested -= OnFocus; }
        _vm = (DataContext as ShellVm)?.Inspector;
        if (_vm != null) { _vm.PropertyChanged += OnVm; _vm.FocusRequested += OnFocus; }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorVm.BodyKey)) Scroll.Offset = default;
    }

    /// <summary>
    /// 「设置」（共用转速；HT-PR4 每釜独立搅拌时是反应釜检查器的「转速」）：选中对象之后，把转速框滚到可见处、聚焦它（等新正文排好版；样稿 inspFocus）。
    /// </summary>
    private void OnFocus(object? sender, string name)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = Body.GetVisualDescendants().OfType<NumBox>().FirstOrDefault(n => n.Name is "SharedRpm" or "ReactorRpm" && n.IsEffectivelyVisible);
            if (box == null) return;
            box.BringIntoView();
            box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
        }, DispatcherPriority.Background);
    }
}
