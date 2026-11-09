using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 对话框宿主：点遮罩、点对话框的 × 都关闭（同样稿 closeDialog）；Esc 由窗口交给 ShellVm.Escape。
/// <para>键盘（M3 复核）：遮罩只挡指针、不挡键盘 —— 对话框打开时把键盘焦点移进对话框（框前面一个看不见的锚点：0 × 0、不在 Tab 顺序里、没有焦点外观，
/// 所以样子同样稿），Tab 只在对话框这一组里循环（TabNavigation = Cycle），回车 / Tab 碰不到下面的页面；关闭时焦点回到打开之前的地方（还在、可见、可用时）。
/// 不把焦点直接放进第一个输入框：那样输入框一打开就带强调色的焦点环，和样稿（打开时没有焦点）不一样 —— 按一下 Tab 就到第一个输入框。</para>
/// </summary>
public partial class DialogHost : UserControl
{
    private ShellVm? _vm;
    private IInputElement? _before;

    public DialogHost()
    {
        InitializeComponent();
        Scrim.PointerPressed += (_, e) => { (DataContext as ShellVm)?.Dialogs.Close(); e.Handled = true; };
        AddHandler(DialogFrame.CloseRequestedEvent, (_, e) => { (DataContext as ShellVm)?.Dialogs.Close(); e.Handled = true; });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.Dialogs.PropertyChanged -= OnDialogsChanged;
        _vm = DataContext as ShellVm;
        if (_vm != null) _vm.Dialogs.PropertyChanged += OnDialogsChanged;
    }

    private void OnDialogsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogHostVm.Current) || _vm == null) return;
        var focus = TopLevel.GetTopLevel(this)?.FocusManager;
        if (_vm.Dialogs.Current != null)
        {
            // 记下打开之前的焦点（对话框换成另一个对话框时不覆盖：关掉最后一个时回到最初的地方）
            if (_before == null && focus?.GetFocusedElement() is { } f && !(f is Visual v && this.IsVisualAncestorOf(v))) _before = f;
            Dispatcher.UIThread.Post(FocusDialog, DispatcherPriority.Loaded);
        }
        else
        {
            var back = _before;
            _before = null;
            if (back is Avalonia.Controls.Control c && c.IsAttachedToVisualTree() && c.IsEffectivelyVisible && c.IsEffectivelyEnabled) c.Focus();
            else if (focus?.GetFocusedElement() is Visual inside && this.IsVisualAncestorOf(inside)) TopLevel.GetTopLevel(this)?.Focus();
        }
    }

    /// <summary>焦点移进对话框（框前面看不见的锚点；见类注释）。对话框的内容晚一拍才建好，所以排在 Loaded 之后。</summary>
    private void FocusDialog()
    {
        if (_vm?.Dialogs.Current == null) return;
        var anchor = Holder.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "FocusAnchor");
        if (anchor == null) return;
        var cur = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        if (cur != null && Holder.IsVisualAncestorOf(cur)) return;   // 已经在对话框里（例如用户已经点进了输入框）
        anchor.Focus();
    }

    /// <summary>当前焦点在对话框里（核对用）。</summary>
    public bool FocusInside => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual v && Holder.IsVisualAncestorOf(v);
}
