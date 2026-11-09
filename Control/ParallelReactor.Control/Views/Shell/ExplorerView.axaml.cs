using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 设备资源管理器。<see cref="IsFloating"/>：窄于 1600 时的 320 px 浮层（ShadowPop），由 ShellView 按布局设置。
/// 树（ListBox，行 = Shell/ExplorerVm 的扁平行）：不可选的行（间隔、断线细节、未安装）禁用（不悬停、不选中、键盘跳过）；
/// 单击 = 选中（ListBox 的选中行与 ExplorerVm.SelectedRow 双向绑定 → ShellVm.Select），双击 = 打开设备页 / 反应釜页；
/// 树底部 18 px 渐隐（样稿 mask-image：最后 18 px 由不透明渐变到透明）。
/// </summary>
public partial class ExplorerView : UserControl
{
    public static readonly StyledProperty<bool> IsFloatingProperty = AvaloniaProperty.Register<ExplorerView, bool>(nameof(IsFloating));

    /// <summary>间隔行里分隔线的位置（离行顶的距离）→ 上外边距。</summary>
    public static readonly IValueConverter TopMargin = new FuncValueConverter<double, Thickness>(v => new Thickness(0, Math.Max(0, v), 0, 0));

    /// <summary>树底部渐隐的高度（样稿 18 px）。</summary>
    public const double FadeH = 18;

    public bool IsFloating { get => GetValue(IsFloatingProperty); set => SetValue(IsFloatingProperty, value); }

    public ExplorerView()
    {
        InitializeComponent();
        Tree.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem li) li.IsEnabled = li.DataContext is XRow { Selectable: true };
        };
        Tree.DoubleTapped += OnDoubleTapped;
        Tree.AddHandler(ContextRequestedEvent, OnContext, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Tree.SizeChanged += (_, e) => UpdateFade(e.NewSize.Height);
    }

    private ExplorerVm? Vm => (DataContext as ShellVm)?.Explorer;
    private ExplorerVm? _vm;
    private bool _revealPending;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.RevealRequested -= OnRevealRequested;
        _vm = Vm;
        if (_vm != null) _vm.RevealRequested += OnRevealRequested;
    }

    private void OnRevealRequested(object? sender, EventArgs e)
    {
        if (_revealPending) return;
        _revealPending = true;
        Dispatcher.UIThread.Post(() => { _revealPending = false; Reveal(retry: true); }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 把选中行滚到可见处（样稿 xReveal）：在上面 → 滚到离顶 6；在下面（离底不到 22，底部 18 px 渐隐）→ 滚到离底 22。
    /// 行还没生成容器（虚拟化）时先 ScrollIntoView，排完版再来一次。
    /// </summary>
    private void Reveal(bool retry)
    {
        var vm = _vm;
        var sv = Tree.FindDescendantOfType<ScrollViewer>();
        if (vm == null || sv == null || !IsEffectivelyVisible) return;
        var row = vm.SelectedRow ?? vm.Rows.FirstOrDefault(r => r is XDeviceRow { HasSel: true });
        if (row == null) return;
        int i = vm.Rows.IndexOf(row);
        if (i < 0) return;
        if (Tree.ContainerFromIndex(i) is not Avalonia.Controls.Control c || !c.IsArrangeValid)
        {
            Tree.ScrollIntoView(i);
            if (retry) Dispatcher.UIThread.Post(() => Reveal(retry: false), DispatcherPriority.Background);
            return;
        }
        if (c.TranslatePoint(default, sv) is not { } p) return;
        double top = p.Y, bottom = p.Y + c.Bounds.Height, view = sv.Viewport.Height;
        if (view <= 0) return;
        const double fade = FadeH + 4;
        if (top < 0) sv.Offset = sv.Offset.WithY(Math.Max(0, sv.Offset.Y + top - 6));
        else if (bottom > view - fade) sv.Offset = sv.Offset.WithY(sv.Offset.Y + bottom - view + fade);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;   // 双击展开箭头不打开页签
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is XRow { Selectable: true } row && Vm is { } vm)
        {
            vm.Open(row);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 右键（样稿 ctxMenu）：先选中这一行，再弹出菜单 —— 设备头 = 设备菜单（与总览 «⋯» 同一份 MenuModel.DeviceItems：急停在最后一组，
    /// 先打开只预选这一台的急停对话框），反应釜行 = 反应釜菜单（MenuModel.ReactorItems）。
    /// </summary>
    private void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not ShellVm shell || e.Source is not Visual v) return;
        var item = v.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        switch (item?.DataContext)
        {
            case XDeviceRow d:
                shell.Select(d.D.K);
                ContextMenus.Open(item, MenuModel.DeviceItems(shell, d.D), e);
                e.Handled = true;
                break;
            case XReactorRow r:
                shell.Select(r.D.K, r.R.N);
                ContextMenus.Open(item, MenuModel.ReactorItems(shell, r.D, r.R), e);
                e.Handled = true;
                break;
        }
    }

    /// <summary>底部渐隐：不透明 → 透明，最后 18 px（只看 alpha，颜色取令牌）。</summary>
    private void UpdateFade(double h)
    {
        if (h <= FadeH * 2) { Tree.OpacityMask = null; return; }
        var c = Palette.T1Color;
        Tree.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(c, 0),
                new GradientStop(c, (h - FadeH) / h),
                new GradientStop(Palette.WithAlpha(c, 0), 1),
            },
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsFloatingProperty) return;
        Classes.Set("floating", IsFloating);   // 浮层时自己不裁剪（ExplorerView.axaml 的 .floating 样式），阴影才画得出框外
        // 阴影画在外层 Shade 上（没有边框）：Box 只有右边 1 px 边框，11.1 的 Border 边框粗细不一致时不画 BoxShadow
        Shade.BoxShadow = IsFloating && Application.Current?.TryFindResource("ShadowPop", out var s) == true && s is BoxShadows bs ? bs : default;
    }
}
