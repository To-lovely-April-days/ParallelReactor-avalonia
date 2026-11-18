using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>主页三种视图（融合版 F4）用到的转换器。</summary>
public static class MonConverters
{
    /// <summary>转速框的边：能改 line-strong（样稿 .ptool .rpm），不能改（有原因）line。</summary>
    public static readonly IValueConverter RpmEdge = new FuncValueConverter<string?, IBrush>(why => why == null ? Palette.LineStrongBrush : Palette.LineBrush);

    /// <summary>选中的通道小块外面再加 1 px 品牌蓝（样稿 box-shadow: 0 0 0 1px var(--brand)）。</summary>
    public static readonly BoxShadows SelRing = new(new BoxShadow { Spread = 1, Color = Palette.AColor });
}
