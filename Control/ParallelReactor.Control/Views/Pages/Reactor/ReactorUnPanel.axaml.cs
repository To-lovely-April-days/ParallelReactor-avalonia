using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Controls.Pr;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 反应釜页 · 未安装的位置（样稿 unPanel；视图模型 = <see cref="Pages.ReactorUnVm"/>）。容器查询（样稿 @container rv：反应釜页的尺寸）按这一块的尺寸判断：
/// 宽 ≤ 900 → 间距 36、内边距 24、空位 120 × 184；高 ≤ 600 → 内边距 18 32、空位 132 × 202、右列间距 10、键值表内边距 8、行距 4（样稿 gap: 4px 16px）；
/// 高 ≤ 540 → 键值表不显示。
/// </summary>
public partial class ReactorUnPanel : UserControl, IReactorPanel
{
    public ReactorUnPanel()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Fit(e.NewSize);
    }

    public PrPanel PanelControl => Frame;

    private void Fit(Size size)
    {
        bool narrow = size.Width <= 900, low = size.Height <= 600, lower = size.Height <= 540;
        Row.Spacing = narrow ? 36 : 64;
        Row.Margin = low ? new Thickness(32, 18) : narrow ? new Thickness(24) : new Thickness(40, 32);
        (VesselBox.Width, VesselBox.Height) = low ? (132, 202) : narrow ? (120, 184) : (170, 260);
        TextCol.Spacing = low ? 10 : 14;
        KvBox.Padding = low ? new Thickness(0, 8) : new Thickness(0, 12);
        KvGrid.RowSpacing = low ? 4 : 8;
        KvBox.IsVisible = !lower;
    }
}
